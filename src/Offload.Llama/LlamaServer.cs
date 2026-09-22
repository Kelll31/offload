using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Processes;
using Offload.Core.Util;

namespace Offload.Llama;

public enum ServerState
{
    /// <summary>Не установлен llama.cpp или нет модели.</summary>
    NotConfigured,
    Stopped,
    /// <summary>Процесс запущен, модель загружается (/health отвечает 503).</summary>
    Starting,
    Running,
    Stopping,
    /// <summary>Процесс упал или не смог запуститься (см. LastError).</summary>
    Failed,
}

/// <summary>Параметры запуска, вычисленные для конкретной модели и оборудования.</summary>
/// <param name="ContextSize">Контекст одного слота (токенов). Общий пул KV (-c) = ContextSize × Parallel.</param>
/// <param name="CpuMoeLayers">Переданное --n-cpu-moe; -1 — размещение решает --fit, 0 — выключено.</param>
public sealed record ServerLaunchPlan(
    string ExePath,
    IReadOnlyList<string> Arguments,
    int ContextSize,
    int Parallel,
    int CpuMoeLayers,
    string ModelAlias);

/// <summary>Построение аргументов командной строки llama-server.</summary>
public static class LlamaServerArgs
{
    /// <summary>
    /// Псевдоним модели в API (--alias): стабильное имя, которое используют OpenCode и MCP,
    /// не зависящее от имени файла. Например, «offload».
    /// </summary>
    public const string DefaultAlias = "offload";

