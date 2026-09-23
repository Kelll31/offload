using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Integrations;
using Offload.OpenCode;

namespace Offload.App.Services;

/// <summary>
/// «Offload.exe --uninstall-cleanup» (вызывает деинсталлятор): остановить трей, отключить Offload
/// от всех IDE, убрать инструкции и разрешения Claude Code, провайдера из глобального конфига OpenCode
/// и автозапуск. Ошибки только журналируются — удаление программы не должно прерываться.
/// </summary>
internal static class UninstallCleanup
{
    public const string Arg = "--uninstall-cleanup";

    public static int Run()
    {
        Log.Init("uninstall");
        Log.Info("uninstall", "Очистка перед удалением Offload");

        Step("остановка сервера", () => // l10n-ignore
        {
            if (!IpcClient.IsTrayRunning()) return;
            IpcClient.SendAsync(new IpcRequest(IpcCommands.StopServer), TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            IpcClient.SendAsync(new IpcRequest(AppIpcCommands.Exit), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            // Ждём завершения трея, чтобы файлы программы освободились.
            for (var i = 0; i < 40 && IpcClient.IsTrayRunning(); i++) Thread.Sleep(250);
        });
        Step("отключение от IDE", () => IntegrationRegistry.UnregisterAllAsync().GetAwaiter().GetResult()); // l10n-ignore
        Step("инструкции Claude Code", () => Report(ClaudeCodeExtras.RemoveGuidance())); // l10n-ignore
        Step("разрешения Claude Code", () => Report(ClaudeCodeExtras.RevokeToolApprovals())); // l10n-ignore
        Step("глобальный конфиг OpenCode", OpenCodeConfigWriter.UnregisterGlobal); // l10n-ignore
        Step("автозапуск", () => Autostart.Set(false)); // l10n-ignore

        Log.Info("uninstall", "Очистка завершена");
        return 0;
    }

    private static void Report(IntegrationResult r)
    {
        if (r.Ok) Log.Info("uninstall", r.Message);
        else Log.Warn("uninstall", r.Message);
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
            Log.Info("uninstall", $"Готово: {name}");
        }
        catch (Exception ex)
        {
            Log.Warn("uninstall", $"Не выполнено ({name}): {ex.GetType().Name}: {ex.Message}");
        }
    }
}
