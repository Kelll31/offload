using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Processes;
using Offload.Core.Util;

namespace Offload.OpenCode;

/// <summary>
/// Неинтерактивный запуск агента OpenCode («opencode run») с локальной моделью в указанной папке.
/// </summary>
public static class OpenCodeRunner
{
    /// <summary>Интервал «пульса» при долгом молчании модели (обработка длинного контекста локально медленная).</summary>
    internal static TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>Сколько ждать загрузки модели сервером (/health = 503) перед запуском.</summary>
    private static readonly TimeSpan ModelLoadingWait = TimeSpan.FromMinutes(3);

    /// <summary>Строка stdout длиннее — обрезается (из неё извлекается только суть события).</summary>
    private const int MaxLineChars = 4 * 1024 * 1024;

    private const long MaxRunLogBytes = 8 * 1024 * 1024;

    internal static string RunLogFile => Path.Combine(AppPaths.LogsDir, "opencode-last-run.log");

    /// <param name="onProgress">Краткие сообщения о ходе работы (вызов инструмента, шаг) — для уведомлений MCP.</param>
    public static async Task<OpenCodeRunResult> RunAsync(AppConfig cfg, string task, string workingDirectory,
        OpenCodeRunOptions options, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(options);
        var total = Stopwatch.StartNew();
        var report = SafeReporter(onProgress);

        OpenCodeRunResult Fail(string error, int exitCode = -1) =>
            new(false, "", [], [], error, exitCode, total.Elapsed, 0, 0);

        if (string.IsNullOrWhiteSpace(task)) return Fail("Пустая задача: опишите, что нужно сделать.");
        if (System.Environment.GetEnvironmentVariable(OpenCodeConfigWriter.NestedEnvVar) == "1")
            return Fail("Рекурсивный вызов: локальный агент уже работает внутри OpenCode, запущенного Offload.");

        var exe = OpenCodeInstaller.FindExecutable(cfg);
        if (exe is null) return Fail("OpenCode не установлен. Установите его в Offload на вкладке «OpenCode».");

        string wd;
        try
        {
            wd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
            if (wd.EndsWith(':')) wd += Path.DirectorySeparatorChar; // корень диска
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail($"Некорректный путь к рабочей папке: {workingDirectory}");
        }
        if (!Directory.Exists(wd)) return Fail($"Рабочая папка не найдена: {wd}");

        var timeout = options.Timeout > TimeSpan.Zero
            ? options.Timeout
            : TimeSpan.FromSeconds(Math.Max(60, cfg.OpenCode.TaskTimeoutSeconds));
        var readOnly = IsReadOnlyAgent(options.Agent);
        var allowShell = options.AllowShell && !readOnly;

        try
        {
            // 1. Сервер модели доступен? Контекст слота — для limit.context.
            report("проверка локального сервера модели…");
            var probe = await LocalServer.ProbeAsync(cfg, report, ModelLoadingWait, ct);
            if (!probe.Reachable) return Fail(probe.Error!);

            // 2. Очередь: одновременные запуски на одной папке данных зависают.
            using var gate = await RunQueueLock.AcquireAsync(RunQueueLock.MutexName(), timeout,
                waited => report($"ожидание очереди: выполняется другая задача локального агента… {FormatWait(waited)}"), ct);
            if (gate is null)
            {
                return ct.IsCancellationRequested
                    ? Fail("Задача отменена.")
                    : Fail($"Не удалось дождаться очереди: другая задача локального агента выполняется дольше {FormatTimeout(timeout)}.");
            }

            // 3. Конфигурация актуальна (порт, модель, контекст).
            try
            {
                OpenCodeConfigWriter.WriteManagedConfig(cfg, probe.ContextPerSlot);
            }
            catch (Exception ex)
            {
                return Fail($"Не удалось записать конфигурацию OpenCode: {ex.Message}");
            }

            // 4. Состояние файлов до запуска.
            var before = await ChangeTracker.CaptureAsync(wd, ct);

            // 5. Запуск.
            var run = await ExecuteAsync(cfg, exe, task, wd, readOnly, allowShell, timeout, report, ct);

            // 6. Изменённые файлы (и при таймауте/отмене — агент мог успеть что-то изменить).
            List<string> changed;
            try
            {
                using var afterCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                changed = ChangeTracker.Diff(before, await ChangeTracker.CaptureAfterAsync(before, afterCts.Token));
            }
            catch (Exception ex)
            {
                Log.Warn("opencode", $"Не удалось определить изменённые файлы: {ex.Message}");
                changed = [];
            }

            var result = new OpenCodeRunResult(run.Success, run.Events.FinalText, changed, run.Events.ToolCalls.ToList(),
                run.Error, run.ExitCode, run.Duration, run.Events.PromptTokens, run.Events.CompletionTokens);
            Log.Info("opencode",
                $"Задача OpenCode завершена: успех={result.Success}, код {result.ExitCode}, {FileUtil.FormatDuration(result.Duration)}, " +
                $"шагов {run.Events.Steps}, инструментов {result.ToolCalls.Count}, файлов изменено {changed.Count}, " +
                $"токены {result.PromptTokens}/{result.CompletionTokens}{(result.Error is null ? "" : ", ошибка: " + result.Error)}");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("Задача отменена.");
        }
    }

