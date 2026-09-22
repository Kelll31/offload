using System.Diagnostics;
using System.Text;

namespace Offload.Core.Processes;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>Запуск короткоживущих внешних процессов (nvidia-smi, git, claude, opencode) без окна консоли.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        string? standardInput = null,
        Action<string>? onStdOutLine = null,
        Action<string>? onStdErrLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                if (v is null) psi.Environment.Remove(k);
                else psi.Environment[k] = v;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(); return; }
            lock (stdout) stdout.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(); return; }
            lock (stderr) stderr.AppendLine(e.Data);
            onStdErrLine?.Invoke(e.Data);
        };

        process.Start();
        JobObject.Shared?.TryAdd(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            if (standardInput is not null)
                await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Процесс мог не читать stdin.
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) timeoutCts.CancelAfter(timeout.Value);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await Task.WhenAll(outDone.Task, errDone.Task).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            KillTree(process);
            if (!timedOut) throw;
        }
        catch (TimeoutException)
        {
            // Потоки вывода не закрылись вовремя — возвращаем то, что есть.
        }

        string o, e2;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e2 = stderr.ToString();
        return new ProcessResult(timedOut ? -1 : SafeExitCode(process), o, e2, timedOut);
    }

    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Процесс уже завершён.
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    /// <summary>Поиск исполняемого файла в PATH (с учётом PATHEXT).</summary>
    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = Path.HasExtension(name)
            ? new[] { "" }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // Некорректный элемент PATH.
                }
            }
        }
        return null;
    }
}
