using Offload.Core.Config;

namespace Offload.Integrations;

/// <summary>
/// Когда Offload подключает Claude сам, без клика пользователя. Только для Claude Code и Claude Desktop и только там,
/// где записи «offload» нет вообще: чужую, устаревшую или нечитаемую запись автоматически не трогаем никогда.
/// </summary>
public static class AutoConnectPolicy
{
    /// <summary>
    /// Клиенты Claude, подключаемые автоматически. Дистрибутивы WSL — вне списка: их перечисление запускает wsl.exe
    /// (подключаются кнопкой на странице «Интеграции»).
    /// </summary>
    public static readonly IReadOnlyList<string> Ids = ["claude-code", "claude-desktop"];

    /// <summary>
    /// IDE, которые можно добавить к автоподключению по выбору пользователя (<see cref="AppConfig.AutoConnectExtra"/>):
    /// клиенты с файлом конфигурации. Не входят: WSL (запуск wsl.exe), JetBrains AI Assistant (подключается вручную).
    /// </summary>
    public static bool CanOptIn(string id) =>
        !Ids.Contains(id) && !id.StartsWith("claude-code-wsl", StringComparison.Ordinal) && id != "jetbrains-ai";

    /// <summary>Кого подключать автоматически: Claude Code и Claude Desktop плюс выбранные пользователем IDE.</summary>
    public static IReadOnlyList<string> Candidates(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return [.. Ids, .. (cfg.AutoConnectExtra ?? []).Where(CanOptIn).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Подключать ли клиента сейчас (чистое решение по настройкам и статусу записи).</summary>
    public static bool ShouldConnect(AppConfig cfg, string id, IntegrationStatus status)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return cfg.SetupCompleted
               && cfg.Ui.AutoRepairIntegrations               // пользователь не запретил автоматику
               && Candidates(cfg).Contains(id)
               && status == IntegrationStatus.NotRegistered   // Foreign / Outdated / Error автоматически не трогаем
               && !cfg.DeclinedIntegrations.Contains(id)      // отказ пользователя важнее
               && !cfg.Integrations.Contains(id);             // уже отслеживается автовосстановлением
    }
}
