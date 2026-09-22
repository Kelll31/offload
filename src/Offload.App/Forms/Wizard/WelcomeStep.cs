using Offload.App.Util;
using Offload.Core;

namespace Offload.App.Forms.Wizard;

/// <summary>Шаг 1: что и куда будет установлено.</summary>
internal sealed class WelcomeStep : WizardStep
{
    public WelcomeStep(WizardContext ctx) : base(ctx)
    {
        var root = Kit.Table();
        root.AddRow(Kit.Wrap(
            "Offload запускает на вашем компьютере локальную модель для программирования и подключает её к Claude Code " +
            "и другим IDE. Облачная модель поручает ей рутинные подзадачи — чтение файлов, сжатие логов, ревью, простые правки — " +
            "и тратит меньше токенов."));

        root.AddRow(Kit.Section("Что будет установлено"));
        var list = Kit.Grid();
        list.AddRow(Bullet(), Kit.Wrap("llama.cpp — сервер для запуска моделей (сборка под вашу видеокарту)."));
        list.AddRow(Bullet(), Kit.Wrap("Локальная модель для программирования (файл GGUF, от нескольких до десятков гигабайт)."));
        list.AddRow(Bullet(), Kit.Wrap("OpenCode — агент, с которым модель выполняет многошаговые задачи (по желанию)."));
        list.AddRow(Bullet(), Kit.Wrap("Подключение Offload к найденным IDE как MCP-сервера."));
        root.AddRow(list);

        root.AddRow(Kit.Section("Куда"));
        root.AddRow(Kit.Wrap($"Все файлы хранятся в папке пользователя:{Environment.NewLine}{AppPaths.DataDir}"));
        root.AddRow(Kit.Hint("Папку для моделей можно будет выбрать на шаге «Модель» — например, на другом диске с большим объёмом."));

        root.AddRow(Kit.Section("Права администратора"));
        root.AddRow(Kit.Wrap(
            "Не нужны. Исключение — Microsoft Visual C++ Redistributable: если его нет в системе, Windows попросит подтвердить установку."));
        root.AddRow(Kit.Hint("Загрузка может занять от нескольких минут до часа — зависит от размера модели и скорости интернета. " +
                             "Прерванная загрузка продолжится с того же места."));
        SetContent(root);
    }

    public override string Title => "Приветствие";

    public override string Heading => "Добро пожаловать в Offload";

    public override string? Subtitle => "Мастер установит всё необходимое для работы локальной модели.";

    public override bool CanGoBack => false;

    private static Label Bullet()
    {
        var l = Kit.Label("•", Theme.Semibold(10f), Theme.Accent);
        l.Margin = new Padding(0, 1, 6, 4);
        l.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        return l;
    }
}
