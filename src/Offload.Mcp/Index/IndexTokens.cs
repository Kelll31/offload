using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Index;

/// <summary>Вызов внутри тела функции: имя вызываемого, цепочка получателя («repo», «a.B», «this», «new», «()», «?») и строка.</summary>
internal readonly record struct CallSiteData(int CallerIndex, string Callee, string? Chain, int Line);

/// <summary>
/// Разбор текста для индекса: идентификаторы (обратный индекс и BM25), их части (camelCase/snake_case) и места вызовов.
/// Всё считается по уже замаскированному тексту (<see cref="SecretRedactor"/>), поэтому значения секретов в индекс не попадают.
/// </summary>
internal static partial class IndexTokens
{
    /// <summary>Строки длиннее не разбираются (минифицированный код, данные) — как в поиске.</summary>
    public const int MaxLineChars = 4000;

    /// <summary>Идентификаторы длиннее не хранятся (base64 и т. п.); файл с ними помечается и всегда проверяется поиском целиком.</summary>
    public const int MaxTokenChars = 200;

    /// <summary>Сколько номеров строк хранить на пару (идентификатор, файл); tf — полное число вхождений.</summary>
    public const int MaxStoredLines = 64;

    public static bool IsIdentChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '$';

    /// <summary>
    /// Запрос можно искать по словарю индекса: только символы идентификатора, длина ≥ 2 и не одни цифры. Любое вхождение такой
    /// строки в текст лежит внутри одного «слова» из символов идентификатора — а все такие слова (длиной ≥ 2, не числа) есть в словаре.
    /// </summary>
    public static bool IsIndexable(string q)
    {
        if (q.Length is < 2 or > MaxTokenChars) return false;
        var nonDigit = false;
        foreach (var c in q)
        {
            if (!IsIdentChar(c)) return false;
            if (c is < '0' or > '9') nonDigit = true;
        }
        return nonDigit;
    }

    /// <summary>
    /// ASCII-буквы, которым Regex с IgnoreCase | CultureInvariant сопоставляет и не-ASCII символы: k/K — знак Кельвина U+212A
    /// (точность списка для текущей версии .NET проверяет тест). Такой символ в тексте разрывает слово словаря.
    /// </summary>
    private static readonly char[] FoldsOutsideAscii = ['k', 'K'];

    /// <summary>
    /// Можно ли отсеять файлы по словарю индекса для поиска строки q (<see cref="IndexView.CandidateFiles"/> — надмножество
    /// совпадений). Без учёта регистра — только если в q нет букв, которые регулярное выражение сопоставит с не-ASCII символами:
    /// «kelvin» совпадает с «Kelvin», а в словаре от этого текста есть лишь «elvin».
    /// </summary>
    public static bool CanPrune(string q, bool caseSensitive) => IsIndexable(q) && (caseSensitive || q.IndexOfAny(FoldsOutsideAscii) < 0);

    /// <summary>
    /// Следующее слово строки начиная с pos: максимальный отрезок из [A-Za-z0-9_$] длиной ≥ 2, в котором есть не только цифры.
    /// Слово длиннее <see cref="MaxTokenChars"/> отдаётся с длиной -1 (признак «слишком длинное»). false — слов больше нет.
    /// </summary>
    public static bool NextToken(string line, ref int pos, out int start, out int length)
    {
        while (pos < line.Length)
        {
            if (!IsIdentChar(line[pos]))
            {
                pos++;
                continue;
            }
            start = pos;
            var nonDigit = false;
            while (pos < line.Length && IsIdentChar(line[pos]))
            {
                if (line[pos] is < '0' or > '9') nonDigit = true;
                pos++;
            }
            length = pos - start;
            if (length < 2 || !nonDigit) continue;
            if (length > MaxTokenChars) length = -1;
            return true;
        }
        start = length = 0;
        return false;
    }

    /// <summary>
    /// Части идентификатора в нижнем регистре: он сам целиком и (если частей больше одной) куски camelCase/PascalCase/snake_case:
    /// «OrderService» → orderservice, order, service; «HTTPServer» → httpserver, http, server; «parse_header» → parse_header, parse, header.
    /// </summary>
    public static List<string> Parts(string term)
    {
        var whole = term.ToLowerInvariant();
        var result = new List<string> { whole };
        var pieces = new List<string>();
        var start = 0;
        for (var i = 0; i <= term.Length; i++)
        {
            var cut = i == term.Length || term[i] is '_' or '$';
            var boundary = !cut && i > start && (
                char.IsUpper(term[i]) && (char.IsLower(term[i - 1]) || char.IsDigit(term[i - 1]))
                || char.IsUpper(term[i]) && char.IsUpper(term[i - 1]) && i + 1 < term.Length && char.IsLower(term[i + 1]));
            if (!cut && !boundary) continue;
            if (i > start) pieces.Add(term[start..i].ToLowerInvariant());
            start = cut ? i + 1 : i;
        }
        if (pieces.Count > 1)
            foreach (var p in pieces)
                if (p.Length >= 2 && !result.Contains(p)) result.Add(p);
        return result;
    }

