using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Offload.Llama;
using Offload.Mcp.Index;
using Offload.Mcp.Infrastructure;
using static Offload.Mcp.Infrastructure.TextUtil;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_find_context: задача на естественном языке → релевантные файлы, символы и фрагменты кода, уложенные в бюджет токенов.
/// Поиск детерминированный (BM25 по постоянному индексу × имена символов × пути), локальная модель — для расширения запроса
/// (в т. ч. с русского на идентификаторы кода), переранжирования с объяснением и (mode=plan) плана реализации.
/// Если назначены вспомогательные модели: embed — гибрид BM25 + векторы фрагментов (слияние рангов RRF), rerank — реранкер
/// вместо реранка моделью. Какое ранжирование сработало, пишется в подвале (ranking: bm25 | hybrid | hybrid+rerank …).
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
        using var index = await CodeIndex.OpenAsync(ctx, paths, codeOnly: false, ctx.Ct).ConfigureAwait(false);
        if (index.Files.Count == 0) throw new ToolException("No files to search. " + index.CoverageNote());

        // 1. Термины: из текста задачи + (модель) вероятные идентификаторы.
        var terms = ExtractTerms(t);
        var mentionedPaths = MentionedPaths(t, index.Files);
        // Вспомогательные модели (embed/rerank) — тоже «модель»: use_model=false оставляет чистый BM25.
        var useAux = useModel;
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

        // 2. Оценка файлов: BM25 по обратному индексу + имена символов и путей.
        var wantsTests = Regex.IsMatch(t, @"\btests?\b|тест", RegexOptions.IgnoreCase);
        var scored = Rank(index, terms, mentionedPaths, wantsTests, maxFiles * 3);

        // 2a. Гибрид: векторы (роль embed) → слияние рангов BM25 и косинуса (RRF).
        var ranking = "bm25";
        string? vectorNote = null;
        var focus = new Dictionary<string, ChunkHit>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> semanticTests = [];
        if (useAux && Embedder.Configured(ctx.Cfg))
        {
            var hybrid = await HybridAsync(ctx, index, t, scored, wantsTests, maxFiles * 3).ConfigureAwait(false);
            if (hybrid is not null)
            {
                scored = hybrid.Scored;
                focus = hybrid.Focus;
                semanticTests = hybrid.Tests;
                vectorNote = hybrid.Note;
                ranking = "hybrid";
            }
            else
            {
                vectorNote = "vectors: unavailable";
            }
        }
        if (scored.Count == 0) return $"Nothing in the project matches the task terms ({string.Join(", ", terms.Take(20))}). {index.CoverageNote()}";

        // 3. Переранжирование: реранкер (роль rerank), если назначен; иначе — моделью (с причинами).
        var selected = scored.Take(maxFiles).Select(s => (s.File, Why: s.Why)).ToList();
        var reranked = false;
        if (useAux && scored.Count > 1 && Reranker.Configured(ctx.Cfg))
        {
            var byReranker = await RerankWithRerankerAsync(ctx, t, scored, focus, maxFiles).ConfigureAwait(false);
            if (byReranker is not null)
            {
                selected = byReranker;
                ranking += "+rerank";
                reranked = true;
            }
        }
        if (!reranked && useModel && model is not null && scored.Count > 1)
        {
            try
            {
                var byModel = await RerankAsync(ctx, model, t, scored, maxFiles).ConfigureAwait(false);
                if (byModel.Count > 0)
                {
                    selected = byModel;
                    ranking += "+llm";
                }
            }
            catch (Exception ex) when (ex is ToolException or ContextExceededException)
            {
                ctx.Progress.Report("rerank skipped: " + ex.Message);
            }
        }
        var footer = $"ranking: {ranking}" + (vectorNote is null ? "" : " · " + vectorNote) + "\n";

        var sb = new StringBuilder();
        sb.Append($"terms: {string.Join(", ", terms.Take(24))}\n");
        if (m == "rank")
        {
            sb.Append("files by relevance:\n");
            foreach (var (f, why) in selected) sb.Append($"  {f.Display}  — {why}\n");
            var rest = scored.Select(s => s.File).Except(selected.Select(s => s.File)).Take(10).ToList();
            if (rest.Count > 0) sb.Append("also matched: ").Append(string.Join(", ", rest.Select(f => f.Display))).Append('\n');
            return sb.Append(footer).Append(index.CoverageNote()).ToString();
        }

        // 4. Пакет контекста в бюджет.
        var pack = BuildPack(selected, terms, budgetTokens, out var expanded, focus);
        sb.Append(pack);
        var notExpanded = selected.Skip(expanded).Select(s => s.File.Display).Concat(scored.Skip(maxFiles).Take(6).Select(s => s.File.Display)).Distinct().ToList();
        if (notExpanded.Count > 0) sb.Append($"\nalso relevant (not expanded; raise budget_tokens or ask local_symbols): {string.Join(", ", notExpanded)}\n");
        var tests = RelatedTests(index, selected.Take(expanded).Select(s => s.File).ToList(), semanticTests);
        if (tests.Count > 0) sb.Append("related tests: ").Append(string.Join(", ", tests)).Append('\n');
        ctx.Stats.FilesRead = index.Files.Count;
        ctx.Stats.AddScanned(index.TotalChars, index.Files.Count);
        ctx.Stats.TokensRead = selected.Sum(s => (long)Tokens.Estimate(string.Join('\n', s.File.Lines)));

        if (m == "plan" && model is not null)
        {
            var plan = await PlanAsync(ctx, model, t, sb.ToString()).ConfigureAwait(false);
            sb.Append("\nimplementation plan (local model draft — check it):\n").Append(plan).Append('\n');
        }
        return sb.Append(footer).Append(index.CoverageNote()).ToString();
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

    private static List<string> MentionedPaths(string task, IReadOnlyList<IndexedFile> files)
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
        await using var slot = await GpuQueue.AcquireAsync(ctx, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, "TASK:\n" + task, 200, "expanding the query", ctx.Ct).ConfigureAwait(false);
        var text = OutputCleaner.StripFence(reply.Text, allowInnerBlock: true).Trim();
        using var doc = ParseJsonFragment(text, JsonValueKind.Array);
        if (doc is null) return [];
        return doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim())
            .Where(s => s.Length is >= 3 and <= 40 && Regex.IsMatch(s, @"^[\w.\- ]+$") && !Stop.Contains(s)).Take(15).ToList();
    }

    // ───────────────────────── оценка ─────────────────────────

    internal sealed record Scored(SourceFile File, double Score, string Why, List<CodeSymbol> Symbols, List<int> HitLines);

    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>Накопитель оценки одного файла.</summary>
    private sealed class Acc
    {
        public double Body;
        public double Names;
        public readonly HashSet<int> Matched = [];
        public readonly List<CodeSymbol> Symbols = [];
        public readonly SortedSet<int> HitLines = [];
    }

    /// <summary>
    /// Ранжирование файлов по индексу: BM25 по токенам терминов (части идентификаторов в нижнем регистре; IDF — по числу файлов
    /// области с токеном в обратном индексе; длина документа — число слов файла; совпадение по началу части — с весом 0,5),
    /// плюс совпадения с именами символов и путями, бонус за несколько разных терминов и штрафы для тестов, не-кода и
    /// сгенерированных файлов. Текст читается только у отобранных take файлов.
    /// </summary>
    internal static List<Scored> Rank(IndexView index, List<string> terms, List<string> mentioned, bool wantsTests, int take)
    {
        var n = index.Files.Count;
        if (n == 0) return [];
        var avgdl = index.AverageTokens;
        var acc = new Dictionary<long, Acc>();
        Acc Of(IndexedFile f) => acc.TryGetValue(f.Id, out var a) ? a : acc[f.Id] = new Acc();

        // Токен запроса → номера терминов, из которых он получен (для «matches …» и бонуса за число терминов).
        var tokens = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < terms.Count; i++)
            foreach (var tok in IndexTokens.QueryTokens(terms[i]))
            {
                if (!tokens.TryGetValue(tok, out var list)) tokens[tok] = list = [];
                if (!list.Contains(i)) list.Add(i);
            }

        foreach (var (tok, termIdx) in tokens)
        {
            var postings = index.PartPostings(tok, prefix: tok.Length >= 4);
            var perFile = new Dictionary<long, (IndexedFile File, double Tf, List<int> Lines)>();
            foreach (var p in postings)
            {
                var w = p.Exact ? 1.0 : 0.5;
                perFile[p.File.Id] = perFile.TryGetValue(p.File.Id, out var cur)
                    ? (cur.File, cur.Tf + w * p.Tf, cur.Lines.Count < 60 ? [.. cur.Lines, .. p.Lines] : cur.Lines)
                    : (p.File, w * p.Tf, p.Lines);
            }
            var df = perFile.Count;
            if (df == 0) continue;
            var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
            foreach (var (file, tf, lines) in perFile.Values)
            {
                var norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * Math.Max(1, file.Tokens) / avgdl));
                var a = Of(file);
                a.Body += idf * norm;
                foreach (var i in termIdx) a.Matched.Add(i);
                foreach (var l in lines)
                {
                    if (a.HitLines.Count >= 60) break;
                    a.HitLines.Add(l);
                }
            }
        }

        // Имена символов и пути.
        for (var i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            var perTerm = new Dictionary<long, double>();
            foreach (var x in index.SymbolsNamed(term, ignoreCase: true))
            {
                var v = perTerm.GetValueOrDefault(x.File.Id);
                if (v > 30) continue;
                perTerm[x.File.Id] = v + (x.Symbol.IsType ? 12 : 9);
                Of(x.File).Symbols.Add(x.Symbol);
            }
            if (term.Length >= 4 && IndexTokens.IsIndexable(term))
            {
                foreach (var x in index.SymbolsByPart(term.ToLowerInvariant(), prefix: true))
                {
                    if (x.Symbol.Name.Equals(term, StringComparison.OrdinalIgnoreCase) || !x.Symbol.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                    var v = perTerm.GetValueOrDefault(x.File.Id);
                    if (v > 30) continue;
                    perTerm[x.File.Id] = v + 3;
                    Of(x.File).Symbols.Add(x.Symbol);
                }
            }
            foreach (var f in index.Files)
            {
                var fileName = Path.GetFileNameWithoutExtension(f.Display);
                double ns = fileName.Equals(term, StringComparison.OrdinalIgnoreCase) ? 12
                    : fileName.Contains(term, StringComparison.OrdinalIgnoreCase) ? 5
                    : f.Display.Contains(term, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                if (ns > 0) perTerm[f.Id] = perTerm.GetValueOrDefault(f.Id) + ns;
            }
            foreach (var (id, v) in perTerm)
            {
                var a = Of(index.FileById(id)!);
                a.Names += v;
                a.Matched.Add(i);
            }
        }
        foreach (var d in mentioned)
            if (index.FileByDisplay(d) is { } f) Of(f).Names += 40;

        var ranked = new List<(IndexedFile File, double Score, Acc Acc)>();
        foreach (var (id, a) in acc)
        {
            var f = index.FileById(id)!;
            var score = a.Names + 1.5 * a.Body;
            if (a.Matched.Count > 1) score += 3 * Math.Pow(a.Matched.Count, 1.2);
            if (f.IsTest && !wantsTests) score *= 0.5;
            if (!f.IsCode) score *= 0.5;
            if (f.Generated) score *= 0.2;
            if (score > 0) ranked.Add((f, score, a));
        }

        var result = new List<Scored>();
        foreach (var (f, score, a) in ranked.OrderByDescending(r => r.Score).ThenBy(r => r.File.Order))
        {
            var text = index.Text(f);
            if (text is null) continue;
            var syms = a.Symbols.Distinct().OrderByDescending(x => x.IsType ? 0 : 1).Take(6).ToList();
            var matched = a.Matched.Order().Select(i => terms[i]).ToList();
            var why = $"matches {string.Join(", ", matched.Take(5))}" + (syms.Count > 0 ? $"; symbols {string.Join(", ", syms.Take(4).Select(x => x.Name))}" : "");
            result.Add(new Scored(text, score, why, syms, [.. a.HitLines.Where(l => l <= text.Lines.Length)]));
            if (result.Count >= take) break;
        }
        return result;
    }

    /// <summary>
    /// Прежняя оценка одного загруженного файла (подстроки терминов в строках). Используется local_solve до перехода на
    /// <see cref="Rank"/>; линейна по размеру файла.
    /// </summary>

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
        await using var slot = await GpuQueue.AcquireAsync(ctx, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, "TASK:\n" + task + "\n\nCANDIDATES:\n" + material, 400, "ranking files", ctx.Ct).ConfigureAwait(false);
        var text = OutputCleaner.StripFence(reply.Text, allowInnerBlock: true);
        using var doc = ParseJsonFragment(text, JsonValueKind.Array);
        if (doc is null) return [];
        var result = new List<(SourceFile, string)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.Number
                || !n.TryGetInt32(out var k) || k < 1 || k > scored.Count) continue;
            var why = e.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? Short(w.GetString()!, 120) : scored[k - 1].Why;
            if (result.Any(r => r.Item1 == scored[k - 1].File)) continue;
            result.Add((scored[k - 1].File, why));
            if (result.Count >= maxFiles) break;
        }
        return result;
    }

    // ───────────────────────── гибрид и реранкер ─────────────────────────

    /// <summary>
    /// Кандидаты после слияния BM25 и векторов, лучший фрагмент файла по векторам (display → фрагмент), строка покрытия и
    /// тестовые файлы, близкие к задаче по смыслу.
    /// </summary>
    private sealed record HybridResult(List<Scored> Scored, Dictionary<string, ChunkHit> Focus, string Note, IReadOnlyList<string> Tests);

    /// <summary>
    /// Гибридный поиск (ROADMAP §6.3): досчитать векторы недостающих файлов в пределах бюджета, найти ближайшие к задаче файлы
    /// по косинусу лучшего фрагмента и слить с кандидатами BM25 методом reciprocal rank fusion. Модель эмбеддингов или хранилище
    /// недоступны — null (остаётся BM25).
    /// </summary>
    private static async Task<HybridResult?> HybridAsync(ToolContext ctx, IndexView index, string task, List<Scored> bm25, bool wantsTests, int take)
    {
        var embedder = await Embedder.ConnectAsync(ctx).ConfigureAwait(false);
        if (embedder is null) return null;
        using var store = EmbeddingsIndex.TryOpenStore(ctx, embedder.ModelId);
        if (store is null) return null;
        VectorSearch found;
        try
        {
            var priority = bm25.Select(s => index.FileByDisplay(s.File.Display)).OfType<IndexedFile>().ToList();
            found = await EmbeddingsIndex.SearchAsync(ctx, embedder, store, index, task, priority, wantsTests, take).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LlamaApiException or ToolException or SqliteException or IOException)
        {
            Offload.Core.Logging.Log.Warn("vectors", "векторный поиск не удался: " + ex.Message);
            ctx.Progress.Report("vector search skipped: " + ex.Message);
            return null;
        }

        var byDisplay = bm25.ToDictionary(s => s.File.Display, StringComparer.OrdinalIgnoreCase);
        var hits = found.Ranked.ToDictionary(r => r.File.Display, r => r.Hit, StringComparer.OrdinalIgnoreCase);
        var fused = VectorMath.Fuse(StringComparer.OrdinalIgnoreCase, bm25.Select(s => s.File.Display).ToList(), found.Ranked.Select(r => r.File.Display).ToList());
        var list = new List<Scored>();
        foreach (var (display, score) in fused)
        {
            if (list.Count >= take) break;
            hits.TryGetValue(display, out var hit);
            if (byDisplay.TryGetValue(display, out var s))
            {
                list.Add(s with { Score = score, Why = hit is null ? s.Why : $"{s.Why}; semantic {hit.Label} ({Num(hit.Score)})" });
                continue;
            }
            if (hit is null || index.FileByDisplay(display) is not { } f || index.Text(f) is not { } text) continue;
            var syms = text.Symbols.Where(x => x.Kind != "namespace" && x.Line <= hit.EndLine && x.EndLine >= hit.StartLine)
                .OrderByDescending(x => x.IsType ? 0 : 1).Take(6).ToList();
            var lines = Enumerable.Range(hit.StartLine, Math.Max(0, Math.Min(hit.EndLine, text.Lines.Length) - hit.StartLine + 1))
                .Where(l => text.Lines[l - 1].Trim().Length > 0).Take(3).ToList();
            list.Add(new Scored(text, score, $"semantic match: {hit.Label} ({Num(hit.Score)})", syms, lines));
        }
        return new HybridResult(list, hits, found.Note(), found.Tests.Select(t => t.File.Display).ToList());
    }

    /// <summary>
    /// Реранкер (роль rerank) вместо реранка моделью: верхние кандидаты — короткими фрагментами (путь, символы, лучший фрагмент по
    /// векторам или строки с совпадениями, секреты замаскированы) → оценки → лучшие max_files. Недоступен — null.
    /// </summary>
    private static async Task<List<(SourceFile File, string Why)>?> RerankWithRerankerAsync(ToolContext ctx, string task, List<Scored> scored,
        IReadOnlyDictionary<string, ChunkHit> focus, int maxFiles)
    {
        var top = scored.Take(Math.Min(24, Math.Max(maxFiles * 2, 10))).ToList();
        var docs = top.Select(s => RerankSnippet(s, focus.GetValueOrDefault(s.File.Display))).ToList();
        var scores = await Reranker.RerankAsync(ctx, task, docs).ConfigureAwait(false);
        if (scores is null || scores.Count == 0) return null;
        return scores.Where(x => x.Index >= 0 && x.Index < top.Count).DistinctBy(x => x.Index).Take(maxFiles)
            .Select(x => (top[x.Index].File, $"{top[x.Index].Why}; rerank {Num(x.Score)}")).ToList();
    }

    /// <summary>Оценка для текста ответа: два знака, точка как разделитель (не зависит от языка системы).</summary>
    private static string Num(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Фрагмент файла для реранкера (не длиннее ~2000 символов, секреты замаскированы).</summary>
    internal static string RerankSnippet(Scored s, ChunkHit? hit)
    {
        var f = s.File;
        var sb = new StringBuilder(f.Display).Append('\n');
        var names = f.Symbols.Where(x => x.Kind != "namespace").Take(12).Select(x => x.Name).ToList();
        if (names.Count > 0) sb.Append("symbols: ").Append(string.Join(", ", names)).Append('\n');
        IEnumerable<int> lines = hit is not null && hit.StartLine >= 1
            ? Enumerable.Range(hit.StartLine, Math.Max(0, Math.Min(hit.EndLine, hit.StartLine + 30) - hit.StartLine + 1))
            : s.HitLines.Count > 0
                ? s.HitLines.SelectMany(l => new[] { l - 1, l, l + 1 }).Distinct().Order().Take(24)
                : Enumerable.Range(1, 30);
        foreach (var l in lines)
        {
            if (l < 1 || l > f.Lines.Length) continue;
            sb.Append(Short(f.Lines[l - 1].Trim(), 200)).Append('\n');
            if (sb.Length > 2000) break;
        }
        var text = sb.Length > 2000 ? sb.ToString(0, 2000) : sb.ToString();
        return SecretRedactor.Redact(text, f.FullPath);
    }

    // ───────────────────────── пакет ─────────────────────────

    /// <summary>
    /// Пакет контекста в бюджет токенов: по каждому файлу — outline и фрагменты символов с совпадениями (или строки ±3).
    /// Токены считаются нарастающим итогом по добавляемым кускам (без повторной оценки всего пакета — линейно).
    /// </summary>
    internal static string BuildPack(List<(SourceFile File, string Why)> selected, List<string> terms, int budgetTokens, out int expanded,
        IReadOnlyDictionary<string, ChunkHit>? focus = null)
    {
        var sb = new StringBuilder();
        var used = 0;
        expanded = 0;
        var perFile = Math.Max(250, budgetTokens / Math.Max(1, selected.Count));
        foreach (var (f, why) in selected)
        {
            if (used > budgetTokens - 150) break;
            var fileBudget = Math.Min(perFile + Math.Max(0, (budgetTokens - used) / 3), budgetTokens - used);
            var part = new StringBuilder();
            var partTokens = 0;
            void Add(string text)
            {
                part.Append(text);
                partTokens += Tokens.Estimate(text);
            }
            Add($"\n## {f.Display} ({f.Lines.Length} lines) — {why}\n");
            // Outline верхнего уровня (коротко).
            var outline = f.Symbols.Where(s => s.Kind != "namespace" && (s.IsType || s.Container is null || !s.Container.Any(c => c == '.'))).Take(25).ToList();
            if (outline.Count > 0) Add("outline: " + string.Join("; ", outline.Select(s => $"{s.Kind} {s.Name} {s.Line}-{s.EndLine}")) + "\n");
            // Строки с терминами — один проход; число совпадений в диапазоне символа — по префиксным суммам.
            var lineCount = f.Lines.Length;
            var hitPrefix = new int[lineCount + 1];
            for (var i = 1; i <= lineCount; i++)
            {
                var line = f.Lines[i - 1];
                hitPrefix[i] = hitPrefix[i - 1] + (terms.Any(t => line.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 1 : 0);
            }
            int HitsIn(int from, int to)
            {
                from = Math.Max(1, from);
                to = Math.Min(lineCount, to);
                return to < from ? 0 : hitPrefix[to] - hitPrefix[from - 1];
            }
            // Фрагменты: символы, в имени или теле которых есть термины; иначе строки с совпадениями ±3.
            var relevant = f.Symbols.Where(s => SymbolsTool.IsCallable(s) || !s.IsType)
                .Select(s => (s, Hits: terms.Count(t => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) * 3 + HitsIn(s.Line, s.EndLine)))
                .Where(x => x.Hits > 0).OrderByDescending(x => x.Hits).Select(x => x.s).ToList();
            var covered = new HashSet<int>();
            // Фрагмент, ближайший к задаче по векторам, — первым (его может не быть среди совпадений терминов).
            if (focus is not null && focus.TryGetValue(f.Display, out var hit) && hit.StartLine >= 1 && hit.StartLine <= lineCount)
            {
                var end = Math.Min(Math.Min(lineCount, hit.EndLine), hit.StartLine + 40);
                Add(CodeIndex.Numbered(f, hit.StartLine, end));
                for (var i = hit.StartLine; i <= end; i++) covered.Add(i);
            }
            foreach (var s in relevant)
            {
                if (partTokens > fileBudget) break;
                var end = Math.Min(s.EndLine, s.Line + 60);
                if (Enumerable.Range(s.Line, Math.Max(0, end - s.Line + 1)).All(covered.Contains)) continue;
                Add(CodeIndex.Numbered(f, s.Line, end));
                if (end < s.EndLine) Add($"… ({s.EndLine - end} more lines of {s.Name})\n");
                for (var i = s.Line; i <= end; i++) covered.Add(i);
            }
            if (relevant.Count == 0 && covered.Count == 0)
            {
                var hits = new List<int>();
                for (var i = 1; i <= lineCount && hits.Count < 12; i++)
                    if (hitPrefix[i] > hitPrefix[i - 1]) hits.Add(i);
                var last = 0;
                foreach (var h in hits)
                {
                    if (partTokens > fileBudget) break;
                    var from = Math.Max(Math.Max(1, h - 3), last + 1);
                    var to = Math.Min(lineCount, h + 3);
                    if (from > to) continue;
                    if (last > 0 && from > last + 1) Add("…\n");
                    Add(CodeIndex.Numbered(f, from, to));
                    last = to;
                }
            }
            sb.Append(part);
            used += partTokens;
            expanded++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Связанные тесты: тестовые файлы, чьё имя начинается с имени типа/файла из пакета или в чьих идентификаторах
    /// оно встречается (словарь обратного индекса, без чтения текста тестов), слитые (RRF) с тестами, близкими к задаче
    /// по смыслу (<paramref name="semantic"/>, гибридный режим). Найденные только по смыслу помечены «(semantic)».
    /// </summary>
    internal static List<string> RelatedTests(IndexView index, List<SourceFile> files, IReadOnlyList<string> semantic)
    {
        var lexical = LexicalTests(index, files);
        if (semantic.Count == 0) return lexical;
        var chosen = files.Select(f => f.Display).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = semantic.Where(t => !chosen.Contains(t)).ToList();
        return VectorMath.Fuse(StringComparer.OrdinalIgnoreCase, lexical, extra).Take(8)
            .Select(x => lexical.Contains(x.Item, StringComparer.OrdinalIgnoreCase) ? x.Item : x.Item + " (semantic)").ToList();
    }

    private static List<string> LexicalTests(IndexView index, List<SourceFile> files)
    {
        var chosen = files.Select(f => index.FileByDisplay(f.Display)).Where(f => f is not null).Select(f => f!).ToList();
        var chosenIds = chosen.Select(f => f.Id).ToHashSet();
        var names = chosen.SelectMany(f => index.SymbolsOf(f).Where(s => s.Symbol.IsType && s.Symbol.Kind != "namespace").Select(s => s.Symbol.Name).Take(3))
            .Concat(files.Select(f => Path.GetFileNameWithoutExtension(f.Display))).Where(n => n.Length >= 4).Distinct().ToList();
        var mentioning = new HashSet<long>();
        foreach (var name in names.Where(IndexTokens.IsIndexable))
            mentioning.UnionWith(index.CandidateFiles(name, wholeWord: false, caseSensitive: true));
        return index.Files.Where(f => f.IsTest && !chosenIds.Contains(f.Id)
                && (mentioning.Contains(f.Id) || names.Any(n => Path.GetFileNameWithoutExtension(f.Display).StartsWith(n, StringComparison.OrdinalIgnoreCase))))
            .Select(f => f.Display).Take(8).ToList();
    }

    private static async Task<string> PlanAsync(ToolContext ctx, LocalModel model, string task, string pack)
    {
        var system = model.SystemPrompt(
            "Draft an implementation plan for the task using ONLY the provided code context. Output sections: Files to change (path - what), " +
            "New files, Steps (numbered, concrete), Tests (which to add/update and the command to run), Risks/open questions. Be concise; cite path:line.");
        var budget = model.MaterialBudget(900, system, task);
        var material = Tokens.Estimate(pack) > budget ? pack[..Math.Max(0, budget * 3)] : pack;
        await using var slot = await GpuQueue.AcquireAsync(ctx, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, "CONTEXT:\n" + material + "\n\nTASK:\n" + task, 900, "planning", ctx.Ct).ConfigureAwait(false);
        return reply.Text.Trim();
    }

}
