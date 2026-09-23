using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_project_map: карта незнакомого репозитория одним вызовом — проекты и их связи, языки, точки входа, тесты, команды,
/// папки; отдельные секции: HTTP-маршруты, конфигурация, переменные окружения, CLI, CI, Docker, правила репозитория, соглашения.
/// </summary>
internal static partial class ProjectMapTool
{
    public static readonly string[] Sections = ["overview", "entrypoints", "routes", "config", "env", "cli", "ci", "docker", "rules", "conventions"];

    public static async Task<string> RunAsync(ToolContext ctx, string? section, int maxResults)
    {
        var sec = string.IsNullOrWhiteSpace(section) ? "overview" : section.Trim().ToLowerInvariant();
        if (!Sections.Contains(sec)) throw new ToolException("section must be one of: " + string.Join(", ", Sections) + ".");
        maxResults = Math.Clamp(maxResults <= 0 ? 80 : maxResults, 5, 500);
        ctx.Progress.Report("Mapping the project…");
        var (map, index) = await ProjectMap.BuildAsync(ctx, ctx.Ct).ConfigureAwait(false);
        return sec switch
        {
            "overview" => Overview(map),
            "entrypoints" => EntryPoints(map, index, maxResults),
            "routes" => Scan(index, RouteRules, maxResults, "HTTP routes"),
            "config" => await ConfigAsync(ctx, index, maxResults).ConfigureAwait(false),
            "env" => Env(ctx, index, maxResults),
            "cli" => Scan(index, CliRules, maxResults, "CLI commands/options"),
            "ci" => FilesDigest(ctx, map.CiFiles, CiLine(), maxResults, "CI pipelines"),
            "docker" => FilesDigest(ctx, map.DockerFiles, DockerLine(), maxResults, "Docker"),
            "rules" => Rules(ctx, map),
            "conventions" => Conventions(index),
            _ => "",
        };
    }

    private static string Overview(ProjectMapResult map)
    {
        var sb = new StringBuilder($"files: {map.TotalFiles}");
        if (map.Note is not null) sb.Append($" ({map.Note})");
        sb.Append('\n');
        if (map.Languages.Count > 0)
            sb.Append("languages: ").Append(string.Join(", ", map.Languages.Take(8).Select(l => $"{l.Lang} {l.Files} files/{l.Lines} lines"))).Append('\n');
        if (map.Projects.Count > 0)
        {
            sb.Append("projects:\n");
            foreach (var p in map.Projects.OrderBy(p => p.IsTest).ThenBy(p => p.Manifest, StringComparer.OrdinalIgnoreCase).Take(60))
            {
                sb.Append($"  {p.Manifest} [{p.Kind}{(p.Framework is null ? "" : " " + p.Framework)}{(p.OutputType is null ? "" : ", " + p.OutputType)}{(p.IsTest ? ", tests" + (p.TestFramework is null ? "" : ": " + p.TestFramework) : "")}]");
                if (p.ProjectRefs.Count > 0) sb.Append(" → ").Append(string.Join(", ", p.ProjectRefs));
                var pk = p.Packages.Where(x => !x.Dev).Select(x => x.Name).Take(10).ToList();
                if (pk.Count > 0) sb.Append($"\n      packages: {string.Join(", ", pk)}{(p.Packages.Count > pk.Count ? $" (+{p.Packages.Count - pk.Count})" : "")}");
                if (p.Scripts.Count > 0) sb.Append("\n      scripts: ").Append(string.Join(", ", p.Scripts.Keys.Take(12)));
                sb.Append('\n');
            }
            if (map.Projects.Count > 60) sb.Append($"  … {map.Projects.Count - 60} more\n");
        }
        if (map.EntryPoints.Count > 0)
            sb.Append("entry points: ").Append(string.Join(", ", map.EntryPoints.Take(12).Select(e => $"{e.Path} ({e.Why})"))).Append('\n');
        if (map.Commands.Count > 0)
        {
            sb.Append("commands (✓ = allowed for local_verify):\n");
            foreach (var c in map.Commands) sb.Append($"  {c.Kind}: {c.Command}{(c.Allowed ? " ✓" : "")}\n");
        }
        if (map.TopFolders.Count > 0) sb.Append("top folders: ").Append(string.Join(", ", map.TopFolders.Select(f => $"{f.Folder} ({f.Files})"))).Append('\n');
        if (map.RuleFiles.Count > 0) sb.Append("repo rules/docs for agents: ").Append(string.Join(", ", map.RuleFiles)).Append(" (section=rules)\n");
        if (map.CiFiles.Count > 0) sb.Append("CI: ").Append(string.Join(", ", map.CiFiles.Take(8))).Append('\n');
        if (map.DockerFiles.Count > 0) sb.Append("Docker: ").Append(string.Join(", ", map.DockerFiles.Take(8))).Append('\n');
        sb.Append("more: section=entrypoints|routes|config|env|cli|ci|docker|rules|conventions");
        return sb.ToString();
    }

