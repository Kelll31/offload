using Microsoft.Data.Sqlite;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Index;

/// <summary>Файл в представлении индекса (без текста): путь, язык, размеры, признаки и порядок перечисления.</summary>
internal sealed class IndexedFile
{
    public required long Id { get; init; }
    public required string FullPath { get; init; }
    public required string Display { get; init; }
    public required CodeLang Lang { get; init; }
    public required int LineCount { get; init; }
    public required long Chars { get; init; }
    public required int Tokens { get; init; }
    public required int Order { get; init; }
    public bool Truncated { get; init; }
    public bool Generated { get; init; }
    public bool LongTokens { get; init; }
    public string? Entry { get; init; }
    public bool IsTest { get; init; }

    /// <summary>sha замаскированного текста (как в таблице files): ключ векторов файла в <see cref="EmbeddingsStore"/>.</summary>
    public string Sha { get; init; } = "";
    public bool IsCode => Lang != CodeLang.Unknown;
}

/// <summary>Символ из индекса: id строки symbols, файл и само объявление.</summary>
internal sealed record IndexedSymbol(long Id, IndexedFile File, CodeSymbol Symbol);

/// <summary>Место вызова из таблицы calls: вызывающий символ, имя вызываемого, цепочка получателя, строка.</summary>
internal sealed record CallRow(IndexedSymbol Caller, string Callee, string? Chain, int Line);

/// <summary>Вхождения идентификатора в файле: сам идентификатор, tf, строки (обрезанный список — Capped), совпала ли часть целиком.</summary>
internal sealed record Posting(IndexedFile File, string Term, int Tf, List<int> Lines, bool Capped, bool Exact);

/// <summary>
/// Представление индекса для одного вызова инструмента: только файлы, найденные и проверенные в этом вызове (FileGatherer/PathGuard),
/// запросы к символам, обратному индексу и графу вызовов — к SQLite в одном снимке; текст файлов — лениво через кэш CodeIndex
/// (замаскированный или нет — по Mcp.RedactSecrets, как у остальных инструментов).
/// </summary>
internal sealed class IndexView : IDisposable
{
    private readonly IndexSession _session;
    private readonly IReadOnlyList<string> _roots;
    private readonly int _maxBytes;
    private readonly bool _redact;
    private readonly List<IndexedFile> _files = [];
    private readonly Dictionary<long, IndexedFile> _byId = [];
    private readonly Dictionary<string, IndexedFile> _byDisplay = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, List<IndexedSymbol>> _symbols = [];
    private readonly Dictionary<long, IndexedSymbol?> _symbolById = [];

    public IndexView(IndexSession session, IReadOnlyList<string> searchable, IReadOnlyList<string> all, GatherResult info, IReadOnlyList<string> roots,
        bool codeOnly, int maxBytes, bool redact)
    {
        _session = session;
        _roots = roots;
        _maxBytes = maxBytes;
        _redact = redact;
        AllPaths = all;
        Info = info;
        foreach (var path in searchable)
        {
            if (!session.Rows.TryGetValue(path, out var row) || !row.Ok || codeOnly && row.Lang == CodeLang.Unknown) continue;
            if (_byId.ContainsKey(row.Id)) continue;
            var display = PathGuard.Display(path, roots);
            var f = new IndexedFile
            {
                Id = row.Id,
                FullPath = path,
                Display = display,
                Lang = row.Lang,
                LineCount = row.Lines,
                Chars = row.Chars,
                Tokens = row.Tokens,
                Order = _files.Count,
                Truncated = row.Truncated,
                Generated = row.Generated,
                LongTokens = row.LongTokens,
                Entry = row.Entry,
                IsTest = CodeIndex.IsTestPath(display),
                Sha = row.Sha,
            };
            _files.Add(f);
            _byId[f.Id] = f;
            _byDisplay.TryAdd(display, f);
            TotalChars += f.Chars;
        }
    }

    /// <summary>Файлы области в порядке перечисления (как CodeIndex.LoadAsync).</summary>
    public IReadOnlyList<IndexedFile> Files => _files;

