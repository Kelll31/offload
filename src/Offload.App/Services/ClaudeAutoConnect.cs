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

    /// <summary>Один прогон (по таймеру; публичен для проверки вручную). Возвращает false, если прогон пропущен.</summary>
    internal async Task<bool> RunAsync()
    {
        if (_disposed) return false;
        if (IntegrationWatcher.PausedRemaining() > TimeSpan.Zero) return false;
        if (Interlocked.Exchange(ref _running, 1) == 1) return false;
        try
        {
            var report = await _engine.RunOnceAsync(_cts.Token).ConfigureAwait(false);
            Report(report);
            return true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("integrations", "Автоподключение Claude", ex);
            return false;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
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
