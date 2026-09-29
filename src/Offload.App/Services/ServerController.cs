using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Llama;
using Offload.OpenCode;

namespace Offload.App.Services;

/// <summary>
/// Единственный владелец процесса llama-server в трее: запуск/остановка (последовательно, через SemaphoreSlim),
/// проверка готовности конфигурации, автоперезапуск после падения или зависания (до 3 раз за 10 минут),
/// сторож /health и состояние сна. События StateChanged и Notification приходят в поток интерфейса.
/// </summary>
/// <remarks>
/// Выгрузку при простое делает сам llama-server (--sleep-idle-seconds): процесс остаётся жив, веса выгружаются
/// и загружаются снова при следующем запросе. Контроллер процесс по простою не останавливает, а только читает
/// is_sleeping из /props для отображения.
/// </remarks>
internal sealed class ServerController : IDisposable
{
    public const int MaxAutoRestarts = 3;
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    private readonly SynchronizationContext _ui;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    // Режим «Авто»: контекст и выгрузка экспертов MoE — по той же оценке, что показывает интерфейс (ServerFit).
    private readonly LlamaServerProcess _process = new() { PlacementProvider = ServerAutoPlacement.ResolveAsync };
    private readonly RestartBudget _budget = new(MaxAutoRestarts, RestartWindow);
    private readonly HealthWatchdog _watchdog = new();
    private readonly System.Threading.Timer _watchdogTimer;
    private readonly TunedFailureTracker _tunedFailures = new();

    private ServerState _state = ServerState.Stopped;
    private string? _lastError;
    private Exception? _lastException;
    private string? _notice;
    private bool _inOperation;
    private bool _tuning;
    private bool _disposed;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _sleeping;
    private int _watchdogBusy;
    // Клиентский режим: последняя задержка /health удалённого сервера и неответы подряд (сторож вместо перезапуска процесса).
    private TimeSpan? _remoteLatency;
    private int _remoteFailures;

    public ServerController(SynchronizationContext ui)
    {
        _ui = ui;
        _process.StateChanged += OnProcessStateChanged;
        Aux.Changed += RaiseChanged;
        _watchdogTimer = new System.Threading.Timer(_ => _ = WatchdogTickAsync(), null, HealthWatchdog.Interval, HealthWatchdog.Interval);
    }

    /// <summary>Смена состояния (в потоке интерфейса), в том числе серверов ролей.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Вспомогательные серверы ролей fast / embed / rerank (запуск по запросу из IDE).</summary>
    public AuxServers Aux { get; } = new();

    /// <summary>Запрос на всплывающее уведомление: заголовок, текст, значок (в потоке интерфейса).</summary>
    public event Action<string, string, ToolTipIcon>? Notification;

    public ServerState State
    {
        get
        {
            lock (_lock) return _state;
        }
    }

    /// <summary>Последняя ошибка (Failed) или причина, по которой сервер не настроен (NotConfigured).</summary>
    public string? LastError
    {
        get
        {
            lock (_lock) return _lastError;
        }
    }

    /// <summary>Исключение последней неудачной попытки запуска (например, LlamaVcRuntimeMissingException) или null.</summary>
    public Exception? LastException
    {
        get
        {
            lock (_lock) return _lastException;
        }
    }

    /// <summary>Пояснение к состоянию Stopped, переданное в StopAsync.</summary>
    public string? Notice
    {
        get
        {
            lock (_lock) return _notice;
        }
    }

    /// <summary>Сервер запускается, останавливается или идёт автоподбор (пробы перезапускают процесс, State при этом не меняется).</summary>
    public bool IsBusy => BusyFor(State, IsTuning);

    internal static bool BusyFor(ServerState state, bool tuning) => tuning || state is ServerState.Starting or ServerState.Stopping;

    /// <summary>
    /// Сервер работает, но модель выгружена по простою (is_sleeping из /props) и загрузится при следующем запросе.
    /// Обновляется сторожем раз в <see cref="HealthWatchdog.Interval"/>.
    /// </summary>
    public bool IsSleeping
    {
        get
        {
            lock (_lock) return _sleeping && _state == ServerState.Running;
        }
    }

    /// <summary>Последнее обращение к серверу из IDE (запись статистики, запуск по запросу) или его запуск.</summary>
    public DateTime LastActivityUtc => _lastActivityUtc;

    public static InstalledModel? Model => ConfigStore.Current.ActiveModel();

    /// <summary>
    /// Клиентский режим: основная модель на удалённом сервере (RemoteServerSettings). Трей свой основной llama-server тогда
    /// не запускает; состояние Running/Failed отражает доступность удалённого сервера, сторож его опрашивает.
    /// </summary>
    public static bool IsRemote => Ui.Try(() => ConfigStore.Current.IsRemote(), false, "IsRemote");

    /// <summary>Задержка последнего ответа /health удалённого сервера (null — не клиентский режим или нет ответа).</summary>
    public TimeSpan? RemoteLatency
    {
        get
        {
            lock (_lock) return IsRemote ? _remoteLatency : null;
        }
    }

    public ServerLaunchPlan? Plan => Ui.Try(() => _process.CurrentPlan, null, "CurrentPlan");

    public DateTime? StartedAtUtc => Ui.Try(() => _process.StartedAtUtc, null, "StartedAtUtc");

    public int? ProcessId => Ui.Try(() => _process.ProcessId, null, "ProcessId");

