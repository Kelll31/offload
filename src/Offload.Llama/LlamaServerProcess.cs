using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Processes;
using Offload.Core.Util;

namespace Offload.Llama;

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

    /// <summary>
    /// Последний неудачный запуск — по вине сборки llama.cpp (процесс завершился при загрузке не из-за памяти, модели, порта
    /// или VC++ Runtime). По нему решается автоматический откат только что установленной сборки.
    /// </summary>
    public bool LastFailureBlamesBuild { get; private set; }

    /// <summary>
    /// Клиент именно запущенного процесса (его адрес и ключ), а не текущих настроек: config.json могли изменить
    /// вручную после запуска. null — сервер не запущен.
    /// </summary>
    public LlamaClient? CreateRunningClient() => _run is { } run ? new LlamaClient(run.BaseUrl, run.ApiKey) : null;

    /// <summary>Журнал вывода llama-server.</summary>
    public static string LogFilePath => Path.Combine(AppPaths.LogsDir, "llama-server.log");

    /// <summary>Вызывается при каждой смене состояния (из фонового потока).</summary>
    public event Action<ServerState>? StateChanged;

    /// <summary>Каждая строка stdout/stderr llama-server (из фонового потока).</summary>
    public event Action<string>? OutputLine;

    /// <summary>
    /// Расчёт размещения «Авто» под оборудование (FitCalculator живёт в Offload.Models, поэтому передаётся снаружи).
    /// Вызывается перед каждым запуском в режиме «Авто»; null или ошибка — размещение решает --fit, как раньше.
    /// </summary>
    public Func<AppConfig, InstalledModel, CancellationToken, Task<ServerPlacement?>>? PlacementProvider { get; set; }

    /// <summary>
    /// Роль сервера. Quality — основной сервер (активная модель, порт и аргументы из настроек). Вспомогательная роль
    /// (fast/embed/rerank) — модель роли, свой порт (<see cref="ModelRoleConfig.AuxPort"/>), аргументы <see cref="AuxServerArgs"/>
    /// и свой журнал; размещение «Авто» не применяется.
    /// </summary>
    public ModelRole Role { get; init; } = ModelRole.Quality;

    /// <summary>Сколько ждать расчёта размещения (nvidia-smi, заголовок GGUF), прежде чем запускать без него.</summary>
    private static readonly TimeSpan PlacementTimeout = TimeSpan.FromSeconds(20);

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
            var placement = await ResolvePlacementAsync(cfg, ct).ConfigureAwait(false);
            var run = await Task.Run(() => Launch(cfg, placement), ct).ConfigureAwait(false);
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
        DisposeRun(run);
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
            DisposeRun(run);
        }
        FlushEvents();
    }

    // ---------- Запуск ----------

    private async Task<ServerPlacement?> ResolvePlacementAsync(AppConfig cfg, CancellationToken ct)
    {
        if (Role != ModelRole.Quality || PlacementProvider is not { } provider || cfg.Server.ContextSize > 0 || cfg.Server.CpuMoeLayers >= 0) return null;
        if (cfg.ActiveModel() is not { } model) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PlacementTimeout);
        try
        {
            return await provider(cfg, model, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("llama", $"Размещение «Авто» не рассчитано, решает --fit: {ex.Message}");
            return null;
        }
    }

    private Run Launch(AppConfig cfg, ServerPlacement? placement)
    {
        var aux = Role != ModelRole.Quality;
        var model = cfg.RoleModel(Role);
        var exe = LlamaInstaller.GetServerExePath(cfg);
        if (exe is null) throw NotConfigured(L.T("llama.cpp не установлен — запустите мастер настройки Offload."));
        if (model is null)
            throw NotConfigured(aux ? L.T("Модель для этой роли не назначена — выберите её на вкладке «Модели».") : L.T("Модель не выбрана — скачайте или добавьте модель в Offload."));
        if (string.IsNullOrWhiteSpace(model.FilePath) || !File.Exists(model.FilePath))
            throw NotConfigured(L.F("Файл модели не найден: {0}", model.FilePath));

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
        var requested = cfg.Server.AuxPort(Role);
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
        if (port != requested && aux)
        {
            var key = Role.Key();
            Log.Warn("llama", $"Порт {requested} занят — вспомогательный llama-server ({key}) запускается на порту {port}");
            cfg.Server.AuxPorts ??= [];
            cfg.Server.AuxPorts[key] = port;
            ConfigStore.Update(c => (c.Server.AuxPorts ??= [])[key] = port);
        }
        else if (port != requested)
        {
            Log.Warn("llama", $"Порт {requested} занят другой программой — llama-server запускается на порту {port}");
            cfg.Server.Port = port;
            ConfigStore.Update(c => c.Server.Port = port);
        }

        var plan = aux ? AuxServerArgs.Build(cfg, Role, model, exe, port) : LlamaServerArgs.Build(cfg, model, exe, placement);
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
        foreach (var (name, value) in LlamaServerArgs.BuildEnvironment(cfg))
        {
            if (value is null) psi.Environment.Remove(name);
            else psi.Environment[name] = value;
        }
        // PTX JIT (карты без готового кода в сборке): кэш скомпилированных ядер, чтобы не компилировать при каждом запуске.
        if (File.Exists(Path.Combine(exeDir, "ggml-cuda.dll")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_CACHE_MAXSIZE")))
            psi.Environment["CUDA_CACHE_MAXSIZE"] = "4294967296";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var run = new Run(process, plan, LocalHttp.ClientBaseUrl(host, port), cfg.Server.ApiKey ?? "");
        run.Writer = ServerLogWriter.TryOpen(AuxServerArgs.LogFilePath(Role));
        run.Writer?.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} Запуск: {LlamaServerArgs.Describe(plan)}"); // l10n-ignore: журнал llama-server

        process.OutputDataReceived += (_, e) => OnOutput(run, e.Data, run.OutClosed);
        process.ErrorDataReceived += (_, e) => OnOutput(run, e.Data, run.ErrClosed);
        process.Exited += (_, _) => _ = HandleExitAsync(run);

        try
        {
            if (!process.Start()) throw new InvalidOperationException(L.T("процесс не создан"));
        }
        catch (Exception ex)
        {
            DisposeRun(run);
            var msg = L.F("Не удалось запустить llama-server ({0}): {1}", exe, ex.Message);
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
            LastFailureBlamesBuild = false;
            SetStateLocked(ServerState.Starting);
        }
        Log.Info("llama", $"Запущен llama-server{(aux ? " (" + Role.Key() + ")" : "")} (PID {pid}), порт {port}, модель {Path.GetFileName(model.FilePath)}, контекст {plan.ContextSize}×{plan.Parallel}");
        if (plan.Placement is { } applied)
            Log.Info("llama", $"Размещение «Авто»: контекст {plan.ContextSize}×{plan.Parallel}, --n-cpu-moe {plan.CpuMoeLayers} ({applied.Reason})");
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
                if (run.StopRequested) throw new OperationCanceledException(L.T("Запуск llama-server прерван остановкой сервера."));
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
                        L.F("llama-server не загрузил модель за {0}. Возможно, модель слишком велика для этого компьютера или диск работает медленно. Подробности — в журнале llama-server.",
                            FileUtil.FormatDuration(timeout))).ConfigureAwait(false);
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
        if (run.StopRequested) throw new OperationCanceledException(L.T("Запуск llama-server прерван остановкой сервера."));
        var message = DescribeExit(run, code, whileStarting: true);
        var blamesBuild = ServerExitDiagnostics.BlamesBuild(run.TailSnapshot(), code);
        var failed = false;
        lock (_lock)
        {
            if (_run == run)
            {
                _run = null;
                ProcessId = null;
                LastError = message;
                LastFailureBlamesBuild = blamesBuild;
                SetStateLocked(ServerState.Failed);
                failed = true;
            }
        }
        FlushEvents();
        Log.Error("llama", message);
        if (failed) DisposeRun(run);
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
        DisposeRun(run);
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
        DisposeRun(run);
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
        run.Writer?.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} llama-server завершился (код {NtStatus.Format(code)})"); // l10n-ignore: журнал llama-server
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
        DisposeRun(run);
    }

    private static string DescribeExit(Run run, int code, bool whileStarting) =>
        ServerExitDiagnostics.DescribeExit(run.TailSnapshot(), code, whileStarting, ServerExitDiagnostics.PortFromArguments(run.Plan.Arguments));

    private static void DisposeRun(Run run) => run.Dispose();

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
}
