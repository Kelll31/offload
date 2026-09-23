using Offload.Core;
using Offload.Core.Logging;
using Offload.Integrations.Claude;
using Offload.Integrations.Clients;

namespace Offload.Integrations;

/// <summary>Описание stdio MCP-сервера, которое прописывается в IDE.</summary>
public sealed record McpServerSpec(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env)
{
    /// <summary>Offload.exe текущего процесса с аргументом --mcp.</summary>
    public static McpServerSpec ForCurrentExecutable()
    {
        var exe = AppPaths.ExecutablePath;
        // Запуск через «dotnet Offload.dll»: в конфиг IDE нужен сам Offload.exe, а не dotnet.exe.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var apphost = Path.Combine(AppContext.BaseDirectory, "Offload.exe");
            if (File.Exists(apphost)) exe = apphost;
        }
        return new McpServerSpec(
            AppInfo.McpServerId,
            Path.GetFullPath(exe),
            [AppInfo.McpArg],
            new Dictionary<string, string>());
    }
}

public enum IntegrationStatus
{
    /// <summary>IDE/агент не найден на компьютере.</summary>
    ClientNotFound,
    /// <summary>IDE найдена, Offload не подключён.</summary>
    NotRegistered,
    /// <summary>Подключён с актуальным путём к exe.</summary>
    Registered,
    /// <summary>Подключён, но путь/аргументы устарели (например, программа перемещена).</summary>
    Outdated,
    /// <summary>Не удалось прочитать конфигурацию.</summary>
    Error,
}

public sealed record IntegrationResult(bool Ok, string Message, string? BackupPath = null);

/// <summary>Одна IDE или агент, в которую можно прописать MCP-сервер.</summary>
public interface IIdeIntegration
{
    /// <summary>Стабильный идентификатор: claude-code, claude-desktop, cursor, vscode, windsurf, cline, roo-code, zed, gemini-cli, codex, continue, visual-studio…</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Путь к файлу конфигурации (для кнопки «Открыть файл»), если применимо.</summary>
    string? ConfigPath { get; }

    /// <summary>Подсказка пользователю на русском после подключения (например, «Перезапустите Cursor»).</summary>
    string? PostRegisterHint { get; }

    bool IsClientInstalled();

    IntegrationStatus GetStatus(McpServerSpec spec);

    /// <summary>Прописать (или обновить) сервер. Перед изменением файла делается резервная копия.</summary>
    Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default);

    Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default);
}

/// <summary>Все поддерживаемые IDE.</summary>
public static class IntegrationRegistry
{
    /// <summary>
    /// Стабильный порядок: claude-code, claude-desktop, cursor, vscode, vscode-insiders, copilot-cli, windsurf, cline,
    /// roo-code, kilo-code, codex, gemini-cli, zed, visual-studio, junie, jetbrains-ai, continue, kiro, trae, qoder.
    /// </summary>
    public static IReadOnlyList<IIdeIntegration> All => KnownIntegrations.All;

    public static IIdeIntegration? Find(string id) => All.FirstOrDefault(i => i.Id == id);

    /// <summary>
    /// Обновить пути во всех уже подключённых IDE, если exe переместили (вызывается при старте трея).
    /// Возвращает список обновлённых IDE.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RefreshOutdatedAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        var updated = new List<string>();
        foreach (var integration in All)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (integration.GetStatus(spec) != IntegrationStatus.Outdated) continue;
                var r = await integration.RegisterAsync(spec, ct);
                if (r.Ok) updated.Add(integration.Id);
                else Log.Warn("Integrations", $"{integration.Id}: не удалось обновить путь: {r.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Integrations", $"{integration.Id}: ошибка обновления", ex);
            }
        }
        if (updated.Count > 0) Log.Info("Integrations", "Обновлён путь к Offload: " + string.Join(", ", updated));
        return updated;
    }

    /// <summary>
    /// Отключить Offload от всех IDE (при удалении программы). Кроме подключённых (Registered/Outdated),
    /// проверяются и устаревшие расположения (старый путь Cline, второй файл Kilo, конфиг удалённой IDE):
    /// UnregisterAsync удаляет только записи, указывающие на Offload, чужие и неразборчивые файлы не трогает.
    /// </summary>
    public static async Task UnregisterAllAsync(CancellationToken ct = default)
    {
        var spec = McpServerSpec.ForCurrentExecutable();
        foreach (var integration in All)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (integration is JetBrainsAiIntegration) continue;
                var status = integration.GetStatus(spec);
                if (status == IntegrationStatus.Error) continue;
                if (status is not (IntegrationStatus.Registered or IntegrationStatus.Outdated) && !HasStaleEntry(integration)) continue;
                var r = await integration.UnregisterAsync(ct);
                Log.Write(r.Ok ? LogLevel.Info : LogLevel.Warn, "Integrations", $"{integration.Id}: {r.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Integrations", $"{integration.Id}: ошибка отключения", ex);
            }
        }
    }

    /// <summary>Наша запись в каком-либо из известных файлов клиента (в т.ч. устаревших расположений).</summary>
    private static bool HasStaleEntry(IIdeIntegration integration) =>
        integration is JsonIntegration json && json.HasOurEntryAnywhere();
}