    private static string EntryPoints(ProjectMapResult map, CodeIndexResult index, int max)
    {
        var sb = new StringBuilder();
        foreach (var (path, why) in map.EntryPoints.Take(max))
        {
            sb.Append($"{path} ({why})");
            var f = index.Files.FirstOrDefault(x => x.Display == path);
            var main = f?.Symbols.FirstOrDefault(s => s.Name is "Main" or "main");
            if (main is not null) sb.Append($" → {main.QualifiedName}:{main.Line}");
            sb.Append('\n');
        }
        var hosted = Scan(index, HostedRules, max, "background services/workers");
        return (sb.Length == 0 ? "No entry points detected.\n" : sb.ToString()) + "\n" + hosted;
    }

    // ───────────────────────── построчные извлечения ─────────────────────────

    private sealed record ScanRule(Regex Regex, string Label, CodeLang? Lang = null);

    private const RegexOptions R = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly ScanRule[] RouteRules =
    [
        new(new Regex(@"\[Http(Get|Post|Put|Delete|Patch|Head|Options)(?:\(\s*""(?<path>[^""]*)"")?", R), "{1}", CodeLang.CSharp),
        new(new Regex(@"\[Route\(\s*""(?<path>[^""]*)""", R), "ROUTE", CodeLang.CSharp),
        new(new Regex(@"\.Map(Get|Post|Put|Delete|Patch|Methods|Group|Hub|Grpc\w*)(?:<[^>]*>)?\(\s*(?:@)?""(?<path>[^""]*)""", R), "MAP {1}", CodeLang.CSharp),
        new(new Regex(@"\b(?:app|router|server|api|route)\.(get|post|put|delete|patch|all|use)\(\s*['""`](?<path>/[^'""`]*)['""`]", R), "{1}", CodeLang.TypeScript),
        new(new Regex(@"@(?:\w+\.)?(get|post|put|delete|patch|route|api_route|websocket)\(\s*['""](?<path>/[^'""]*)['""]", R), "{1}", CodeLang.Python),
        new(new Regex(@"\bpath\(\s*['""](?<path>[^'""]*)['""]\s*,", R), "DJANGO", CodeLang.Python),
        new(new Regex(@"@(Get|Post|Put|Delete|Patch|Request)Mapping\(\s*(?:value\s*=\s*|path\s*=\s*)?""?(?<path>[^""),]*)", R), "{1}", CodeLang.Java),
        new(new Regex(@"@(Get|Post|Put|Delete|Patch|Controller)\(\s*['""]?(?<path>[^'"")]*)", R), "NEST {1}", CodeLang.TypeScript),
        new(new Regex(@"\b(?:http\.HandleFunc|mux\.HandleFunc|\.Handle|\.HandleFunc)\(\s*""(?<path>[^""]*)""", R), "HANDLE", CodeLang.Go),
        new(new Regex(@"\.(GET|POST|PUT|DELETE|PATCH|Any|Group)\(\s*""(?<path>[^""]*)""", R), "{1}", CodeLang.Go),
        new(new Regex(@"#\[(get|post|put|delete|patch)\(\s*""(?<path>[^""]*)""", R), "{1}", CodeLang.Rust),
        new(new Regex(@"\.route\(\s*""(?<path>[^""]*)""", R), "ROUTE", CodeLang.Rust),
    ];

