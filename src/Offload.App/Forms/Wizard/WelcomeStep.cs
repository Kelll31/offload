using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;

namespace Offload.App.Forms.Wizard;

/// <summary>Шаг 1: что и куда будет установлено.</summary>
internal sealed class WelcomeStep : WizardStep
{
    /// <summary>Варианты языка: названия языков — на самих языках.</summary>
    private static (string Text, string Value)[] LanguageOptions =>
    [
        ("Русский", L.Russian), // l10n-ignore
        ("English", L.English),
        (L.T("Как в Windows"), L.System),
    ];

    private readonly ComboBox _language = Kit.Combo(220);

    public WelcomeStep(WizardContext ctx) : base(ctx)
    {
        var options = LanguageOptions;
        foreach (var (text, _) in options) _language.Items.Add(text);
        var current = ConfigStore.Current.Ui.Language ?? L.Russian;
        _language.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value == current));
        _language.SelectionChangeCommitted += (_, _) => ChangeLanguage();

        var root = Kit.Table();
        var languageRow = Kit.Grid();
        languageRow.AddField("Язык / Language:", _language); // l10n-ignore
        root.AddRow(languageRow);

        root.AddRow(Kit.Wrap(L.T(
            "Offload запускает на вашем компьютере локальную модель для программирования и подключает её к Claude Code и другим IDE. Облачная модель поручает ей рутинные подзадачи — чтение файлов, сжатие логов, ревью, простые правки — и тратит меньше токенов.")));

        root.AddRow(Kit.Section(L.T("Что будет установлено")));
        var list = Kit.Grid();
        list.AddRow(Bullet(), Kit.Wrap(L.T("llama.cpp — сервер для запуска моделей (сборка под вашу видеокарту).")));
        list.AddRow(Bullet(), Kit.Wrap(L.T("Локальная модель для программирования (файл GGUF, от нескольких до десятков гигабайт).")));
        list.AddRow(Bullet(), Kit.Wrap(L.T("OpenCode — агент, с которым модель выполняет многошаговые задачи (по желанию).")));
        list.AddRow(Bullet(), Kit.Wrap(L.T("Подключение Offload к найденным IDE как MCP-сервера.")));
        root.AddRow(list);

        root.AddRow(Kit.Section(L.T("Куда")));
        root.AddRow(Kit.Wrap(L.F("Все файлы хранятся в папке пользователя:{0}{1}", Environment.NewLine, AppPaths.DataDir)));
        root.AddRow(Kit.Hint(L.T("Папку для моделей можно будет выбрать на шаге «Модель» — например, на другом диске с большим объёмом.")));

        root.AddRow(Kit.Section(L.T("Права администратора")));
        root.AddRow(Kit.Wrap(L.T(
            "Не нужны. Исключение — Microsoft Visual C++ Redistributable: если его нет в системе, Windows попросит подтвердить установку.")));
        root.AddRow(Kit.Hint(L.T(
            "Загрузка может занять от нескольких минут до часа — зависит от размера модели и скорости интернета. Прерванная загрузка продолжится с того же места.")));
        SetContent(root);
    }

    public override string Title => L.T("Приветствие");

    public override string Heading => L.T("Добро пожаловать в Offload");

    public override string? Subtitle => L.T("Мастер установит всё необходимое для работы локальной модели.");

    public override bool CanGoBack => false;

    /// <summary>Сохранить выбранный язык и открыть мастер заново уже на нём (вместе с главным окном и меню трея).</summary>
    private void ChangeLanguage()
    {
        var index = _language.SelectedIndex;
        var options = LanguageOptions;
        if (index < 0 || index >= options.Length) return;
        var value = options[index].Value;
        if (value == (ConfigStore.Current.Ui.Language ?? L.Russian)) return;
        // Главное окно пересоздаётся на новом языке — несохранённые правки в нём только с согласия.
        if (L.Resolve(value) != L.Language && !Ctx.Shell.ConfirmWindowRecreate(Ctx.Form))
        {
            _language.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value == (ConfigStore.Current.Ui.Language ?? L.Russian)));
            return;
        }
        if (!Ui.RunSafe(Ctx.Form, () => ConfigStore.Update(c => c.Ui.Language = value), L.T("Не удалось сохранить язык интерфейса"))) return;
        var before = L.Language;
        Program.ApplyLanguage(value);
        if (L.Language != before && Ctx.Form is SetupWizardForm wizard) wizard.Reopen();
    }

    private static Label Bullet()
    {
        var l = Kit.Label("•", Theme.Semibold(10f), Theme.Accent);
        l.Margin = new Padding(0, 1, 6, 4);
        l.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        return l;
    }
}
