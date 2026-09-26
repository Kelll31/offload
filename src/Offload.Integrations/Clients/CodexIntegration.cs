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

    private static FileProbe Probe(string path, McpServerSpec? spec, string name)
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
            if (ours && spec is not null && StaleDefaultKeys(patcher, PreviousDefaults).Count > 0) info = info with { StaleDefaults = true };
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

    internal override IReadOnlyList<FileProbe> ProbeEntries(McpServerSpec spec) =>
        IsClientInstalled() ? [Probe(ConfigPath!, spec, spec.Name)] : [];

    /// <summary>Новый текст config.toml с нашей таблицей (или null, если менять нечего). Результат проверяется повторным разбором.</summary>
    /// <summary>
    /// Прежние значения по умолчанию (текст TOML) для <c>startup_timeout_sec</c> / <c>tool_timeout_sec</c>: такое значение записал
    /// Offload, и при смене умолчания его можно заменить. Меняя <see cref="StartupTimeoutSec"/> или <see cref="ToolTimeoutSec"/>,
    /// добавьте сюда старое значение. Прочие значения считаются выбором пользователя и сохраняются.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> PreviousDefaults = new Dictionary<string, string[]>
    {
        ["startup_timeout_sec"] = [],
        ["tool_timeout_sec"] = [],
    };

    /// <summary>Ключи основной таблицы со значением по умолчанию прежней версии Offload.</summary>
    internal static IReadOnlyList<string> StaleDefaultKeys(TomlTablePatcher p, IReadOnlyDictionary<string, string[]> previous)
    {
        var stale = new List<string>();
        foreach (var (key, values) in previous)
        {
            if (values.Length == 0 || p.MainValue(key) is not { ValueStart: >= 0 } kv) continue;
            var raw = p.Document.Text[kv.ValueStart..kv.ValueEnd].Trim();
            if (values.Contains(raw, StringComparer.Ordinal)) stale.Add(key);
        }
        return stale;
    }

    internal static string? BuildRegistered(string text, McpServerSpec spec, IReadOnlyDictionary<string, string[]>? previousDefaults = null)
    {
        var doc = TomlDocument.Parse(text);
        var patcher = new TomlTablePatcher(doc, Table(spec.Name));
        var stale = patcher.Main is null ? [] : StaleDefaultKeys(patcher, previousDefaults ?? PreviousDefaults);
        if (patcher.Main is not null && stale.Count == 0 && ReadEntry(patcher).Matches(spec) && CommandPath.IsOffload(ReadEntry(patcher).Command, spec.Command))
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
        // Устаревшие умолчания Offload переписываются, как свои ключи; значения пользователя остаются.
        owned.AddRange(defaults.Where(d => stale.Contains(d.Item1)));
        defaults.RemoveAll(d => stale.Contains(d.Item1));
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

    /// <summary>Только путь к exe (ключ command); остальные ключи таблицы, умолчания и подтаблицы не трогаются.</summary>
    internal override Task<IntegrationResult?> RepairPathAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        if (!IsClientInstalled()) return Task.FromResult<IntegrationResult?>(null);
        var path = ConfigPath!;
        if (Probe(path, spec, spec.Name).Need(spec) != RepairNeed.PathMoved) return Task.FromResult<IntegrationResult?>(null);
        try
        {
            var res = ConfigFile.Edit(path, snap => BuildPathRepaired(snap.Text, spec));
            return Task.FromResult<IntegrationResult?>(res.Outcome == WriteOutcome.Written
                ? new IntegrationResult(true, MsgRegistered(path), res.BackupPath)
                : null);
        }
        catch (Exception ex) when (DescribeFailure(path, ex) is { } msg)
        {
            Log.Warn("Integrations", $"{Id}: {msg}");
            return Task.FromResult<IntegrationResult?>(new IntegrationResult(false, msg));
        }
    }

    /// <summary>Текст с новым путём к exe в нашей таблице или null, если запись не «наша с отсутствующим exe».</summary>
    internal static string? BuildPathRepaired(string text, McpServerSpec spec)
    {
        var patcher = new TomlTablePatcher(TomlDocument.Parse(text), Table(spec.Name));
        if (patcher.Unsupported is not null || patcher.Main is null) return null;
        var info = ReadEntry(patcher);
        if (!CommandPath.IsOffload(info.Command, spec.Command) || IntegrationBase.NeedOf(info, spec) != RepairNeed.PathMoved) return null;
        var updated = patcher.SetTable([("command", TomlDocument.FormatString(spec.Command))], []);
        Verify(patcher, updated, spec);
        return updated;
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