    /// <summary>Все найденные файлы (включая не-текстовые для поиска: go.mod, LICENSE…) — для карты проекта.</summary>
    public IReadOnlyList<string> AllPaths { get; }

    public GatherResult Info { get; }
    public IndexSyncStats Sync => _session.Stats;
    public bool Persistent => _session.Persistent;
    public string? DbPath => _session.DbPath;

    /// <summary>Символов в файлах области (строки + переводы строк) — для учёта просмотренного материала.</summary>
    public long TotalChars { get; }

    public double AverageTokens => _files.Count == 0 ? 1 : Math.Max(1, _files.Average(f => (double)f.Tokens));

    public string CoverageNote()
    {
        var parts = new List<string> { $"{_files.Count} files indexed" };
        if (Info.LimitNote is not null) parts.Add(Info.LimitNote);
        var skipped = Info.Skipped.Where(s => s.Reason is not ("binary" or "no files match")).Take(5).ToList();
        if (skipped.Count > 0) parts.Add("skipped: " + string.Join(", ", skipped.Select(s => $"{s.Display} ({s.Reason})")));
        return string.Join(" · ", parts);
    }

    public IndexedFile? FileById(long id) => _byId.GetValueOrDefault(id);

    public IndexedFile? FileByDisplay(string display) => _byDisplay.GetValueOrDefault(display.Replace('\\', '/'));

    /// <summary>Текст файла (кэш процесса; файл уже проверен в этом вызове). null — файл пропал или стал нечитаемым.</summary>
    public SourceFile? Text(IndexedFile f) => CodeIndex.TryLoad(f.FullPath, _roots, _maxBytes, _redact, out _);

    /// <summary>Текст файла с замаскированными секретами — независимо от настройки показа (для эмбеддингов и реранкера).</summary>
    public SourceFile? RedactedText(IndexedFile f) => CodeIndex.TryLoad(f.FullPath, _roots, _maxBytes, redact: true, out _);

    // ───────────────────────── символы ─────────────────────────

    private const string SymbolColumns = "s.id, s.file_id, s.name, s.kind, s.container, s.line, s.end_line, s.signature";

    private IndexedSymbol? ReadSymbol(SqliteDataReader r)
    {
        var file = FileById(r.GetInt64(1));
        if (file is null) return null;
        var s = new CodeSymbol(r.GetString(2), r.GetString(3), r.GetInt32(5), r.GetInt32(6), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(7));
        return new IndexedSymbol(r.GetInt64(0), file, s);
    }

    private List<IndexedSymbol> QuerySymbols(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _session.Command(sql);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var list = new List<IndexedSymbol>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (ReadSymbol(r) is { } s)
                list.Add(s);
        return list;
    }

    private static List<IndexedSymbol> Ordered(IEnumerable<IndexedSymbol> list) =>
        list.DistinctBy(s => s.Id).OrderBy(s => s.File.Order).ThenBy(s => s.Id).ToList();

    /// <summary>Объявления файла в порядке разбора.</summary>
    public IReadOnlyList<IndexedSymbol> SymbolsOf(IndexedFile f)
    {
        if (_symbols.TryGetValue(f.Id, out var list)) return list;
        list = QuerySymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.file_id = $f ORDER BY s.id", ("$f", f.Id));
        _symbols[f.Id] = list;
        foreach (var s in list) _symbolById[s.Id] = s;
        return list;
    }

    public List<CodeSymbol> CodeSymbolsOf(IndexedFile f) => SymbolsOf(f).Select(s => s.Symbol).ToList();

    /// <summary>Символы с точным именем (ignoreCase — без учёта регистра) в файлах области.</summary>
    public List<IndexedSymbol> SymbolsNamed(string name, bool ignoreCase = false) =>
        Ordered(QuerySymbols(ignoreCase
            ? $"SELECT {SymbolColumns} FROM symbols s WHERE s.name = $n COLLATE NOCASE"
            : $"SELECT {SymbolColumns} FROM symbols s WHERE s.name = $n", ("$n", name)));

