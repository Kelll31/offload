using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Integrations;
using Offload.OpenCode;

namespace Offload.App.Services;

/// <summary>
/// «Offload.exe --uninstall-cleanup» (вызывает деинсталлятор): остановить трей, отключить Offload
/// от всех IDE, убрать все дополнения Claude Code (<see cref="ClaudeCodeExtras.RemoveAll"/>) и секции инструкций
/// в файлах других клиентов (<see cref="ClientGuidance.RemoveAll"/>), провайдера
/// из глобального конфига OpenCode и автозапуск. Ошибки только журналируются — удаление программы не должно прерываться.
/// <para>
/// Изолированный OpenCode (<c>OpenCodeInstaller.Uninstall</c>) здесь намеренно не удаляется: он лежит в
/// %LOCALAPPDATA%\Offload, и деинсталлятор (installer/Offload.iss, usPostUninstall) отдельно спрашивает, удалять ли
/// эту папку вместе с моделями и llama.cpp. «Нет» означает сохранить всё для повторной установки, «Да» — папка
/// удаляется целиком, включая OpenCode. Файлы вне этой папки (конфиги IDE, ~/.claude) снимаются здесь всегда.
/// </para>
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
        // Все дополнения сразу (навык, правило, субагент, разрешения) — новое дополнение не будет забыто.
        Step("дополнения Claude Code", () => { foreach (var r in ClaudeCodeExtras.RemoveAll()) Report(r); }); // l10n-ignore
        // Секции Offload в AGENTS.md (Codex) и GEMINI.md (Gemini CLI); текст пользователя в этих файлах остаётся.
        Step("инструкции для других клиентов", () => { foreach (var r in ClientGuidance.RemoveAll()) Report(r); }); // l10n-ignore
        Step("глобальный конфиг OpenCode", OpenCodeConfigWriter.UnregisterGlobal); // l10n-ignore
        // Только свой автозапуск (или указывающий на удалённый exe): значение другой существующей копии не трогаем.
        Step("автозапуск", () => // l10n-ignore
        {
            var value = Autostart.CurrentValue;
            if (Autostart.OwnedBy(value, Offload.Core.AppPaths.ExecutablePath, File.Exists)) Autostart.Set(false);
            else if (value is not null) Log.Info("uninstall", $"Автозапуск указывает на другую копию Offload ({Autostart.ExeOf(value)}) — оставлен");
        });

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
