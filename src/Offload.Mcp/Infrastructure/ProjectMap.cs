using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Offload.Mcp.Infrastructure;

/// <summary>Проект/модуль сборки (csproj, package.json, pyproject, go.mod, Cargo.toml, pom.xml, gradle, dproj…).</summary>
internal sealed class ProjectInfo
{
    public required string Kind { get; init; }
    public required string Manifest { get; init; }
    public required string Name { get; init; }
    public string Dir => Path.GetDirectoryName(Manifest.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
    public string? Framework { get; set; }
    public string? OutputType { get; set; }
    public bool IsTest { get; set; }
    public List<(string Name, string? Version, bool Dev)> Packages { get; } = [];
    public List<string> ProjectRefs { get; } = [];
    public Dictionary<string, string> Scripts { get; } = new(StringComparer.Ordinal);
    public string? TestFramework { get; set; }
}

/// <summary>Предлагаемая команда сборки/проверки и проходит ли она белый список verify_command.</summary>
internal sealed record SuggestedCommand(string Kind, string Command, bool Allowed);

internal sealed class ProjectMapResult
{
    public List<ProjectInfo> Projects { get; } = [];
    public List<(string Lang, int Files, long Lines)> Languages { get; } = [];
    public List<(string Path, string Why)> EntryPoints { get; } = [];
    public List<(string Folder, int Files)> TopFolders { get; } = [];
    public List<SuggestedCommand> Commands { get; } = [];
    public List<string> RuleFiles { get; } = [];
    public List<string> CiFiles { get; } = [];
    public List<string> DockerFiles { get; } = [];
    public int TotalFiles { get; set; }
    public string? Note { get; set; }

    public SuggestedCommand? Pick(string kind) =>
        Commands.FirstOrDefault(c => c.Kind == kind && c.Allowed) ?? Commands.FirstOrDefault(c => c.Kind == kind);
}

/// <summary>
/// Карта проекта без сборки и без сети: манифесты, языки, точки входа, тестовые проекты, команды сборки/тестов/линтера
/// (с проверкой по белому списку) и крупные папки. Всё выводится из файлов рабочей папки за один проход.
/// </summary>
internal static partial class ProjectMap
{
    private static readonly string[] RuleFileNames =
    [
        "CLAUDE.md", "AGENTS.md", ".claude/CLAUDE.md", ".cursorrules", ".windsurfrules", ".github/copilot-instructions.md", "CONTRIBUTING.md",
        ".editorconfig", "CONVENTIONS.md", "STYLEGUIDE.md", "docs/CONTRIBUTING.md", "GEMINI.md", ".clinerules",
    ];

