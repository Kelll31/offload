using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.OpenCode;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «OpenCode»: установка агента OpenCode и настройки агентных задач локальной модели.</summary>
internal sealed class OpenCodePage : PageBase
{
    private readonly Label _state = Kit.Label("", Theme.Semibold(10f));
    private readonly Label _path = Kit.Hint("");
    private readonly Label _latest = Kit.Hint("");
    private readonly Button _install;
    private readonly Button _uninstall;
    private readonly Button _launch;
    private readonly ProgressPanel _progress = new();

    private readonly CheckBox _enabled = Kit.Check(L.F("Использовать OpenCode для агентных задач ({0})", McpToolNames.EditFiles));
    private readonly CheckBox _allowShell = Kit.Check(L.T("Разрешить агенту выполнять команды оболочки"));
    private readonly Label _shellWarning = Kit.Wrap(
        L.T("Внимание: агент сможет запускать любые команды в папке проекта (сборку, тесты, скрипты). Включайте, только если понимаете риск."),
        Theme.Regular(8.5f), Theme.WarnText);
    private readonly NumericUpDown _timeout = Kit.Number(1, 240, 15, 80);
    private readonly CheckBox _global = Kit.Check(L.T("Добавить Offload в глобальный конфиг OpenCode"));
    private readonly Button _save;
    private readonly Label _saveStatus = Kit.Hint("", autoWidth: true);

    private CancellationTokenSource? _cts;
    private bool _loading;
    private bool _dirty;
    private bool _latestChecked;

    public OpenCodePage(IAppShell shell) : base(shell)
    {
        _install = Kit.Primary(L.T("Установить"), async (_, _) => await InstallAsync(), 130);
        _uninstall = Kit.Button(L.T("Удалить"), async (_, _) => await UninstallAsync());
        _launch = Kit.Button(L.T("Открыть OpenCode в папке…"), async (_, _) => await OpenCodeLauncher.LaunchAsync(Shell, Owner), 180);
        _save = Kit.Primary(L.T("Сохранить"), (_, _) => Save());
        _progress.CancelRequested += (_, _) => _cts?.Cancel();

        var root = Kit.Table();
        root.AddRow(Kit.Section("OpenCode", first: true));
        root.AddRow(Kit.Hint(
            L.T("OpenCode — агент для программирования. Offload запускает его с локальной моделью, чтобы она могла выполнять многошаговые задачи: читать проект, править несколько файлов и проверять результат. Устанавливается отдельный исполняемый файл в папку Offload, Node.js не нужен.")));
        var head = Kit.Grid();
        head.AddField(L.T("Состояние:"), _state);
        root.AddRow(head);
        root.AddRow(_path);
        root.AddRow(_latest);
        root.AddRow(Kit.Flow(_install, _uninstall, _launch));
        root.AddRow(_progress);

        root.AddRow(Kit.Section(L.T("Агентные задачи")));
        root.AddRow(_enabled);
        root.AddRow(Kit.Hint(L.T("Если выключено, правки выполняются только прямой перезаписью файлов локальной моделью, без агента.")));
        root.AddRow(_allowShell);
        _shellWarning.Margin = new Padding(20, 0, 0, 6);
        root.AddRow(_shellWarning);
        var grid = Kit.Grid();
        grid.AddField(L.T("Таймаут задачи:"), _timeout, L.T("минут"));
        root.AddRow(grid);
        root.AddRow(_global);
        root.AddRow(Kit.Hint(L.T("Провайдер «offload» появится в вашем обычном OpenCode (с резервной копией конфигурации). Для работы через Offload это не обязательно — у него своя управляемая конфигурация.")));
        var saveRow = Kit.Flow(_save, _saveStatus);
        saveRow.Margin = new Padding(0, 10, 0, 4);
        root.AddRow(saveRow);

        Controls.Add(Kit.Scroll(root));

        _enabled.CheckedChanged += (_, _) => MarkDirty();
        _allowShell.CheckedChanged += (_, _) =>
        {
            if (!_loading && _allowShell.Checked &&
                !Ui.Confirm(Owner, L.T("Разрешить агенту OpenCode выполнять команды оболочки?\n\nАгент сможет запускать любые программы в папке проекта. Это удобно для сборки и тестов, но небезопасно для непроверенных задач."), warning: true))
            {
                _loading = true;
                _allowShell.Checked = false;
                _loading = false;
                return;
            }
            MarkDirty();
        };
        _timeout.ValueChanged += (_, _) => MarkDirty();
        _global.CheckedChanged += (_, _) => MarkDirty();

        LoadSettings();
        UpdateStatus();
    }

