using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Core.Usage;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Запись usage.jsonl без задержки ответа инструмента: при запущенном трее UsageLog.Append (IPC до 2 с, при сбое — файл)
/// выполняется в фоне; без трея запись сразу дописывается в файл. Незавершённые фоновые записи дожидаются при остановке
/// MCP-сервера (FlushAsync в McpEntry) и при завершении процесса (ProcessExit), чтобы статистика не терялась.
/// </summary>
internal static class UsageRecorder
{
    /// <summary>Сколько ждать незавершённые записи при завершении процесса (UsageLog.Append сам ограничен ~2 с на IPC).</summary>
    private static readonly TimeSpan ExitFlushTimeout = TimeSpan.FromSeconds(3);

    private static readonly object Lock = new();
    private static readonly HashSet<Task> Pending = [];
    private static int _exitHooked;

    /// <summary>Куда пишется запись (тесты подменяют). По умолчанию — UsageLog.Append.</summary>
    internal static Action<UsageRecord> Sink { get; set; } = UsageLog.Append;

    /// <summary>Запущен ли трей (тесты подменяют). Без трея запись идёт прямо в файл — это быстро, фон не нужен.</summary>
    internal static Func<bool> IsTrayRunning { get; set; } = IpcClient.IsTrayRunning;

    /// <summary>Число ещё не записанных записей.</summary>
    internal static int PendingCount
    {
        get { lock (Lock) return Pending.Count; }
    }

    /// <summary>Записать обращение: при запущенном трее — в фоне (IPC может ждать до 2 с), иначе сразу в файл. Не бросает исключений.</summary>
    public static void Enqueue(UsageRecord record)
    {
        bool tray;
        try { tray = IsTrayRunning(); }
        catch { tray = false; }
        if (!tray)
        {
            // Без трея UsageLog.Append сразу дописывает файл (миллисекунды) — синхронно, чтобы запись
            // гарантированно попала в текущий профиль (AppPaths) и не зависела от завершения процесса.
            Write(Sink, record);
            return;
        }

        HookProcessExit();
        var sink = Sink;
        var task = Task.Run(() => Write(sink, record));
        lock (Lock) Pending.Add(task);
        // Продолжение регистрируется после Add — удаление всегда после добавления.
        task.ContinueWith(t =>
        {
            lock (Lock) Pending.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Write(Action<UsageRecord> sink, UsageRecord record)
    {
        try
        {
            sink(record);
        }
        catch (Exception ex)
        {
            Log.Debug("mcp", $"usage не записан через основной путь ({ex.Message}) — запись в файл напрямую");
            try { UsageLog.AppendLocal(record); }
            catch (Exception ex2) { Log.Debug("mcp", $"usage не записан: {ex2.Message}"); }
        }
    }

    /// <summary>Дождаться незавершённых записей (не дольше timeout). false — не всё успело записаться.</summary>
    public static async Task<bool> FlushAsync(TimeSpan timeout)
    {
        Task[] tasks;
        lock (Lock) tasks = [.. Pending];
        if (tasks.Length == 0) return true;
        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            Log.Warn("mcp", $"Не дождались записи статистики использования: {tasks.Count(t => !t.IsCompleted)} шт.");
            return false;
        }
        catch (Exception ex)
        {
            // Write не бросает; сюда попадаем только при непредвиденной ошибке планировщика.
            Log.Debug("mcp", $"Ожидание записи статистики: {ex.Message}");
            return tasks.All(t => t.IsCompleted);
        }
        finally
        {
            // Продолжение, удаляющее задачу из Pending, может выполниться позже продолжения WhenAll —
            // завершённые убираем сами, чтобы после FlushAsync счётчик уже не включал их.
            lock (Lock) Pending.RemoveWhere(t => t.IsCompleted && tasks.Contains(t));
        }
    }

    private static void HookProcessExit()
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { FlushAsync(ExitFlushTimeout).GetAwaiter().GetResult(); }
            catch { /* процесс завершается */ }
        };
    }
}