    public static async Task<(ProjectMapResult Map, CodeIndexResult Index)> BuildAsync(ToolContext ctx, CancellationToken ct)
    {
        var root = ctx.Roots[0];
        var map = new ProjectMapResult();
        var info = new GatherResult();
        var opts = ctx.GatherOptions with { MaxFiles = CodeIndex.MaxIndexFiles, MaxEntriesVisited = 120_000 };
        var all = await FileGatherer.ListFilesAsync([root], ctx.Roots, opts, info, ct).ConfigureAwait(false);
        map.TotalFiles = all.Count;
        if (info.LimitNote is not null) map.Note = info.LimitNote;
        var rel = all.Select(f => (Full: f, Rel: Path.GetRelativePath(root, f).Replace('\\', '/'))).Where(x => !x.Rel.StartsWith("..", StringComparison.Ordinal)).ToList();

        // Манифесты.
        foreach (var (full, r) in rel)
        {
            var name = Path.GetFileName(r);
            var ext = Path.GetExtension(r).ToLowerInvariant();
            try
            {
                if (ext is ".csproj" or ".fsproj" or ".vbproj") map.Projects.Add(ParseMsBuild(full, r));
                else if (ext == ".dproj") map.Projects.Add(new ProjectInfo { Kind = "delphi", Manifest = r, Name = Path.GetFileNameWithoutExtension(r) });
                else if (name == "package.json") map.Projects.Add(ParsePackageJson(full, r));
                else if (name == "pyproject.toml" || name == "setup.py" && !rel.Any(x => x.Rel == Path.GetDirectoryName(r)?.Replace('\\', '/') + "/pyproject.toml"))
                    map.Projects.Add(ParsePython(full, r, rel.Select(x => x.Rel)));
                else if (name == "go.mod") map.Projects.Add(ParseGoMod(full, r));
                else if (name == "Cargo.toml") map.Projects.Add(ParseCargo(full, r));
                else if (name == "pom.xml") map.Projects.Add(ParseSimple("maven", full, r, ArtifactId()));
                else if (name is "build.gradle" or "build.gradle.kts") map.Projects.Add(new ProjectInfo { Kind = "gradle", Manifest = r, Name = DirName(r) });
                else if (name == "CMakeLists.txt" && !r.Contains('/')) map.Projects.Add(ParseSimple("cmake", full, r, CMakeProject()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException)
            {
                map.Projects.Add(new ProjectInfo { Kind = "unreadable", Manifest = r, Name = DirName(r) });
            }
            if (RuleFileNames.Contains(r, StringComparer.OrdinalIgnoreCase)) map.RuleFiles.Add(r);
            if (r.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) || name is ".gitlab-ci.yml" or "azure-pipelines.yml" or "Jenkinsfile" or ".travis.yml" or "appveyor.yml")
                map.CiFiles.Add(r);
            if (name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) || name is "docker-compose.yml" or "docker-compose.yaml" or "compose.yml" or "compose.yaml")
                map.DockerFiles.Add(r);
        }

        // Языки и крупные папки.
        var index = await CodeIndex.LoadAsync(ctx, null, codeOnly: true, ct).ConfigureAwait(false);
        foreach (var g in index.Files.GroupBy(f => f.Lang).OrderByDescending(g => g.Sum(f => (long)f.Lines.Length)))
            map.Languages.Add((g.Key.ToString(), g.Count(), g.Sum(f => (long)f.Lines.Length)));
        foreach (var g in rel.GroupBy(x => x.Rel.Contains('/') ? x.Rel[..x.Rel.IndexOf('/')] : ".").OrderByDescending(g => g.Count()).Take(14))
            map.TopFolders.Add((g.Key, g.Count()));

        FindEntryPoints(map, index);
        SuggestCommands(map, root, rel.Select(x => x.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase), ctx.Cfg.Mcp.VerifyCommandAllowlist);
        return (map, index);
    }

    // ───────────────────────── манифесты ─────────────────────────

    private static readonly string[] TestPackages = ["xunit", "xunit.v3", "nunit", "mstest.testframework", "microsoft.net.test.sdk", "mstest"];

