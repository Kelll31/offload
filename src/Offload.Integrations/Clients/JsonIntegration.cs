using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Clients;

internal enum EntryShape
{
    /// <summary>{"command": "exe", "args": [...]}.</summary>
    CommandAndArgs,
    /// <summary>{"command": ["exe", ...args]} (OpenCode / Kilo).</summary>
    CommandArray,
}

/// <summary>
/// Ключ записи, которым клиент разрешает вызовы без подтверждения: список имён инструментов (autoApprove, alwaysAllow)
/// или флаг доверия всему серверу (<paramref name="TrustFlag"/>: trust у Gemini CLI — все инструменты сразу).
/// </summary>
internal sealed record ApprovalKey(string Key, bool TrustFlag = false);

/// <summary>
/// Клиент с JSON/JSONC-конфигом: запись «offload» внутри объекта <see cref="Container"/>.
/// Правка точечная (JsoncEditor), с резервной копией; чужие ключи и комментарии сохраняются.
/// </summary>
internal class JsonIntegration(string id, string displayName, string? hint) : IntegrationBase
{
    public override string Id => id;
    public override string DisplayName => displayName;
    public override string? PostRegisterHint => hint is null ? null : L.T(hint);

    /// <summary>Установлен ли клиент.</summary>
    public required Func<bool> Detect { get; init; }

    /// <summary>Файлы, в которые прописываем сервер (обычно один).</summary>
    public required Func<IReadOnlyList<string>> Targets { get; init; }

    /// <summary>Все известные файлы (для отключения, в т.ч. устаревшие расположения). По умолчанию = Targets.</summary>
    public Func<IReadOnlyList<string>>? KnownFiles { get; init; }

    /// <summary>Путь к объекту со списком серверов: ["mcpServers"], ["servers"], ["context_servers"], ["mcp"].</summary>
    public required string[] Container { get; init; }

    public required Func<McpServerSpec, JsonObject> Entry { get; init; }

    public EntryShape Shape { get; init; } = EntryShape.CommandAndArgs;

    /// <summary>Ключи, которые мы всегда перезаписываем в своей записи; остальные (disabled, timeout, env…) пользователь может менять.</summary>
    public string[] OwnedKeys { get; init; } = ["type", "command", "args"];

    /// <summary>Ключи, которые удаляем из своей записи при обновлении (устаревшие формы).</summary>
    public string[] DroppedKeys { get; init; } = [];

    /// <summary>
    /// Ключи со значениями по умолчанию от Offload (таймаут, список автоодобрения), которые обновляются при смене умолчаний,
    /// если в записи осталось прежнее значение Offload (<see cref="IsStaleDefault"/>); значения пользователя не трогаются.
    /// </summary>
    public string[] RefreshableKeys { get; init; } = [];

    /// <summary>
    /// Прежние значения по умолчанию (JSON-текст) для ключей из <see cref="RefreshableKeys"/>: если в записи осталось одно из них,
    /// это значение записал Offload — его можно заменить новым. Меняя умолчание, добавьте сюда старое значение.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> PreviousDefaults { get; init; } = new Dictionary<string, string[]>();

    /// <summary>
    /// Как клиент разрешает инструменты без подтверждения (см. <see cref="ToolApprovals"/>). null — не умеет
    /// или Offload этим не управляет.
    /// </summary>
    public ApprovalKey? Approval { get; init; }

    /// <summary>Дополнение к сообщению об успешном подключении (например, предупреждение о дубликатах).</summary>
    public Func<string?>? ExtraRegisterNote { get; init; }

    public override string? ConfigPath => First(Targets()) ?? First(KnownFiles?.Invoke());

    private static string? First(IReadOnlyList<string>? list) => list is { Count: > 0 } ? list[0] : null;

    public override bool IsClientInstalled()
    {
        try
        {
            return Detect();
        }
        catch (Exception ex)
        {
            Log.Warn("Integrations", $"{Id}: ошибка определения установки: {ex.Message}");
            return false;
        }
    }

    private string[] EntryPath(string name) => [.. Container, name];

    // ------------------------------------------------------------------ чтение

