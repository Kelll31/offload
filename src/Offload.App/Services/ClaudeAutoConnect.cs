using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Integrations;

namespace Offload.App.Services;

/// <summary>
/// Автоподключение Claude Code и Claude Desktop, поставленных до или после Offload: первый прогон через 20 секунд после
/// старта, затем раз в 15 минут. Решает <see cref="AutoConnectPolicy"/>, делает <see cref="AutoConnectEngine"/>; здесь —
/// только расписание, защита от параллельных прогонов и одно объединённое уведомление на прогон.
/// В режиме разработчика и в копии не из папки установки ничего не делает (как <see cref="IntegrationWatcher"/>).
/// Пока пользователь сам правит подключения (<see cref="IntegrationWatcher.PauseFor"/>), прогон откладывается.
/// </summary>
internal sealed class ClaudeAutoConnect : IDisposable
{
    internal static readonly TimeSpan FirstRun = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly AutoConnectEngine _engine = new(new AutoConnectHost
    {
        GetSpec = InstallInfo.McpSpec,
        Pause = IntegrationWatcher.PauseFor,
    });
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer? _timer;
    private IAppShell? _shell;
    private int _running;
    private bool _started;
    private bool _disposed;

    public void Start(IAppShell shell)
    {
        if (DevMode.Active || InstallInfo.Foreign is not null)
        {
            Log.Info("integrations", "Автоподключение Claude не действует (режим разработчика или копия не из папки установки)");
            return;
        }
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _shell = shell;
            _timer = new System.Threading.Timer(_ => _ = RunAsync(), null, FirstRun, Interval);
        }
    }

    /// <summary>
    /// Один прогон: подключить появившийся Claude, затем раз в сутки перепроверить работающие подключения.
    /// Возвращает false, если прогон пропущен (идёт другой, пауза автовосстановления).
    /// </summary>
    internal async Task<bool> RunAsync() => await RunCoreAsync().ConfigureAwait(false) is not null;

    /// <summary>Прогон с итогом автоподключения (null — пропущен).</summary>
    private async Task<AutoConnectReport?> RunCoreAsync()
    {
        if (_disposed) return null;
        if (IntegrationWatcher.PausedRemaining() > TimeSpan.Zero) return null;
        if (Interlocked.Exchange(ref _running, 1) == 1) return null;
        try
        {
            var report = await _engine.RunOnceAsync(_cts.Token).ConfigureAwait(false);
            Report(report);
            var recheck = await _engine.RecheckAsync(force: false, _cts.Token).ConfigureAwait(false);
            if (!recheck.IsEmpty) ReportRecheck(recheck);
            return report;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("integrations", "Автоподключение Claude", ex);
            return null;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Подключить Claude сейчас (по кнопке или пункту меню трея): прогон без ожидания таймера. В режиме разработчика и в копии
    /// не из папки установки — только сообщение, что автоматика здесь не действует.
    /// </summary>
    public async Task ConnectNowAsync(IAppShell shell)
    {
        if (DevMode.Active || InstallInfo.Foreign is not null)
        {
            shell.Notify(L.T("Автоподключение недоступно"), L.T("Эта копия Offload запущена не из папки установки или в режиме разработчика — подключите IDE на странице «Интеграции»."),
                ToolTipIcon.Warning, force: true, tab: Tabs.Integrations);
            return;
        }
        _shell ??= shell;
        if (await RunCoreAsync().ConfigureAwait(false) is not { } report)
        {
            shell.Notify(L.T("Подключение уже выполняется"), L.T("Offload уже проверяет подключения — результат придёт уведомлением."), force: true);
            return;
        }
        if (!report.IsEmpty) return; // итог уже показан уведомлением прогона
        // Подключать нечего — объяснить почему.
        var cfg = ConfigStore.Current;
        var rows = ClaudeOverview.Build(cfg, InstallInfo.McpSpec());
        var (title, text) = cfg.ClaudeLink(AutoConnectPolicy.Ids) != ClaudeLink.None
            ? (L.T("Claude уже подключён"), L.T("Offload уже подключён к найденному Claude. Проверить подключение можно кнопкой «Проверить сейчас»."))
            : !rows.Any(r => r.Installed)
                ? (L.T("Claude не найден"), L.T("Установите Claude Code или Claude Desktop — Offload подключится к нему сам."))
                : !cfg.Ui.AutoRepairIntegrations
                    ? (L.T("Автоподключение выключено"), L.T("Включите «Подключать Claude автоматически и следить за подключениями к IDE» на странице «Интеграции» или подключите Claude там вручную."))
                    : (L.T("Claude не подключён автоматически"), L.T("Вы отказались от подключения или в настройках Claude уже есть чужая запись «offload» — подключите его на странице «Интеграции»."));
        shell.Notify(title, text, force: true, tab: Tabs.Integrations);
    }

    /// <summary>Перепроверить все подключения запуском сервера и сообщить итог (даже если ничего не изменилось).</summary>
    public async Task CheckNowAsync(IAppShell shell)
    {
        var engine = DevMode.Active || InstallInfo.Foreign is not null ? CheckOnlyEngine() : _engine;
        RecheckReport report;
        try
        {
            report = await engine.RecheckAsync(force: true, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var states = ConfigStore.Current.IntegrationStates;
        var failed = AutoConnectPolicy.Candidates(ConfigStore.Current)
            .Where(id => ConfigStore.Current.Integrations.Contains(id) && states.TryGetValue(id, out var st) && st is { LastCheckUtc: not null, LastCheckOk: false })
            .Select(id => IntegrationRegistry.Find(id)?.DisplayName ?? id)
            .ToList();
        if (!report.IsEmpty)
        {
            var (title, text, warning) = report.Notice();
            shell.Notify(title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info, force: true, tab: Tabs.Integrations);
        }
        else if (failed.Count > 0)
        {
            shell.Notify(L.T("Подключение требует внимания"), L.F("Проверка не прошла: {0}. Подробности — на странице «Интеграции».", string.Join(", ", failed)),
                ToolTipIcon.Warning, force: true, tab: Tabs.Integrations);
        }
        else if (ConfigStore.Current.ClaudeLink(AutoConnectPolicy.Ids) == ClaudeLink.None)
        {
            shell.Notify(L.T("Claude не подключён"), L.T("Claude Code и Claude Desktop не подключены к Offload — подключите их на странице «Интеграции»."),
                force: true, tab: Tabs.Integrations);
        }
        else
        {
            shell.Notify(L.T("Подключения работают"), L.T("Сервер Offload запускается и отвечает во всех подключённых программах."), force: true);
        }
        shell.PostToUi(shell.ConfigChanged);
    }

    /// <summary>Проверка без автоподключения (dev-сборка и чужая копия: только чтение конфигов и запуск сервера).</summary>
    private static AutoConnectEngine CheckOnlyEngine() => new(new AutoConnectHost { GetSpec = InstallInfo.McpSpec });

    private void ReportRecheck(RecheckReport report)
    {
        var shell = _shell;
        if (shell is null) return;
        var (title, text, warning) = report.Notice();
        shell.Notify(title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info, tab: Tabs.Integrations);
        shell.PostToUi(shell.ConfigChanged);
    }

    private void Report(AutoConnectReport report)
    {
        var shell = _shell;
        if (shell is null || report.IsEmpty) return;
        var (title, text, warning) = report.Notice();
        shell.Notify(title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info, tab: Tabs.Integrations);
        shell.PostToUi(shell.ConfigChanged);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
