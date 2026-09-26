using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

/// <summary>Класс приоритета обращения к GPU (меньше — важнее).</summary>
internal enum GpuPriority
{
    /// <summary>Короткие интерактивные вызовы, которых ждёт IDE: ask_files, summarize_log, commit_message, find_context, review.</summary>
    Interactive = 0,

    /// <summary>Остальные обращения к модели (write_file, правка rewrite, ревью песочницы агента…).</summary>
    Normal = 1,

    /// <summary>Долгий запуск агента OpenCode (до 90 мин): OpenCode сам ходит в llama-server.</summary>
    Agent = 2,
}

/// <summary>Кто просит слот: для доски очереди (local_status) и выбора политики.</summary>
/// <param name="Pool">
/// Очередь вспомогательного сервера роли (fast/embed/rerank) — свои мьютексы, один слот; null — основной сервер.
/// Вызовы разных серверов друг друга не ждут: у каждого свой процесс llama-server.
/// </param>
internal sealed record GpuRequest(string Tool, string? Client, GpuPriority Priority, int Parallel, string? Pool = null);

/// <summary>Захваченный слот GPU. Index = −1 — без мьютекса (агент при Parallel = 1 или очередь недоступна).</summary>
internal sealed class GpuSlot : IAsyncDisposable
{
    private readonly ManualResetEventSlim? _release;
    private readonly Thread? _thread;
    private readonly GpuQueueBoard.Entry? _entry;
    private int _disposed;

    internal GpuSlot(int index, TimeSpan waited, ManualResetEventSlim? release, Thread? thread, GpuQueueBoard.Entry? entry)
    {
        Index = index;
        Waited = waited;
        _release = release;
        _thread = thread;
        _entry = entry;
    }

    public int Index { get; }

    /// <summary>Сколько ждали слот.</summary>
    public TimeSpan Waited { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_release is not null)
        {
            try { _release.Set(); } catch (ObjectDisposedException) { }
        }
        if (_thread is not null) await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        _entry?.Dispose();
    }
}

