using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Integrations;
using Offload.Llama;
using Offload.Models;

namespace Offload.App.Services;

/// <summary>Что сделать с сервером автодополнения при очередной сверке с настройками.</summary>
internal enum AutocompleteAction
{
    /// <summary>Ничего: работает с нужной моделью или не должен работать и не работает.</summary>
    None,
    /// <summary>Запустить.</summary>
    Start,
    /// <summary>Работает с другой моделью или другими настройками — перезапустить.</summary>
    Restart,
    /// <summary>Автодополнение выключено или модель не назначена — остановить.</summary>
    Stop,
    /// <summary>Недавний запуск этой же конфигурации не удался — подождать до конца паузы.</summary>
    CoolingDown,
    /// <summary>Основной сервер сейчас запускается или останавливается — запуск отложен (видеопамять распределяется по очереди).</summary>
    Deferred,
}

/// <summary>Оценка памяти для модели автодополнения.</summary>
internal enum FimFit
{
    /// <summary>Модель не назначена или оценки нет (пользовательский GGUF без заголовка, оборудование не определено).</summary>
    Unknown,
    /// <summary>Целиком в видеопамяти рядом с основной моделью и другими ролями.</summary>
    Gpu,
    /// <summary>На процессоре (выбран режим «только процессор» или видеокарты нет) — памяти хватает.</summary>
    Cpu,
    /// <summary>Рядом с основной моделью в видеопамять не помещается — предложить режим «только процессор».</summary>
    NotBesideMain,
    /// <summary>Не хватает оперативной памяти.</summary>
    NoMemory,
}

/// <summary>Состояние автодополнения для интерфейса.</summary>
/// <param name="Endpoint">Адрес, ключ и модель для IDE (null — модель не назначена).</param>
/// <param name="Continue">Результат последней записи блока Continue (null — не записывался).</param>
internal sealed record AutocompleteStatus(
    bool Enabled,
    ServerState State,
    InstalledModel? Model,
    FimEndpoint? Endpoint,
    string? Error,
    bool Deferred,
    string? Continue);

