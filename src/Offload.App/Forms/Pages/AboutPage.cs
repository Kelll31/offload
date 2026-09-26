using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «О программе»: версия и обновления Offload, пакет диагностики, ссылки, горячие клавиши, расположение данных.</summary>
internal sealed class AboutPage : PageBase
{
    /// <summary>Разделов в окне по умолчанию (до того, как страница окажется в окне).</summary>
    private const int DefaultSectionCount = 9;

    private readonly Label _sectionKeys = Kit.Label(SectionShortcut(DefaultSectionCount), Theme.Mono(9f));
    private readonly Label _updateStatus = Kit.Wrap("");
    private readonly Button _checkUpdate;
    private readonly Button _installUpdate;
    private readonly ProgressPanel _updateProgress = new(withCancel: true);
    private CancellationTokenSource? _updateCts;
    // null — проверка в этом окне не выполнялась; true — обновлений нет; false — проверка не удалась.
    private bool? _upToDate;

    public AboutPage(IAppShell shell) : base(shell)
    {
        _checkUpdate = Kit.Button(L.T("Проверить обновления"), async (_, _) => await CheckUpdateAsync(), 170);
        _installUpdate = Kit.Primary(L.T("Обновить"), async (_, _) => await RunUpdateAsync(), 160);
        _updateProgress.CancelRequested += (_, _) => _updateCts?.Cancel();
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

        root.AddRow(Kit.Section(L.T("Обновления")));
        root.AddRow(_updateStatus);
        root.AddRow(Kit.Flow(_installUpdate, _checkUpdate));
        root.AddRow(_updateProgress);
        root.AddRow(Kit.Hint(L.T("После обновления перезапустите IDE: MCP-серверы Offload, которые IDE уже запустили, работают на старой версии (или закрываются установщиком), пока IDE не перезапущена.")));

        root.AddRow(Kit.Section(L.T("Сообщить о проблеме")));
        root.AddRow(Kit.Wrap(L.T("Пакет диагностики — архив с журналами, настройками без ключей, сведениями о компьютере и версиях. Он сохраняется на диск и никуда не отправляется: после сохранения можно открыть форму issue на GitHub и приложить архив самостоятельно.")));
        root.AddRow(Kit.Flow(Kit.Button(L.T("Собрать пакет диагностики…"), async (_, _) =>
            await RunBusyAsync(() => DiagnosticsUi.RunAsync(Owner, Shell), L.T("Не удалось собрать пакет диагностики")), 220)));

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
            Kit.Link(L.T("Все релизы"), AppInfo.RepositoryUrl + "/releases"),
            Kit.Link("llama.cpp", "https://github.com/ggml-org/llama.cpp"),
            Kit.Link("OpenCode", "https://opencode.ai")));

        root.AddRow(Kit.Section(L.T("Горячие клавиши")));
        var keys = Kit.Grid();
        keys.AddField(L.T("Разделы по порядку:"), _sectionKeys);
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

    /// <summary>«Ctrl+1 … Ctrl+N» по числу разделов (быстрые клавиши есть только для первых девяти).</summary>
    internal static string SectionShortcut(int count) =>
        count <= 1 ? "Ctrl+1" : $"Ctrl+1 … Ctrl+{Math.Min(count, 9)}";

    protected override void OnActivated()
    {
        if (FindForm() is MainForm main) _sectionKeys.Text = SectionShortcut(main.PageCount);
        UpdateUpdateState();
    }

    public override void OnConfigChanged() => UpdateUpdateState();

    public override string? BusyDescription => _updateCts is null ? null : L.T("загрузка обновления Offload");

    protected override void UpdateUiState()
    {
        var mode = AppUpdater.CurrentMode;
        _checkUpdate.Enabled = !IsBusy && mode != AppUpdateMode.None;
        _installUpdate.Enabled = !IsBusy;
    }

    // ---------- Обновления ----------

    private void UpdateUpdateState()
    {
        var mode = AppUpdater.CurrentMode;
        var pending = Shell.PendingAppUpdate;
        _installUpdate.Visible = pending is not null;
        if (pending is not null)
            _installUpdate.Text = pending.CanInstall ? L.F("Обновить до {0}", pending.Version) : L.T("Открыть страницу релиза");

        (_updateStatus.Text, _updateStatus.ForeColor) = mode switch
        {
            AppUpdateMode.None when DevMode.Active => (L.T("Режим разработчика: самообновление выключено."), Theme.TextMuted),
            AppUpdateMode.None => (L.T("Эта сборка не обновляется автоматически."), Theme.TextMuted),
            _ when pending is { CanInstall: false } => (L.F("Доступна версия {0} (установлена {1}). Эту установку («для всех пользователей») нужно обновить установщиком со страницы релиза.", pending.Version, AppInfo.Version), Theme.WarnText),
            _ when pending is not null => (L.F("Доступна версия {0} (установлена {1}).", pending.Version, AppInfo.Version), Theme.OkText),
            _ when _upToDate == true => (L.F("Установлена последняя версия ({0}).", AppInfo.Version), Theme.OkText),
            _ when _upToDate == false => (L.T("Не удалось проверить обновления — подробности в журнале."), Theme.WarnText),
            _ => (ConfigStoreChecks()
                ? L.T("Обновления проверяются при запуске, не чаще раза в 12 часов.")
                : L.T("Автоматическая проверка обновлений выключена в настройках."), Theme.TextMuted),
        };
        UpdateUiState();
    }

    private static bool ConfigStoreChecks() => Core.Config.ConfigStore.Current.Ui.CheckAppUpdates;

    private async Task CheckUpdateAsync()
    {
        await RunBusyAsync(async () =>
        {
            _updateStatus.Text = L.T("Проверка обновлений…");
            _updateStatus.ForeColor = Theme.TextMuted;
            try
            {
                // До ответа считаем, что проверка не удалась (таймаут или ошибка сети).
                _upToDate = false;
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                var info = await AppUpdater.CheckAsync(force: true, cts.Token);
                Shell.PendingAppUpdate = info;
                _upToDate = info is null;
            }
            finally
            {
                if (!IsDisposed) UpdateUpdateState();
            }
        }, L.T("Не удалось проверить обновления Offload"));
    }

    /// <summary>Обновить Offload: загрузка с проверкой, затем выход — установщик или новая версия запускаются после закрытия.</summary>
    public async Task RunUpdateAsync()
    {
        if (IsBusy) return;
        var info = Shell.PendingAppUpdate;
        if (info is null)
        {
            await CheckUpdateAsync();
            info = Shell.PendingAppUpdate;
            if (info is null)
            {
                if (_upToDate == true) Ui.Info(Owner, L.F("Установлена последняя версия Offload ({0}).", AppInfo.Version));
                return;
            }
        }
        if (!info.CanInstall)
        {
            Ui.OpenShell(info.PageUrl);
            return;
        }
        var how = info.Mode == AppUpdateMode.Installer
            ? L.T("Будет загружен установщик и запущен в тихом режиме; Offload закроется и откроется снова после установки.")
            : L.T("Будет загружен новый Offload.exe; программа закроется, заменит файл и запустится снова.");
        if (!Ui.Confirm(Owner, L.F("Обновить Offload до версии {0}?{1}{1}{2}{1}{1}Сервер llama.cpp будет остановлен. После обновления перезапустите IDE: запущенные ими MCP-серверы Offload работают на старой версии, пока IDE не перезапущена.",
                info.Version, Environment.NewLine, how)))
            return;

        var cts = _updateCts = new CancellationTokenSource();
        _updateProgress.Reset();
        _updateProgress.Start(L.F("Загрузка Offload {0}…", info.Version));
        var progress = new Progress<DownloadProgress>(p => _updateProgress.Report(ToStep(p, info.Version)));
        string? file = null;
        bool ok;
        try
        {
            ok = await RunBusyAsync(async () =>
            {
                file = await AppUpdater.DownloadAsync(info, AppPaths.ExecutablePath, progress, cts.Token);
            }, L.T("Не удалось загрузить обновление Offload"));
        }
        finally
        {
            cts.Dispose();
            _updateCts = null;
            UpdateUiState();
        }
        if (!ok || file is null)
        {
            _updateProgress.Finish(L.T("Обновление не установлено."), false);
            return;
        }

        _updateProgress.Finish(L.T("Обновление загружено и проверено — Offload перезапускается…"), true);
        if (info.Mode == AppUpdateMode.Installer) AppUpdater.ScheduleInstaller(file, AppPaths.ExecutablePath);
        else AppUpdater.SchedulePortableSwap(file, AppPaths.ExecutablePath);
        await Shell.ExitAsync();
        if (Shell.IsExiting) return;
        // Выход отменён (например, идёт загрузка модели) — ничего не меняем, файл обновления остаётся для следующей попытки.
        AppUpdater.CancelPending();
        if (!IsDisposed) _updateProgress.Finish(L.T("Выход отменён — обновление не установлено. Нажмите «Обновить» ещё раз, когда программа освободится."), false);
    }

    private static StepProgress ToStep(DownloadProgress p, string version) => p.Stage switch
    {
        DownloadStage.Connecting => new StepProgress(L.F("Загрузка Offload {0}…", version)),
        DownloadStage.Verifying => new StepProgress(L.T("Проверка контрольной суммы…"), p.Fraction),
        DownloadStage.Completed => new StepProgress(L.T("Проверка подписи…")),
        _ => new StepProgress(L.F("Загрузка Offload {0}…", version), p.Fraction,
            (p.TotalBytes is long total ? L.F("{0} из {1}", FileUtil.FormatBytes(p.BytesReceived), FileUtil.FormatBytes(total)) : FileUtil.FormatBytes(p.BytesReceived))
            + (p.BytesPerSecond > 0 ? " · " + FileUtil.FormatSpeed(p.BytesPerSecond) : "")),
    };

    private static (string Key, string What)[] Shortcuts =>
    [
        ("Ctrl+Tab", L.T("Следующий раздел:")),
        ("↑ ↓ Enter", L.T("Навигация в боковой панели:")),
        ("Ctrl+C / Ctrl+A", L.T("Журнал — копировать / выделить всё:")),
    ];

    public override string Key => Tabs.About;

    public override string Title => L.T("О программе");

    public override string Subtitle => L.T("Версия, ссылки и расположение данных программы");

    public override string Glyph => Glyphs.Info;
}