    /// <summary>Символы, в имени или «Контейнер.Имени» которых есть подстрока (LIKE без учёта регистра ASCII).</summary>
    public List<IndexedSymbol> SymbolsLike(string fragment)
    {
        var pattern = "%" + EscapeLike(fragment) + "%";
        return Ordered(QuerySymbols(
            $"SELECT {SymbolColumns} FROM symbols s WHERE s.name LIKE $p ESCAPE '\\' OR (s.container || '.' || s.name) LIKE $p ESCAPE '\\'", ("$p", pattern)));
    }

    /// <summary>Символы, имя которых содержит часть идентификатора part (целиком или, при prefix, по началу части).</summary>
    public List<IndexedSymbol> SymbolsByPart(string part, bool prefix) =>
        Ordered(prefix
            ? QuerySymbols($"SELECT {SymbolColumns} FROM term_parts p JOIN terms t ON t.id = p.term_id JOIN symbols s ON s.name = t.text " +
                           "WHERE p.part >= $q AND p.part < $hi", ("$q", part), ("$hi", IndexTokens.PrefixEnd(part)))
            : QuerySymbols($"SELECT {SymbolColumns} FROM term_parts p JOIN terms t ON t.id = p.term_id JOIN symbols s ON s.name = t.text " +
                           "WHERE p.part = $q", ("$q", part)));

    /// <summary>Символ по id (null — файл вне области этого вызова).</summary>
    public IndexedSymbol? SymbolById(long id)
    {
        if (_symbolById.TryGetValue(id, out var s)) return s;
        s = QuerySymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.id = $id", ("$id", id)).FirstOrDefault();
        _symbolById[id] = s;
        return s;
    }

    // ───────────────────────── обратный индекс ─────────────────────────

