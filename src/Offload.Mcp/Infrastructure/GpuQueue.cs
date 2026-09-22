using System.Security.Cryptography;
using System.Text;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Межпроцессная очередь к GPU: N именованных мьютексов-«слотов» (N = число слотов llama-server).
/// Мьютекс, а не семафор: если процесс-владелец упал, Windows отдаёт мьютекс следующему (AbandonedMutexException),
/// а счётчик семафора был бы потерян навсегда. Мьютекс привязан к потоку, поэтому ожидание и освобождение
/// выполняются в отдельном выделенном потоке.
/// </summary>
internal static class GpuQueue
{
    public static string BaseName
    {
        get
        {
            var key = AppPaths.DataDir.TrimEnd('\\', '/').ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            return $@"Local\Offload.Gpu.{hash}";
        }
    }

    public static async Task<IAsyncDisposable> AcquireAsync(int slots, ProgressReporter progress, CancellationToken ct)
    {
        slots = Math.Clamp(slots, 1, 16);
        var names = Enumerable.Range(0, slots).Select(i => $"{BaseName}.{i}").ToArray();
        var acquired = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() => Hold(names, acquired, release, ct))
        {
            IsBackground = true,
            Name = "Offload GPU slot",
        };
        thread.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var announced = false;
        while (true)
        {
            var done = await Task.WhenAny(acquired.Task, Task.Delay(TimeSpan.FromSeconds(announced ? 5 : 1), CancellationToken.None)).ConfigureAwait(false);
            if (done == acquired.Task) break;
            announced = true;
            progress.Report($"Queued behind other Offload requests… {sw.Elapsed.TotalSeconds:0} s");
        }
        // Отмена до захвата → OperationCanceledException из задачи; поток уже завершён.
        await acquired.Task.ConfigureAwait(false);
        return new Slot(release, thread);
    }

    private static void Hold(string[] names, TaskCompletionSource<int> acquired, ManualResetEventSlim release, CancellationToken ct)
    {
        var mutexes = new List<Mutex>();
        try
        {
            foreach (var n in names) mutexes.Add(new Mutex(false, n));
            var handles = mutexes.Cast<WaitHandle>().ToArray();
            var index = -1;
            while (index < 0)
            {
                if (ct.IsCancellationRequested)
                {
                    acquired.TrySetCanceled(ct);
                    return;
                }
                try
                {
                    var r = WaitHandle.WaitAny(handles, 250);
                    if (r != WaitHandle.WaitTimeout) index = r;
                }
                catch (AbandonedMutexException ex) when (ex.MutexIndex >= 0)
                {
                    // Предыдущий владелец завершился аварийно — мьютекс теперь наш.
                    Log.Warn("mcp", "Слот GPU освобождён после аварийного завершения другого процесса");
                    index = ex.MutexIndex;
                }
            }
            acquired.TrySetResult(index);
            release.Wait();
            try { mutexes[index].ReleaseMutex(); } catch (Exception ex) { Log.Warn("mcp", $"ReleaseMutex: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            // Нет прав на именованный объект и т.п. — работаем без очереди (llama-server сам ставит запросы в очередь).
            Log.Warn("mcp", $"Очередь GPU недоступна: {ex.Message}");
            acquired.TrySetResult(-1);
            release.Wait();
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

    private sealed class Slot(ManualResetEventSlim release, Thread thread) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { release.Set(); } catch (ObjectDisposedException) { }
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
    }
}
