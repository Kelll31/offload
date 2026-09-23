using Offload.App.Controls;
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
        names.AddRow(Kit.Label(L.F("Версия {0}", AppInfo.Version), color: Theme.TextMuted));
        names.AddRow(Kit.Label(L.T("Локальная модель-помощник для Claude Code и других IDE")));
        title.AddRow(logo, names);
        root.AddRow(title);

        root.AddRow(Kit.Section(L.T("Что делает Offload")));
        root.AddRow(Kit.Wrap(L.T(
            "Offload устанавливает llama.cpp и локальную модель для программирования, запускает сервер llama-server на вашем компьютере и подключается к IDE (Claude Code, Cursor, VS Code и другим) как MCP-сервер. Всё хранится в профиле пользователя, права администратора не нужны.")));
        root.AddRow(Kit.Section(L.T("Как экономятся токены")));
        root.AddRow(Kit.Wrap(L.T(
            "Облачная модель поручает Offload рутинные подзадачи: прочитать и проанализировать файлы, сжать длинный лог сборки, сделать первичное ревью diff, написать сообщение коммита, создать файл по описанию или выполнить механическую правку. Файлы читает и код пишет локальная модель, а в контекст облачной модели попадает только короткий результат — поэтому облачных токенов тратится меньше.")));
        root.AddRow(Kit.Wrap(
            L.T("Чтобы начать, перезапустите Claude Code после подключения и попросите: «используй offload, чтобы …».")));

        root.AddRow(Kit.Section(L.T("Ссылки")));
        root.AddRow(Kit.Flow(
            Kit.Link(L.T("Репозиторий на GitHub"), AppInfo.RepositoryUrl),
            Kit.Link(L.T("Сообщить о проблеме"), AppInfo.RepositoryUrl + "/issues"),
            Kit.Link("llama.cpp", "https://github.com/ggml-org/llama.cpp"),
            Kit.Link("OpenCode", "https://opencode.ai")));

        root.AddRow(Kit.Section(L.T("Горячие клавиши")));
        var keys = Kit.Grid();
        foreach (var (key, what) in Shortcuts)
        {
            var k = Kit.Label(key, Theme.Mono(9f));
            keys.AddField(what, k);
        }
        root.AddRow(keys);

        root.AddRow(Kit.Section(L.T("Данные программы")));
        var data = Kit.Grid();
        var dataPath = Kit.Label(AppPaths.DataDir);
        data.AddField(L.T("Папка данных:"), Kit.Flow(dataPath, Kit.Button(L.T("Открыть"), (_, _) => Ui.OpenFolder(AppPaths.DataDir))));
        data.AddField(L.T("Настройки:"), Kit.Flow(Kit.Label(AppPaths.ConfigFile), Kit.Button(L.T("Показать"), (_, _) => Ui.SelectInExplorer(AppPaths.ConfigFile))));
        data.AddField(L.T("Программа:"), Kit.Label(AppPaths.ExecutablePath));
        root.AddRow(data);

        Controls.Add(Kit.Scroll(root));
    }

    private static (string Key, string What)[] Shortcuts =>
    [
        ("Ctrl+1 … Ctrl+8", L.T("Разделы по порядку:")),
        ("Ctrl+Tab", L.T("Следующий раздел:")),
        ("↑ ↓ Enter", L.T("Навигация в боковой панели:")),
        ("Ctrl+C / Ctrl+A", L.T("Журнал — копировать / выделить всё:")),
    ];

    public override string Key => Tabs.About;

    public override string Title => L.T("О программе");

    public override string Subtitle => L.T("Версия, ссылки и расположение данных программы");

    public override string Glyph => Glyphs.Info;
}