/// <summary>Чистая логика сверки сервера автодополнения с настройками (тесты — AutocompletePolicyTests).</summary>
internal static class AutocompletePolicy
{
    /// <summary>
    /// Ключ конфигурации запуска: модель + режим «только процессор». Смена любой части — перезапуск сервера.
    /// null — сервер не нужен (выключено или модель не назначена).
    /// </summary>
    public static string? LaunchKey(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Autocomplete?.Enabled != true || cfg.RoleModel(ModelRole.Fim) is not { } model) return null;
        return cfg.Autocomplete.CpuOnly ? model.Id + "|cpu" : model.Id;
    }

    /// <param name="wantedKey">Ключ из <see cref="LaunchKey"/> (null — сервер не нужен).</param>
    /// <param name="runningKey">Ключ запущенного процесса.</param>
    /// <param name="deferStart">Основной сервер занят запуском/остановкой.</param>
    public static AutocompleteAction Decide(string? wantedKey, string? runningKey, ServerState state, string? failedKey, DateTime? failedAtUtc,
        DateTime nowUtc, bool deferStart)
    {
        var alive = state is ServerState.Running or ServerState.Starting or ServerState.Stopping;
        if (wantedKey is null) return alive ? AutocompleteAction.Stop : AutocompleteAction.None;
        var same = string.Equals(wantedKey, runningKey, StringComparison.OrdinalIgnoreCase);
        if (alive) return same ? AutocompleteAction.None : AutocompleteAction.Restart;
        if (failedAtUtc is DateTime t && nowUtc - t < AuxServerPolicy.FailureCooldown && string.Equals(wantedKey, failedKey, StringComparison.OrdinalIgnoreCase))
            return AutocompleteAction.CoolingDown;
        return deferStart ? AutocompleteAction.Deferred : AutocompleteAction.Start;
    }

    /// <summary>
    /// Помещается ли модель автодополнения рядом с остальными ролями (оценка <see cref="RoleBudget"/> с includeFim).
    /// </summary>
    public static FimFit AssessFit(RoleBudgetResult? budget, bool cpuOnly)
    {
        if (budget?.Items.FirstOrDefault(i => i.Role == ModelRole.Fim) is not { } fim) return FimFit.Unknown;
        if (fim.Fit.Level == FitLevel.TooLarge || budget.TotalRamBytes > budget.RamBudgetBytes) return FimFit.NoMemory;
        if (cpuOnly || budget.VramBudgetBytes <= 0) return FimFit.Cpu;
        return budget.TotalVramBytes <= budget.VramBudgetBytes && fim.Fit.Level == FitLevel.FullGpu ? FimFit.Gpu : FimFit.NotBesideMain;
    }

    /// <summary>
    /// Модель каталога, которую предложить скачать: если ни одной модели автодополнения не установлено — первая по приоритету
    /// модель каталога с role = fim; иначе null.
    /// </summary>
    public static CatalogModel? Suggested(IReadOnlyList<CatalogModel> catalog, AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Models.Installed.Any(m => m.Kind == ModelKind.Fim)) return null;
        return catalog.Where(m => m.Role == ModelKind.Fim).OrderBy(m => m.Priority).FirstOrDefault();
    }

    /// <summary>
    /// Адрес для IDE: адрес работающего процесса или тот, на котором он будет запущен. Ключ — свой ключ сервера автодополнения
    /// (<see cref="AutocompleteSettings.ApiKey"/>), не ключ основного сервера.
    /// </summary>
    public static FimEndpoint? Endpoint(AppConfig cfg, string? runningBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.RoleModel(ModelRole.Fim) is not { } model) return null;
        var url = string.IsNullOrWhiteSpace(runningBaseUrl) ? AuxServerArgs.ClientBaseUrl(cfg, ModelRole.Fim) : runningBaseUrl;
        return new FimEndpoint(url.TrimEnd('/'), cfg.Autocomplete?.ApiKey?.Trim() ?? "", model.Id);
    }

    /// <summary>
    /// Что сделать с блоком Continue. Записывается только адрес работающего сервера автодополнения (<paramref name="runningBaseUrl"/>):
    /// пока сервер не запущен (отложен, упал), порт по умолчанию мог занять чужой процесс — ему не должны уходить код и ключ.
    /// Выключено (или запись в Continue запрещена, модель снята) — наш блок убирается; не запущен — блок не трогаем.
    /// </summary>
    /// <param name="want">Ожидаемое содержимое блока (null — не записывать).</param>
    public static ContinueAction DecideContinue(AppConfig cfg, string? runningBaseUrl, Func<FimEndpoint?, AutocompleteTargetState> status, out FimEndpoint? want)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(status);
        want = null;
        var enabled = cfg.Autocomplete?.Enabled == true && cfg.Autocomplete.ConfigureContinue && cfg.RoleModel(ModelRole.Fim) is not null;
        if (!enabled)
            return status(null) is AutocompleteTargetState.Configured or AutocompleteTargetState.Outdated ? ContinueAction.Remove : ContinueAction.None;
        if (string.IsNullOrWhiteSpace(runningBaseUrl) || string.IsNullOrWhiteSpace(cfg.Autocomplete!.ApiKey)) return ContinueAction.None;
        want = Endpoint(cfg, runningBaseUrl);
        return want is not null && status(want) is AutocompleteTargetState.NotConfigured or AutocompleteTargetState.Outdated
            ? ContinueAction.Apply
            : ContinueAction.None;
    }
}

/// <summary>Действие с блоком Continue при сверке автодополнения.</summary>
internal enum ContinueAction { None, Apply, Remove }

