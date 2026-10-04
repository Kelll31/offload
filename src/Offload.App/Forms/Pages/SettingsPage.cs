using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Localization;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Security;
using Offload.Models;

namespace Offload.App.Forms.Pages;

/// <summary>
/// Раздел «Настройки»: мастер настройки, внешний вид (тема, цветовая схема, свой акцент), язык, папка моделей,
/// запуск и уведомления, сеть (токен Hugging Face, зеркала, прокси, удалённый каталог моделей), папка данных.
/// Внешний вид и язык применяются сразу — окно пересоздаётся.
/// </summary>
internal sealed class SettingsPage : PageBase
{
    private static (string Text, string Value)[] ThemeOptions =>
    [
        (L.T("Как в Windows"), Theme.ModeSystem),
        (L.T("Светлая"), Theme.ModeLight),
        (L.T("Тёмная"), Theme.ModeDark),
    ];

    private static (string Text, string Value)[] LanguageOptions =>
    [
        ("Русский", L.Russian), // l10n-ignore — название языка не переводится
        ("English", L.English),
        (L.T("Как в Windows"), L.System),
    ];

    private readonly ComboBox _theme = Kit.Combo(220);
    private readonly ComboBox _preset = Kit.Combo(260);
    private readonly Panel _swatch = new()
    {
        Size = new Size(28, 22),
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 4, 8, 0),
    };
    private readonly Button _pickColor;
    private readonly ComboBox _language = Kit.Combo(220);
    private readonly Label _modelsDir = Kit.Wrap("");
    private readonly Label _dataDir = Kit.Wrap("");
    private readonly CheckBox _startWithWindows = Kit.Check(L.T("Запускать Offload вместе с Windows"));
    private readonly CheckBox _notifications = Kit.Check(L.T("Показывать всплывающие уведомления"));
    private readonly CheckBox _minimizeToTray = Kit.Check(L.T("При закрытии окна оставлять Offload в области уведомлений"));
    private readonly CheckBox _checkAppUpdates = Kit.Check(L.T("Проверять обновления Offload при запуске"));
    private readonly CheckBox _verboseLog = Kit.Check(L.T("Подробный журнал (отладочные записи)"));
    private readonly CheckBox _liveHubSearch = Kit.Check(L.T("Искать на Hugging Face по мере набора названия (текст запроса уходит на huggingface.co)"));
    private readonly CheckBox _watchHardware = Kit.Check(L.T("Следить за оборудованием и подбирать лучшую модель и параметры"));
    private readonly CheckBox _quietHours = Kit.Check(L.T("Тихие часы: не показывать всплывающие уведомления"));
    private readonly DateTimePicker _quietFrom = TimePicker();
    private readonly DateTimePicker _quietTo = TimePicker();
    private readonly CheckBox _hotkey = Kit.Check(L.F("Открывать Offload сочетанием {0} из любой программы", GlobalHotkey.Display));
    private readonly Label _logLevelHint = Kit.Hint("");

    // Сеть
    private readonly TextBox _hfToken = new() { UseSystemPasswordChar = true, Width = 300, Margin = new Padding(0, 3, 8, 3) };
    private readonly Label _hfTokenStatus = Kit.Hint("");
    private readonly CheckBox _sendTokenToMirror = Kit.Check(L.T("Передавать токен и зеркалу Hugging Face (иначе — только huggingface.co)"));
    private readonly TextBox _hfMirror = UrlBox();
    private readonly TextBox _gitHubApiMirror = UrlBox();
    private readonly TextBox _gitHubMirror = UrlBox();
    private readonly ComboBox _proxyMode = Kit.Combo(220);
    private readonly TextBox _proxyUrl = UrlBox();
    private readonly CheckBox _remoteCatalog = Kit.Check(L.T("Обновлять каталог моделей из репозитория Offload (с проверкой подписи, не чаще раза в сутки)"));
    private readonly Label _catalogStatus = Kit.Hint("");
    private readonly Button _checkCatalog;
    private readonly RemoteServerSection _remote;
    private bool _loading;

    private static (string Text, string Value)[] ProxyOptions =>
    [
        (L.T("Как в Windows"), NetworkOptions.ProxySystem),
        (L.T("Без прокси"), NetworkOptions.ProxyNone),
        (L.T("Свой прокси"), NetworkOptions.ProxyCustom),
    ];

    public SettingsPage(IAppShell shell) : base(shell)
    {
        _pickColor = Kit.Button(L.T("Выбрать цвет…"), (_, _) => PickColor(), 130);
        _checkCatalog = Kit.Button(L.T("Проверить сейчас"), (_, _) => _ = CheckCatalogAsync(), 140);
        _remote = new RemoteServerSection(shell, this, RunBusyAsync);
        var root = Kit.Table();

        root.AddRow(Kit.Section(L.T("Мастер настройки"), first: true));
        root.AddRow(Kit.Wrap(L.T("Пошаговая настройка: подобрать сборку llama.cpp и модель под ваше железо, переустановить компоненты, подключить IDE и OpenCode.")));
        var wizard = Kit.Primary(L.T("Открыть мастер настройки"), (_, _) => Shell.ShowSetupWizard(), 200);
        wizard.Margin = new Padding(0, 6, 0, 4);
        root.AddRow(wizard);

        root.AddRow(Kit.Section(L.T("Внешний вид")));
        var look = Kit.Grid();
        look.AddField(L.T("Тема:"), _theme, L.T("переключается и кнопкой внизу боковой панели"));
        look.AddField(L.T("Цветовая схема:"), _preset);
        look.AddField(L.T("Свой цвет:"), Kit.Flow(_swatch, _pickColor), L.T("акцент кнопок, выделения и графиков"));
        root.AddRow(look);
        root.AddRow(Kit.Hint(L.T("Если в Windows включён режим высокой контрастности, а тема — «Как в Windows», используется схема «Высокий контраст».")));

        root.AddRow(Kit.Section("Язык / Language")); // l10n-ignore — двуязычный заголовок
        var lang = Kit.Grid();
        lang.AddField(L.T("Язык интерфейса:"), _language, L.T("меняется сразу, без перезапуска"));
        root.AddRow(lang);

        root.AddRow(Kit.Section(L.T("Модели")));
        var models = Kit.Grid();
        models.AddField(L.T("Папка для моделей:"), _modelsDir);
        root.AddRow(models);
        root.AddRow(Kit.Flow(
            Kit.Button(L.T("Изменить…"), (_, _) =>
            {
                if (ModelsFolder.Change(Owner, Shell)) UpdatePaths();
            }, 110),
            Kit.Button(L.T("Открыть папку"), (_, _) => Ui.OpenFolder(ModelsFolder.Current()), 120)));
        root.AddRow(Kit.Hint(L.T("Уже скачанные модели при смене папки не перемещаются и продолжают работать.")));
        root.AddRow(_liveHubSearch);
        root.AddRow(_watchHardware);
        root.AddRow(Kit.Hint(L.T("Подбор ничего не скачивает и не меняет сам: Offload только сообщает, что подойдёт лучше, а решаете вы на вкладке «Модели».")));

        root.AddRow(Kit.Section(L.T("Запуск и уведомления")));
        root.AddRow(_startWithWindows);
        root.AddRow(_notifications);
        root.AddRow(_minimizeToTray);
        root.AddRow(_checkAppUpdates);
        root.AddRow(_hotkey);
        root.AddRow(_quietHours);
        var quiet = Kit.Flow(Kit.Label(L.T("с"), color: Theme.TextMuted), _quietFrom, Kit.Label(L.T("до"), color: Theme.TextMuted), _quietTo,
            Kit.Hint(L.T("пропущенные уведомления остаются в разделе «Уведомления»"), autoWidth: true));
        quiet.WrapContents = false;
        quiet.Margin = new Padding(48, 0, 0, 6);
        root.AddRow(quiet);

        root.AddRow(Kit.Section(L.T("Перенос настроек")));
        root.AddRow(Kit.Hint(L.T("Файл с внешним видом, параметрами сервера и инструментов — чтобы перенести их на другой компьютер. Ключи и токены, пути, подключения к IDE и параметры безопасности не сохраняются и при импорте не меняются.")));
        root.AddRow(Kit.Flow(
            Kit.IconButton(Glyphs.Save, L.T("Экспорт настроек…"), (_, _) => ExportSettings(), 170),
            Kit.IconButton(Glyphs.OpenFile, L.T("Импорт настроек…"), (_, _) => ImportSettings(), 170)));

        BuildNetworkSection(root);
        _remote.AddTo(root);

        root.AddRow(Kit.Section(L.T("Данные программы")));
        var data = Kit.Grid();
        data.AddField(L.T("Папка данных:"), _dataDir, L.T("настройки, журналы, llama.cpp, OpenCode, снимки для отката"));
        root.AddRow(data);
        root.AddRow(Kit.Flow(
            Kit.Button(L.T("Открыть папку данных"), (_, _) => Ui.OpenFolder(AppPaths.DataDir), 170),
            Kit.Button(L.T("Открыть config.json"), (_, _) => Ui.OpenInNotepad(AppPaths.ConfigFile), 150)));
        root.AddRow(Kit.Hint(L.T("Ручные правки config.json применяются автоматически; файл с ошибкой не применяется — остаются прежние настройки.")));

        root.AddRow(Kit.Section(L.T("Журнал")));
        root.AddRow(_verboseLog);
        root.AddRow(_logLevelHint);

        Controls.Add(Kit.Scroll(root));

        foreach (var (text, _) in ThemeOptions) _theme.Items.Add(text);
        foreach (var p in Theme.Presets) _preset.Items.Add(L.T(p.Name));
        _preset.Items.Add(L.T("Свой цвет"));
        foreach (var (text, _) in LanguageOptions) _language.Items.Add(text);
        foreach (var (text, _) in ProxyOptions) _proxyMode.Items.Add(text);

        // SelectionChangeCommitted — только явный выбор (не колесо мыши над закрытым списком).
        _theme.SelectionChangeCommitted += (_, _) => ChangeAppearance(c => c.Ui.Theme = ThemeOptions[_theme.SelectedIndex].Value);
        _preset.SelectionChangeCommitted += (_, _) => ChangePreset();
        _language.SelectionChangeCommitted += (_, _) => ChangeAppearance(c => c.Ui.Language = LanguageOptions[_language.SelectedIndex].Value);
        _startWithWindows.CheckedChanged += (_, _) =>
        {
            if (!_loading) Shell.SetAutostart(_startWithWindows.Checked, Owner);
        };
        _notifications.CheckedChanged += (_, _) => Save(c => c.Ui.ShowNotifications = _notifications.Checked);
        _minimizeToTray.CheckedChanged += (_, _) => Save(c => c.Ui.MinimizeToTrayOnClose = _minimizeToTray.Checked);
        _checkAppUpdates.CheckedChanged += (_, _) => Save(c => c.Ui.CheckAppUpdates = _checkAppUpdates.Checked);
        _liveHubSearch.CheckedChanged += (_, _) => Save(c => c.Ui.LiveHubSearch = _liveHubSearch.Checked);
        _watchHardware.CheckedChanged += (_, _) => Save(c => c.Models.WatchHardware = _watchHardware.Checked);
        _hotkey.CheckedChanged += (_, _) => Save(c => c.Ui.GlobalHotkey = _hotkey.Checked);
        _quietHours.CheckedChanged += (_, _) =>
        {
            _quietFrom.Enabled = _quietTo.Enabled = _quietHours.Checked;
            Save(c => c.Ui.QuietHoursEnabled = _quietHours.Checked);
        };
        _quietFrom.ValueChanged += (_, _) => Save(c => c.Ui.QuietHoursFrom = _quietFrom.Value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        _quietTo.ValueChanged += (_, _) => Save(c => c.Ui.QuietHoursTo = _quietTo.Value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        _sendTokenToMirror.CheckedChanged += (_, _) => Save(c => Net(c).SendHfTokenToMirror = _sendTokenToMirror.Checked);
        _remoteCatalog.CheckedChanged += (_, _) =>
        {
            Save(c => Net(c).RemoteCatalog = _remoteCatalog.Checked);
            if (!_loading) UpdateCatalogStatus();
        };
        _proxyMode.SelectionChangeCommitted += (_, _) => UpdateProxyState();
        _verboseLog.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            Save(c => c.Ui.VerboseLog = _verboseLog.Checked);
            // Сразу в этом процессе; MCP-серверы IDE применят настройку при следующем запуске.
            Log.ApplyLevel(_verboseLog.Checked);
            UpdateLogHint();
        };

        LoadSettings();
    }

    public override string Key => Tabs.Settings;
    public override string Title => L.T("Настройки");
    public override string? Subtitle => L.T("Мастер настройки, тема и цвета, язык, папка моделей, запуск программы, сеть и удалённый сервер");
    public override string Glyph => Glyphs.Settings;

    /// <summary>Поля сети изменены, но не сохранены кнопкой «Сохранить сетевые настройки» (или введён токен).</summary>
    public override bool HasUnsavedChanges
    {
        get
        {
            var n = ConfigStore.Current.Network ?? new NetworkSettings();
            return _hfToken.TextLength > 0
                   || _remote.HasUnsavedChanges
                   || !SameUrl(_hfMirror.Text, n.HfMirror)
                   || !SameUrl(_gitHubApiMirror.Text, n.GitHubApiMirror)
                   || !SameUrl(_gitHubMirror.Text, n.GitHubMirror)
                   || SelectedProxyMode() != NormalizeProxyMode(n.ProxyMode)
                   || !SameUrl(_proxyUrl.Text, n.ProxyUrl);
        }
    }

    public override void OnConfigChanged() => LoadSettings();

    protected override void OnActivated() => LoadSettings();

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var ui = ConfigStore.Current.Ui;
            _theme.SelectedIndex = Math.Max(0, Array.FindIndex(ThemeOptions, o => o.Value == ui.Theme));
            var presetIndex = ui.ThemePreset == Theme.PresetCustom ? Theme.Presets.Count : Theme.Presets.ToList().FindIndex(p => p.Key == ui.ThemePreset);
            _preset.SelectedIndex = Math.Max(0, presetIndex);
            _language.SelectedIndex = Math.Max(0, Array.FindIndex(LanguageOptions, o => o.Value == (ui.Language ?? L.Russian)));
            // Образец цвета показывает текущий акцент (свой или из схемы).
            _swatch.BackColor = ui.ThemePreset == Theme.PresetCustom && Theme.TryParseColor(ui.AccentColor, out var c) ? c : Theme.Accent;
            _startWithWindows.Checked = Autostart.IsEnabled;
            _notifications.Checked = ui.ShowNotifications;
            _minimizeToTray.Checked = ui.MinimizeToTrayOnClose;
            _checkAppUpdates.Checked = ui.CheckAppUpdates;
            _liveHubSearch.Checked = ui.LiveHubSearch;
            _watchHardware.Checked = ConfigStore.Current.Models.WatchHardware;
            _hotkey.Checked = ui.GlobalHotkey;
            _quietHours.Checked = ui.QuietHoursEnabled;
            _quietFrom.Value = TimeValue(ui.QuietHoursFrom, 22);
            _quietTo.Value = TimeValue(ui.QuietHoursTo, 8);
            _quietFrom.Enabled = _quietTo.Enabled = ui.QuietHoursEnabled;
            _verboseLog.Checked = ui.VerboseLog;
            UpdateLogHint();
            UpdatePaths();
            LoadNetwork();
            _remote.Load();
        }
        finally
        {
            _loading = false;
        }
    }

    public override void OnServerStateChanged() => _remote.OnServerStateChanged();

    private void UpdateLogHint()
    {
        var env = Environment.GetEnvironmentVariable(Log.LevelEnvVar);
        _logLevelHint.Text = Log.ParseLevel(env) is null
            ? L.T("Отладочные записи помогают разобраться в сбоях, но журнал растёт быстрее. MCP-серверы в IDE применят настройку после перезапуска IDE.")
            : L.F("Уровень журнала задан переменной окружения {0} = {1} — она важнее этой настройки.", Log.LevelEnvVar, env!.Trim());
    }

    private void UpdatePaths()
    {
        _modelsDir.Text = ModelsFolder.Current();
        _dataDir.Text = AppPaths.DataDir;
    }

    // ── Сеть ─────────────────────────────────────────────────────────────────────

    private void BuildNetworkSection(TableLayoutPanel root)
    {
        root.AddRow(Kit.Section(L.T("Сеть")));
        var grid = Kit.Grid();
        var tokenRow = Kit.Flow(
            _hfToken,
            Kit.Button(L.T("Сохранить токен"), (_, _) => SaveToken(), 140),
            Kit.Button(L.T("Удалить"), (_, _) => ClearToken(), 90));
        // Без переноса: иначе предварительный расчёт высоты оставляет пустоту под строкой (как в Kit.AddField).
        tokenRow.WrapContents = false;
        tokenRow.Margin = Padding.Empty;
        grid.AddField(L.T("Токен Hugging Face:"), tokenRow);
        root.AddRow(grid);
        root.AddRow(_hfTokenStatus);
        root.AddRow(_sendTokenToMirror);

        var mirrors = Kit.Grid();
        mirrors.AddField(L.T("Зеркало Hugging Face:"), _hfMirror, L.T("вместо https://huggingface.co; пусто — без зеркала"));
        mirrors.AddField(L.T("Зеркало GitHub API:"), _gitHubApiMirror, L.T("вместо https://api.github.com"));
        mirrors.AddField(L.T("Зеркало github.com:"), _gitHubMirror, L.T("к адресу дописывается путь файла релиза"));
        mirrors.AddField(L.T("Прокси:"), _proxyMode);
        mirrors.AddField(L.T("Адрес прокси:"), _proxyUrl, L.T("http(s):// или socks5://; логин — user:pass@хост"));
        root.AddRow(mirrors);
        var save = Kit.Primary(L.T("Сохранить сетевые настройки"), (_, _) => SaveNetwork(), 220);
        save.Margin = new Padding(0, 6, 0, 4);
        root.AddRow(save);
        root.AddRow(Kit.Hint(L.T("Зеркало GitHub API должно быть доверенным: оно выбирает, какие сборки llama.cpp и OpenCode ставить, и сообщает их контрольные суммы (файлы при этом берутся только из github.com/…/releases/download/ или через зеркало github.com). Модели проверяются по SHA-256 из каталога. Прокси применяется сразу, к новым соединениям.")));

        root.AddRow(_remoteCatalog);
        root.AddRow(Kit.Flow(_checkCatalog));
        root.AddRow(_catalogStatus);
    }

    private static TextBox UrlBox() => new() { Width = 320, Margin = new Padding(0, 3, 8, 3) };

    private static NetworkSettings Net(AppConfig c) => c.Network ??= new NetworkSettings();

    private static bool SameUrl(string? a, string? b) =>
        string.Equals(NetworkOptions.NormalizeBase(a), NetworkOptions.NormalizeBase(b), StringComparison.Ordinal);

    private static string NormalizeProxyMode(string? mode)
    {
        var m = mode?.Trim().ToLowerInvariant();
        return ProxyOptions.Any(o => o.Value == m) ? m! : NetworkOptions.ProxySystem;
    }

    private string SelectedProxyMode() =>
        _proxyMode.SelectedIndex >= 0 ? ProxyOptions[_proxyMode.SelectedIndex].Value : NetworkOptions.ProxySystem;

    private void LoadNetwork()
    {
        var n = ConfigStore.Current.Network ?? new NetworkSettings();
        _hfToken.Clear();
        _sendTokenToMirror.Checked = n.SendHfTokenToMirror;
        _hfMirror.Text = n.HfMirror ?? "";
        _gitHubApiMirror.Text = n.GitHubApiMirror ?? "";
        _gitHubMirror.Text = n.GitHubMirror ?? "";
        _proxyMode.SelectedIndex = Math.Max(0, Array.FindIndex(ProxyOptions, o => o.Value == NormalizeProxyMode(n.ProxyMode)));
        _proxyUrl.Text = n.ProxyUrl ?? "";
        _remoteCatalog.Checked = n.RemoteCatalog;
        UpdateProxyState();
        UpdateTokenStatus();
        UpdateCatalogStatus();
    }

    private void UpdateProxyState() => _proxyUrl.Enabled = SelectedProxyMode() == NetworkOptions.ProxyCustom;

    private void UpdateTokenStatus()
    {
        var text = (NetworkOptions.HasStoredHfToken ? L.T("Токен сохранён.") : L.T("Токен не задан — доступны только открытые модели.")) + " " + L.T("Токен нужен для закрытых моделей и хранится зашифрованным для вашей учётной записи Windows.");
        if (NetworkOptions.HfTokenFromEnvironment)
            text += " " + L.F("Задана переменная окружения {0} — она важнее сохранённого токена.", NetworkOptions.HfTokenEnvVar);
        _hfTokenStatus.Text = text;
    }

    private void SaveToken()
    {
        var token = _hfToken.Text.Trim();
        if (token.Length == 0 || token.Any(char.IsWhiteSpace))
        {
            Ui.Warn(Owner, L.T("Вставьте токен Hugging Face (Settings → Access Tokens на huggingface.co) без пробелов."));
            return;
        }
        if (Ui.RunSafe(Owner, () =>
            {
                var protectedToken = Dpapi.Protect(token);
                ConfigStore.Update(c => Net(c).HfTokenProtected = protectedToken);
            }, L.T("Не удалось сохранить настройку")))
        {
            _hfToken.Clear();
            Log.Info("settings", "Токен Hugging Face сохранён (DPAPI).");
        }
        UpdateTokenStatus();
    }

    private void ClearToken()
    {
        _hfToken.Clear();
        if (!NetworkOptions.HasStoredHfToken) return;
        if (!Ui.Confirm(Owner, L.T("Удалить сохранённый токен Hugging Face?"))) return;
        Save(c => Net(c).HfTokenProtected = null);
        UpdateTokenStatus();
    }

    private void SaveNetwork()
    {
        var mode = SelectedProxyMode();
        var errors = new[] { _hfMirror.Text, _gitHubApiMirror.Text, _gitHubMirror.Text }
            .Select(NetworkOptions.ValidateMirror)
            .Append(mode == NetworkOptions.ProxyCustom ? NetworkOptions.ValidateProxy(_proxyUrl.Text) : null)
            .OfType<string>()
            .ToList();
        if (errors.Count > 0)
        {
            Ui.Warn(Owner, string.Join(Environment.NewLine, errors));
            return;
        }
        var hf = NetworkOptions.NormalizeBase(_hfMirror.Text);
        var api = NetworkOptions.NormalizeBase(_gitHubApiMirror.Text);
        var gh = NetworkOptions.NormalizeBase(_gitHubMirror.Text);
        var proxy = NetworkOptions.NormalizeBase(_proxyUrl.Text);
        if (Ui.RunSafe(Owner, () => ConfigStore.Update(c =>
            {
                var n = Net(c);
                n.HfMirror = hf;
                n.GitHubApiMirror = api;
                n.GitHubMirror = gh;
                n.ProxyMode = mode;
                n.ProxyUrl = proxy;
            }), L.T("Не удалось сохранить настройку")))
        {
            Log.Info("settings", $"Сеть: прокси {mode}, зеркала HF={hf ?? "—"}, GitHub API={api ?? "—"}, github.com={gh ?? "—"}");
            _loading = true;
            try
            {
                LoadNetwork();
            }
            finally
            {
                _loading = false;
            }
        }
    }

    private void UpdateCatalogStatus()
    {
        _remoteCatalog.Enabled = RemoteCatalog.IsAvailable;
        _checkCatalog.Enabled = RemoteCatalog.IsAvailable && _remoteCatalog.Checked;
        if (!RemoteCatalog.IsAvailable)
        {
            _catalogStatus.Text = L.T("В этой сборке удалённый каталог недоступен (не задан ключ подписи) — используется встроенный.");
            return;
        }
        var current = ModelCatalog.Current;
        var text = ModelCatalog.IsRemote
            ? L.F("Действует каталог версии {0}, обновлённый из репозитория.", current.Version)
            : L.F("Действует встроенный каталог версии {0}.", current.Version);
        if (RemoteCatalog.LastCheck is { } last)
        {
            text += " " + L.F("Последняя проверка: {0}.", last.LastCheckUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
            if (!string.IsNullOrEmpty(last.LastError)) text += " " + L.F("Ошибка: {0}", last.LastError);
        }
        _catalogStatus.Text = text;
    }

    private async Task CheckCatalogAsync()
    {
        CatalogUpdateResult? result = null;
        await RunBusyAsync(async () => result = await RemoteCatalog.RefreshAsync(force: true), L.T("Не удалось проверить каталог моделей"), _checkCatalog);
        if (IsDisposed) return;
        UpdateCatalogStatus();
        switch (result?.Status)
        {
            case CatalogUpdateStatus.Updated:
                (FindForm() as MainForm)?.NotifyConfigChanged();
                Ui.Info(Owner, L.F("Каталог моделей обновлён до версии {0}.", result.Version));
                break;
            case CatalogUpdateStatus.UpToDate:
                Ui.Info(Owner, L.T("Каталог моделей актуален."));
                break;
            case CatalogUpdateStatus.Failed:
                Ui.Warn(Owner, L.F("Не удалось обновить каталог моделей: {0}", result.Error));
                break;
        }
    }

    private void ChangePreset()
    {
        if (_loading || _preset.SelectedIndex < 0) return;
        if (_preset.SelectedIndex >= Theme.Presets.Count)
        {
            // «Свой цвет»: сразу предложить выбрать цвет.
            PickColor();
            return;
        }
        var key = Theme.Presets[_preset.SelectedIndex].Key;
        ChangeAppearance(c => c.Ui.ThemePreset = key);
    }

    private void PickColor()
    {
        var ui = ConfigStore.Current.Ui;
        using var dlg = new ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = Theme.TryParseColor(ui.AccentColor, out var current) ? current : Theme.Accent,
        };
        if (dlg.ShowDialog(Owner) != DialogResult.OK)
        {
            LoadSettings();
            return;
        }
        var hex = Theme.ToHex(dlg.Color);
        ChangeAppearance(c =>
        {
            c.Ui.ThemePreset = Theme.PresetCustom;
            c.Ui.AccentColor = hex;
        });
    }

    /// <summary>Сохранить настройку внешнего вида/языка и пересоздать окно (с вопросом о несохранённых правках).</summary>
    private void ChangeAppearance(Action<AppConfig> mutate)
    {
        if (_loading) return;
        if (FindForm() is MainForm main)
        {
            if (!main.RequestAppearance(mutate)) LoadSettings();
            return;
        }
        if (Ui.RunSafe(Owner, () => ConfigStore.Update(mutate), L.T("Не удалось сохранить настройку")))
            Shell.PostToUi(Shell.ApplyAppearance);
    }

    private static DateTimePicker TimePicker() => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
        Width = 80,
        Margin = new Padding(0, 3, 8, 3),
        Anchor = AnchorStyles.Left,
    };

    private static DateTime TimeValue(string? text, int fallbackHour) =>
        DateTime.Today + (Offload.Core.Notifications.QuietHours.TryParse(text, out var t) ? t : TimeSpan.FromHours(fallbackHour));

    /// <summary>Сохранить разрешённые настройки в файл (без ключей, путей и параметров безопасности).</summary>
    private void ExportSettings()
    {
        using var dlg = new SaveFileDialog
        {
            Title = L.T("Экспорт настроек Offload"),
            Filter = L.T("Настройки Offload (*.json)|*.json"),
            FileName = $"offload-settings-{DateTime.Now:yyyy-MM-dd}.json",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        if (Ui.RunSafe(Owner, () => File.WriteAllText(dlg.FileName, SettingsTransfer.Export(ConfigStore.Current, DateTime.UtcNow)),
                L.T("Не удалось сохранить файл настроек")))
            Shell.Notify(L.T("Настройки сохранены"), dlg.FileName, force: true);
    }

    /// <summary>Загрузить настройки из файла: показать, что изменится, и применить после подтверждения.</summary>
    private void ImportSettings()
    {
        using var dlg = new OpenFileDialog
        {
            Title = L.T("Импорт настроек Offload"),
            Filter = L.T("Настройки Offload (*.json)|*.json|Все файлы (*.*)|*.*"),
        };
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        SettingsImport import;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 1024 * 1024) throw new SettingsTransferException(L.T("файл слишком большой для файла настроек"));
            import = SettingsTransfer.Prepare(File.ReadAllText(dlg.FileName), ConfigStore.Current);
        }
        catch (Exception ex) when (ex is SettingsTransferException or IOException or UnauthorizedAccessException)
        {
            Ui.ShowError(Owner, L.T("Файл настроек не загружен"), ex.Message);
            return;
        }
        if (import.Changes.Count == 0)
        {
            Ui.Info(Owner, L.T("Настройки в файле совпадают с текущими — менять нечего."));
            return;
        }
        var list = string.Join(Environment.NewLine, import.Changes.Take(25).Select(c => "• " + c)) +
                   (import.Changes.Count > 25 ? Environment.NewLine + L.F("…и ещё {0}", import.Changes.Count - 25) : "");
        var skipped = import.Skipped.Count > 0 ? Environment.NewLine + Environment.NewLine + L.F("Не переносятся (ключи, пути, безопасность): {0}", import.Skipped.Count) : "";
        if (!Ui.Confirm(Owner, L.F("Будут изменены настройки ({0}):{1}{2}{3}{1}{1}Применить?", import.Changes.Count, Environment.NewLine, list, skipped)))
            return;
        var result = import.Result;
        var appearance = import.Changes.Any(c => c is "ui.theme" or "ui.themePreset" or "ui.accentColor" or "ui.language");
        void Apply(AppConfig c) => SettingsTransfer.Apply(result, c);
        if (appearance) ChangeAppearance(Apply);
        else if (Ui.RunSafe(Owner, () => ConfigStore.Update(Apply), L.T("Не удалось сохранить настройки")))
        {
            Shell.ConfigChanged();
            Shell.Notify(L.T("Настройки загружены"), L.F("Изменено параметров: {0}.", import.Changes.Count), force: true);
        }
    }

    private void Save(Action<AppConfig> mutate)
    {
        if (_loading) return;
        Ui.RunSafe(Owner, () => ConfigStore.Update(mutate), L.T("Не удалось сохранить настройку"));
    }
}
