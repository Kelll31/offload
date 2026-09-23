using System.Collections.Concurrent;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Фоновые задачи этого MCP-процесса (local_agent_task background=true): IDE получает job_id сразу и продолжает работу,
/// а состояние/результат спрашивает через local_job. Живут, пока жив процесс; итог пишется в папку задачи (result.txt).
/// </summary>
internal static class BackgroundJobs
{
    private sealed record Entry(Task<string> Task, ProgressReporter Progress, CancellationTokenSource Cts, DateTime StartedUtc);

    private static readonly ConcurrentDictionary<string, Entry> Running = new(StringComparer.Ordinal);

    public const string ResultFile = "result.txt";

    /// <summary>Запустить тело задачи в фоне. Результат (или текст ошибки) сохраняется в result.txt задачи.</summary>
    public static void Start(string jobId, ProgressReporter progress, CancellationTokenSource cts, Func<Task<string>> body)
    {
        var task = Task.Run(async () =>
        {
            string text;
            try
            {
                text = await body().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                text = "Cancelled.";
            }
            catch (Exception ex) when (ex is ToolException or ContextExceededException)
            {
                text = "FAILED: " + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("mcp", $"Фоновая задача {jobId} упала", ex);
                text = $"FAILED: Offload internal error: {ex.GetType().Name}: {ex.Message}";
            }
            SaveResult(jobId, text);
            return text;
        });
        Running[jobId] = new Entry(task, progress, cts, DateTime.UtcNow);
        _ = task.ContinueWith(_ =>
        {
            Running.TryRemove(jobId, out var e);
            e?.Cts.Dispose();
        }, TaskScheduler.Default);
    }

    public static bool IsRunning(string jobId) => Running.ContainsKey(jobId);

    /// <summary>Последнее сообщение о ходе работы и время с начала (null — задача не выполняется в этом процессе).</summary>
    public static (string? LastMessage, TimeSpan Elapsed)? Progress(string jobId)
    {
        if (!Running.TryGetValue(jobId, out var e)) return null;
        return (e.Progress.LastMessage, DateTime.UtcNow - e.StartedUtc);
    }

    /// <summary>Дождаться завершения не дольше wait. true — задача завершилась (или не выполняется).</summary>
    public static async Task<bool> WaitAsync(string jobId, TimeSpan wait, CancellationToken ct)
    {
        if (!Running.TryGetValue(jobId, out var e)) return true;
        if (wait <= TimeSpan.Zero) return e.Task.IsCompleted;
        var done = await Task.WhenAny(e.Task, Task.Delay(wait, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return done == e.Task;
    }

    public static bool Cancel(string jobId)
    {
        if (!Running.TryGetValue(jobId, out var e)) return false;
        try { e.Cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    public static string? ReadResult(string jobId)
    {
        try
        {
            var file = Path.Combine(JobStore.DirOf(jobId), ResultFile);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveResult(string jobId, string text)
    {
        try
        {
            File.WriteAllText(Path.Combine(JobStore.DirOf(jobId), ResultFile), text);
        }
        catch (Exception ex)
        {
            Log.Warn("mcp", $"Результат фоновой задачи {jobId} не сохранён: {ex.Message}");
        }
    }
}
