using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_summarize_log: серверная фильтрация лога + сжатое изложение моделью.</summary>
internal static class SummarizeLogTool
{
    /// <summary>Из очень больших логов читаем только конец (ошибки сборки/тестов почти всегда там).</summary>
    public const long MaxScanBytes = 256L * 1024 * 1024;

    public static async Task<string> RunAsync(ToolContext ctx, string? path, string? focus, int tailLines, int maxAnswerTokens, string? pattern = null)
    {
        var raw = ToolHelpers.RequireText(path, "path", 1024);
        var full = ctx.ResolveRead(raw);
        if (!File.Exists(full))
            throw new ToolException(Directory.Exists(full) ? $"'{raw}' is a directory; pass a log file." : $"Log file '{raw}' not found.");
        if (TextCodec.IsBinaryExtension(full)) throw new ToolException($"'{raw}' looks like a binary file, not a log.");
        var focusText = string.IsNullOrWhiteSpace(focus) ? "errors, failing tests, root cause" : focus.Trim();
        if (focusText.Length > 500) focusText = focusText[..500];
        var maxAnswer = Math.Clamp(maxAnswerTokens <= 0 ? 600 : maxAnswerTokens, 64, 4096);
        var display = ctx.Display(full);
        if (!string.IsNullOrWhiteSpace(pattern)) return Grep(full, display, pattern!, tailLines, ctx.Ct);

        ctx.Progress.Report($"Scanning {display}…");
        var (digest, bytesScanned, skippedHead) = ReadDigest(full, tailLines, ctx.Ct);
        ctx.Stats.FilesRead = 1;
        ctx.Stats.TokensRead = (long)(bytesScanned / 3.2);

        var model = await ctx.GetModelAsync().ConfigureAwait(false);
        var words = Math.Max(60, maxAnswer * 2 / 3);
        var system = model.SystemPrompt(
            "You summarize a build/test/application log for a developer. Output exactly these four sections, nothing else:\n" +
            "Failing: the failing tests/targets/steps (or 'none found').\n" +
            "Root error: the FIRST real error that caused the failure, quoting its key message, with source path:line if the log has it.\n" +
            "Likely cause: one or two sentences.\n" +
            "Next step: one concrete action.\n" +
            $"Cite log lines as L<number>. Keep it under {words} words. Later errors are often consequences of the first one.");
        var header = new StringBuilder();
        header.Append($"LOG FILE: {display} ({digest.TotalLines} lines{(skippedHead ? ", only the last part of a very large file was scanned" : "")}{(tailLines > 0 ? $", last {tailLines} lines only" : "")}; " +
                      $"{digest.ErrorLines} lines match error patterns{(digest.CollapsedRepeats > 0 ? $", {digest.CollapsedRepeats} repeats collapsed" : "")}).\n");
        header.Append("Filtered excerpt below; 'L<n>:' is the original line number, '… (N lines omitted) …' marks gaps.\n");
        header.Append("FOCUS: ").Append(focusText).Append('\n');

        var budget = model.MaterialBudget(maxAnswer, system, header.ToString());
        if (budget < 300) throw new ToolException($"The local model context ({model.ContextPerSlot} tok) is too small; lower max_answer_tokens.");
        var excerpt = LogPrefilter.Render(digest, budget * 3);
        // Оценка могла оказаться выше бюджета (кириллица) — ужимаем.
        while (Tokens.Estimate(excerpt) > budget && excerpt.Length > 2000)
            excerpt = LogPrefilter.Render(digest, excerpt.Length * 3 / 4);

        await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        ModelReply reply;
        try
        {
            reply = await model.ChatAsync(system, header + "\n" + excerpt, maxAnswer, "summarizing", ctx.Ct).ConfigureAwait(false);
        }
        catch (ContextExceededException)
        {
            excerpt = LogPrefilter.Render(digest, excerpt.Length / 2);
            reply = await model.ChatAsync(system, header + "\n" + excerpt, maxAnswer, "summarizing", ctx.Ct).ConfigureAwait(false);
        }
        var text = reply.Text.Trim();
        if (text.Length == 0) throw new ToolException("The local model returned an empty summary; read the log tail yourself.");
        if (reply.Truncated) text += $"\n[summary cut at max_answer_tokens={maxAnswer}]";
        var stats = $"log: {display} · {digest.TotalLines} lines · {digest.ErrorLines} error-pattern lines";
        return text + "\n\n" + stats;
    }