    internal static bool IsReadOnlyAgent(string? agent) =>
        string.Equals(agent?.Trim(), "plan", StringComparison.OrdinalIgnoreCase)
        || string.Equals(agent?.Trim(), OpenCodeConfigWriter.ReadOnlyAgent, StringComparison.OrdinalIgnoreCase);

    internal sealed record ExecOutcome(bool Success, int ExitCode, string? Error, TimeSpan Duration, RunEvents Events);

    internal static IReadOnlyList<string> BuildArguments(AppConfig cfg, string wd, bool readOnly) =>
    [
        "run",
        "--dir", wd,
        "-m", OpenCodeConfigWriter.ModelRef(cfg),
        "--agent", readOnly ? OpenCodeConfigWriter.ReadOnlyAgent : OpenCodeConfigWriter.EditAgent,
        "--format", "json",
        // Своё название сессии — без лишнего запроса к модели для генерации заголовка.
        "--title", "offload",
    ];

    private static async Task<ExecOutcome> ExecuteAsync(AppConfig cfg, string exe, string task, string wd,
        bool readOnly, bool allowShell, TimeSpan timeout, Action<string> report, CancellationToken ct)
    {
        var events = new RunEvents(wd);
        var stderr = new TailBuffer(60);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = wd,
            UseShellExecute = false,
            CreateNoWindow = true,
            // stdin обязательно перенаправлен и закрыт: «opencode run» читает его до EOF (иначе зависнет).
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = FileUtil.Utf8NoBom,
            StandardOutputEncoding = FileUtil.Utf8NoBom,
            StandardErrorEncoding = FileUtil.Utf8NoBom,
        };
        foreach (var a in BuildArguments(cfg, wd, readOnly)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in OpenCodeConfigWriter.BuildEnvironment(cfg, interactive: false))
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }
        // OpenCode берёт корень из PWD, если он задан (Git Bash наследует чужой PWD).
        psi.Environment["PWD"] = wd;
        psi.Environment["NO_COLOR"] = "1";
        if (!readOnly && allowShell != cfg.OpenCode.AllowShellCommands)
            psi.Environment["OPENCODE_CONFIG_CONTENT"] = OpenCodeConfigWriter.ShellOverrideContent(cfg, allowShell);

        using var runLog = RunLog.Open(exe, psi, task);
        // Свой Job Object: при его закрытии завершаются и «внуки» (процессы, запущенные агентом через bash).
        using var job = TryCreateJob();

        var sw = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("процесс не создан");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new ExecOutcome(false, -1, $"Не удалось запустить OpenCode ({exe}): {ex.Message}", sw.Elapsed, events);
        }

        using (process)
        {
            if (job is null || !job.TryAdd(process)) JobObject.Shared?.TryAdd(process);
            Log.Info("opencode", $"Запуск OpenCode (PID {process.Id}) в {wd}, агент {(readOnly ? "только чтение" : allowShell ? "правка + оболочка" : "правка")}");

            var lastEventTicks = Stopwatch.GetTimestamp();
            var progressLock = new object();

            var stdoutTask = PumpLinesAsync(process.StandardOutput, MaxLineChars, (line, truncated) =>
            {
                Interlocked.Exchange(ref lastEventTicks, Stopwatch.GetTimestamp());
                runLog.Write(truncated ? "[обрезано] " + line[..Math.Min(line.Length, 2000)] : line);
                string? msg;
                lock (progressLock) msg = truncated ? events.FeedTruncated(line) : events.Feed(line);
                if (msg is not null) report(msg);
            });
            var stderrTask = PumpLinesAsync(process.StandardError, 64 * 1024, (line, _) =>
            {
                stderr.Add(line);
                runLog.Write("[stderr] " + line);
            });
            var stdinTask = WriteInputAsync(process, task);

            using var hbCts = new CancellationTokenSource();
            var heartbeat = HeartbeatAsync(() => Interlocked.Read(ref lastEventTicks), events, progressLock, report, hbCts.Token);

            var timedOut = false;
            var cancelled = false;
            using (var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                runCts.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(runCts.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = ct.IsCancellationRequested;
                    timedOut = !cancelled;
                    Log.Warn("opencode", timedOut
                        ? $"OpenCode превысил время ожидания ({FormatTimeout(timeout)}) — процесс завершается"
                        : "Задача OpenCode отменена — процесс завершается");
                    Kill(process, job);
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* не дождались */ }
                }
            }

            // Потоки вывода закрываются, когда завершатся и все наследники с копиями дескрипторов.
            if (!await AllDone(TimeSpan.FromSeconds(5), stdoutTask, stderrTask))
            {
                job?.Dispose(); // завершить оставшихся «внуков»
                await AllDone(TimeSpan.FromSeconds(3), stdoutTask, stderrTask);
            }
            hbCts.Cancel();
            await AllDone(TimeSpan.FromSeconds(1), heartbeat, stdinTask);

            var exitCode = SafeExitCode(process);
            var duration = sw.Elapsed;
            runLog.Write($"[exit] код {exitCode}, {duration}");

            string? error = null;
            if (timedOut) error = $"Превышено время ожидания ({FormatTimeout(timeout)}).";
            else if (cancelled) error = "Задача отменена.";
            else if (exitCode != 0 || events.Errors.Count > 0)
            {
                var detail = events.Errors.Count > 0 ? string.Join("; ", events.Errors) : stderr.Text(12);
                error = string.IsNullOrWhiteSpace(detail)
                    ? $"OpenCode завершился с кодом {exitCode}."
                    : $"OpenCode завершился с ошибкой (код {exitCode}): {detail}";
            }
            else if (events.Events == 0)
            {
                var detail = stderr.Text(12);
                error = "OpenCode не вернул ни одного события." + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail);
            }

            if (error is not null && events.Errors.Count == 0 && !timedOut && !cancelled)
                Log.Warn("opencode", $"stderr OpenCode: {stderr.Text(30)}");

            return new ExecOutcome(error is null, timedOut || cancelled ? -1 : exitCode, error, duration, events);
        }
    }

    private static async Task WriteInputAsync(Process process, string text)
    {
        try
        {
            await process.StandardInput.WriteAsync(text);
            await process.StandardInput.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Процесс завершился, не дочитав задачу.
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { /* уже закрыт */ }
        }
    }

    /// <summary>Чтение строк без ограничения ReadLine: слишком длинная строка обрезается (onLine(prefix, truncated: true)).</summary>
    internal static async Task PumpLinesAsync(StreamReader reader, int maxLineChars, Action<string, bool> onLine)
    {
        var buf = new char[16 * 1024];
        var sb = new StringBuilder();
        var overflow = false;

        void Emit()
        {
            var line = sb.ToString();
            if (line.EndsWith('\r')) line = line[..^1];
            sb.Clear();
            var truncated = overflow;
            overflow = false;
            if (line.Length == 0 && !truncated) return;
            try { onLine(line, truncated); } catch (Exception ex) { Log.Debug("opencode", $"Обработка строки: {ex.Message}"); }
        }

        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf.AsMemory());
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }
            if (n == 0) break;
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                if (buf[i] != '\n') continue;
                Append(buf, start, i - start);
                Emit();
                start = i + 1;
            }
            Append(buf, start, n - start);
        }
        if (sb.Length > 0 || overflow) Emit();

        void Append(char[] b, int offset, int count)
        {
            if (count <= 0 || overflow) return;
            var room = maxLineChars - sb.Length;
            if (count > room)
            {
                sb.Append(b, offset, Math.Max(0, room));
                overflow = true;
                return;
            }
            sb.Append(b, offset, count);
        }
    }

    private static async Task HeartbeatAsync(Func<long> lastEvent, RunEvents events, object progressLock,
        Action<string> report, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, token);
                var idle = Stopwatch.GetElapsedTime(lastEvent());
                if (idle < HeartbeatInterval - TimeSpan.FromSeconds(1)) continue;
                int steps;
                lock (progressLock) steps = events.Steps;
                var secs = (int)idle.TotalSeconds;
                report(steps > 0 ? $"шаг {steps}: модель думает… {secs} с" : $"запуск агента… {secs} с");
            }
        }
        catch (OperationCanceledException)
        {
            // Остановлен.
        }
    }

    private static JobObject? TryCreateJob()
    {
        try
        {
            return new JobObject();
        }
        catch (Exception ex)
        {
            Log.Debug("opencode", $"Job Object не создан: {ex.Message}");
            return null;
        }
    }

    private static void Kill(Process process, JobObject? job)
    {
        ProcessRunner.KillTree(process);
        job?.Dispose();
    }

    private static async Task<bool> AllDone(TimeSpan wait, params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(wait);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return tasks.All(t => t.IsCompleted);
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
    }

    private static Action<string> SafeReporter(Action<string>? onProgress)
    {
        if (onProgress is null) return _ => { };
        var gate = new object();
        return msg =>
        {
            lock (gate)
            {
                try { onProgress(msg); } catch (Exception ex) { Log.Debug("opencode", $"onProgress: {ex.Message}"); }
            }
        };
    }

    internal static string FormatTimeout(TimeSpan t) =>
        t.TotalMinutes >= 1 && t.Seconds == 0 ? $"{(int)t.TotalMinutes} мин" : FileUtil.FormatDuration(t);

    private static string FormatWait(TimeSpan t) => FileUtil.FormatDuration(t);

    /// <summary>Открыть интерактивный OpenCode (TUI) в новом окне терминала в указанной папке.</summary>
    public static void LaunchInteractive(AppConfig cfg, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var exe = OpenCodeInstaller.FindExecutable(cfg)
                  ?? throw new InvalidOperationException("OpenCode не установлен. Установите его в Offload на вкладке «OpenCode».");
        var wd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(wd)) throw new DirectoryNotFoundException($"Папка не найдена: {wd}");

        try
        {
            OpenCodeConfigWriter.WriteManagedConfig(cfg);
        }
        catch (Exception ex)
        {
            Log.Warn("opencode", $"Конфигурация OpenCode не обновлена перед запуском: {ex.Message}");
        }

        var env = OpenCodeConfigWriter.BuildEnvironment(cfg, interactive: true);
        env["PWD"] = wd;
        // Переменные задаёт сам скрипт: Windows Terminal открывает вкладку в своём процессе,
        // и окружение вызывающего туда не попадает. -EncodedCommand (base64 UTF-16) — без проблем с кавычками,
        // «;» (разделитель команд wt) и кириллицей в путях.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildLaunchScript(exe, wd, env)));
        var powershell = Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        var psArgs = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";

        var wt = ProcessRunner.FindOnPath("wt.exe");
        if (wt is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(wt)
                {
                    UseShellExecute = false,
                    WorkingDirectory = wd,
                    Arguments = $"-w new new-tab --title OpenCode \"{powershell}\" {psArgs}",
                })?.Dispose();
                Log.Info("opencode", $"OpenCode (TUI) открыт в Windows Terminal: {wd}");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("opencode", $"Windows Terminal не запустился ({ex.Message}) — открываем обычное окно консоли");
            }
        }

        // ShellExecute всегда создаёт новое окно консоли.
        Process.Start(new ProcessStartInfo(powershell)
        {
            UseShellExecute = true,
            WorkingDirectory = wd,
            Arguments = psArgs,
        })?.Dispose();
        Log.Info("opencode", $"OpenCode (TUI) открыт в новом окне консоли: {wd}");
    }

    /// <summary>Скрипт PowerShell: окружение → папка проекта → opencode; при ошибке окно не закрывается сразу.</summary>
    internal static string BuildLaunchScript(string exe, string wd, IReadOnlyDictionary<string, string?> env)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("try { $Host.UI.RawUI.WindowTitle = 'OpenCode — Offload' } catch { }");
        sb.AppendLine("try {");
        foreach (var (k, v) in env.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append("  [Environment]::SetEnvironmentVariable(").Append(PsQuote(k)).Append(", ")
              .Append(v is null ? "$null" : PsQuote(v)).AppendLine(", 'Process')");
        }
        sb.Append("  Set-Location -LiteralPath ").AppendLine(PsQuote(wd));
        sb.Append("  & ").AppendLine(PsQuote(exe));
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) {");
        sb.AppendLine("    Write-Host ''");
        sb.AppendLine("    Write-Host \"OpenCode завершился с кодом $LASTEXITCODE.\" -ForegroundColor Yellow");
        sb.AppendLine("    [void](Read-Host 'Нажмите Enter, чтобы закрыть окно')");
        sb.AppendLine("  }");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-Host \"Не удалось запустить OpenCode: $_\" -ForegroundColor Red");
        sb.AppendLine("  [void](Read-Host 'Нажмите Enter, чтобы закрыть окно')");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Строка в одинарных кавычках PowerShell: все виды одинарных кавычек удваиваются.</summary>
    internal static string PsQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('\'');
        foreach (var ch in s)
        {
            sb.Append(ch);
            if (ch is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(ch);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>Журнал последнего запуска (logs\opencode-last-run.log): события и stderr — для диагностики.</summary>
    private sealed class RunLog : IDisposable
    {
        private readonly StreamWriter? _writer;
        private long _written;

        private RunLog(StreamWriter? writer) => _writer = writer;

        public static RunLog Open(string exe, ProcessStartInfo psi, string task)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                var w = new StreamWriter(new FileStream(RunLogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                    FileUtil.Utf8NoBom) { AutoFlush = true };
                var log = new RunLog(w);
                log.Write($"[start] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {exe} {string.Join(' ', psi.ArgumentList)}");
                log.Write("[task] " + (task.Length > 4000 ? task[..4000] + "…" : task).Replace("\r", "").Replace("\n", "\\n"));
                return log;
            }
            catch
            {
                return new RunLog(null);
            }
        }

        public void Write(string line)
        {
            if (_writer is null) return;
            lock (_writer)
            {
                if (_written > MaxRunLogBytes) return;
                try
                {
                    _writer.WriteLine(line);
                    _written += line.Length + 2;
                    if (_written > MaxRunLogBytes) _writer.WriteLine("[журнал обрезан]");
                }
                catch
                {
                    // Журнал не критичен.
                }
            }
        }

        public void Dispose()
        {
            if (_writer is null) return;
            lock (_writer)
            {
                try { _writer.Dispose(); } catch { }
            }
        }
    }
}