    public TimeSpan? Uptime => State == ServerState.Running && StartedAtUtc is DateTime t ? DateTime.UtcNow - t : null;

    /// <summary>«Работает: Qwen3-Coder 30B», «Ошибка: …».</summary>
    public string Summary
    {
        get
        {
            var s = State;
            var text = Texts.State(s);
            if (IsRemote)
            {
                var ep = ConfigStore.Current.MainEndpoint();
                return s is ServerState.Failed or ServerState.NotConfigured && !string.IsNullOrWhiteSpace(LastError)
                    ? $"{text}: {LastError}"
                    : $"{text}: {L.F("{0} на удалённом сервере {1}", ep.Model, ep.Host)}";
            }
            return s switch
            {
                _ when IsTuning => L.F("{0}: автоподбор параметров…", Texts.ModelName(Model)),
                ServerState.Running when IsSleeping => L.F("{0}: {1} (модель выгружена после простоя)", text, Texts.ModelName(Model)),
                ServerState.Running or ServerState.Starting => $"{text}: {Texts.ModelName(Model)}",
                ServerState.Failed or ServerState.NotConfigured when !string.IsNullOrWhiteSpace(LastError) => $"{text}: {LastError}",
                ServerState.Stopped when !string.IsNullOrWhiteSpace(Notice) => $"{text} — {Notice}",
                _ => text,
            };
        }
    }

    /// <summary>Отметить обращение к серверу из IDE (для отображения; выгрузкой при простое управляет llama-server).</summary>
    public void MarkActivity() => _lastActivityUtc = DateTime.UtcNow;

    /// <summary>Пересчитать «Не настроен/Остановлен», если сервер сейчас не работает.</summary>
    public void RefreshConfigured()
    {
        if (State is ServerState.Running or ServerState.Starting or ServerState.Stopping) return;
        var reason = CheckConfigured(ConfigStore.Current);
        lock (_lock)
        {
            if (reason is not null)
            {
                _state = ServerState.NotConfigured;
                _lastError = reason;
            }
            else if (_state == ServerState.NotConfigured)
            {
                _state = ServerState.Stopped;
                _lastError = null;
            }
        }
        RaiseChanged();
    }

