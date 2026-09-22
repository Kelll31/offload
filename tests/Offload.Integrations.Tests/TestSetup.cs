using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Offload.Core;
using Offload.Integrations;

[assembly: AssemblyFixture(typeof(Offload.Integrations.Tests.RealProfileGuard))]

namespace Offload.Integrations.Tests;

/// <summary>
/// Страховка «на всякий случай»: ещё до первого теста все корни IntegrationEnvironment указывают во временную папку,
/// а резервные копии Offload (AppPaths.BackupsDir) — тоже во временную папку. Каждый тест дополнительно создаёт свою песочницу.
/// </summary>
internal static class TestSetup
{
    public const string SandboxPrefix = "PcTestSandbox-";

    public static readonly string CatchAllRoot =
        Path.Combine(Path.GetTempPath(), SandboxPrefix + "catchall-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Init()
    {
        // Снимок реальных файлов делается до любых действий тестов.
        RealProfileGuard.Snapshot();
        Directory.CreateDirectory(CatchAllRoot);
        Environment.SetEnvironmentVariable(IntegrationEnvironment.UserProfileVar, CatchAllRoot);
        Environment.SetEnvironmentVariable(IntegrationEnvironment.AppDataVar, Path.Combine(CatchAllRoot, "AppData", "Roaming"));
        Environment.SetEnvironmentVariable(IntegrationEnvironment.LocalAppDataVar, Path.Combine(CatchAllRoot, "AppData", "Local"));
        AppPaths.OverrideDataDir(Path.Combine(CatchAllRoot, "offload-data"));
    }
}

/// <summary>Песочница одного теста: свой домашний каталог; все пути интеграций — внутри него.</summary>
internal sealed class Sandbox : IDisposable
{
    private readonly IDisposable _override;

    public string Root { get; }
    public string Home => Root;
    public string AppData => Path.Combine(Root, "AppData", "Roaming");
    public string Local => Path.Combine(Root, "AppData", "Local");

    public Sandbox(IReadOnlyDictionary<string, string>? vars = null, bool allowCli = false, string? claudeCli = null)
    {
        Root = Path.Combine(Path.GetTempPath(), TestSetup.SandboxPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(Local);
        _override = IntegrationEnvironment.Override(Root, vars, allowCli, claudeCli);
    }

    public string P(params string[] parts) => Path.Combine([Root, .. parts]);

    public string Dir(params string[] parts)
    {
        var d = P(parts);
        Directory.CreateDirectory(d);
        return d;
    }

    public string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
        return path;
    }

    /// <summary>Путь к «нашему» exe внутри песочницы (с пробелом и кириллицей).</summary>
    public McpServerSpec Spec(string folder = "Программы Offload") =>
        new(AppInfo.McpServerId, P("Program Files", folder, "Offload.exe"), [AppInfo.McpArg], new Dictionary<string, string>());

    public void Dispose()
    {
        _override.Dispose();
        try { Directory.Delete(Root, true); } catch { }
    }
}

/// <summary>
/// Страж реального профиля: до тестов снимает отпечатки настоящих конфигов, после всех тестов проверяет,
/// что тесты их не тронули. ~/.claude.json и ~/.claude/settings.json постоянно переписывает сам работающий
/// Claude Code, поэтому для изменившихся файлов проверяется «наш след»: число упоминаний offload и
/// отсутствие путей песочницы. Файлы, которые создаёт только Offload, сверяются строго по хэшу.
/// </summary>
public sealed class RealProfileGuard : IDisposable
{
    private sealed record Print(bool Exists, string Hash, int OffloadMentions, bool HasSandboxMarker);

    private static Dictionary<string, Print>? _before;
    private static readonly object Lock = new();

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalApp => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Файлы, которые может менять только Offload — сверяются строго.</summary>
    internal static IEnumerable<string> StrictFiles() =>
    [
        Path.Combine(Home, ".claude", "skills", "offload", "SKILL.md"),
        Path.Combine(Home, ".claude", "rules", "offload.md"),
        Path.Combine(Home, ".claude", "agents", "offload-runner.md"),
        Path.Combine(Home, ".continue", "mcpServers", "offload.yaml"),
    ];

