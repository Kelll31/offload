using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Util;
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
        new(false, L.T("Claude Code не найден на этом компьютере (нет папки ~/.claude и файла ~/.claude.json)."));

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

    /// <summary>Состояние нашего файла относительно шаблона текущей версии (нечитаемый файл — «чужой», его не трогаем).</summary>
    public static ManagedState StateOf(string path, string template, IReadOnlyCollection<string>? shippedHashes = null)
    {
        try
        {
            var snap = ConfigFile.Read(path);
            return ManagedContent.Inspect(snap.Exists ? snap.Text : null, template, shippedHashes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException)
        {
            return ManagedState.Foreign;
        }
    }

    /// <summary>
    /// Записать наш файл: в метку вписываются версия Offload и хэш содержимого (<see cref="ManagedContent.Stamp"/>),
    /// чтобы при следующем обновлении отличить устаревший файл от изменённого пользователем.
    /// </summary>
    public static IntegrationResult InstallManaged(string path, string template, string what)
    {
        if (!ClientLocations.ClaudeCodeInstalled()) return NotInstalled();
        try
        {
            if (File.Exists(path) && !IsManagedFile(path))
                return new IntegrationResult(false, L.F("Файл {0} уже существует и создан не Offload — он не изменён. Переименуйте или удалите его, чтобы установить {1}.", path, what));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = ManagedContent.Stamp(template);
            var res = ConfigFile.Edit(path, snap =>
            {
                if (snap.Exists && !snap.Text.Contains(ClaudeTexts.Marker, StringComparison.Ordinal))
                    throw new IOException(L.T("файл был создан другой программой"));
                return snap.Exists && ManagedContent.Inspect(snap.Text, template) == ManagedState.Current ? null : content;
            });
            return new IntegrationResult(true,
                res.Outcome == WriteOutcome.Unchanged ? L.F("Claude Code: {0} уже установлен ({1}).", what, path) : L.F("Claude Code: {0} установлен ({1}).", what, path),
                res.BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException)
        {
            return new IntegrationResult(false, L.F("Не удалось записать {0}: {1}", path, ex.Message));
        }
    }

    /// <summary>
    /// Удалить наш файл (с меткой): решение принимается по снимку, удаление — через <see cref="ConfigFile.Delete"/>
    /// (резервная копия; если файл изменился после чтения — отказ, файл не трогаем).
    /// </summary>
    public static IntegrationResult RemoveManaged(string path, string what, bool deleteEmptyDir)
    {
        try
        {
            var snap = ConfigFile.Read(path);
            if (!snap.Exists) return new IntegrationResult(true, L.F("Claude Code: {0} не был установлен.", what));
            if (!snap.Text.Contains(ClaudeTexts.Marker, StringComparison.Ordinal))
                return new IntegrationResult(false, L.F("Файл {0} создан не Offload — он оставлен без изменений.", path));
            var backup = ConfigFile.Delete(snap);
            if (deleteEmptyDir)
            {
                var dir = Path.GetDirectoryName(path)!;
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            return new IntegrationResult(true, L.F("Claude Code: {0} удалён.", what), backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException)
        {
            return new IntegrationResult(false, L.F("Не удалось удалить {0}: {1}", path, ex.Message));
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

    /// <summary>
    /// Инструменты записи Offload, которых нет в permissions.allow. null — разрешений Offload нет совсем (ни одного нашего
    /// имени) или файл не читается.
    /// </summary>
    public static IReadOnlyList<string>? MissingTools()
    {
        var allow = ReadAllow();
        if (allow is null || !McpToolNames.All.Select(McpToolNames.ClaudeCodeName).Any(allow.Contains)) return null;
        return McpToolNames.Writing.Where(t => !allow.Contains(McpToolNames.ClaudeCodeName(t))).ToList();
    }

    private static void ValidateSettings(JsoncEditor ed)
    {
        if (ed.IsEmpty) return;
        if (ed.KindAt([]) != JsoncKind.Object) throw new JsoncEditException(L.T("корень файла не является объектом JSON"));
        if (ed.KindAt(["permissions"]) is not null and not JsoncKind.Object and not JsoncKind.Null)
            throw new JsoncEditException(L.T("«permissions» не является объектом"));
        if (ed.KindAt(AllowPath) is not null and not JsoncKind.Array and not JsoncKind.Null)
            throw new JsoncEditException(L.T("«permissions.allow» не является массивом"));
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
            ApprovalsRecord.Save(wantedNames);
            var msg = okMessage;
            var allow = ReadAllow();
            var server = $"mcp__{AppInfo.McpServerId}";
            if (allow is not null && (allow.Contains(server) || allow.Contains(server + "__*")))
                msg += " " + L.F("Внимание: в настройках есть общее правило «{0}» (добавлено не Offload) — оно разрешает все инструменты сервера и оставлено без изменений.", server);
            return new IntegrationResult(true, msg, res.BackupPath);
        }
        catch (Exception ex) when (ex is JsoncParseException or ConfigReadException or JsoncEditException)
        {
            return new IntegrationResult(false,
                L.F("Не удалось разобрать файл {0}: {1}. Файл не изменён — исправьте ошибку в нём вручную и повторите.", path, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IntegrationResult(false, L.F("Не удалось записать файл {0}: {1}", path, ex.Message));
        }
    }

    public static IntegrationResult Preapprove(bool includeWriteTools)
    {
        if (!ClientLocations.ClaudeCodeInstalled()) return NotInstalled();
        IReadOnlyList<string> wanted = includeWriteTools ? McpToolNames.All : McpToolNames.ReadOnly;
        return SetApprovals(wanted, includeWriteTools
            ? L.T("Claude Code: инструменты Offload (чтение и запись) разрешены без подтверждения.")
            : L.T("Claude Code: инструменты Offload только для чтения разрешены без подтверждения; инструменты записи будут спрашивать разрешение."));
    }

    /// <summary>
    /// Добавить в permissions.allow недостающие инструменты чтения, ничего не убирая (уже выданные разрешения на запись
    /// остаются).
    /// </summary>
    public static IntegrationResult AllowReadTools()
    {
        if (!ClientLocations.ClaudeCodeInstalled()) return NotInstalled();
        var names = McpToolNames.ReadOnly.Select(McpToolNames.ClaudeCodeName).ToList();
        var r = EditAllow(current => (names.Where(n => !current.Contains(n)).ToList(), new HashSet<string>()),
            L.T("Claude Code: инструменты Offload только для чтения разрешены без подтверждения."));
        // Как и SetApprovals: предложенные имена запоминаются, чтобы RefreshApprovals не возвращал убранные пользователем.
        if (r.Ok) ApprovalsRecord.Save((ApprovalsRecord.Load() ?? []).Concat(names));
        return r;
    }

    /// <summary>
    /// Убрать из permissions.allow только инструменты записи Offload. Инструменты чтения не добавляются — даже те, что
    /// пользователь сам убрал из списка.
    /// </summary>
    public static IntegrationResult RevokeWriteTools()
    {
        if (!ClientLocations.ClaudeCodeInstalled()) return NotInstalled();
        var writing = McpToolNames.Writing.Select(McpToolNames.ClaudeCodeName).ToHashSet(StringComparer.Ordinal);
        return EditAllow(_ => (Array.Empty<string>(), writing), L.T("Claude Code: инструменты записи Offload снова спрашивают подтверждение."));
    }

    /// <summary>Точечная правка permissions.allow: добавить имена и убрать перечисленные (чужие правила не трогаются).</summary>
    private static IntegrationResult EditAllow(
        Func<IReadOnlyList<string>, (IReadOnlyList<string> Add, IReadOnlySet<string> Remove)> plan, string okMessage)
    {
        var path = SettingsFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var res = ConfigFile.Edit(path, snap =>
            {
                var ed = JsoncEditor.Parse(snap.Text);
                ValidateSettings(ed);
                var current = ed.TryGet(AllowPath, out var node) ? JsonTree.AsStringListLenient(node) : [];
                var (add, remove) = plan(current);
                var changed = false;
                if (current.Any(remove.Contains))
                    changed |= ed.RemoveFromArray(AllowPath, n => JsonTree.AsString(n) is { } s && remove.Contains(s));
                var toAdd = add.Where(n => !current.Contains(n)).Select(n => (JsonNode?)JsonValue.Create(n)).ToList();
                if (toAdd.Count > 0) changed |= ed.AppendToArray(AllowPath, toAdd);
                return changed ? ed.Text : null;
            });
            return new IntegrationResult(true, okMessage, res.BackupPath);
        }
        catch (Exception ex) when (ex is JsoncParseException or ConfigReadException or JsoncEditException)
        {
            return new IntegrationResult(false,
                L.F("Не удалось разобрать файл {0}: {1}. Файл не изменён — исправьте ошибку в нём вручную и повторите.", path, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IntegrationResult(false, L.F("Не удалось записать файл {0}: {1}", path, ex.Message));
        }
    }

    public static IntegrationResult Revoke() =>
        SetApprovals([], L.T("Claude Code: разрешения инструментов Offload удалены из настроек."));

    /// <summary>
    /// После обновления Offload: если разрешения включены, добавить в permissions.allow только новые инструменты
    /// <b>только для чтения</b>, появившиеся с последней записи (список записанных имён хранит <see cref="ApprovalsRecord"/>).
    /// Имена, которые пользователь сам убрал из списка, не возвращаются. Нет записи (первый запуск этой версии после 1.0.0) —
    /// ничего не добавляется, только запоминается текущее состояние. Новые инструменты записи сами не добавляются никогда:
    /// согласие на них — переключатель «…и инструменты записи» на странице «Интеграции» (он снимается, пока разрешены не все).
    /// null — менять нечего.
    /// </summary>
    public static IntegrationResult? RefreshApprovals()
    {
        if (!ClientLocations.ClaudeCodeInstalled()) return null;
        var allow = ReadAllow();
        if (allow is null) return null;
        var ours = McpToolNames.All.Select(McpToolNames.ClaudeCodeName).ToHashSet(StringComparer.Ordinal);
        var written = ApprovalsRecord.Load();
        if (written is null)
        {
            // Первый запуск с учётом разрешений: ничего не добавляем. Все известные сейчас инструменты считаем уже
            // «предложенными» — иначе при следующем запуске недостающие в списке (в т.ч. убранные пользователем) добавились бы.
            ApprovalsRecord.Save(ours);
            return null;
        }
        if (!written.Any(allow.Contains)) return null; // разрешения выключены
        var added = McpToolNames.ReadOnly.Select(McpToolNames.ClaudeCodeName).Where(n => !written.Contains(n) && !allow.Contains(n)).ToList();
        if (added.Count == 0) return null;
        return AppendApprovals(added, written);
    }

    private static IntegrationResult AppendApprovals(IReadOnlyList<string> names, IReadOnlySet<string> written)
    {
        var path = SettingsFile;
        try
        {
            var res = ConfigFile.Edit(path, snap =>
            {
                var ed = JsoncEditor.Parse(snap.Text);
                ValidateSettings(ed);
                var current = ed.TryGet(AllowPath, out var node) ? JsonTree.AsStringListLenient(node) : [];
                var toAdd = names.Where(n => !current.Contains(n)).Select(n => (JsonNode?)JsonValue.Create(n)).ToList();
                return toAdd.Count > 0 && ed.AppendToArray(AllowPath, toAdd) ? ed.Text : null;
            });
            ApprovalsRecord.Save(written.Concat(names));
            return new IntegrationResult(true,
                L.F("Claude Code: без подтверждения разрешены новые инструменты Offload: {0}.", string.Join(", ", names)), res.BackupPath);
        }
        catch (Exception ex) when (ex is JsoncParseException or ConfigReadException or JsoncEditException)
        {
            return new IntegrationResult(false,
                L.F("Не удалось разобрать файл {0}: {1}. Файл не изменён — исправьте ошибку в нём вручную и повторите.", path, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IntegrationResult(false, L.F("Не удалось записать файл {0}: {1}", path, ex.Message));
        }
    }
}

/// <summary>
/// Какие имена инструментов Offload уже записал в permissions.allow или учёл при первом запуске (claude-approvals.json
/// в папке данных Offload). Нужен, чтобы при обновлении добавлять только новые инструменты и не возвращать убранные пользователем.
/// </summary>
internal static class ApprovalsRecord
{
    internal static string FilePath => Path.Combine(AppPaths.DataDir, "claude-approvals.json");

    public static HashSet<string>? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(FilePath));
            return JsonTree.AsStringListLenient(node?["names"]).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static void Save(IEnumerable<string> names)
    {
        try
        {
            var list = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var json = new JsonObject { ["names"] = JsonIntegration.Strings(list) }.ToJsonString();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            FileUtil.WriteAllTextAtomic(FilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Integrations", $"Не удалось сохранить список разрешений: {ex.Message}");
        }
    }
}