    /// <summary>
    /// Запустить сервер и дождаться готовности. true — сервер работает.
    /// Если это первый запуск после обновления llama.cpp и он не удался, установщик откатывается на прежнюю сборку
    /// (<see cref="LlamaInstaller.ReportServerStartAsync"/>) — и запуск повторяется один раз на ней.
    /// </summary>
    /// <param name="manual">Запуск по команде пользователя (сбрасывает счётчик автоперезапусков).</param>
    public async Task<bool> StartAsync(bool manual = true, CancellationToken ct = default)
    {
        // Весь цикл «запуск → отчёт установщику → повтор после отката» — под одной блокировкой: иначе параллельный
        // запуск (IPC из IDE, автоперезапуск) увидел бы промежуточное состояние отката и вернул бы новую сборку.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ok = await StartOnceAsync(manual, ct).ConfigureAwait(false);
            if (!ShouldReportToInstaller(ok)) return ok;
            var rolledBack = await ReportStartSafeAsync(ok, ct).ConfigureAwait(false);
            if (rolledBack is null) return ok;

            Log.Warn("llama", $"Новая сборка llama.cpp не запустила сервер — откат на {rolledBack.Tag} ({rolledBack.InstallDir})");
            Notify(L.T("Откат llama.cpp"), L.F("Новая сборка не запустила сервер. Возвращена сборка {0}.", rolledBack.Tag), ToolTipIcon.Warning);
            ok = await StartOnceAsync(manual, ct).ConfigureAwait(false);
            if (ShouldReportToInstaller(ok)) await ReportStartSafeAsync(ok, ct).ConfigureAwait(false);
            return ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Сообщать установщику llama.cpp об исходе запуска: успех — всегда (подтверждает новую сборку); неудачу — только
    /// если виновата сборка (не память, не модель, не порт, не VC++ Runtime, не «не настроен» и не выход из программы).
    /// </summary>
    private bool ShouldReportToInstaller(bool ok) =>
        // Клиентский режим: llama.cpp не запускался — ни подтверждать сборку, ни откатывать её нельзя.
        !IsRemote && (ok || (!_disposed && State == ServerState.Failed && _lastException is not LlamaVcRuntimeMissingException
               && Ui.Try(() => _process.LastFailureBlamesBuild, false, "LastFailureBlamesBuild")));

    private static async Task<LlamaInstallResult?> ReportStartSafeAsync(bool ok, CancellationToken ct)
    {
        try
        {
            return await LlamaInstaller.ReportServerStartAsync(ok, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("llama", $"Проверка отката llama.cpp после запуска: {ex.Message}");
            return null;
        }
    }

    /// <summary>Запуск процесса: null — успешно, иначе понятный текст ошибки (исключение — в _lastException).</summary>
    private async Task<string?> TryStartProcessAsync(AppConfig cfg, CancellationToken ct)
    {
        try
        {
            await _process.StartAsync(cfg, null, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await SafeStopProcessAsync().ConfigureAwait(false);
            SetState(ServerState.Stopped, null);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("server", "Не удалось запустить llama-server", ex);
            lock (_lock) _lastException = ex;
            return Ui.FriendlyError(ex);
        }
    }

    private static async Task DropTunedProfileAsync(AppConfig cfg)
    {
        try
        {
            if (cfg.ActiveModel() is { } model)
                LlamaAutoTune.RemoveProfile(model.Id, await ServerAutoPlacement.HardwareKeyAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Log.Warn("server", $"Профиль автоподбора не сброшен: {ex.Message}");
        }
    }

    /// <summary>Одна попытка запуска. Вызывать под <see cref="_gate"/>.</summary>
    /// <param name="tunedFallback">Не запустился с подобранными параметрами — сбросить их и повторить.</param>
    private async Task<bool> StartOnceAsync(bool manual, CancellationToken ct, bool tunedFallback = true)
    {
        try
        {
            if (_disposed) return false;
            // Клиентский режим проверяется раньше «процесс уже работает»: свой сервер, оставшийся от прежнего режима,
            // не должен выдавать себя за удалённый — ConnectRemoteAsync его остановит и проверит удалённый.
            if (SafeProcessState() == ServerState.Running && !IsRemote)
            {
                SetState(ServerState.Running);
                return true;
            }
            if (manual) _budget.Reset();
            lock (_lock)
            {
                _inOperation = true;
                _notice = null;
                _lastException = null;
            }

            var cfg = ConfigStore.Reload();
            var reason = CheckConfigured(cfg);
            if (reason is not null)
            {
                Log.Warn("server", $"Сервер не настроен: {reason}");
                SetState(ServerState.NotConfigured, reason);
                return false;
            }
            if (cfg.IsRemote()) return await ConnectRemoteAsync(cfg, ct).ConfigureAwait(false);

            var portBefore = cfg.Server.Port;
            SetState(ServerState.Starting, null);
            Log.Info("server", $"Запуск llama-server: {Texts.ModelName(cfg.ActiveModel())}");

            var error = await TryStartProcessAsync(cfg, ct).ConfigureAwait(false);
            var tunedKey = cfg.ActiveModel()?.Id;
            var usedTuned = Ui.Try(() => _process.LastStartUsedTunedProfile, false, "LastStartUsedTunedProfile");
            if (error is null && usedTuned) _tunedFailures.Succeeded(tunedKey);
            // Не запустился с подобранными параметрами: этот запуск повторяется без них. Профиль сбрасывается, только если
            // запуск с ним не удался второй раз подряд или llama-server явно не хватило памяти; разовый сбой (холодный диск,
            // видеопамять ненадолго занята игрой) профиль не стирает.
            if (error is not null && tunedFallback && _lastException is not LlamaVcRuntimeMissingException && usedTuned)
            {
                var drop = _tunedFailures.Failed(tunedKey, Ui.Try(() => _process.LastFailureOutOfMemory, false, "LastFailureOutOfMemory"));
                if (drop)
                {
                    Log.Warn("server", "llama-server снова не запустился с подобранными параметрами — профиль автоподбора сброшен, повторный запуск без него");
                    await DropTunedProfileAsync(cfg).ConfigureAwait(false);
                }
                else
                {
                    Log.Warn("server", "llama-server не запустился с подобранными параметрами — повторный запуск без них (профиль сохранён до следующей неудачи)");
                }
                await SafeStopProcessAsync().ConfigureAwait(false);
                lock (_lock) _lastException = null;
                _process.IgnoreTunedProfile = true;
                try
                {
                    error = await TryStartProcessAsync(ConfigStore.Reload(), ct).ConfigureAwait(false);
                }
                finally
                {
                    _process.IgnoreTunedProfile = false;
                }
                if (error is null && drop)
                    Notify(L.T("Подобранные параметры сброшены"),
                        L.T("Сервер не запустился с параметрами автоподбора и запущен с обычными. Автоподбор можно повторить на странице «Сервер»."),
                        ToolTipIcon.Warning);
            }

            var ps = SafeProcessState();
            if (error is null && ps == ServerState.Running)
            {
                _lastActivityUtc = DateTime.UtcNow;
                SetState(ServerState.Running, null);
                Log.Info("server", "llama-server готов к работе");
                AfterStart(portBefore);
                return true;
            }

            error ??= SafeProcessError()
                      ?? (ps == ServerState.Starting
                          ? L.T("Сервер не успел загрузить модель за отведённое время.")
                          : L.T("Сервер не запустился — подробности в журнале llama-server."));
            if (ps is ServerState.Starting or ServerState.Running) await SafeStopProcessAsync().ConfigureAwait(false);
            SetState(ServerState.Failed, error);
            return false;
        }
        finally
        {
            lock (_lock) _inOperation = false;
        }
    }

    /// <summary>
    /// Остановить сервер. notice — пояснение для состояния «Остановлен». Серверы ролей останавливаются тоже
    /// (при необходимости они снова запустятся по запросу из IDE).
    /// </summary>
    public async Task StopAsync(string? notice = null)
    {
        await Aux.StopAllAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock) _inOperation = true;
            var ps = SafeProcessState();
            if (ps is ServerState.Running or ServerState.Starting or ServerState.Stopping)
            {
                SetState(ServerState.Stopping, null);
                Log.Info("server", "Остановка llama-server");
                await SafeStopProcessAsync().ConfigureAwait(false);
            }
            var reason = CheckConfigured(ConfigStore.Current);
            lock (_lock) _notice = notice;
            if (reason is not null) SetState(ServerState.NotConfigured, reason);
            else SetState(ServerState.Stopped, null);
        }
        finally
        {
            lock (_lock) _inOperation = false;
            _gate.Release();
        }
    }

    public async Task<bool> RestartAsync(CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        return await StartAsync(true, ct).ConfigureAwait(false);
    }

    /// <summary>Сколько ждать загрузки модели в пробном запуске автоподбора.</summary>
    private static readonly TimeSpan TuneReadyTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Сколько ждать замера скорости в пробном запуске.</summary>
    private static readonly TimeSpan TuneBenchmarkTimeout = TimeSpan.FromMinutes(4);

    /// <summary>Идёт автоподбор параметров (сервер перезапускается пробами).</summary>
    public bool IsTuning
    {
        get
        {
            lock (_lock) return _tuning;
        }
    }

    /// <summary>
    /// Автоподбор параметров llama-server (<see cref="LlamaAutoTune"/>): сервер перезапускается с пробными наборами,
    /// лучший сохраняется для пары «модель × оборудование». В конце сервер всегда запускается: с победителем, а при отмене,
    /// ошибке или неудачном запуске победителя — с прежними параметрами. Всё время подбора запуск/остановка ждут его окончания.
    /// </summary>
    /// <exception cref="InvalidOperationException">Сервер не настроен (нет llama.cpp или модели).</exception>
    public async Task<AutoTuneOutcome> AutoTuneAsync(IProgress<AutoTuneProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cfg = ConfigStore.Reload();
            if (CheckConfigured(cfg) is { } reason) throw new InvalidOperationException(reason);
            // Удалённый сервер настраивается на своём компьютере: здесь нечего перезапускать.
            if (cfg.IsRemote() || cfg.ActiveModel() is not { } model)
                throw new InvalidOperationException(L.T("Автоподбор доступен только для своего сервера: при «Удалённом сервере» его запускают на том компьютере."));
            lock (_lock)
            {
                _inOperation = true;
                _tuning = true;
                _notice = null;
            }
            RaiseChanged();
            Log.Info("server", $"Автоподбор параметров llama-server: {Texts.ModelName(model)}");
            await SafeStopProcessAsync().ConfigureAwait(false);

            TunePlan plan;
            try
            {
                plan = await ServerAutoPlacement.PrepareTuneAsync(cfg, model, ct).ConfigureAwait(false);
            }
            catch
            {
                await StartOnceAsync(true, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            Log.Info("server", $"Автоподбор: исходные параметры {plan.Baseline.Describe()}; измерения: {string.Join(", ", plan.Dimensions.Select(d => d.Name))}");
            return await LlamaAutoTune.RunAsync(new TuneHost(this, model, plan), plan.Baseline, plan.Dimensions, new AutoTuneOptions(), progress, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _process.PlacementOverride = null;
            lock (_lock)
            {
                _inOperation = false;
                _tuning = false;
            }
            _gate.Release();
            RaiseChanged();
        }
    }

    /// <summary>Пробные запуски автоподбора на процессе контроллера (вызывается под <see cref="_gate"/>).</summary>
    private sealed class TuneHost(ServerController owner, InstalledModel model, TunePlan plan) : IAutoTuneHost
    {
        private bool _saved;

        private int PlacementCpuMoe => plan.BasePlacement?.CpuMoeLayers ?? -1;

        public async Task<TuneMeasurement> MeasureAsync(TuneCandidate candidate, CancellationToken ct)
        {
            await owner.SafeStopProcessAsync().ConfigureAwait(false);
            // Одно и то же размещение для всех проб: без пересчёта между ними (видеопамять освобождается не мгновенно).
            owner._process.PlacementOverride = (plan.BasePlacement ?? ServerPlacement.Manual) with { Tuned = candidate.ToProfile(PlacementCpuMoe) };
            await owner._process.StartAsync(ConfigStore.Reload(), TuneReadyTimeout, ct).ConfigureAwait(false);
            var client = owner._process.CreateRunningClient() ?? throw new InvalidOperationException(L.T("Сервер не запущен."));
            owner.MarkActivity();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TuneBenchmarkTimeout);
            var r = await LlamaBenchmark.RunAsync(client, cts.Token).ConfigureAwait(false);
            return new TuneMeasurement(r.PromptTokensPerSecond, r.GenerationTokensPerSecond);
        }

        public async Task<bool> RestoreAsync(AutoTuneResult? apply, CancellationToken ct)
        {
            await owner.SafeStopProcessAsync().ConfigureAwait(false);
            owner._process.PlacementOverride = null;
            var cfg = ConfigStore.Current;
            if (apply is not null)
            {
                var profile = LlamaAutoTune.ProfileFrom(apply, PlacementCpuMoe, cfg.Server, cfg.Llama.InstalledTag, DateTime.UtcNow);
                LlamaAutoTune.SaveProfile(model.Id, plan.HardwareKey, profile);
                _saved = true;
                Log.Info("server", $"Автоподбор: сохранены параметры {LlamaAutoTune.Describe(profile)} (≈{profile.BaselineSeconds:0.00} → {profile.TunedSeconds:0.00} с на типичный запрос)");
            }
            else if (_saved)
            {
                // Победитель не запустился — вернуть профиль, действовавший до подбора.
                if (plan.Previous is { } previous) LlamaAutoTune.SaveProfile(model.Id, plan.HardwareKey, previous);
                else LlamaAutoTune.RemoveProfile(model.Id, plan.HardwareKey);
                _saved = false;
            }
            var ok = await owner.StartOnceAsync(true, ct, tunedFallback: apply is null).ConfigureAwait(false);
            return ok && (apply is null || Ui.Try(() => owner._process.LastStartUsedTunedProfile, false, "LastStartUsedTunedProfile"));
        }
    }

    /// <summary>Для запросов из IDE (IPC): запустить, если не запущен, и дождаться результата.</summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        MarkActivity();
        if (State == ServerState.Running && SafeProcessState() == ServerState.Running) return true;
        return await StartAsync(false, ct).ConfigureAwait(false);
    }

    /// <summary>Причина, по которой сервер нельзя запустить, или null.</summary>
    public static string? CheckConfigured(AppConfig cfg)
    {
        // Клиентский режим: ни llama.cpp, ни модель на этом компьютере не нужны — только ключ удалённого сервера.
        if (cfg.IsRemote())
            return cfg.RemoteApiKey() is null
                ? L.T("Ключ удалённого сервера не задан или не расшифровывается — введите его в разделе «Настройки» → «Удалённый сервер».")
                : null;
        bool installed;
        try
        {
            installed = LlamaInstaller.IsInstalled(cfg);
        }
        catch (Exception ex)
        {
            return L.F("Не удалось проверить установку llama.cpp: {0}", Ui.FriendlyError(ex));
        }
        if (!installed) return L.T("llama.cpp не установлен — запустите мастер настройки.");
        var model = cfg.ActiveModel();
        if (model is null) return L.T("Модель не выбрана — скачайте модель на вкладке «Модели».");
        if (string.IsNullOrWhiteSpace(model.FilePath) || !File.Exists(model.FilePath))
            return L.F("Файл модели не найден: {0}", model.FilePath);
        return null;
    }

    /// <summary>Данные для IPC-команды status.</summary>
    public Dictionary<string, string> StatusData()
    {
        var cfg = ConfigStore.Current;
        var model = cfg.ActiveModel();
        var ep = cfg.MainEndpoint();
        return new Dictionary<string, string>
        {
            ["state"] = State.ToString(),
            ["stateText"] = Texts.State(State),
            ["model"] = ep.IsRemote ? ep.Model : model?.DisplayName ?? "",
            ["modelId"] = ep.IsRemote ? ep.Model : model?.Id ?? "",
            ["baseUrl"] = ep.BaseUrl,
            ["remote"] = ep.IsRemote ? ep.Host : "",
            ["lastError"] = LastError ?? "",
            ["sleeping"] = IsSleeping ? "true" : "false",
            ["setupCompleted"] = cfg.SetupCompleted ? "true" : "false",
        };
    }

    private void AfterStart(int portBefore)
    {
        try
        {
            // Порт мог смениться (был занят) — конфиг OpenCode должен указывать на актуальный адрес.
            // Контекст — фактический из плана запуска: режим «Авто» мог уменьшить его под свободную память.
            var cfg = ConfigStore.Reload();
            var context = Plan?.ContextSize;
            OpenCodeConfigWriter.WriteManagedConfig(cfg, context);
            if (ShouldUpdateGlobalOpenCode(cfg, portBefore))
            {
                Log.Info("server", $"Порт llama-server сменился ({portBefore} → {cfg.Server.Port}) — обновляется запись Offload в глобальном конфиге OpenCode");
                OpenCodeConfigWriter.RegisterGlobal(cfg, context);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("server", $"Конфигурация OpenCode не обновлена: {ex.Message}");
        }
    }

    /// <summary>
    /// Глобальный конфиг OpenCode трогаем только если пользователь сам включил регистрацию в нём
    /// и только когда адрес сервера действительно изменился.
    /// </summary>
    internal static bool ShouldUpdateGlobalOpenCode(AppConfig cfg, int portBefore) =>
        cfg.OpenCode.RegisterInGlobalConfig && cfg.Server.Port != portBefore;

    private void OnProcessStateChanged(ServerState s)
    {
        var crash = false;
        lock (_lock)
        {
            if (_disposed) return;
            var prev = _state;
            _state = s;
            if (!_inOperation && prev == ServerState.Running && s is ServerState.Failed or ServerState.Stopped)
            {
                crash = true;
                _state = ServerState.Failed;
                _lastError = SafeProcessError() ?? L.T("Процесс llama-server неожиданно завершился.");
            }
        }
        RaiseChanged();
        if (crash) HandleCrash();
    }

    private void HandleCrash(string? title = null)
    {
        if (_disposed) return;
        var err = LastError ?? L.T("Процесс llama-server неожиданно завершился.");
        if (_budget.TryTake(out var attempt))
        {
            Log.Warn("server", $"llama-server упал: {err}. Автоперезапуск {attempt}/{MaxAutoRestarts}");
            Notify(title ?? L.T("Сервер llama.cpp остановился"), L.F("{0}{1}Перезапуск ({2} из {3})…", err, Environment.NewLine, attempt, MaxAutoRestarts), ToolTipIcon.Warning);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                if (_disposed || State is not (ServerState.Failed or ServerState.Stopped)) return;
                var ok = await StartAsync(false).ConfigureAwait(false);
                if (ok)
                    Notify(L.T("Сервер llama.cpp перезапущен"), L.T("Локальная модель снова доступна."), ToolTipIcon.Info);
                else if (State == ServerState.Failed)
                    HandleCrash();
            });
        }
        else
        {
            Log.Error("server", $"llama-server упал {MaxAutoRestarts} раза за {RestartWindow.TotalMinutes:0} минут, автоперезапуск отключён: {err}");
            SetState(ServerState.Failed, err);
            Notify(L.T("Сервер llama.cpp не работает"),
                L.F("Сервер падал {0} раза за {1:0} минут и больше не перезапускается автоматически. {2}", MaxAutoRestarts, RestartWindow.TotalMinutes, err),
                ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// Сторож: /health раз в <see cref="HealthWatchdog.Interval"/>. Зависший сервер (процесс жив, но не отвечает
    /// <see cref="HealthWatchdog.DefaultThreshold"/> раза подряд) перезапускается через общий счётчик автоперезапусков.
    /// Заодно читает is_sleeping из /props (этот запрос не будит сервер и не сбрасывает таймер простоя).
    /// </summary>
    private async Task WatchdogTickAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _watchdogBusy, 1) == 1) return;
        try
        {
            if (IsRemote)
            {
                await RemoteWatchdogTickAsync().ConfigureAwait(false);
                return;
            }
            if (!CanProbe(out var state, out var alive, out var inOperation))
            {
                _watchdog.Observe(state, alive, inOperation, HealthState.Down);
                lock (_lock) _sleeping = false;
                return;
            }

            // Адрес и ключ запущенного процесса: после ручной правки config.json они могут отличаться от настроек.
            var client = _process.CreateRunningClient();
            if (client is null) return;
            var probedPid = ProcessId;
            var health = await ProbeHealthAsync(client).ConfigureAwait(false);

            // За время запроса могли начаться остановка или перезапуск — решение по свежему состоянию.
            CanProbe(out state, out alive, out inOperation);
            switch (_watchdog.Observe(state, alive, inOperation, health))
            {
                case WatchdogVerdict.Healthy:
                    SetSleeping(await ProbeSleepingAsync(client).ConfigureAwait(false));
                    break;
                case WatchdogVerdict.Suspect:
                    Log.Warn("server", $"llama-server не ответил на /health ({_watchdog.Failures} из {_watchdog.Threshold} подряд)");
                    break;
                case WatchdogVerdict.Restart:
                    await RestartHungAsync(probedPid).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("server", $"Сторож llama-server: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _watchdogBusy, 0);
        }
    }

    // ── Клиентский режим (удалённый сервер) ───────────────────────────────────────

    /// <summary>Неответов удалённого сервера подряд, после которых он считается недоступным (опрос раз в <see cref="HealthWatchdog.Interval"/>).</summary>
    internal const int RemoteFailureThreshold = 2;

    /// <summary>
    /// Клиентский режим вместо запуска процесса: остановить свой основной сервер (если остался от прежнего режима) и проверить
    /// удалённый (/health, /v1/models, /props). Готов — Running; загружает модель — Starting (сторож переведёт в Running);
    /// нет ответа или ключ отклонён — Failed. Вызывать под <see cref="_gate"/>.
    /// </summary>
    private async Task<bool> ConnectRemoteAsync(AppConfig cfg, CancellationToken ct)
    {
        if (SafeProcessState() is ServerState.Running or ServerState.Starting) await SafeStopProcessAsync().ConfigureAwait(false);
        var ep = cfg.MainEndpoint();
        SetState(ServerState.Starting, null);
        Log.Info("server", $"Клиентский режим: подключение к удалённому серверу {ep.Host}");
        var result = await RemoteProbe.CheckAsync(ep.BaseUrl, ep.ApiKey, cfg.Remote.ModelId, ct).ConfigureAwait(false);
        lock (_lock)
        {
            _remoteLatency = result.State == HealthState.Down ? null : result.Latency;
            _remoteFailures = 0;
        }
        if (result.Error is not null)
        {
            SetState(ServerState.Failed, result.Error);
            return false;
        }
        RememberRemote(result.ModelId, result.ContextPerRequest);
        if (result.State != HealthState.Ready)
        {
            Log.Info("server", $"Удалённый сервер {ep.Host} загружает модель — ожидание готовности");
            return false;
        }
        _lastActivityUtc = DateTime.UtcNow;
        SetState(ServerState.Running, null);
        AfterRemoteConnect(result.ContextPerRequest);
        return true;
    }

    /// <summary>Запомнить id модели и контекст удалённого сервера (для OpenCode и запасной оценки в MCP), если они изменились.</summary>
    private static void RememberRemote(string? modelId, int? context)
    {
        try
        {
            var r = ConfigStore.Current.Remote;
            if (r is not null && r.ModelId == modelId && (context is null || r.ContextSize == context)) return;
            ConfigStore.Update(c =>
            {
                c.Remote ??= new RemoteServerSettings();
                c.Remote.ModelId = modelId;
                if (context is > 0) c.Remote.ContextSize = context.Value;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("server", $"Сведения об удалённом сервере не сохранены: {ex.Message}");
        }
    }

    /// <summary>Конфиг OpenCode — на удалённый сервер (адрес, id модели, контекст).</summary>
    private static void AfterRemoteConnect(int? context)
    {
        try
        {
            OpenCodeConfigWriter.WriteManagedConfig(ConfigStore.Reload(), context);
        }
        catch (Exception ex)
        {
            Log.Warn("server", $"Конфигурация OpenCode не обновлена: {ex.Message}");
        }
    }

    /// <summary>
    /// Сторож клиентского режима: процесса нет — перезапускать нечего. /health удалённого сервера раз в
    /// <see cref="HealthWatchdog.Interval"/>: пропал — Failed с уведомлением (после <see cref="RemoteFailureThreshold"/> неответов),
    /// вернулся — Running. Остановленный пользователем или не настроенный режим не опрашивается.
    /// </summary>
    private async Task RemoteWatchdogTickAsync()
    {
        ServerState state;
        lock (_lock)
        {
            if (_inOperation) return;
            state = _state;
        }
        if (state is ServerState.Stopped or ServerState.Stopping or ServerState.NotConfigured) return;

        var cfg = ConfigStore.Current;
        var client = LlamaClient.FromConfig(cfg);
        var host = RemoteServer.DisplayHost(client.BaseUrl);
        RemoteHealth health;
        using (var cts = new CancellationTokenSource(HealthWatchdog.ProbeTimeout))
        {
            try
            {
                health = await RemoteProbe.HealthAsync(client, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                health = new RemoteHealth(HealthState.Down, HealthWatchdog.ProbeTimeout, "timeout");
            }
        }

        var verdict = RemoteVerdict.None;
        lock (_lock)
        {
            // За время запроса могли остановить сервер или сменить режим — решение по свежему состоянию.
            if (_inOperation || _disposed || _state != state) return;
            (verdict, _remoteFailures) = DecideRemote(state, health.State, _remoteFailures);
            _remoteLatency = health.State == HealthState.Down ? null : health.Latency;
        }
        // /health и /v1/models llama-server отдаёт без ключа: «снова доступен» — только если ключ принят (/props требует ключ).
        if (verdict == RemoteVerdict.Up)
        {
            bool? keyAccepted;
            using (var cts = new CancellationTokenSource(HealthWatchdog.ProbeTimeout))
            {
                try
                {
                    keyAccepted = await RemoteProbe.KeyAcceptedAsync(client, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    keyAccepted = null;
                }
            }
            verdict = ConfirmKey(verdict, keyAccepted);
            lock (_lock)
            {
                if (_inOperation || _disposed || _state != state) return;
            }
        }
        switch (verdict)
        {
            case RemoteVerdict.KeyRejected:
                if (state == ServerState.Failed && LastError == RemoteProbe.KeyRejectedError) break;
                Log.Warn("server", $"Удалённый сервер {host} отвечает, но отклоняет ключ API (401/403 на /props)");
                SetState(ServerState.Failed, RemoteProbe.KeyRejectedError);
                if (state != ServerState.Failed)
                    Notify(L.T("Удалённый сервер недоступен"), RemoteProbe.KeyRejectedError, ToolTipIcon.Warning);
                break;
            case RemoteVerdict.Up:
                Log.Info("server", $"Удалённый сервер {host} доступен ({health.Latency.TotalMilliseconds:0} мс)");
                SetState(ServerState.Running, null);
                AfterRemoteConnect(null);
                if (state == ServerState.Failed)
                    Notify(L.T("Удалённый сервер снова доступен"), L.F("Модель на {0} снова отвечает.", host), ToolTipIcon.Info);
                break;
            case RemoteVerdict.Loading:
                SetState(ServerState.Starting, null);
                break;
            case RemoteVerdict.Down:
                Log.Warn("server", $"Удалённый сервер {host} не отвечает {RemoteFailureThreshold} раза подряд: {health.Detail}");
                var error = L.F("Удалённый сервер {0} не отвечает.", host);
                SetState(ServerState.Failed, error);
                Notify(L.T("Удалённый сервер недоступен"), L.F("{0} Проверьте сеть или VPN и Offload на том компьютере.", error), ToolTipIcon.Warning);
                break;
            default:
                RaiseChanged(); // обновить задержку в интерфейсе
                break;
        }
    }

    internal enum RemoteVerdict { None, Up, Loading, Down, KeyRejected }

    /// <summary>
    /// «Снова доступен» подтверждается ключом: /props ответил 401/403 — сервер работает, но наш ключ отклонён (остаёмся в Failed).
    /// null (проверить не удалось: нет /props у стороннего сервера) ключ не опровергает.
    /// </summary>
    internal static RemoteVerdict ConfirmKey(RemoteVerdict verdict, bool? keyAccepted) =>
        verdict == RemoteVerdict.Up && keyAccepted == false ? RemoteVerdict.KeyRejected : verdict;

    /// <summary>Решение сторожа клиентского режима (без сети): новое состояние и счётчик неответов подряд.</summary>
    internal static (RemoteVerdict Verdict, int Failures) DecideRemote(ServerState state, HealthState health, int failures)
    {
        switch (health)
        {
            case HealthState.Ready:
                return (state == ServerState.Running ? RemoteVerdict.None : RemoteVerdict.Up, 0);
            case HealthState.Loading:
                return (state == ServerState.Failed ? RemoteVerdict.Loading : RemoteVerdict.None, 0);
        }
        failures++;
        if (state == ServerState.Failed) return (RemoteVerdict.None, failures);
        return failures >= RemoteFailureThreshold ? (RemoteVerdict.Down, 0) : (RemoteVerdict.None, failures);
    }

    private bool CanProbe(out ServerState state, out bool alive, out bool inOperation)
    {
        lock (_lock) inOperation = _inOperation;
        state = State;
        alive = SafeProcessState() == ServerState.Running;
        return !_disposed && state == ServerState.Running && alive && !inOperation;
    }

    private static async Task<HealthState> ProbeHealthAsync(LlamaClient client)
    {
        using var cts = new CancellationTokenSource(HealthWatchdog.ProbeTimeout);
        try
        {
            return await client.GetHealthAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return HealthState.Down;
        }
    }

    private static async Task<bool> ProbeSleepingAsync(LlamaClient client)
    {
        using var cts = new CancellationTokenSource(HealthWatchdog.ProbeTimeout);
        try
        {
            var props = await client.GetPropsAsync(cts.Token).ConfigureAwait(false);
            return props?.IsSleeping == true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetSleeping(bool sleeping)
    {
        bool changed;
        lock (_lock)
        {
            changed = _sleeping != sleeping;
            _sleeping = sleeping;
        }
        if (!changed) return;
        Log.Info("server", sleeping ? "llama-server выгрузил модель после простоя (сон)" : "llama-server вышел из сна");
        RaiseChanged();
    }

    /// <summary>Процесс жив, но не отвечает: остановить и перезапустить через общий счётчик автоперезапусков.</summary>
    /// <param name="probedPid">Процесс, который не отвечал: если за время ожидания его уже перезапустили, свежий не трогаем.</param>
    private async Task RestartHungAsync(int? probedPid)
    {
        var stopped = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || State != ServerState.Running || ProcessId != probedPid) return;
            lock (_lock) _inOperation = true;
            Log.Error("server", $"llama-server не отвечает на /health {_watchdog.Threshold} раза подряд (раз в {HealthWatchdog.Interval.TotalSeconds:0} с) — процесс завис, принудительный перезапуск");
            await SafeStopProcessAsync().ConfigureAwait(false);
            SetState(ServerState.Failed, L.T("Сервер llama.cpp перестал отвечать на запросы."));
            stopped = true;
        }
        finally
        {
            lock (_lock) _inOperation = false;
            _gate.Release();
        }
        if (stopped) HandleCrash(L.T("Сервер llama.cpp завис"));
    }

    private ServerState SafeProcessState() => Ui.Try(() => _process.State, ServerState.Stopped, "process.State");

    private string? SafeProcessError()
    {
        var e = Ui.Try(() => _process.LastError, null, "process.LastError");
        return string.IsNullOrWhiteSpace(e) ? null : e;
    }

    private async Task SafeStopProcessAsync()
    {
        try
        {
            await _process.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("server", $"Ошибка остановки llama-server: {ex.Message}");
        }
    }

    private void SetState(ServerState state, string? error = null)
    {
        lock (_lock)
        {
            _state = state;
            if (state != ServerState.Running) _sleeping = false;
            if (state is ServerState.Failed or ServerState.NotConfigured) _lastError = error;
            else if (state is ServerState.Running or ServerState.Starting or ServerState.Stopped) _lastError = null;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_disposed) return;
        _ui.Post(_ =>
        {
            try { StateChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("server", "Обработчик смены состояния", ex); }
        }, null);
    }

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        if (_disposed) return;
        _ui.Post(_ =>
        {
            try { Notification?.Invoke(title, text, icon); }
            catch (Exception ex) { Log.Warn("server", $"Уведомление: {ex.Message}"); }
        }, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdogTimer.Dispose();
        _process.StateChanged -= OnProcessStateChanged;
        Aux.Changed -= RaiseChanged;
        Aux.Dispose();
        try { _process.Dispose(); } catch (Exception ex) { Log.Warn("server", $"Освобождение llama-server: {ex.Message}"); }
    }
}

/// <summary>
/// Неудачные запуски с профилем автоподбора подряд (по модели): когда профиль сбрасывать, а когда только обойти один раз.
/// </summary>
internal sealed class TunedFailureTracker
{
    /// <summary>Неудач с профилем подряд, после которых он сбрасывается.</summary>
    public const int FailuresToDrop = 2;

    private readonly object _lock = new();
    private string? _key;
    private int _count;

    /// <summary>Запуск с профилем не удался. true — профиль сбросить (вторая неудача подряд или явная нехватка памяти).</summary>
    public bool Failed(string? key, bool outOfMemory)
    {
        lock (_lock)
        {
            if (!string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
            {
                _key = key;
                _count = 0;
            }
            _count++;
            if (!outOfMemory && _count < FailuresToDrop) return false;
            _count = 0;
            return true;
        }
    }

    /// <summary>Запуск с профилем удался — счётчик неудач сбрасывается.</summary>
    public void Succeeded(string? key)
    {
        lock (_lock)
        {
            if (string.Equals(_key, key, StringComparison.OrdinalIgnoreCase)) _count = 0;
        }
    }
}

/// <summary>Счётчик автоперезапусков: не более N за скользящее окно времени.</summary>
internal sealed class RestartBudget(int max, TimeSpan window, Func<DateTime>? clock = null)
{
    private readonly List<DateTime> _times = [];
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Попытаться израсходовать одну попытку. attempt — её номер (1..max).</summary>
    public bool TryTake(out int attempt)
    {
        lock (_times)
        {
            var now = _clock();
            _times.RemoveAll(t => now - t > window);
            if (_times.Count >= max)
            {
                attempt = _times.Count;
                return false;
            }
            _times.Add(now);
            attempt = _times.Count;
            return true;
        }
    }

    public void Reset()
    {
        lock (_times) _times.Clear();
    }
}