    /// <summary>Контекст по умолчанию, если ни настройки, ни модель его не задают.</summary>
    public const int FallbackContext = 32768;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] CacheTypes = ["f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1"];

    /// <summary>Флаги, удалённые из llama-server (v0.4.1, #28334): с ними сервер не запускается.</summary>
    private static readonly HashSet<string> RemovedFlags = new(StringComparer.Ordinal)
    {
        "--no-mmap", "--mmap", "--mlock", "--direct-io", "--no-direct-io",
    };

    /// <summary>Флаги, которыми управляет Offload (адрес, ключ, модель, псевдоним) — из доп. аргументов не принимаются.</summary>
    private static readonly HashSet<string> ManagedFlags = new(StringComparer.Ordinal)
    {
        "-m", "--model", "--host", "--port", "--api-key", "--api-key-file", "-a", "--alias",
    };

    /// <summary>
    /// Собрать план запуска: -m, --host, --port, --api-key, --alias, -ngl, -c, -np, --jinja,
    /// flash attention, тип KV-кэша, --n-cpu-moe, потоки, дополнительные аргументы пользователя.
    /// Контекст 0 в настройках → рекомендованный контекст модели.
    /// </summary>
    public static ServerLaunchPlan Build(AppConfig cfg, InstalledModel model, string serverExePath)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExePath);
        if (string.IsNullOrWhiteSpace(model.FilePath))
            throw new ArgumentException("У модели не указан файл GGUF.", nameof(model));

        var s = cfg.Server ?? new ServerSettings();
        var parallel = Math.Clamp(s.Parallel, 1, 64);
        var ctx = ResolveContext(s.ContextSize, model);
        var total = (int)Math.Min(int.MaxValue, (long)ctx * parallel);
        var args = new List<string>();

        args.AddRange(["-m", Path.GetFullPath(model.FilePath)]);
        args.AddRange(["--host", string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host.Trim()]);
        // Порт всегда явно: значение по умолчанию llama-server скоро сменится (8080 → 9931).
        args.AddRange(["--port", s.Port.ToString(Inv)]);
        if (!string.IsNullOrWhiteSpace(s.ApiKey)) args.AddRange(["--api-key", s.ApiKey.Trim()]);
        args.AddRange(["--alias", DefaultAlias]);

        // -c явно, чтобы --fit не уменьшал контекст; -np явно (авто = 4 слота).
        args.AddRange(["-c", total.ToString(Inv)]);
        args.AddRange(["-np", parallel.ToString(Inv)]);
        // При явном -np общий KV-кэш выключен и слот получает c/np — включаем общий пул.
        if (parallel > 1) args.Add("-kvu");

        // -fa всегда со значением: «голый» -fa съедает следующий аргумент.
        var fa = NormalizeFlashAttention(s.FlashAttention);
        args.AddRange(["-fa", fa]);
        if (NormalizeCacheType(s.CacheType) is { } cache)
        {
            args.AddRange(["-ctk", cache]);
            // Квантованный V-кэш требует flash attention.
            if (fa != "off" || !IsQuantized(cache)) args.AddRange(["-ctv", cache]);
        }

        // -1 — не передаём -ngl: слои по видеопамяти распределяет --fit.
        if (s.GpuLayers >= 0) args.AddRange(["-ngl", s.GpuLayers.ToString(Inv)]);

        int cpuMoe;
        if (s.CpuMoeLayers > 0 && (model.IsMoe || model.IsCustom))
        {
            args.AddRange(["--n-cpu-moe", s.CpuMoeLayers.ToString(Inv)]);
            cpuMoe = s.CpuMoeLayers;
        }
        else
        {
            cpuMoe = s.CpuMoeLayers < 0 ? -1 : 0;
        }

        if (s.Threads > 0) args.AddRange(["-t", s.Threads.ToString(Inv)]);
        if (s.IdleUnloadMinutes > 0) args.AddRange(["--sleep-idle-seconds", (s.IdleUnloadMinutes * 60L).ToString(Inv)]);

        args.Add("--jinja");
        args.Add("--no-webui");
        args.AddRange(["--log-colors", "off"]);

        // Сэмплинг по умолчанию — рекомендации модели (запросы могут переопределить).
        var sp = model.Sampling ?? new SamplingSettings();
        AddNumber(args, "--temp", sp.Temperature);
        AddNumber(args, "--top-p", sp.TopP);
        args.AddRange(["--top-k", Math.Max(0, sp.TopK).ToString(Inv)]);
        AddNumber(args, "--min-p", sp.MinP);
        AddNumber(args, "--repeat-penalty", sp.RepeatPenalty);
        if (sp.PresencePenalty > 0) AddNumber(args, "--presence-penalty", sp.PresencePenalty);

        // MTP-спекуляция: только для моделей со встроенным MTP-слоем и одного слота.
        if (s.EnableMtp && model.HasMtp && parallel == 1)
            args.AddRange(["--spec-type", "draft-mtp", "--spec-draft-n-max", "2"]);

        args.AddRange(SanitizeExtra(SplitArgs(s.ExtraArgs ?? "")));

        return new ServerLaunchPlan(Path.GetFullPath(serverExePath), args, ctx, parallel, cpuMoe, DefaultAlias);
    }

    /// <summary>Контекст одного слота: настройки → рекомендация модели → 32768; не больше родного контекста.</summary>
    internal static int ResolveContext(int configured, InstalledModel model)
    {
        var ctx = configured > 0 ? configured : model.RecommendedContext > 0 ? model.RecommendedContext : FallbackContext;
        ctx = Math.Max(ctx, 512);
        if (model.NativeContext > 0) ctx = Math.Min(ctx, model.NativeContext);
        return ctx;
    }

    internal static string NormalizeFlashAttention(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" or "enabled" => "on",
        "off" or "false" or "0" or "no" or "disabled" => "off",
        _ => "auto",
    };

    internal static string? NormalizeCacheType(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return CacheTypes.Contains(v) ? v : null;
    }

    private static bool IsQuantized(string cacheType) => cacheType is not ("f32" or "f16" or "bf16");

    private static void AddNumber(List<string> args, string flag, double value)
    {
        if (double.IsFinite(value)) args.AddRange([flag, value.ToString("0.######", Inv)]);
    }

    /// <summary>Убрать удалённые и управляемые Offload флаги, дописать значение к «голому» -fa.</summary>
    internal static IReadOnlyList<string> SanitizeExtra(IReadOnlyList<string> extra)
    {
        var result = new List<string>(extra.Count);
        for (var i = 0; i < extra.Count; i++)
        {
            var a = extra[i];
            var flag = a.Split('=', 2)[0];
            if (RemovedFlags.Contains(flag))
            {
                Log.Warn("llama", $"Дополнительный аргумент {a} удалён из llama.cpp и пропущен");
                continue;
            }
            if (ManagedFlags.Contains(flag))
            {
                Log.Warn("llama", $"Дополнительный аргумент {flag} пропущен: его задаёт Offload");
                if (!a.Contains('=') && i + 1 < extra.Count && !extra[i + 1].StartsWith('-')) i++;
                continue;
            }
            result.Add(a);
            if (a is "-fa" or "--flash-attn" && (i + 1 >= extra.Count || extra[i + 1].StartsWith('-')))
                result.Add("on");
        }
        return result;
    }

    /// <summary>Разбор строки дополнительных аргументов с учётом кавычек.</summary>
    /// <remarks>
    /// Разделители — пробельные символы. "…" и '…' группируют (кавычки убираются); внутри "…" \" — буквальная кавычка;
    /// '…' берётся как есть (удобно для JSON: --chat-template-kwargs '{"enable_thinking":false}'). "" — пустой аргумент.
    /// </remarks>
    public static IReadOnlyList<string> SplitArgs(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;
        var sb = new StringBuilder();
        var inToken = false;
        var i = 0;
        var s = commandLine;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    inToken = false;
                }
                i++;
                continue;
            }
            inToken = true;
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    sb.Append(s[i++]);
                }
                i++; // закрывающая кавычка (или конец строки)
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'') sb.Append(s[i++]);
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        if (inToken) result.Add(sb.ToString());
        return result;
    }

    /// <summary>Командная строка для журнала: ключ API скрыт.</summary>
    internal static string Describe(ServerLaunchPlan plan)
    {
        var sb = new StringBuilder(Quote(plan.ExePath));
        for (var i = 0; i < plan.Arguments.Count; i++)
        {
            var a = plan.Arguments[i];
            var masked = i > 0 && plan.Arguments[i - 1] == "--api-key" ? "***" : a;
            sb.Append(' ').Append(Quote(masked));
        }
        return sb.ToString();
    }

    private static string Quote(string a) => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;
}

