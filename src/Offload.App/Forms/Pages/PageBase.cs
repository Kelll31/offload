using Offload.App.Services;
using Offload.App.Util;

namespace Offload.App.Forms.Pages;

/// <summary>
/// Вкладка панели управления. Activated/Deactivated вызываются, когда вкладка становится видимой
/// или скрывается (таймеры опроса работают только на видимой вкладке).
/// </summary>
internal abstract class PageBase : UserControl
{
    private int _busy;

    protected PageBase(IAppShell shell)
    {
        Shell = shell;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = Theme.Surface;
        AutoScaleMode = AutoScaleMode.Inherit;
    }

    protected IAppShell Shell { get; }

    /// <summary>Ключ вкладки (см. Tabs).</summary>
    public abstract string Key { get; }

    /// <summary>Заголовок вкладки.</summary>
    public abstract string Title { get; }

    public bool IsActive { get; private set; }

    /// <summary>Выполняется длительная операция (закрытие окна не прерывает её, но предупреждает).</summary>
    public bool IsBusy => _busy > 0;

    /// <summary>Описание текущей длительной операции (для предупреждения при выходе).</summary>
    public virtual string? BusyDescription => null;

    public void Activate()
    {
        if (IsActive || IsDisposed) return;
        IsActive = true;
        try { OnActivated(); }
        catch (Exception ex) { Core.Logging.Log.Error("ui", $"Вкладка {Key}: ошибка при открытии", ex); }
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        try { OnDeactivated(); }
        catch (Exception ex) { Core.Logging.Log.Warn("ui", $"Вкладка {Key}: {ex.Message}"); }
    }

    protected virtual void OnActivated() { }

    protected virtual void OnDeactivated() { }

    /// <summary>Конфигурация изменилась (в этом процессе).</summary>
    public virtual void OnConfigChanged() { }

    /// <summary>Сменилось состояние сервера.</summary>
    public virtual void OnServerStateChanged() { }

    protected IWin32Window? Owner => FindForm();

    /// <summary>
    /// Выполнить длительную операцию: элементы на время выключены, ошибки показываются пользователю.
    /// </summary>
    protected async Task<bool> RunBusyAsync(Func<Task> action, string errorTitle, params Control[] disable)
    {
        _busy++;
        foreach (var c in disable) c.Enabled = false;
        UpdateUiState();
        try
        {
            return await Ui.RunSafeAsync(Owner, action, errorTitle);
        }
        finally
        {
            _busy--;
            if (!IsDisposed)
            {
                foreach (var c in disable)
                {
                    if (!c.IsDisposed) c.Enabled = true;
                }
                UpdateUiState();
            }
        }
    }

    /// <summary>Пересчитать доступность кнопок (с учётом IsBusy и состояния).</summary>
    protected virtual void UpdateUiState() { }

    /// <summary>Таймер интерфейса, работающий только пока вкладка активна.</summary>
    protected System.Windows.Forms.Timer CreateTimer(int intervalMs, Action tick)
    {
        var t = new System.Windows.Forms.Timer { Interval = intervalMs };
        t.Tick += (_, _) =>
        {
            if (!IsActive || IsDisposed) return;
            try { tick(); }
            catch (Exception ex) { Core.Logging.Log.Debug("ui", $"Таймер вкладки {Key}: {ex.Message}"); }
        };
        Disposed += (_, _) => t.Dispose();
        return t;
    }
}
