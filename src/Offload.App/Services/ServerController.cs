using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Llama;
using Offload.OpenCode;

namespace Offload.App.Services;

/// <summary>
/// Единственный владелец процесса llama-server в трее: запуск/остановка (последовательно, через SemaphoreSlim),
/// проверка готовности конфигурации, автоперезапуск после падения (до 3 раз за 10 минут),
/// выгрузка при простое. События StateChanged и Notification приходят в поток интерфейса.
/// </summary>
internal sealed class ServerController : IDisposable
{
    public const int MaxAutoRestarts = 3;
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    private readonly SynchronizationContext _ui;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly LlamaServerProcess _process = new();
    private readonly RestartBudget _budget = new(MaxAutoRestarts, RestartWindow);
    private readonly System.Threading.Timer _idleTimer;

    private ServerState _state = ServerState.Stopped;
    private string? _lastError;
    private Exception? _lastException;
    private string? _notice;
    private bool _inOperation;
    private bool _disposed;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public ServerController(SynchronizationContext ui)
    {
        _ui = ui;
        _process.StateChanged += OnProcessStateChanged;
        _idleTimer = new System.Threading.Timer(_ => CheckIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Смена состояния (в потоке интерфейса).</summary>
    public event EventHandler? StateChanged;

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

    /// <summary>Пояснение к состоянию Stopped (например, «выгружен после простоя»).</summary>
    public string? Notice
    {
        get
        {
            lock (_lock) return _notice;
        }
    }

    public bool IsBusy => State is ServerState.Starting or ServerState.Stopping;

    public InstalledModel? Model => ConfigStore.Current.ActiveModel();

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
                ServerState.Running or ServerState.Starting => $"{text}: {Texts.ModelName(Model)}",
                ServerState.Failed or ServerState.NotConfigured when !string.IsNullOrWhiteSpace(LastError) => $"{text}: {LastError}",
                ServerState.Stopped when !string.IsNullOrWhiteSpace(Notice) => $"{text} — {Notice}",
                _ => text,
            };
        }
    }

    /// <summary>Отметить обращение к серверу (для выгрузки при простое).</summary>
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
    /// </summary>
    /// <param name="manual">Запуск по команде пользователя (сбрасывает счётчик автоперезапусков).</param>
    public async Task<bool> StartAsync(bool manual = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
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
                          ? "Сервер не успел загрузить модель за отведённое время."
                          : "Сервер не запустился — подробности в журнале llama-server.");
            if (ps is ServerState.Starting or ServerState.Running) await SafeStopProcessAsync().ConfigureAwait(false);
            SetState(ServerState.Failed, error);
            return false;
        }
        finally
        {
            lock (_lock) _inOperation = false;
            _gate.Release();
        }
    }

    /// <summary>Остановить сервер. notice — пояснение для состояния «Остановлен».</summary>
    public async Task StopAsync(string? notice = null)
    {
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
            return "Не удалось проверить установку llama.cpp: " + Ui.FriendlyError(ex);
        }
        if (!installed) return "llama.cpp не установлен — запустите мастер настройки.";
        var model = cfg.ActiveModel();
        if (model is null) return "Модель не выбрана — скачайте модель на вкладке «Модели».";
        if (string.IsNullOrWhiteSpace(model.FilePath) || !File.Exists(model.FilePath))
            return $"Файл модели не найден: {model.FilePath}";
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
            ["setupCompleted"] = cfg.SetupCompleted ? "true" : "false",
        };
    }

    private void AfterStart(int portBefore)
    {
        try
        {
            // Порт мог смениться (был занят) — конфиг OpenCode должен указывать на актуальный адрес.
            var cfg = ConfigStore.Reload();
            OpenCodeConfigWriter.WriteManagedConfig(cfg);
            if (cfg.OpenCode.RegisterInGlobalConfig && cfg.Server.Port != portBefore)
                OpenCodeConfigWriter.RegisterGlobal(cfg);
        }
        catch (Exception ex)
        {
            Log.Debug("server", $"Конфигурация OpenCode не обновлена: {ex.Message}");
        }
    }

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
                _lastError = SafeProcessError() ?? "Процесс llama-server неожиданно завершился.";
            }
        }
        RaiseChanged();
        if (crash) HandleCrash();
    }

    private void HandleCrash()
    {
        if (_disposed) return;
        var err = LastError ?? "Процесс llama-server неожиданно завершился.";
        if (_budget.TryTake(out var attempt))
        {
            Log.Warn("server", $"llama-server упал: {err}. Автоперезапуск {attempt}/{MaxAutoRestarts}");
            Notify("Сервер llama.cpp остановился", $"{err}{Environment.NewLine}Перезапуск ({attempt} из {MaxAutoRestarts})…", ToolTipIcon.Warning);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                if (_disposed || State is not (ServerState.Failed or ServerState.Stopped)) return;
                var ok = await StartAsync(false).ConfigureAwait(false);
                if (ok)
                    Notify("Сервер llama.cpp перезапущен", "Локальная модель снова доступна.", ToolTipIcon.Info);
                else if (State == ServerState.Failed)
                    HandleCrash();
            });
        }
        else
        {
            Log.Error("server", $"llama-server упал {MaxAutoRestarts} раза за {RestartWindow.TotalMinutes:0} минут, автоперезапуск отключён: {err}");
            SetState(ServerState.Failed, err);
            Notify("Сервер llama.cpp не работает",
                $"Сервер падал {MaxAutoRestarts} раза за {RestartWindow.TotalMinutes:0} минут и больше не перезапускается автоматически. {err}",
                ToolTipIcon.Error);
        }
    }

    private void CheckIdle()
    {
        try
        {
            if (_disposed || State != ServerState.Running) return;
            var minutes = ConfigStore.Current.Server.IdleUnloadMinutes;
            if (minutes <= 0) return;
            var last = _lastActivityUtc;
            if (StartedAtUtc is DateTime s && s > last) last = s;
            last = Max(last, FileTimeUtc(AppPaths.UsageFile));
            last = Max(last, FileTimeUtc(Path.Combine(AppPaths.LogsDir, "llama-server.log")));
            if (DateTime.UtcNow - last < TimeSpan.FromMinutes(minutes)) return;
            Log.Info("server", $"Простой {minutes} мин — модель выгружается из памяти");
            _ = StopAsync($"модель выгружена после простоя ({minutes} мин), запустится при следующем обращении из IDE");
        }
        catch (Exception ex)
        {
            Log.Debug("server", $"Проверка простоя: {ex.Message}");
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime FileTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
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
        _idleTimer.Dispose();
        _process.StateChanged -= OnProcessStateChanged;
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
