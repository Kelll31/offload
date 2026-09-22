using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Claude;

/// <summary>Реализация ClaudeCodeExtras: наши файлы в ~/.claude (с меткой) и разрешения в ~/.claude/settings.json.</summary>
internal static class ClaudeExtrasImpl
{
    private static readonly string[] AllowPath = ["permissions", "allow"];

    public static string SkillFile => Path.Combine(ClientLocations.ClaudeHome, "skills", "offload", "SKILL.md");
    public static string RuleFile => Path.Combine(ClientLocations.ClaudeHome, "rules", "offload.md");
    public static string AgentFile => Path.Combine(ClientLocations.ClaudeHome, "agents", "offload-runner.md");
    public static string SettingsFile => ClientLocations.ClaudeSettings;

    private static IntegrationResult NotInstalled() =>
        new(false, "Claude Code не найден на этом компьютере (нет папки ~/.claude и файла ~/.claude.json).");

    // ---------------------------------------------------------------- файлы с меткой

    public static bool IsManagedFile(string path)
    {
        try
        {
            return File.Exists(path) && ConfigFile.Read(path).Text.Contains(ClaudeTexts.Marker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException)
        {
            return false;
        }
    }

    public static IntegrationResult InstallManaged(string path, string content, string what)
    {
        if (!ClientLocations.ClaudeCodeInstalled()) return NotInstalled();
        try
        {
            if (File.Exists(path) && !IsManagedFile(path))
                return new IntegrationResult(false, $"Файл {path} уже существует и создан не Offload — он не изменён. Переименуйте или удалите его, чтобы установить {what}.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var res = ConfigFile.Edit(path, snap =>
            {
                if (snap.Exists && !snap.Text.Contains(ClaudeTexts.Marker, StringComparison.Ordinal))
                    throw new IOException("файл был создан другой программой");
                return snap.Exists && snap.Text == content ? null : content;
            });
            return new IntegrationResult(true,
                res.Outcome == WriteOutcome.Unchanged ? $"Claude Code: {what} уже установлен ({path})." : $"Claude Code: {what} установлен ({path}).",
                res.BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException)
        {
            return new IntegrationResult(false, $"Не удалось записать {path}: {ex.Message}");
        }
    }

    public static IntegrationResult RemoveManaged(string path, string what, bool deleteEmptyDir)
    {
        try
        {
            if (!File.Exists(path)) return new IntegrationResult(true, $"Claude Code: {what} не был установлен.");
            if (!IsManagedFile(path))
                return new IntegrationResult(false, $"Файл {path} создан не Offload — он оставлен без изменений.");
            var snap = ConfigFile.Read(path);
            File.Delete(snap.Path);
            Log.Info("Integrations", $"Удалён файл {path}");
            if (deleteEmptyDir)
            {
                var dir = Path.GetDirectoryName(path)!;
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            return new IntegrationResult(true, $"Claude Code: {what} удалён.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException)
        {
            return new IntegrationResult(false, $"Не удалось удалить {path}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- разрешения

    private static HashSet<string>? ReadAllow()
    {
        var path = SettingsFile;
        if (!File.Exists(path)) return [];
        try
        {
            var ed = JsoncEditor.Parse(ConfigFile.Read(path).Text);
            if (!ed.TryGet(AllowPath, out var node)) return [];
            return JsonTree.AsStringListLenient(node).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsoncParseException or ConfigReadException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool AllPresent(IEnumerable<string> tools)
    {
        var allow = ReadAllow();
        return allow is not null && tools.Select(McpToolNames.ClaudeCodeName).All(allow.Contains);
    }

    private static void ValidateSettings(JsoncEditor ed)
    {
        if (ed.IsEmpty) return;
        if (ed.KindAt([]) != JsoncKind.Object) throw new JsoncEditException("корень файла не является объектом JSON");
        if (ed.KindAt(["permissions"]) is not null and not JsoncKind.Object and not JsoncKind.Null)
            throw new JsoncEditException("«permissions» не является объектом");
        if (ed.KindAt(AllowPath) is not null and not JsoncKind.Array and not JsoncKind.Null)
            throw new JsoncEditException("«permissions.allow» не является массивом");
    }

    /// <summary>Привести наши явные имена в permissions.allow к набору <paramref name="wanted"/> (чужие правила не трогаем).</summary>
    public static IntegrationResult SetApprovals(IReadOnlyList<string> wanted, string okMessage)
    {
        var path = SettingsFile;
        var wantedNames = wanted.Select(McpToolNames.ClaudeCodeName).ToList();
        var ourNames = McpToolNames.All.Select(McpToolNames.ClaudeCodeName).ToHashSet(StringComparer.Ordinal);
        var unwanted = ourNames.Except(wantedNames).ToHashSet(StringComparer.Ordinal);
        try
        {
            if (wantedNames.Count > 0) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            else if (!File.Exists(path)) return new IntegrationResult(true, okMessage);

            var res = ConfigFile.Edit(path, snap =>
            {
                var ed = JsoncEditor.Parse(snap.Text);
                ValidateSettings(ed);
                var current = ed.TryGet(AllowPath, out var node) ? JsonTree.AsStringListLenient(node) : [];
                var changed = false;
                if (current.Any(unwanted.Contains))
                    changed |= ed.RemoveFromArray(AllowPath, n => JsonTree.AsString(n) is { } s && unwanted.Contains(s));
                var toAdd = wantedNames.Where(n => !current.Contains(n)).Select(n => (JsonNode?)JsonValue.Create(n)).ToList();
                if (toAdd.Count > 0) changed |= ed.AppendToArray(AllowPath, toAdd);
                return changed ? ed.Text : null;
            });
            var msg = okMessage;
            var allow = ReadAllow();
            var server = $"mcp__{AppInfo.McpServerId}";
            if (allow is not null && (allow.Contains(server) || allow.Contains(server + "__*")))
                msg += $" Внимание: в настройках есть общее правило «{server}» (добавлено не Offload) — оно разрешает все инструменты сервера и оставлено без изменений.";
            return new IntegrationResult(true, msg, res.BackupPath);
        }
        catch (Exception ex) when (ex is JsoncParseException or ConfigReadException or JsoncEditException)
        {
            return new IntegrationResult(false,
                $"Не удалось разобрать файл {path}: {ex.Message}. Файл не изменён — исправьте ошибку в нём вручную и повторите.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IntegrationResult(false, $"Не удалось записать файл {path}: {ex.Message}");
        }
    }

    public static IntegrationResult Preapprove(bool includeWriteTools)
    {
        if (!ClientLocations.ClaudeCodeInstalled()) return NotInstalled();
        IReadOnlyList<string> wanted = includeWriteTools ? McpToolNames.All : McpToolNames.ReadOnly;
        return SetApprovals(wanted, includeWriteTools
            ? "Claude Code: инструменты Offload (чтение и запись) разрешены без подтверждения."
            : "Claude Code: инструменты Offload только для чтения разрешены без подтверждения; инструменты записи будут спрашивать разрешение.");
    }

    public static IntegrationResult Revoke() =>
        SetApprovals([], "Claude Code: разрешения инструментов Offload удалены из настроек.");
}
