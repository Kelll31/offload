using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Localization;

namespace Offload.App.Forms.Pages;

/// <summary>
/// Раздел «Настройки»: мастер настройки, внешний вид (тема, цветовая схема, свой акцент), язык, папка моделей,
/// запуск и уведомления, папка данных. Внешний вид и язык применяются сразу — окно пересоздаётся.
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
    private bool _loading;

    public SettingsPage(IAppShell shell) : base(shell)
    {
        _pickColor = Kit.Button(L.T("Выбрать цвет…"), (_, _) => PickColor(), 130);
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

        root.AddRow(Kit.Section(L.T("Запуск и уведомления")));
        root.AddRow(_startWithWindows);
        root.AddRow(_notifications);
        root.AddRow(_minimizeToTray);

        root.AddRow(Kit.Section(L.T("Данные программы")));
        var data = Kit.Grid();
        data.AddField(L.T("Папка данных:"), _dataDir, L.T("настройки, журналы, llama.cpp, OpenCode, снимки для отката"));
        root.AddRow(data);
        root.AddRow(Kit.Flow(
            Kit.Button(L.T("Открыть папку данных"), (_, _) => Ui.OpenFolder(AppPaths.DataDir), 170),
            Kit.Button(L.T("Открыть config.json"), (_, _) => Ui.OpenInNotepad(AppPaths.ConfigFile), 150)));

        Controls.Add(Kit.Scroll(root));

        foreach (var (text, _) in ThemeOptions) _theme.Items.Add(text);
        foreach (var p in Theme.Presets) _preset.Items.Add(L.T(p.Name));
        _preset.Items.Add(L.T("Свой цвет"));
        foreach (var (text, _) in LanguageOptions) _language.Items.Add(text);

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

        LoadSettings();
    }

    public override string Key => Tabs.Settings;
    public override string Title => L.T("Настройки");
    public override string? Subtitle => L.T("Мастер настройки, тема и цвета, язык, папка моделей, запуск программы");
    public override string Glyph => Glyphs.Settings;

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
            UpdatePaths();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdatePaths()
    {
        _modelsDir.Text = ModelsFolder.Current();
        _dataDir.Text = AppPaths.DataDir;
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

    private void Save(Action<AppConfig> mutate)
    {
        if (_loading) return;
        Ui.RunSafe(Owner, () => ConfigStore.Update(mutate), L.T("Не удалось сохранить настройку"));
    }
}