    internal EntryInfo? ReadEntry(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        if (Shape == EntryShape.CommandArray)
        {
            var list = JsonTree.AsStringList(o["command"]);
            if (list is not null && list.Count > 0) return new EntryInfo(list[0], list.Skip(1).ToList());
            var single = JsonTree.AsString(o["command"]);
            return new EntryInfo(single, JsonTree.AsStringList(o["args"]) ?? []);
        }
        var cmd = JsonTree.AsString(o["command"]);
        var argsNode = o["args"];
        if (cmd is null && o["transport"] is JsonObject t)
        {
            cmd = JsonTree.AsString(t["command"]);
            argsNode = t["args"];
        }
        return new EntryInfo(cmd, JsonTree.AsStringList(argsNode) ?? []);
    }

    private static bool IsOurs(EntryInfo? info, McpServerSpec? spec) =>
        info?.Command is not null && CommandPath.IsOffload(info.Command, spec?.Command);

    /// <summary>Проверка, что файл имеет ожидаемую структуру (корень и контейнер — объекты).</summary>
    private void ValidateStructure(JsoncEditor ed)
    {
        if (ed.IsEmpty) return;
        if (ed.KindAt([]) != JsoncKind.Object) throw new JsoncEditException(L.T("корень файла не является объектом JSON"));
        for (var i = 1; i <= Container.Length; i++)
        {
            var kind = ed.KindAt(Container.Take(i).ToArray());
            if (kind is not null and not JsoncKind.Object and not JsoncKind.Null)
                throw new JsoncEditException(L.F("«{0}» не является объектом", string.Join('.', Container.Take(i))));
        }
    }

    internal FileProbe Probe(string path, McpServerSpec? spec, string name)
    {
        try
        {
            if (!File.Exists(path)) return new FileProbe(path, ProbeState.FileMissing);
            var snap = ConfigFile.Read(path);
            var ed = JsoncEditor.Parse(snap.Text);
            ValidateStructure(ed);
            if (!ed.TryGet(EntryPath(name), out var node)) return new FileProbe(path, ProbeState.Absent);
            var info = ReadEntry(node);
            if (!IsOurs(info, spec)) return new FileProbe(path, ProbeState.Foreign, info ?? new EntryInfo(null, []));
            if (spec is not null && HasStaleDefaults(node, spec)) info = info! with { StaleDefaults = true };
            return new FileProbe(path, ProbeState.Ours, info);
        }
        catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
        {
            return new FileProbe(path, ProbeState.Error, Error: msg, ErrorKind: KindOf(ex));
        }
    }

    public override IntegrationStatus GetStatus(McpServerSpec spec)
    {
        if (!IsClientInstalled()) return IntegrationStatus.ClientNotFound;
        var targets = Targets();
        if (targets.Count == 0) return IntegrationStatus.ClientNotFound;
        return Aggregate(targets.Select(p => Probe(p, spec, spec.Name).ToStatus(spec)).ToList());
    }

    // ------------------------------------------------------------------ запись

    /// <summary>Новая запись; если прежняя — наша, сохраняем пользовательские ключи (disabled, timeout, alwaysAllow…).</summary>
    internal JsonObject MergeEntry(JsonNode? existing, McpServerSpec spec)
    {
        var entry = MergeWithExisting(existing, spec);
        // Согласие на инструменты записи при подключении добавляется и к существующей записи; снимается — только явно (SetWriteApproval).
        if (spec.ApproveWriteTools && Approval is { } a && WithWriteTools(entry[a.Key], allow: true, a.TrustFlag) is { } approved)
            entry[a.Key] = approved;
        return entry;
    }

    private JsonObject MergeWithExisting(JsonNode? existing, McpServerSpec spec)
    {
        var fresh = Entry(spec);
        if (existing is not JsonObject old || !IsOurs(ReadEntry(old), spec)) return fresh;
        var merged = (JsonObject)old.DeepClone();
        foreach (var k in DroppedKeys) merged.Remove(k);
        foreach (var (k, v) in fresh)
        {
            if (OwnedKeys.Contains(k) || !merged.ContainsKey(k)) merged[k] = v?.DeepClone();
            else if (RefreshableKeys.Contains(k) && IsStaleDefault(k, merged[k], v)) merged[k] = RefreshedDefault(merged[k], v);
        }
        return merged;
    }

    // ------------------------------------------------------------------ инструменты записи без подтверждения

