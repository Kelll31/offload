using Offload.App.Forms.Pages;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.App.Forms;

/// <summary>
/// «Offload — панель управления». Единственный экземпляр; при закрытии прячется в трей
/// (если включено Ui.MinimizeToTrayOnClose), иначе закрытие окна завершает программу.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly IAppShell _shell;
    private readonly TabControl _tabs;
    private readonly List<PageBase> _pages;
    private bool _allowClose;
    private bool _hintShown;

    public MainForm(IAppShell shell)
    {
        _shell = shell;
        SuspendLayout();

        Text = "Offload — панель управления";
        Icon = AppIcons.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(820, 600);
        BackColor = Theme.Surface;
        KeyPreview = true;

        _pages =
        [
            new StatusPage(shell),
            new ModelsPage(shell),
            new ServerPage(shell),
            new IntegrationsPage(shell),
            new OpenCodePage(shell),
            new PromptPage(shell),
            new LogPage(shell),
            new AboutPage(shell),
        ];

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 5),
            Margin = Padding.Empty,
        };
        foreach (var page in _pages)
        {
            var tab = new TabPage(page.Title)
            {
                Name = page.Key,
                BackColor = Theme.Surface,
                UseVisualStyleBackColor = false,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
            };
            tab.Controls.Add(page);
            _tabs.TabPages.Add(tab);
        }
        _tabs.SelectedIndexChanged += (_, _) => UpdateActivePage();
        Controls.Add(_tabs);

        Kit.FinishForm(this);
        ResumeLayout(false);
        PerformLayout();
    }

    private PageBase? CurrentPage =>
        _tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < _pages.Count ? _pages[_tabs.SelectedIndex] : null;

    /// <summary>Открыть вкладку по ключу (status, models, server, integrations, opencode, prompt, log, about).</summary>
    public void ShowTab(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var idx = _pages.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx != _tabs.SelectedIndex) _tabs.SelectedIndex = idx;
        UpdateActivePage();
    }

    /// <summary>Показать окно поверх остальных (из трея или по IPC).</summary>
    public void ShowAndActivate()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        NativeMethods.SetForegroundWindow(Handle);
        UpdateActivePage();
    }

    /// <summary>Запустить обновление llama.cpp (пункт меню трея).</summary>
    public async Task RunLlamaUpdateAsync()
    {
        ShowTab(Tabs.Server);
        if (_pages.OfType<ServerPage>().FirstOrDefault() is { } page) await page.RunUpdateAsync();
    }

    /// <summary>Описание выполняющихся длительных операций (для подтверждения выхода) или null.</summary>
    public string? BusyDescription
    {
        get
        {
            var list = _pages.Where(p => p.IsBusy).Select(p => p.BusyDescription).Where(d => d is not null).Distinct().ToList();
            return list.Count == 0 ? null : string.Join(", ", list);
        }
    }

    public void NotifyServerStateChanged()
    {
        foreach (var p in _pages) SafeCall(p, p.OnServerStateChanged);
    }

    public void NotifyConfigChanged()
    {
        foreach (var p in _pages) SafeCall(p, p.OnConfigChanged);
    }

    /// <summary>Закрыть окно по-настоящему (выход из программы).</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private static void SafeCall(PageBase page, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("ui", $"Вкладка {page.Key}", ex);
        }
    }

    private void UpdateActivePage()
    {
        // Вызывается и из OnResize во время конструктора, когда вкладки ещё не созданы.
        if (_pages is null || _tabs is null) return;
        var visible = Visible && WindowState != FormWindowState.Minimized;
        var current = CurrentPage;
        foreach (var p in _pages)
        {
            if (visible && p == current) p.Activate();
            else p.Deactivate();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateActivePage();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateActivePage();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateActivePage();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl+Tab и Ctrl+1…8 — переключение вкладок.
        if (e.Control && e.KeyCode is >= Keys.D1 and <= Keys.D8)
        {
            var idx = e.KeyCode - Keys.D1;
            if (idx < _tabs.TabCount) _tabs.SelectedIndex = idx;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            if (ConfigStore.Current.Ui.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                if (!_hintShown)
                {
                    _hintShown = true;
                    _shell.Notify("Offload продолжает работать",
                        "Значок в области уведомлений: двойной щелчок открывает панель, правый — меню.");
                }
                return;
            }
            // Сворачивание в трей выключено — закрытие окна завершает программу.
            e.Cancel = true;
            _shell.PostToUi(() => _ = _shell.ExitAsync());
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (var p in _pages) p.Deactivate();
        base.OnFormClosed(e);
    }
}
