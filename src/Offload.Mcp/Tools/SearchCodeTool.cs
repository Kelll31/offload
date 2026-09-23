using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_search_code: поиск по тексту/regex/слову/имени файла с ограничением результатов и короткими сниппетами (без модели).</summary>
internal static class SearchCodeTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? query, string? mode, string[]? paths, bool caseSensitive, int contextLines,
        int maxResults, bool filesOnly)
    {
        var q = ToolHelpers.RequireText(query, "query", 500);
        var m = (mode ?? "text").Trim().ToLowerInvariant();
        if (m is not ("text" or "regex" or "word" or "file")) throw new ToolException("mode must be text, regex, word or file.");
        contextLines = Math.Clamp(contextLines, 0, 5);
        maxResults = Math.Clamp(maxResults <= 0 ? 60 : maxResults, 1, 500);

        ctx.Progress.Report("Indexing files…");
        var index = await CodeIndex.LoadAsync(ctx, paths, codeOnly: false, ctx.Ct).ConfigureAwait(false);
        if (m == "file") return FindFiles(index, q, maxResults);

        Regex regex;
        try
        {
            var opts = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            regex = m switch
            {
                "regex" => new Regex(q, opts, TimeSpan.FromSeconds(1)),
                "word" => CodeIndex.WordRegex(q, !caseSensitive),
                _ => new Regex(Regex.Escape(q), opts, TimeSpan.FromSeconds(1)),
            };
        }
        catch (ArgumentException ex)
        {
            throw new ToolException($"Invalid regex: {ex.Message}");
        }

        var sb = new StringBuilder();
        var hits = 0;
        var filesWithHits = 0;
        var truncated = false;
        foreach (var f in index.Files.OrderBy(f => f.IsTest).ThenBy(f => f.Display, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Ct.ThrowIfCancellationRequested();
            List<int> lines;
            try
            {
                lines = [];
                for (var i = 0; i < f.Lines.Length; i++)
                    if (f.Lines[i].Length <= 4000 && regex.IsMatch(f.Lines[i])) lines.Add(i + 1);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new ToolException("The regex is too slow (catastrophic backtracking); simplify it.");
            }
            if (lines.Count == 0) continue;
            filesWithHits++;
            if (hits >= maxResults)
            {
                truncated = true;
                continue;
            }
            if (filesOnly)
            {
                sb.Append(f.Display).Append($" ({lines.Count})\n");
                hits++;
                continue;
            }
            sb.Append(f.Display).Append(lines.Count > 1 ? $" ({lines.Count} matches)" : "").Append('\n');
            var shownUntil = 0;
            foreach (var line in lines)
            {
                if (hits >= maxResults)
                {
                    truncated = true;
                    break;
                }
                var from = Math.Max(line - contextLines, shownUntil + 1);
                var to = Math.Min(f.Lines.Length, line + contextLines);
                if (contextLines > 0 && shownUntil > 0 && from > shownUntil + 1) sb.Append("  …\n");
                for (var i = from; i <= to; i++)
                {
                    var text = f.Lines[i - 1].TrimEnd();
                    if (text.Length > 200) text = text[..200] + "…";
                    sb.Append(i == line ? "  " : "   ").Append(i).Append(i == line ? ": " : "- ").Append(text).Append('\n');
                }
                shownUntil = to;
                hits++;
            }
        }
        if (hits == 0) return $"No matches for {m} \"{q}\". {index.CoverageNote()}";
        sb.Append($"\n{hits} match{(hits == 1 ? "" : "es")} shown in {filesWithHits} file{(filesWithHits == 1 ? "" : "s")}");
        if (truncated) sb.Append($" (limit max_results={maxResults} reached; narrow the query or paths)");
        sb.Append(" · ").Append(index.CoverageNote());
        return sb.ToString();
    }

    private static string FindFiles(CodeIndexResult index, string q, int max)
    {
        Regex? glob = Glob.HasWildcards(q) ? Glob.ToRegex(q.Replace('\\', '/')) : null;
        var matches = index.Files
            .Where(f => glob is not null
                ? glob.IsMatch(f.Display) || glob.IsMatch(Path.GetFileName(f.Display))
                : f.Display.Contains(q.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f.Display).Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Display.Length)
            .ToList();
        if (matches.Count == 0) return $"No files match \"{q}\". {index.CoverageNote()}";
        var sb = new StringBuilder();
        foreach (var f in matches.Take(max)) sb.Append(f.Display).Append($"  ({f.Lines.Length} lines)\n");
        if (matches.Count > max) sb.Append($"… {matches.Count - max} more\n");
        return sb.ToString().TrimEnd();
    }
}