    /// <summary>
    /// Новое значение ключа разрешений: <paramref name="allow"/> — добавить все инструменты записи Offload (список)
    /// или trust = true; иначе — убрать их (trust = false). null — менять нечего или значение не того типа (не трогаем).
    /// Отсутствующий список при разрешении создаётся из всех инструментов Offload.
    /// </summary>
    internal static JsonNode? WithWriteTools(JsonNode? current, bool allow, bool trustFlag)
    {
        if (trustFlag)
        {
            var trusted = current is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            return trusted == allow || (current is not null and not JsonValue) ? null : JsonValue.Create(allow);
        }
        if (current is null) return allow ? Strings(McpToolNames.All) : null;
        if (current is not JsonArray) return null;
        var have = JsonTree.AsStringListLenient(current);
        if (allow)
        {
            var missing = McpToolNames.Writing.Where(t => !have.Contains(t)).ToList();
            return missing.Count == 0 ? null : Strings([.. have, .. missing]);
        }
        return have.Any(McpToolNames.Writing.Contains) ? Strings(have.Where(t => !McpToolNames.Writing.Contains(t))) : null;
    }

    /// <summary>Инструменты записи Offload, не разрешённые значением ключа (все — если ключа нет или он не того типа).</summary>
    private static IReadOnlyList<string> MissingWriteTools(JsonNode? value, bool trustFlag)
    {
        if (trustFlag) return value is JsonValue v && v.TryGetValue<bool>(out var b) && b ? [] : McpToolNames.Writing;
        var have = value is JsonArray ? JsonTree.AsStringListLenient(value) : [];
        return McpToolNames.Writing.Where(t => !have.Contains(t)).ToList();
    }

