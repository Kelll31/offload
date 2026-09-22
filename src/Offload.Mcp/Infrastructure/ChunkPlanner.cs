using System.Text;

namespace Offload.Mcp.Infrastructure;

/// <summary>Фрагмент файла в части материала: весь файл или диапазон строк.</summary>
internal sealed record ChunkPart(GatheredFile File, int FromLine, int ToLine, int Tokens)
{
    public bool IsWhole => FromLine == 1 && ToLine >= File.Lines.Length;

    /// <summary>Заголовок + пронумерованные строки.</summary>
    public string Render()
    {
        var total = File.Lines.Length;
        var header = IsWhole
            ? $"=== {File.Display} ({total} lines{(File.Truncated ? ", file truncated by size limit" : "")}) ==="
            : $"=== {File.Display} (lines {FromLine}-{ToLine} of {total}{(File.Truncated ? ", file truncated by size limit" : "")}) ===";
        return header + "\n" + File.Numbered(FromLine, ToLine);
    }
}

internal sealed class Chunk
{
    public List<ChunkPart> Parts { get; } = [];
    public int Tokens { get; set; }

    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var p in Parts) sb.Append(p.Render()).Append('\n');
        return sb.ToString();
    }
}

internal sealed class ChunkPlan
{
    public List<Chunk> Chunks { get; } = [];
    /// <summary>Файлы, не попавшие ни в одну часть (превышен лимит частей).</summary>
    public List<GatheredFile> Uncovered { get; } = [];
    /// <summary>Файлы, покрытые не полностью (часть строк не вошла).</summary>
    public List<GatheredFile> Partial { get; } = [];

    public int CoveredFiles(IReadOnlyList<GatheredFile> files) => files.Count - Uncovered.Count - Partial.Count;
}

/// <summary>
/// Раскладка файлов по частям, каждая из которых помещается в бюджет контекста модели.
/// Файлы идут целиком, если помещаются; большие — режутся по строкам. Частей не больше maxChunks —
/// остальное честно помечается как непокрытое.
/// </summary>
internal static class ChunkPlanner
{
    private const int HeaderTokens = 24;

    public static int LineTokens(string line) => Tokens.Estimate(line) + 3;

    public static ChunkPlan Plan(IReadOnlyList<GatheredFile> files, int budgetTokens, int maxChunks)
    {
        budgetTokens = Math.Max(256, budgetTokens);
        var plan = new ChunkPlan();
        var current = new Chunk();

        void Close()
        {
            if (current.Parts.Count == 0) return;
            plan.Chunks.Add(current);
            current = new Chunk();
        }

        for (var fi = 0; fi < files.Count; fi++)
        {
            var f = files[fi];
            var whole = f.EstTokens + HeaderTokens;
            if (whole <= budgetTokens)
            {
                if (current.Tokens + whole > budgetTokens) Close();
                if (plan.Chunks.Count >= maxChunks)
                {
                    plan.Uncovered.AddRange(files.Skip(fi));
                    return plan;
                }
                current.Parts.Add(new ChunkPart(f, 1, Math.Max(1, f.Lines.Length), whole));
                current.Tokens += whole;
                continue;
            }

            // Большой файл — режем по строкам.
            var lines = f.Lines;
            var from = 1;
            while (from <= lines.Length)
            {
                var room = budgetTokens - current.Tokens - HeaderTokens;
                if (room < budgetTokens / 4)
                {
                    Close();
                    room = budgetTokens - HeaderTokens;
                }
                if (plan.Chunks.Count >= maxChunks)
                {
                    if (from > 1) plan.Partial.Add(f);
                    else plan.Uncovered.Add(f);
                    plan.Uncovered.AddRange(files.Skip(fi + 1));
                    return plan;
                }
                var to = from - 1;
                var used = 0;
                while (to < lines.Length)
                {
                    var t = LineTokens(lines[to]);
                    if (used + t > room && to >= from) break;
                    used += t;
                    to++;
                }
                if (to < from) to = from; // одна строка длиннее бюджета — всё равно берём (обрезана при чтении)
                current.Parts.Add(new ChunkPart(f, from, to, used + HeaderTokens));
                current.Tokens += used + HeaderTokens;
                from = to + 1;
                if (from <= lines.Length) Close();
            }
        }
        Close();
        // Последнее закрытие могло превысить лимит частей.
        while (plan.Chunks.Count > maxChunks)
        {
            var last = plan.Chunks[^1];
            plan.Chunks.RemoveAt(plan.Chunks.Count - 1);
            foreach (var p in last.Parts)
            {
                if (p.FromLine > 1) { if (!plan.Partial.Contains(p.File)) plan.Partial.Add(p.File); }
                else if (!plan.Uncovered.Contains(p.File)) plan.Uncovered.Add(p.File);
            }
        }
        return plan;
    }
}
