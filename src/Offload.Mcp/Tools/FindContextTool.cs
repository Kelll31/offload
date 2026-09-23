using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_find_context: задача на естественном языке → релевантные файлы, символы и фрагменты кода, уложенные в бюджет токенов.
/// Поиск детерминированный (термины задачи × имена символов × пути × содержимое), локальная модель — для расширения запроса
/// (в т. ч. с русского на идентификаторы кода), переранжирования с объяснением и (mode=plan) плана реализации.
/// </summary>
internal static partial class FindContextTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? task, string[]? paths, int maxFiles, int budgetTokens, string? mode, bool useModel)
    {
        var t = ToolHelpers.RequireText(task, "task", 4000);
        var m = (mode ?? "pack").Trim().ToLowerInvariant();
        if (m is not ("pack" or "rank" or "plan")) throw new ToolException("mode must be pack, rank or plan.");
        maxFiles = Math.Clamp(maxFiles <= 0 ? 8 : maxFiles, 1, 25);
        budgetTokens = Math.Clamp(budgetTokens <= 0 ? 3000 : budgetTokens, 500, 15000);

        ctx.Progress.Report("Indexing the project…");
        var index = await CodeIndex.LoadAsync(ctx, paths, codeOnly: false, ctx.Ct).ConfigureAwait(false);
        if (index.Files.Count == 0) throw new ToolException("No files to search. " + index.CoverageNote());

        // 1. Термины: из текста задачи + (модель) вероятные идентификаторы.
        var terms = ExtractTerms(t);
        var mentionedPaths = MentionedPaths(t, index.Files);
        LocalModel? model = null;
        if (useModel)
        {
            try
            {
                model = await ctx.GetModelAsync().ConfigureAwait(false);
                foreach (var x in await ExpandAsync(ctx, model, t).ConfigureAwait(false))
                    if (!terms.Contains(x, StringComparer.OrdinalIgnoreCase)) terms.Add(x);
            }
            catch (ToolException ex)
            {
                useModel = false;
                ctx.Progress.Report("local model unavailable, using keyword search only: " + ex.Message);
            }
        }
        if (terms.Count == 0 && mentionedPaths.Count == 0)
            throw new ToolException("Could not extract search terms from the task; mention identifiers, file names or English keywords.");

        // 2. Оценка файлов.
        var wantsTests = Regex.IsMatch(t, @"\btests?\b|тест", RegexOptions.IgnoreCase);
        var scored = index.Files.AsParallel().WithCancellation(ctx.Ct)
            .Select(f => Score(f, terms, mentionedPaths, wantsTests))
            .Where(s => s.Score > 0).OrderByDescending(s => s.Score).Take(maxFiles * 3).ToList();
        if (scored.Count == 0) return $"Nothing in the project matches the task terms ({string.Join(", ", terms.Take(20))}). {index.CoverageNote()}";

        // 3. Переранжирование моделью (с причинами).
        var selected = scored.Take(maxFiles).Select(s => (s.File, Why: s.Why)).ToList();
        if (useModel && model is not null && scored.Count > 1)
        {
            try
            {
                var reranked = await RerankAsync(ctx, model, t, scored, maxFiles).ConfigureAwait(false);
                if (reranked.Count > 0) selected = reranked;
            }
            catch (Exception ex) when (ex is ToolException or ContextExceededException)
            {
                ctx.Progress.Report("rerank skipped: " + ex.Message);
            }
        }

        var sb = new StringBuilder();
        sb.Append($"terms: {string.Join(", ", terms.Take(24))}\n");
        if (m == "rank")
        {
            sb.Append("files by relevance:\n");
            foreach (var (f, why) in selected) sb.Append($"  {f.Display}  — {why}\n");
            var rest = scored.Select(s => s.File).Except(selected.Select(s => s.File)).Take(10).ToList();
            if (rest.Count > 0) sb.Append("also matched: ").Append(string.Join(", ", rest.Select(f => f.Display))).Append('\n');
            return sb.Append(index.CoverageNote()).ToString();
        }

        // 4. Пакет контекста в бюджет.
        var pack = BuildPack(selected, terms, budgetTokens, out var expanded);
        sb.Append(pack);
        var notExpanded = selected.Skip(expanded).Select(s => s.File.Display).Concat(scored.Skip(maxFiles).Take(6).Select(s => s.File.Display)).Distinct().ToList();
        if (notExpanded.Count > 0) sb.Append($"\nalso relevant (not expanded; raise budget_tokens or ask local_symbols): {string.Join(", ", notExpanded)}\n");
        var tests = RelatedTests(index.Files, selected.Take(expanded).Select(s => s.File).ToList());
        if (tests.Count > 0) sb.Append("related tests: ").Append(string.Join(", ", tests)).Append('\n');
        ctx.Stats.FilesRead = index.Files.Count;
        ctx.Stats.TokensRead = selected.Sum(s => (long)Tokens.Estimate(string.Join('\n', s.File.Lines)));

        if (m == "plan" && model is not null)
        {
            var plan = await PlanAsync(ctx, model, t, sb.ToString()).ConfigureAwait(false);
            sb.Append("\nimplementation plan (local model draft — check it):\n").Append(plan).Append('\n');
        }
        return sb.Append(index.CoverageNote()).ToString();
    }

    // ───────────────────────── термины ─────────────────────────

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "when", "then", "than", "should", "would", "could", "must", "need", "needs",
        "add", "adds", "added", "make", "makes", "fix", "fixes", "use", "uses", "using", "new", "all", "any", "not", "are", "was", "were", "has",
        "have", "does", "how", "what", "where", "which", "who", "why", "code", "file", "files", "function", "method", "class", "implement",
        "change", "update", "create", "remove", "delete", "support", "feature", "bug", "issue", "task", "please", "also", "only", "just",
        "there", "their", "they", "will", "can", "get", "set", "via", "each", "some", "more", "less", "like", "its", "our", "your", "about",
        "should", "want", "wants", "into", "out", "per", "one", "two", "test", "tests",
    };

    [GeneratedRegex(@"[`""']([^`""']{3,60})[`""']", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Ident();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|_", RegexOptions.CultureInvariant)]
    private static partial Regex CamelSplit();

    internal static List<string> ExtractTerms(string task)
    {
        var terms = new List<string>();
        void Add(string x)
        {
            x = x.Trim();
            if (x.Length < 3 || Stop.Contains(x) || terms.Contains(x, StringComparer.OrdinalIgnoreCase)) return;
            terms.Add(x);
        }
        foreach (Match q in Quoted().Matches(task)) Add(q.Groups[1].Value);
        foreach (Match w in Ident().Matches(task))
        {
            Add(w.Value);
            // CamelCase/snake_case → части (ParseHeader → Parse, Header).
            var parts = CamelSplit().Split(w.Value).Where(p => p.Length >= 4).ToList();
            if (parts.Count > 1) foreach (var p in parts) Add(p);
        }
        return terms.Take(30).ToList();
    }

    private static List<string> MentionedPaths(string task, List<SourceFile> files)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(task, @"[\w./\\-]+\.[A-Za-z]{1,6}\b"))
        {
            var p = m.Value.Replace('\\', '/');
            var f = files.FirstOrDefault(x => x.Display.EndsWith(p, StringComparison.OrdinalIgnoreCase));
            if (f is not null && !list.Contains(f.Display)) list.Add(f.Display);
        }
        return list;
    }

    private static async Task<List<string>> ExpandAsync(ToolContext ctx, LocalModel model, string task)
    {
        var system = model.SystemPrompt(
            "Given a programming task (any language), output a JSON array of 5-15 short search keywords likely to appear in the relevant source code: " +
            "English identifiers, class/method name fragments, API names, domain words (e.g. [\"auth\",\"login\",\"TokenService\",\"refresh\"]). " +
            "Translate non-English words to the English terms a programmer would use in code. Output ONLY the JSON array.");
        await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, "TASK:\n" + task, 200, "expanding the query", ctx.Ct).ConfigureAwait(false);
        var text = OutputCleaner.StripFence(reply.Text, allowInnerBlock: true).Trim();
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim())
                .Where(s => s.Length is >= 3 and <= 40 && Regex.IsMatch(s, @"^[\w.\- ]+$") && !Stop.Contains(s)).Take(15).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ───────────────────────── оценка ─────────────────────────

    internal sealed record Scored(SourceFile File, double Score, string Why, List<CodeSymbol> Symbols, List<int> HitLines);

    internal static Scored Score(SourceFile f, List<string> terms, List<string> mentioned, bool wantsTests)
    {
        double score = 0;
        var matchedTerms = new List<string>();
        var symHits = new List<CodeSymbol>();
        var hitLines = new List<int>();
        var fileName = Path.GetFileNameWithoutExtension(f.Display);
        if (mentioned.Contains(f.Display)) score += 40;
        foreach (var term in terms)
        {
            double ts = 0;
            if (fileName.Equals(term, StringComparison.OrdinalIgnoreCase)) ts += 12;
            else if (fileName.Contains(term, StringComparison.OrdinalIgnoreCase)) ts += 5;
            else if (f.Display.Contains(term, StringComparison.OrdinalIgnoreCase)) ts += 2;
            foreach (var s in f.Symbols)
            {
                if (s.Name.Equals(term, StringComparison.OrdinalIgnoreCase)) { ts += s.IsType ? 12 : 9; symHits.Add(s); }
                else if (term.Length >= 4 && s.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) { ts += 3; symHits.Add(s); }
                if (ts > 30) break;
            }
            var count = 0;
            for (var i = 0; i < f.Lines.Length; i++)
            {
                if (f.Lines[i].Length > 1000 || !f.Lines[i].Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                if (hitLines.Count < 60) hitLines.Add(i + 1);
            }
            if (count > 0) ts += Math.Min(6, Math.Log2(1 + count) * 2);
            if (ts > 0) matchedTerms.Add(term);
            score += ts;
        }
        if (matchedTerms.Count > 1) score += 3 * Math.Pow(matchedTerms.Count, 1.2);
        if (f.IsTest && !wantsTests) score *= 0.5;
        if (!Symbols.IsCode(f.FullPath)) score *= 0.5;
        if (f.Lines.Take(12).Any(l => CodeRules.GeneratedMarker().IsMatch(l))) score *= 0.2;
        var syms = symHits.Distinct().OrderByDescending(s => s.IsType ? 0 : 1).Take(6).ToList();
        var why = $"matches {string.Join(", ", matchedTerms.Take(5))}" + (syms.Count > 0 ? $"; symbols {string.Join(", ", syms.Take(4).Select(s => s.Name))}" : "");
        return new Scored(f, score, why, syms, hitLines.Distinct().OrderBy(x => x).ToList());
    }

    private static async Task<List<(SourceFile File, string Why)>> RerankAsync(ToolContext ctx, LocalModel model, string task, List<Scored> scored, int maxFiles)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < scored.Count; i++)
        {
            var s = scored[i];
            sb.Append($"[{i + 1}] {s.File.Display}\n  symbols: {string.Join(", ", s.File.Symbols.Where(x => x.Kind != "namespace").Take(14).Select(x => x.Name))}\n");
            foreach (var l in s.HitLines.Take(3)) sb.Append("  ").Append(l).Append("| ").Append(Short(s.File.Lines[l - 1].Trim(), 120)).Append('\n');
        }
        var system = model.SystemPrompt(
            $"You rank candidate files by how useful they are for implementing/understanding the task. Pick at most {maxFiles}, most important first. " +
            "Output ONLY a JSON array like [{\"n\":3,\"why\":\"defines TokenService.Refresh\"}] with n = candidate number and a short reason (max 12 words).");
        var budget = model.MaterialBudget(400, system, task);
        var material = sb.ToString();
        if (Tokens.Estimate(material) > budget) material = material[..Math.Max(0, budget * 3)];
        await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, "TASK:\n" + task + "\n\nCANDIDATES:\n" + material, 400, "ranking files", ctx.Ct).ConfigureAwait(false);
        var text = OutputCleaner.StripFence(reply.Text, allowInnerBlock: true);
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        var result = new List<(SourceFile, string)>();
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("n", out var n) || !n.TryGetInt32(out var k) || k < 1 || k > scored.Count) continue;
                var why = e.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? Short(w.GetString()!, 120) : scored[k - 1].Why;
                if (result.Any(r => r.Item1 == scored[k - 1].File)) continue;
                result.Add((scored[k - 1].File, why));
                if (result.Count >= maxFiles) break;
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return result;
    }

    // ───────────────────────── пакет ─────────────────────────

    private static string BuildPack(List<(SourceFile File, string Why)> selected, List<string> terms, int budgetTokens, out int expanded)
    {
        var sb = new StringBuilder();
        expanded = 0;
        var perFile = Math.Max(250, budgetTokens / Math.Max(1, selected.Count));
        foreach (var (f, why) in selected)
        {
            var used = Tokens.Estimate(sb.ToString());
            if (used > budgetTokens - 150) break;
            var fileBudget = Math.Min(perFile + Math.Max(0, (budgetTokens - used) / 3), budgetTokens - used);
            var part = new StringBuilder($"\n## {f.Display} ({f.Lines.Length} lines) — {why}\n");
            // Outline верхнего уровня (коротко).
            var outline = f.Symbols.Where(s => s.Kind != "namespace" && (s.IsType || s.Container is null || s.Container.Count(c => c == '.') == 0)).Take(25).ToList();
            if (outline.Count > 0) part.Append("outline: ").Append(string.Join("; ", outline.Select(s => $"{s.Kind} {s.Name} {s.Line}-{s.EndLine}"))).Append('\n');
            // Фрагменты: символы, в имени или теле которых есть термины; иначе строки с совпадениями ±3.
            var relevant = f.Symbols.Where(s => SymbolsTool.IsCallable(s) || !s.IsType)
                .Select(s => (s, Hits: terms.Count(t => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) * 3
                                       + Enumerable.Range(s.Line, Math.Max(0, Math.Min(s.EndLine, f.Lines.Length) - s.Line + 1)).Count(i => terms.Any(t => f.Lines[i - 1].Contains(t, StringComparison.OrdinalIgnoreCase)))))
                .Where(x => x.Hits > 0).OrderByDescending(x => x.Hits).Select(x => x.s).ToList();
            var covered = new HashSet<int>();
            foreach (var s in relevant)
            {
                if (Tokens.Estimate(part.ToString()) > fileBudget) break;
                var end = Math.Min(s.EndLine, s.Line + 60);
                if (Enumerable.Range(s.Line, end - s.Line + 1).All(covered.Contains)) continue;
                part.Append(CodeIndex.Numbered(f, s.Line, end));
                if (end < s.EndLine) part.Append($"… ({s.EndLine - end} more lines of {s.Name})\n");
                for (var i = s.Line; i <= end; i++) covered.Add(i);
            }
            if (relevant.Count == 0)
            {
                var hits = new List<int>();
                for (var i = 0; i < f.Lines.Length && hits.Count < 12; i++)
                    if (terms.Any(t => f.Lines[i].Contains(t, StringComparison.OrdinalIgnoreCase))) hits.Add(i + 1);
                var last = 0;
                foreach (var h in hits)
                {
                    if (Tokens.Estimate(part.ToString()) > fileBudget) break;
                    var from = Math.Max(Math.Max(1, h - 3), last + 1);
                    var to = Math.Min(f.Lines.Length, h + 3);
                    if (from > to) continue;
                    if (last > 0 && from > last + 1) part.Append("…\n");
                    part.Append(CodeIndex.Numbered(f, from, to));
                    last = to;
                }
            }
            sb.Append(part);
            expanded++;
        }
        return sb.ToString();
    }

    private static List<string> RelatedTests(List<SourceFile> all, List<SourceFile> files)
    {
        var names = files.SelectMany(f => f.Symbols.Where(s => s.IsType && s.Kind != "namespace").Select(s => s.Name).Take(3))
            .Concat(files.Select(f => Path.GetFileNameWithoutExtension(f.Display))).Where(n => n.Length >= 4).Distinct().ToList();
        return all.Where(f => f.IsTest && !files.Contains(f) && names.Any(n => Path.GetFileNameWithoutExtension(f.Display).StartsWith(n, StringComparison.OrdinalIgnoreCase)
                || f.Lines.Take(400).Any(l => l.Contains(n, StringComparison.Ordinal))))
            .Select(f => f.Display).Take(8).ToList();
    }

    private static async Task<string> PlanAsync(ToolContext ctx, LocalModel model, string task, string pack)
    {
        var system = model.SystemPrompt(
            "Draft an implementation plan for the task using ONLY the provided code context. Output sections: Files to change (path - what), " +
            "New files, Steps (numbered, concrete), Tests (which to add/update and the command to run), Risks/open questions. Be concise; cite path:line.");
        var budget = model.MaterialBudget(900, system, task);
        var material = Tokens.Estimate(pack) > budget ? pack[..Math.Max(0, budget * 3)] : pack;
        await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, "CONTEXT:\n" + material + "\n\nTASK:\n" + task, 900, "planning", ctx.Ct).ConfigureAwait(false);
        return reply.Text.Trim();
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
