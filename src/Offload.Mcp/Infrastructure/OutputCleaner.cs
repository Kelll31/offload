using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

/// <summary>Очистка ответа модели: блоки размышлений, обёртки ```, маркер NO_CHANGES, «ленивые» пропуски кода.</summary>
internal static partial class OutputCleaner
{
    public const string NoChangesMarker = "NO_CHANGES";

    [GeneratedRegex(@"<think(?:ing)?>.*?</think(?:ing)?>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"^\s*<think(?:ing)?>", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingThinkOpen();

    [GeneratedRegex(@"</think(?:ing)?>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkClose();

    /// <summary>
    /// Удалить &lt;think&gt;…&lt;/think&gt; (в том числе незакрытый ведущий блок — ответ целиком из размышлений)
    /// и «хвост» размышлений до одиночного &lt;/think&gt; (когда открывающий тег был в шаблоне промпта).
    /// </summary>
    public static string StripThink(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = ThinkBlock().Replace(text, "");
        var open = LeadingThinkOpen().Match(t);
        if (open.Success)
        {
            // Незакрытый ведущий <think> — модель не дошла до ответа.
            return "";
        }
        var close = ThinkClose().Match(t);
        if (close.Success) t = t[(close.Index + close.Length)..];
        return t.Trim('\r', '\n', ' ', '\t');
    }

    /// <summary>
    /// Снять обёртку ```lang … ``` вокруг содержимого файла. allowInnerBlock — если вокруг единственного блока
    /// короткий пояснительный текст («Here is the file:»); для Markdown-файлов не применяется.
    /// </summary>
    public static string StripFence(string text, bool allowInnerBlock = true)
    {
        var t = text.Replace("\r\n", "\n").Trim('\n', '\r');
        if (t.TrimStart().StartsWith("```", StringComparison.Ordinal) || t.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
        {
            var start = t.TrimStart();
            var fence = start[..3];
            var nl = start.IndexOf('\n');
            if (nl < 0) return "";
            var body = start[(nl + 1)..];
            var bodyEnd = body.TrimEnd();
            if (bodyEnd.EndsWith(fence, StringComparison.Ordinal))
            {
                var lastNl = bodyEnd.LastIndexOf('\n');
                var lastLine = lastNl < 0 ? bodyEnd : bodyEnd[(lastNl + 1)..];
                if (lastLine.Trim() == fence) body = lastNl < 0 ? "" : bodyEnd[..lastNl];
            }
            return body.TrimEnd('\n', '\r') + "\n";
        }
        if (!allowInnerBlock) return t + (t.EndsWith('\n') ? "" : "\n");

        // «Вот файл:\n```cs\n…\n```» — ровно один блок и немного текста вокруг.
        var matches = FencedBlock().Matches(t);
        if (matches.Count == 1)
        {
            var m = matches[0];
            var outside = t.Length - m.Length;
            if (outside < 300) return m.Groups["body"].Value.TrimEnd('\n', '\r') + "\n";
        }
        return t + "\n";
    }

    [GeneratedRegex(@"^(?:```|~~~)[^\n]*\n(?<body>.*?)\n(?:```|~~~)[ \t]*$", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex FencedBlock();

    public static bool IsNoChanges(string text)
    {
        var t = text.Trim().Trim('`', '"', '\'', '.', ' ').Trim();
        return t.Equals(NoChangesMarker, StringComparison.OrdinalIgnoreCase)
            || t.Equals("NO CHANGES", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"^\s*(?://|#|--|;|/\*|\*|<!--|\{|\(\*)?\s*(?:\.\.\.|…)?\s*(?:\(?\s*)?(?:rest of (?:the )?(?:file|code|class|method|implementation)|remaining (?:code|methods|content)|existing code|unchanged (?:code|methods|content)|same as (?:before|above|original)|code (?:remains|stays) (?:the same|unchanged)|other methods)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ElisionLine();

    [GeneratedRegex(@"^\s*(?://|#)\s*(?:\.\.\.|…)\s*$", RegexOptions.Multiline)]
    private static partial Regex BareEllipsisComment();

    /// <summary>
    /// Модель «сократила» файл («// ... rest of the code unchanged») — такой ответ нельзя применять.
    /// Маркеры, уже присутствовавшие в исходном файле, не считаются.
    /// </summary>
    public static bool HasElision(string newText, string original)
    {
        foreach (Match m in ElisionLine().Matches(newText))
        {
            var line = m.Value.Trim();
            if (!original.Contains(line, StringComparison.OrdinalIgnoreCase)) return true;
        }
        var bare = BareEllipsisComment().Matches(newText).Count;
        var bareOrig = BareEllipsisComment().Matches(original).Count;
        return bare > bareOrig;
    }
}
