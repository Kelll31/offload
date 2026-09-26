using System.Security.Cryptography;
using System.Text;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Эвристика «сэкономленных облачных токенов» (подвал ответа и usage.jsonl). Консервативная: лучше недооценить.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Материал</b> — то, что облачной модели пришлось бы прочитать самой:
/// у инструментов с моделью — прочитанный сервером материал (<see cref="ToolStats.TokensRead"/>);
/// у детерминированных (search_code, symbols, project_map, verify, diagnostics, code_scan, impact, git_history…) —
/// просмотренный сервером объём (<see cref="ToolStats.ScannedTokens"/>: файлы индекса, вывод команд), но не больше
/// <see cref="ScanCapFactor"/>× ответа: без Offload IDE искала бы через grep и читала бы не всё подряд. Берётся большее из двух.</item>
/// <item><b>Написанное</b> локально (<see cref="ToolStats.TokensWritten"/>: новые файлы, вставленные/изменённые строки,
/// добавленные строки diff агента) ×5 — выходные токены облака примерно в 5 раз дороже входных.</item>
/// <item>Итог = написанное×5 + материал − ответ, не меньше нуля; при известной поправке токенизатора
/// (<see cref="TokenCounter"/>) — умножается на неё.</item>
/// </list>
/// </remarks>
internal static class Savings
{
    /// <summary>Потолок учёта просмотренного детерминированным инструментом: во сколько раз больше ответа.</summary>
    public const int ScanCapFactor = 20;

    /// <summary>Во сколько раз выходной токен облака дороже входного.</summary>
    public const int WriteWeight = 5;

    public static long Estimate(ToolStats s, string resultText, double? ratio)
    {
        var response = (long)Tokens.Estimate(resultText);
        var scanned = Math.Min(s.ScannedTokens, ScanCapFactor * Math.Max(1, response));
        var material = Math.Max(s.TokensRead, scanned);
        var raw = s.TokensWritten * WriteWeight + material - response;
        return Math.Max(0, TokenCounter.Scale(raw, ratio));
    }

    /// <summary>Токены вставленных и изменённых строк (after относительно before), с переводами строк.</summary>
    public static long InsertedTokens(string before, string after)
    {
        if (before == after) return 0;
        var a = TextCodec.SplitLines(before);
        var b = TextCodec.SplitLines(after);
        long tokens = 0;
        foreach (var e in LineDiff.Compute(a, b))
        {
            if (e.Op == DiffOp.Insert) tokens += Tokens.Estimate(b[e.NewIndex]) + 1;
        }
        return tokens;
    }

    /// <summary>
    /// Токены добавленных строк («+», кроме заголовков «+++») текстового unified diff. Если diff обрезан, результат
    /// экстраполируется на известное из numstat число добавленных строк totalAdded.
    /// </summary>
    public static long DiffAddedTokens(string diff, long totalAdded)
    {
        long tokens = 0;
        long lines = 0;
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.Length == 0 || raw[0] != '+' || raw.StartsWith("+++ ", StringComparison.Ordinal)) continue;
            var line = raw.EndsWith('\r') ? raw[1..^1] : raw[1..];
            tokens += Tokens.Estimate(line) + 1;
            lines++;
        }
        if (lines > 0 && totalAdded > lines) tokens = (long)Math.Round(tokens * (totalAdded / (double)lines));
        return tokens;
    }

    /// <summary>Идентификатор рабочей папки для статистики: имя папки + короткий хэш полного пути (сам путь не пишется).</summary>
    public static string? WorkspaceId(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var norm = root.Trim().TrimEnd('\\', '/').ToLowerInvariant();
        var name = Path.GetFileName(root.Trim().TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = norm.Length > 0 ? norm.TrimEnd(':') : "root";
        if (name.Length > 40) name = name[..40];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..8].ToLowerInvariant();
        return name + "#" + hash;
    }
}
