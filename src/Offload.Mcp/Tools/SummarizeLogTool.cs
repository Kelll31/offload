using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_summarize_log: серверная фильтрация лога + сжатое изложение моделью.</summary>
internal static class SummarizeLogTool
{
    /// <summary>Из очень больших логов читаем только конец (ошибки сборки/тестов почти всегда там).</summary>
    public const long MaxScanBytes = 256L * 1024 * 1024;

    public static async Task<string> RunAsync(ToolContext ctx, string? path, string? focus, int tailLines, int maxAnswerTokens)
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