/// <summary>
/// Управление процессом llama-server: запуск, ожидание готовности (/health), остановка,
/// перехват вывода в журнал logs\llama-server.log, привязка к Job Object.
/// Экземпляр живёт в трей-приложении (единственный владелец сервера).
/// </summary>
/// <remarks>
/// StateChanged вызывается синхронно, в порядке смены состояний, вне внутренних блокировок;
/// обработчик не должен синхронно ждать операций этого же экземпляра.
/// </remarks>
public sealed class LlamaServerProcess : IDisposable
{
    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromMinutes(10);

    private const long MaxLogBytes = 10L * 1024 * 1024;
    private const int TailCapacity = 80;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentQueue<ServerState> _pendingEvents = new();
    private readonly object _eventLock = new();
    private Run? _run;
    private bool _disposed;

    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>Текст последней ошибки на русском (для уведомления).</summary>
    public string? LastError { get; private set; }

    public ServerLaunchPlan? CurrentPlan { get; private set; }

    public int? ProcessId { get; private set; }

    public DateTime? StartedAtUtc { get; private set; }

    /// <summary>Журнал вывода llama-server.</summary>
    public static string LogFilePath => Path.Combine(AppPaths.LogsDir, "llama-server.log");

    /// <summary>Вызывается при каждой смене состояния (из фонового потока).</summary>
    public event Action<ServerState>? StateChanged;

    /// <summary>Каждая строка stdout/stderr llama-server (из фонового потока).</summary>
    public event Action<string>? OutputLine;

