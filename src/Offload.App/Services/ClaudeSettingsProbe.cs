using System.Text.Json;
using Offload.Core;
using Offload.Core.Util;

namespace Offload.App.Services;

/// <summary>
/// Чтение (только чтение!) ~/.claude/settings.json, чтобы понять, разрешены ли без подтверждения
/// инструменты записи Offload. В контракте ClaudeCodeExtras есть лишь общий признак AreToolsPreapproved.
/// </summary>
internal static class ClaudeSettingsProbe
{
    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>Есть ли в permissions.allow хотя бы один инструмент записи Offload (или весь сервер целиком).</summary>
    public static bool AreWriteToolsAllowed()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath), Json.LenientDocument);
            if (!doc.RootElement.TryGetProperty("permissions", out var perms) || perms.ValueKind != JsonValueKind.Object) return false;
            if (!perms.TryGetProperty("allow", out var allow) || allow.ValueKind != JsonValueKind.Array) return false;
            var writes = McpToolNames.Writing.Select(McpToolNames.ClaudeCodeName).ToHashSet(StringComparer.Ordinal);
            var server = $"mcp__{AppInfo.McpServerId}";
            foreach (var e in allow.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) continue;
                var s = e.GetString() ?? "";
                if (writes.Contains(s) || s == server || s == server + "__*") return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
