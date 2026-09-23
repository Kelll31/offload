using System.Text;
using System.Text.RegularExpressions;
using Offload.Core.Logging;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Clients;

/// <summary>
/// Continue: отдельный блок ~/.continue/mcpServers/offload.yaml (Continue загружает глобальные блоки сам),
/// поэтому config.yaml пользователя не трогаем. Отключение — удаление нашего файла.
/// </summary>
internal sealed partial class ContinueIntegration : IntegrationBase
{
    internal const string Marker = "x-offload: managed";

    public override string Id => "continue";
    public override string DisplayName => "Continue";
    public override string? PostRegisterHint => L.T("Перезагрузите окно IDE. MCP-инструменты в Continue работают только в режиме Agent.");

    private static string ContinueDir => Path.Combine(IntegrationEnvironment.UserProfile, ".continue");

    public override string? ConfigPath => Path.Combine(ContinueDir, "mcpServers", "offload.yaml");

    public override bool IsClientInstalled() => Directory.Exists(ContinueDir);

    /// <summary>Содержимое нашего блока. Строки YAML — в одинарных кавычках (обратные слеши и кириллица без экранирования).</summary>
    internal static string BuildYaml(McpServerSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Marker).Append(" (created by Offload; this file is rewritten or deleted automatically)\n");
        sb.Append("name: Offload\n");
        sb.Append("version: 0.0.1\n");
        sb.Append("schema: v1\n");
        sb.Append("mcpServers:\n");
        sb.Append("  - name: ").Append(Quote(spec.Name)).Append('\n');
        sb.Append("    type: stdio\n");
        sb.Append("    command: ").Append(Quote(spec.Command)).Append('\n');
        if (spec.Args.Count == 0)
        {
            sb.Append("    args: []\n");
        }
        else
        {
            sb.Append("    args:\n");
            foreach (var a in spec.Args) sb.Append("      - ").Append(Quote(a)).Append('\n');
        }
        if (spec.Env.Count > 0)
        {
            sb.Append("    env:\n");
            foreach (var (k, v) in spec.Env) sb.Append("      ").Append(Quote(k)).Append(": ").Append(Quote(v)).Append('\n');
        }
        return sb.ToString();
    }

    internal static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Разбор command/args из блока (наш формат или простой ручной).</summary>
    internal static EntryInfo? ParseYaml(string text)
    {
        string? command = null;
        var args = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = KeyLine().Match(lines[i]);
            if (!m.Success) continue;
            var key = m.Groups["key"].Value;
            var value = m.Groups["value"].Value.Trim();
            if (key == "command" && command is null)
            {
                command = Unquote(value);
            }
            else if (key == "args" && args.Count == 0)
            {
                if (value.StartsWith('['))
                {
                    args.AddRange(SplitFlow(value));
                }
                else if (value.Length == 0)
                {
                    var indent = m.Groups["indent"].Value.Length;
                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        var item = ListItem().Match(lines[j]);
                        if (!item.Success || item.Groups["indent"].Value.Length < indent) break;
                        args.Add(Unquote(item.Groups["value"].Value.Trim()) ?? "");
                    }
                }
            }
        }
        return command is null ? null : new EntryInfo(command, args);
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?:-\s+)?(?<key>command|args):(?<value>.*)$")]
    private static partial Regex KeyLine();

    [GeneratedRegex(@"^(?<indent>\s*)-\s+(?<value>.+)$")]
    private static partial Regex ListItem();

    internal static string? Unquote(string v)
    {
        v = v.Trim();
        if (v.StartsWith('\''))
        {
            var sb = new StringBuilder();
            for (var i = 1; i < v.Length; i++)
            {
                if (v[i] == '\'')
                {
                    if (i + 1 < v.Length && v[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                    return sb.ToString();
                }
                sb.Append(v[i]);
            }
            return null;
        }
        if (v.StartsWith('"'))
        {
            try
            {
                var end = v.LastIndexOf('"');
                return end > 0 ? System.Text.Json.JsonSerializer.Deserialize<string>(v[..(end + 1)]) : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? v[..hash] : v).Trim();
    }

    private static IEnumerable<string> SplitFlow(string value)
    {
        var inner = value.Trim().TrimStart('[');
        var close = inner.LastIndexOf(']');
        if (close >= 0) inner = inner[..close];
        var parts = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        foreach (var c in inner)
        {
            if (quote is null && c == ',')
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            if (quote is null && c is '\'' or '"') quote = c;
            else if (quote == c) quote = null;
            sb.Append(c);
        }
        if (sb.ToString().Trim().Length > 0) parts.Add(sb.ToString());
        return parts.Select(p => Unquote(p) ?? "");
    }

    private FileProbe Probe(McpServerSpec? spec)
    {
        var path = ConfigPath!;
        try
        {
            if (!File.Exists(path)) return new FileProbe(path, ProbeState.FileMissing);
            var text = ConfigFile.Read(path).Text;
            var info = ParseYaml(text);
            var managed = text.Contains(Marker, StringComparison.Ordinal);
            if (info?.Command is not null && CommandPath.IsOffload(info.Command, spec?.Command))
                return new FileProbe(path, ProbeState.Ours, info);
            // Наш файл, но команда не читается — считаем устаревшим (перезапишем).
            if (managed) return new FileProbe(path, ProbeState.Ours, info ?? new EntryInfo("", []));
            return new FileProbe(path, ProbeState.Foreign, info);
        }
        catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
        {
            return new FileProbe(path, ProbeState.Error, Error: msg);
        }
    }

    public override IntegrationStatus GetStatus(McpServerSpec spec) =>
        IsClientInstalled() ? Probe(spec).ToStatus(spec) : IntegrationStatus.ClientNotFound;

    public override Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        if (!IsClientInstalled()) return Task.FromResult(NotFound());
        var path = ConfigPath!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = BuildYaml(spec);
            string? replacedForeign = null;
            var res = ConfigFile.Edit(path, snap =>
            {
                replacedForeign = null;
                if (snap.Exists && snap.Text == content) return null;
                if (snap.Exists && !snap.Text.Contains(Marker, StringComparison.Ordinal))
                {
                    var info = ParseYaml(snap.Text);
                    if (!CommandPath.IsOffload(info?.Command, spec.Command)) replacedForeign = info?.Command ?? "";
                }
                return content;
            });
            var msg = res.Outcome == WriteOutcome.Unchanged ? MsgAlready(path) : MsgRegistered(path);
            if (res.Outcome == WriteOutcome.Written && replacedForeign is not null)
                msg += " " + MsgReplacedForeign(replacedForeign.Length == 0 ? null : replacedForeign);
            return Task.FromResult(new IntegrationResult(true, msg, res.BackupPath));
        }
        catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
        {
            Log.Warn("Integrations", $"{Id}: {msg}");
            return Task.FromResult(new IntegrationResult(false, msg));
        }
    }

    public override Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default)
    {
        var path = ConfigPath!;
        var probe = Probe(null);
        switch (probe.State)
        {
            case ProbeState.FileMissing:
            case ProbeState.Absent:
                return Task.FromResult(new IntegrationResult(true, MsgNotRegistered));
            case ProbeState.Error:
                return Task.FromResult(new IntegrationResult(false, probe.Error!));
            case ProbeState.Foreign:
                return Task.FromResult(new IntegrationResult(false, MsgForeign(path, probe.Entry?.Command)));
        }
        try
        {
            var snap = ConfigFile.Read(path);
            var backup = ConfigFile.Delete(snap);
            try
            {
                var dir = Path.GetDirectoryName(path)!;
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException)
            {
                // Папка занята — не важно.
            }
            return Task.FromResult(new IntegrationResult(true, MsgUnregistered(path), backup));
        }
        catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
        {
            return Task.FromResult(new IntegrationResult(false, msg));
        }
    }
}