    /// <summary>
    /// Какие инструменты записи Offload ещё спрашивают подтверждение в наших записях целевых файлов (объединение по файлам).
    /// null — клиент не умеет, не установлен или нашей записи нет ни в одном файле.
    /// </summary>
    internal IReadOnlyList<string>? MissingWriteApprovals()
    {
        if (Approval is not { } a || !IsClientInstalled()) return null;
        List<string>? missing = null;
        foreach (var path in Targets())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var ed = JsoncEditor.Parse(ConfigFile.Read(path).Text);
                if (!ed.TryGet(EntryPath(ServerName), out var node) || node is not JsonObject o || !IsOurs(ReadEntry(o), null)) continue;
                missing ??= [];
                missing.AddRange(MissingWriteTools(o[a.Key], a.TrustFlag).Where(t => !missing.Contains(t)));
            }
            catch (Exception ex) when (DescribeFailure(path, ex) is not null)
            {
                // Нечитаемый файл показывается статусом интеграции; здесь его пропускаем.
            }
        }
        return missing;
    }

    /// <summary>
    /// Разрешить (<paramref name="allow"/>) или снова спрашивать подтверждение для инструментов записи Offload в нашей
    /// записи каждого целевого файла. Остальные ключи и чужие записи не трогаются. null — менять было нечего.
    /// </summary>
    internal IntegrationResult? SetWriteApproval(bool allow)
    {
        if (Approval is not { } a || !IsClientInstalled()) return null;
        var messages = new List<string>();
        var ok = true;
        string? firstBackup = null;
        foreach (var path in Targets())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var res = ConfigFile.Edit(path, snap =>
                {
                    var ed = JsoncEditor.Parse(snap.Text);
                    ValidateStructure(ed);
                    var entryPath = EntryPath(ServerName);
                    if (!ed.TryGet(entryPath, out var node) || node is not JsonObject o || !IsOurs(ReadEntry(o), null)) return null;
                    var value = WithWriteTools(o[a.Key], allow, a.TrustFlag);
                    return value is not null && ed.Set([.. entryPath, a.Key], value) ? ed.Text : null;
                });
                if (res.Outcome != WriteOutcome.Written) continue;
                firstBackup ??= res.BackupPath;
                messages.Add(allow
                    ? L.F("{0}: инструменты записи Offload разрешены без подтверждения ({1}).", DisplayName, path)
                    : L.F("{0}: инструменты записи Offload снова спрашивают подтверждение ({1}).", DisplayName, path));
            }
            catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
            {
                ok = false;
                messages.Add(msg);
                Log.Warn("Integrations", $"{Id}: {msg}");
            }
        }
        return messages.Count > 0 ? new IntegrationResult(ok, string.Join(Environment.NewLine, messages), firstBackup) : null;
    }

    /// <summary>Есть ли в нашей записи устаревшие значения по умолчанию.</summary>
    internal bool HasStaleDefaults(JsonNode? node, McpServerSpec spec)
    {
        if (RefreshableKeys.Length == 0 || node is not JsonObject o) return false;
        var fresh = Entry(spec);
        return RefreshableKeys.Any(k => o.ContainsKey(k) && fresh.ContainsKey(k) && IsStaleDefault(k, o[k], fresh[k]));
    }

    /// <summary>
    /// Значение ключа по умолчанию устарело: для списка имён инструментов — список целиком из инструментов Offload, и в нём
    /// нет части текущих (появились новые инструменты чтения); для прочих — совпадает с одним из <see cref="PreviousDefaults"/>.
    /// </summary>
    internal bool IsStaleDefault(string key, JsonNode? current, JsonNode? fresh)
    {
        if (current is null || fresh is null || JsonNode.DeepEquals(current, fresh)) return false;
        if (fresh is JsonArray && current is JsonArray)
        {
            var have = JsonTree.AsStringList(current);
            var want = JsonTree.AsStringList(fresh);
            if (have is null || want is null) return false;
            return have.All(McpToolNames.All.Contains) && want.Any(w => !have.Contains(w));
        }
        var text = current.ToJsonString();
        return PreviousDefaults.TryGetValue(key, out var old) && old.Contains(text, StringComparer.Ordinal);
    }

    /// <summary>Новое значение устаревшего умолчания: списки объединяются (добавленное пользователем остаётся), прочее заменяется.</summary>
    private static JsonNode? RefreshedDefault(JsonNode? current, JsonNode? fresh)
    {
        if (current is JsonArray && fresh is JsonArray && JsonTree.AsStringList(current) is { } have && JsonTree.AsStringList(fresh) is { } want)
            return Strings(have.Concat(want.Where(w => !have.Contains(w))));
        return fresh?.DeepClone();
    }

    public override Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default) =>
        Task.FromResult(RegisterInFiles(spec));

    protected IntegrationResult RegisterInFiles(McpServerSpec spec)
    {
        if (!IsClientInstalled()) return NotFound();
        var targets = Targets();
        if (targets.Count == 0) return NotFound();

        var messages = new List<string>();
        var ok = true;
        string? firstBackup = null;
        foreach (var path in targets)
        {
            try
            {
                string? replacedForeign = null;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var res = ConfigFile.Edit(path, snap =>
                {
                    replacedForeign = null;
                    var ed = JsoncEditor.Parse(snap.Text);
                    ValidateStructure(ed);
                    var entryPath = EntryPath(spec.Name);
                    ed.TryGet(entryPath, out var existing);
                    if (existing is not null)
                    {
                        var info = ReadEntry(existing);
                        if (!IsOurs(info, spec)) replacedForeign = info?.Command ?? "";
                    }
                    return ed.Set(entryPath, MergeEntry(existing, spec)) ? ed.Text : null;
                });
                firstBackup ??= res.BackupPath;
                if (res.Outcome == WriteOutcome.Unchanged)
                {
                    messages.Add(MsgAlready(path));
                }
                else
                {
                    messages.Add(MsgRegistered(path));
                    if (replacedForeign is not null) messages.Add(MsgReplacedForeign(replacedForeign.Length == 0 ? null : replacedForeign));
                }
            }
            catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
            {
                ok = false;
                messages.Add(msg);
                Log.Warn("Integrations", $"{Id}: {msg}");
            }
        }
        if (ok && ExtraRegisterNote?.Invoke() is { } note) messages.Add(note);
        return new IntegrationResult(ok, string.Join(Environment.NewLine, messages), firstBackup);
    }

    internal override Task<IntegrationResult?> RepairPathAsync(McpServerSpec spec, CancellationToken ct = default) =>
        Task.FromResult(RepairPathInFiles(spec));

    /// <summary>
    /// В каждом целевом файле, где наша запись указывает на отсутствующий Offload.exe, заменить только путь к exe
    /// (ключ command или первый элемент массива command). Решение перепроверяется по снимку внутри <see cref="ConfigFile.Edit"/>.
    /// </summary>
    protected IntegrationResult? RepairPathInFiles(McpServerSpec spec)
    {
        if (!IsClientInstalled()) return null;
        var messages = new List<string>();
        var ok = true;
        string? firstBackup = null;
        foreach (var path in Targets())
        {
            if (Probe(path, spec, spec.Name).Need(spec) != RepairNeed.PathMoved) continue;
            try
            {
                var res = ConfigFile.Edit(path, snap =>
                {
                    var ed = JsoncEditor.Parse(snap.Text);
                    ValidateStructure(ed);
                    var entryPath = EntryPath(spec.Name);
                    if (!ed.TryGet(entryPath, out var node) || node is not JsonObject o) return null;
                    var info = ReadEntry(o);
                    if (!IsOurs(info, spec) || IntegrationBase.NeedOf(info!, spec) != RepairNeed.PathMoved) return null;
                    var (keyPath, value) = CommandLocation(entryPath, o, spec);
                    return ed.Set(keyPath, value) ? ed.Text : null;
                });
                firstBackup ??= res.BackupPath;
                if (res.Outcome == WriteOutcome.Written) messages.Add(MsgRegistered(path));
            }
            catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
            {
                ok = false;
                messages.Add(msg);
                Log.Warn("Integrations", $"{Id}: {msg}");
            }
        }
        // Ничего не записано и ошибок нет (файл успели изменить) — считаем, что исправлять было нечего.
        return messages.Count > 0 ? new IntegrationResult(ok, string.Join(Environment.NewLine, messages), firstBackup) : null;
    }

    /// <summary>Где в записи лежит путь к exe и его новое значение (форма записи сохраняется).</summary>
    private (string[] Path, JsonNode Value) CommandLocation(string[] entryPath, JsonObject entry, McpServerSpec spec)
    {
        if (Shape == EntryShape.CommandArray && JsonTree.AsStringList(entry["command"]) is { Count: > 0 } list)
            return ([.. entryPath, "command"], Strings([spec.Command, .. list.Skip(1)]));
        if (JsonTree.AsString(entry["command"]) is null && entry["transport"] is JsonObject t && JsonTree.AsString(t["command"]) is not null)
            return ([.. entryPath, "transport", "command"], JsonValue.Create(spec.Command));
        return ([.. entryPath, "command"], JsonValue.Create(spec.Command));
    }

    public override Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default) =>
        Task.FromResult(UnregisterFromFiles());

    protected IReadOnlyList<string> AllFiles() =>
        (KnownFiles?.Invoke() ?? []).Concat(Targets()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal override IReadOnlyList<FileProbe> ProbeEntries(McpServerSpec spec) =>
        IsClientInstalled() ? Targets().Select(p => Probe(p, spec, spec.Name)).ToList() : [];

    internal override IReadOnlyList<string> WatchedFiles() => Targets();

    internal bool HasOurEntryAnywhere() =>
        AllFiles().Any(p => Probe(p, null, ServerName).State == ProbeState.Ours);

    protected IntegrationResult UnregisterFromFiles()
    {
        var messages = new List<string>();
        var ok = true;
        var removed = false;
        string? firstBackup = null;
        foreach (var path in AllFiles())
        {
            var probe = Probe(path, null, ServerName);
            switch (probe.State)
            {
                case ProbeState.FileMissing:
                case ProbeState.Absent:
                    continue;
                case ProbeState.Error:
                    ok = false;
                    messages.Add(probe.Error!);
                    continue;
                case ProbeState.Foreign:
                    ok = false;
                    messages.Add(MsgForeign(path, probe.Entry?.Command));
                    continue;
            }
            try
            {
                var res = ConfigFile.Edit(path, snap =>
                {
                    var ed = JsoncEditor.Parse(snap.Text);
                    var entryPath = EntryPath(ServerName);
                    // Файл мог измениться — ещё раз убеждаемся, что запись наша.
                    if (!ed.TryGet(entryPath, out var node) || !IsOurs(ReadEntry(node), null)) return null;
                    return ed.Remove(entryPath) ? ed.Text : null;
                });
                if (res.Outcome == WriteOutcome.Written)
                {
                    removed = true;
                    firstBackup ??= res.BackupPath;
                    messages.Add(MsgUnregistered(path));
                }
            }
            catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
            {
                ok = false;
                messages.Add(msg);
                Log.Warn("Integrations", $"{Id}: {msg}");
            }
        }
        if (messages.Count == 0 && !removed) messages.Add(MsgNotRegistered);
        return new IntegrationResult(ok, string.Join(Environment.NewLine, messages), firstBackup);
    }

    // ------------------------------------------------------------------ помощники для описаний клиентов

    internal static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    internal static JsonObject EnvObject(McpServerSpec spec)
    {
        var o = new JsonObject();
        foreach (var (k, v) in spec.Env) o[k] = v;
        return o;
    }
}
