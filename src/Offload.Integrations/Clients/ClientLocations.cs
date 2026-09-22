using System.Text.RegularExpressions;
using Offload.Core.Processes;

namespace Offload.Integrations.Clients;

/// <summary>Пути и признаки установки клиентов (все — от корней IntegrationEnvironment).</summary>
internal static partial class ClientLocations
{
    private static string Profile => IntegrationEnvironment.UserProfile;
    private static string AppData => IntegrationEnvironment.AppData;
    private static string Local => IntegrationEnvironment.LocalAppData;

    public static bool Dir(string path) => Directory.Exists(path);

    // ---------------------------------------------------------------- Claude Code

    /// <summary>~/.claude (или CLAUDE_CONFIG_DIR).</summary>
    public static string ClaudeHome =>
        IntegrationEnvironment.GetVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Profile, ".claude");

    /// <summary>
    /// Глобальный файл Claude Code: ~/.claude.json; при заданном CLAUDE_CONFIG_DIR — $CLAUDE_CONFIG_DIR\.claude.json
    /// (так делает сам claude.exe; проверено вживую). Устаревший ~/.claude/.config.json используется, если он существует.
    /// </summary>
    public static string ClaudeGlobalConfig
    {
        get
        {
            var legacy = Path.Combine(ClaudeHome, ".config.json");
            if (File.Exists(legacy)) return legacy;
            var dir = IntegrationEnvironment.GetVariable("CLAUDE_CONFIG_DIR") ?? Profile;
            return Path.Combine(dir, ".claude.json");
        }
    }

    public static string ClaudeSettings => Path.Combine(ClaudeHome, "settings.json");

    public static bool ClaudeCodeInstalled() =>
        Dir(ClaudeHome) || File.Exists(ClaudeGlobalConfig) || ClaudeCli.Find() is not null;

    // ---------------------------------------------------------------- Claude Desktop

    /// <summary>Папки MSIX-пакетов Claude (Claude_&lt;издатель&gt;), без жёсткой привязки к суффиксу.</summary>
    public static IReadOnlyList<string> ClaudeDesktopPackages()
    {
        var root = Path.Combine(Local, "Packages");
        if (!Dir(root)) return [];
        try
        {
            return Directory.EnumerateDirectories(root, "Claude_*")
                .Where(d => ClaudePackageName().IsMatch(Path.GetFileName(d)))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^Claude_[a-z0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudePackageName();

    private static string MsixDesktopConfig(string package) =>
        Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");

    public static string ClassicDesktopConfig => Path.Combine(AppData, "Claude", "claude_desktop_config.json");

    /// <summary>Файл, который реально читает установленный Claude Desktop (MSIX-версия читает свою копию в LocalCache).</summary>
    public static string ClaudeDesktopConfig
    {
        get
        {
            var packages = ClaudeDesktopPackages();
            if (packages.Count == 0) return ClassicDesktopConfig;
            return packages.Select(MsixDesktopConfig).FirstOrDefault(File.Exists) ?? MsixDesktopConfig(packages[0]);
        }
    }

    public static IReadOnlyList<string> AllClaudeDesktopConfigs() =>
        [.. ClaudeDesktopPackages().Select(MsixDesktopConfig), ClassicDesktopConfig];

    public static bool ClaudeDesktopInstalled() =>
        ClaudeDesktopPackages().Count > 0 || Dir(Path.Combine(AppData, "Claude")) || Dir(Path.Combine(Local, "AnthropicClaude"));

    // ---------------------------------------------------------------- VS Code и расширения

    public static string VsCodeUser(bool insiders) => Path.Combine(AppData, insiders ? "Code - Insiders" : "Code", "User");

    private static IEnumerable<string> ExtensionRoots() =>
    [
        Path.Combine(Profile, ".vscode", "extensions"),
        Path.Combine(Profile, ".vscode-insiders", "extensions"),
    ];

    /// <summary>Версии установленного расширения (папки &lt;id&gt;-&lt;версия&gt;[-платформа]).</summary>
    public static IReadOnlyList<Version> ExtensionVersions(string extensionId)
    {
        var result = new List<Version>();
        var re = new Regex("^" + Regex.Escape(extensionId) + @"-(\d+)\.(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        foreach (var root in ExtensionRoots())
        {
            if (!Dir(root)) continue;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(root))
                {
                    var m = re.Match(Path.GetFileName(d));
                    if (m.Success && Version.TryParse($"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}", out var v))
                        result.Add(v);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Нет доступа — считаем, что расширения нет.
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- Cline

    private static string ClineLegacyDir => Path.Combine(VsCodeUser(false), "globalStorage", "saoudrizwan.claude-dev");

    public static string ClineLegacyConfig => Path.Combine(ClineLegacyDir, "settings", "cline_mcp_settings.json");

    public static string ClineV4Config
    {
        get
        {
            if (IntegrationEnvironment.GetVariable("CLINE_MCP_SETTINGS_PATH") is { } p) return Path.GetFullPath(p);
            if (IntegrationEnvironment.GetVariable("CLINE_DATA_DIR") is { } d)
                return Path.Combine(Path.GetFullPath(d), "settings", "cline_mcp_settings.json");
            return Path.Combine(ClineDir, "data", "settings", "cline_mcp_settings.json");
        }
    }

    private static string ClineDir =>
        IntegrationEnvironment.GetVariable("CLINE_DIR") is { } c ? Path.GetFullPath(c) : Path.Combine(Profile, ".cline");

    /// <summary>Cline 4.x читает ~/.cline/...; старый путь в globalStorage после миграции игнорируется.</summary>
    public static bool ClineUsesV4() =>
        IntegrationEnvironment.GetVariable("CLINE_MCP_SETTINGS_PATH") is not null ||
        IntegrationEnvironment.GetVariable("CLINE_DATA_DIR") is not null ||
        Dir(ClineDir) ||
        ExtensionVersions("saoudrizwan.claude-dev").Any(v => v.Major >= 4);

    public static bool ClineInstalled() =>
        ClineUsesV4() || Dir(ClineLegacyDir) || ExtensionVersions("saoudrizwan.claude-dev").Count > 0;

    // ---------------------------------------------------------------- Roo Code / Kilo

    public static string RooDir => Path.Combine(VsCodeUser(false), "globalStorage", "rooveterinaryinc.roo-cline");

    public static string RooConfig => Path.Combine(RooDir, "settings", "mcp_settings.json");

    public static bool RooInstalled() => Dir(RooDir) || ExtensionVersions("rooveterinaryinc.roo-cline").Count > 0;

    /// <summary>~/.config/kilo (xdg-basedir, НЕ %APPDATA%).</summary>
    public static string KiloDir =>
        Path.Combine(IntegrationEnvironment.GetVariable("XDG_CONFIG_HOME") ?? Path.Combine(Profile, ".config"), "kilo");

    public static IReadOnlyList<string> KiloTargets()
    {
        var jsonc = Path.Combine(KiloDir, "kilo.jsonc");
        var json = Path.Combine(KiloDir, "kilo.json");
        var existing = new[] { jsonc, json }.Where(File.Exists).ToList();
        return existing.Count > 0 ? existing : [json];
    }

    public static IReadOnlyList<string> KiloAllFiles() => [Path.Combine(KiloDir, "kilo.jsonc"), Path.Combine(KiloDir, "kilo.json")];

    // ---------------------------------------------------------------- Windsurf / Devin, Trae

    public static IReadOnlyList<string> WindsurfTargets()
    {
        var list = new List<string>();
        var devin = Path.Combine(AppData, "devin");
        if (Dir(devin)) list.Add(Path.Combine(devin, "mcp_config.json"));
        foreach (var channel in new[] { "windsurf", "windsurf-next" })
        {
            var d = Path.Combine(Profile, ".codeium", channel);
            if (Dir(d)) list.Add(Path.Combine(d, "mcp_config.json"));
        }
        return list;
    }

    public static IReadOnlyList<string> TraeTargets() =>
        new[] { "Trae", "Trae CN", "TRAE SOLO CN" }
            .Select(n => Path.Combine(AppData, n))
            .Where(Dir)
            .Select(d => Path.Combine(d, "User", "mcp.json"))
            .ToList();

    // ---------------------------------------------------------------- Прочие

    public static string CopilotHome =>
        IntegrationEnvironment.GetVariable("COPILOT_HOME") is { } h ? Path.GetFullPath(h) : Path.Combine(Profile, ".copilot");

    public static string CodexHome =>
        IntegrationEnvironment.GetVariable("CODEX_HOME") is { } h ? Path.GetFullPath(h) : Path.Combine(Profile, ".codex");

    public static bool CursorInstalled() =>
        Dir(Path.Combine(Profile, ".cursor")) || Dir(Path.Combine(Local, "Programs", "cursor"));

    public static bool ZedInstalled() =>
        Dir(Path.Combine(AppData, "Zed")) || Dir(Path.Combine(Local, "Programs", "Zed")) || Dir(Path.Combine(Local, "Zed"));

    /// <summary>Visual Studio (не Build Tools): ищем devenv.exe в стандартных папках установки.</summary>
    public static bool VisualStudioInstalled()
    {
        foreach (var pf in new[] { IntegrationEnvironment.ProgramFiles, IntegrationEnvironment.ProgramFilesX86 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            var root = Path.Combine(pf, "Microsoft Visual Studio");
            if (!Dir(root)) continue;
            try
            {
                foreach (var version in Directory.EnumerateDirectories(root))
                {
                    foreach (var edition in Directory.EnumerateDirectories(version))
                    {
                        if (File.Exists(Path.Combine(edition, "Common7", "IDE", "devenv.exe"))) return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Пропускаем недоступные папки.
            }
        }
        return false;
    }

    public static bool JetBrainsInstalled() => Dir(Path.Combine(AppData, "JetBrains"));

    /// <summary>Найти консольный exe в PATH (только .exe — .cmd-обёртки npm искажают аргументы).</summary>
    public static string? FindExeOnPath(string exeName)
    {
        if (!IntegrationEnvironment.CliAllowed || IntegrationEnvironment.IsSandboxed) return null;
        var found = ProcessRunner.FindOnPath(exeName);
        return found is not null && found.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? found : null;
    }
}
