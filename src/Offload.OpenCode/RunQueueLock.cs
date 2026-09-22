using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.OpenCode;

/// <summary>
/// Очередь запусков OpenCode между процессами (несколько MCP-серверов, трей): одновременные «opencode run»
/// на одной папке данных зависают при старте (issue #29395). Именованный Mutex, а не Semaphore: при аварийном
/// завершении владельца мьютекс освобождается системой (AbandonedMutexException), семафор — нет.
/// Mutex привязан к потоку, поэтому им владеет отдельный поток — await в вызывающем коде безопасен.
/// </summary>
internal sealed class RunQueueLock : IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private Thread? _thread;
    private int _disposed;

    private RunQueueLock()
    {
    }

    /// <summary>Имя мьютекса: одна очередь на папку данных OpenCode в сеансе пользователя.</summary>
    internal static string MutexName()
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.OpenCodeDir.ToUpperInvariant())))[..16];
        return $@"Local\Offload.OpenCodeRun.{key}";
    }

    /// <summary>
    /// Дождаться очереди. null — не дождались за timeout или отменено (ct).
    /// onWaiting вызывается каждые ~15 с ожидания (время ожидания).
    /// </summary>
    public static async Task<RunQueueLock?> AcquireAsync(string name, TimeSpan timeout, Action<TimeSpan>? onWaiting, CancellationToken ct)
    {
        var gate = new RunQueueLock();
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        gate._thread = new Thread(() =>
        {
            Mutex? mutex = null;
            var owned = false;
            try
            {
                mutex = new Mutex(false, name);
                while (!owned)
                {
                    if (abort.IsCancellationRequested || ct.IsCancellationRequested || sw.Elapsed >= timeout)
                    {
                        acquired.TrySetResult(false);
                        return;
                    }
                    try
                    {
                        owned = mutex.WaitOne(TimeSpan.FromMilliseconds(250));
                    }
                    catch (AbandonedMutexException)
                    {
                        // Предыдущий владелец завершился аварийно — мьютекс теперь наш.
                        owned = true;
                        Log.Warn("opencode", "Предыдущий запуск OpenCode завершился аварийно (очередь освобождена)");
                    }
                }
                acquired.TrySetResult(true);
                gate._release.Wait();
            }
            catch (Exception ex)
            {
                // Например, мьютекс с тем же именем создан процессом с другими правами — работаем без очереди.
                Log.Warn("opencode", $"Очередь запусков OpenCode недоступна: {ex.Message}");
                acquired.TrySetException(ex);
            }
            finally
            {
                if (owned)
                {
                    try { mutex!.ReleaseMutex(); } catch { /* уже освобождён */ }
                }
                mutex?.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "Offload.OpenCodeRunQueue",
        };
        gate._thread.Start();

        try
        {
            while (true)
            {
                var done = await Task.WhenAny(acquired.Task, Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None));
                if (done == acquired.Task) break;
                onWaiting?.Invoke(sw.Elapsed);
            }
            if (await acquired.Task) return gate;
        }
        catch (Exception) when (!acquired.Task.IsCompletedSuccessfully)
        {
            // Мьютекс не создан — запускаем без очереди.
            return gate;
        }
        abort.Cancel();
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }
}