/// <summary>
/// Сервер автодополнения (роль fim, ROADMAP «FIM с маленькой coder-моделью»). В отличие от серверов ролей MCP
/// (<see cref="AuxServers"/>, запуск по запросу) работает всё время, пока автодополнение включено и модель назначена:
/// IDE обращается к нему напрямую. Сверка с настройками — при изменении конфигурации и раз в <see cref="HealthWatchdog.Interval"/>:
/// запуск/остановка/перезапуск при смене модели или режима, сторож /health (зависший процесс перезапускается), после падения —
/// новая попытка не раньше <see cref="AuxServerPolicy.FailureCooldown"/>. Основной сервер его не останавливает.
/// После запуска прописывает адрес в Continue (<see cref="AutocompleteSetup"/>), при выключении — убирает.
/// </summary>
internal sealed class AutocompleteService : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FirstSync = TimeSpan.FromSeconds(10);

    private readonly LlamaServerProcess _process = new() { Role = ModelRole.Fim };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HealthWatchdog _watchdog = new();
    private readonly System.Threading.Timer _timer;
    private readonly Func<bool> _deferStart;
    private readonly object _lock = new();
    private string? _runningKey;
    private string? _failedKey;
    private DateTime? _failedAtUtc;
    private string? _error;
    private string? _continue;
    private bool _deferred;
    private DateTime? _pausedUntilUtc;
    private DateTime _lastHealthUtc;
    private int _pending;
    private bool _disposed;

    /// <param name="deferStart">true — не запускать сейчас (основной сервер запускается или останавливается).</param>
    public AutocompleteService(Func<bool> deferStart)
    {
        _deferStart = deferStart;
        _process.StateChanged += _ => RaiseChanged();
        _timer = new System.Threading.Timer(_ => _ = SyncAsync(), null, FirstSync, HealthWatchdog.Interval);
    }

    /// <summary>Смена состояния (из фонового потока).</summary>
    public event Action? Changed;

    public AutocompleteStatus Snapshot()
    {
        var cfg = ConfigStore.Current;
        var state = Ui.Try(() => _process.State, ServerState.Stopped, "fim.State");
        var url = state is ServerState.Running or ServerState.Starting ? Ui.Try(() => _process.CreateRunningClient()?.BaseUrl, null, "fim.Url") : null;
        lock (_lock)
        {
            return new AutocompleteStatus(cfg.Autocomplete?.Enabled == true, state, cfg.RoleModel(ModelRole.Fim),
                AutocompletePolicy.Endpoint(cfg, url), _error ?? (state == ServerState.Failed ? Ui.Try(() => _process.LastError, null, "fim.LastError") : null),
                _deferred, _continue);
        }
    }

    /// <summary>Сверить сервер с настройками (из любого потока; параллельные вызовы схлопываются в один повтор).</summary>
    public async Task SyncAsync()
    {
        if (_disposed) return;
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _pending, 1);
            return;
        }
        try
        {
            // Сверка, запрошенная во время текущей, выполняется сразу после неё (одним повтором).
            do
            {
                Interlocked.Exchange(ref _pending, 0);
                await SyncOnceAsync().ConfigureAwait(false);
            }
            while (!_disposed && Volatile.Read(ref _pending) == 1);
        }
        catch (Exception ex)
        {
            Log.Warn("server", $"Автодополнение: ошибка сверки с настройками: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Остановить сервер и не запускать его в течение <paramref name="pause"/> (например, перед удалением файла модели:
    /// процесс держит файл открытым). После паузы — обычная сверка с настройками.
    /// </summary>
    public async Task PauseAsync(TimeSpan pause)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock) _pausedUntilUtc = DateTime.UtcNow + pause;
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        RaiseChanged();
    }

    private async Task SyncOnceAsync()
    {
        var cfg = ConfigStore.Current;
        bool paused;
        lock (_lock) paused = _pausedUntilUtc is DateTime until && DateTime.UtcNow < until;
        var wanted = paused ? null : AutocompletePolicy.LaunchKey(cfg);
        var state = Ui.Try(() => _process.State, ServerState.Stopped, "fim.State");
        string? running, failedKey;
        DateTime? failedAt;
        lock (_lock)
        {
            // Процесс упал после запуска — повтор после паузы, как у неудачного запуска.
            if (state == ServerState.Failed && _runningKey is not null)
            {
                _failedKey = _runningKey;
                _failedAtUtc = DateTime.UtcNow;
                _error = Ui.Try(() => _process.LastError, null, "fim.LastError") ?? L.T("Сервер автодополнения завершился с ошибкой.");
                _runningKey = null;
                Log.Warn("server", $"Сервер автодополнения упал: {_error}");
            }
            running = _runningKey;
            failedKey = _failedKey;
            failedAt = _failedAtUtc;
        }
        var action = AutocompletePolicy.Decide(wanted, running, state, failedKey, failedAt, DateTime.UtcNow, Ui.Try(_deferStart, false, "fim.Defer"));
        SetDeferred(action == AutocompleteAction.Deferred);
        switch (action)
        {
            case AutocompleteAction.Stop:
                Log.Info("server", "Автодополнение выключено или модель снята — остановка сервера автодополнения");
                await StopProcessAsync().ConfigureAwait(false);
                break;
            case AutocompleteAction.Restart:
                Log.Info("server", $"Автодополнение: настройки сменились ({running} → {wanted}), перезапуск");
                await StopProcessAsync().ConfigureAwait(false);
                await StartProcessAsync(cfg, wanted!).ConfigureAwait(false);
                break;
            case AutocompleteAction.Start:
                await StartProcessAsync(cfg, wanted!).ConfigureAwait(false);
                break;
            case AutocompleteAction.None when state == ServerState.Running:
                await WatchHealthAsync(cfg, wanted!).ConfigureAwait(false);
                break;
        }
        if (wanted is null)
        {
            lock (_lock) _error = null;
        }
        await Task.Run(() => SyncContinue(ConfigStore.Current)).ConfigureAwait(false);
        RaiseChanged();
    }

    private async Task StartProcessAsync(AppConfig cfg, string key)
    {
        Log.Info("server", $"Запуск сервера автодополнения: {key}");
        lock (_lock)
        {
            _runningKey = key;
            _error = null;
        }
        _watchdog.Reset();
        try
        {
            await _process.StartAsync(cfg, ReadyTimeout).ConfigureAwait(false);
            lock (_lock)
            {
                _failedKey = null;
                _failedAtUtc = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("server", "Сервер автодополнения не запустился", ex);
            await StopProcessAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _error = Ui.FriendlyError(ex);
                _failedKey = key;
                _failedAtUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task WatchHealthAsync(AppConfig cfg, string key)
    {
        // Сверки по событиям (смена настроек, состояния основного сервера) не учащают проверки сторожа.
        if (DateTime.UtcNow - _lastHealthUtc < HealthWatchdog.Interval / 2) return;
        _lastHealthUtc = DateTime.UtcNow;
        if (_process.CreateRunningClient() is not { } client) return;
        HealthState health;
        using (var cts = new CancellationTokenSource(HealthWatchdog.ProbeTimeout))
        {
            try
            {
                health = await client.GetHealthAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
            {
                health = HealthState.Down;
            }
        }
        var state = Ui.Try(() => _process.State, ServerState.Stopped, "fim.State");
        if (_watchdog.Observe(state, state == ServerState.Running, false, health) != WatchdogVerdict.Restart) return;
        Log.Error("server", $"Сервер автодополнения не отвечает на /health {_watchdog.Threshold} раза подряд — перезапуск");
        await StopProcessAsync().ConfigureAwait(false);
        await StartProcessAsync(cfg, key).ConfigureAwait(false);
    }

    private async Task StopProcessAsync()
    {
        try
        {
            await _process.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("server", $"Ошибка остановки сервера автодополнения: {ex.Message}");
        }
        lock (_lock) _runningKey = null;
        _watchdog.Reset();
    }

    /// <summary>
    /// Continue: блок с моделью автодополнения есть, пока автодополнение включено, модель назначена и запись в Continue разрешена;
    /// иначе наш блок убирается. Адрес — работающего процесса (порт мог смениться, если 8012 был занят); пока процесс
    /// не работает, блок не записывается (<see cref="AutocompletePolicy.DecideContinue"/>).
    /// </summary>
    private void SyncContinue(AppConfig cfg)
    {
        if (!AutocompleteSetup.ContinueInstalled()) return;
        var url = Ui.Try(() => _process.State is ServerState.Running ? _process.CreateRunningClient()?.BaseUrl : null, null, "fim.Url");
        var action = AutocompletePolicy.DecideContinue(cfg, url, e => AutocompleteSetup.ContinueStatus(e).State, out var want);
        var result = action switch
        {
            ContinueAction.Apply => AutocompleteSetup.ApplyContinue(want!),
            ContinueAction.Remove => AutocompleteSetup.RemoveContinue(),
            _ => null,
        };
        if (result is null) return;
        if (result.Ok) Log.Info("Integrations", result.Message);
        else Log.Warn("Integrations", result.Message);
        lock (_lock) _continue = result.Message;
    }

    private void SetDeferred(bool value)
    {
        lock (_lock) _deferred = value;
    }

    private void RaiseChanged()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn("server", $"Обработчик состояния автодополнения: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        try { _process.Dispose(); } catch (Exception ex) { Log.Warn("server", $"Освобождение сервера автодополнения: {ex.Message}"); }
    }
}
