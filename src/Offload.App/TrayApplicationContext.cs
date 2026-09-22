using Offload.App.Controls;
using Offload.App.Forms;
using Offload.App.Forms.Wizard;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Logging;
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
    private readonly System.Windows.Forms.Timer _configDebounce;

    private readonly ToolStripMenuItem _header;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _restart;
    private readonly ToolStripMenuItem _models;
    private readonly ToolStripMenuItem _updateLlama;
    private readonly ToolStripSeparator _updateSeparator;
    private readonly ToolStripMenuItem _autostart;

    private MainForm? _main;
    private SetupWizardForm? _wizard;
    private bool _exiting;
    private bool _disposed;

    public TrayApplicationContext(StartupView view)
    {
        // Скрытый элемент управления: гарантирует WindowsFormsSynchronizationContext в потоке интерфейса.
        _marshal = new Control();
        _marshal.CreateControl();
        _ = _marshal.Handle;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Server = new ServerController(_ui);
        Server.StateChanged += (_, _) => OnServerStateChanged();
        Server.Notification += (title, text, icon) => Notify(title, text, icon);

        _renderer = new TrayIconRenderer();
        _menu = new ContextMenuStrip { Renderer = MenuColors.Renderer(), ShowImageMargin = true };

        _header = new ToolStripMenuItem("Offload") { Enabled = false, Font = Theme.Bold(9f) };
        _open = Item("Открыть панель управления", (_, _) => ShowMainWindow(Tabs.Status));
        _open.Font = Theme.Semibold(9f);
        _start = Item("Запустить сервер", async (_, _) => await StartServerAsync());
        _stop = Item("Остановить сервер", async (_, _) => await Ui.RunSafeAsync(null, () => Server.StopAsync(), "Не удалось остановить сервер"));
        _restart = Item("Перезапустить сервер", async (_, _) => await RestartServerAsync());
        _models = new ToolStripMenuItem("Модель");
        _models.DropDownItems.Add(new ToolStripMenuItem("…"));
        _updateLlama = Item("Обновить llama.cpp", async (_, _) => await UpdateLlamaAsync());
        _updateSeparator = new ToolStripSeparator();

        var openCode = new ToolStripMenuItem("OpenCode");
        openCode.DropDownItems.Add(Item("Открыть OpenCode в папке…", async (_, _) => await OpenCodeLauncher.LaunchAsync(this, ActiveOwner())));
        openCode.DropDownItems.Add(Item("Настройки OpenCode…", (_, _) => ShowMainWindow(Tabs.OpenCode)));

        _autostart = Item("Запускать вместе с Windows", (_, _) => SetAutostart(!Autostart.IsEnabled));

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
            _updateLlama,
            new ToolStripSeparator(),
            Item("Подключение к IDE…", (_, _) => ShowMainWindow(Tabs.Integrations)),
            openCode,
            Item("Веб-чат llama.cpp", async (_, _) => await OpenWebChatAsync()),
            new ToolStripSeparator(),
            Item("Журнал…", (_, _) => ShowMainWindow(Tabs.Log)),
            Item("Настройки…", (_, _) => ShowMainWindow(Tabs.Server)),
            Item("Мастер настройки…", (_, _) => ShowSetupWizard()),
            new ToolStripSeparator(),
            _autostart,
            Item("О программе", (_, _) => ShowMainWindow(Tabs.About)),
            Item("Выход", async (_, _) => await ExitAsync()),
        });
        _updateLlama.Visible = _updateSeparator.Visible = false;
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
        _tray.BalloonTipClicked += (_, _) => ShowMainWindow();
        _tray.Visible = true;

        _configDebounce = new System.Windows.Forms.Timer { Interval = 300 };
        _configDebounce.Tick += (_, _) =>
        {
            _configDebounce.Stop();
            ConfigChanged();
        };
        ConfigStore.Saved += OnConfigSaved;

        _ipc = new IpcServer(new IpcRequestHandler(this).HandleAsync);
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

    public HardwareCache Hardware { get; } = new();

    public LlamaUpdateInfo? PendingLlamaUpdate { get; set; }

    private static ToolStripMenuItem Item(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += onClick;
        return item;
    }

    private IWin32Window? ActiveOwner() =>
        _main is { IsDisposed: false, Visible: true } m ? m : _wizard is { IsDisposed: false, Visible: true } w ? w : null;

    // ---------- Запуск ----------

    private void OnStarted(StartupView view)
    {
        try
        {
            switch (view)
            {
                case StartupView.MainWindow:
                    ShowMainWindow(Tabs.Status);
                    break;
                case StartupView.SetupWizard:
                    ShowSetupWizard();
                    break;
                default:
                    Log.Info("app", "Запуск в фоне (автозапуск)");
                    if (!ConfigStore.Current.SetupCompleted)
                        Notify("Настройка Offload не завершена", "Щёлкните значок Offload правой кнопкой и выберите «Мастер настройки…».", ToolTipIcon.Warning);
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
        if (cfg.Integrations.Count > 0)
        {
            try
            {
                var updated = await IntegrationRegistry.RefreshOutdatedAsync(McpServerSpec.ForCurrentExecutable()).ConfigureAwait(false);
                if (updated.Count > 0)
                {
                    Log.Info("integrations", "Обновлён путь к Offload в: " + string.Join(", ", updated));
                    Notify("Подключения к IDE обновлены", "Путь к Offload обновлён в: " + string.Join(", ", updated) + ". Перезапустите эти IDE.");
                }
            }
            catch (Exception ex)
            {
                Log.Debug("integrations", $"Обновление путей в IDE: {ex.Message}");
            }
        }

        SyncAutostart(cfg);

        PostToUi(() => Server.RefreshConfigured());
        cfg = ConfigStore.Reload();
        if (cfg.SetupCompleted && cfg.Server.AutoStart)
        {
            try
            {
                if (!await Server.StartAsync(manual: false).ConfigureAwait(false) && Server.State == ServerState.Failed)
                    Notify("Сервер llama.cpp не запустился", Server.LastError ?? "Подробности — в журнале.", ToolTipIcon.Error);
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
                        Notify("Доступна новая версия llama.cpp",
                            $"Версия {info.LatestTag} (установлена {info.InstalledTag ?? "—"}). Обновить можно из меню значка или на вкладке «Сервер».");
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug("llama", $"Проверка обновлений llama.cpp: {ex.Message}");
            }
        }
    }

    /// <summary>Автозапуск: значение в реестре — источник истины; путь обновляется, если программу переместили.</summary>
    private static void SyncAutostart(AppConfig cfg)
    {
        try
        {
            // Путь обновляем, только если автозапуск включён в настройках этой установки.
            if (cfg.SetupCompleted && cfg.Ui.StartWithWindows && Autostart.IsEnabled && !Autostart.IsCurrent)
            {
                Autostart.Set(true);
                Log.Info("autostart", "Путь автозапуска обновлён");
            }
            var enabled = Autostart.IsEnabled;
            if (cfg.SetupCompleted && cfg.Ui.StartWithWindows != enabled)
                ConfigStore.Update(c => c.Ui.StartWithWindows = enabled);
        }
        catch (Exception ex)
        {
            Log.Warn("autostart", $"Проверка автозапуска: {ex.Message}");
        }
    }

    // ---------- IAppShell ----------

    public void ShowMainWindow(string? tab = null)
    {
        if (_exiting) return;
        try
        {
            if (_main is null || _main.IsDisposed)
            {
                _main = new MainForm(this);
                _main.FormClosed += (_, _) => _main = null;
            }
            _main.ShowAndActivate();
            _main.ShowTab(tab);
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Не удалось открыть панель управления", ex);
            Ui.ShowError(null, "Не удалось открыть панель управления", ex);
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
            Ui.ShowError(null, "Не удалось открыть мастер настройки", ex);
        }
    }

    public void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, bool force = false)
    {
        PostToUi(() =>
        {
            if (_exiting || _disposed) return;
            if (!force && !ConfigStore.Current.Ui.ShowNotifications) return;
            try
            {
                _tray.ShowBalloonTip(6000, Texts.Truncate(title, 63), Texts.Truncate(string.IsNullOrWhiteSpace(text) ? title : text, 250), icon);
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
        Server.RefreshConfigured();
        UpdateTray();
        _main?.NotifyConfigChanged();
    }

    public async Task SwitchModelAsync(string modelId, IWin32Window? owner = null)
    {
        var cfg = ConfigStore.Current;
        if (cfg.Models.ActiveModelId == modelId && cfg.ActiveModel()?.Id == modelId) return;
        var model = cfg.Models.Installed.FirstOrDefault(m => m.Id == modelId);
        if (!Ui.RunSafe(owner, () => ModelManager.SetActive(modelId), "Не удалось сменить активную модель")) return;
        Log.Info("models", $"Активная модель: {model?.DisplayName ?? modelId}");
        ConfigChanged();

        var state = Server.State;
        if (state is ServerState.Running or ServerState.Starting or ServerState.Failed)
        {
            Notify("Смена модели", $"Перезапуск сервера с моделью «{model?.DisplayName ?? modelId}»…");
            await Ui.RunSafeAsync(owner, async () =>
            {
                if (!await Server.RestartAsync())
                    Notify("Сервер не запустился", Server.LastError ?? "Подробности — в журнале.", ToolTipIcon.Error, force: true);
            }, "Не удалось перезапустить сервер");
        }
        else
        {
            Notify("Активная модель изменена", model?.DisplayName ?? modelId);
        }
    }

    public void SetAutostart(bool enabled, IWin32Window? owner = null)
    {
        var ok = Ui.RunSafe(owner, () =>
        {
            Autostart.Set(enabled);
            ConfigStore.Update(c => c.Ui.StartWithWindows = enabled);
        }, enabled ? "Не удалось включить автозапуск" : "Не удалось выключить автозапуск");
        if (ok) _autostart.Checked = enabled;
        ConfigChanged();
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        var busy = _main?.BusyDescription;
        if (_wizard is { IsDisposed: false, IsInstalling: true }) busy = busy is null ? "установка в мастере настройки" : busy + ", установка в мастере настройки";
        if (busy is not null &&
            !Ui.Confirm(ActiveOwner(), $"Сейчас выполняется: {busy}.{Environment.NewLine}{Environment.NewLine}Прервать и выйти из Offload?", warning: true))
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
            ServerState.Running => $"{AppInfo.DisplayName} — работает: {Texts.ModelName(model)}",
            ServerState.Starting => $"{AppInfo.DisplayName} — запускается: {Texts.ModelName(model)}",
            ServerState.Stopping => $"{AppInfo.DisplayName} — останавливается",
            ServerState.Failed => $"{AppInfo.DisplayName} — ошибка сервера",
            ServerState.NotConfigured => $"{AppInfo.DisplayName} — требуется настройка",
            _ => $"{AppInfo.DisplayName} — сервер остановлен",
        };
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

        _header.Text = state is ServerState.Running or ServerState.Starting
            ? $"{Texts.State(state)} · {Texts.ModelName(model)}"
            : model is null ? Texts.State(state) : $"{Texts.State(state)} · {model.DisplayName}";
        var old = _header.Image;
        _header.Image = TrayIconRenderer.DotImage(Theme.StateColor(state), 16);
        old?.Dispose();

        var busy = Server.IsBusy;
        _start.Enabled = !busy && state is ServerState.Stopped or ServerState.Failed or ServerState.NotConfigured;
        _stop.Enabled = state is ServerState.Running or ServerState.Starting;
        _restart.Enabled = !busy && state is ServerState.Running or ServerState.Failed;

        // Модели.
        _models.DropDownItems.Clear();
        if (cfg.Models.Installed.Count == 0)
        {
            _models.DropDownItems.Add(new ToolStripMenuItem("Нет установленных моделей") { Enabled = false });
        }
        else
        {
            foreach (var m in cfg.Models.Installed.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var id = m.Id;
                var item = new ToolStripMenuItem(m.DisplayName + (string.IsNullOrWhiteSpace(m.Quant) ? "" : $"  ({m.Quant})"))
                {
                    Checked = model?.Id == id,
                    Enabled = !busy,
                };
                item.Click += async (_, _) => await SwitchModelAsync(id, ActiveOwner());
                _models.DropDownItems.Add(item);
            }
        }
        _models.DropDownItems.Add(new ToolStripSeparator());
        _models.DropDownItems.Add(Item("Скачать другую модель…", (_, _) => ShowMainWindow(Tabs.Models)));

        var update = PendingLlamaUpdate is { UpdateAvailable: true };
        _updateLlama.Visible = _updateSeparator.Visible = update;
        if (update) _updateLlama.Text = $"Обновить llama.cpp до {PendingLlamaUpdate!.LatestTag}";

        _autostart.Checked = Autostart.IsEnabled;
    }

    private async Task StartServerAsync()
    {
        await Ui.RunSafeAsync(null, async () =>
        {
            if (await Server.StartAsync()) return;
            if (Server.State == ServerState.NotConfigured)
            {
                if (Ui.Confirm(null, $"{Server.LastError}{Environment.NewLine}{Environment.NewLine}Открыть мастер настройки?"))
                    ShowSetupWizard();
            }
            else
            {
                Notify("Сервер llama.cpp не запустился", Server.LastError ?? "Подробности — в журнале.", ToolTipIcon.Error, force: true);
            }
        }, "Не удалось запустить сервер");
    }

    private async Task RestartServerAsync()
    {
        await Ui.RunSafeAsync(null, async () =>
        {
            if (!await Server.RestartAsync())
                Notify("Сервер llama.cpp не запустился", Server.LastError ?? "Подробности — в журнале.", ToolTipIcon.Error, force: true);
        }, "Не удалось перезапустить сервер");
    }

    private async Task UpdateLlamaAsync()
    {
        ShowMainWindow(Tabs.Server);
        if (_main is not null) await _main.RunLlamaUpdateAsync();
    }

    private async Task OpenWebChatAsync()
    {
        await Ui.RunSafeAsync(null, async () =>
        {
            if (Server.State != ServerState.Running)
            {
                Notify("Запуск локальной модели", "Веб-чат откроется, когда модель загрузится.", force: true);
                if (!await Server.StartAsync())
                {
                    Ui.ShowError(null, "Не удалось запустить сервер llama.cpp", Server.LastError ?? "");
                    return;
                }
            }
            var cfg = ConfigStore.Current;
            var copied = Ui.TrySetClipboard(cfg.Server.ApiKey);
            Ui.OpenShell(cfg.Server.BaseUrl);
            Notify("Веб-чат llama.cpp",
                copied
                    ? "Ключ API скопирован в буфер обмена — вставьте его в настройках веб-чата, если страница попросит ключ."
                    : $"Если страница попросит ключ API, он указан в настройках: {AppPaths.ConfigFile}",
                ToolTipIcon.Info, force: true);
        }, "Не удалось открыть веб-чат");
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

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            ConfigStore.Saved -= OnConfigSaved;
            try { _ipc.Dispose(); } catch (Exception ex) { Log.Debug("ipc", $"Остановка IPC: {ex.Message}"); }
            _configDebounce.Dispose();
            try { _main?.Dispose(); } catch { }
            try { _wizard?.Dispose(); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            _renderer.Dispose();
            _header.Image?.Dispose();
            _menu.Dispose();
            Server.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