    private List<Posting> QueryPostings(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _session.Command(sql);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var list = new List<Posting>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var file = FileById(r.GetInt64(0));
            if (file is null) continue;
            var (lines, capped) = TermAcc.Parse(r.GetString(3));
            list.Add(new Posting(file, r.GetString(1), r.GetInt32(2), lines, capped, r.GetInt64(4) != 0));
        }
        list.Sort((a, b) => a.File.Order.CompareTo(b.File.Order));
        return list;
    }

    /// <summary>Файлы, где встречается идентификатор ровно с таким написанием.</summary>
    public List<Posting> Postings(string exactTerm) =>
        QueryPostings("SELECT r.file_id, t.text, r.tf, r.lines, 1 FROM terms t JOIN refs r ON r.term_id = t.id WHERE t.text = $t", ("$t", exactTerm));

    /// <summary>
    /// Вхождения идентификаторов, у которых есть часть part (в нижнем регистре): целиком («orderservice», «order») или, при prefix,
    /// по началу части («retry» → retrying). Exact — часть совпала целиком.
    /// </summary>
    public List<Posting> PartPostings(string part, bool prefix) =>
        prefix
            ? QueryPostings("SELECT r.file_id, t.text, r.tf, r.lines, MAX(p.part = $q) FROM term_parts p JOIN terms t ON t.id = p.term_id " +
                            "JOIN refs r ON r.term_id = p.term_id WHERE p.part >= $q AND p.part < $hi GROUP BY p.term_id, r.file_id",
                ("$q", part), ("$hi", IndexTokens.PrefixEnd(part)))
            : QueryPostings("SELECT r.file_id, t.text, r.tf, r.lines, 1 FROM term_parts p JOIN terms t ON t.id = p.term_id " +
                            "JOIN refs r ON r.term_id = p.term_id WHERE p.part = $q", ("$q", part));

    /// <summary>
    /// Файлы, которые могут содержать строку q (только символы идентификатора, см. <see cref="IndexTokens.IsIndexable"/>):
    /// wholeWord — есть идентификатор, равный q (с учётом регистра или без), иначе — идентификатор, содержащий q.
    /// Плюс файлы со слишком длинными словами (их нет в словаре). Надмножество: совпадения проверяет вызывающий по тексту.
    /// Для поиска регулярным выражением без учёта регистра — только если <see cref="IndexTokens.CanPrune"/>.
    /// </summary>
    public HashSet<long> CandidateFiles(string q, bool wholeWord, bool caseSensitive)
    {
        var ids = new HashSet<long>();
        if (wholeWord && caseSensitive)
        {
            foreach (var p in Postings(q)) ids.Add(p.File.Id);
        }
        else if (wholeWord)
        {
            foreach (var p in PartPostings(q.ToLowerInvariant(), prefix: false))
                if (string.Equals(p.Term, q, StringComparison.OrdinalIgnoreCase))
                    ids.Add(p.File.Id);
        }
        else
        {
            using var cmd = _session.Command("SELECT DISTINCT r.file_id FROM terms t JOIN refs r ON r.term_id = t.id WHERE t.text LIKE $p ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$p", "%" + EscapeLike(q) + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        foreach (var f in _files.Where(f => f.LongTokens)) ids.Add(f.Id);
        return ids;
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Текстовые ссылки на имя (как <see cref="SymbolsTool.FindReferences"/>: слово целиком, не в комментарии и не в строке, без строк
    /// объявлений), но кандидаты — из обратного индекса: читаются только файлы и строки, где идентификатор встречается.
    /// </summary>
    public List<CodeRef> References(string name, int max, bool includeDefinitions = false, Func<IndexedFile, bool>? where = null)
    {
        var (n, _) = SymbolsTool.SplitName(name);
        var refs = new List<CodeRef>();
        if (n.Length == 0) return refs;
        var regex = CodeIndex.WordRegex(n);
        IEnumerable<(IndexedFile File, List<int>? Lines)> candidates = IndexTokens.IsIndexable(n)
            ? Postings(n).Select(p => (p.File, p.Capped ? null : p.Lines))
            : _files.Select(f => (f, (List<int>?)null));
        foreach (var (file, lines) in candidates)
        {
            if (where is not null && !where(file)) continue;
            var text = Text(file);
            if (text is null) continue;
            var symbols = CodeSymbolsOf(file);
            foreach (var i in lines ?? Enumerable.Range(1, text.Lines.Length))
            {
                if (i < 1 || i > text.Lines.Length) continue;
                var line = text.Lines[i - 1];
                if (line.Length > 2000 || !line.Contains(n, StringComparison.Ordinal)) continue;
                var m = regex.Match(line);
                if (!m.Success || CodeIndex.IsCommentLine(line) || CodeIndex.InStringOrComment(line, m.Index)) continue;
                if (!includeDefinitions && symbols.Any(s => s.Line == i && s.Name == n)) continue;
                refs.Add(new CodeRef(text, i, Symbols.Enclosing(symbols, i), line.Trim()));
                if (refs.Count >= max) return refs;
            }
        }
        return refs;
    }

    // ───────────────────────── граф вызовов ─────────────────────────

    /// <summary>Места вызова имени (callee) во всех функциях области, по файлам и строкам.</summary>
    public List<CallRow> CallSitesOf(string callee)
    {
        using var cmd = _session.Command("SELECT caller_id, chain, line FROM calls WHERE callee = $n ORDER BY file_id, line");
        cmd.Parameters.AddWithValue("$n", callee);
        var raw = new List<(long Caller, string? Chain, int Line)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                raw.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2)));
        var list = new List<CallRow>();
        foreach (var (caller, chain, line) in raw)
            if (SymbolById(caller) is { } s)
                list.Add(new CallRow(s, callee, chain, line));
        return list.OrderBy(c => c.Caller.File.Order).ThenBy(c => c.Line).ToList();
    }

    /// <summary>Вызовы из тела символа в порядке строк (повторы имя+получатель уже схлопнуты при индексации).</summary>
    public List<CallRow> CallsFrom(IndexedSymbol s)
    {
        using var cmd = _session.Command("SELECT callee, chain, line FROM calls WHERE caller_id = $id ORDER BY line, rowid");
        cmd.Parameters.AddWithValue("$id", s.Id);
        var list = new List<CallRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CallRow(s, r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2)));
        return list;
    }

    public void Dispose() => _session.Dispose();
}
