using System.Security.Cryptography;
using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Index;

/// <summary>Вхождения одного идентификатора в файле: сколько раз и в каких строках (первые <see cref="IndexTokens.MaxStoredLines"/>).</summary>
internal sealed class TermAcc
{
    public int Tf { get; set; }
    public List<int> Lines { get; } = [];

    /// <summary>Строк больше, чем сохранено: при проверке нужен весь файл.</summary>
    public bool Capped { get; set; }

    /// <summary>Хранимая форма: «3,17,42», с «,+» в конце, если список обрезан.</summary>
    public string Serialize() => string.Join(',', Lines) + (Capped ? ",+" : "");

    /// <summary>Разбор хранимой формы: номера строк и признак обрезки.</summary>
    public static (List<int> Lines, bool Capped) Parse(string stored)
    {
        var list = new List<int>();
        var capped = false;
        foreach (var part in stored.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "+") capped = true;
            else if (int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) list.Add(n);
        }
        return (list, capped);
    }
}

/// <summary>
/// Всё, что индекс хранит о файле, — извлечено из замаскированного текста: sha, размеры, символы, идентификаторы, вызовы,
/// признаки (сгенерированный, точка входа). Сам текст в базу не пишется.
/// </summary>
internal sealed class FileData
{
    public bool Ok { get; private init; }
    public string Sha { get; private init; } = "";
    public int Redacted { get; private init; }
    public CodeLang Lang { get; private init; }
    public int Lines { get; private init; }
    public long Chars { get; private init; }
    public int Tokens { get; private init; }
    public bool Truncated { get; private init; }
    public bool Generated { get; private init; }
    public bool LongTokens { get; private init; }
    public string? Entry { get; private init; }
    public IReadOnlyList<CodeSymbol> Symbols { get; private init; } = [];
    public Dictionary<string, TermAcc> Terms { get; private init; } = [];
    public List<CallSiteData> Calls { get; private init; } = [];

    /// <summary>
    /// Прочитать (через кэш текста CodeIndex, с маскированием секретов — независимо от настройки показа) и разобрать файл.
    /// Двоичный — запись с Ok = false (чтобы не перечитывать); временно нечитаемый — null.
    /// </summary>
    public static FileData? Extract(string full, IReadOnlyList<string> roots, int maxBytes)
    {
        var file = CodeIndex.TryLoad(full, roots, maxBytes, redact: true, out var binary);
        if (file is null) return binary ? new FileData { Ok = false, Lang = Infrastructure.Symbols.LangOf(full) } : null;
        var lines = file.Lines;
        long chars = lines.Length;
        foreach (var l in lines) chars += l.Length;

        var terms = new Dictionary<string, TermAcc>(StringComparer.Ordinal);
        var lookup = terms.GetAlternateLookup<ReadOnlySpan<char>>();
        var tokens = 0;
        var longTokens = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > IndexTokens.MaxLineChars)
            {
                continue;
            }
            var pos = 0;
            while (IndexTokens.NextToken(line, ref pos, out var start, out var len))
            {
                tokens++;
                if (len < 0)
                {
                    longTokens = true;
                    continue;
                }
                var span = line.AsSpan(start, len);
                if (!lookup.TryGetValue(span, out var acc))
                {
                    acc = new TermAcc();
                    lookup[span] = acc;
                }
                acc.Tf++;
                if (acc.Lines.Count > 0 && acc.Lines[^1] == i + 1) continue;
                if (acc.Lines.Count < IndexTokens.MaxStoredLines) acc.Lines.Add(i + 1);
                else acc.Capped = true;
            }
        }

        var symbols = file.Symbols;
        return new FileData
        {
            Ok = true,
            Sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))),
            Redacted = file.RedactedCount,
            Lang = file.Lang,
            Lines = lines.Length,
            Chars = chars,
            Tokens = tokens,
            Truncated = file.Truncated,
            Generated = lines.Take(12).Any(l => CodeRules.GeneratedMarker().IsMatch(l)),
            LongTokens = longTokens,
            Entry = ProjectMap.EntryHint(file.Lang, lines),
            Symbols = symbols,
            Terms = terms,
            Calls = IndexTokens.ExtractCalls(lines, symbols),
        };
    }
}
