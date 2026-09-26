using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
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

    private ServerState _state = ServerState.Stopped;
    private string? _lastError;
    private Exception? _lastException;
    private string? _notice;
    private bool _inOperation;
    private bool _disposed;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _sleeping;
    private int _watchdogBusy;

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

    public bool IsBusy => State is ServerState.Starting or ServerState.Stopping;

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
            return s switch
            {
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
        ok || (!_disposed && State == ServerState.Failed && _lastException is not LlamaVcRuntimeMissingException
               && Ui.Try(() => _process.LastFailureBlamesBuild, false, "LastFailureBlamesBuild"));

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

    /// <summary>Одна попытка запуска. Вызывать под <see cref="_gate"/>.</summary>
    private async Task<bool> StartOnceAsync(bool manual, CancellationToken ct)
    {
        try
        {
            if (_disposed) return false;
            if (SafeProcessState() == ServerState.Running)
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

            var portBefore = cfg.Server.Port;
            SetState(ServerState.Starting, null);
            Log.Info("server", $"Запуск llama-server: {Texts.ModelName(cfg.ActiveModel())}");

            string? error = null;
            try
            {
                await _process.StartAsync(cfg, null, ct).ConfigureAwait(false);
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
                error = Ui.FriendlyError(ex);
                lock (_lock) _lastException = ex;
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
        return new Dictionary<string, string>
        {
            ["state"] = State.ToString(),
            ["stateText"] = Texts.State(State),
            ["model"] = model?.DisplayName ?? "",
            ["modelId"] = model?.Id ?? "",
            ["baseUrl"] = cfg.Server.BaseUrl,
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
