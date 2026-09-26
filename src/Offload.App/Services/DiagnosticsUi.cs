using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.App.Services;

/// <summary>Кнопка «Пакет диагностики…»: выбор файла, сборка zip в фоне, предложение открыть issue на GitHub.</summary>
internal static class DiagnosticsUi
{
    /// <summary>Собрать пакет (UI-поток). false — пользователь отменил выбор файла.</summary>
    public static async Task<bool> RunAsync(IWin32Window? owner, IAppShell shell)
    {
        string path;
        using (var dialog = new SaveFileDialog
        {
            Title = L.T("Сохранить пакет диагностики"),
            Filter = L.T("Архив ZIP (*.zip)|*.zip"),
            FileName = DiagnosticsBundle.DefaultFileName(DateTime.Now),
            InitialDirectory = DefaultFolder(),
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = "zip",
        })
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
            path = dialog.FileName;
        }

        var hw = shell.Hardware.Current;
        if (hw is null)
        {
            try
            {
                hw = await shell.Hardware.GetAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug("diagnostics", $"Оборудование не определено: {ex.Message}");
            }
        }
        await Task.Run(() => DiagnosticsBundle.Create(path, shell, hw));
        Log.Info("diagnostics", $"Пакет диагностики сохранён: {path}");
        ShowResult(owner, path, hw);
        return true;
    }

    /// <summary>«Загрузки», если есть, иначе рабочий стол.</summary>
    private static string DefaultFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static void ShowResult(IWin32Window? owner, string path, Core.Hardware.HardwareInfo? hw)
    {
        var show = new TaskDialogCommandLinkButton(L.T("Показать файл"), L.T("Открыть папку с архивом в проводнике"));
        var issue = new TaskDialogCommandLinkButton(L.T("Открыть новое issue на GitHub"),
            L.T("Откроется форма с заполненной версией, видеокартой и моделью. Архив не прикрепляется автоматически — перетащите его в форму сами."));
        var page = new TaskDialogPage
        {
            Caption = Ui.Caption,
            Heading = L.T("Пакет диагностики сохранён"),
            Text = L.F("{0}{1}{1}В архиве — журналы, настройки (ключи и токены заменены на ***), сведения о компьютере, версиях, подключениях к IDE и сводка статистики без содержимого запросов. Просмотрите архив перед отправкой.", path, Environment.NewLine),
            Icon = TaskDialogIcon.Information,
            AllowCancel = true,
            Buttons = { show, issue, TaskDialogButton.Close },
            DefaultButton = issue,
        };
        TaskDialogButton result;
        try
        {
            result = owner is Control { IsHandleCreated: true } c
                ? TaskDialog.ShowDialog(c, page)
                : TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("ui", $"Диалог не показан: {ex.Message}");
            Ui.SelectInExplorer(path);
            return;
        }
        if (result == show) Ui.SelectInExplorer(path);
        else if (result == issue)
            Ui.OpenShell(DiagnosticsBundle.IssueUrl(AppInfo.Version, DiagnosticsBundle.GpuLine(hw), DiagnosticsBundle.ModelLine(ConfigStore.Current)));
    }
}
