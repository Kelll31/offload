using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_code_scan: детерминированные проверки по всему проекту или папке — todo, secrets (значения маскируются), unsafe_api,
/// async, dead_code, duplicates, complexity, hotspots (сложность × частота изменений в git), generated. Без модели.
/// </summary>
internal static partial class CodeScanTool
{
    public static readonly string[] Checks = ["todo", "secrets", "unsafe_api", "async", "dead_code", "duplicates", "complexity", "hotspots", "generated"];

    public static async Task<string> RunAsync(ToolContext ctx, string? check, string[]? paths, int maxResults, bool includeTests)
    {
        var c = (check ?? "").Trim().ToLowerInvariant();
        if (!Checks.Contains(c)) throw new ToolException("check must be one of: " + string.Join(", ", Checks) + ".");
        maxResults = Math.Clamp(maxResults <= 0 ? 50 : maxResults, 5, 500);
        ctx.Progress.Report("Indexing files…");
        var index = await CodeIndex.LoadAsync(ctx, paths, codeOnly: c is not ("secrets" or "todo"), ctx.Ct).ConfigureAwait(false);
        var files = index.Files.Where(f => includeTests || !f.IsTest || c is "secrets" or "duplicates" or "generated").ToList();
        var body = c switch
        {
            "todo" => Todo(files, maxResults),
            "secrets" => LineRules(files, maxResults, (f, l) => CodeRules.Secrets(l), redact: true, "possible secrets"),
            "unsafe_api" => LineRules(files, maxResults, (f, l) => CodeRules.Unsafe(f.Lang, l), redact: false, "risky API usages"),
            "async" => LineRules(files, maxResults, (f, l) => CodeRules.Async(f.Lang, l), redact: false, "async/concurrency issues"),
            "dead_code" => DeadCode(index.Files, includeTests, maxResults),
            "duplicates" => Duplicates(files, maxResults),
            "complexity" => Complexity(files, maxResults),
            "hotspots" => await HotspotsAsync(ctx, files, maxResults).ConfigureAwait(false),
            "generated" => Generated(files, maxResults),
            _ => "",
        };
        return body.TrimEnd() + "\n\n" + index.CoverageNote();
    }