/// <summary>
/// Межпроцессная очередь к GPU: N именованных мьютексов-«слотов» (N = число слотов llama-server, <c>Parallel</c>).
/// Мьютекс, а не семафор: если процесс-владелец упал, Windows отдаёт мьютекс следующему (AbandonedMutexException),
/// а счётчик семафора был бы потерян навсегда. Мьютекс привязан к потоку, поэтому ожидание и освобождение
/// выполняются в отдельном выделенном потоке.
/// </summary>
/// <remarks>
/// Справедливая политика (ROADMAP §5.1):
/// <list type="bullet">
/// <item>Агент OpenCode держит слот всё время запуска (до 90 мин), но берёт его только из первых N−1 слотов: последний слот
/// всегда остаётся интерактивным вызовам. При N = 1 агент мьютекс не держит вовсе — llama-server сам ставит запросы
/// в очередь, и короткие вызовы IDE проходят между шагами агента (ожидание — не дольше одного ответа агенту).</item>
/// <item>Не-агентные вызовы предпочитают старший (резервный) слот, оставляя младшие агенту.</item>
/// <item>Пока ждёт вызов более высокого приоритета, вызовы ниже приоритетом не встают в ожидание мьютекса (уступают);
/// через <see cref="PriorityAging"/> ожидания уступка прекращается — чтобы не было голодания.</item>
/// </list>
/// Кто держит слоты и кто ждёт — видно на доске <see cref="GpuQueueBoard"/> (файлы в DataDir, удаляются при закрытии дескриптора,
/// в том числе при аварийном завершении процесса).
/// </remarks>
internal static class GpuQueue
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>Сколько вызов ниже приоритетом уступает очередь, прежде чем бороться за слот на равных.</summary>
    internal static TimeSpan PriorityAging { get; set; } = TimeSpan.FromSeconds(30);

    public static string BaseName
    {
        get
        {
            var key = AppPaths.DataDir.TrimEnd('\\', '/').ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            return $@"Local\Offload.Gpu.{hash}";
        }
    }

    /// <summary>Приоритет инструмента по умолчанию (агентные запуски OpenCode передают <see cref="GpuPriority.Agent"/> явно).</summary>
    public static GpuPriority PriorityFor(string tool) => tool switch
    {
        McpToolNames.AskFiles or McpToolNames.SummarizeLog or McpToolNames.CommitMessage or McpToolNames.FindContext
            or McpToolNames.ReviewDiff or McpToolNames.SecurityReview or McpToolNames.GitHistory => GpuPriority.Interactive,
        _ => GpuPriority.Normal,
    };

    /// <summary>
    /// Слоты, за которые может бороться вызов, в порядке предпочтения. Пусто — мьютекс не нужен (агент при Parallel = 1).
    /// </summary>
    internal static int[] SlotsFor(int parallel, GpuPriority priority)
    {
        var n = Math.Clamp(parallel, 1, 16);
        if (priority == GpuPriority.Agent) return n == 1 ? [] : [.. Enumerable.Range(0, n - 1)];
        return [.. Enumerable.Range(0, n).Reverse()];
    }

    /// <summary>
    /// Уступить очередь: есть ждущий вызов более высокого приоритета, который может занять один из наших слотов
    /// (<paramref name="mySlots"/>), а сами ждём меньше <see cref="PriorityAging"/>. Ждущему только других слотов уступать
    /// бессмысленно — он не займёт наш слот, а мы потеряли бы до <see cref="PriorityAging"/>. Запись без списка слотов
    /// (старая версия Offload) считается претендующей на любой слот.
    /// </summary>
    internal static bool ShouldYield(GpuPriority mine, TimeSpan waited, IEnumerable<GpuQueueEntry> board, string selfId, IReadOnlyCollection<int>? mySlots = null)
    {
        if (mine == GpuPriority.Interactive || waited >= PriorityAging) return false;
        return board.Any(e => e.Id != selfId && e.State == GpuQueueEntry.Waiting && e.Priority < mine
                              && (mySlots is null || e.Slots is null || e.Slots.Any(mySlots.Contains)));
    }

    /// <summary>
    /// Захватить слот для вызова инструмента; время ожидания добавляется в статистику вызова. Очередь — того сервера,
    /// к которому идёт вызов: роль уже полученной модели (<see cref="ToolContext.ModelIfUsed"/>) или маршрут
    /// <see cref="ModelRouting.Resolve"/>.
    /// </summary>
    public static async Task<GpuSlot> AcquireAsync(ToolContext ctx, CancellationToken ct, GpuPriority? priority = null)
    {
        var p = priority ?? PriorityFor(ctx.Tool);
        var role = ctx.ModelIfUsed?.Role ?? ModelRouting.Resolve(ctx.Cfg, ctx.Tool, p);
        var request = RequestFor(ctx.Tool, ctx.Server?.ClientInfo?.Name, p, ctx.Cfg.Server.Parallel, role);
        var slot = await AcquireAsync(request, ctx.Progress, ct).ConfigureAwait(false);
        ctx.Stats.AddQueueWait(slot.Waited);
        return slot;
    }

    /// <summary>Запрос слота: основной сервер — N слотов; вспомогательная роль — своя очередь из одного слота.</summary>
    internal static GpuRequest RequestFor(string tool, string? client, GpuPriority priority, int parallel, ModelRole role) =>
        role == ModelRole.Quality
            ? new GpuRequest(tool, client, priority, parallel)
            : new GpuRequest(tool, client, priority, 1, role.Key());

    /// <summary>Имя мьютекса слота: основной сервер — «…Gpu.hash.N», вспомогательный — «…Gpu.hash.fast.N».</summary>
    internal static string MutexName(string? pool, int index) =>
        pool is null ? $"{BaseName}.{index}" : $"{BaseName}.{pool}.{index}";

    /// <summary>Совместимая форма (тесты): обычный приоритет.</summary>
    public static Task<GpuSlot> AcquireAsync(int slots, ProgressReporter progress, CancellationToken ct) =>
        AcquireAsync(new GpuRequest("?", null, GpuPriority.Normal, slots), progress, ct);

    public static async Task<GpuSlot> AcquireAsync(GpuRequest request, ProgressReporter progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var indexes = SlotsFor(request.Parallel, request.Priority);
        // На доске слоты вспомогательной очереди не указываем: они не пересекаются со слотами основного сервера,
        // и ожидающий их вызов не должен заставлять уступать вызовы основного (ShouldYield сравнивает номера слотов).
        var entry = GpuQueueBoard.Register(request, request.Pool is null ? indexes : []);
        if (indexes.Length == 0)
        {
            // Агент при одном слоте: мьютекс не держим, llama-server чередует его запросы с интерактивными.
            entry?.Update(GpuQueueEntry.Holding, -1);
            return new GpuSlot(-1, TimeSpan.Zero, null, null, entry);
        }

        var acquired = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var sw = Stopwatch.StartNew();
        var thread = new Thread(() => Hold(request, indexes, entry, sw, acquired, release, ct))
        {
            IsBackground = true,
            Name = "Offload GPU slot",
        };
        thread.Start();

        var announced = false;
        while (true)
        {
            var done = await Task.WhenAny(acquired.Task, Task.Delay(TimeSpan.FromSeconds(announced ? 5 : 1), CancellationToken.None)).ConfigureAwait(false);
            if (done == acquired.Task) break;
            announced = true;
            progress.Report($"Queued behind other Offload requests… {sw.Elapsed.TotalSeconds:0} s");
        }
        int index;
        try
        {
            // Отмена до захвата → OperationCanceledException из задачи; поток уже завершён.
            index = await acquired.Task.ConfigureAwait(false);
        }
        catch
        {
            entry?.Dispose();
            throw;
        }
        entry?.Update(GpuQueueEntry.Holding, request.Pool is null ? index : -1);
        return new GpuSlot(index, sw.Elapsed, release, thread, entry);
    }

    private static void Hold(GpuRequest request, int[] indexes, GpuQueueBoard.Entry? entry, Stopwatch sw,
        TaskCompletionSource<int> acquired, ManualResetEventSlim release, CancellationToken ct)
    {
        var mutexes = new List<Mutex>();
        var index = -1;
        try
        {
            foreach (var i in indexes) mutexes.Add(new Mutex(false, MutexName(request.Pool, i)));
            var handles = mutexes.Cast<WaitHandle>().ToArray();
            while (index < 0)
            {
                if (ct.IsCancellationRequested)
                {
                    acquired.TrySetCanceled(ct);
                    return;
                }
                if (request.Priority != GpuPriority.Interactive && entry is not null
                    && ShouldYield(request.Priority, sw.Elapsed, GpuQueueBoard.ReadSafe(), entry.Id, indexes))
                {
                    ct.WaitHandle.WaitOne(Poll);
                    continue;
                }
                try
                {
                    var r = WaitHandle.WaitAny(handles, Poll);
                    if (r != WaitHandle.WaitTimeout) index = r;
                }
                catch (AbandonedMutexException ex) when (ex.MutexIndex >= 0)
                {
                    // Предыдущий владелец завершился аварийно — мьютекс теперь наш.
                    Log.Warn("mcp", "Слот GPU освобождён после аварийного завершения другого процесса");
                    index = ex.MutexIndex;
                }
            }
            acquired.TrySetResult(indexes[index]);
            // Слот уже захвачен — держим его до Dispose, независимо от отмены исходного ожидания.
            release.Wait(CancellationToken.None);
            try { mutexes[index].ReleaseMutex(); } catch (Exception ex) { Log.Warn("mcp", $"ReleaseMutex: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            // Нет прав на именованный объект и т.п. — работаем без очереди (llama-server сам ставит запросы в очередь).
            Log.Warn("mcp", $"Очередь GPU недоступна: {ex.Message}");
            if (acquired.TrySetResult(-1)) release.Wait(CancellationToken.None);
            else if (index >= 0)
            {
                try { mutexes[index].ReleaseMutex(); } catch { /* уже не наш */ }
            }
        }
        finally
        {
            foreach (var m in mutexes) m.Dispose();
            release.Dispose();
        }
    }

    /// <summary>Сколько слотов сейчас занято (для local_status). Проверка без ожидания.</summary>
    public static int CountBusy(int slots)
    {
        var busy = 0;
        var t = new Thread(() =>
        {
            for (var i = 0; i < Math.Clamp(slots, 1, 16); i++)
            {
                try
                {
                    using var m = new Mutex(false, $"{BaseName}.{i}");
                    bool got;
                    try { got = m.WaitOne(0); }
                    catch (AbandonedMutexException) { got = true; }
                    if (got) m.ReleaseMutex();
                    else busy++;
                }
                catch
                {
                    // Нет доступа — не считаем.
                }
            }
        }) { IsBackground = true };
        t.Start();
        t.Join(TimeSpan.FromSeconds(2));
        return busy;
    }
}