    internal static IEnumerable<string> WatchedFiles()
    {
        var list = new List<string>
        {
            Path.Combine(Home, ".claude.json"),
            Path.Combine(Home, ".claude", "settings.json"),
            Path.Combine(Home, ".cursor", "mcp.json"),
            Path.Combine(Home, ".mcp.json"),
            Path.Combine(Home, ".codex", "config.toml"),
            Path.Combine(Home, ".gemini", "settings.json"),
            Path.Combine(Home, ".copilot", "mcp-config.json"),
            Path.Combine(Home, ".junie", "mcp", "mcp.json"),
            Path.Combine(Home, ".kiro", "settings", "mcp.json"),
            Path.Combine(Home, ".qoder", "settings.json"),
            Path.Combine(Home, ".cline", "data", "settings", "cline_mcp_settings.json"),
            Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json"),
            Path.Combine(Home, ".config", "kilo", "kilo.json"),
            Path.Combine(Home, ".config", "kilo", "kilo.jsonc"),
            Path.Combine(Roaming, "Code", "User", "mcp.json"),
            Path.Combine(Roaming, "Code", "User", "settings.json"),
            Path.Combine(Roaming, "Code - Insiders", "User", "mcp.json"),
            Path.Combine(Roaming, "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
            Path.Combine(Roaming, "Code", "User", "globalStorage", "rooveterinaryinc.roo-cline", "settings", "mcp_settings.json"),
            Path.Combine(Roaming, "Claude", "claude_desktop_config.json"),
            Path.Combine(Roaming, "devin", "mcp_config.json"),
            Path.Combine(Roaming, "Zed", "settings.json"),
            Path.Combine(Roaming, "Trae", "User", "mcp.json"),
        };
        try
        {
            var packages = Path.Combine(LocalApp, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var d in Directory.EnumerateDirectories(packages, "Claude_*"))
                    list.Add(Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
            }
        }
        catch
        {
            // Нет доступа к Packages — просто не проверяем.
        }
        return list.Concat(StrictFiles());
    }

    private static Print Take(string path)
    {
        if (!File.Exists(path)) return new Print(false, "", 0, false);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                byte[] bytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                var text = Encoding.UTF8.GetString(bytes);
                var mentions = 0;
                for (var i = text.IndexOf("offload", StringComparison.OrdinalIgnoreCase); i >= 0;
                     i = text.IndexOf("offload", i + 1, StringComparison.OrdinalIgnoreCase))
                    mentions++;
                var marker = text.Contains(TestSetup.SandboxPrefix, StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("PcTestSandbox", StringComparison.OrdinalIgnoreCase);
                return new Print(true, Convert.ToHexString(SHA256.HashData(bytes)), mentions, marker);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    internal static void Snapshot()
    {
        lock (Lock)
        {
            _before ??= WatchedFiles().Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, Take, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Список нарушений (пусто — реальный профиль не тронут).</summary>
    internal static List<string> Violations()
    {
        Snapshot();
        var strict = StrictFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var (path, before) in _before!)
        {
            var after = Take(path);
            if (after == before) continue;
            if (strict.Contains(path) || before.Exists != after.Exists)
            {
                problems.Add($"{path}: изменился (существовал: {before.Exists} → {after.Exists})");
                continue;
            }
            if (after.OffloadMentions != before.OffloadMentions)
                problems.Add($"{path}: число упоминаний offload {before.OffloadMentions} → {after.OffloadMentions}");
            if (after.HasSandboxMarker && !before.HasSandboxMarker)
                problems.Add($"{path}: появились пути тестовой песочницы");
        }
        return problems;
    }

    public void Dispose()
    {
        var problems = Violations();
        // Самопроверка стража: OFFLOAD_GUARD_SELFTEST=1 имитирует нарушение (запуск должен упасть).
        if (Environment.GetEnvironmentVariable("OFFLOAD_GUARD_SELFTEST") == "1") problems.Add("самопроверка стража");
        try { Directory.Delete(TestSetup.CatchAllRoot, true); } catch { }
        if (problems.Count > 0)
            throw new InvalidOperationException("ТЕСТЫ ИЗМЕНИЛИ РЕАЛЬНЫЙ ПРОФИЛЬ:\n" + string.Join("\n", problems));
    }
}
