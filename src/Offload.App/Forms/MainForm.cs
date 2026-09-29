using Offload.App.Controls;
using Offload.App.Forms.Pages;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.App.Forms;

/// <summary>
/// «Offload — панель управления». Слева — навигация по разделам (NavigationRail), справа — заголовок и страница.
/// Единственный экземпляр; при закрытии прячется в трей (если включено Ui.MinimizeToTrayOnClose),
/// иначе закрытие окна завершает программу. При смене темы окно пересоздаётся (IAppShell.ApplyTheme).
/// </summary>
internal sealed class MainForm : Form
{
    private readonly IAppShell _shell;
    private readonly NavigationRail _nav;
    private readonly Panel _host;
    private readonly PageHeader _header = new();
    private readonly List<PageBase> _pages;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private PageBase? _current;
    private bool _allowClose;
    private bool _hintShown;
    // Восстановленное положение до первого показа: если при переносе на монитор с другим масштабом WinForms
    // пересчитает размер, он возвращается к сохранённому.
    private Rectangle? _pendingPlacement;

    public MainForm(IAppShell shell)
    {
        _shell = shell;
        IsDark = Theme.IsDark;
        SuspendLayout();

        Text = L.T("Offload — панель управления");
        Icon = AppIcons.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(860, 600);
        BackColor = Theme.Surface;
        ForeColor = Theme.TextPrimary;
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
            new NotificationsPage(shell),
            new SettingsPage(shell),
            new AboutPage(shell),
        ];

