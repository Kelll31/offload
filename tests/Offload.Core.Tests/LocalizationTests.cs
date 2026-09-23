using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core.Localization;

namespace Offload.Core.Tests;

[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection;

/// <summary>Переключатель языка: русский по умолчанию, английский из словаря, запасной вариант — исходный текст.</summary>
[Collection("Language")]
public sealed class LocalizationTests
{
    [Fact]
    public void Russian_ReturnsKey()
    {
        L.Initialize(L.Russian);
        Assert.Equal("Сохранить что-то несуществующее", L.T("Сохранить что-то несуществующее"));
        Assert.Equal("2 строки", L.Plural(2, "строка", "строки", "строк"));
        Assert.Equal("11 строк", L.Plural(11, "строка", "строки", "строк"));
        Assert.Equal("21 строка", L.Plural(21, "строка", "строки", "строк"));
    }

    [Fact]
    public void English_TranslatesAndFallsBack()
    {
        try
        {
            L.Initialize(L.English);
            Assert.True(L.IsEnglish);
            Assert.Equal("Settings", L.T("Настройки"));
            Assert.Equal("Нет такого ключа", L.T("Нет такого ключа"));
            Assert.Equal("1 line", L.Plural(1, "строка", "строки", "строк"));
            Assert.Equal("1,234 lines", L.Plural(1234, "строка", "строки", "строк"));
            Assert.Equal("lines", L.PluralWord(5, "строка", "строки", "строк"));
            // Нет перевода формата — русский формат с подстановкой.
            Assert.Equal("Нет ключа 5", L.F("Нет ключа {0}", 5));
        }
        finally
        {
            L.Initialize(L.Russian);
        }
    }

    [Fact]
    public void Resolve_KnownAndSystem()
    {
        Assert.Equal(L.English, L.Resolve("en"));
        Assert.Equal(L.Russian, L.Resolve(" RU "));
        Assert.Contains(L.Resolve(L.System), new[] { L.Russian, L.English });
        Assert.Contains(L.Resolve(null), new[] { L.Russian, L.English });
    }

    [Fact]
    public void EmbeddedDictionary_MatchesFilesOnDisk()
    {
        var disk = SourceScan.LoadDictionary(out _);
        Assert.NotEmpty(disk);
        Assert.Equal(disk.Count, L.EnglishKeys.Count);
    }
}

/// <summary>
/// Полнота перевода по исходникам: каждый текст в L.T / L.F / L.Plural / Ui.Plural есть в Localization/en/*.json,
/// а кириллических строк вне перевода нет (кроме журналов Log.* и строк с пометкой <c>// l10n-ignore</c>).
/// Пометка <c>// l10n-key</c> — строки на этой строке кода являются ключами перевода (например, названия схем).
/// </summary>
public sealed class LocalizationCoverageTests
{
    private static readonly string[] TranslateCalls = ["L.T", "L.F"];
    private static readonly string[] PluralCalls = ["L.Plural", "L.PluralWord", "Ui.Plural"];

    [Fact]
    public void Dictionary_IsValid()
    {
        var problems = new List<string>();
        var dict = SourceScan.LoadDictionary(out var fileProblems);
        problems.AddRange(fileProblems);
        foreach (var (ru, en) in dict)
        {
            if (string.IsNullOrWhiteSpace(en)) problems.Add($"пустой перевод: «{ru}»");
            if (ru.Count(c => c == '|') == 2 && !en.Contains('|')) problems.Add($"множественное число без «|»: «{ru}» → «{en}»");
            var ruHoles = SourceScan.Placeholders(ru);
            var enHoles = SourceScan.Placeholders(en);
            if (!ruHoles.SetEquals(enHoles)) problems.Add($"подстановки не совпадают: «{ru}» → «{en}»");
            if (SourceScan.HasCyrillic(en)) problems.Add($"кириллица в переводе: «{ru}» → «{en}»");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(300)));
    }

    [Fact]
    public void AllUiTexts_AreTranslated()
    {
        var dict = SourceScan.LoadDictionary(out _);
        var problems = new List<string>();
        // OFFLOAD_L10N_FILTER=часть пути — проверить только свои файлы (удобно при переводе по частям).
        var filter = Environment.GetEnvironmentVariable("OFFLOAD_L10N_FILTER");
        foreach (var file in SourceScan.SourceFiles())
        {
            if (!string.IsNullOrEmpty(filter) && !filter.Split(';').Any(f => file.Replace('\\', '/').Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
            problems.AddRange(SourceScan.CheckFile(file, dict, TranslateCalls, PluralCalls));
        }
        Assert.True(problems.Count == 0,
            $"Непереведённых мест: {problems.Count}{Environment.NewLine}" + string.Join(Environment.NewLine, problems.Take(1500)));
    }
}

/// <summary>Минимальный лексер C#: строки (обычные, @, $, raw), символы, комментарии — и контекст вызова вокруг строки.</summary>
internal static class SourceScan
{
    private sealed record Lit(int Start, int End, string Value, bool Interpolated);

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Offload.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public static IEnumerable<string> SourceFiles()
    {
        var src = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(src, f).Replace('\\', '/');
                // MCP-сервер отвечает модели по-английски и не переводится.
                return !rel.StartsWith("Offload.Mcp/", StringComparison.Ordinal)
                    && !rel.Contains("/obj/", StringComparison.Ordinal) && !rel.Contains("/bin/", StringComparison.Ordinal);
            })
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    public static Dictionary<string, string> LoadDictionary(out List<string> problems)
    {
        problems = [];
        var dir = Path.Combine(RepoRoot(), "src", "Offload.Core", "Localization", "en");
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var origin = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return dict;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            Dictionary<string, string>? part;
            try
            {
                part = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file),
                    new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                problems.Add($"{Path.GetFileName(file)}: неверный JSON: {ex.Message}");
                continue;
            }
            foreach (var (k, v) in part ?? [])
            {
                if (dict.TryGetValue(k, out var prev) && prev != v)
                    problems.Add($"«{k}»: разные переводы в {origin[k]} и {Path.GetFileName(file)}");
                dict[k] = v;
                origin[k] = Path.GetFileName(file);
            }
        }
        return dict;
    }

    public static bool HasCyrillic(string s) => s.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё');

    public static HashSet<string> Placeholders(string s) =>
        Regex.Matches(s, @"(?<!\{)\{(\d+)").Select(m => m.Groups[1].Value).ToHashSet();

    public static IEnumerable<string> CheckFile(string path, Dictionary<string, string> dict, string[] translateCalls, string[] pluralCalls)
    {
        var src = File.ReadAllText(path);
        var (lits, masked) = Lex(src);
        var rel = Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < src.Length; i++)
            if (src[i] == '\n') lineStarts.Add(i + 1);
        int LineOf(int pos)
        {
            var idx = lineStarts.BinarySearch(pos);
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        }
        string LineText(int line)
        {
            var start = lineStarts[line - 1];
            var end = line < lineStarts.Count ? lineStarts[line] : src.Length;
            return src[start..end];
        }

        var plurals = new Dictionary<int, string?[]>();
        foreach (var lit in lits)
        {
            var line = LineOf(lit.Start);
            var lineText = LineText(line);
            var where = $"{rel}:{line}";
            // Литерал — сам аргумент вызова (а не часть выражения вроде `x ?? ""`).
            var call = StartsArgument(masked, lit.Start) ? Enclosing(masked, lit.Start) : null;
            if (call is { } c && translateCalls.Contains(c.Name) && c.Arg == 0)
            {
                if (lit.Interpolated) yield return $"{where}: интерполяция внутри {c.Name} — нужен L.F(\"… {{0}}\", …): {Short(lit.Value)}";
                else if (!EndsArgument(masked, lit.End)) yield return $"{where}: склейка строк внутри {c.Name} — нужна одна строка: {Short(lit.Value)}";
                else if (!dict.ContainsKey(lit.Value)) yield return $"{where}: нет перевода: {Json(lit.Value)}";
                continue;
            }
            if (call is { } p && pluralCalls.Contains(p.Name) && p.Arg is >= 1 and <= 3)
            {
                if (!plurals.TryGetValue(p.Open, out var forms)) plurals[p.Open] = forms = new string?[4];
                forms[0] = where;
                forms[p.Arg] = lit.Value;
                continue;
            }
            if (!HasCyrillic(lit.Value) || lineText.Contains("l10n-ignore", StringComparison.Ordinal)) continue;
            if (lineText.Contains("l10n-key", StringComparison.Ordinal))
            {
                if (!dict.ContainsKey(lit.Value)) yield return $"{where}: нет перевода: {Json(lit.Value)}";
                continue;
            }
            if (InsideLog(masked, lit.Start)) continue;
            yield return $"{where}: строка без перевода (L.T / L.F / // l10n-ignore): {Short(lit.Value)}";
        }
        foreach (var forms in plurals.Values)
        {
            if (forms[1] is null || forms[2] is null || forms[3] is null) continue;
            var key = $"{forms[1]}|{forms[2]}|{forms[3]}";
            if (!dict.ContainsKey(key)) yield return $"{forms[0]}: нет перевода множественного числа: {Json(key)} (значение «one|many»)";
        }
    }

    private static string Short(string s) => s.Length > 90 ? s[..90] + "…" : s;

    private static string Json(string s) => JsonSerializer.Serialize(s, new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static bool StartsArgument(char[] masked, int start)
    {
        for (var i = start - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(masked[i])) continue;
            return masked[i] is '(' or ',';
        }
        return false;
    }

    private static bool EndsArgument(char[] masked, int end)
    {
        for (var i = end; i < masked.Length; i++)
        {
            if (char.IsWhiteSpace(masked[i])) continue;
            return masked[i] is ')' or ',';
        }
        return false;
    }

    private static bool InsideLog(char[] masked, int pos)
    {
        var at = pos;
        for (var guard = 0; guard < 20; guard++)
        {
            if (Enclosing(masked, at) is not { } c) return false;
            if (c.Name.StartsWith("Log.", StringComparison.Ordinal) || c.Name.Contains(".Log.", StringComparison.Ordinal)) return true;
            at = c.Open;
        }
        return false;
    }

    private readonly record struct Call(int Open, string Name, int Arg);

    /// <summary>Ближайший незакрытый вызов «Имя(» слева от позиции (в пределах одного выражения) и номер аргумента.</summary>
    private static Call? Enclosing(char[] masked, int pos)
    {
        int depth = 0, commas = 0;
        for (var i = pos - 1; i >= 0; i--)
        {
            var ch = masked[i];
            switch (ch)
            {
                case ')' or ']':
                    depth++;
                    break;
                case '(' or '[' when depth > 0:
                    depth--;
                    break;
                case '[':
                    // Коллекция/индексатор: продолжаем искать вызов снаружи, номер аргумента теряется.
                    commas = -100;
                    break;
                case '(':
                    var name = NameBefore(masked, i);
                    return new Call(i, name, commas < 0 ? -1 : commas);
                case ',' when depth == 0:
                    commas++;
                    break;
                case ';' or '{' or '}' when depth == 0:
                    return null;
            }
        }
        return null;
    }

    private static string NameBefore(char[] masked, int open)
    {
        var i = open - 1;
        while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
        // Обобщённый вызов Foo<T>(...) — пропустить аргументы типа.
        if (i >= 0 && masked[i] == '>')
        {
            var d = 0;
            for (; i >= 0; i--)
            {
                if (masked[i] == '>') d++;
                else if (masked[i] == '<' && --d == 0) { i--; break; }
            }
        }
        var end = i + 1;
        while (i >= 0 && (char.IsLetterOrDigit(masked[i]) || masked[i] is '_' or '.')) i--;
        return new string(masked, i + 1, end - i - 1);
    }

    // ---- Лексер ----

    private static (List<Lit> Lits, char[] Masked) Lex(string s)
    {
        var lits = new List<Lit>();
        var masked = s.ToCharArray();
        var i = 0;
        LexCode(s, ref i, masked, lits, stopAtBrace: false);
        return (lits, masked);
    }

    private static void Mask(char[] masked, int from, int to)
    {
        for (var k = from; k < to && k < masked.Length; k++)
            if (masked[k] != '\n') masked[k] = ' ';
    }

    private static void LexCode(string s, ref int i, char[] masked, List<Lit> lits, bool stopAtBrace)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                var start = i;
                while (i < s.Length && s[i] != '\n') i++;
                Mask(masked, start, i);
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var start = i;
                var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? s.Length : close + 2;
                Mask(masked, start, i);
                continue;
            }
            if (c == '\'')
            {
                var start = i;
                i++;
                if (i < s.Length && s[i] == '\\') i += 2;
                while (i < s.Length && s[i] != '\'' && s[i] != '\n') i++;
                i++;
                Mask(masked, start, i);
                continue;
            }
            if (c is '"' or '$' or '@' && TryString(s, ref i, masked, lits)) continue;
            if (stopAtBrace)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    if (depth == 0) return;
                    depth--;
                }
            }
            i++;
        }
    }

    private static bool TryString(string s, ref int i, char[] masked, List<Lit> lits)
    {
        var start = i;
        var j = i;
        var dollars = 0;
        var verbatim = false;
        while (j < s.Length && (s[j] == '$' || s[j] == '@'))
        {
            if (s[j] == '$') dollars++;
            else verbatim = true;
            j++;
        }
        if (j >= s.Length || s[j] != '"') return false;
        if (j > start && (dollars == 0 && !verbatim)) return false;
        // Идентификатор перед @ / $ (например, a@"..." не бывает) — не проверяем.
        var quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"') quotes++;
        if (quotes >= 3)
        {
            // Raw-строка: закрывается тем же числом кавычек; подстановки не разбираем.
            var open = new string('"', quotes);
            var close = s.IndexOf(open, j + quotes, StringComparison.Ordinal);
            var end = close < 0 ? s.Length : close + quotes;
            lits.Add(new Lit(start, end, close < 0 ? s[(j + quotes)..] : s[(j + quotes)..close], dollars > 0));
            Mask(masked, start, end);
            i = end;
            return true;
        }
        j++; // открывающая кавычка
        var value = new StringBuilder();
        var interpolated = dollars > 0;
        var textStart = start;
        while (j < s.Length)
        {
            var c = s[j];
            if (verbatim && c == '"')
            {
                if (j + 1 < s.Length && s[j + 1] == '"') { value.Append('"'); j += 2; continue; }
                j++;
                break;
            }
            if (!verbatim && c == '"') { j++; break; }
            if (!verbatim && c == '\n') break;
            if (!verbatim && c == '\\' && j + 1 < s.Length)
            {
                var e = s[j + 1];
                j += 2;
                switch (e)
                {
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case '0': value.Append('\0'); break;
                    case 'u' when j + 4 <= s.Length:
                        value.Append((char)Convert.ToInt32(s.Substring(j, 4), 16));
                        j += 4;
                        break;
                    default: value.Append(e); break;
                }
                continue;
            }
            if (interpolated && c == '{')
            {
                if (j + 1 < s.Length && s[j + 1] == '{') { value.Append('{'); j += 2; continue; }
                // Подстановка: текст до неё — маскируем, код внутри разбираем как обычный.
                Mask(masked, textStart, j + 1);
                j++;
                LexCode(s, ref j, masked, lits, stopAtBrace: true);
                value.Append("{…}");
                textStart = j;
                j++;
                continue;
            }
            if (interpolated && c == '}' && j + 1 < s.Length && s[j + 1] == '}') { value.Append('}'); j += 2; continue; }
            value.Append(c);
            j++;
        }
        Mask(masked, textStart, j);
        lits.Add(new Lit(start, j, value.ToString(), interpolated));
        i = j;
        return true;
    }
}
