using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

internal static class ToolHelpers
{
    public static string RequireText(string? value, string name, int maxChars)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new ToolException($"'{name}' is required.");
        if (v.Length > maxChars) throw new ToolException($"'{name}' is too long ({v.Length} chars, max {maxChars}). Pass file paths instead of pasting content.");
        return v;
    }

    public static string[] RequireList(string[]? values, string name, int max)
    {
        var list = (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        if (list.Length == 0) throw new ToolException($"'{name}' must contain at least one path.");
        if (list.Length > max) throw new ToolException($"'{name}' has {list.Length} entries (max {max}); use globs or directories.");
        return list;
    }

    /// <summary>Справочные файлы без номеров строк (для записи/правки — чтобы модель не копировала номера в код).</summary>
    public static (string Text, int Included, List<string> Omitted) RenderReferences(IReadOnlyList<GatheredFile> files, int budgetTokens)
    {
        var sb = new StringBuilder();
        var used = 0;
        var included = 0;
        var omitted = new List<string>();
        foreach (var f in files)
        {
            var t = Tokens.Estimate(f.Text) + 20;
            if (used + t > budgetTokens)
            {
                omitted.Add(f.Display);
                continue;
            }
            sb.Append("=== ").Append(f.Display).Append(f.Truncated ? " (truncated)" : "").Append(" ===\n").Append(f.Text);
            if (!f.Text.EndsWith('\n')) sb.Append('\n');
            used += t;
            included++;
        }
        return (sb.ToString(), included, omitted);
    }

    /// <summary>Убрать эхо заголовка «=== path ===», если модель повторила его в начале ответа.</summary>
    public static string StripEchoHeader(string text, string display)
    {
        var t = text.TrimStart('\n', '\r');
        var nl = t.IndexOf('\n');
        var first = (nl < 0 ? t : t[..nl]).Trim();
        if (first.StartsWith("===", StringComparison.Ordinal) && first.EndsWith("===", StringComparison.Ordinal) && first.Contains(Path.GetFileName(display), StringComparison.OrdinalIgnoreCase))
            return nl < 0 ? "" : t[(nl + 1)..];
        return text;
    }

    public static bool IsMarkdownLike(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdx" or ".rst" or ".txt" or ".adoc";

    public static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
}