    private static readonly ScanRule[] CliRules =
    [
        new(new Regex(@"new\s+(?:Option|Argument)<[^>]+>\(\s*(?:name:\s*)?""(?<path>[^""]+)""", R), "option", CodeLang.CSharp),
        new(new Regex(@"new\s+(?:Root)?Command\(\s*""(?<path>[^""]+)""", R), "command", CodeLang.CSharp),
        new(new Regex(@"(?:case|==|Equals\(|Contains\()\s*""(?<path>--?[\w-]+)""", R), "arg", CodeLang.CSharp),
        new(new Regex(@"\[(?:Option|Value|Verb)\(\s*(?:'\w'\s*,\s*)?""(?<path>[^""]+)""", R), "option", CodeLang.CSharp),
        new(new Regex(@"add_argument\(\s*['""](?<path>[^'""]+)['""]", R), "argparse", CodeLang.Python),
        new(new Regex(@"@click\.(?:option|argument|command)\(\s*['""](?<path>[^'""]+)['""]", R), "click", CodeLang.Python),
        new(new Regex(@"\.(?:option|requiredOption|command|argument)\(\s*['""`](?<path>[^'""`]+)['""`]", R), "commander", CodeLang.TypeScript),
        new(new Regex(@"flag\.(?:String|Int|Bool|Duration|Float64|StringVar|IntVar|BoolVar)\([^""]*""(?<path>[^""]+)""", R), "flag", CodeLang.Go),
        new(new Regex(@"Use:\s*""(?<path>[^""]+)""", R), "cobra", CodeLang.Go),
        new(new Regex(@"#\[(?:arg|command|clap)\((?<path>[^)]*)\)", R), "clap", CodeLang.Rust),
    ];

    private static readonly ScanRule[] HostedRules =
    [
        new(new Regex(@"class\s+(?<path>\w+)\s*(?:\([^)]*\))?\s*:\s*(?:BackgroundService|IHostedService)", R), "hosted service", CodeLang.CSharp),
        new(new Regex(@"AddHostedService<(?<path>\w+)>", R), "registered hosted service", CodeLang.CSharp),
        new(new Regex(@"@(?:app\.task|shared_task|celery\.task|scheduler\.scheduled_job)\b(?<path>)", R), "task", CodeLang.Python),
        new(new Regex(@"new\s+(?:Worker|CronJob)\(\s*['""]?(?<path>[^'"",)]*)", R), "worker", CodeLang.TypeScript),
    ];