    private static string Todo(List<SourceFile> files, int max)
    {
        var hits = new List<(string Tag, string Where, string Text)>();
        foreach (var f in files)
            for (var i = 0; i < f.Lines.Length; i++)
            {
                var line = f.Lines[i];
                if (line.Length > 1000) continue;
                var m = CodeRules.Todo().Match(line);
                if (!m.Success) continue;
                // Только в комментариях (или в Markdown-файлах).
                var commentAt = Math.Max(line.IndexOf("//", StringComparison.Ordinal), Math.Max(line.IndexOf('#'), line.IndexOf("/*", StringComparison.Ordinal)));
                if (f.Lang != CodeLang.Unknown && !(commentAt >= 0 && commentAt < m.Index) && !line.TrimStart().StartsWith('*') && !line.TrimStart().StartsWith("--", StringComparison.Ordinal)) continue;
                hits.Add((m.Groups[1].Value, $"{f.Display}:{i + 1}", line[m.Index..].Trim()));
            }
        if (hits.Count == 0) return "No TODO/FIXME/HACK markers found.";
        var sb = new StringBuilder($"{hits.Count} markers: {string.Join(", ", hits.GroupBy(h => h.Tag).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}×{g.Count()}"))}\n");
        foreach (var g in hits.GroupBy(h => h.Tag).OrderBy(g => g.Key is "FIXME" or "BUG" or "HACK" or "XXX" ? 0 : 1))
        {
            sb.Append(g.Key).Append(":\n");
            foreach (var h in g.Take(Math.Max(5, max / 3))) sb.Append($"  {h.Where}  {Short(h.Text, 140)}\n");
        }
        return sb.ToString();
    }

    private static string LineRules(List<SourceFile> files, int max, Func<SourceFile, string, IEnumerable<RuleHit>> rules, bool redact, string title)
    {
        var hits = new List<(RuleHit Hit, string Where, string Text)>();
        foreach (var f in files)
            for (var i = 0; i < f.Lines.Length; i++)
            {
                var line = f.Lines[i];
                if (line.Length > 2000 || !redact && CodeIndex.IsCommentLine(line)) continue;
                foreach (var h in rules(f, line))
                    hits.Add((f.IsTest ? h with { Message = h.Message + " [test]" } : h, $"{f.Display}:{i + 1}", redact ? CodeRules.RedactLine(line.Trim()) : line.Trim()));
            }
        if (hits.Count == 0) return $"No {title} found.";
        var order = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2, ["info"] = 3 };
        var sb = new StringBuilder($"{hits.Count} {title}: {string.Join(", ", hits.GroupBy(h => h.Hit.Rule).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}×{g.Count()}"))}\n");
        foreach (var h in hits.OrderBy(h => h.Hit.Message.EndsWith("[test]", StringComparison.Ordinal)).ThenBy(h => order.GetValueOrDefault(h.Hit.Severity, 4)).Take(max))
            sb.Append($"[{h.Hit.Severity}] {h.Where} {h.Hit.Rule}: {h.Hit.Message}\n    {Short(h.Text, 160)}\n");
        if (hits.Count > max) sb.Append($"… {hits.Count - max} more (raise max_results or narrow paths)\n");
        return sb.ToString();
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    private static string DeadCode(List<SourceFile> all, bool includeTests, int max)
    {
        // Сколько раз каждое имя встречается во всём коде (включая тесты — использование в тестах тоже использование).
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in all)
            foreach (var line in f.Lines)
            {
                if (line.Length > 2000) continue;
                foreach (Match m in Word().Matches(line)) counts[m.Value] = counts.GetValueOrDefault(m.Value) + 1;
            }
        var declarations = all.SelectMany(f => f.Symbols).GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var candidates = new List<(SourceFile File, CodeSymbol Symbol, bool Public)>();
        foreach (var f in all.Where(f => includeTests || !f.IsTest))
        {
            foreach (var s in f.Symbols)
            {
                if (s.Kind is "namespace" or "ctor" or "impl" or "module" || IsEntryLike(f, s)) continue;
                if (counts.GetValueOrDefault(s.Name) > Math.Max(1, declarations.GetValueOrDefault(s.Name))) continue;
                candidates.Add((f, s, LooksPublic(f, s)));
            }
        }
        if (candidates.Count == 0) return "No unreferenced declarations found.";
        var sb = new StringBuilder($"{candidates.Count} declarations whose name appears nowhere else (textual check: reflection, DI, serialization, XAML and public API consumers are invisible to it):\n");
        foreach (var (f, s, pub) in candidates.OrderBy(c => c.Public).ThenBy(c => c.File.Display, StringComparer.OrdinalIgnoreCase).Take(max))
            sb.Append($"  {f.Display}:{s.Line} {s.Kind} {s.QualifiedName}{(pub ? " (public: may be used externally)" : "")}\n");
        if (candidates.Count > max) sb.Append($"… {candidates.Count - max} more\n");
        return sb.ToString();
    }

    private static bool IsEntryLike(SourceFile f, CodeSymbol s)
    {
        if (s.Name is "Main" or "main" or "Dispose" or "ToString" or "Equals" or "GetHashCode" or "__init__" or "setUp" or "tearDown" or "Configure" or "ConfigureServices") return true;
        if (s.Signature.Contains("override ", StringComparison.Ordinal) || s.Name.StartsWith("On", StringComparison.Ordinal) && s.Name.Length > 2 && char.IsUpper(s.Name[2])) return true;
        if (s.Name.StartsWith("test", StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith("__", StringComparison.Ordinal)) return true;
        // Атрибуты над объявлением ([Fact], [HttpGet], [JsonPropertyName]…) — используются фреймворком.
        return s.Line >= 2 && f.Lines[s.Line - 2].TrimStart().StartsWith('[') || s.Line >= 2 && f.Lines[s.Line - 2].TrimStart().StartsWith('@');
    }

    private static bool LooksPublic(SourceFile f, CodeSymbol s) => f.Lang switch
    {
        CodeLang.CSharp or CodeLang.Java => s.Signature.Contains("public ", StringComparison.Ordinal) || s.Signature.Contains("protected ", StringComparison.Ordinal),
        CodeLang.TypeScript => s.Signature.StartsWith("export", StringComparison.Ordinal),
        CodeLang.Go => char.IsUpper(s.Name[0]),
        CodeLang.Rust => s.Signature.StartsWith("pub", StringComparison.Ordinal),
        CodeLang.Python => !s.Name.StartsWith('_'),
        _ => true,
    };

    private const int DupWindow = 6;

    private static string Duplicates(List<SourceFile> files, int max)
    {
        var windows = new Dictionary<string, List<(SourceFile File, int Line)>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var meaningful = new List<(string Norm, int Line)>();
            for (var i = 0; i < f.Lines.Length; i++)
            {
                var n = Normalize(f.Lines[i]);
                if (n is not null) meaningful.Add((n, i + 1));
            }
            for (var i = 0; i + DupWindow <= meaningful.Count; i++)
            {
                var key = string.Join("\n", meaningful.Skip(i).Take(DupWindow).Select(x => x.Norm));
                if (key.Length < 120) continue;
                if (!windows.TryGetValue(key, out var list)) windows[key] = list = [];
                if (list.Count < 20) list.Add((f, meaningful[i].Line));
            }
        }
        // Схлопываем соседние окна одной пары мест в один диапазон.
        var groups = windows.Values.Where(l => l.Count > 1 && l.Select(x => (x.File.Display, x.Line / 1000)).Distinct().Count() > 1).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = new List<string>();
        foreach (var g in groups.OrderByDescending(g => g.Count))
        {
            var key = string.Join("|", g.Select(x => $"{x.File.Display}:{x.Line / 12}"));
            if (!seen.Add(key)) continue;
            reported.Add($"  {g.Count}× " + string.Join(", ", g.Take(6).Select(x => $"{x.File.Display}:{x.Line}")) + (g.Count > 6 ? ", …" : ""));
            if (reported.Count >= max) break;
        }
        return reported.Count == 0 ? $"No duplicated blocks of {DupWindow}+ lines found." : $"duplicated blocks (≥{DupWindow} meaningful lines, whitespace-insensitive):\n" + string.Join("\n", reported);
    }

    private static string? Normalize(string line)
    {
        var t = line.Trim();
        if (t.Length < 4 || t is "{" or "}" or "};" or ")" or ");" or "]" or "end" or "else" or "try" or "finally") return null;
        if (t.StartsWith("using ", StringComparison.Ordinal) || t.StartsWith("import ", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal)
            || t.StartsWith('#') || t.StartsWith("from ", StringComparison.Ordinal) || t.StartsWith('*')) return null;
        return Regex.Replace(t, @"\s+", " ");
    }

    internal sealed record SymbolMetrics(SourceFile File, CodeSymbol Symbol, int Complexity, int Nesting, int Length);

    internal static List<SymbolMetrics> Measure(IEnumerable<SourceFile> files)
    {
        var list = new List<SymbolMetrics>();
        foreach (var f in files)
        {
            if (f.Symbols.Count == 0) continue;
            var code = Symbols.StripForBraces(f.Lang, f.Lines);
            foreach (var s in f.Symbols.Where(s => SymbolsTool.IsCallable(s) && s.EndLine > s.Line))
            {
                var complexity = 1;
                var depth = 0;
                var maxDepth = 0;
                for (var i = s.Line; i <= s.EndLine && i <= code.Length; i++)
                {
                    var l = code[i - 1];
                    complexity += CodeRules.Branch().Matches(l).Count;
                    foreach (var ch in l)
                    {
                        if (ch == '{') maxDepth = Math.Max(maxDepth, ++depth);
                        else if (ch == '}') depth--;
                    }
                }
                if (f.Lang == CodeLang.Python)
                {
                    var baseIndent = f.Lines[s.Line - 1].Length - f.Lines[s.Line - 1].TrimStart().Length;
                    maxDepth = Enumerable.Range(s.Line, s.EndLine - s.Line + 1).Where(i => i <= f.Lines.Length && f.Lines[i - 1].Trim().Length > 0)
                        .Select(i => (f.Lines[i - 1].Length - f.Lines[i - 1].TrimStart().Length - baseIndent) / 4).DefaultIfEmpty(0).Max();
                }
                list.Add(new SymbolMetrics(f, s, complexity, Math.Max(0, maxDepth - 1), s.EndLine - s.Line + 1));
            }
        }
        return list;
    }

    private static string Complexity(List<SourceFile> files, int max)
    {
        var metrics = Measure(files);
        if (metrics.Count == 0) return "No functions/methods found.";
        var sb = new StringBuilder("most complex functions (cyclomatic ≈ branches+1; nesting = max brace depth inside):\n");
        foreach (var m in metrics.OrderByDescending(m => m.Complexity * 2 + m.Nesting * 3 + m.Length / 20).Take(max))
            sb.Append($"  cc {m.Complexity,3} · nest {m.Nesting} · {m.Length,4} lines  {m.Symbol.QualifiedName}  {m.File.Display}:{m.Symbol.Line}\n");
        var avg = metrics.Average(m => m.Complexity);
        sb.Append($"functions: {metrics.Count}, average cc {avg:0.0}, >15: {metrics.Count(m => m.Complexity > 15)}, >100 lines: {metrics.Count(m => m.Length > 100)}");
        return sb.ToString();
    }

    private static async Task<string> HotspotsAsync(ToolContext ctx, List<SourceFile> files, int max)
    {
        if (Git.FindWorkTreeRoot(ctx.Roots[0]) is null) return "hotspots need git history; the project is not a git repository. Use check=complexity.";
        var res = await Git.RunAsync(ctx.Roots[0], ["log", "--since=18.months", "--no-merges", "--format=", "--name-only", "--relative", "-n", "3000"], ctx.Ct,
            timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var churn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in res.StdOut.Split('\n'))
        {
            var p = Git.Unquote(l.Trim());
            if (p.Length > 0) churn[p] = churn.GetValueOrDefault(p) + 1;
        }
        var metrics = Measure(files).GroupBy(m => m.File).ToDictionary(g => g.Key, g => (Max: g.Max(m => m.Complexity), Sum: g.Sum(m => m.Complexity)));
        var rows = files.Select(f => (File: f, Commits: churn.GetValueOrDefault(f.Display), Cx: metrics.TryGetValue(f, out var v) ? v : (Max: 0, Sum: 0)))
            .Where(r => r.Commits > 0)
            .Select(r => (r.File, r.Commits, Max: r.Cx.Max, Sum: r.Cx.Sum, Score: r.Commits * Math.Max(1, r.Cx.Sum)))
            .OrderByDescending(r => r.Score).Take(max).ToList();
        if (rows.Count == 0) return "No hotspots: no git history for the scanned files in the last 18 months.";
        var sb = new StringBuilder("hotspots = commits (18 months) × total complexity; refactor/test these first:\n");
        foreach (var r in rows) sb.Append($"  {r.Commits,4} commits · cc sum {r.Sum,4} · max {r.Max,3} · {r.File.Lines.Length,5} lines  {r.File.Display}\n");
        return sb.ToString();
    }

    private static string Generated(List<SourceFile> files, int max)
    {
        var gen = files.Where(f =>
            f.Display.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || f.Display.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || f.Display.Contains(".generated.", StringComparison.OrdinalIgnoreCase) || f.Display.EndsWith(".pb.go", StringComparison.Ordinal)
            || f.Display.EndsWith("_pb2.py", StringComparison.Ordinal) || f.Lines.Take(12).Any(l => CodeRules.GeneratedMarker().IsMatch(l))).ToList();
        if (gen.Count == 0) return "No generated files detected.";
        var sb = new StringBuilder($"{gen.Count} generated files (do not edit by hand; change the generator/source instead):\n");
        foreach (var f in gen.Take(max)) sb.Append("  ").Append(f.Display).Append('\n');
        return sb.ToString();
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