    /// <summary>
    /// Запустить сервер с активной моделью и дождаться готовности (или ошибки/таймаута).
    /// Если порт занят другим процессом — выбирает свободный порт и сохраняет его в конфиг.
    /// Повторный вызов при Running — ничего не делает.
    /// </summary>
    /// <exception cref="LlamaServerException">Не удалось запустить (Message = LastError).</exception>
    /// <exception cref="LlamaVcRuntimeMissingException">Нет Visual C++ Redistributable.</exception>
    /// <exception cref="OperationCanceledException">Отмена через ct (процесс остановлен) или вызов StopAsync во время запуска.</exception>
    public async Task StartAsync(AppConfig cfg, TimeSpan? readyTimeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_lock)
            {
                if (State == ServerState.Running && _run is { } current && !current.Exited.Task.IsCompleted) return;
            }
            // Проверки портов и запуск процесса — не в потоке интерфейса.
            var run = await Task.Run(() => Launch(cfg), ct).ConfigureAwait(false);
            FlushEvents();
            await WaitReadyAsync(run, readyTimeout is { } t && t > TimeSpan.Zero ? t : DefaultReadyTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            FlushEvents();
            _startGate.Release();
        }
    }

    /// <summary>Остановить сервер (мягко, затем принудительно).</summary>
    /// <remarks>На Windows llama-server обрабатывает только CTRL_C, поэтому процесс завершается вместе с деревом.</remarks>
    public async Task StopAsync()
    {
        Run? run;
        lock (_lock)
        {
            run = _run;
            if (run is null)
            {
                if (State is ServerState.Starting or ServerState.Running or ServerState.Stopping) SetStateLocked(ServerState.Stopped);
            }
            else
            {
                run.StopRequested = true;
                SetStateLocked(ServerState.Stopping);
            }
        }
        FlushEvents();
        if (run is null) return;

        await KillAndWaitAsync(run).ConfigureAwait(false);
        lock (_lock)
        {
            if (_run == run)
            {
                _run = null;
                ProcessId = null;
                StartedAtUtc = null;
                SetStateLocked(ServerState.Stopped);
            }
        }
        FlushEvents();
        Log.Info("llama", "llama-server остановлен");
        run.Dispose();
    }

    public async Task RestartAsync(AppConfig cfg, CancellationToken ct = default)
    {
        await StopAsync();
        await StartAsync(cfg, ct: ct);
    }

    public void Dispose()
    {
        Run? run;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            run = _run;
            _run = null;
            ProcessId = null;
            if (run is not null) run.StopRequested = true;
            if (State is ServerState.Starting or ServerState.Running or ServerState.Stopping) SetStateLocked(ServerState.Stopped);
        }
        if (run is not null)
        {
            try
            {
                if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true);
                run.Process.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Log.Debug("llama", $"Завершение llama-server при освобождении: {ex.Message}");
            }
            run.Dispose();
        }
        FlushEvents();
    }

    // ---------- Запуск ----------

    private Run Launch(AppConfig cfg)
    {
        var model = cfg.ActiveModel();
        var exe = LlamaInstaller.GetServerExePath(cfg);
        if (exe is null) throw NotConfigured("llama.cpp не установлен — запустите мастер настройки Offload.");
        if (model is null) throw NotConfigured("Модель не выбрана — скачайте или добавьте модель в Offload.");
        if (string.IsNullOrWhiteSpace(model.FilePath) || !File.Exists(model.FilePath))
            throw NotConfigured($"Файл модели не найден: {model.FilePath}");

        var exeDir = Path.GetDirectoryName(exe)!;
        try
        {
            VcRuntimeCheck.EnsureAvailable(exeDir);
        }
        catch (LlamaVcRuntimeMissingException ex)
        {
            SetFailed(ex.Message);
            throw;
        }

        var host = string.IsNullOrWhiteSpace(cfg.Server.Host) ? "127.0.0.1" : cfg.Server.Host.Trim();
        var requested = cfg.Server.Port;
        int port;
        try
        {
            port = PortProbe.ChooseFreePort(host, requested);
        }
        catch (LlamaServerException ex)
        {
            SetFailed(ex.Message);
            throw;
        }
        if (port != requested)
        {
            Log.Warn("llama", $"Порт {requested} занят другой программой — llama-server запускается на порту {port}");
            cfg.Server.Port = port;
            ConfigStore.Update(c => c.Server.Port = port);
        }

        var plan = LlamaServerArgs.Build(cfg, model, exe);
        var psi = new ProcessStartInfo(plan.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = exeDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);
        // PTX JIT (карты без готового кода в сборке): кэш скомпилированных ядер, чтобы не компилировать при каждом запуске.
        if (File.Exists(Path.Combine(exeDir, "ggml-cuda.dll")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_CACHE_MAXSIZE")))
            psi.Environment["CUDA_CACHE_MAXSIZE"] = "4294967296";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var run = new Run(process, plan, LocalHttp.ClientBaseUrl(host, port), cfg.Server.ApiKey ?? "");
        run.Writer = ServerLogWriter.TryOpen(LogFilePath);
        run.Writer?.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} Запуск: {LlamaServerArgs.Describe(plan)}");

        process.OutputDataReceived += (_, e) => OnOutput(run, e.Data, run.OutClosed);
        process.ErrorDataReceived += (_, e) => OnOutput(run, e.Data, run.ErrClosed);
        process.Exited += (_, _) => _ = HandleExitAsync(run);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("процесс не создан");
        }
        catch (Exception ex)
        {
            run.Dispose();
            var msg = $"Не удалось запустить llama-server ({exe}): {ex.Message}";
            SetFailed(msg);
            throw new LlamaServerException(msg, ex);
        }

        JobObject.Shared?.TryAdd(process);
        var pid = SafePid(process);
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершился — это обработает ожидание готовности.
        }

        lock (_lock)
        {
            _run = run;
            CurrentPlan = plan;
            ProcessId = pid;
            StartedAtUtc = null;
            LastError = null;
            SetStateLocked(ServerState.Starting);
        }
        Log.Info("llama", $"Запущен llama-server (PID {pid}), порт {port}, модель {Path.GetFileName(model.FilePath)}, контекст {plan.ContextSize}×{plan.Parallel}");
        return run;
    }

    private async Task WaitReadyAsync(Run run, TimeSpan timeout, CancellationToken ct)
    {
        var client = new LlamaClient(run.BaseUrl, run.ApiKey);
        var sw = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (run.StopRequested) throw new OperationCanceledException("Запуск llama-server прерван остановкой сервера.");
                if (run.Exited.Task.IsCompleted) await OnExitWhileStartingAsync(run).ConfigureAwait(false);

                // Запрос не должен продлевать отведённое на запуск время.
                var remaining = timeout - sw.Elapsed;
                var health = remaining > TimeSpan.Zero
                    ? await PollHealthAsync(client, remaining < PollRequestTimeout ? remaining : PollRequestTimeout, ct).ConfigureAwait(false)
                    : HealthState.Down;
                if (health == HealthState.Ready)
                {
                    bool ok;
                    lock (_lock)
                    {
                        ok = _run == run && !run.StopRequested && !run.Exited.Task.IsCompleted;
                        if (ok)
                        {
                            StartedAtUtc = DateTime.UtcNow;
                            SetStateLocked(ServerState.Running);
                        }
                    }
                    FlushEvents();
                    if (ok)
                    {
                        Log.Info("llama", $"llama-server готов за {sw.Elapsed.TotalSeconds:0.0} с");
                        return;
                    }
                    continue;
                }

                if (sw.Elapsed >= timeout)
                {
                    await FailStartAsync(run,
                        $"llama-server не загрузил модель за {FileUtil.FormatDuration(timeout)}. Возможно, модель слишком велика для этого компьютера " +
                        "или диск работает медленно. Подробности — в журнале llama-server.").ConfigureAwait(false);
                }

                await Task.WhenAny(run.Exited.Task, Task.Delay(PollInterval, ct)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !run.StopRequested)
        {
            Log.Info("llama", "Запуск llama-server отменён");
            await AbortRunAsync(run).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<HealthState> PollHealthAsync(LlamaClient client, TimeSpan limit, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        try
        {
            return await client.GetHealthAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return HealthState.Down;
        }
    }

    /// <summary>Процесс завершился во время загрузки: Failed + исключение.</summary>
    private async Task OnExitWhileStartingAsync(Run run)
    {
        var code = await run.Exited.Task.ConfigureAwait(false);
        if (run.StopRequested) throw new OperationCanceledException("Запуск llama-server прерван остановкой сервера.");
        var message = DescribeExit(run, code, whileStarting: true);
        var failed = false;
        lock (_lock)
        {
            if (_run == run)
            {
                _run = null;
                ProcessId = null;
                LastError = message;
                SetStateLocked(ServerState.Failed);
                failed = true;
            }
        }
        FlushEvents();
        Log.Error("llama", message);
        if (failed) run.Dispose();
        if (NtStatus.StartupFailure(code) is LlamaVcRuntimeMissingException vc) throw vc;
        throw new LlamaServerException(message);
    }

    /// <summary>Таймаут: остановить процесс, Failed + исключение.</summary>
    private async Task FailStartAsync(Run run, string message)
    {
        run.StopRequested = true;
        await KillAndWaitAsync(run).ConfigureAwait(false);
        lock (_lock)
        {
            if (_run == run)
            {
                _run = null;
                ProcessId = null;
                LastError = message;
                SetStateLocked(ServerState.Failed);
            }
        }
        FlushEvents();
        Log.Error("llama", message);
        run.Dispose();
        throw new LlamaServerException(message);
    }

    /// <summary>Отмена запуска вызывающим: Stopping → Stopped.</summary>
    private async Task AbortRunAsync(Run run)
    {
        lock (_lock)
        {
            if (_run != run) return;
            run.StopRequested = true;
            SetStateLocked(ServerState.Stopping);
        }
        FlushEvents();
        await KillAndWaitAsync(run).ConfigureAwait(false);
        lock (_lock)
        {
            if (_run == run)
            {
                _run = null;
                ProcessId = null;
                StartedAtUtc = null;
                SetStateLocked(ServerState.Stopped);
            }
        }
        FlushEvents();
        run.Dispose();
    }

    private static async Task KillAndWaitAsync(Run run)
    {
        try
        {
            if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Kill llama-server: {ex.Message}");
        }
        try
        {
            await run.Process.WaitForExitAsync().WaitAsync(KillWait).ConfigureAwait(false);
            await run.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            Log.Warn("llama", $"llama-server не завершился вовремя: {ex.Message}");
        }
    }

    // ---------- Вывод и завершение процесса ----------

    private void OnOutput(Run run, string? data, TaskCompletionSource closed)
    {
        if (data is null)
        {
            closed.TrySetResult();
            return;
        }
        var line = InstallerImpl.StripAnsi(data);
        run.AddTail(line);
        run.Writer?.WriteLine(line);
        var handler = OutputLine;
        if (handler is null) return;
        try
        {
            handler(line);
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Обработчик OutputLine: {ex.Message}");
        }
    }

    private async Task HandleExitAsync(Run run)
    {
        try
        {
            // Дочитать вывод, чтобы в сообщении об ошибке были последние строки.
            await Task.WhenAll(run.OutClosed.Task, run.ErrClosed.Task).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        var code = SafeExitCode(run.Process);
        run.Writer?.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} llama-server завершился (код {NtStatus.Format(code)})");
        run.Exited.TrySetResult(code);

        string? error = null;
        lock (_lock)
        {
            // Падение во время работы. Выход при запуске обрабатывает WaitReadyAsync, при остановке — StopAsync.
            if (_run == run && !run.StopRequested && State == ServerState.Running)
            {
                error = DescribeExit(run, code, whileStarting: false);
                _run = null;
                ProcessId = null;
                StartedAtUtc = null;
                LastError = error;
                SetStateLocked(ServerState.Failed);
            }
        }
        if (error is null) return;
        Log.Error("llama", error);
        FlushEvents();
        run.Dispose();
    }

    private static readonly Regex LogPrefix = new(@"^\d+\.\d+\.\d+\.\d+\s+[DIWE]\s+", RegexOptions.CultureInvariant);

    /// <summary>Понятное описание завершения с подсказкой и последними важными строками журнала.</summary>
    internal static string DescribeExit(IReadOnlyList<string> tail, int code, bool whileStarting, int port)
    {
        if (NtStatus.StartupFailure(code) is { } known) return known.Message;

        var sb = new StringBuilder(whileStarting
            ? $"llama-server не запустился (код {NtStatus.Format(code)})."
            : $"llama-server неожиданно завершился (код {NtStatus.Format(code)}).");
        if (Hint(tail, code, port) is { } hint) sb.Append(' ').Append(hint);
        var lines = ImportantLines(tail);
        if (lines.Count > 0) sb.Append(" Журнал: ").Append(string.Join(" | ", lines));
        return sb.ToString();
    }

    private static string DescribeExit(Run run, int code, bool whileStarting)
    {
        var port = 0;
        var args = run.Plan.Arguments;
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == "--port") int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
        return DescribeExit(run.TailSnapshot(), code, whileStarting, port);
    }

    private static string? Hint(IReadOnlyList<string> tail, int code, int port)
    {
        var text = string.Join("\n", tail).ToLowerInvariant();
        bool Has(params string[] keys) => keys.Any(text.Contains);
        if (Has("out of memory", "cudamalloc failed", "failed to allocate", "outofdevicememory", "unable to allocate", "not enough memory",
                "failed to create context", "error_out_of"))
            return "Не хватило памяти (видеопамяти или ОЗУ): уменьшите размер контекста или число параллельных слотов либо выберите модель меньше.";
        if (Has("failed to load model", "error loading model", "invalid magic", "gguf_init_from_file", "failed to read magic", "unknown model architecture"))
            return "Не удалось загрузить модель: файл повреждён или не докачан, либо его формат не поддерживается этой версией llama.cpp (обновите llama.cpp).";
        if (Has("couldn't bind", "could not bind", "failed to bind", "address already in use", "only one usage of each socket address"))
            return port > 0 ? $"Порт {port} занят другой программой." : "Порт занят другой программой.";
        if (Has("invalid argument", "unknown argument", "unknown value", "error while handling argument", "invalid value"))
            return "llama-server не принял параметры запуска — проверьте поле «Дополнительные аргументы» в настройках сервера.";
        if (Has("cuda driver version is insufficient", "no cuda-capable device", "ggml_cuda_init: failed", "cuda error", "vk::", "vulkan error"))
            return "Сборка llama.cpp не смогла использовать видеокарту: обновите драйвер или выберите другую сборку (например, Vulkan).";
        if (code is NtStatus.AccessViolation or NtStatus.StackBufferOverrun)
            return "Процесс аварийно завершился (сбой в llama.cpp или в драйвере видеокарты).";
        return null;
    }

    private static List<string> ImportantLines(IReadOnlyList<string> tail)
    {
        var cleaned = tail.Select(l => LogPrefix.Replace(l.Trim(), "")).Where(l => l.Length > 0).ToList();
        var important = tail
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && (Regex.IsMatch(l, @"^\d+\.\d+\.\d+\.\d+\s+E\s") || Regex.IsMatch(l, @"\b(error|failed|exception|out of memory|unable|abort)", RegexOptions.IgnoreCase)))
            .Select(l => LogPrefix.Replace(l, ""))
            .ToList();
        var pick = important.Count > 0 ? important.TakeLast(4) : cleaned.TakeLast(3);
        return pick.Select(l => l.Length > 200 ? l[..200] + "…" : l).ToList();
    }

    // ---------- Состояние и события ----------

    private LlamaServerException NotConfigured(string message)
    {
        lock (_lock)
        {
            LastError = message;
            SetStateLocked(ServerState.NotConfigured);
        }
        FlushEvents();
        return new LlamaServerException(message);
    }

    private void SetFailed(string message)
    {
        lock (_lock)
        {
            LastError = message;
            SetStateLocked(ServerState.Failed);
        }
        FlushEvents();
        Log.Error("llama", message);
    }

    /// <summary>Вызывать под _lock. Событие ставится в очередь и отправляется FlushEvents вне блокировки.</summary>
    private void SetStateLocked(ServerState state)
    {
        if (State == state) return;
        State = state;
        _pendingEvents.Enqueue(state);
    }

    /// <summary>Отправить накопленные события по порядку (очередь разбирает тот, кто первым взял _eventLock).</summary>
    private void FlushEvents()
    {
        lock (_eventLock)
        {
            while (_pendingEvents.TryDequeue(out var s))
            {
                try
                {
                    StateChanged?.Invoke(s);
                }
                catch (Exception ex)
                {
                    Log.Warn("llama", $"Обработчик StateChanged: {ex.Message}");
                }
            }
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private static int? SafePid(Process p)
    {
        try { return p.Id; } catch { return null; }
    }

    /// <summary>Один запуск процесса: вывод, журнал, признак остановки.</summary>
    private sealed class Run(Process process, ServerLaunchPlan plan, string baseUrl, string apiKey) : IDisposable
    {
        private readonly Queue<string> _tail = new();
        private int _disposed;

        public Process Process { get; } = process;
        public ServerLaunchPlan Plan { get; } = plan;
        public string BaseUrl { get; } = baseUrl;
        public string ApiKey { get; } = apiKey;
        public ServerLogWriter? Writer { get; set; }
        public TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OutClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ErrClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool StopRequested;

        public void AddTail(string line)
        {
            lock (_tail)
            {
                _tail.Enqueue(line);
                while (_tail.Count > TailCapacity) _tail.Dequeue();
            }
        }

        public string[] TailSnapshot()
        {
            lock (_tail) return _tail.ToArray();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Writer?.Dispose();
            try { Process.Dispose(); } catch { }
        }
    }

    /// <summary>Журнал llama-server.log с ротацией (~10 МБ → .1). Ошибки записи не мешают работе сервера.</summary>
    private sealed class ServerLogWriter : IDisposable
    {
        private static readonly object FileLock = new();
        private readonly string _path;
        private StreamWriter? _writer;
        private long _written;
        private long _limit;

        private ServerLogWriter(string path) => _path = path;

        public static ServerLogWriter? TryOpen(string path)
        {
            try
            {
                var w = new ServerLogWriter(path);
                lock (FileLock) w.Open();
                return w;
            }
            catch (Exception ex)
            {
                Log.Warn("llama", $"Журнал llama-server недоступен: {ex.Message}");
                return null;
            }
        }

        private void Open()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length >= MaxLogBytes)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
            }
            catch
            {
                // Файл занят — ротация в следующий раз.
            }
            var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(fs, FileUtil.Utf8NoBom) { AutoFlush = true };
            _written = fs.Length;
            // Если ротация не удалась, не пытаемся на каждой строке.
            _limit = Math.Max(MaxLogBytes, _written + 1024 * 1024);
        }

        public void WriteLine(string line)
        {
            lock (FileLock)
            {
                if (_writer is null) return;
                try
                {
                    _writer.WriteLine(line);
                    _written += Encoding.UTF8.GetByteCount(line) + 2;
                    if (_written >= _limit)
                    {
                        _writer.Dispose();
                        _writer = null;
                        Open();
                    }
                }
                catch
                {
                    // Журнал не должен ронять сервер.
                }
            }
        }

        public void Dispose()
        {
            lock (FileLock)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}