    private static string Scan(CodeIndexResult index, ScanRule[] rules, int max, string title)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var f in index.Files.Where(f => !f.IsTest))
        {
            for (var i = 0; i < f.Lines.Length && n < max; i++)
            {
                var line = f.Lines[i];
                if (line.Length > 1000) continue;
                foreach (var r in rules)
                {
                    if (r.Lang is { } lang && lang != f.Lang) continue;
                    var m = r.Regex.Match(line);
                    if (!m.Success || CodeIndex.IsCommentLine(line)) continue;
                    var label = r.Label.Contains("{1}", StringComparison.Ordinal) ? r.Label.Replace("{1}", m.Groups[1].Value.ToUpperInvariant()) : r.Label;
                    var target = m.Groups["path"].Value;
                    var handler = Symbols.Enclosing(f.Symbols, i + 1);
                    // Атрибут стоит над методом: обработчик — следующее объявление.
                    if (f.Lang == CodeLang.CSharp && line.TrimStart().StartsWith('['))
                        handler = f.Symbols.FirstOrDefault(s => s.Line > i + 1 && s.Line <= i + 6) ?? handler;
                    sb.Append($"{label} {target}".TrimEnd()).Append($"  {f.Display}:{i + 1}").Append(handler is not null ? $" → {handler.QualifiedName}" : "").Append('\n');
                    n++;
                    break;
                }
            }
            if (n >= max) break;
        }
        if (n == 0) return $"No {title} found.";
        return $"{title} ({n}{(n >= max ? "+, raise max_results" : "")}):\n" + sb;
    }

    // ───────────────────────── конфигурация и окружение ─────────────────────────

    [GeneratedRegex(@"(?:Configuration|config(?:uration)?|_config|cfg)\s*\[\s*""(?<key>[^""]+)""\s*\]|GetSection\(\s*""(?<key>[^""]+)""|GetValue<[^>]+>\(\s*""(?<key>[^""]+)""|GetConnectionString\(\s*""(?<key>[^""]+)""|Configure<(?<key>\w+)>|config\.get\(\s*['""](?<key>[^'""]+)['""]|settings\.(?<key>[A-Z][A-Z0-9_]{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigKey();

    [GeneratedRegex(@"GetEnvironmentVariable\(\s*""(?<key>[^""]+)""|process\.env\.(?<key>[A-Za-z_][A-Za-z0-9_]*)|process\.env\[\s*['""](?<key>[^'""]+)['""]|os\.environ(?:\.get)?\(?\[?\s*['""](?<key>[^'""]+)['""]|os\.getenv\(\s*['""](?<key>[^'""]+)['""]|os\.Getenv\(\s*""(?<key>[^""]+)""|env::var\(\s*""(?<key>[^""]+)""|System\.getenv\(\s*""(?<key>[^""]+)""|import\.meta\.env\.(?<key>\w+)|%(?<key>[A-Z][A-Z0-9_]{2,})%|\$\{(?<key>[A-Z][A-Z0-9_]{2,})(?::-[^}]*)?\}|\{env:(?<key>[A-Z][A-Z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvVar();

    private static async Task<string> ConfigAsync(ToolContext ctx, CodeIndexResult index, int max)
    {
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in index.Files.Where(f => !f.IsTest))
            Collect(f, ConfigKey(), keys, max);
        var sb = new StringBuilder();
        // Файлы настроек: ключи верхних уровней.
        // Только ключи (значения могут быть секретными и не выводятся); файлы — через те же проверки, что и чтение.
        var settingsFiles = (await FileGatherer.ListFilesAsync(["**/appsettings*.json", "**/config/*.json", "**/settings*.json"], ctx.Roots,
            ctx.GatherOptions with { MaxFiles = 20 }, new GatherResult(), ctx.Ct).ConfigureAwait(false)).Take(10).ToList();
        foreach (var s in settingsFiles)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(s), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var flat = new List<string>();
                Flatten(doc.RootElement, "", 0, flat);
                sb.Append(ctx.Display(s)).Append(": ").Append(string.Join(", ", flat.Take(40))).Append(flat.Count > 40 ? " …" : "").Append('\n');
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                sb.Append(ctx.Display(s)).Append(": (unreadable)\n");
            }
        }
        if (keys.Count == 0 && sb.Length == 0) return "No configuration keys found.";
        if (keys.Count > 0)
        {
            sb.Append("keys read in code:\n");
            foreach (var (k, where) in keys.OrderBy(k => k.Key, StringComparer.Ordinal).Take(max))
                sb.Append($"  {k}  ← {string.Join(", ", where.Take(3))}{(where.Count > 3 ? $" (+{where.Count - 3})" : "")}\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static void Flatten(System.Text.Json.JsonElement e, string prefix, int depth, List<string> result)
    {
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object || depth >= 3)
        {
            if (prefix.Length > 0) result.Add(prefix);
            return;
        }
        foreach (var p in e.EnumerateObject()) Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + ":" + p.Name, depth + 1, result);
    }

    private static string Env(ToolContext ctx, CodeIndexResult index, int max)
    {
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in index.Files) Collect(f, EnvVar(), keys, max);
        if (keys.Count == 0) return "No environment variables found.";
        var sb = new StringBuilder($"environment variables ({keys.Count}):\n");
        foreach (var (k, where) in keys.OrderBy(k => k.Key, StringComparer.Ordinal).Take(max))
            sb.Append($"  {k}  ← {string.Join(", ", where.Take(3))}{(where.Count > 3 ? $" (+{where.Count - 3})" : "")}\n");
        return sb.ToString().TrimEnd();
    }

    private static void Collect(SourceFile f, Regex regex, Dictionary<string, List<string>> keys, int max)
    {
        for (var i = 0; i < f.Lines.Length; i++)
        {
            var line = f.Lines[i];
            if (line.Length > 1000) continue;
            foreach (Match m in regex.Matches(line))
            {
                var k = m.Groups["key"].Value;
                if (k.Length == 0 || k.Length > 120) continue;
                if (!keys.TryGetValue(k, out var l))
                {
                    if (keys.Count >= max * 3) return;
                    keys[k] = l = [];
                }
                if (l.Count < 10) l.Add($"{f.Display}:{i + 1}");
            }
        }
    }

    // ───────────────────────── CI, Docker, правила ─────────────────────────

    [GeneratedRegex(@"^\s*(?:(?:name|runs-on|uses|run|image|stage|script|on|jobs|- run|- uses|- name|trigger|pool|task)\s*:|-\s+(?:run|uses|name)\s*:)", RegexOptions.CultureInvariant)]
    private static partial Regex CiLine();

    [GeneratedRegex(@"^\s*(?:FROM|EXPOSE|ENTRYPOINT|CMD|WORKDIR|ENV|ARG|USER|services:|image:|build:|ports:|depends_on:|environment:|volumes:|\w[\w-]*:\s*$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DockerLine();

    private static string FilesDigest(ToolContext ctx, List<string> files, Regex keep, int max, string title)
    {
        if (files.Count == 0) return $"No {title} files found.";
        var sb = new StringBuilder();
        foreach (var rel in files.Take(10))
        {
            var full = Path.Combine(ctx.Roots[0], rel.Replace('/', Path.DirectorySeparatorChar));
            sb.Append(rel).Append('\n');
            var n = 0;
            try
            {
                foreach (var line in File.ReadLines(full))
                {
                    if (!keep.IsMatch(line)) continue;
                    var t = line.TrimEnd();
                    sb.Append("  ").Append(t.Length > 160 ? t[..160] + "…" : t).Append('\n');
                    if (++n >= max) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sb.Append("  (unreadable)\n");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Rules(ToolContext ctx, ProjectMapResult map)
    {
        if (map.RuleFiles.Count == 0) return "No repository rule files (CLAUDE.md, AGENTS.md, .cursorrules, CONTRIBUTING.md, .editorconfig…) found.";
        var sb = new StringBuilder();
        foreach (var rel in map.RuleFiles)
        {
            var full = Path.Combine(ctx.Roots[0], rel.Replace('/', Path.DirectorySeparatorChar));
            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (text.Length > 6000) text = text[..6000] + "\n…[truncated; read the file for the rest]";
            sb.Append("=== ").Append(rel).Append(" ===\n").Append(text.TrimEnd()).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────── соглашения ─────────────────────────

    private static string Conventions(CodeIndexResult index)
    {
        var sb = new StringBuilder();
        foreach (var g in index.Files.Where(f => !f.IsTest).GroupBy(f => f.Lang).OrderByDescending(g => g.Count()).Take(3))
        {
            var files = g.Take(300).ToList();
            var lines = files.SelectMany(f => f.Lines).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) continue;
            var tabs = lines.Count(l => l.StartsWith('\t'));
            var spaces = lines.Where(l => l.StartsWith(' ')).Select(l => l.Length - l.TrimStart(' ').Length).Where(n => n > 0).ToList();
            var unit = IndentUnit(files);
            sb.Append($"{g.Key} ({g.Count()} files):\n");
            sb.Append($"  indentation: {(tabs > spaces.Count ? "tabs" : $"{unit} spaces")}\n");
            var maxLen = lines.Select(l => l.Length).OrderByDescending(l => l).Skip(lines.Count / 100).FirstOrDefault();
            sb.Append($"  line length: 99% of lines ≤ {maxLen} chars\n");
            var symbols = files.SelectMany(f => f.Symbols).ToList();
            if (g.Key == CodeLang.CSharp)
            {
                var fileScoped = files.Count(f => f.Lines.Any(l => Regex.IsMatch(l, @"^namespace\s+[\w.]+\s*;")));
                sb.Append($"  namespaces: {(fileScoped * 2 >= files.Count ? "file-scoped" : "block")}\n");
                var allman = lines.Count(l => l.Trim() == "{");
                var knr = lines.Count(l => l.TrimEnd().EndsWith(" {", StringComparison.Ordinal) && !l.Contains("=>"));
                sb.Append($"  braces: {(allman >= knr ? "Allman (own line)" : "K&R (same line)")}\n");
                var underscore = lines.Count(l => Regex.IsMatch(l, @"^\s*private\s+(?:readonly\s+)?[\w<>\[\],.?]+\s+_\w+\s*[;=]"));
                var plain = lines.Count(l => Regex.IsMatch(l, @"^\s*private\s+(?:readonly\s+)?[\w<>\[\],.?]+\s+[a-z]\w*\s*[;=]"));
                if (underscore + plain > 0) sb.Append($"  private fields: {(underscore >= plain ? "_camelCase" : "camelCase")}\n");
                var asyncMethods = symbols.Where(s => s.Kind == "method" && s.Signature.Contains("async ", StringComparison.Ordinal)).ToList();
                if (asyncMethods.Count > 0)
                    sb.Append($"  async methods with 'Async' suffix: {asyncMethods.Count(s => s.Name.EndsWith("Async", StringComparison.Ordinal)) * 100 / asyncMethods.Count}%\n");
                var sealedCount = symbols.Count(s => s.Kind == "class" && s.Signature.Contains("sealed ", StringComparison.Ordinal));
                var classes = symbols.Count(s => s.Kind == "class");
                if (classes > 0) sb.Append($"  sealed classes: {sealedCount * 100 / classes}%, primary constructors: {symbols.Count(s => s.Kind == "class" && s.Signature.Contains('('))} classes\n");
                sb.Append($"  var usage: {lines.Count(l => Regex.IsMatch(l, @"^\s*var\s+\w+\s*=")) * 100 / Math.Max(1, lines.Count(l => Regex.IsMatch(l, @"^\s*(var|[A-Z][\w<>\[\],.?]*)\s+\w+\s*=\s*")))}% of local declarations\n");
            }
            if (g.Key == CodeLang.TypeScript)
            {
                var semi = lines.Count(l => l.TrimEnd().EndsWith(';'));
                var single = lines.Count(l => l.Contains('\'')) ;
                var dbl = lines.Count(l => l.Contains('"'));
                sb.Append($"  semicolons: {(semi * 3 > lines.Count ? "yes" : "no")}, quotes: {(single >= dbl ? "single" : "double")}\n");
            }
            var types = symbols.Where(s => s.IsType && s.Kind != "namespace").Select(s => s.Name).ToList();
            var funcs = symbols.Where(s => s.Kind is "method" or "function").Select(s => s.Name).ToList();
            if (types.Count > 0) sb.Append($"  type names: {Casing(types)}; function names: {Casing(funcs)}\n");
            var testFile = index.Files.FirstOrDefault(f => f.IsTest && f.Lang == g.Key);
            if (testFile is not null) sb.Append($"  example test file: {testFile.Display}\n");
            var sample = files.Where(f => f.Lines.Length is > 40 and < 250).OrderByDescending(f => f.Symbols.Count).FirstOrDefault();
            if (sample is not null) sb.Append($"  representative file for style: {sample.Display}\n");
        }
        return sb.Length == 0 ? "No source files to analyze." : sb.ToString().TrimEnd();
    }

    /// <summary>Шаг отступа: самое частое приращение отступа между соседними непустыми строками (выравнивание продолжений не сбивает).</summary>
    internal static int IndentUnit(IEnumerable<SourceFile> files)
    {
        var deltas = new Dictionary<int, int>();
        foreach (var f in files)
        {
            var prev = 0;
            foreach (var l in f.Lines)
            {
                if (l.Trim().Length == 0 || l.StartsWith('\t')) continue;
                var indent = l.Length - l.TrimStart(' ').Length;
                var d = indent - prev;
                if (d is 2 or 3 or 4 or 8) deltas[d] = deltas.GetValueOrDefault(d) + 1;
                prev = indent;
            }
        }
        return deltas.Count == 0 ? 4 : deltas.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First().Key;
    }

    private static string Casing(List<string> names)
    {
        if (names.Count == 0) return "n/a";
        var pascal = names.Count(n => char.IsUpper(n[0]) && !n.Contains('_'));
        var camel = names.Count(n => char.IsLower(n[0]) && !n.Contains('_') && n.Any(char.IsUpper));
        var snake = names.Count(n => n.Contains('_') && n == n.ToLowerInvariant());
        var best = new[] { ("PascalCase", pascal), ("camelCase", camel), ("snake_case", snake) }.OrderByDescending(x => x.Item2).First();
        return $"{best.Item1} ({best.Item2 * 100 / names.Count}%)";
    }
}
