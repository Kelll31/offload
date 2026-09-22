using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Llama;
using Offload.OpenCode;

namespace Offload.App.Services;

/// <summary>Запуск интерактивного OpenCode (TUI) с локальной моделью в выбранной папке.</summary>
internal static class OpenCodeLauncher
{
    private static string? _lastFolder;

    public static async Task LaunchAsync(IAppShell shell, IWin32Window? owner)
    {
        var cfg = ConfigStore.Current;
        var exe = Ui.Try(() => OpenCodeInstaller.FindExecutable(cfg), null, "FindExecutable");
        if (exe is null)
        {
            if (Ui.Confirm(owner, "OpenCode не установлен. Открыть вкладку OpenCode, чтобы установить его?"))
                shell.ShowMainWindow("opencode");
            return;
        }

        var folder = DialogOwner.Run(owner, o =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Папка проекта, в которой открыть OpenCode",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                InitialDirectory = _lastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            return dlg.ShowDialog(o) == DialogResult.OK ? dlg.SelectedPath : null;
        });
        if (string.IsNullOrWhiteSpace(folder)) return;
        _lastFolder = folder;

        if (shell.Server.State != ServerState.Running)
        {
            shell.Notify("Запуск локальной модели", "OpenCode откроется, когда модель загрузится.");
            if (!await shell.Server.StartAsync())
            {
                Ui.ShowError(owner, "Не удалось запустить сервер llama.cpp", shell.Server.LastError ?? "");
                return;
            }
        }

        try
        {
            cfg = ConfigStore.Reload();
            Ui.Try(() => OpenCodeConfigWriter.WriteManagedConfig(cfg), "", "WriteManagedConfig");
            OpenCodeRunner.LaunchInteractive(cfg, folder);
            Log.Info("opencode", $"OpenCode открыт в {folder}");
        }
        catch (Exception ex)
        {
            Log.Error("opencode", "Не удалось открыть OpenCode", ex);
            Ui.ShowError(owner, "Не удалось открыть OpenCode", ex);
        }
    }
}

/// <summary>
/// Владелец для диалогов, вызванных из меню трея: без окна-владельца диалог может оказаться под другими окнами.
/// </summary>
internal static class DialogOwner
{
    public static T Run<T>(IWin32Window? owner, Func<IWin32Window, T> show)
    {
        if (owner is Control { IsDisposed: false, Visible: true }) return show(owner);
        // Невидимое окно в центре экрана: диалоги центрируются относительно владельца.
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        using var host = new Form
        {
            ShowInTaskbar = false,
            TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2),
            Size = new Size(1, 1),
            Opacity = 0,
        };
        host.Show();
        host.Activate();
        return show(host);
    }
}
