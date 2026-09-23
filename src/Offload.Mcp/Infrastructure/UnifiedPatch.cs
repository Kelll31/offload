using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

/// <summary>Одна правка файла из unified diff.</summary>
internal sealed class FilePatch
{
    public string? OldPath { get; set; }
    public string? NewPath { get; set; }
    public List<Hunk> Hunks { get; } = [];
    public bool IsNew => OldPath is null;
    public bool IsDelete => NewPath is null;
    public string Target => NewPath ?? OldPath ?? "";
}

internal sealed class Hunk
{
    /// <summary>Начальная строка в старом файле (с 1); 0 — номера нет («@@ @@») или файл новый.</summary>
    public int OldStart { get; set; }
    public int? OldCount { get; set; }
    public int? NewCount { get; set; }
    public string Header { get; set; } = "";

    /// <summary>Строки: ' ' контекст, '-' удалить, '+' добавить.</summary>
    public List<(char Op, string Text)> Lines { get; } = [];
    public bool NoNewlineAtEndOfNew { get; set; }
    public bool NoNewlineAtEndOfOld { get; set; }
}

/// <summary>
/// Разбор и применение unified diff (git diff, diff -u; допускаются хунки без номеров «@@ @@»). Применение устойчиво к
/// сдвигу номеров строк (поиск ближайшего совпадения) и к различиям пробелов/CRLF; кодировка и перевод строк файла
/// сохраняются вызывающим кодом. Ничего не пишет на диск — только вычисляет новое содержимое.
/// </summary>
internal static partial class UnifiedPatch
{
    [GeneratedRegex(@"^@@\s*-(?<os>\d+)(?:,(?<oc>\d+))?\s+\+(?<ns>\d+)(?:,(?<nc>\d+))?\s*@@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();

    public static List<FilePatch> Parse(string patch)
    {
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var files = new List<FilePatch>();
        FilePatch? cur = null;
        Hunk? hunk = null;
        int oldLeft = 0, newLeft = 0;
        var counted = false;

        bool InCountedHunk() => hunk is not null && counted && (oldLeft > 0 || newLeft > 0);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Счётчики хунка исчерпаны: дальше только «\ No newline…», остальное (подпись «-- » format-patch, мусор) — не хунк.
            if (hunk is not null && counted && oldLeft <= 0 && newLeft <= 0 && !line.StartsWith('\\')) hunk = null;
            if (!InCountedHunk())
            {
                if (line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("Index: ", StringComparison.Ordinal))
                {
                    hunk = null;
                    continue;
                }
                if (line.StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Length && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var oldP = PathOf(line[4..]);
                    var newP = PathOf(lines[i + 1][4..]);
                    cur = new FilePatch { OldPath = oldP == "/dev/null" ? null : oldP, NewPath = newP == "/dev/null" ? null : newP };
                    files.Add(cur);
                    hunk = null;
                    i++;
                    continue;
                }
                if (line.StartsWith("@@", StringComparison.Ordinal) && cur is not null)
                {
                    var m = HunkHeader().Match(line);
                    hunk = new Hunk { Header = line.Trim() };
                    if (m.Success)
                    {
                        hunk.OldStart = int.Parse(m.Groups["os"].Value, System.Globalization.CultureInfo.InvariantCulture);
                        hunk.OldCount = m.Groups["oc"].Success ? int.Parse(m.Groups["oc"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                        hunk.NewCount = m.Groups["nc"].Success ? int.Parse(m.Groups["nc"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                        (oldLeft, newLeft, counted) = (hunk.OldCount.Value, hunk.NewCount.Value, true);
                    }
                    else
                    {
                        counted = false;
                    }
                    cur.Hunks.Add(hunk);
                    continue;
                }
            }
            if (hunk is null) continue;
            if (line.StartsWith('\\'))
            {
                // «\ No newline at end of file» относится к предыдущей строке.
                if (hunk.Lines.Count > 0)
                {
                    var op = hunk.Lines[^1].Op;
                    if (op != '+') hunk.NoNewlineAtEndOfOld = true;
                    if (op != '-') hunk.NoNewlineAtEndOfNew = true;
                }
                continue;
            }
            char c;
            string text;
            if (line.Length == 0)
            {
                // Пустая контекстная строка, у которой редактор срезал пробел.
                if (counted ? oldLeft > 0 && newLeft > 0 : NextIsHunkBody(lines, i + 1)) (c, text) = (' ', "");
                else continue;
            }
            else if (line[0] is ' ' or '-' or '+') (c, text) = (line[0], line[1..]);
            else
            {
                hunk = null;
                continue;
            }
            hunk.Lines.Add((c, text));
            if (counted)
            {
                if (c != '+') oldLeft--;
                if (c != '-') newLeft--;
            }
        }
        return files.Where(f => f.Hunks.Count > 0 || f.IsNew || f.IsDelete).ToList();
    }

    private static bool NextIsHunkBody(string[] lines, int i)
    {
        for (; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0) continue;
            return l[0] is ' ' or '-' or '+' && !l.StartsWith("--- ", StringComparison.Ordinal) && !l.StartsWith("+++ ", StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>Путь из заголовка «--- a/src/x.cs\t2024-…» → «a/src/x.cs» (снятие префиксов — в StripPrefixes).</summary>
    private static string PathOf(string header)
    {
        var p = header.Split('\t')[0].Trim();
        if (p.Length >= 2 && p[0] == '"' && p[^1] == '"') p = Git.Unquote(p);
        return p;
    }

    /// <summary>Снять префиксы a/ и b/, если они есть у обеих сторон (или у единственной стороны нового/удалённого файла).</summary>
    public static void StripPrefixes(List<FilePatch> files)
    {
        foreach (var f in files)
        {
            var oldHas = f.OldPath is null || f.OldPath.StartsWith("a/", StringComparison.Ordinal);
            var newHas = f.NewPath is null || f.NewPath.StartsWith("b/", StringComparison.Ordinal);
            if (!oldHas || !newHas || f.OldPath is null && f.NewPath is null) continue;
            if (f.OldPath is not null) f.OldPath = f.OldPath[2..];
            if (f.NewPath is not null) f.NewPath = f.NewPath[2..];
        }
    }

    /// <summary>Применить хунки к строкам. Возвращает новые строки и признак финального перевода строки или текст ошибки.</summary>
    public static (List<string>? Lines, bool FinalNewline, string? Error) Apply(IReadOnlyList<string> original, bool finalNewline, FilePatch patch)
    {
        var lines = original.ToList();
        var delta = 0;
        var cursor = 0;
        var fin = finalNewline;
        for (var h = 0; h < patch.Hunks.Count; h++)
        {
            var hunk = patch.Hunks[h];
            var oldBlock = hunk.Lines.Where(l => l.Op != '+').Select(l => l.Text).ToList();
            var newBlock = hunk.Lines.Where(l => l.Op != '-').Select(l => l.Text).ToList();
            int at;
            if (oldBlock.Count == 0)
            {
                // «@@ -N,0 +M,K @@» — вставка ПОСЛЕ строки N старого файла.
                at = hunk.OldStart > 0 ? hunk.OldStart + delta : patch.IsNew ? 0 : cursor;
                at = Math.Clamp(at, 0, lines.Count);
            }
            else
            {
                var expected = Math.Clamp(hunk.OldStart > 0 ? hunk.OldStart - 1 + delta : cursor, 0, Math.Max(0, lines.Count));
                at = Find(lines, oldBlock, expected, exact: true);
                if (at < 0) at = Find(lines, oldBlock, expected, exact: false);
                if (at < 0)
                    return (null, fin, $"hunk {h + 1}/{patch.Hunks.Count} ({hunk.Header}) does not match the file: expected \"{Short(oldBlock.FirstOrDefault(l => l.Trim().Length > 0) ?? "", 100)}\" near line {expected + 1}");
            }
            var touchesEnd = at + oldBlock.Count >= lines.Count;
            // Контекстные строки берём из файла (совпадение могло быть без учёта пробелов) — меняются только строки «-»/«+».
            if (oldBlock.Count > 0)
            {
                var merged = new List<string>(newBlock.Count);
                var oi = 0;
                foreach (var (op, text) in hunk.Lines)
                {
                    if (op == ' ') merged.Add(lines[at + oi++]);
                    else if (op == '-') oi++;
                    else merged.Add(text);
                }
                newBlock = merged;
            }
            lines.RemoveRange(at, oldBlock.Count);
            lines.InsertRange(at, newBlock);
            if (hunk.OldStart > 0) delta = at - (hunk.OldStart - (oldBlock.Count == 0 ? 0 : 1)) + newBlock.Count - oldBlock.Count;
            cursor = at + newBlock.Count;
            if (touchesEnd)
            {
                if (hunk.NoNewlineAtEndOfNew) fin = false;
                else if (hunk.NoNewlineAtEndOfOld || patch.IsNew) fin = true;
            }
        }
        return (lines, fin, null);
    }

    /// <summary>Позиция блока строк: точное совпадение (без учёта \r) или с нормализацией пробелов; ближайшее к ожидаемой.</summary>
    private static int Find(List<string> lines, List<string> block, int expected, bool exact)
    {
        if (block.Count > lines.Count) return -1;
        Func<string, string> norm = exact ? s => s.TrimEnd('\r') : s => WhiteSpace().Replace(s.Trim(), " ");
        var normBlock = block.Select(norm).ToList();
        var maxStart = lines.Count - block.Count;
        var span = Math.Max(expected, maxStart - expected) + 1;
        for (var d = 0; d <= span; d++)
        {
            for (var sign = 0; sign < (d == 0 ? 1 : 2); sign++)
            {
                var start = sign == 0 ? expected - d : expected + d;
                if (d == 0) start = expected;
                if (start < 0 || start > maxStart) continue;
                var ok = true;
                for (var k = 0; k < block.Count && ok; k++) ok = norm(lines[start + k]) == normBlock[k];
                if (ok) return start;
            }
        }
        return -1;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
