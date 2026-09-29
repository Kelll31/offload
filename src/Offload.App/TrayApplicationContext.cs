using Microsoft.Win32;
using Offload.App.Controls;
using Offload.App.Forms;
using Offload.App.Forms.Wizard;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Localization;
using Offload.Core.Logging;
using Offload.Core.Usage;
using Offload.Integrations;
using Offload.Llama;
using Offload.Models;

namespace Offload.App;

/// <summary>
/// Трей-приложение: значок с цветной точкой состояния, контекстное меню, IPC-сервер для MCP-процессов,
/// единственные экземпляры панели управления и мастера настройки.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext, IAppShell
{
    private const int MaxTooltip = 127;

    private readonly Control _marshal;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly TrayIconRenderer _renderer;
    private readonly ContextMenuStrip _menu;
    private readonly IpcServer _ipc;
    private readonly BackgroundJobHost _jobs;
    private readonly System.Windows.Forms.Timer _configDebounce;
    private readonly IntegrationWatcher _integrationWatcher = new();
    private readonly ClaudeAutoConnect _claudeAutoConnect = new();

    private readonly ToolStripMenuItem _header;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _restart;
    private readonly ToolStripMenuItem _models;
    private readonly ToolStripMenuItem _updateLlama;
    private readonly ToolStripMenuItem _updateApp;
    private readonly ToolStripSeparator _updateSeparator;
    private readonly ToolStripMenuItem _autostart;

    private MainForm? _main;
    private bool _themePending;
    private bool _themePendingByUser;
    // Отложена смена языка/схемы/акцента: окно нужно пересоздать, даже если светлая/тёмная не меняется.
    private bool _themePendingForce;
    private SetupWizardForm? _wizard;
    private bool _exiting;
    private bool _disposed;
    // Что открыть по щелчку на последнем показанном уведомлении (null — просто окно).
    private Action? _balloonClick;

    public TrayApplicationContext(StartupView view)
    {
        // Скрытый элемент управления: гарантирует WindowsFormsSynchronizationContext в потоке интерфейса.
        _marshal = new Control();
        _marshal.CreateControl();
        _ = _marshal.Handle;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Server = new ServerController(_ui);
        Server.StateChanged += (_, _) => OnServerStateChanged();
        Server.Notification += (title, text, icon) => Notify(title, text, icon, tab: ServerNotificationTab(icon));
        // Автодополнение в IDE: свой сервер. Запуск откладывается, пока основной сервер запускается (или вот-вот запустится
        // автоматически после старта Offload): размещение «Авто» основной модели считается по свободной видеопамяти.
        var startedUtc = DateTime.UtcNow;
        Autocomplete = new AutocompleteService(() => Server.IsBusy
            || (ConfigStore.Current.Server.AutoStart && Server.State == ServerState.Stopped && DateTime.UtcNow - startedUtc < TimeSpan.FromMinutes(1)));
        Autocomplete.Changed += () => PostToUi(() => _main?.NotifyServerStateChanged());

        _renderer = new TrayIconRenderer();
        _menu = new ContextMenuStrip { Renderer = MenuColors.Renderer(), ShowImageMargin = true };

        _header = new ToolStripMenuItem("Offload") { Enabled = false, Font = Theme.Bold(9f) };
        _open = Item("Открыть панель управления", (_, _) => ShowMainWindow(Tabs.Status)); // l10n-key
        _open.Font = Theme.Semibold(9f);
        _start = Item("Запустить сервер", async (_, _) => await StartServerAsync()); // l10n-key
        _stop = Item("Остановить сервер", async (_, _) => await Ui.RunSafeAsync(null, () => Server.StopAsync(), L.T("Не удалось остановить сервер"))); // l10n-key
        _restart = Item("Перезапустить сервер", async (_, _) => await RestartServerAsync()); // l10n-key
        _models = Localized(new ToolStripMenuItem("Модель")); // l10n-key
        _models.DropDownItems.Add(new ToolStripMenuItem("…"));
        _updateLlama = Item("Обновить llama.cpp", async (_, _) => await UpdateLlamaAsync()); // l10n-key
        _updateApp = Item("Обновить Offload", async (_, _) => await UpdateAppAsync()); // l10n-key
        _updateSeparator = new ToolStripSeparator();

        var openCode = new ToolStripMenuItem("OpenCode");
        openCode.DropDownItems.Add(Item("Открыть OpenCode в папке…", async (_, _) => await OpenCodeLauncher.LaunchAsync(this, ActiveOwner()))); // l10n-key
        openCode.DropDownItems.Add(Item("Настройки OpenCode…", (_, _) => ShowMainWindow(Tabs.OpenCode))); // l10n-key

        _autostart = Item("Запускать вместе с Windows", (_, _) => SetAutostart(!Autostart.IsEnabled)); // l10n-key

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _header,
            _open,
            new ToolStripSeparator(),
            _start,
            _stop,
            _restart,
            _models,
            _updateSeparator,
            _updateApp,
            _updateLlama,
            new ToolStripSeparator(),
            Item("Подключение к IDE…", (_, _) => ShowMainWindow(Tabs.Integrations)), // l10n-key
            openCode,
            Item("Веб-чат llama.cpp", async (_, _) => await OpenWebChatAsync()), // l10n-key
            new ToolStripSeparator(),
            Item("Журнал…", (_, _) => ShowMainWindow(Tabs.Log)), // l10n-key
            Item("Настройки…", (_, _) => ShowMainWindow(Tabs.Settings)), // l10n-key
            Item("Мастер настройки…", (_, _) => ShowSetupWizard()), // l10n-key
            new ToolStripSeparator(),
            _autostart,
            Item("О программе", (_, _) => ShowMainWindow(Tabs.About)), // l10n-key
            Item("Выход", async (_, _) => await ExitAsync()), // l10n-key
        });
        _updateLlama.Visible = _updateApp.Visible = _updateSeparator.Visible = false;
        _menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = AppInfo.DisplayName,
            Visible = false,
        };
        _renderer.Apply(_tray, Theme.StateColor(ServerState.Stopped));
        _tray.DoubleClick += (_, _) =>
        {
            if (ConfigStore.Current.SetupCompleted || _wizard is null) ShowMainWindow();
            else ShowSetupWizard();
        };
        // Щелчок по уведомлению открывает то, к чему оно относится (журнал для ошибок сервера, «Сервер» для обновления…).
        _tray.BalloonTipClicked += (_, _) => (_balloonClick ?? (() => ShowMainWindow()))();
        _tray.Visible = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _configDebounce = new System.Windows.Forms.Timer { Interval = 300 };
        _configDebounce.Tick += (_, _) =>
        {
            _configDebounce.Stop();
            ConfigChanged();
        };
        ConfigStore.Saved += OnConfigSaved;
        ConfigStore.ExternallyChanged += OnConfigSaved;
        ConfigStore.InvalidFile += OnConfigInvalid;

        _ipc = new IpcServer(new IpcRequestHandler(this, _jobs = new BackgroundJobHost((title, text, icon) => Notify(title, text, icon, tab: Tabs.Log))).HandleAsync);
        try
        {
            _ipc.Start();
        }
        catch (Exception ex)
        {
            Log.Error("ipc", "Не удалось запустить IPC-сервер", ex);
        }

        Server.RefreshConfigured();
        UpdateTray();
        _ui.Post(_ => OnStarted(view), null);
    }

    public ServerController Server { get; }

    public AutocompleteService Autocomplete { get; }

    public HardwareCache Hardware { get; } = new();

    public LlamaUpdateInfo? PendingLlamaUpdate { get; set; }

    public AppUpdateInfo? PendingAppUpdate { get; set; }

    public bool IsExiting => _exiting;

    public event EventHandler<UsageRecord>? UsageRecorded;

    public void ReportUsageRecorded(UsageRecord record) => PostToUi(() => UsageRecorded?.Invoke(this, record));

    /// <summary>Пункты меню с исходным (русским) текстом — для перевода на лету при смене языка.</summary>
    private readonly List<(ToolStripItem Item, string Ru)> _menuTexts = [];

    private ToolStripMenuItem Item(string text, EventHandler onClick)
    {
        var item = Localized(new ToolStripMenuItem(text));
        item.Click += onClick;
        return item;
    }

    private ToolStripMenuItem Localized(ToolStripMenuItem item)
    {
        _menuTexts.Add((item, item.Text ?? ""));
        item.Text = L.T(item.Text ?? "");
        return item;
    }

    private void RefreshMenuTexts()
    {
        foreach (var (item, ru) in _menuTexts) item.Text = L.T(ru);
    }

    private IWin32Window? ActiveOwner() =>
        _main is { IsDisposed: false, Visible: true } m ? m : _wizard is { IsDisposed: false, Visible: true } w ? w : null;

    // ---------- Запуск ----------

    private void OnStarted(StartupView view)
    {
        // Остатки прошлого обновления — до того, как пользователь успеет начать новое.
        AppUpdater.CleanupLeftovers(AppPaths.ExecutablePath);
        try
        {
            switch (view)
            {
                case StartupView.MainWindow:
                    // Без явного раздела: окно откроется на разделе, который был открыт в прошлый раз.
                    ShowMainWindow();
                    break;
                case StartupView.SetupWizard:
                    ShowSetupWizard();
                    break;
                default:
                    Log.Info("app", "Запуск в фоне (автозапуск)");
                    if (!ConfigStore.Current.SetupCompleted)
                        ShowBalloon(L.T("Настройка Offload не завершена"), L.T("Щёлкните значок Offload правой кнопкой и выберите «Мастер настройки…»."),
                            ToolTipIcon.Warning, force: false, ShowSetupWizard);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("app", "Не удалось открыть окно при запуске", ex);
        }

        _ = Task.Run(BackgroundStartupAsync);
    }

    /// <summary>Фоновые задачи при старте: проверка моделей, путей в IDE, автозапуска, сервера и обновлений.</summary>
    private async Task BackgroundStartupAsync()
    {
        var cfg = ConfigStore.Current;
        _jobs.RecoverInterrupted();

        try
        {
            var removed = ModelManager.Validate();
            if (removed > 0)
            {
                Log.Warn("models", $"Удалено записей о пропавших моделях: {removed}");
                PostToUi(ConfigChanged);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("models", $"Проверка моделей: {ex.Message}");
        }

        // Только если эта установка сама подключала IDE: иначе можно «перехватить» подключения другой копии Offload.
        // Копия, запущенная не из папки установки (портативная, dev-сборка), пути не трогает — это делает установленная.
        var foreign = InstallInfo.Foreign;
        if (foreign is not null)
            Log.Info("install", $"Это не установленная копия (установлена: {foreign.ExePath}) — пути в IDE и автозапуск не обновляются");
        var ownsSetup = foreign is null && !DevMode.Active;
        if (DevMode.Active)
            Log.Info("install", $"Режим разработчика ({DevMode.EnvVar}) — пути в IDE и автозапуск не обновляются");
        if (cfg.Integrations.Count > 0 && ownsSetup)
        {
            try
            {
                var updated = await IntegrationRegistry.RefreshOutdatedAsync(InstallInfo.McpSpec()).ConfigureAwait(false);
                if (updated.Count > 0)
                {
                    Log.Info("integrations", "Обновлён путь к Offload в: " + string.Join(", ", updated));
                    Notify(L.T("Подключения к IDE обновлены"), L.F("Путь к Offload обновлён в: {0}. Перезапустите эти IDE.", string.Join(", ", updated)),
                        tab: Tabs.Integrations);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("integrations", $"Обновление путей в IDE: {ex.Message}");
            }
        }

        if (ownsSetup) SyncAutostart(cfg);
        if (ownsSetup) _integrationWatcher.Start(this);
        if (ownsSetup) _claudeAutoConnect.Start(this);

        PostToUi(() => Server.RefreshConfigured());
        cfg = ConfigStore.Reload();
        if (cfg.SetupCompleted && cfg.Server.AutoStart)
        {
            try
            {
                if (!await Server.StartAsync(manual: false).ConfigureAwait(false) && Server.State == ServerState.Failed)
                    Notify(cfg.IsRemote() ? L.T("Удалённый сервер недоступен") : L.T("Сервер llama.cpp не запустился"),
                        Server.LastError ?? L.T("Подробности — в журнале."), ToolTipIcon.Error, tab: Tabs.Log);
            }
            catch (Exception ex)
            {
                Log.Error("server", "Автозапуск сервера", ex);
            }
        }

        if (cfg.SetupCompleted && cfg.Llama.CheckUpdates && Ui.Try(() => LlamaInstaller.IsInstalled(cfg), false, "IsInstalled"))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                var info = await LlamaInstaller.CheckUpdateAsync(cfg, cts.Token).ConfigureAwait(false);
                if (info.UpdateAvailable)
                {
                    Log.Info("llama", $"Доступна новая версия llama.cpp: {info.LatestTag} (установлена {info.InstalledTag})");
                    PostToUi(() =>
                    {
                        PendingLlamaUpdate = info;
                        Notify(L.T("Доступна новая версия llama.cpp"),
                            L.F("Версия {0} (установлена {1}). Обновить можно из меню значка или на вкладке «Сервер».", info.LatestTag, info.InstalledTag ?? "—"),
                            tab: Tabs.Server);
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug("llama", $"Проверка обновлений llama.cpp: {ex.Message}");
            }
        }

        ShowWhatsNewIfUpdated(cfg, ownsSetup);
        await CheckAppUpdateAsync(cfg).ConfigureAwait(false);

        // Удалённый каталог моделей: подпись Ed25519, не чаще раза в сутки; без сети остаётся прежний каталог.
        try
        {
            if ((await RemoteCatalog.RefreshIfDueAsync().ConfigureAwait(false)).Status == CatalogUpdateStatus.Updated)
                PostToUi(() => _main?.NotifyConfigChanged());
        }
        catch (Exception ex)
        {
            Log.Debug("catalog", $"Проверка удалённого каталога: {ex.Message}");
        }
    }

    /// <summary>
    /// Первый запуск после обновления — уведомление «Что нового» (раз на версию; щелчок открывает «О программе»).
    /// Версию запоминает только «своя» копия: dev-сборка или чужая копия с той же папкой данных не сбивает показ.
    /// </summary>
    private void ShowWhatsNewIfUpdated(AppConfig cfg, bool ownsSetup)
    {
        var current = AppInfo.Version;
        var last = cfg.Ui.LastRunVersion;
        if (!ownsSetup || string.Equals(last, current, StringComparison.Ordinal)) return;
        try
        {
            ConfigStore.Update(c => c.Ui.LastRunVersion = current);
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"Версия запуска не сохранена: {ex.Message}");
        }
        if (WhatsNew.UpdatedFrom(last, current) is not { } from) return;
        Log.Info("update", $"Offload обновлён: {from} → {current}");
        Notify(L.F("Offload обновлён до {0}", current),
            L.T("Что нового — в разделе «О программе». Перезапустите IDE, чтобы их MCP-серверы Offload работали на новой версии."),
            tab: Tabs.About);
    }

    /// <summary>Проверка обновления Offload при запуске (не чаще раза в 12 ч; в режиме разработчика — никогда).</summary>
    private async Task CheckAppUpdateAsync(AppConfig cfg)
    {
        if (!cfg.Ui.CheckAppUpdates) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var info = await AppUpdater.CheckAsync(force: false, cts.Token).ConfigureAwait(false);
            if (info is null) return;
            PostToUi(() =>
            {
                PendingAppUpdate = info;
                _main?.NotifyConfigChanged();
                ShowBalloon(L.F("Доступна новая версия Offload {0}", info.Version),
                    info.CanInstall
                        ? L.T("Щёлкните, чтобы обновить. Обновить можно и из меню значка или в разделе «О программе».")
                        : L.T("Щёлкните, чтобы открыть страницу релиза: эту установку нужно обновить установщиком."),
                    ToolTipIcon.Info, force: false, () => _ = UpdateAppAsync());
            });
        }
        catch (Exception ex)
        {
            Log.Debug("update", $"Проверка обновлений Offload: {ex.Message}");
        }
    }

    /// <summary>
    /// Автозапуск: значение в реестре — источник истины; путь обновляется, только если программу переместили (прежнего exe нет).
    /// Автозапуск другой существующей копии не перехватывается (<see cref="Autostart.Decide"/>).
    /// </summary>
    private static void SyncAutostart(AppConfig cfg)
    {
        try
        {
            var value = Autostart.CurrentValue;
            var (repoint, enabled) = Autostart.Decide(cfg.SetupCompleted, cfg.Ui.StartWithWindows, value, Autostart.Command, File.Exists);
            if (repoint)
            {
                Autostart.Set(true);
                Log.Info("autostart", "Путь автозапуска обновлён");
            }
            else if (!enabled && Autostart.IsEnabled)
            {
                Log.Info("autostart", $"Автозапуск указывает на другую копию Offload ({Autostart.ExeOf(value)}) — не меняется");
            }
            if (cfg.SetupCompleted && cfg.Ui.StartWithWindows != enabled)
                ConfigStore.Update(c => c.Ui.StartWithWindows = enabled);
        }
        catch (Exception ex)
        {
            Log.Warn("autostart", $"Проверка автозапуска: {ex.Message}");
        }
    }

    // ---------- IAppShell ----------

    public void ApplyTheme(string mode)
    {
        if (!Ui.RunSafe(null, () => ConfigStore.Update(c => c.Ui.Theme = mode), L.T("Не удалось сохранить тему"))) return;
        RefreshTheme(userInitiated: true);
    }

    public void ApplyAppearance()
    {
        Program.ApplyLanguage(ConfigStore.Current.Ui.Language);
        RefreshMenuTexts();
        UpdateTray();
        RefreshTheme(userInitiated: true, force: true);
        // Открытый мастер — тоже заново, на новом языке и в новой палитре (кроме идущей установки).
        if (_wizard is { IsDisposed: false, IsInstalling: false } wizard) wizard.Reopen(applyAppearance: false);
    }

    public bool ConfirmWindowRecreate(IWin32Window? owner) =>
        _main is not { IsDisposed: false } main || main.ConfirmRecreate(owner);

    /// <summary>
    /// Применить тему из настроек (и из Windows для режима «как в системе»). Если меняется светлая/тёмная, окно панели
    /// пересоздаётся — но не во время длительной операции, не под открытым диалогом и (для смены темы Windows) не при
    /// несохранённых правках: тогда тема откладывается (<see cref="_themePending"/>) и применяется, когда окно освободится.
    /// Палитра меняется только вместе с окном, чтобы окно не оказалось «наполовину» в другой теме.
    /// </summary>
    private void RefreshTheme(bool userInitiated, bool force = false)
    {
        var ui = ConfigStore.Current.Ui;
        var wantDark = Theme.WouldBeDark(ui.Theme, ui.ThemePreset);
        var main = _main is { IsDisposed: false } m ? m : null;
        if (main is null || main.IsDark == wantDark && !force)
        {
            _themePending = _themePendingForce = false;
            ApplyPalette();
            main?.RefreshChrome();
            return;
        }
        if (RecreateBlocker(main, userInitiated) is { } reason)
        {
            _themePending = true;
            _themePendingByUser |= userInitiated;
            _themePendingForce |= force;
            if (userInitiated)
                Notify(L.T("Тема изменится позже"), L.F("{0}. Новая тема применится, когда окно освободится.", reason), force: true);
            return;
        }
        _themePending = _themePendingByUser = _themePendingForce = false;
        ApplyPalette();
        RecreateMainWindow(main);
    }

    /// <summary>Почему окно сейчас нельзя пересоздать (null — можно).</summary>
    private static string? RecreateBlocker(MainForm main, bool userInitiated)
    {
        if (main.BusyDescription is { } busy) return L.F("Сейчас выполняется: {0}", busy);
        // Модальный диалог (MessageBox, выбор папки) отключает окно-владельца; пересоздание уничтожило бы его под диалогом.
        if (main.OwnedForms.Length > 0 || (main.IsHandleCreated && !NativeMethods.IsWindowEnabled(main.Handle)))
            return L.T("Открыто диалоговое окно");
        if (!userInitiated && main.HasUnsavedChanges) return L.T("Есть несохранённые изменения");
        return null;
    }

    private void ApplyPalette()
    {
        Program.ApplyColorMode(ConfigStore.Current.Ui);
        _menu.Renderer = MenuColors.Renderer();
        UpdateTray();
    }

    private void RecreateMainWindow(MainForm main)
    {
        var tab = main.CurrentKey;
        var visible = main.Visible;
        var state = main.WindowState;
        var bounds = main.WindowState == FormWindowState.Normal ? main.Bounds : main.RestoreBounds;
        main.ForceClose();
        _main = null;
        if (!visible) return;
        _main = CreateMainForm();
        _main.SkipSavedPlacement = true;
        _main.StartPosition = FormStartPosition.Manual;
        _main.Bounds = bounds;
        _main.ShowAndActivate();
        if (state == FormWindowState.Maximized) _main.WindowState = FormWindowState.Maximized;
        _main.ShowTab(tab);
    }

    private MainForm CreateMainForm()
    {
        var form = new MainForm(this);
        form.FormClosed += (_, _) =>
        {
            if (_main == form) _main = null;
        };
        // Спрятали в трей — хороший момент применить отложенную тему.
        form.VisibleChanged += (_, _) =>
        {
            if (!form.Visible && _themePending) PostToUi(TryApplyPendingTheme);
        };
        return form;
    }

    private void TryApplyPendingTheme()
    {
        if (_themePending) RefreshTheme(_themePendingByUser, _themePendingForce);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)) return;
        if (ConfigStore.Current.Ui.Theme is Theme.ModeLight or Theme.ModeDark) return;
        // Включение/выключение высокой контрастности Windows меняет схему (тема «как в Windows» → «Высокий контраст»).
        var contrastChanged = Theme.SystemHighContrast() != Theme.HighContrastApplied;
        if (!contrastChanged && Theme.SystemPrefersDark() == Theme.IsDark) return;
        PostToUi(() => RefreshTheme(userInitiated: false, force: contrastChanged));
    }

    /// <summary>Смена масштаба или мониторов: значок трея перерисовывается под новый размер.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => PostToUi(UpdateTray);

    public void ShowMainWindow(string? tab = null)
    {
        if (_exiting) return;
        try
        {
            TryApplyPendingTheme();
            if (_main is null || _main.IsDisposed) _main = CreateMainForm();
            _main.ShowAndActivate();
            _main.ShowTab(tab);
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Не удалось открыть панель управления", ex);
            Ui.ShowError(null, L.T("Не удалось открыть панель управления"), ex);
        }
    }

    public void ShowSetupWizard()
    {
        if (_exiting) return;
        try
        {
            if (_wizard is null || _wizard.IsDisposed)
            {
                _wizard = new SetupWizardForm(this);
                // Немодальная форма освобождается сама при закрытии.
                _wizard.FormClosed += (_, _) => _wizard = null;
                _wizard.Show();
            }
            if (_wizard.WindowState == FormWindowState.Minimized) _wizard.WindowState = FormWindowState.Normal;
            _wizard.Activate();
            _wizard.BringToFront();
            NativeMethods.SetForegroundWindow(_wizard.Handle);
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Не удалось открыть мастер настройки", ex);
            Ui.ShowError(null, L.T("Не удалось открыть мастер настройки"), ex);
        }
    }

    public void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, bool force = false, string? tab = null) =>
        ShowBalloon(title, text, icon, force, () => ShowMainWindow(tab));

    /// <summary>Раздел для уведомлений сервера: сбои и предупреждения — «Журнал», остальное — «Состояние».</summary>
    internal static string ServerNotificationTab(ToolTipIcon icon) =>
        icon is ToolTipIcon.Error or ToolTipIcon.Warning ? Tabs.Log : Tabs.Status;

    /// <summary>Показать уведомление и запомнить, что сделать по щелчку на нём.</summary>
    private void ShowBalloon(string title, string text, ToolTipIcon icon, bool force, Action onClick)
    {
        PostToUi(() =>
        {
            if (_exiting || _disposed) return;
            if (!force && !ConfigStore.Current.Ui.ShowNotifications) return;
            try
            {
                _tray.ShowBalloonTip(6000, Texts.Truncate(title, 63), Texts.Truncate(string.IsNullOrWhiteSpace(text) ? title : text, 250), icon);
                _balloonClick = onClick;
            }
            catch (Exception ex)
            {
                Log.Debug("ui", $"Уведомление не показано: {ex.Message}");
            }
        });
    }

    public void PostToUi(Action action)
    {
        if (_disposed) return;
        _ui.Post(_ =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("ui", "Ошибка в действии интерфейса", ex);
            }
        }, null);
    }

    public void ConfigChanged()
    {
        if (_disposed) return;
        Log.ApplyLevel(ConfigStore.Current.Ui.VerboseLog);
        Server.RefreshConfigured();
        _ = Autocomplete.SyncAsync();
        UpdateTray();
        _main?.NotifyConfigChanged();
    }

    public async Task SwitchModelAsync(string modelId, IWin32Window? owner = null)
    {
        var cfg = ConfigStore.Current;
        if (cfg.Models.ActiveModelId == modelId && cfg.ActiveModel()?.Id == modelId) return;
        var model = cfg.Models.Installed.FirstOrDefault(m => m.Id == modelId);
        if (!Ui.RunSafe(owner, () => ModelManager.SetActive(modelId), L.T("Не удалось сменить активную модель"))) return;
        Log.Info("models", $"Активная модель: {model?.DisplayName ?? modelId}");
        ConfigChanged();

        var state = Server.State;
        if (state is ServerState.Running or ServerState.Starting or ServerState.Failed)
        {
            Notify(L.T("Смена модели"), L.F("Перезапуск сервера с моделью «{0}»…", model is null ? modelId : Texts.ModelName(model)));
            await Ui.RunSafeAsync(owner, async () =>
            {
                if (!await Server.RestartAsync())
                    Notify(L.T("Сервер не запустился"), Server.LastError ?? L.T("Подробности — в журнале."), ToolTipIcon.Error, force: true);
            }, L.T("Не удалось перезапустить сервер"));
        }
        else
        {
            Notify(L.T("Активная модель изменена"), model is null ? modelId : Texts.ModelName(model));
        }
    }

    public void SetAutostart(bool enabled, IWin32Window? owner = null)
    {
        var ok = Ui.RunSafe(owner, () =>
        {
            Autostart.Set(enabled);
            ConfigStore.Update(c => c.Ui.StartWithWindows = enabled);
        }, enabled ? L.T("Не удалось включить автозапуск") : L.T("Не удалось выключить автозапуск"));
        if (ok) _autostart.Checked = enabled;
        ConfigChanged();
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        var busy = _main?.BusyDescription;
        if (_wizard is { IsDisposed: false, IsInstalling: true }) busy = busy is null ? L.T("установка в мастере настройки") : busy + ", " + L.T("установка в мастере настройки");
        busy = BackgroundJobHost.AddBusy(busy);
        if (busy is not null &&
            !Ui.Confirm(ActiveOwner(), L.F("Сейчас выполняется: {0}.{1}{1}Прервать и выйти из Offload?", busy, Environment.NewLine), warning: true))
            return;

        _exiting = true;
        Log.Info("app", "Выход из Offload");
        try
        {
            _wizard?.Close();
            _main?.ForceClose();
        }
        catch (Exception ex)
        {
            Log.Warn("app", $"Закрытие окон: {ex.Message}");
        }

        _tray.Visible = false;
        try
        {
            await Server.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Log.Warn("app", $"Остановка сервера при выходе: {ex.Message}");
        }
        ExitThread();
    }

    // ---------- Сервер и значок ----------

    private void OnServerStateChanged()
    {
        // Отложенный запуск автодополнения — сразу, как только основной сервер запустился.
        if (Autocomplete.Snapshot().Deferred) _ = Autocomplete.SyncAsync();
        UpdateTray();
        _main?.NotifyServerStateChanged();
    }

    private void UpdateTray()
    {
        if (_disposed) return;
        var state = Server.State;
        try
        {
            _renderer.Apply(_tray, Theme.StateColor(state));
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Значок трея: {ex.Message}");
        }
        var model = ConfigStore.Current.ActiveModel();
        var text = state switch
        {
            ServerState.Running when Server.IsSleeping => L.F("{0} — модель выгружена после простоя: {1}", AppInfo.DisplayName, Texts.ModelName(model)),
            ServerState.Running => L.F("{0} — работает: {1}", AppInfo.DisplayName, Texts.ModelName(model)),
            ServerState.Starting => L.F("{0} — запускается: {1}", AppInfo.DisplayName, Texts.ModelName(model)),
            ServerState.Stopping => L.F("{0} — останавливается", AppInfo.DisplayName),
            ServerState.Failed => L.F("{0} — ошибка сервера", AppInfo.DisplayName),
            ServerState.NotConfigured => L.F("{0} — требуется настройка", AppInfo.DisplayName),
            _ => L.F("{0} — сервер остановлен", AppInfo.DisplayName),
        };
        // Вторая строка: связь с Claude (по итогу последней проверки подключения; файлы IDE здесь не читаются).
        var claude = Texts.ClaudeLink(ConfigStore.Current);
        if (claude is not null) text = Texts.Truncate(text, MaxTooltip - claude.Length - 1) + "\n" + claude;
        try
        {
            _tray.Text = Texts.Truncate(text, MaxTooltip);
        }
        catch (ArgumentException)
        {
            _tray.Text = AppInfo.DisplayName;
        }
    }

    private void RefreshMenu()
    {
        var cfg = ConfigStore.Current;
        var state = Server.State;
        var model = cfg.ActiveModel();

        _header.Text = state == ServerState.Running && Server.IsSleeping
            ? L.F("{0} · {1} · модель выгружена", Texts.State(state), Texts.ModelName(model))
            : state is ServerState.Running or ServerState.Starting
            ? $"{Texts.State(state)} · {Texts.ModelName(model)}"
            : model is null ? Texts.State(state) : $"{Texts.State(state)} · {Texts.ModelName(model)}";
        var old = _header.Image;
        _header.Image = TrayIconRenderer.DotImage(Theme.StateColor(state), 16);
        old?.Dispose();

        var busy = Server.IsBusy;
        _start.Enabled = !busy && state is ServerState.Stopped or ServerState.Failed or ServerState.NotConfigured;
        _stop.Enabled = state is ServerState.Running or ServerState.Starting;
        _restart.Enabled = !busy && state is ServerState.Running or ServerState.Failed;

        // Модели.
        _models.DropDownItems.Clear();
        // Только чат-модели: эмбеддинги и реранкеры активными не бывают — их назначают ролям на странице «Модели».
        var chat = cfg.Models.Installed.Where(m => m.Kind == ModelKind.Chat).ToList();
        if (chat.Count == 0)
        {
            _models.DropDownItems.Add(new ToolStripMenuItem(cfg.Models.Installed.Count == 0
                ? L.T("Нет установленных моделей")
                : L.T("Нет установленных чат-моделей")) { Enabled = false });
        }
        else
        {
            foreach (var m in chat.OrderBy(Texts.ModelName, StringComparer.CurrentCultureIgnoreCase))
            {
                var id = m.Id;
                var item = new ToolStripMenuItem(Texts.ModelName(m) + (string.IsNullOrWhiteSpace(m.Quant) ? "" : $"  ({m.Quant})"))
                {
                    Checked = model?.Id == id,
                    Enabled = !busy,
                };
                item.Click += async (_, _) => await SwitchModelAsync(id, ActiveOwner());
                _models.DropDownItems.Add(item);
            }
        }
        _models.DropDownItems.Add(new ToolStripSeparator());
        var download = new ToolStripMenuItem(L.T("Скачать другую модель…"));
        download.Click += (_, _) => ShowMainWindow(Tabs.Models);
        _models.DropDownItems.Add(download);

        var update = PendingLlamaUpdate is { UpdateAvailable: true };
        var appUpdate = PendingAppUpdate is not null;
        _updateLlama.Visible = update;
        _updateApp.Visible = appUpdate;
        _updateSeparator.Visible = update || appUpdate;
        if (update) _updateLlama.Text = L.F("Обновить llama.cpp до {0}", PendingLlamaUpdate!.LatestTag);
        if (appUpdate) _updateApp.Text = L.F("Обновить Offload до {0}", PendingAppUpdate!.Version);

        _autostart.Checked = Autostart.IsEnabled;
    }

    private async Task StartServerAsync()
    {
        await Ui.RunSafeAsync(null, async () =>
        {
            if (await Server.StartAsync()) return;
            if (Server.State == ServerState.NotConfigured)
            {
                if (Ui.Confirm(null, L.F("{0}{1}{1}Открыть мастер настройки?", Server.LastError, Environment.NewLine)))
                    ShowSetupWizard();
            }
            else
            {
                Notify(L.T("Сервер llama.cpp не запустился"), Server.LastError ?? L.T("Подробности — в журнале."), ToolTipIcon.Error, force: true);
            }
        }, L.T("Не удалось запустить сервер"));
    }

    private async Task RestartServerAsync()
    {
        await Ui.RunSafeAsync(null, async () =>
        {
            if (!await Server.RestartAsync())
                Notify(L.T("Сервер llama.cpp не запустился"), Server.LastError ?? L.T("Подробности — в журнале."), ToolTipIcon.Error, force: true);
        }, L.T("Не удалось перезапустить сервер"));
    }

    private async Task UpdateLlamaAsync()
    {
        ShowMainWindow(Tabs.Server);
        if (_main is not null) await _main.RunLlamaUpdateAsync();
    }

    /// <summary>Обновление Offload: раздел «О программе» показывает прогресс и предупреждения.</summary>
    private async Task UpdateAppAsync()
    {
        ShowMainWindow(Tabs.About);
        if (_main is not null) await _main.RunAppUpdateAsync();
    }

    private async Task OpenWebChatAsync()
    {
        await Ui.RunSafeAsync(null, async () =>
        {
            if (Server.State != ServerState.Running)
            {
                Notify(L.T("Запуск локальной модели"), L.T("Веб-чат откроется, когда модель загрузится."), force: true);
                if (!await Server.StartAsync())
                {
                    Ui.ShowError(null, L.T("Не удалось запустить сервер llama.cpp"), Server.LastError ?? "");
                    return;
                }
            }
            // Клиентский режим — веб-чат удалённого сервера и его ключ (пользователь сам попросил открыть чат).
            var ep = ConfigStore.Current.MainEndpoint();
            var copied = Ui.TrySetClipboard(ep.ApiKey);
            Ui.OpenShell(ep.BaseUrl);
            Notify(L.T("Веб-чат llama.cpp"),
                copied
                    ? L.T("Ключ API скопирован в буфер обмена — вставьте его в настройках веб-чата, если страница попросит ключ.")
                    : L.F("Если страница попросит ключ API, он указан в настройках: {0}", AppPaths.ConfigFile),
                ToolTipIcon.Info, force: true);
        }, L.T("Не удалось открыть веб-чат"));
    }

    private void OnConfigSaved(AppConfig _)
    {
        // Вызывается из потока, сохранившего конфигурацию.
        PostToUi(() =>
        {
            if (_disposed) return;
            _configDebounce.Stop();
            _configDebounce.Start();
        });
    }

    private void OnConfigInvalid(string error)
    {
        // Вызывается из потока, обратившегося к настройкам; прежние настройки продолжают действовать.
        Notify(L.T("Ошибка в config.json"),
            L.F("Файл настроек изменён вручную, но содержит ошибку: {0}. Пока действуют прежние настройки — исправьте файл.", error),
            ToolTipIcon.Warning, force: true, tab: Tabs.Settings);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            ConfigStore.Saved -= OnConfigSaved;
            ConfigStore.ExternallyChanged -= OnConfigSaved;
            ConfigStore.InvalidFile -= OnConfigInvalid;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            try { _ipc.Dispose(); } catch (Exception ex) { Log.Debug("ipc", $"Остановка IPC: {ex.Message}"); }
            _jobs.Dispose();
            _configDebounce.Dispose();
            _integrationWatcher.Dispose();
            _claudeAutoConnect.Dispose();
            try { _main?.Dispose(); } catch { }
            try { _wizard?.Dispose(); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            _renderer.Dispose();
            _header.Image?.Dispose();
            _menu.Dispose();
            Autocomplete.Dispose();
            Server.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
