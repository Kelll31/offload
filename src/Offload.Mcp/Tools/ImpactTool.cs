using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_impact: что затронет изменение. Источник — git diff (target) или символ (symbol): изменённые символы → вызывающие
/// (текстовые ссылки внутри функций), связанные тесты и проекты. run_tests=true запускает только связанные тесты разрешёнными
/// командами (dotnet test с фильтром класса, jest/vitest/pytest по файлам, go test по пакету).
/// </summary>
internal static partial class ImpactTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? target, string? symbol, bool runTests, int maxResults, int timeoutSec)
    {
        maxResults = Math.Clamp(maxResults <= 0 ? 40 : maxResults, 5, 200);
        ctx.Progress.Report("Indexing source files…");
        var (map, index) = await ProjectMap.BuildAsync(ctx, ctx.Ct).ConfigureAwait(false);
        var files = index.Files;

        // 1. Изменённые символы.
        var changed = new List<(SourceFile File, CodeSymbol? Symbol, string Why)>();
        var changedFiles = new List<string>();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            foreach (var (f, s) in SymbolsTool.Definitions(files, symbol)) changed.Add((f, s, "requested"));
            if (changed.Count == 0) throw new ToolException($"No definition of '{symbol}' found.");
        }
        else
        {
            var ranges = await ChangedRangesAsync(ctx, string.IsNullOrWhiteSpace(target) ? "all" : target.Trim()).ConfigureAwait(false);
            if (ranges.Count == 0) return "No changes found for target=" + (target ?? "all") + ".";
            foreach (var (display, lines) in ranges)
            {
                changedFiles.Add(display);
                var f = files.FirstOrDefault(x => x.Display.Equals(display, StringComparison.OrdinalIgnoreCase));
                if (f is null) continue;
                var syms = new List<CodeSymbol>();
                var inner = f.Symbols.Where(s => s.Kind is not ("namespace" or "module")).ToList();
                foreach (var l in lines)
                    if (Symbols.Enclosing(inner, Math.Max(1, l)) is { } s && !syms.Contains(s)) syms.Add(s);
                if (syms.Count == 0) changed.Add((f, null, "file-level change"));
                foreach (var s in syms) changed.Add((f, s, "changed lines"));
            }
        }

        var sb = new StringBuilder();
        if (changedFiles.Count > 0) sb.Append($"changed files ({changedFiles.Count}): {string.Join(", ", changedFiles.Take(20))}{(changedFiles.Count > 20 ? ", …" : "")}\n");
        var symbols = changed.Where(c => c.Symbol is not null).Select(c => (c.File, Symbol: c.Symbol!)).DistinctBy(c => (c.File.Display, c.Symbol.Line)).ToList();
        // Для типов, изменённых целиком, интереснее члены; но не больше 25 символов.
        symbols = symbols.OrderBy(s => s.Symbol.IsType ? 1 : 0).Take(25).ToList();
        sb.Append($"changed symbols ({symbols.Count}):\n");
        foreach (var (f, s) in symbols) sb.Append($"  {s.QualifiedName} [{s.Kind}] {f.Display}:{s.Line}\n");

        // 2. Вызывающие и тесты.
        var impactedFiles = new HashSet<string>(changedFiles, StringComparer.OrdinalIgnoreCase);
        var tests = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var callersShown = 0;
        var callerLines = new StringBuilder();
        foreach (var (f, s) in symbols)
        {
            var refs = SymbolsTool.FindReferences(files, s.Name, 300);
            foreach (var r in refs)
            {
                impactedFiles.Add(r.File.Display);
                if (r.File.IsTest)
                {
                    if (!tests.TryGetValue(r.File.Display, out var set)) tests[r.File.Display] = set = new HashSet<string>(StringComparer.Ordinal);
                    if (r.Enclosing is { } e) set.Add(e.Container is null ? e.Name : e.Container.Split('.')[^1]);
                }
            }
            var callers = refs.Where(r => !r.File.IsTest && r.Enclosing is { } e && SymbolsTool.IsCallable(e) && e.Name != s.Name && SymbolsTool.MayReferTo(r, f, s))
                .GroupBy(r => (r.File.Display, r.Enclosing!.QualifiedName)).ToList();
            if (callers.Count == 0 || callersShown >= maxResults) continue;
            callerLines.Append($"  {s.QualifiedName} ← ");
            callerLines.Append(string.Join(", ", callers.Take(8).Select(g => $"{g.Key.QualifiedName} ({g.Key.Display}:{g.First().Line})")));
            if (callers.Count > 8) callerLines.Append($", +{callers.Count - 8}");
            callerLines.Append('\n');
            callersShown += Math.Min(8, callers.Count);
        }
        // Тесты по имени файла: FooTests для Foo.cs.
        foreach (var name in changed.Select(c => Path.GetFileNameWithoutExtension(c.File.Display)).Distinct())
            foreach (var t in files.Where(t => t.IsTest && (Path.GetFileNameWithoutExtension(t.Display).StartsWith(name, StringComparison.OrdinalIgnoreCase))))
                if (!tests.ContainsKey(t.Display)) tests[t.Display] = [.. t.Symbols.Where(x => x.IsType).Select(x => x.Name).Take(3)];
        // Изменённые сами тестовые файлы — тоже связанные тесты.
        foreach (var c in changed.Where(c => c.File.IsTest))
            if (!tests.ContainsKey(c.File.Display)) tests[c.File.Display] = [.. c.File.Symbols.Where(x => x.IsType).Select(x => x.Name).Take(3)];

        if (callerLines.Length > 0) sb.Append("callers:\n").Append(callerLines);
        else sb.Append("callers: none found outside tests\n");
        if (tests.Count > 0)
        {
            sb.Append($"related tests ({tests.Count} files):\n");
            foreach (var (t, classes) in tests.Take(maxResults)) sb.Append($"  {t}{(classes.Count > 0 ? " [" + string.Join(", ", classes.Take(4)) + "]" : "")}\n");
        }
        else
        {
            sb.Append("related tests: none found (consider adding tests)\n");
        }
        var projects = impactedFiles.Select(f => ProjectMap.OwnerOf(map, f)).Where(p => p is not null).Select(p => p!.Manifest).Distinct().ToList();
        if (projects.Count > 0) sb.Append($"projects affected: {string.Join(", ", projects.Take(15))}\n");

        if (!runTests) return sb.Append(tests.Count > 0 ? "run only these tests: call again with run_tests=true" : "").ToString().TrimEnd();

        // 3. Запуск только связанных тестов.
        var commands = TestCommands(ctx, map, tests);
        if (commands.Count == 0) return sb.Append("no runnable test command could be built for these tests (check the allowlist)").ToString().TrimEnd();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec <= 0 ? 900 : timeoutSec, 30, 3600));
        foreach (var cmd in commands.Take(4))
        {
            var run = await VerifyTool.RunLoggedAsync(ctx, cmd, timeout).ConfigureAwait(false);
            var scan = VerifyTool.Scan(run.LogPath, ctx.Ct);
            sb.Append($"`{cmd}` → {(run.Result.Passed ? "PASSED" : run.Result.TimedOut ? "TIMED OUT" : $"FAILED (exit {run.Result.ExitCode})")}, {run.Result.Duration.TotalSeconds:0} s · log: {run.Display}\n");
            foreach (var l in scan.Summary.TakeLast(3)) sb.Append("    ").Append(l).Append('\n');
            if (!run.Result.Passed)
                foreach (var e in scan.Errors.Take(8)) sb.Append("    ").Append(e).Append('\n');
        }
        if (commands.Count > 4) sb.Append($"({commands.Count - 4} more test commands not run)\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Изменённые строки (новой версии) по файлам: git diff -U0 для target (all/staged/unstaged/ref) + новые файлы целиком.</summary>
    internal static async Task<Dictionary<string, List<int>>> ChangedRangesAsync(ToolContext ctx, string target)
    {
        var root = ctx.Roots[0];
        var repo = Git.FindWorkTreeRoot(root) ?? throw new ToolException("The project is not a git repository; pass symbol instead of target.");
        List<string> args = ["diff", "-U0", "--no-color", "--no-ext-diff", "--no-renames", "--relative"];
        switch (target)
        {
            case "all": args.Add("HEAD"); break;
            case "staged": args.Add("--cached"); break;
            case "unstaged": break;
            default:
                if (!Git.IsSafeRevision(target)) throw new ToolException($"Invalid git ref/range '{target}'.");
                args.Add(target);
                break;
        }
        var res = await Git.RunAsync(root, args, ctx.Ct).ConfigureAwait(false);
        if (!res.Success && target == "all")
            res = await Git.RunAsync(root, ["diff", "-U0", "--no-color", "--no-ext-diff", "--no-renames", "--relative"], ctx.Ct).ConfigureAwait(false);
        if (!res.Success) throw new ToolException("git diff failed: " + GitSandbox.FirstLines(res.StdErr, 3));
        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        string? file = null;
        foreach (var line in res.StdOut.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var p = line[4..].Trim();
                file = p == "/dev/null" ? null : Git.Unquote(p).StartsWith("b/", StringComparison.Ordinal) ? Git.Unquote(p)[2..] : Git.Unquote(p);
                continue;
            }
            if (file is null || !line.StartsWith("@@", StringComparison.Ordinal)) continue;
            var m = Hunk().Match(line);
            if (!m.Success) continue;
            var start = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var count = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
            if (!result.TryGetValue(file, out var list)) result[file] = list = [];
            if (count == 0) list.Add(start);
            for (var i = 0; i < Math.Min(count, 400); i++) list.Add(start + i);
        }
        if (target is "all" or "unstaged")
        {
            var untracked = await Git.RunAsync(root, ["ls-files", "--others", "--exclude-standard"], ctx.Ct).ConfigureAwait(false);
            foreach (var u in untracked.StdOut.Split('\n').Select(l => Git.Unquote(l.Trim())).Where(l => l.Length > 0).Take(200))
                if (!result.ContainsKey(u)) result[u] = [.. Enumerable.Range(1, 2000)];
        }
        // Секреты и файлы вне проверок чтения не показываем.
        return result.Where(kv => PathGuard.IsSecretName(Path.GetFileName(kv.Key), ctx.Cfg.Mcp.SecretFilePatterns) is false)
            .ToDictionary(kv => kv.Key.Replace('\\', '/'), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex Hunk();

    /// <summary>Команды запуска только связанных тестов (прошедшие белый список).</summary>
    internal static List<string> TestCommands(ToolContext ctx, ProjectMapResult map, Dictionary<string, HashSet<string>> tests)
    {
        var commands = new List<string>();
        var mtp = ProjectMap.IsMtp(ctx.Roots[0]);
        foreach (var g in tests.GroupBy(t => ProjectMap.OwnerOf(map, t.Key)))
        {
            var p = g.Key;
            var classes = g.SelectMany(t => t.Value.Count > 0 ? t.Value : [Path.GetFileNameWithoutExtension(t.Key)]).Where(c => Symbols.Identifier().IsMatch(c)).Distinct().Take(6).ToList();
            var fileArgs = string.Join(' ', g.Select(t => t.Key).Where(k => !k.Contains(' ')).Take(10));
            switch (p?.Kind)
            {
                case "dotnet":
                    if (mtp) commands.Add($"dotnet test --project {p.Manifest} " + string.Join(' ', classes.Select(c => $"--filter-class \"*{c}\"")));
                    else foreach (var c in classes.Take(3)) commands.Add($"dotnet test {p.Manifest} --filter FullyQualifiedName~{c}");
                    break;
                case "node":
                    commands.Add(p.Packages.Any(x => x.Name == "vitest") ? $"npx vitest run {fileArgs}" : $"npx jest {fileArgs}");
                    break;
                case "python":
                    commands.Add($"pytest -q {fileArgs}");
                    break;
                case "go":
                    foreach (var dir in g.Select(t => Path.GetDirectoryName(t.Key)?.Replace('\\', '/') ?? ".").Distinct().Take(4))
                        commands.Add($"go test ./{(dir.Length == 0 ? "" : dir + "/")}...".Replace(".//", "./"));
                    break;
                case "rust":
                    commands.Add("cargo test");
                    break;
                default:
                    if (fileArgs.EndsWith(".py", StringComparison.Ordinal) || fileArgs.Contains(".py ", StringComparison.Ordinal)) commands.Add($"pytest -q {fileArgs}");
                    break;
            }
        }
        var valid = new List<string>();
        foreach (var c in commands.Distinct())
        {
            try { valid.Add(VerifyCommand.Validate(c, ctx.Cfg.Mcp.VerifyCommandAllowlist)); }
            catch (ToolException) { }
        }
        return valid;
    }
}