    /// <summary>Токены запроса для BM25: слова из символов идентификатора в нижнем регистре и их части.</summary>
    public static List<string> QueryTokens(string term)
    {
        var list = new List<string>();
        var pos = 0;
        while (NextToken(term, ref pos, out var start, out var len))
        {
            if (len < 0) continue;
            foreach (var p in Parts(term.Substring(start, len)))
                if (!list.Contains(p)) list.Add(p);
        }
        return list;
    }

    /// <summary>Верхняя граница диапазона для поиска по префиксу: «retry» → «retrz».</summary>
    public static string PrefixEnd(string prefix) => prefix[..^1] + (char)(prefix[^1] + 1);

    // ───────────────────────── вызовы ─────────────────────────

    [GeneratedRegex(@"(?<![\w$])(\w+)\s*(?:<[\w\s,.<>\[\]?]*>)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CallSite();

    /// <summary>
    /// Места вызовов «Имя(», «obj.Имя(», «Type.Имя<T>(», «new Имя(» внутри тел функций/методов (самая внутренняя вызываемая
    /// сущность, содержащая строку). Строки-комментарии, строковые литералы, ключевые слова и само объявление пропускаются;
    /// повтор (вызывающий, имя, цепочка) хранится один раз — с первой строкой.
    /// </summary>
    public static List<CallSiteData> ExtractCalls(string[] lines, IReadOnlyList<CodeSymbol> symbols)
    {
        var result = new List<CallSiteData>();
        if (symbols.Count == 0 || lines.Length == 0) return result;
        var owner = new int[lines.Length + 1];
        Array.Fill(owner, -1);
        // Более поздние (вложенные) объявления перекрывают внешние — получается самая внутренняя вызываемая сущность.
        var order = Enumerable.Range(0, symbols.Count).Where(i => IsCallable(symbols[i])).OrderBy(i => symbols[i].Line).ToList();
        foreach (var k in order)
        {
            var s = symbols[k];
            var end = Math.Min(s.EndLine, lines.Length);
            for (var l = Math.Max(1, s.Line); l <= end; l++) owner[l] = k;
        }
        var seen = new HashSet<(int, string, string?)>();
        for (var l = 1; l <= lines.Length; l++)
        {
            var k = owner[l];
            if (k < 0) continue;
            var text = lines[l - 1];
            if (text.Length > 2000 || text.IndexOf('(') < 0 || CodeIndex.IsCommentLine(text)) continue;
            var s = symbols[k];
            foreach (Match m in CallSite().Matches(text))
            {
                var callee = m.Groups[1].Value;
                if (callee == s.Name && l == s.Line || Symbols.Keywords.Contains(callee) || char.IsDigit(callee[0])
                    || CodeIndex.InStringOrComment(text, m.Index)) continue;
                var chain = ChainOf(text, m.Index);
                if (seen.Add((k, callee, chain))) result.Add(new CallSiteData(k, callee, chain, l));
            }
        }
        return result;
    }

    public static bool IsCallable(CodeSymbol s) => s.Kind is "method" or "function" or "ctor" or "property" or "delegate";

    /// <summary>
    /// Цепочка получателя слева от имени: «a.b.Name(» → «a.b», «this.Name(» → «this», «foo().Name(» → «()», «Name(» → null,
    /// «new Name(» → «new». Сегменты — только идентификаторы; «?.»/«!.» пропускаются; не больше 4 сегментов.
    /// </summary>
    public static string? ChainOf(string text, int nameIndex)
    {
        var i = nameIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0 || text[i] != '.')
        {
            var before = text[..(i + 1)].TrimEnd();
            return before.EndsWith("new", StringComparison.Ordinal) && (before.Length == 3 || !char.IsLetterOrDigit(before[^4]) && before[^4] != '_')
                ? "new" : null;
        }
        var segments = new List<string>();
        while (i >= 0 && text[i] == '.' && segments.Count < 4)
        {
            i--;
            while (i >= 0 && (text[i] == '?' || text[i] == '!' || char.IsWhiteSpace(text[i]))) i--;
            if (i >= 0 && text[i] == ')')
            {
                segments.Add("()");
                break;
            }
            var end = i;
            while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i--;
            if (end <= i)
            {
                segments.Add("?");
                break;
            }
            segments.Add(text[(i + 1)..(end + 1)]);
            while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        }
        segments.Reverse();
        return string.Join('.', segments);
    }

    /// <summary>Непосредственный получатель вызова по цепочке: «a.b» → «b», «()» → «()», null → null.</summary>
    public static string? ImmediateReceiver(string? chain) =>
        chain is null or "new" ? null : chain.LastIndexOf('.') is var dot and >= 0 ? chain[(dot + 1)..] : chain;
}