/// <summary>Проверка занятости TCP-порта и выбор свободного.</summary>
internal static class PortProbe
{
    public const int SearchRange = 50;

    public static int ChooseFreePort(string host, int port)
    {
        if (port is <= 0 or > 65535) port = 8765;
        var listening = ListeningPorts();
        if (IsFree(host, port, listening)) return port;
        var last = Math.Min(65535, port + SearchRange);
        for (var p = port + 1; p <= last; p++)
        {
            if (IsFree(host, p, listening)) return p;
        }
        throw new LlamaServerException(
            $"Порт {port} занят, и среди портов {port + 1}–{last} нет свободного. Укажите другой порт в настройках сервера.");
    }

    public static bool IsFree(string host, int port) => IsFree(host, port, ListeningPorts());

    /// <summary>
    /// Порт свободен: его никто не слушает (на любом адресе — таблица TCP-слушателей) и его удаётся занять
    /// (не входит в зарезервированные Windows диапазоны, не занят эксклюзивно).
    /// </summary>
    private static bool IsFree(string host, int port, HashSet<int> listening)
    {
        if (listening.Contains(port)) return false;
        try
        {
            var l = new TcpListener(BindAddress(host), port) { ExclusiveAddressUse = false };
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static HashSet<int> ListeningPorts()
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(ep => ep.Port)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Список TCP-слушателей недоступен: {ex.Message}");
            return [];
        }
    }

    private static IPAddress BindAddress(string host)
    {
        var h = (host ?? "").Trim().Trim('[', ']');
        if (h.Length == 0 || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (h is "*" or "+" or "0.0.0.0") return IPAddress.Any;
        return IPAddress.TryParse(h, out var ip) ? ip : IPAddress.Loopback;
    }
}
