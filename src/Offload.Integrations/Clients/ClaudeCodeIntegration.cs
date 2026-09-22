using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Offload.Core.Logging;
using Offload.Core.Processes;
using Offload.Core.Util;

namespace Offload.Integrations.Clients;

/// <summary>Поиск claude.exe: PATH → ~/.local/bin → копия внутри расширения VS Code (самая новая).</summary>
internal static partial class ClaudeCli
{
    public static string? Find()
    {
        if (IntegrationEnvironment.ClaudeCliOverride is { } forced) return File.Exists(forced) ? forced : null;
        if (!IntegrationEnvironment.CliAllowed || IntegrationEnvironment.IsSandboxed) return null;

        if (ClientLocations.FindExeOnPath("claude.exe") is { } onPath) return onPath;

        var native = Path.Combine(IntegrationEnvironment.UserProfile, ".local", "bin", "claude.exe");
        if (File.Exists(native)) return native;

        return FindInExtensions(IntegrationEnvironment.UserProfile);
    }

    /// <summary>...\.vscode\extensions\anthropic.claude-code-&lt;ver&gt;-win32-x64\resources\native-binary\claude.exe — берём новейшую версию.</summary>
    internal static string? FindInExtensions(string profile)
    {
        var best = (Version: new Version(0, 0), Path: (string?)null);
        foreach (var root in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
        {
            var dir = Path.Combine(profile, root, "extensions");
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var ext in Directory.EnumerateDirectories(dir, "anthropic.claude-code-*"))
                {
                    var m = ExtensionVersion().Match(Path.GetFileName(ext));
                    if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var v)) continue;
                    var exe = Path.Combine(ext, "resources", "native-binary", "claude.exe");
                    if (File.Exists(exe) && v > best.Version) best = (v, exe);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Пропускаем.
            }
        }
        return best.Path;
    }

    [GeneratedRegex(@"^anthropic\.claude-code-(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionVersion();

    public static Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct) =>
        ProcessRunner.RunAsync(
            exe,
            args,
            workingDirectory: Directory.Exists(IntegrationEnvironment.UserProfile) ? IntegrationEnvironment.UserProfile : null,
            environment: IntegrationEnvironment.ChildEnvironment(),
            timeout: TimeSpan.FromSeconds(60),
            ct: ct);
}

/// <summary>
/// Claude Code: предпочтительно через «claude mcp add --scope user» (CLI сам блокирует и переписывает
/// большой ~/.claude.json), иначе — точечная правка верхнего mcpServers в ~/.claude.json.
/// Статус всегда читается из файла, без запуска CLI.
/// </summary>
internal sealed class ClaudeCodeIntegration : JsonIntegration
{
    public ClaudeCodeIntegration()
        : base("claude-code", "Claude Code", "Перезапустите Claude Code или выполните команду /mcp в открытой сессии.")
    {
    }

    public static ClaudeCodeIntegration Create() => new()
    {
        Detect = ClientLocations.ClaudeCodeInstalled,
        Targets = () => [ClientLocations.ClaudeGlobalConfig],
        Container = ["mcpServers"],
        Entry = spec => new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = spec.Command,
            ["args"] = Strings(spec.Args),
            ["env"] = EnvObject(spec),
        },
    };

    private string FilePath => ClientLocations.ClaudeGlobalConfig;

    public override async Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        if (!IsClientInstalled()) return NotFound();
        var probe = Probe(FilePath, spec, spec.Name);
        if (probe.State == ProbeState.Error) return new IntegrationResult(false, probe.Error!);
        if (probe.State == ProbeState.Ours && probe.Entry!.Matches(spec)) return new IntegrationResult(true, MsgAlready(FilePath));

        var cli = ClaudeCli.Find();
        if (cli is not null)
        {
            var backup = File.Exists(FilePath) ? FileUtil.Backup(FilePath) : null;
            try
            {
                if (probe.State is ProbeState.Ours or ProbeState.Foreign)
                    await ClaudeCli.RunAsync(cli, ["mcp", "remove", spec.Name, "--scope", "user"], ct);

                var args = new List<string> { "mcp", "add" };
                foreach (var (k, v) in spec.Env) args.AddRange(["-e", $"{k}={v}"]);
                args.AddRange(["--scope", "user", "--transport", "stdio", spec.Name, "--"]);
                args.Add(spec.Command);
                args.AddRange(spec.Args);
                var r = await ClaudeCli.RunAsync(cli, args, ct);
                var after = Probe(FilePath, spec, spec.Name);
                if (r.Success && after.ToStatus(spec) == IntegrationStatus.Registered)
                {
                    var msg = MsgRegistered(FilePath) + " Использован claude CLI.";
                    if (probe.State == ProbeState.Foreign) msg += " " + MsgReplacedForeign(probe.Entry?.Command);
                    return new IntegrationResult(true, msg, backup);
                }
                Log.Warn("Integrations", $"claude mcp add не сработал (код {r.ExitCode}): {Trim(r.StdErr + r.StdOut)} — правим файл напрямую");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("Integrations", $"Не удалось запустить {cli}: {ex.Message} — правим файл напрямую");
            }
        }
        return RegisterInFiles(spec);
    }

    public override async Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default)
    {
        var probe = Probe(FilePath, null, ServerName);
        switch (probe.State)
        {
            case ProbeState.FileMissing:
            case ProbeState.Absent:
                return new IntegrationResult(true, MsgNotRegistered);
            case ProbeState.Error:
                return new IntegrationResult(false, probe.Error!);
            case ProbeState.Foreign:
                return new IntegrationResult(false, MsgForeign(FilePath, probe.Entry?.Command));
        }

        var cli = ClaudeCli.Find();
        if (cli is not null)
        {
            var backup = FileUtil.Backup(FilePath);
            try
            {
                var r = await ClaudeCli.RunAsync(cli, ["mcp", "remove", ServerName, "--scope", "user"], ct);
                var after = Probe(FilePath, null, ServerName);
                if (r.Success && after.State is ProbeState.Absent or ProbeState.FileMissing)
                    return new IntegrationResult(true, MsgUnregistered(FilePath) + " Использован claude CLI.", backup);
                Log.Warn("Integrations", $"claude mcp remove не сработал (код {r.ExitCode}): {Trim(r.StdErr + r.StdOut)} — правим файл напрямую");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("Integrations", $"Не удалось запустить {cli}: {ex.Message} — правим файл напрямую");
            }
        }
        return UnregisterFromFiles();
    }

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}

