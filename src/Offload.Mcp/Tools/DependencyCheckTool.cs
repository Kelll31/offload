using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_dependency_check: зависимости проекта — list (прямые пакеты по проектам), graph (ссылки проектов; with from/to — путь
/// «почему A зависит от B»), outdated / vulnerable (обращаются к реестрам пакетов через штатные команды dotnet/npm/pip),
/// licenses (из локальных кэшей NuGet и node_modules, без сети), unused (эвристика: пакет не импортируется в коде).
/// </summary>
internal static partial class DependencyCheckTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? action, string? from, string? to, int maxResults)
    {
        var act = (action ?? "list").Trim().ToLowerInvariant();
        maxResults = Math.Clamp(maxResults <= 0 ? 80 : maxResults, 5, 500);
        ctx.Progress.Report("Reading project manifests…");
        var (map, index) = await ProjectMap.BuildAsync(ctx, ctx.Ct).ConfigureAwait(false);
        if (map.Projects.Count == 0) throw new ToolException("No package manifests found (csproj, package.json, pyproject/requirements, go.mod, Cargo.toml…).");
        return act switch
        {
            "list" => List(map, maxResults),
            "graph" => Graph(map, from, to),
            "outdated" => await OnlineAsync(ctx, map, vulnerable: false, maxResults).ConfigureAwait(false),
            "vulnerable" => await OnlineAsync(ctx, map, vulnerable: true, maxResults).ConfigureAwait(false),
            "licenses" => Licenses(ctx, map, maxResults),
            "unused" => Unused(map, index, maxResults),
            _ => throw new ToolException("action must be list, graph, outdated, vulnerable, licenses or unused."),
        };
    }

    private static string List(ProjectMapResult map, int max)
    {
        var sb = new StringBuilder();
        foreach (var p in map.Projects.Where(p => p.Packages.Count > 0 || p.ProjectRefs.Count > 0))
        {
            sb.Append($"{p.Manifest} [{p.Kind}]\n");
            if (p.ProjectRefs.Count > 0) sb.Append("  projects: ").Append(string.Join(", ", p.ProjectRefs)).Append('\n');
            foreach (var (name, version, dev) in p.Packages.Take(max)) sb.Append($"  {name} {version ?? "(version from central management/lock)"}{(dev ? " (dev)" : "")}\n");
            if (p.Packages.Count > max) sb.Append($"  … {p.Packages.Count - max} more\n");
        }
        var central = map.Projects.Any(p => p.Kind == "dotnet") ? " · versions may come from Directory.Packages.props" : "";
        return sb.Length == 0 ? "No dependencies declared." : sb.Append($"{map.Projects.Sum(p => p.Packages.Count)} package references{central}").ToString();
    }

    private static string Graph(ProjectMapResult map, string? from, string? to)
    {
        var nodes = map.Projects.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Edges(string n) => nodes.TryGetValue(n, out var p) ? p.ProjectRefs.Concat(p.Packages.Select(x => x.Name)) : [];
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            var sb = new StringBuilder("project graph (→ references):\n");
            foreach (var p in map.Projects.OrderBy(p => p.ProjectRefs.Count))
                sb.Append($"  {p.Name}{(p.IsTest ? " (tests)" : "")} → {(p.ProjectRefs.Count == 0 ? "-" : string.Join(", ", p.ProjectRefs))}\n");
            var dependents = map.Projects.SelectMany(p => p.ProjectRefs.Select(r => (Ref: r, By: p.Name))).GroupBy(x => x.Ref, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} ({g.Count()})");
            sb.Append("most depended-on: ").Append(string.Join(", ", dependents));
            return sb.ToString();
        }
        // BFS от from до to (по ссылкам проектов и пакетам).
        var queue = new Queue<string>();
        var prev = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [from.Trim()] = null };
        queue.Enqueue(from.Trim());
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n.Equals(to.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var path = new List<string>();
                for (string? x = n; x is not null; x = prev[x]) path.Add(x);
                path.Reverse();
                return $"{from} depends on {to} via: {string.Join(" → ", path)}";
            }
            foreach (var e in Edges(n))
                if (prev.TryAdd(e, n)) queue.Enqueue(e);
        }
        return $"No dependency path from {from} to {to} among declared project/package references (transitive package dependencies are not resolved offline; try action=outdated for dotnet list output).";
    }

    private static async Task<string> OnlineAsync(ToolContext ctx, ProjectMapResult map, bool vulnerable, int max)
    {
        var sb = new StringBuilder();
        var timeout = TimeSpan.FromMinutes(6);
        var root = ctx.Roots[0];
        var sln = Directory.EnumerateFiles(root, "*.sln*").Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).FirstOrDefault();
        var dotnetTarget = sln ?? map.Projects.FirstOrDefault(p => p.Kind == "dotnet")?.Manifest;
        if (dotnetTarget is not null && SafeArg().IsMatch(dotnetTarget))
        {
            var cmd = $"dotnet list {dotnetTarget} package {(vulnerable ? "--vulnerable --include-transitive" : "--outdated")} --format json";
            var run = await VerifyTool.RunLoggedAsync(ctx, cmd, timeout).ConfigureAwait(false);
            sb.Append(ParseDotnet(File.ReadAllText(run.LogPath), vulnerable, max, cmd));
        }
        var node = map.Projects.FirstOrDefault(p => p.Kind == "node" && !p.Manifest.Contains('/'));
        if (node is not null && (Directory.Exists(Path.Combine(root, "node_modules")) || vulnerable))
        {
            var cmd = vulnerable ? "npm audit --json" : "npm outdated --json";
            var run = await VerifyTool.RunLoggedAsync(ctx, cmd, timeout).ConfigureAwait(false);
            sb.Append(ParseNpm(File.ReadAllText(run.LogPath), vulnerable, max, cmd));
        }
        if (map.Projects.Any(p => p.Kind == "python") && !vulnerable)
        {
            var run = await VerifyTool.RunLoggedAsync(ctx, "pip list --outdated --format=json", timeout).ConfigureAwait(false);
            sb.Append(ParsePip(File.ReadAllText(run.LogPath), map, max));
        }
        if (sb.Length == 0) return $"No supported package manager for action={(vulnerable ? "vulnerable" : "outdated")} (dotnet, npm, pip). Other ecosystems: run their tool via local_verify if allowlisted.";
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"^[\w.\-/]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeArg();

    private static JsonDocument? ParseJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonDocument.Parse(text[start..(end + 1)]); }
        catch (JsonException) { return null; }
    }

    private static string ParseDotnet(string output, bool vulnerable, int max, string cmd)
    {
        using var doc = ParseJson(output);
        if (doc is null) return $"`{cmd}`: could not parse the output (restore may have failed): {Short(output.Trim(), 300)}\n";
        var sb = new StringBuilder($"`{cmd}`:\n");
        var n = 0;
        foreach (var proj in doc.RootElement.TryGetProperty("projects", out var ps) ? ps.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            var name = proj.TryGetProperty("path", out var pp) ? Path.GetFileNameWithoutExtension(pp.GetString()) : "?";
            foreach (var fw in proj.TryGetProperty("frameworks", out var fws) ? fws.EnumerateArray() : Enumerable.Empty<JsonElement>())
                foreach (var kind in new[] { "topLevelPackages", "transitivePackages" })
                    foreach (var pkg in fw.TryGetProperty(kind, out var list) ? list.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    {
                        var id = pkg.GetProperty("id").GetString();
                        var resolved = pkg.TryGetProperty("resolvedVersion", out var rv) ? rv.GetString() : "?";
                        if (vulnerable)
                        {
                            foreach (var v in pkg.TryGetProperty("vulnerabilities", out var vs) ? vs.EnumerateArray() : Enumerable.Empty<JsonElement>())
                            {
                                sb.Append($"  [{Str(v, "severity")}] {name}: {id} {resolved}{(kind.StartsWith("trans") ? " (transitive)" : "")} {Str(v, "advisoryurl")}\n");
                                if (++n >= max) return sb.ToString();
                            }
                        }
                        else if (pkg.TryGetProperty("latestVersion", out var lv))
                        {
                            sb.Append($"  {name}: {id} {resolved} → {lv.GetString()}\n");
                            if (++n >= max) return sb.ToString();
                        }
                    }
        }
        if (n == 0) sb.Append(vulnerable ? "  no known vulnerable packages\n" : "  all packages are up to date\n");
        return sb.ToString();
    }

    private static string Str(JsonElement e, string name) =>
        e.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";

    private static string ParseNpm(string output, bool vulnerable, int max, string cmd)
    {
        using var doc = ParseJson(output);
        if (doc is null) return output.Trim().Length == 0 || output.Contains("{}") ? $"`{cmd}`: nothing to report\n" : $"`{cmd}`: could not parse: {Short(output.Trim(), 300)}\n";
        var sb = new StringBuilder($"`{cmd}`:\n");
        var n = 0;
        if (vulnerable)
        {
            if (doc.RootElement.TryGetProperty("vulnerabilities", out var vulns))
                foreach (var v in vulns.EnumerateObject())
                {
                    var via = v.Value.TryGetProperty("via", out var vi) && vi.ValueKind == JsonValueKind.Array
                        ? string.Join("; ", vi.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => Str(x, "title")).Where(t => t.Length > 0).Take(2)) : "";
                    sb.Append($"  [{Str(v.Value, "severity")}] {v.Name} {Str(v.Value, "range")} {via}{(v.Value.TryGetProperty("isDirect", out var d) && d.ValueKind == JsonValueKind.True ? " (direct)" : "")}\n");
                    if (++n >= max) break;
                }
            if (n == 0) sb.Append("  no known vulnerabilities\n");
        }
        else
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                sb.Append($"  {p.Name} {Str(p.Value, "current")} → wanted {Str(p.Value, "wanted")}, latest {Str(p.Value, "latest")}\n");
                if (++n >= max) break;
            }
            if (n == 0) sb.Append("  all packages are up to date\n");
        }
        return sb.ToString();
    }

    private static string ParsePip(string output, ProjectMapResult map, int max)
    {
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end <= start) return $"`pip list --outdated`: could not parse: {Short(output.Trim(), 200)}\n";
        try
        {
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            var declared = map.Projects.Where(p => p.Kind == "python").SelectMany(p => p.Packages.Select(x => x.Name.ToLowerInvariant().Replace('_', '-'))).ToHashSet();
            var sb = new StringBuilder("`pip list --outdated` (declared packages first; reflects the active Python environment):\n");
            foreach (var p in doc.RootElement.EnumerateArray().OrderByDescending(p => declared.Contains(Str(p, "name").ToLowerInvariant())).Take(max))
                sb.Append($"  {Str(p, "name")} {Str(p, "version")} → {Str(p, "latest_version")}{(declared.Contains(Str(p, "name").ToLowerInvariant()) ? "" : " (not declared)")}\n");
            return sb.ToString();
        }
        catch (JsonException)
        {
            return "`pip list --outdated`: could not parse the output\n";
        }
    }

    private static string Licenses(ToolContext ctx, ProjectMapResult map, int max)
    {
        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } np ? np
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var rows = new List<(string Package, string Version, string License)>();
        foreach (var (name, version, _) in map.Projects.Where(p => p.Kind == "dotnet").SelectMany(p => p.Packages).DistinctBy(p => p.Name.ToLowerInvariant()))
        {
            if (!IsSafePackageName(name) || version is not null && !IsSafePackageName(version)) continue;
            var dir = Path.Combine(nugetRoot, name.ToLowerInvariant());
            var ver = version ?? (Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OrderByDescending(v => v).FirstOrDefault() : null);
            var nuspec = ver is null ? null : Path.Combine(dir, ver.ToLowerInvariant(), name.ToLowerInvariant() + ".nuspec");
            rows.Add((name, ver ?? "?", nuspec is not null && File.Exists(nuspec) ? NuspecLicense(nuspec) : "unknown (package not restored)"));
        }
        var nodeModules = Path.Combine(ctx.Roots[0], "node_modules");
        foreach (var (name, version, dev) in map.Projects.Where(p => p.Kind == "node" && !p.Manifest.Contains('/')).SelectMany(p => p.Packages).DistinctBy(p => p.Name))
        {
            if (!IsSafePackageName(name)) continue;
            var pj = Path.Combine(nodeModules, name.Replace('/', Path.DirectorySeparatorChar), "package.json");
            var lic = "unknown (not installed)";
            if (File.Exists(pj))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                    lic = doc.RootElement.TryGetProperty("license", out var l) ? l.ValueKind == JsonValueKind.String ? l.GetString()! : Str(l, "type") : "none declared";
                }
                catch (JsonException) { lic = "unreadable"; }
            }
            rows.Add((name + (dev ? " (dev)" : ""), version ?? "?", lic));
        }
        if (rows.Count == 0) return "No NuGet/npm packages to check (other ecosystems are not supported offline).";
        var copyleft = rows.Where(r => Copyleft().IsMatch(r.License)).ToList();
        var sb = new StringBuilder($"licenses of {rows.Count} direct packages (from local caches, no network):\n");
        foreach (var g in rows.GroupBy(r => r.License).OrderByDescending(g => g.Count()))
            sb.Append($"  {g.Key}: {string.Join(", ", g.Take(max).Select(r => $"{r.Package} {r.Version}"))}{(g.Count() > max ? ", …" : "")}\n");
        if (copyleft.Count > 0) sb.Append($"copyleft/restrictive (review for distribution): {string.Join(", ", copyleft.Select(c => $"{c.Package} ({c.License})"))}\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Имя/версия пакета из манифеста используются в пути кэша — без «..», корней и двоеточий.</summary>
    private static bool IsSafePackageName(string name) =>
        name.Length is > 0 and < 200 && !name.Contains("..", StringComparison.Ordinal) && !name.Contains(':') && !name.StartsWith('/') && !name.Contains('\\')
        && name.Count(c => c == '/') <= 1;

    [GeneratedRegex(@"\b(A?GPL|LGPL|MPL|EPL|CDDL|SSPL|BUSL|CC-BY-NC|OSL|EUPL)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Copyleft();

    private static string NuspecLicense(string nuspec)
    {
        try
        {
            var doc = XDocument.Load(nuspec);
            var lic = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "license");
            if (lic is not null) return (string?)lic.Attribute("type") == "file" ? "file: " + lic.Value : lic.Value;
            var url = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "licenseUrl")?.Value;
            return url is null ? "none declared" : "url: " + url;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    /// <summary>Пакеты, которые, судя по коду, не импортируются (эвристика по именам; инструменты сборки/тестов/анализаторы пропускаются).</summary>
    private static string Unused(ProjectMapResult map, CodeIndexResult index, int max)
    {
        var sb = new StringBuilder();
        foreach (var p in map.Projects.Where(p => p.Kind is "dotnet" or "node"))
        {
            var files = index.Files.Where(f => ProjectMap.OwnerOf(map, f.Display) == p).ToList();
            if (files.Count == 0) continue;
            var text = new StringBuilder();
            foreach (var f in files)
                foreach (var l in f.Lines)
                    if (l.Contains("using ", StringComparison.Ordinal) || l.Contains("import", StringComparison.Ordinal) || l.Contains("require(", StringComparison.Ordinal)
                        || l.Contains("global::", StringComparison.Ordinal))
                        text.Append(l).Append('\n');
            var usings = text.ToString();
            var unused = new List<string>();
            foreach (var (name, _, dev) in p.Packages)
            {
                if (IsTooling(name) || p.Kind == "node" && (dev || name.StartsWith("@types/", StringComparison.Ordinal))) continue;
                var probe = p.Kind == "node" ? name : name.Split('.').Take(2).Aggregate((a, b) => a + "." + b);
                if (!usings.Contains(probe, StringComparison.OrdinalIgnoreCase)) unused.Add(name);
            }
            if (unused.Count > 0) sb.Append($"{p.Manifest}: {string.Join(", ", unused.Take(max))}\n");
        }
        return sb.Length == 0
            ? "No obviously unused packages (heuristic: import/using names)."
            : "possibly unused packages (no import/using found; DI registrations, attributes, source generators and implicit usings may still use them):\n" + sb;
    }

    private static bool IsTooling(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith("microsoft.net.test", StringComparison.Ordinal) || n.Contains("analyzers") || n.Contains("sourcelink") || n.StartsWith("coverlet", StringComparison.Ordinal)
               || n.Contains("runner") || n.Contains("testadapter") || n.StartsWith("microsoft.build", StringComparison.Ordinal) || n.Contains("tools")
               || n is "typescript" or "eslint" or "prettier" or "vite" or "webpack" or "rollup" or "nodemon" or "ts-node" or "tsx" or "husky" or "lint-staged"
               || n.StartsWith("eslint-", StringComparison.Ordinal) || n.StartsWith("@eslint/", StringComparison.Ordinal) || n.StartsWith("@vitejs/", StringComparison.Ordinal);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
