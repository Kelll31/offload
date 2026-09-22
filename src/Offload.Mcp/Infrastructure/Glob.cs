using System.Text;
using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Glob-шаблоны путей: **, *, ?, {a,b}. Сравнение без учёта регистра (Windows).
/// Регулярные выражения — NonBacktracking: шаблон присылает LLM, катастрофический перебор исключён.
/// </summary>
internal static class Glob
{
    public static bool HasWildcards(string s) =>
        s.IndexOfAny(['*', '?']) >= 0 || (s.Contains('{') && s.Contains('}'));

    /// <summary>
    /// Разделить шаблон на базовую папку (сегменты без подстановок) и остаток.
    /// «src/**/*.cs» → («src», «**/*.cs»); «*.md» → («», «*.md»).
    /// </summary>
    public static (string BaseDir, string Pattern) Split(string pattern)
    {
        var norm = pattern.Replace('\\', '/');
        var segments = norm.Split('/');
        var i = 0;
        while (i < segments.Length - 1 && !HasWildcards(segments[i])) i++;
        if (!HasWildcards(segments[i])) i = segments.Length; // без подстановок
        var baseDir = string.Join('/', segments.Take(i));
        var rest = string.Join('/', segments.Skip(i));
        // «C:» → «C:/» (иначе это путь относительно текущей папки диска)
        if (baseDir.Length == 2 && baseDir[1] == ':') baseDir += "/";
        if (baseDir.Length == 0 && norm.StartsWith('/')) baseDir = "/";
        return (baseDir, rest);
    }

    /// <summary>Шаблон относительного пути (разделитель «/») → регулярное выражение целиком.</summary>
    public static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        Append(sb, pattern.Replace('\\', '/').TrimStart('/'), allowBraces: true);
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static void Append(StringBuilder sb, string p, bool allowBraces)
    {
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*')
            {
                if (i + 1 < p.Length && p[i + 1] == '*')
                {
                    // «**/» — ноль или более папок; «**» в конце — всё что угодно.
                    if (i + 2 < p.Length && p[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 1;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else if (c == '{' && allowBraces)
            {
                var close = p.IndexOf('}', i + 1);
                var inner = close > i ? p[(i + 1)..close] : null;
                if (inner is null || inner.Contains('{'))
                {
                    sb.Append(Regex.Escape("{"));
                    continue;
                }
                sb.Append("(?:");
                var parts = inner.Split(',');
                for (var k = 0; k < parts.Length; k++)
                {
                    if (k > 0) sb.Append('|');
                    Append(sb, parts[k], allowBraces: false);
                }
                sb.Append(')');
                i = close;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
    }

    /// <summary>Сопоставление имени файла с шаблоном из * и ? (без учёта регистра), без регулярных выражений.</summary>
    public static bool MatchName(string name, string pattern)
    {
        int n = 0, p = 0, starP = -1, starN = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starN = n;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                n = ++starN;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