/// <summary>
/// JetBrains AI Assistant: MCP настраивается только в интерфейсе IDE. Файлы не пишем — выдаём инструкцию.
/// </summary>
internal sealed class JetBrainsAiIntegration : IntegrationBase
{
    public override string Id => "jetbrains-ai";
    public override string DisplayName => "JetBrains AI Assistant";
    public override string? PostRegisterHint => L.T("Подключение выполняется вручную в настройках IDE JetBrains.");
    public override string? ConfigPath => null;

    public override bool IsClientInstalled() => ClientLocations.JetBrainsInstalled();

    /// <summary>Хранилище AI Assistant не документировано — честно сообщаем «не подключено».</summary>
    public override IntegrationStatus GetStatus(McpServerSpec spec) =>
        IsClientInstalled() ? IntegrationStatus.NotRegistered : IntegrationStatus.ClientNotFound;

    internal static string Snippet(McpServerSpec spec)
    {
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                [spec.Name] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = spec.Command,
                    ["args"] = JsonIntegration.Strings(spec.Args),
                },
            },
        };
        return o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = JsonTree.Encoder });
    }

    public override Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        if (!IsClientInstalled()) return Task.FromResult(NotFound());
        var msg =
            L.F("JetBrains AI Assistant настраивается только вручную: откройте Settings | Tools | AI Assistant | Model Context Protocol (MCP) и нажмите «Import from Claude» (после подключения Offload к Claude Desktop), либо нажмите «Add», выберите ввод JSON и вставьте:{0}{1}",
                Environment.NewLine, Snippet(spec));
        return Task.FromResult(new IntegrationResult(false, msg));
    }

    public override Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default) =>
        Task.FromResult(new IntegrationResult(true,
            L.T("Удалите сервер «offload» вручную: Settings | Tools | AI Assistant | Model Context Protocol (MCP).")));
}
