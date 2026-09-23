using System.Text.Json.Nodes;
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

    /// <summary>Дополнение к сообщению об успешном подключении (например, предупреждение о дубликатах).</summary>
    public Func<string?>? ExtraRegisterNote { get; init; }

    public override string? ConfigPath => Targets().FirstOrDefault() ?? KnownFiles?.Invoke().FirstOrDefault();

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

    private bool IsOurs(EntryInfo? info, McpServerSpec? spec) =>
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
            return new FileProbe(path, IsOurs(info, spec) ? ProbeState.Ours : ProbeState.Foreign, info ?? new EntryInfo(null, []));
        }
        catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
        {
            return new FileProbe(path, ProbeState.Error, Error: msg);
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
        var fresh = Entry(spec);
        if (existing is not JsonObject old || !IsOurs(ReadEntry(old), spec)) return fresh;
        var merged = (JsonObject)old.DeepClone();
        foreach (var k in DroppedKeys) merged.Remove(k);
        foreach (var (k, v) in fresh)
        {
            if (OwnedKeys.Contains(k) || !merged.ContainsKey(k)) merged[k] = v?.DeepClone();
        }
        return merged;
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

    public override Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default) =>
        Task.FromResult(UnregisterFromFiles());

    protected IReadOnlyList<string> AllFiles() =>
        (KnownFiles?.Invoke() ?? []).Concat(Targets()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
