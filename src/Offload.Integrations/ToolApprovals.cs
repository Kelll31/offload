using Offload.Core;
using Offload.Core.Logging;
using Offload.Integrations.Claude;
using Offload.Integrations.Clients;

namespace Offload.Integrations;

/// <summary>Разрешены ли в клиенте инструменты записи Offload без подтверждения.</summary>
public enum WriteApproval
{
    /// <summary>Все инструменты записи спрашивают подтверждение.</summary>
    None,
    /// <summary>Разрешена часть: обычно после обновления Offload появились новые инструменты записи (сами они не разрешаются).</summary>
    Partial,
    /// <summary>Все инструменты записи разрешены.</summary>
    Full,
}

/// <summary>
/// Состояние разрешений в одном клиенте: <paramref name="Missing"/> — инструменты записи, которые ещё спрашивают подтверждение;
/// <paramref name="WholeServer"/> — клиент разрешает только весь сервер сразу (trust у Gemini CLI): такое разрешение
/// распространяется и на будущие инструменты, а состояния <see cref="WriteApproval.Partial"/> у него не бывает.
/// </summary>
public sealed record ClientApproval(string Id, string DisplayName, WriteApproval State, IReadOnlyList<string> Missing, bool WholeServer = false);

/// <summary>
/// Единая настройка «инструменты записи Offload без подтверждения» для всех клиентов, где это возможно: Claude Code
/// (permissions.allow в ~/.claude/settings.json), Cline и Kiro (autoApprove), Roo Code (alwaysAllow), Gemini CLI (trust —
/// доверие всему серверу). Состояние не хранится в настройках Offload, а читается из самих файлов клиентов.
/// Новые инструменты записи после обновления Offload сами не разрешаются — только повторным согласием; исключение —
/// Gemini CLI, где trust разрешает сервер целиком.
/// </summary>
public static class ToolApprovals
{
    private const string ClaudeCodeId = "claude-code";

    /// <summary>Клиенты с разрешениями, которыми управляет Offload (идентификаторы в порядке IntegrationRegistry).</summary>
    public static IReadOnlyList<string> SupportedIds =>
        [ClaudeCodeId, .. IntegrationRegistry.All.OfType<JsonIntegration>().Where(j => j.Approval is not null).Select(j => j.Id)];

    /// <summary>Названия поддерживаемых клиентов для подсказки в интерфейсе.</summary>
    public static IReadOnlyList<string> SupportedNames =>
        SupportedIds.Select(id => IntegrationRegistry.Find(id)?.DisplayName ?? id).ToList();

    /// <summary>
    /// Клиенты, где разрешения Offload уже заведены: Claude Code — если в permissions.allow есть хоть один инструмент Offload;
    /// остальные — если в их конфиге есть наша запись. Чтение файлов — не вызывать в цикле из потока интерфейса.
    /// </summary>
    public static IReadOnlyList<ClientApproval> Inspect()
    {
        var result = new List<ClientApproval>();
        try
        {
            if (ClientLocations.ClaudeCodeInstalled() && ClaudeExtrasImpl.MissingTools() is { } claudeMissing)
                result.Add(Of(ClaudeCodeId, claudeMissing, wholeServer: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Integrations", $"{ClaudeCodeId}: разрешения не прочитаны: {ex.Message}");
        }
        foreach (var json in IntegrationRegistry.All.OfType<JsonIntegration>().Where(j => j.Approval is not null))
        {
            if (json.MissingWriteApprovals() is { } missing) result.Add(Of(json.Id, missing, json.Approval!.TrustFlag));
        }
        return result;
    }

    private static ClientApproval Of(string id, IReadOnlyList<string> missing, bool wholeServer)
    {
        var state = missing.Count == 0 ? WriteApproval.Full
            : missing.Count < McpToolNames.Writing.Count ? WriteApproval.Partial
            : WriteApproval.None;
        return new ClientApproval(id, IntegrationRegistry.Find(id)?.DisplayName ?? id, state, missing, wholeServer);
    }

    /// <summary>
    /// Разрешить (<paramref name="allow"/>) инструменты записи без подтверждения во всех поддерживаемых клиентах или снова
    /// спрашивать подтверждение. Claude Code: разрешение добавляет все инструменты Offload (и чтения), запрет убирает
    /// только инструменты записи; остальные клиенты — только там, где Offload подключён. Возвращает то, что изменилось
    /// или не удалось; ошибка одного клиента не мешает остальным.
    /// </summary>
    public static IReadOnlyList<(string Id, IntegrationResult Result)> SetWriteTools(bool allow)
    {
        var results = new List<(string, IntegrationResult)>();
        if (ClientLocations.ClaudeCodeInstalled())
        {
            var claude = Guarded(() =>
            {
                var missing = ClaudeExtrasImpl.MissingTools();
                if (allow) return missing is { Count: 0 } ? null : ClaudeExtrasImpl.Preapprove(includeWriteTools: true);
                return missing is not null && missing.Count < McpToolNames.Writing.Count ? ClaudeExtrasImpl.RevokeWriteTools() : null;
            });
            if (claude is not null) results.Add((ClaudeCodeId, claude));
        }
        foreach (var json in IntegrationRegistry.All.OfType<JsonIntegration>().Where(j => j.Approval is not null))
        {
            if (Guarded(() => json.SetWriteApproval(allow)) is { } r) results.Add((json.Id, r));
        }
        foreach (var (id, r) in results)
            Log.Write(r.Ok ? LogLevel.Info : LogLevel.Warn, "Integrations", $"{id}: разрешения инструментов записи: {r.Message}");
        return results;
    }

    private static IntegrationResult? Guarded(Func<IntegrationResult?> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new IntegrationResult(false, L.F("Не удалось изменить разрешения: {0}", ex.Message));
        }
    }
}
