using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «О программе».</summary>
internal sealed class AboutPage : PageBase
{
    public AboutPage(IAppShell shell) : base(shell)
    {
        var root = Kit.Table();

        var logo = new PictureBox
        {
            Image = AppIcons.Logo,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(64, 64),
            Margin = new Padding(0, 0, 16, 0),
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            BackColor = Color.Transparent,
        };
        var title = Kit.Table(0, 100);
        var names = Kit.Table();
        names.AddRow(Kit.Label(AppInfo.DisplayName, Theme.Semibold(18f)));
        names.AddRow(Kit.Label($"Версия {AppInfo.Version}", color: Theme.TextMuted));
        names.AddRow(Kit.Label("Локальная модель-помощник для Claude Code и других IDE"));
        title.AddRow(logo, names);
        root.AddRow(title);

        root.AddRow(Kit.Section("Что делает Offload"));
        root.AddRow(Kit.Wrap(
            "Offload устанавливает llama.cpp и локальную модель для программирования, запускает сервер llama-server на вашем компьютере " +
            "и подключается к IDE (Claude Code, Cursor, VS Code и другим) как MCP-сервер. Всё хранится в профиле пользователя, " +
            "права администратора не нужны."));
        root.AddRow(Kit.Section("Как экономятся токены"));
        root.AddRow(Kit.Wrap(
            "Облачная модель поручает Offload рутинные подзадачи: прочитать и проанализировать файлы, сжать длинный лог сборки, " +
            "сделать первичное ревью diff, написать сообщение коммита, создать файл по описанию или выполнить механическую правку. " +
            "Файлы читает и код пишет локальная модель, а в контекст облачной модели попадает только короткий результат — " +
            "поэтому облачных токенов тратится меньше."));
        root.AddRow(Kit.Wrap(
            "Чтобы начать, перезапустите Claude Code после подключения и попросите: «используй offload, чтобы …»."));

        root.AddRow(Kit.Section("Ссылки"));
        root.AddRow(Kit.Flow(
            Kit.Link("Репозиторий на GitHub", AppInfo.RepositoryUrl),
            Kit.Link("Сообщить о проблеме", AppInfo.RepositoryUrl + "/issues"),
            Kit.Link("llama.cpp", "https://github.com/ggml-org/llama.cpp"),
            Kit.Link("OpenCode", "https://opencode.ai")));

        root.AddRow(Kit.Section("Данные программы"));
        var data = Kit.Grid();
        var dataPath = Kit.Label(AppPaths.DataDir);
        data.AddField("Папка данных:", Kit.Flow(dataPath, Kit.Button("Открыть", (_, _) => Ui.OpenFolder(AppPaths.DataDir))));
        data.AddField("Настройки:", Kit.Flow(Kit.Label(AppPaths.ConfigFile), Kit.Button("Показать", (_, _) => Ui.SelectInExplorer(AppPaths.ConfigFile))));
        data.AddField("Программа:", Kit.Label(AppPaths.ExecutablePath));
        root.AddRow(data);

        Controls.Add(Kit.Scroll(root));
    }

    public override string Key => Tabs.About;

    public override string Title => "О программе";
}