/// <summary>
/// Дополнительная настройка Claude Code: инструкции по делегированию (субагент/навык/CLAUDE.md)
/// и разрешения на вызов инструментов без подтверждения.
/// </summary>
public static class ClaudeCodeExtras
{
    /// <summary>Установлены ли инструкции по делегированию для Claude Code.</summary>
    public static bool IsGuidanceInstalled() => ClaudeExtrasImpl.IsManagedFile(ClaudeExtrasImpl.SkillFile);

    /// <summary>Установить инструкции (файлы в ~/.claude), объясняющие Claude, когда делегировать задачи Offload.</summary>
    public static IntegrationResult InstallGuidance() =>
        ClaudeExtrasImpl.InstallManaged(ClaudeExtrasImpl.SkillFile, ClaudeTexts.Skill(), L.T("навык /offload"));

    public static IntegrationResult RemoveGuidance() =>
        ClaudeExtrasImpl.RemoveManaged(ClaudeExtrasImpl.SkillFile, L.T("навык /offload"), deleteEmptyDir: true);

    /// <summary>Разрешены ли инструменты Offload без подтверждения (~/.claude/settings.json permissions.allow).</summary>
    public static bool AreToolsPreapproved() => ClaudeExtrasImpl.AllPresent(McpToolNames.ReadOnly);

    /// <summary>Разрешены ли без подтверждения и инструменты записи (все имена из McpToolNames.Writing).</summary>
    public static bool AreWriteToolsPreapproved() => ClaudeExtrasImpl.AllPresent(McpToolNames.Writing);

    /// <param name="includeWriteTools">Также разрешить инструменты, изменяющие файлы (false — убрать их, если были).</param>
    public static IntegrationResult PreapproveTools(bool includeWriteTools) => ClaudeExtrasImpl.Preapprove(includeWriteTools);

    public static IntegrationResult RevokeToolApprovals() => ClaudeExtrasImpl.Revoke();

    /// <summary>«Строгий режим»: правило ~/.claude/rules/offload.md — Claude активнее делегирует (загружается в каждой сессии).</summary>
    public static bool IsStrongRuleInstalled() => ClaudeExtrasImpl.IsManagedFile(ClaudeExtrasImpl.RuleFile);

    public static IntegrationResult InstallStrongRule() =>
        ClaudeExtrasImpl.InstallManaged(ClaudeExtrasImpl.RuleFile, ClaudeTexts.StrongRule(), L.T("правило «строгого режима»"));

    public static IntegrationResult RemoveStrongRule() =>
        ClaudeExtrasImpl.RemoveManaged(ClaudeExtrasImpl.RuleFile, L.T("правило «строгого режима»"), deleteEmptyDir: false);

    /// <summary>Субагент ~/.claude/agents/offload-runner.md (Haiku) для пакетных механических задач через локальную модель.</summary>
    public static bool IsRunnerAgentInstalled() => ClaudeExtrasImpl.IsManagedFile(ClaudeExtrasImpl.AgentFile);

    public static IntegrationResult InstallRunnerAgent() =>
        ClaudeExtrasImpl.InstallManaged(ClaudeExtrasImpl.AgentFile, ClaudeTexts.RunnerAgent(), L.T("субагент offload-runner"));

    public static IntegrationResult RemoveRunnerAgent() =>
        ClaudeExtrasImpl.RemoveManaged(ClaudeExtrasImpl.AgentFile, L.T("субагент offload-runner"), deleteEmptyDir: false);
}
