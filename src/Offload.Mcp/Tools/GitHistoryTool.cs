using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_git_history: история без сырого вывода git в контексте IDE — file (коммиты файла), symbol (коммиты, менявшие тело
/// символа, git log -L), blame (сводка авторства диапазона по коммитам), related (коммиты по словам в сообщении и в коде),
/// changelog (коммиты диапазона по типам; с моделью — заметки к релизу). question + use_model: локальная модель отвечает
/// «почему код такой / какой коммит мог внести ошибку» по истории изменений.
/// </summary>
internal static partial class GitHistoryTool
{
    private const string Format = "--format=%h%x09%ad%x09%an%x09%s";

    public static async Task<string> RunAsync(ToolContext ctx, string? action, string? path, string? symbol, string? lines, string? query,
        string? range, string? question, bool useModel, int maxCommits, string? language)
    {
        var act = (action ?? "file").Trim().ToLowerInvariant();
        maxCommits = Math.Clamp(maxCommits <= 0 ? 15 : maxCommits, 1, 200);
        var root = ctx.Roots[0];
        if (Git.FindWorkTreeRoot(root) is null) throw new ToolException("The project is not a git repository.");

        switch (act)
        {
            case "file":
            {
                var rel = RelPath(ctx, ToolHelpers.RequireText(path, "path", 1024));
                var log = await GitOk(ctx, ["log", "--follow", "-n", maxCommits.ToString(System.Globalization.CultureInfo.InvariantCulture), "--date=short", Format, "--shortstat", "--", rel]).ConfigureAwait(false);
                var text = $"history of {rel}:\n" + CompactLog(log);
                return await MaybeAskAsync(ctx, text, question, useModel, rel, null).ConfigureAwait(false);
            }
            case "symbol":
            {
                var (rel, from, to, name) = await SymbolRangeAsync(ctx, path, symbol).ConfigureAwait(false);
                var log = await GitOk(ctx, ["log", "-L", $"{from},{to}:{rel}", "-n", maxCommits.ToString(System.Globalization.CultureInfo.InvariantCulture), "--date=short", Format, "-s"]).ConfigureAwait(false);
                var text = $"history of {name} ({rel}:{from}-{to}):\n" + CompactLog(log);
                return await MaybeAskAsync(ctx, text, question, useModel, rel, (from, to)).ConfigureAwait(false);
            }
            case "blame":
            {
                string rel;
                int from, to;
                if (!string.IsNullOrWhiteSpace(symbol)) (rel, from, to, _) = await SymbolRangeAsync(ctx, path, symbol).ConfigureAwait(false);
                else
                {
                    rel = RelPath(ctx, ToolHelpers.RequireText(path, "path", 1024));
                    (from, to) = ParseLines(lines) ?? (1, 400);
                }
                var blame = await GitOk(ctx, ["blame", "--line-porcelain", "-w", "-L", $"{from},{to}", "--", rel]).ConfigureAwait(false);
                return BlameSummary(rel, from, to, blame);
            }
            case "related":
            {
                var q = ToolHelpers.RequireText(query, "query", 100);
                if (q.StartsWith('-') || q.Contains('\n')) throw new ToolException("query must be plain words.");
                var byMessage = await GitOk(ctx, ["log", "-i", "--grep=" + q, "-n", maxCommits.ToString(System.Globalization.CultureInfo.InvariantCulture), "--date=short", Format]).ConfigureAwait(false);
                // Файлы-секреты исключены: иначе -S стал бы оракулом для подбора их содержимого.
                var byCode = await Git.RunAsync(root, ["log", "-S" + q, "-n", maxCommits.ToString(System.Globalization.CultureInfo.InvariantCulture), "--date=short", Format, "--name-only",
                        "--", ".", .. PathGuard.SecretPathspecExcludes(ctx.Cfg.Mcp.SecretFilePatterns)], ctx.Ct,
                    timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.Append($"commits mentioning \"{q}\" in the message:\n").Append(byMessage.Trim().Length == 0 ? "  none\n" : Indent(byMessage));
                sb.Append($"commits that added/removed \"{q}\" in code:\n").Append(!byCode.Success || byCode.StdOut.Trim().Length == 0 ? "  none\n" : Indent(CompactNameOnly(byCode.StdOut)));
                return await MaybeAskAsync(ctx, sb.ToString(), question, useModel, null, null).ConfigureAwait(false);
            }
            case "changelog":
            {
                var r = await DefaultRangeAsync(ctx, range).ConfigureAwait(false);
                var log = await GitOk(ctx, ["log", r, "--no-merges", "-n", Math.Max(maxCommits, 200).ToString(System.Globalization.CultureInfo.InvariantCulture), "--format=%h%x09%s"]).ConfigureAwait(false);
                var grouped = GroupConventional(log);
                if (!useModel) return $"changes in {r}:\n{grouped}";
                var model = await ctx.GetModelAsync().ConfigureAwait(false);
                var lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
                var system = model.SystemPrompt("Write concise user-facing release notes from the commit list: sections New, Improved, Fixed (omit empty ones), " +
                                                $"one bullet per user-visible change, merge duplicates, skip internal chores/refactors/tests/CI. Language: {lang}. Markdown, no preamble.");
                await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
                var reply = await model.ChatAsync(system, "COMMITS:\n" + Truncate(log, model.MaterialBudget(900, system) * 3), 900, "release notes", ctx.Ct).ConfigureAwait(false);
                ctx.Stats.TokensRead = Tokens.Estimate(log);
                return $"release notes for {r} (draft by the local model):\n{reply.Text.Trim()}\n\nraw changes:\n{grouped}";
            }
            default:
                throw new ToolException("action must be file, symbol, blame, related or changelog.");
        }
    }

    private static async Task<string> GitOk(ToolContext ctx, List<string> args)
    {
        var r = await Git.RunAsync(ctx.Roots[0], args, ctx.Ct, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (!r.Success) throw new ToolException("git " + args[0] + " failed: " + GitSandbox.FirstLines(r.StdErr, 3));
        return r.StdOut;
    }

    private static string RelPath(ToolContext ctx, string raw)
    {
        var full = ctx.ResolveRead(raw);
        return Path.GetRelativePath(ctx.Roots[0], full).Replace('\\', '/');
    }

    private static async Task<(string Rel, int From, int To, string Name)> SymbolRangeAsync(ToolContext ctx, string? path, string? symbol)
    {
        var name = ToolHelpers.RequireText(symbol, "symbol", 200);
        IReadOnlyList<string>? scope = string.IsNullOrWhiteSpace(path) ? null : [path];
        var index = await CodeIndex.LoadAsync(ctx, scope, codeOnly: true, ctx.Ct).ConfigureAwait(false);
        var defs = SymbolsTool.Definitions(index.Files, name);
        if (defs.Count == 0) throw new ToolException($"No definition of '{name}' found{(scope is null ? "" : " in " + path)}.");
        var (f, s) = defs[0];
        return (Path.GetRelativePath(ctx.Roots[0], f.FullPath).Replace('\\', '/'), s.Line, Math.Max(s.Line, s.EndLine), s.QualifiedName);
    }

    private static (int, int)? ParseLines(string? lines)
    {
        if (string.IsNullOrWhiteSpace(lines)) return null;
        var m = Regex.Match(lines, @"^\s*(\d+)\s*[-,:]\s*(\d+)\s*$");
        if (!m.Success) throw new ToolException("lines must look like 120-160.");
        var a = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var b = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (Math.Max(1, Math.Min(a, b)), Math.Max(a, b));
    }

    /// <summary>Лог «hash\tdate\tauthor\tsubject» + shortstat → одна строка на коммит.</summary>
    private static string CompactLog(string log)
    {
        var sb = new StringBuilder();
        foreach (var line in log.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0))
        {
            if (line.StartsWith(' ') && line.Contains("changed", StringComparison.Ordinal))
            {
                sb.Length = Math.Max(0, sb.Length - 1);
                sb.Append("  (").Append(StatShort().Replace(line.Trim(), "$1")).Append(")\n");
                continue;
            }
            var p = line.Split('\t');
            sb.Append("  ").Append(p.Length >= 4 ? $"{p[0]} {p[1]} {p[2]}: {p[3]}" : line).Append('\n');
        }
        return sb.Length == 0 ? "  (no commits)\n" : sb.ToString();
    }

    [GeneratedRegex(@"\d+ files? changed,?\s*(.*)", RegexOptions.CultureInvariant)]
    private static partial Regex StatShort();

    private static string CompactNameOnly(string log)
    {
        var sb = new StringBuilder();
        foreach (var block in log.Split('\n').Aggregate(new List<List<string>>(), (acc, l) =>
                 {
                     if (l.Contains('\t')) acc.Add([l]);
                     else if (l.Trim().Length > 0 && acc.Count > 0) acc[^1].Add(l.Trim());
                     return acc;
                 }))
        {
            var p = block[0].Split('\t');
            sb.Append(p.Length >= 4 ? $"{p[0]} {p[1]} {p[2]}: {p[3]}" : block[0]);
            if (block.Count > 1) sb.Append("  [").Append(string.Join(", ", block.Skip(1).Take(5))).Append(block.Count > 6 ? ", …" : "").Append(']');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BlameSummary(string rel, int from, int to, string porcelain)
    {
        var entries = new List<(int Line, string Sha, string Author, long Time, string Summary)>();
        string? sha = null, author = null, summary = null;
        long time = 0;
        var line = 0;
        var meta = new Dictionary<string, (string Author, long Time, string Summary)>(StringComparer.Ordinal);
        foreach (var l in porcelain.Split('\n'))
        {
            if (Regex.IsMatch(l, @"^[0-9a-f]{40} \d+ \d+"))
            {
                var parts = l.Split(' ');
                sha = parts[0];
                line = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                author = null;
                summary = null;
                continue;
            }
            if (l.StartsWith("author ", StringComparison.Ordinal)) author = l[7..];
            else if (l.StartsWith("author-time ", StringComparison.Ordinal)) _ = long.TryParse(l[12..], out time);
            else if (l.StartsWith("summary ", StringComparison.Ordinal)) summary = l[8..];
            else if (l.StartsWith('\t') && sha is not null)
            {
                if (author is not null) meta[sha] = (author, time, summary ?? "");
                var m = meta.GetValueOrDefault(sha);
                entries.Add((line, sha, m.Author ?? "?", m.Time, m.Summary ?? ""));
            }
        }
        if (entries.Count == 0) return $"No blame data for {rel}:{from}-{to}.";
        var sb = new StringBuilder($"blame of {rel}:{from}-{to} ({entries.Count} lines) grouped by commit:\n");
        foreach (var g in entries.GroupBy(e => e.Sha).OrderByDescending(g => g.First().Time))
        {
            var ranges = Ranges(g.Select(e => e.Line).OrderBy(x => x).ToList());
            var date = DateTimeOffset.FromUnixTimeSeconds(g.First().Time).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var short7 = g.Key.StartsWith("0000000", StringComparison.Ordinal) ? "uncommitted" : g.Key[..8];
            sb.Append($"  {short7} {date} {g.First().Author}: {Short(g.First().Summary, 80)} — lines {ranges} ({g.Count()})\n");
        }
        return sb.ToString();
    }

    private static string Ranges(List<int> lines)
    {
        var parts = new List<string>();
        for (var i = 0; i < lines.Count;)
        {
            var j = i;
            while (j + 1 < lines.Count && lines[j + 1] == lines[j] + 1) j++;
            parts.Add(i == j ? lines[i].ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{lines[i]}-{lines[j]}");
            i = j + 1;
        }
        return parts.Count > 6 ? string.Join(",", parts.Take(6)) + ",…" : string.Join(",", parts);
    }

    private static async Task<string> DefaultRangeAsync(ToolContext ctx, string? range)
    {
        if (!string.IsNullOrWhiteSpace(range))
        {
            if (!Git.IsSafeRevision(range.Trim())) throw new ToolException($"Invalid git range '{range}'.");
            return range.Trim();
        }
        var tag = await Git.RunAsync(ctx.Roots[0], ["describe", "--tags", "--abbrev=0"], ctx.Ct).ConfigureAwait(false);
        return tag.Success && tag.StdOut.Trim().Length > 0 && Git.IsSafeRevision(tag.StdOut.Trim()) ? tag.StdOut.Trim() + "..HEAD" : "HEAD~30..HEAD";
    }

    [GeneratedRegex(@"^(?<type>feat|fix|perf|refactor|docs|test|build|ci|chore|style|revert)(?:\((?<scope>[^)]*)\))?!?:\s*(?<msg>.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Conventional();

    private static string GroupConventional(string log)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in log.Split('\n').Where(l => l.Contains('\t')))
        {
            var p = l.Split('\t', 2);
            var m = Conventional().Match(p[1].Trim());
            var type = m.Success ? m.Groups["type"].Value.ToLowerInvariant() : "other";
            var msg = m.Success ? (m.Groups["scope"].Success ? $"{m.Groups["scope"].Value}: " : "") + m.Groups["msg"].Value : p[1].Trim();
            if (!groups.TryGetValue(type, out var list)) groups[type] = list = [];
            list.Add($"{msg} ({p[0]})");
        }
        if (groups.Count == 0) return "  (no commits)";
        var order = new[] { "feat", "fix", "perf", "refactor", "docs", "test", "build", "ci", "chore", "style", "revert", "other" };
        var sb = new StringBuilder();
        foreach (var t in order.Where(groups.ContainsKey))
        {
            sb.Append(t).Append(":\n");
            foreach (var m in groups[t].Take(40)) sb.Append("  - ").Append(m).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Вопрос к локальной модели по истории: для файла/символа добавляются диффы изменений (урезанные под контекст).</summary>
    private static async Task<string> MaybeAskAsync(ToolContext ctx, string summary, string? question, bool useModel, string? rel, (int From, int To)? range)
    {
        if (string.IsNullOrWhiteSpace(question) || !useModel) return summary.TrimEnd();
        var args = rel is null ? null
            : range is { } r ? new List<string> { "log", "-L", $"{r.From},{r.To}:{rel}", "-n", "8", "--date=short", Format }
            : ["log", "-n", "8", "-p", "--date=short", Format, "--", rel, .. PathGuard.SecretPathspecExcludes(ctx.Cfg.Mcp.SecretFilePatterns)];
        var detail = args is null ? "" : (await Git.RunAsync(ctx.Roots[0], args, ctx.Ct, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false)).StdOut;
        var model = await ctx.GetModelAsync().ConfigureAwait(false);
        var system = model.SystemPrompt("You answer a question about the history of code using git log output (commits with their diffs). " +
                                        "Cite commit hashes. Be brief and concrete; say when the history does not answer the question.");
        var budget = model.MaterialBudget(700, system, question);
        var material = Truncate(summary + "\n" + detail, budget * 3);
        await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, "GIT HISTORY:\n" + material + "\nQUESTION:\n" + question.Trim(), 700, "history", ctx.Ct).ConfigureAwait(false);
        ctx.Stats.TokensRead = Tokens.Estimate(detail);
        return summary.TrimEnd() + "\n\nanswer (local model):\n" + reply.Text.Trim();
    }

    private static string Indent(string s) => string.Join("\n", s.Split('\n').Where(l => l.Trim().Length > 0).Select(l =>
    {
        var p = l.Split('\t');
        return "  " + (p.Length >= 4 ? $"{p[0]} {p[1]} {p[2]}: {p[3]}" : l.Trim());
    })) + "\n";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max)] + "\n…[truncated]";

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
