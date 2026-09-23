using System.Globalization;
using Offload.Core.Logging;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Clients;

/// <summary>
/// OpenAI Codex (CLI, расширение IDE, приложение): таблица [mcp_servers.offload] в ~/.codex/config.toml
/// (CODEX_HOME). Правится только эта таблица; остальной TOML, включая комментарии, не трогается.
/// </summary>
internal sealed class CodexIntegration : IntegrationBase
{
    /// <summary>Codex по умолчанию ждёт запуск 10 с и ответ 60 с — мало для долгих делегирований.</summary>
    public const int StartupTimeoutSec = 30;
    public const int ToolTimeoutSec = 1800;

    public override string Id => "codex";
    public override string DisplayName => "OpenAI Codex";
    public override string? PostRegisterHint => L.T("Перезапустите Codex (CLI, расширение IDE или приложение), чтобы он увидел сервер.");
    public override string? ConfigPath => Path.Combine(ClientLocations.CodexHome, "config.toml");

    public override bool IsClientInstalled() => Directory.Exists(ClientLocations.CodexHome);

    private static string[] Table(string name) => ["mcp_servers", name];

    private FileProbe Probe(string path, McpServerSpec? spec, string name)
    {
        try
        {
            if (!File.Exists(path)) return new FileProbe(path, ProbeState.FileMissing);
            var doc = TomlDocument.Parse(ConfigFile.Read(path).Text);
            var patcher = new TomlTablePatcher(doc, Table(name));
            if (patcher.Unsupported is not null)
                return new FileProbe(path, ProbeState.Error, Error: MsgParse(path, patcher.Unsupported));
            if (patcher.Main is null) return new FileProbe(path, ProbeState.Absent);
            var info = ReadEntry(patcher);
            var ours = info.Command is not null && CommandPath.IsOffload(info.Command, spec?.Command);
            return new FileProbe(path, ours ? ProbeState.Ours : ProbeState.Foreign, info);
        }
        catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
        {
            return new FileProbe(path, ProbeState.Error, Error: msg);
        }
    }

    private static EntryInfo ReadEntry(TomlTablePatcher p)
    {
        var cmd = p.MainValue("command") is { } c ? p.Document.ReadString(c) : null;
        var args = p.MainValue("args") is { } a ? p.Document.ReadStringArray(a) : null;
        return new EntryInfo(cmd, args ?? []);
    }

    public override IntegrationStatus GetStatus(McpServerSpec spec)
    {
        if (!IsClientInstalled()) return IntegrationStatus.ClientNotFound;
        return Probe(ConfigPath!, spec, spec.Name).ToStatus(spec);
    }

    /// <summary>Новый текст config.toml с нашей таблицей (или null, если менять нечего). Результат проверяется повторным разбором.</summary>
    internal static string? BuildRegistered(string text, McpServerSpec spec)
    {
        var doc = TomlDocument.Parse(text);
        var patcher = new TomlTablePatcher(doc, Table(spec.Name));
        if (patcher.Main is not null && ReadEntry(patcher).Matches(spec) && CommandPath.IsOffload(ReadEntry(patcher).Command, spec.Command))
            return null;

        var owned = new List<(string, string)>
        {
            ("command", TomlDocument.FormatString(spec.Command)),
            ("args", TomlDocument.FormatStringArray(spec.Args)),
        };
        if (spec.Env.Count > 0)
        {
            if (patcher.Ranges.Any(r => !r.IsMain))
                throw new TomlPatchException(L.T("у записи уже есть подтаблицы (например, env) — обновите их вручную"));
            owned.Add(("env", "{ " + string.Join(", ", spec.Env.Select(kv => $"{TomlDocument.FormatKey(kv.Key)} = {TomlDocument.FormatString(kv.Value)}")) + " }"));
        }
        var defaults = new List<(string, string)>
        {
            ("startup_timeout_sec", StartupTimeoutSec.ToString(CultureInfo.InvariantCulture)),
            ("tool_timeout_sec", ToolTimeoutSec.ToString(CultureInfo.InvariantCulture)),
            ("enabled", "true"),
        };
        var updated = patcher.SetTable(owned, defaults);
        Verify(patcher, updated, spec);
        return updated;
    }

    internal static string? BuildUnregistered(string text, string name)
    {
        var doc = TomlDocument.Parse(text);
        var patcher = new TomlTablePatcher(doc, Table(name));
        if (patcher.Ranges.Count == 0) return null;
        if (patcher.Main is not null && !CommandPath.IsOffload(ReadEntry(patcher).Command)) return null;
        var updated = patcher.RemoveTable();
        Verify(patcher, updated, null);
        return updated;
    }

    /// <summary>Разбор результата: запись соответствует ожиданию, чужой текст не изменился.</summary>
    private static void Verify(TomlTablePatcher before, string updated, McpServerSpec? spec)
    {
        var after = new TomlTablePatcher(TomlDocument.Parse(updated), Table(spec?.Name ?? ServerName));
        if (after.Unsupported is not null) throw new TomlPatchException(L.F("проверка результата не прошла: {0}", after.Unsupported));
        if (spec is null)
        {
            if (after.Ranges.Count > 0) throw new TomlPatchException(L.T("проверка результата не прошла: таблица не удалена"));
        }
        else if (after.Main is null || !ReadEntry(after).Matches(spec))
        {
            throw new TomlPatchException(L.T("проверка результата не прошла: запись не совпала с ожидаемой"));
        }
        if (after.ForeignFingerprint() != before.ForeignFingerprint())
            throw new TomlPatchException(L.T("проверка результата не прошла: изменился бы чужой текст"));
    }

    public override Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        if (!IsClientInstalled()) return Task.FromResult(NotFound());
        var path = ConfigPath!;
        try
        {
            string? replacedForeign = null;
            var res = ConfigFile.Edit(path, snap =>
            {
                var p = new TomlTablePatcher(TomlDocument.Parse(snap.Text), Table(spec.Name));
                replacedForeign = p.Main is not null && !CommandPath.IsOffload(ReadEntry(p).Command, spec.Command)
                    ? ReadEntry(p).Command ?? "" : null;
                return BuildRegistered(snap.Text, spec);
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
        var probe = Probe(path, null, ServerName);
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
            var res = ConfigFile.Edit(path, snap => BuildUnregistered(snap.Text, ServerName));
            return Task.FromResult(new IntegrationResult(true,
                res.Outcome == WriteOutcome.Written ? MsgUnregistered(path) : MsgNotRegistered, res.BackupPath));
        }
        catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
        {
            Log.Warn("Integrations", $"{Id}: {msg}");
            return Task.FromResult(new IntegrationResult(false, msg));
        }
    }
}