    private static ProjectInfo ParseMsBuild(string full, string rel)
    {
        var doc = XDocument.Load(full);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        string? Prop(string name) => doc.Descendants(ns + name).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        var p = new ProjectInfo
        {
            Kind = "dotnet",
            Manifest = rel,
            Name = Prop("AssemblyName") ?? Path.GetFileNameWithoutExtension(rel),
            Framework = Prop("TargetFramework") ?? Prop("TargetFrameworks"),
            OutputType = Prop("OutputType"),
        };
        foreach (var e in doc.Descendants(ns + "PackageReference"))
        {
            var id = (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update");
            if (id is null) continue;
            p.Packages.Add((id, (string?)e.Attribute("Version") ?? e.Element(ns + "Version")?.Value, false));
        }
        foreach (var e in doc.Descendants(ns + "ProjectReference"))
            if ((string?)e.Attribute("Include") is { } inc) p.ProjectRefs.Add(Path.GetFileNameWithoutExtension(inc.Replace('\\', '/')));
        var testByProp = string.Equals(Prop("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase);
        var testPkg = p.Packages.Select(x => x.Name.ToLowerInvariant()).FirstOrDefault(n => TestPackages.Any(t => n == t || n.StartsWith(t + ".", StringComparison.Ordinal)));
        p.IsTest = testByProp || testPkg is not null || CodeIndex.IsTestPath(rel);
        p.TestFramework = testPkg?.Split('.')[0];
        if (p.IsTest && p.TestFramework is null && CodeIndex.IsTestPath(rel)) p.TestFramework = "(from Directory.Build.props)";
        return p;
    }

    private static ProjectInfo ParsePackageJson(string full, string rel)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(full), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var r = doc.RootElement;
        var p = new ProjectInfo
        {
            Kind = "node",
            Manifest = rel,
            Name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : DirName(rel),
        };
        foreach (var (prop, dev) in new[] { ("dependencies", false), ("devDependencies", true), ("peerDependencies", false) })
        {
            if (!r.TryGetProperty(prop, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            foreach (var d in deps.EnumerateObject()) p.Packages.Add((d.Name, d.Value.ValueKind == JsonValueKind.String ? d.Value.GetString() : null, dev));
        }
        if (r.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
            foreach (var s in scripts.EnumerateObject())
                if (s.Value.ValueKind == JsonValueKind.String) p.Scripts[s.Name] = s.Value.GetString()!;
        p.TestFramework = new[] { "vitest", "jest", "mocha", "ava", "playwright", "@playwright/test", "cypress" }
            .FirstOrDefault(t => p.Packages.Any(x => x.Name == t));
        p.IsTest = false;
        if (r.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.String) p.OutputType = "main: " + main.GetString();
        if (r.TryGetProperty("bin", out var bin)) p.OutputType = "bin";
        return p;
    }

    private static ProjectInfo ParsePython(string full, string rel, IEnumerable<string> allFiles)
    {
        var text = File.ReadAllText(full);
        var p = new ProjectInfo
        {
            Kind = "python",
            Manifest = rel,
            Name = PyName().Match(text) is { Success: true } m ? m.Groups[1].Value : DirName(rel),
        };
        foreach (Match d in PyDep().Matches(text)) p.Packages.Add((d.Groups[1].Value, null, false));
        var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        foreach (var req in allFiles.Where(f => Path.GetDirectoryName(f)?.Replace('\\', '/') == dir && Path.GetFileName(f).StartsWith("requirements", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var reqFull = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileName(req));
                foreach (var line in File.ReadLines(reqFull))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith('#') || t.StartsWith('-')) continue;
                    var m2 = ReqLine().Match(t);
                    if (m2.Success) p.Packages.Add((m2.Groups[1].Value, m2.Groups[2].Success ? m2.Groups[2].Value : null, req.Contains("dev", StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (IOException)
            {
                // Нечитаемый requirements — пропускаем.
            }
        }
        p.TestFramework = p.Packages.Any(x => x.Name.Equals("pytest", StringComparison.OrdinalIgnoreCase)) || text.Contains("pytest") ? "pytest" : null;
        return p;
    }

    private static ProjectInfo ParseGoMod(string full, string rel)
    {
        var text = File.ReadAllText(full);
        var p = new ProjectInfo { Kind = "go", Manifest = rel, Name = GoModule().Match(text) is { Success: true } m ? m.Groups[1].Value : DirName(rel) };
        foreach (Match d in GoRequire().Matches(text)) p.Packages.Add((d.Groups[1].Value, d.Groups[2].Value, d.Value.Contains("// indirect")));
        p.TestFramework = "go test";
        return p;
    }

    private static ProjectInfo ParseCargo(string full, string rel)
    {
        var text = File.ReadAllText(full);
        var p = new ProjectInfo { Kind = "rust", Manifest = rel, Name = PyName().Match(text) is { Success: true } m ? m.Groups[1].Value : DirName(rel) };
        var section = "";
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('['))
            {
                section = t;
                continue;
            }
            if (section is "[dependencies]" or "[dev-dependencies]" && CargoDep().Match(t) is { Success: true } d)
                p.Packages.Add((d.Groups[1].Value, d.Groups[2].Success ? d.Groups[2].Value : null, section == "[dev-dependencies]"));
        }
        p.TestFramework = "cargo test";
        return p;
    }

    private static ProjectInfo ParseSimple(string kind, string full, string rel, Regex nameRegex)
    {
        var text = File.ReadAllText(full);
        return new ProjectInfo { Kind = kind, Manifest = rel, Name = nameRegex.Match(text) is { Success: true } m ? m.Groups[1].Value : DirName(rel) };
    }

    private static string DirName(string rel)
    {
        var dir = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(dir) ? "(root)" : Path.GetFileName(dir);
    }

    [GeneratedRegex(@"(?m)^\s*name\s*=\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex PyName();

    [GeneratedRegex(@"(?m)^\s*""([A-Za-z0-9_.\-]+)(?:\[[^\]]*\])?\s*(?:[<>=!~]=?[^""]*)?""\s*,?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PyDep();

    [GeneratedRegex(@"^([A-Za-z0-9_.\-]+)(?:\[[^\]]*\])?\s*(?:[<>=!~]=\s*([^\s;,#]+))?", RegexOptions.CultureInvariant)]
    private static partial Regex ReqLine();

    [GeneratedRegex(@"(?m)^module\s+(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex GoModule();

    [GeneratedRegex(@"(?m)^\s*(?:require\s+)?([a-zA-Z0-9.\-_/]+\.[a-z]{2,}[^\s]*)\s+(v[0-9][^\s]*)", RegexOptions.CultureInvariant)]
    private static partial Regex GoRequire();

    [GeneratedRegex(@"^([A-Za-z0-9_\-]+)\s*=\s*(?:""([^""]+)""|\{[^}]*?version\s*=\s*""([^""]+)"")?", RegexOptions.CultureInvariant)]
    private static partial Regex CargoDep();

    [GeneratedRegex(@"<artifactId>([^<]+)</artifactId>", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactId();

    [GeneratedRegex(@"project\s*\(\s*([\w\-]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CMakeProject();

    // ───────────────────────── точки входа ─────────────────────────

    [GeneratedRegex(@"\bstatic\s+(?:async\s+)?(?:void|int|Task(?:<int>)?)\s+Main\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CsMain();

    [GeneratedRegex(@"WebApplication\.CreateBuilder|Host\.CreateDefaultBuilder|Host\.CreateApplicationBuilder|WebHost\.CreateDefaultBuilder", RegexOptions.CultureInvariant)]
    private static partial Regex CsHost();

    [GeneratedRegex(@"^if\s+__name__\s*==\s*['""]__main__['""]", RegexOptions.CultureInvariant)]
    private static partial Regex PyMain();

    private static void FindEntryPoints(ProjectMapResult map, CodeIndexResult index)
    {
        foreach (var f in index.Files)
        {
            if (map.EntryPoints.Count >= 40) break;
            if (f.IsTest) continue;
            string? why = null;
            switch (f.Lang)
            {
                case CodeLang.CSharp:
                    if (f.Lines.Any(l => CsMain().IsMatch(l))) why = "Main";
                    else if (f.Lines.Any(l => CsHost().IsMatch(l))) why = "host builder";
                    else if (Path.GetFileName(f.Display) == "Program.cs") why = "top-level statements";
                    break;
                case CodeLang.Python:
                    if (f.Lines.Any(l => PyMain().IsMatch(l))) why = "__main__";
                    break;
                case CodeLang.Go:
                    if (f.Lines.Any(l => l.StartsWith("package main", StringComparison.Ordinal)) && f.Lines.Any(l => l.StartsWith("func main()", StringComparison.Ordinal))) why = "func main";
                    break;
                case CodeLang.Rust:
                    if (f.Display.EndsWith("src/main.rs", StringComparison.Ordinal) || f.Display.Contains("/src/bin/", StringComparison.Ordinal)) why = "fn main";
                    break;
                case CodeLang.Pascal:
                    if (f.Display.EndsWith(".dpr", StringComparison.OrdinalIgnoreCase) || f.Display.EndsWith(".lpr", StringComparison.OrdinalIgnoreCase)) why = "program";
                    break;
                case CodeLang.Java:
                case CodeLang.Kotlin:
                    if (f.Lines.Any(l => l.Contains("public static void main(", StringComparison.Ordinal) || l.StartsWith("fun main(", StringComparison.Ordinal))) why = "main";
                    else if (f.Lines.Any(l => l.Contains("@SpringBootApplication", StringComparison.Ordinal))) why = "Spring Boot";
                    break;
                case CodeLang.TypeScript:
                    var n = Path.GetFileNameWithoutExtension(f.Display);
                    if (n is "main" or "index" or "server" or "app" && f.Display.Count(c => c == '/') <= 2) why = "conventional entry file";
                    break;
            }
            if (why is not null) map.EntryPoints.Add((f.Display, why));
        }
        foreach (var p in map.Projects.Where(p => p.Kind == "node" && p.OutputType is not null)) map.EntryPoints.Add((p.Manifest, p.OutputType!));
    }

    // ───────────────────────── команды ─────────────────────────

    private static void SuggestCommands(ProjectMapResult map, string root, HashSet<string> files, IReadOnlyList<string>? allowlist)
    {
        void Add(string kind, string cmd)
        {
            if (map.Commands.Any(c => c.Kind == kind && c.Command == cmd)) return;
            bool allowed;
            try
            {
                VerifyCommand.Validate(cmd, allowlist);
                allowed = true;
            }
            catch (ToolException)
            {
                allowed = false;
            }
            map.Commands.Add(new SuggestedCommand(kind, cmd, allowed));
        }

        var hasSln = files.Any(f => !f.Contains('/') && (f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)));
        var dotnet = map.Projects.Where(p => p.Kind == "dotnet").ToList();
        if (hasSln || dotnet.Count > 0)
        {
            var sln = files.Where(f => !f.Contains('/') && (f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Length).FirstOrDefault();
            var target = sln is not null && files.Count(f => !f.Contains('/') && (f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))) > 1
                ? " " + sln : hasSln ? "" : dotnet.Count == 1 ? " " + dotnet[0].Manifest : "";
            Add("build", "dotnet build" + target);
            if (dotnet.Any(p => p.IsTest))
                Add("test", IsMtp(root) && sln is not null ? $"dotnet test --solution {sln}" : "dotnet test" + target);
            Add("format", "dotnet format --verify-no-changes" + target);
            Add("lint", "dotnet build" + target + " -warnaserror");
        }
        var rootNode = map.Projects.FirstOrDefault(p => p.Kind == "node" && !p.Manifest.Contains('/'));
        if (rootNode is not null)
        {
            var pm = files.Contains("pnpm-lock.yaml") ? "pnpm" : files.Contains("yarn.lock") ? "yarn" : "npm";
            string Script(string s) => pm == "npm" ? (s == "test" ? "npm test" : $"npm run {s}") : pm == "yarn" ? (s == "test" ? "yarn test" : $"yarn run {s}") : $"pnpm run {s}";
            foreach (var (kind, names) in new[] { ("test", new[] { "test" }), ("build", new[] { "build", "compile" }), ("lint", new[] { "lint", "typecheck" }), ("format", new[] { "format", "fmt" }) })
                foreach (var s in names.Where(rootNode.Scripts.ContainsKey)) Add(kind, Script(s));
            if (files.Contains("tsconfig.json")) Add("build", "npx tsc --noEmit");
            if (rootNode.Packages.Any(p => p.Name == "eslint")) Add("lint", "npx eslint .");
            if (rootNode.Packages.Any(p => p.Name == "prettier")) Add("format", "npx prettier --check .");
        }
        if (map.Projects.Any(p => p.Kind == "python") || files.Any(f => f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
        {
            if (map.Projects.Any(p => p.TestFramework == "pytest") || files.Any(f => Path.GetFileName(f).StartsWith("test_", StringComparison.Ordinal))) Add("test", "pytest -q");
            Add("lint", "ruff check .");
            Add("format", "ruff format --check .");
        }
        if (map.Projects.Any(p => p.Kind == "go"))
        {
            Add("build", "go build ./...");
            Add("test", "go test ./...");
            Add("lint", "go vet ./...");
        }
        if (map.Projects.Any(p => p.Kind == "rust"))
        {
            Add("build", "cargo build");
            Add("test", "cargo test");
            Add("lint", "cargo clippy");
            Add("format", "cargo fmt --check");
        }
        if (map.Projects.Any(p => p.Kind == "maven")) Add("test", "mvn -q test");
        if (map.Projects.Any(p => p.Kind == "gradle")) Add("test", files.Contains("gradlew.bat") || files.Contains("gradlew") ? "gradlew test" : "gradle test");
        if (map.Projects.Any(p => p.Kind == "cmake")) Add("build", "cmake --build build");
        if (files.Contains("Makefile"))
        {
            Add("build", "make");
            Add("test", "make test");
        }
        foreach (var d in map.Projects.Where(p => p.Kind == "delphi").Take(3)) Add("build", "msbuild " + d.Manifest);
    }

    /// <summary>Тесты на Microsoft.Testing.Platform (dotnet test --project/--solution): global.json с "test": { "runner": … }.</summary>
    internal static bool IsMtp(string root)
    {
        try
        {
            var gj = Path.Combine(root, "global.json");
            return File.Exists(gj) && File.ReadAllText(gj).Contains("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Проект, которому принадлежит файл (самый глубокий манифест выше по пути).</summary>
    public static ProjectInfo? OwnerOf(ProjectMapResult map, string display)
    {
        var d = display.Replace('\\', '/');
        return map.Projects.Where(p => p.Dir.Length == 0 || d.StartsWith(p.Dir + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Dir.Length).FirstOrDefault();
    }
}
