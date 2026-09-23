using System.Diagnostics;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Ipc;
using Offload.Core.Logging;

namespace Offload.App.Services;

/// <summary>
/// На компьютере должна быть одна копия Offload. Если программа установлена, а запущена другая копия
/// (портативный exe, файл из «Загрузок», старая сборка), пользователь выбирает: открыть установленную,
/// обновить её этой версией, удалить её или отменить запуск. Две копии, работающие по очереди, перехватывали бы
/// друг у друга подключения к IDE и автозапуск.
/// </summary>
internal static class InstallGuard
{
    /// <summary>Проверить при запуске. true — продолжить запуск этой копии.</summary>
    public static bool Check(bool background)
    {
        var installed = InstallInfo.Installed;
        if (installed is null) return true;
        if (installed.IsCurrentProcess)
        {
            CleanupReplacedExe(installed);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar)))
        {
            // Явный портативный режим со своей папкой данных: работаем, но пути в IDE и автозапуск сами не трогаем.
            Log.Info("install", $"Запуск копии с отдельной папкой данных; установленная версия — {installed.ExePath}");
            return true;
        }
        if (!File.Exists(installed.ExePath))
        {
            Log.Warn("install", $"Запись об установке есть, но файла нет: {installed.ExePath} — продолжаем с этой копией");
            return true;
        }
        if (background)
        {
            Log.Warn("install", $"Автозапуск неустановленной копии {AppPaths.ExecutablePath} пропущен: установлена {installed.ExePath}");
            return false;
        }
        return Ask(installed);
    }

    private static bool Ask(InstalledCopy installed)
    {
        var thisVersion = AppInfo.Version;
        var cmp = InstallInfo.CompareVersions(thisVersion, installed.Version);
        var canUpdate = cmp > 0 && !installed.PerMachine && IsSingleFile();

        var open = new TaskDialogCommandLinkButton(L.T("Открыть установленную версию"),
            L.F("Offload {0} из папки {1}", installed.Version ?? "?", installed.Directory));
        var update = new TaskDialogCommandLinkButton(L.F("Обновить установленную до версии {0}", thisVersion),
            L.T("Файл программы будет заменён этой копией. Настройки, модели и подключения к IDE сохранятся."));
        var remove = new TaskDialogCommandLinkButton(L.T("Удалить установленную версию"),
            installed.PerMachine
                ? L.T("Запустится деинсталлятор. После удаления продолжит работу эта копия. Потребуются права администратора.")
                : L.T("Запустится деинсталлятор. После удаления продолжит работу эта копия."));
        var cancel = TaskDialogButton.Cancel;

        var page = new TaskDialogPage
        {
            Caption = "Offload",
            Heading = L.T("Offload уже установлен на этом компьютере"),
            Text =
                L.F("Установлена версия {0} в папке {1}.{2}Сейчас запущена другая копия ({3}): {4}{2}{2}На компьютере должна быть одна копия Offload — иначе копии будут перехватывать друг у друга подключения к IDE и автозапуск.",
                    installed.Version ?? "?", installed.Directory, Environment.NewLine, thisVersion, AppPaths.ExecutablePath) +
                (cmp > 0 && installed.PerMachine
                    ? L.F("{0}{0}Установленная версия стоит «для всех пользователей» — обновите её установщиком Offload-Setup.", Environment.NewLine)
                    : ""),
            Icon = TaskDialogIcon.Information,
            AllowCancel = true,
            DefaultButton = canUpdate ? update : open,
        };
        page.Buttons.Add(open);
        if (canUpdate) page.Buttons.Add(update);
        page.Buttons.Add(remove);
        page.Buttons.Add(cancel);

        var result = TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        try
        {
            if (result == open)
            {
                Launch(installed.ExePath);
                return false;
            }
            if (result == update)
            {
                UpdateInstalled(installed);
                return false;
            }
            if (result == remove)
            {
                if (InstallInfo.Uninstall(installed, TimeSpan.FromMinutes(2)))
                {
                    Log.Info("install", $"Установленная копия удалена: {installed.Directory}");
                    return true;
                }
                Ui.Warn(null, L.T("Установленная версия Offload не удалена. Запуск этой копии отменён, чтобы не было двух копий."));
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("install", "Действие с установленной копией не выполнено", ex);
            Ui.ShowError(null, L.T("Не удалось выполнить действие с установленной версией Offload"), ex);
        }
        return false;
    }

    /// <summary>
    /// Заменить exe установленной копии этим файлом. Работающий exe нельзя перезаписать, но можно переименовать —
    /// поэтому старый файл становится Offload.exe.old (удаляется при следующем запуске), а на его место копируется новый.
    /// MCP-процессы, которые IDE уже запустили, дорабатывают на старом файле до перезапуска IDE.
    /// </summary>
    private static void UpdateInstalled(InstalledCopy installed)
    {
        StopRunningTray();
        var exe = installed.ExePath;
        var old = exe + ".old";
        TryDelete(old);
        File.Move(exe, old, overwrite: true);
        try
        {
            File.Copy(AppPaths.ExecutablePath, exe, overwrite: false);
        }
        catch
        {
            File.Move(old, exe, overwrite: true);
            throw;
        }
        try
        {
            InstallInfo.SetDisplayVersion(AppInfo.Version);
        }
        catch (Exception ex)
        {
            Log.Warn("install", $"Версия в «Программах и компонентах» не обновлена: {ex.Message}");
        }
        Log.Info("install", $"Установленная копия обновлена до {AppInfo.Version}: {exe}");
        Launch(exe);
    }

    private static void StopRunningTray()
    {
        if (!IpcClient.IsTrayRunning()) return;
        IpcClient.SendAsync(new IpcRequest(IpcCommands.StopServer), TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        IpcClient.SendAsync(new IpcRequest(AppIpcCommands.Exit), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        for (var i = 0; i < 40 && IpcClient.IsTrayRunning(); i++) Thread.Sleep(250);
    }

    /// <summary>Самодостаточный однофайловый exe (у dev-сборки рядом лежит Offload.dll — её одним файлом не заменить).</summary>
    private static bool IsSingleFile() => !File.Exists(Path.Combine(AppContext.BaseDirectory, "Offload.dll"));

    private static void CleanupReplacedExe(InstalledCopy installed) => TryDelete(installed.ExePath + ".old");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug("install", $"Не удалён {path}: {ex.Message}");
        }
    }

    private static void Launch(string exe) => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
}
