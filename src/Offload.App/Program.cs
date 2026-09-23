using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Localization;
using Offload.Core.Logging;

namespace Offload.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Режим MCP-сервера — первым и без инициализации WinForms: stdout принадлежит протоколу.
        if (HasArg(args, AppInfo.McpArg))
            return Offload.Mcp.McpEntry.RunAsync(args).GetAwaiter().GetResult();

        if (HasArg(args, UninstallCleanup.Arg))
            return UninstallCleanup.Run();

        Log.Init("app");
        ApplicationConfiguration.Initialize();
        // Сначала обработчики: SetColorMode(Dark) создаёт служебное окно, а после этого режим исключений потока менять нельзя.
        InstallExceptionHandlers();
        ApplyLanguage(ConfigStore.Current.Ui.Language);
        ApplyColorMode(ConfigStore.Current.Ui);

        // Одна копия на компьютер: если установлена другая — предложить открыть, обновить или удалить её.
        if (!InstallGuard.Check(background: HasArg(args, Autostart.BackgroundArg)))
            return 0;

        using var instance = new Mutex(true, IpcNames.MutexName, out var createdNew);
        if (!createdNew)
            return ForwardToRunningInstance(args);

        // Мьютекс для установщика (Inno Setup AppMutex): «программа запущена».
        using var installerMutex = new Mutex(false, IpcNames.InstallerMutexName);

        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            Log.Error("app", "Не удалось создать папку данных", ex);
        }

        Log.Info("app", $"Offload {AppInfo.Version} запущен, данные: {AppPaths.DataDir}");

        var background = HasArg(args, Autostart.BackgroundArg);
        var cfg = ConfigStore.Current;
        var view = background ? StartupView.TrayOnly
            : cfg.SetupCompleted ? StartupView.MainWindow
            : StartupView.SetupWizard;

        using (var context = new TrayApplicationContext(view))
        {
            Application.Run(context);
        }

        GC.KeepAlive(installerMutex);
        Log.Info("app", "Offload завершён");
        return 0;
    }

    private static bool HasArg(string[] args, string arg) => args.Contains(arg, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Применить язык интерфейса: переводы (L) и культура процесса для чисел и дат, чтобы «8.5 GB» и «$0.86»
    /// в английском интерфейсе не выводились с русской запятой.
    /// </summary>
    internal static void ApplyLanguage(string? setting)
    {
        L.Initialize(setting);
        System.Globalization.CultureInfo.CurrentCulture = L.Culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = L.Culture;
    }

    /// <summary>
    /// Тема интерфейса из настроек: палитра Theme (режим, цветовая схема, свой акцент) и тёмный режим стандартных элементов WinForms.
    /// Вызывается до создания окон и при смене внешнего вида (окна после этого пересоздаются).
    /// </summary>
    internal static void ApplyColorMode(UiSettings ui)
    {
        var dark = Theme.Initialize(ui.Theme, ui.ThemePreset, ui.AccentColor);
        try
        {
            Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic);
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Тёмный режим элементов недоступен: {ex.Message}");
        }
    }

    /// <summary>Уже запущен другой экземпляр: попросить его показать окно и выйти.</summary>
    private static int ForwardToRunningInstance(string[] args)
    {
        if (HasArg(args, Autostart.BackgroundArg)) return 0;

        // Разрешаем работающему трею вывести окно на передний план.
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        var command = ConfigStore.Current.SetupCompleted ? IpcCommands.Show : IpcCommands.OpenSetup;
        var response = IpcClient.SendAsync(new IpcRequest(command), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (response is null)
        {
            Log.Warn("app", "Запущенный экземпляр Offload не отвечает");
            Ui.Warn(null, L.T("Offload уже запущен, но не отвечает.\n\nНайдите его значок в области уведомлений или завершите процесс Offload в диспетчере задач и запустите программу снова."));
        }
        return 0;
    }

    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            Log.Error("app", "Необработанная ошибка в интерфейсе", e.Exception);
            Ui.ShowError(null, L.T("Произошла непредвиденная ошибка. Программа продолжит работу, подробности записаны в журнал"), e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("app", "Критическая ошибка", ex);
            else Log.Error("app", $"Критическая ошибка: {e.ExceptionObject}");
            if (e.IsTerminating)
            {
                try
                {
                    MessageBox.Show(
                        L.F("Offload будет закрыт из-за критической ошибки.\n\n{0}\n\nЖурнал: {1}",
                            e.ExceptionObject is Exception ex2 ? Ui.FriendlyError(ex2) : "", Log.CurrentFile),
                        Ui.Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                    // Показать сообщение уже невозможно.
                }
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("app", "Необработанная ошибка фоновой задачи", e.Exception);
            e.SetObserved();
        };
    }
}

/// <summary>Что показать при запуске.</summary>
internal enum StartupView
{
    /// <summary>Только значок в трее (автозапуск).</summary>
    TrayOnly,
    MainWindow,
    SetupWizard,
}