    public override string Key => Tabs.OpenCode;

    public override string Title => "OpenCode";

    public override string Subtitle => L.T("Агент для многошаговых правок на локальной модели");

    public override string Glyph => Glyphs.Code;

    public override string? BusyDescription => _cts is not null ? L.T("установка OpenCode") : null;

    protected override async void OnActivated()
    {
        try
        {
            if (!_dirty) LoadSettings();
            UpdateStatus();
            if (!_latestChecked)
            {
                _latestChecked = true;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var latest = await OpenCodeInstaller.GetLatestVersionAsync(cts.Token);
                if (!IsDisposed && !string.IsNullOrWhiteSpace(latest)) ShowLatest(latest);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Последняя версия OpenCode: {ex.Message}");
        }
    }

    public override void OnConfigChanged()
    {
        if (!_dirty && !IsBusy) LoadSettings();
        UpdateStatus();
    }

    private string? _latestVersion;

    private void ShowLatest(string latest)
    {
        _latestVersion = latest;
        var installed = ConfigStore.Current.OpenCode.InstalledVersion;
        _latest.Text = installed is not null && NormalizeVersion(installed) != NormalizeVersion(latest)
            ? L.F("Доступна новая версия: {0}", latest)
            : L.F("Последняя версия: {0}", latest);
        _latest.ForeColor = installed is not null && NormalizeVersion(installed) != NormalizeVersion(latest) ? Theme.WarnText : Theme.TextMuted;
    }

    private static string NormalizeVersion(string v) => v.Trim().TrimStart('v', 'V');

    private void UpdateStatus()
    {
        var cfg = ConfigStore.Current;
        var exe = Ui.Try(() => OpenCodeInstaller.FindExecutable(cfg), null, "FindExecutable");
        if (exe is not null)
        {
            var ver = cfg.OpenCode.InstalledVersion;
            _state.Text = string.IsNullOrWhiteSpace(ver) ? L.T("✓ Установлен") : L.F("✓ Установлен, версия {0}", ver);
            _state.ForeColor = Theme.OkText;
            _path.Text = exe;
            _install.Text = L.T("Обновить");
        }
        else
        {
            _state.Text = L.T("Не установлен");
            _state.ForeColor = Theme.WarnText;
            _path.Text = L.T("Установите OpenCode, чтобы локальная модель могла выполнять агентные задачи.");
            _install.Text = L.T("Установить");
        }
        if (_latestVersion is not null) ShowLatest(_latestVersion);
        UpdateUiState();
    }

    protected override void UpdateUiState()
    {
        var installed = Ui.Try(() => OpenCodeInstaller.FindExecutable(ConfigStore.Current), null, "FindExecutable") is not null;
        _install.Enabled = !IsBusy;
        _uninstall.Enabled = !IsBusy && installed;
        _launch.Enabled = !IsBusy && installed;
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var o = ConfigStore.Current.OpenCode;
            _enabled.Checked = o.Enabled;
            _allowShell.Checked = o.AllowShellCommands;
            _timeout.Value = Math.Clamp((int)Math.Round(o.TaskTimeoutSeconds / 60.0), (int)_timeout.Minimum, (int)_timeout.Maximum);
            _global.Checked = o.RegisterInGlobalConfig;
        }
        finally
        {
            _loading = false;
        }
        _dirty = false;
        _saveStatus.Text = "";
    }

