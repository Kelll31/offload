using System.Text;
using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

internal sealed record LogLine(long Number, string Text, int Repeats = 1)
{
    public int Repeats { get; set; } = Repeats;
}

/// <summary>Результат предварительной фильтрации лога: начало, окна вокруг ошибок, конец.</summary>
internal sealed class LogDigest
{
    public List<LogLine> Head { get; } = [];
    public List<LogLine> Windows { get; } = [];
    public List<LogLine> Tail { get; } = [];
    public long TotalLines { get; set; }
    /// <summary>Номер последней строки (с учётом смещения при tail_lines).</summary>
    public long LastLineNumber { get; set; }
    public int ErrorLines { get; set; }
    public int OmittedErrorLines { get; set; }
    public int CollapsedRepeats { get; set; }
}

/// <summary>
/// Сжатие лога на стороне сервера: первые строки, последние строки и окна вокруг строк с ошибками;
/// повторы схлопываются («[×57]»), ANSI-цвета и «\r»-прогресс удаляются.
/// </summary>
internal static partial class LogPrefilter
{
    [GeneratedRegex(@"error|exception|fail|panic|traceback|fatal|assert|✗|×|\bCS\d{4}\b|\bTS\d{4}\b|\bE\d{4}\b|warning MSB|npm ERR|\bFAILED\b|\bFAIL\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex ErrorPattern();

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"^\s*\[?\d{2,4}[-/.:]\d{1,2}[-/.:]\d{1,4}(?:[T ]\d{1,2}:\d{2}(?::\d{2})?(?:[.,]\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?\]?\s*")]
    private static partial Regex LeadingTimestamp();

    public static string CleanLine(string raw, int maxChars = 500)
    {
        var s = raw;
        var cr = s.LastIndexOf('\r');
        if (cr >= 0 && cr < s.Length - 1) s = s[(cr + 1)..];
        s = s.TrimEnd('\r', ' ', '\t');
        if (s.IndexOf('\x1B') >= 0) s = AnsiEscape().Replace(s, "");
        if (s.Length > maxChars) s = s[..maxChars] + "…";
        return s;
    }

    public static LogDigest Filter(IEnumerable<string> lines, int headLines = 40, int tailLines = 80, int context = 3,
        int maxWindowLines = 600, long firstLineNumber = 1)
    {
        var d = new LogDigest();
        var before = new Queue<LogLine>();
        var tail = new Queue<LogLine>();
        var seenCounts = new Dictionary<string, LogLine>(StringComparer.Ordinal);
        var after = 0;
        LogLine? lastKept = null;
        var n = firstLineNumber - 1;

        foreach (var raw in lines)
        {
            n++;
            var text = CleanLine(raw);
            var entry = new LogLine(n, text);
            d.TotalLines++;

            var isError = text.Length > 0 && ErrorPattern().IsMatch(text);
            if (isError) d.ErrorLines++;

            if (d.Head.Count < headLines)
            {
                d.Head.Add(entry);
                lastKept = entry;
            }
            else if (isError || after > 0)
            {
                if (lastKept is not null && lastKept.Text == text && text.Length > 0)
                {
                    // Подряд идущие одинаковые строки → «[×N]».
                    lastKept.Repeats++;
                    d.CollapsedRepeats++;
                    if (!isError) after--;
                }
                else
                {
                    var added = false;
                    var key = isError ? LeadingTimestamp().Replace(text, "") : "";
                    if (isError && key.Length > 0 && seenCounts.TryGetValue(key, out var first))
                    {
                        // Та же ошибка уже показана — только счётчик у первого вхождения.
                        first.Repeats++;
                        d.CollapsedRepeats++;
                    }
                    else if (d.Windows.Count < maxWindowLines)
                    {
                        if (isError)
                        {
                            var lastWin = d.Windows.Count > 0 ? d.Windows[^1].Number : 0;
                            foreach (var b in before)
                            {
                                if (b.Number > lastWin && b.Number > HeadEnd(d)) d.Windows.Add(b);
                            }
                            if (key.Length > 0) seenCounts.TryAdd(key, entry);
                        }
                        d.Windows.Add(entry);
                        lastKept = entry;
                        added = true;
                    }
                    else if (isError)
                    {
                        d.OmittedErrorLines++;
                    }
                    if (isError) after = added ? context : 0;
                    else after--;
                }
            }

            before.Enqueue(entry);
            while (before.Count > context) before.Dequeue();
            tail.Enqueue(entry);
            while (tail.Count > tailLines) tail.Dequeue();
        }

        d.LastLineNumber = n;
        var lastShown = Math.Max(HeadEnd(d), d.Windows.Count > 0 ? d.Windows[^1].Number : 0);
        foreach (var t in tail)
        {
            if (t.Number > lastShown) d.Tail.Add(t);
        }
        return d;
    }

    private static long HeadEnd(LogDigest d) => d.Head.Count > 0 ? d.Head[^1].Number : 0;

    /// <summary>
    /// Текст для модели в пределах maxChars: «L12: …»; пропуски — «… (N lines) …».
    /// Бюджет: начало ≤15%, окна ошибок ≤60% (первые — важнее: там корневая причина), конец — остальное.
    /// </summary>
    public static string Render(LogDigest d, int maxChars)
    {
        maxChars = Math.Max(1000, maxChars);
        var headBudget = maxChars * 15 / 100;
        var tailBudget = maxChars * 25 / 100;
        var winBudget = maxChars - headBudget - tailBudget;

        var head = Take(d.Head, headBudget, fromEnd: false);
        var tail = Take(d.Tail, tailBudget + Math.Max(0, headBudget - Size(head)), fromEnd: true);
        var windows = Take(d.Windows, winBudget + Math.Max(0, tailBudget - Size(tail)), fromEnd: false);

        var all = head.Concat(windows).Concat(tail).GroupBy(l => l.Number).Select(g => g.First()).OrderBy(l => l.Number).ToList();
        var sb = new StringBuilder();
        long prev = 0;
        foreach (var l in all)
        {
            if (l.Number > prev + 1 && prev > 0) sb.Append($"… ({l.Number - prev - 1} lines omitted) …\n");
            sb.Append('L').Append(l.Number).Append(": ").Append(l.Text);
            if (l.Repeats > 1) sb.Append($"  [×{l.Repeats}]");
            sb.Append('\n');
            prev = l.Number;
        }
        if (d.LastLineNumber > prev) sb.Append($"… ({d.LastLineNumber - prev} lines omitted) …\n");
        var dropped = d.Windows.Count - windows.Count + d.OmittedErrorLines;
        if (dropped > 0) sb.Append($"[{dropped} more error-context lines omitted to fit]\n");
        return sb.ToString();
    }

    private static int Size(List<LogLine> lines) => lines.Sum(l => l.Text.Length + 12);

    private static List<LogLine> Take(List<LogLine> lines, int budget, bool fromEnd)
    {
        var result = new List<LogLine>();
        var used = 0;
        var seq = fromEnd ? Enumerable.Reverse(lines) : lines;
        foreach (var l in seq)
        {
            var cost = l.Text.Length + 12;
            if (used + cost > budget) break;
            used += cost;
            result.Add(l);
        }
        if (fromEnd) result.Reverse();
        return result;
    }
}