/// <summary>Qoder: CLI «qoder mcp add -s user», если найден настоящий консольный qoder.exe; иначе правка ~/.qoder/settings.json.</summary>
internal sealed class QoderIntegration : JsonIntegration
{
    public QoderIntegration()
        : base("qoder", "Qoder", "Перезапустите Qoder; в Qoder CLI можно выполнить /mcp reload.")
    {
    }

    public static QoderIntegration Create() => new()
    {
        Detect = () => ClientLocations.Dir(Path.Combine(IntegrationEnvironment.UserProfile, ".qoder")) || FindCli() is not null,
        Targets = () => [Path.Combine(IntegrationEnvironment.UserProfile, ".qoder", "settings.json")],
        Container = ["mcpServers"],
        Entry = spec =>
        {
            var o = new JsonObject { ["command"] = spec.Command, ["args"] = Strings(spec.Args) };
            if (spec.Env.Count > 0) o["env"] = EnvObject(spec);
            return o;
        },
    };

    /// <summary>qoder.exe из PATH, но не исполняемый файл IDE (Electron-приложение с папкой resources\app).</summary>
    internal static string? FindCli()
    {
        var exe = ClientLocations.FindExeOnPath("qoder.exe");
        if (exe is null) return null;
        var dir = Path.GetDirectoryName(exe)!;
        if (Directory.Exists(Path.Combine(dir, "resources", "app"))) return null;
        return exe;
    }

    private string FilePath => Targets()[0];

    public override async Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        if (!IsClientInstalled()) return NotFound();
        var probe = Probe(FilePath, spec, spec.Name);
        if (probe.State == ProbeState.Error) return new IntegrationResult(false, probe.Error!);
        if (probe.State == ProbeState.Ours && probe.Entry!.Matches(spec)) return new IntegrationResult(true, MsgAlready(FilePath));

        if (FindCli() is { } cli)
        {
            var backup = File.Exists(FilePath) ? FileUtil.Backup(FilePath) : null;
            try
            {
                if (probe.State is ProbeState.Ours or ProbeState.Foreign)
                    await ClaudeCli.RunAsync(cli, ["mcp", "remove", "-s", "user", spec.Name], ct);
                var r = await ClaudeCli.RunAsync(cli, ["mcp", "add", "-s", "user", spec.Name, "--", spec.Command, .. spec.Args], ct);
                if (r.Success && Probe(FilePath, spec, spec.Name).ToStatus(spec) == IntegrationStatus.Registered)
                    return new IntegrationResult(true, MsgRegistered(FilePath) + " Использован qoder CLI.", backup);
                Log.Warn("Integrations", $"qoder mcp add: код {r.ExitCode} — правим файл напрямую");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("Integrations", $"Не удалось запустить {cli}: {ex.Message}");
            }
        }
        return RegisterInFiles(spec);
    }

    public override async Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default)
    {
        var probe = Probe(FilePath, null, ServerName);
        if (probe.State == ProbeState.Ours && FindCli() is { } cli)
        {
            try
            {
                var r = await ClaudeCli.RunAsync(cli, ["mcp", "remove", "-s", "user", ServerName], ct);
                if (r.Success && Probe(FilePath, null, ServerName).State is ProbeState.Absent or ProbeState.FileMissing)
                    return new IntegrationResult(true, MsgUnregistered(FilePath) + " Использован qoder CLI.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("Integrations", $"Не удалось запустить {cli}: {ex.Message}");
            }
        }
        return UnregisterFromFiles();
    }
}