        _host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(12, 0, 0, 0) };
        foreach (var page in _pages)
        {
            page.Visible = false;
            _host.Controls.Add(page);
        }

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        content.Controls.Add(_host);
        content.Controls.Add(_header);

        _nav = new NavigationRail(AppIcons.Logo);
        _nav.SetItems(_pages.Select(p => new NavigationRail.Item(p.Key, p.Title, p.Glyph, GroupOf(p.Key))));
        _nav.Collapsed = ConfigStore.Current.Ui.NavCollapsed;
        _nav.SelectedChanged += (_, key) => Select(key);
        _nav.StatusClicked += (_, _) => ShowTab(Tabs.Status);
        _nav.ThemeClicked += (_, _) => RequestTheme(NextTheme(Theme.Mode));
        shell.Notifications.Changed += OnNotificationsChanged;
        _nav.CollapsedChanged += (_, _) =>
            Ui.RunSafe(this, () => ConfigStore.Update(c => c.Ui.NavCollapsed = _nav.Collapsed), L.T("Не удалось сохранить настройку"));

        Controls.Add(content);
        Controls.Add(_nav);

        var lastTab = ConfigStore.Current.Ui.LastTab;
        Select(_pages.Any(p => p.Key == lastTab) ? lastTab! : _pages[0].Key);
        UpdateNavStatus();
        _nav.SetBadge(Tabs.Notifications, shell.Notifications.UnreadCount);
        _statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _statusTimer.Tick += (_, _) => UpdateNavStatus();

        Kit.FinishForm(this);
        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>Окно создано в тёмной теме (цвета элементов задаются при создании).</summary>
    public bool IsDark { get; }

    /// <summary>Хотя бы на одной странице есть несохранённые изменения.</summary>
    public bool HasUnsavedChanges => _pages.Any(p => p.HasUnsavedChanges);

    /// <summary>Перерисовать рисованные элементы окна (после смены режима темы без смены цветов).</summary>
    public void RefreshChrome() => _nav.Invalidate();

    /// <summary>Ключ открытой страницы.</summary>
    public string? CurrentKey => _current?.Key;

    /// <summary>Число разделов (для подсказки о Ctrl+1…9).</summary>
    public int PageCount => _pages.Count;

    /// <summary>Не восстанавливать сохранённое положение (окно пересоздаётся с положением предыдущего).</summary>
    public bool SkipSavedPlacement { get; set; }

    private static string GroupOf(string key) => key switch
    {
        Tabs.Status or Tabs.Models => L.T("Главное"),
        Tabs.Log or Tabs.Notifications => L.T("Сервис"),
        Tabs.Server or Tabs.Integrations or Tabs.OpenCode or Tabs.Prompt => L.T("Настройка"),
        _ => L.T("Сервис"),
    };

    private static string NextTheme(string mode) => mode switch
    {
        Theme.ModeSystem => Theme.ModeLight,
        Theme.ModeLight => Theme.ModeDark,
        _ => Theme.ModeSystem,
    };

    /// <summary>
    /// Сменить тему по просьбе пользователя. Если окно придётся пересоздать, а на страницах есть несохранённые правки, —
    /// спросить. Сама смена откладывается до выхода из обработчика (окно пересоздаётся не изнутри своего же события).
    /// Возвращает false, если пользователь отказался.
    /// </summary>
    public bool RequestTheme(string mode)
    {
        var wantDark = Theme.WouldBeDark(mode, ConfigStore.Current.Ui.ThemePreset);
        if (wantDark != IsDark && !ConfirmRecreate()) return false;
        _shell.PostToUi(() => _shell.ApplyTheme(mode));
        return true;
    }

    /// <summary>
    /// Сменить внешний вид или язык (сохранить настройку и пересоздать окно). Несохранённые правки на страницах —
    /// с вопросом. Возвращает false, если пользователь отказался или сохранить не удалось.
    /// </summary>
    public bool RequestAppearance(Action<AppConfig> mutate)
    {
        if (!ConfirmRecreate()) return false;
        if (!Ui.RunSafe(this, () => ConfigStore.Update(mutate), L.T("Не удалось сохранить настройку"))) return false;
        _shell.PostToUi(_shell.ApplyAppearance);
        return true;
    }

    internal bool ConfirmRecreate(IWin32Window? owner = null)
    {
        var unsaved = _pages.Where(p => p.HasUnsavedChanges).Select(p => $"«{p.Title}»").ToList();
        return unsaved.Count == 0 || Ui.Confirm(owner ?? this,
            L.F("Окно будет открыто заново, а несохранённые изменения в разделах {0} — потеряны. Продолжить?", string.Join(", ", unsaved)));
    }

    private void Select(string key)
    {
        var page = _pages.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (page is null || page == _current) return;
        // Фокус на странице, которая скрывается, иначе Windows передаст его первому полю новой страницы
        // и прокрутит её к этому полю (страница открылась бы не с начала).
        if (_current is not null && _current.ContainsFocus) _nav.Focus();
        _host.SuspendLayout();
        page.Visible = true;
        page.BringToFront();
        if (_current is not null) _current.Visible = false;
        _host.ResumeLayout(true);
        ScrollToTop(page);
        _current = page;
        _header.Set(page.Title, page.Subtitle, page.Glyph);
        _nav.SelectedKey = page.Key;
        UpdateActivePage();
    }

    private static void ScrollToTop(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is ScrollableControl { AutoScroll: true } sc) sc.AutoScrollPosition = Point.Empty;
            ScrollToTop(c);
        }
    }

    /// <summary>Открыть вкладку по ключу (status, models, server, integrations, opencode, prompt, log, about).</summary>
    public void ShowTab(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) Select(key);
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

    /// <summary>Запустить обновление Offload (уведомление или пункт меню трея).</summary>
    public async Task RunAppUpdateAsync()
    {
        ShowTab(Tabs.About);
        if (_pages.OfType<AboutPage>().FirstOrDefault() is { } page) await page.RunUpdateAsync();
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
        UpdateNavStatus();
        foreach (var p in _pages) SafeCall(p, p.OnServerStateChanged);
    }

    public void NotifyConfigChanged()
    {
        UpdateNavStatus();
        foreach (var p in _pages) SafeCall(p, p.OnConfigChanged);
    }

    /// <summary>Закрыть окно по-настоящему (выход из программы или пересоздание при смене темы).</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void UpdateNavStatus()
    {
        try
        {
            var s = _shell.Server.State;
            var model = ConfigStore.Current.ActiveModel();
            _nav.SetStatus(Theme.StateColor(s), Texts.State(s), model is null ? L.T("модель не выбрана") : Texts.ModelName(model));
            _header.SetPills(HeaderPills(s));
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Состояние в навигации: {ex.Message}");
        }
    }

    /// <summary>Таблетки шапки: состояние сервера и связь с Claude (видны на любой странице).</summary>
    private static List<(string, Color)> HeaderPills(Offload.Llama.ServerState state)
    {
        var pills = new List<(string, Color)> { (Texts.State(state), Theme.StateColor(state)) };
        var link = ConfigStore.Current.ClaudeLink(Offload.Integrations.AutoConnectPolicy.Ids);
        if (link == ClaudeLink.Connected) pills.Add(("Claude", Theme.Green));
        else if (link == ClaudeLink.NeedsAttention) pills.Add(("Claude", Theme.Amber));
        return pills;
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
        // Вызывается и из OnResize во время конструктора, когда страницы ещё не созданы.
        if (_pages is null || _nav is null) return;
        var visible = Visible && WindowState != FormWindowState.Minimized;
        foreach (var p in _pages)
        {
            if (visible && p == _current) p.Activate();
            else p.Deactivate();
        }
        if (_statusTimer is null) return;
        if (visible) _statusTimer.Start();
        else _statusTimer.Stop();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RestorePlacement();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _pendingPlacement = null;
        UpdateActivePage();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Окно перенесено на монитор с другим масштабом при восстановлении — размер уже рассчитан под него.
        if (_pendingPlacement is { } rect) Bounds = rect;
    }

    /// <summary>Положение и размер из прошлого сеанса — с поправкой на текущие мониторы (иначе остаётся «по центру»).</summary>
    private void RestorePlacement()
    {
        if (SkipSavedPlacement) return;
        try
        {
            var saved = ConfigStore.Current.Ui.Window;
            var areas = Screen.AllScreens.Select(s => s.WorkingArea).ToList();
            var rect = WindowPlacements.Restore(saved, areas, NativeMethods.DpiAt, DeviceDpi, new Size(860, 600));
            if (rect is not { } r) return;
            _pendingPlacement = r;
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
            if (saved!.Maximized) WindowState = FormWindowState.Maximized;
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Положение окна не восстановлено: {ex.Message}");
        }
    }

    /// <summary>Запомнить положение, размер и открытый раздел (при закрытии или сворачивании в трей).</summary>
    private void SavePlacement()
    {
        if (!IsHandleCreated) return;
        try
        {
            var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (normal.Width <= 0 || normal.Height <= 0) return;
            var placement = WindowPlacements.Capture(normal, WindowState == FormWindowState.Maximized, DeviceDpi);
            var tab = _current?.Key;
            var ui = ConfigStore.Current.Ui;
            if (WindowPlacements.Same(ui.Window, placement) && ui.LastTab == tab) return;
            ConfigStore.Update(c =>
            {
                c.Ui.Window = placement;
                c.Ui.LastTab = tab;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Положение окна не сохранено: {ex.Message}");
        }
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

    private void OnNotificationsChanged() => _shell.PostToUi(() =>
    {
        if (!IsDisposed) _nav.SetBadge(Tabs.Notifications, _shell.Notifications.UnreadCount);
    });

    /// <summary>Палитра команд (Ctrl+K): разделы и действия; выбранное выполняется после закрытия палитры.</summary>
    public void ShowCommandPalette()
    {
        if (!Visible || IsDisposed) return;
        var chosen = CommandPaletteForm.Pick(this, PaletteCommands());
        // Сбой команды ловит общий обработчик Application.ThreadException (журнал и сообщение пользователю).
        chosen?.Run();
    }

    private List<PaletteCommand> PaletteCommands()
    {
        var go = L.T("Раздел");
        var act = L.T("Действие");
        var list = new List<PaletteCommand>();
        for (var i = 0; i < _pages.Count; i++)
        {
            var p = _pages[i];
            list.Add(new PaletteCommand(p.Title, go, p.Glyph, (p.Subtitle ?? "") + " " + p.Key, i < 9 ? $"Ctrl+{i + 1}" : null, () => Select(p.Key)));
        }
        list.AddRange(
        [
            new(L.T("Запустить сервер"), act, Glyphs.Play, "llama start server модель", null, // l10n-ignore — ключевые слова поиска
                () => _ = Ui.RunSafeAsync(this, async () => await _shell.Server.StartAsync(), L.T("Не удалось запустить сервер"))),
            new(L.T("Остановить сервер"), act, Glyphs.Stop, "llama stop server", null,
                () => _ = Ui.RunSafeAsync(this, () => _shell.Server.StopAsync(), L.T("Не удалось остановить сервер"))),
            new(L.T("Перезапустить сервер"), act, Glyphs.Refresh, "llama restart server", null,
                () => _ = Ui.RunSafeAsync(this, async () => await _shell.Server.RestartAsync(), L.T("Не удалось перезапустить сервер"))),
            new(L.T("Проверить подключение Claude"), act, Glyphs.Check, "claude code desktop mcp проверка verify", null, // l10n-ignore — ключевые слова поиска
                () => _ = _shell.CheckConnectionsAsync()),
            new(L.T("Подключить Claude сейчас"), act, Glyphs.Link, "claude code desktop mcp connect автоподключение", null, // l10n-ignore — ключевые слова поиска
                () => _ = _shell.ConnectClaudeNowAsync()),
            new(L.T("Переключить тему"), act, Theme.IsDark ? Glyphs.Sun : Glyphs.Moon, "theme dark light тёмная светлая", null, // l10n-ignore — ключевые слова поиска
                () => RequestTheme(NextTheme(Theme.Mode))),
            new(L.T("Мастер настройки"), act, Glyphs.Wand, "wizard setup установка", null, _shell.ShowSetupWizard), // l10n-ignore — ключевые слова поиска
            new(L.T("Сочетания клавиш"), act, Glyphs.Keyboard, "hotkeys shortcuts справка help", "F1", ShowShortcuts), // l10n-ignore — ключевые слова поиска
            new(L.T("Выход из Offload"), act, Glyphs.Power, "exit quit закрыть", null, () => _ = _shell.ExitAsync()), // l10n-ignore — ключевые слова поиска
        ]);
        return list;
    }

    private void ShowShortcuts()
    {
        using var f = new ShortcutsForm();
        f.ShowDialog(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.K)
        {
            e.Handled = e.SuppressKeyPress = true;
            BeginInvoke(ShowCommandPalette);
            return;
        }
        if (e.KeyCode == Keys.F1)
        {
            e.Handled = true;
            BeginInvoke(ShowShortcuts);
            return;
        }
        if (e.Control && e.Shift && e.KeyCode == Keys.N)
        {
            e.Handled = e.SuppressKeyPress = true;
            Select(Tabs.Notifications);
            return;
        }
        // Ctrl+1…9 — разделы по порядку, Ctrl+Tab / Ctrl+Shift+Tab — следующий/предыдущий.
        if (e.Control && e.KeyCode is >= Keys.D1 and <= Keys.D9)
        {
            var idx = e.KeyCode - Keys.D1;
            if (idx < _pages.Count) Select(_pages[idx].Key);
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.Tab && _current is not null)
        {
            var idx = _pages.IndexOf(_current) + (e.Shift ? -1 : 1);
            Select(_pages[(idx + _pages.Count) % _pages.Count].Key);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePlacement();
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            if (ConfigStore.Current.Ui.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                if (!_hintShown)
                {
                    _hintShown = true;
                    _shell.Notify(L.T("Offload продолжает работать"),
                        L.T("Значок в области уведомлений: двойной щелчок открывает панель, правый — меню."));
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
        _statusTimer.Stop();
        foreach (var p in _pages) p.Deactivate();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            _shell.Notifications.Changed -= OnNotificationsChanged;
        }
        base.Dispose(disposing);
    }
}
