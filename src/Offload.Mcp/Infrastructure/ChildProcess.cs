using System.Diagnostics;
using System.Text;
using Offload.Core.Processes;

namespace Offload.Mcp.Infrastructure;

internal sealed record ChildResult(
    int ExitCode,
    bool TimedOut,
    /// <summary>Начало вывода stdout (не больше MaxCaptureChars).</summary>
    string StdOut,
    string StdErr,
    /// <summary>Последние строки объединённого вывода (stdout+stderr) — для проверочных команд.</summary>
    IReadOnlyList<string> Tail,
    long TotalLines,
    bool OutputTruncated,
    TimeSpan Duration)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Запуск дочерних процессов для MCP: всегда без окна, все три потока перенаправлены (дочерний процесс
/// никогда не наследует stdio MCP), вывод ограничен по объёму, дерево процессов в Job Object и убивается по таймауту/отмене.
/// </summary>
internal static class ChildProcess
{
    public sealed record Options
    {
        public string? WorkingDirectory { get; init; }
        public IReadOnlyDictionary<string, string?>? Environment { get; init; }
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
        public int MaxCaptureChars { get; init; } = 1_000_000;
        public int TailLines { get; init; } = 0;
        public int MaxLineChars { get; init; } = 2000;
        /// <summary>Вызывается на каждую строку stdout; false — остановить процесс (достаточно данных).</summary>
        public Func<string, bool>? OnStdOutLine { get; init; }
        public Action<string>? OnAnyLine { get; init; }
    }

    public static Task<ChildResult> RunAsync(string fileName, IEnumerable<string> args, Options options, CancellationToken ct) =>
        RunCoreAsync(fileName, args, null, options, ct);

    /// <summary>Запуск с готовой командной строкой (для cmd.exe /d /s /c "…", где нужна точная строка).</summary>
    public static Task<ChildResult> RunRawAsync(string fileName, string rawArguments, Options options, CancellationToken ct) =>
        RunCoreAsync(fileName, null, rawArguments, options, ct);

    private static async Task<ChildResult> RunCoreAsync(string fileName, IEnumerable<string>? args, string? rawArgs, Options o, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = o.WorkingDirectory ?? System.Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (rawArgs is not null) psi.Arguments = rawArgs;
        else foreach (var a in args!) psi.ArgumentList.Add(a);
        if (o.Environment is not null)
        {
            foreach (var (k, v) in o.Environment)
            {
                if (v is null) psi.Environment.Remove(k);
                else psi.Environment[k] = v;
            }
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var tail = new Queue<string>();
        long totalLines = 0;
        var truncated = false;
        var stopRequested = false;
        var gate = new object();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void AddTail(string line)
        {
            if (o.TailLines <= 0) return;
            tail.Enqueue(line.Length > o.MaxLineChars ? line[..o.MaxLineChars] + "…" : line);
            while (tail.Count > o.TailLines) tail.Dequeue();
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(); return; }
            var kill = false;
            lock (gate)
            {
                totalLines++;
                if (stdout.Length + e.Data.Length + 1 <= o.MaxCaptureChars) stdout.Append(e.Data).Append('\n');
                else truncated = true;
                AddTail(e.Data);
                try { o.OnAnyLine?.Invoke(e.Data); } catch { }
                if (o.OnStdOutLine is not null && !stopRequested)
                {
                    bool keepGoing;
                    try { keepGoing = o.OnStdOutLine(e.Data); } catch { keepGoing = true; }
                    if (!keepGoing) { stopRequested = true; kill = true; }
                }
            }
            if (kill) ProcessRunner.KillTree(process);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(); return; }
            lock (gate)
            {
                totalLines++;
                if (stderr.Length < 64 * 1024) stderr.Append(e.Data).Append('\n');
                AddTail(e.Data);
                try { o.OnAnyLine?.Invoke(e.Data); } catch { }
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new ToolException($"Cannot start '{Path.GetFileName(fileName)}': {ex.Message}");
        }
        JobObject.Shared?.TryAdd(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { process.StandardInput.Close(); } catch { }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(o.Timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.KillTree(process);
            if (ct.IsCancellationRequested)
            {
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                throw;
            }
            timedOut = true;
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        }
        // Потомки (msbuild-узлы и т.п.) могут держать каналы вывода открытыми — ждём недолго.
        try { await Task.WhenAll(outDone.Task, errDone.Task).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }

        int exit;
        try { exit = timedOut ? -1 : process.ExitCode; } catch { exit = -1; }
        lock (gate)
        {
            return new ChildResult(exit, timedOut, stdout.ToString(), stderr.ToString(), [.. tail], totalLines, truncated || stopRequested, sw.Elapsed);
        }
    }
}