    /// <summary>
    /// Поиск по логу без модели (pattern — regex или текст): совпадения с номерами строк и частоты «похожих» строк
    /// (цифры/hex/GUID заменены на #) — для поиска событий и корреляции по id запроса/задачи/потока.
    /// </summary>
    internal static string Grep(string full, string display, string pattern, int tailLines, CancellationToken ct)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            regex = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        long lineCount = 0;
        if (tailLines > 0)
            foreach (var _ in File.ReadLines(full)) lineCount++;
        var firstLine = tailLines > 0 ? Math.Max(1, lineCount - tailLines + 1) : 1;

        const int MaxShown = 80;
        var hits = new Queue<(long Line, string Text)>();
        var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
        long n = 0, total = 0;
        var norm = new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}|0x[0-9a-fA-F]+|\d+", RegexOptions.CultureInvariant);
        foreach (var raw in File.ReadLines(full))
        {
            n++;
            if ((n & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            if (n < firstLine) continue;
            var line = LogPrefilter.CleanLine(raw, 8000);
            bool match;
            try { match = regex.IsMatch(line); }
            catch (RegexMatchTimeoutException) { throw new ToolException("pattern is too slow (catastrophic backtracking); simplify it or use plain text."); }
            if (!match) continue;
            total++;
            // При tail_lines — последние совпадения, иначе первые.
            if (hits.Count < MaxShown) hits.Enqueue((n, line.Length > 300 ? line[..300] + "…" : line));
            else if (tailLines > 0)
            {
                hits.Dequeue();
                hits.Enqueue((n, line.Length > 300 ? line[..300] + "…" : line));
            }
            var shape = norm.Replace(line.Length > 200 ? line[..200] : line, "#");
            if (shapes.Count < 5000 || shapes.ContainsKey(shape)) shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
        }
        if (total == 0) return $"No lines in {display} ({n} lines) match \"{pattern}\".";
        var sb = new StringBuilder($"{total} of {n} lines in {display} match \"{pattern}\"");
        sb.Append(total > hits.Count ? $" ({(tailLines > 0 ? "last" : "first")} {hits.Count} shown)\n" : "\n");
        foreach (var (line, text) in hits) sb.Append('L').Append(line).Append(": ").Append(text).Append('\n');
        var top = shapes.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).Take(8).ToList();
        if (top.Count > 0)
        {
            sb.Append("most frequent shapes (numbers/ids → #):\n");
            foreach (var (shape, count) in top) sb.Append($"  {count}× {(shape.Length > 160 ? shape[..160] + "…" : shape)}\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Потоковое чтение лога (кодировка по BOM / строгому UTF-8 / cp1251) и фильтрация.</summary>
    internal static (LogDigest Digest, long BytesScanned, bool SkippedHead) ReadDigest(string full, int tailLines, CancellationToken ct)
    {
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var length = fs.Length;
        var skippedHead = false;
        var head = new byte[(int)Math.Min(64 * 1024, length)];
        var n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var (_, format) = TextCodec.Decode(head.AsSpan(0, n), truncated: n < length);
        var encoding = format.Kind switch
        {
            TextEncodingKind.Utf16LE => Encoding.Unicode,
            TextEncodingKind.Utf16BE => Encoding.BigEndianUnicode,
            TextEncodingKind.Windows1251 => TextCodec.Cp1251,
            _ => (Encoding)new UTF8Encoding(false),
        };
        if (length > MaxScanBytes)
        {
            skippedHead = true;
            var pos = length - MaxScanBytes;
            if (encoding is UnicodeEncoding && pos % 2 == 1) pos++;
            fs.Seek(pos, SeekOrigin.Begin);
        }
        else
        {
            fs.Seek(0, SeekOrigin.Begin);
        }
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: !skippedHead, 1 << 16);
        if (skippedHead) reader.ReadLine(); // неполная первая строка

        IEnumerable<string> Lines()
        {
            string? line;
            var i = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                if ((++i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                yield return line;
            }
        }

        LogDigest digest;
        if (tailLines > 0)
        {
            var ring = new Queue<string>();
            long total = 0;
            foreach (var l in Lines())
            {
                total++;
                ring.Enqueue(l);
                if (ring.Count > tailLines) ring.Dequeue();
            }
            digest = LogPrefilter.Filter(ring, firstLineNumber: total - ring.Count + 1);
        }
        else
        {
            digest = LogPrefilter.Filter(Lines());
        }
        return (digest, Math.Min(length, MaxScanBytes), skippedHead);
    }
}