    public override bool HasUnsavedChanges => _dirty;

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        _saveStatus.ForeColor = Theme.WarnText;
        _saveStatus.Text = L.T("Есть несохранённые изменения");
    }

    private void Save()
    {
        var before = ConfigStore.Current.OpenCode.RegisterInGlobalConfig;
        var ok = Ui.RunSafe(Owner, () => ConfigStore.Update(c =>
        {
            c.OpenCode.Enabled = _enabled.Checked;
            c.OpenCode.AllowShellCommands = _allowShell.Checked;
            c.OpenCode.TaskTimeoutSeconds = (int)_timeout.Value * 60;
            c.OpenCode.RegisterInGlobalConfig = _global.Checked;
        }), L.T("Не удалось сохранить настройки OpenCode"));
        if (!ok) return;

        var cfg = ConfigStore.Current;
        var messages = new List<string>();
        // Разрешения агента (bash) хранятся в управляемом конфиге — переписываем его.
        try
        {
            if (Ui.Try(() => OpenCodeInstaller.FindExecutable(cfg), null, "FindExecutable") is not null)
                OpenCodeConfigWriter.WriteManagedConfig(cfg);
        }
        catch (Exception ex)
        {
            Log.Warn("opencode", $"Управляемый конфиг OpenCode не обновлён: {ex.Message}");
            messages.Add(L.F("Конфигурация OpenCode не обновлена: {0}", Ui.FriendlyError(ex)));
        }
        if (before != _global.Checked)
        {
            try
            {
                if (_global.Checked) OpenCodeConfigWriter.RegisterGlobal(cfg);
                else OpenCodeConfigWriter.UnregisterGlobal();
                Log.Info("opencode", _global.Checked ? "Offload добавлен в глобальный конфиг OpenCode" : "Offload убран из глобального конфига OpenCode");
            }
            catch (Exception ex)
            {
                Log.Error("opencode", "Глобальный конфиг OpenCode", ex);
                messages.Add(L.F("Глобальный конфиг OpenCode не изменён: {0}", Ui.FriendlyError(ex)));
            }
        }
        _dirty = false;
        if (messages.Count > 0)
        {
            _saveStatus.ForeColor = Theme.ErrorText;
            _saveStatus.Text = L.T("Сохранено с ошибками");
            Ui.Warn(Owner, string.Join(Environment.NewLine + Environment.NewLine, messages));
        }
        else
        {
            _saveStatus.ForeColor = Theme.OkText;
            _saveStatus.Text = L.T("Сохранено");
        }
        Shell.ConfigChanged();
    }

    private async Task InstallAsync()
    {
        using var cts = new CancellationTokenSource();
        _cts = cts;
        _progress.Reset();
        _progress.Start(L.T("Подготовка установки OpenCode…"));
        try
        {
            await RunBusyAsync(async () =>
            {
                try
                {
                    var r = await OpenCodeInstaller.InstallAsync(_progress.CreateProgress(), cts.Token);
                    _progress.Finish(L.F("OpenCode {0} установлен.", r.Version), true);
                    Log.Info("opencode", $"OpenCode {r.Version} установлен: {r.ExecutablePath}");
                    try
                    {
                        OpenCodeConfigWriter.WriteManagedConfig(ConfigStore.Current);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("opencode", $"Управляемый конфиг OpenCode не записан: {ex.Message}");
                    }
                }
                catch (OperationCanceledException)
                {
                    _progress.Finish(L.T("Установка отменена."), false);
                    throw;
                }
                catch (Exception ex)
                {
                    _progress.Finish(L.F("Ошибка установки: {0}", Ui.FriendlyError(ex)), false);
                    throw;
                }
            }, L.T("Не удалось установить OpenCode"));
        }
        finally
        {
            _cts = null;
        }
        Shell.ConfigChanged();
        UpdateStatus();
    }

    private async Task UninstallAsync()
    {
        if (!Ui.Confirm(Owner, L.T("Удалить OpenCode из папки Offload? Агентные задачи станут недоступны до повторной установки."))) return;
        await RunBusyAsync(async () =>
        {
            await Task.Run(OpenCodeInstaller.Uninstall);
            Log.Info("opencode", "OpenCode удалён");
        }, L.T("Не удалось удалить OpenCode"));
        _progress.Reset();
        Shell.ConfigChanged();
        UpdateStatus();
    }
}
