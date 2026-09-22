using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_write_file: модель пишет один новый файл на диск; проверка командой и цикл исправлений.</summary>
internal static class WriteFileTool
{
    public const int MaxOutputTokens = 16384;

    public static async Task<string> RunAsync(ToolContext ctx, string? path, string? task, string[]? contextPaths, string? verifyCommand,
        int fixAttempts, int timeoutSec, bool overwrite, bool preview)
    {
        var rawPath = ToolHelpers.RequireText(path, "path", 1024);
        var spec = ToolHelpers.RequireText(task, "task", 12000);
        fixAttempts = Math.Clamp(fixAttempts, 0, 3);
        var verifyTimeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec <= 0 ? 600 : timeoutSec, 10, 3600));
        var verify = string.IsNullOrWhiteSpace(verifyCommand) ? null : VerifyCommand.Validate(verifyCommand, ctx.Cfg.Mcp.VerifyCommandAllowlist);

        var (full, target) = ctx.ResolveWrite(rawPath);
        if (Directory.Exists(target)) throw new ToolException($"'{rawPath}' is a directory.");
        var exists = File.Exists(target);
        if (exists && !overwrite)
            throw new ToolException($"'{rawPath}' already exists. Pass overwrite=true to replace it, or use local_edit_files to modify it.");
        if (TextCodec.IsBinaryExtension(target)) throw new ToolException($"'{rawPath}' has a binary file extension; only text files can be written.");
        var display = ctx.Display(target);
        var root = PathGuard.FindRoot(target, ctx.Roots) ?? ctx.Roots[0];

        var references = new GatherResult();
        if (contextPaths is { Length: > 0 })
        {
            ctx.Progress.Report("Reading reference files…");
            references = await FileGatherer.GatherAsync(contextPaths.Take(64), ctx.Roots, ctx.GatherOptions, ctx.Ct).ConfigureAwait(false);
        }
        var format = ChooseFormat(target, exists, references.Files);

        var model = await ctx.GetModelAsync().ConfigureAwait(false);
        var system = model.SystemPrompt(
            "You write exactly ONE file that will be saved to disk as-is. Output ONLY the complete raw content of the file: " +
            "no explanations, no markdown code fences, no placeholders such as '...', 'TODO: implement' or 'rest of code'. " +
            "Follow the conventions, namespaces, style and test framework of the reference files. The code must compile/run as-is.");
        var taskBlock = $"TARGET FILE: {display}\nTASK:\n{spec}\n";
        var refBudget = Math.Max(0, (model.ContextPerSlot - Tokens.Estimate(system) - Tokens.Estimate(taskBlock)) / 2 - 256);
        var (refText, included, omitted) = ToolHelpers.RenderReferences(references.Files, refBudget);
        ctx.Stats.FilesRead = included;
        ctx.Stats.TokensRead = references.Files.Where(f => !omitted.Contains(f.Display)).Sum(f => (long)f.EstTokens);

        var user = new StringBuilder(taskBlock);
        if (refText.Length > 0) user.Append("\nREFERENCE FILES (read-only):\n").Append(refText);
        user.Append($"\nNow output the complete content of {display}.");

        var job = JobStore.Create("local_write_file", root, spec, ctx.ToolUseId);
        job.Mode = "write";
        job.VerifyCommand = verify;
        JobStore.Snapshot(job, target, display);

        var notes = new List<string>();
        if (omitted.Count > 0) notes.Add($"reference files not shown to the model (context limit): {string.Join(", ", omitted.Take(8))}");
        VerifyResult? lastVerify = null;
        var attempt = 0;
        string content;
        try
        {
            await using (var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false))
            {
                content = await GenerateAsync(ctx, model, system, user.ToString(), display, "writing " + Path.GetFileName(display)).ConfigureAwait(false);
            }
            Write(target, content, format, notes);
            ctx.Stats.TokensWritten += Tokens.Estimate(content);

            while (verify is not null)
            {
                attempt++;
                lastVerify = await VerifyCommand.RunAsync(verify, root, verifyTimeout, ctx.Progress, ctx.Ct).ConfigureAwait(false);
                if (lastVerify.Passed || attempt > fixAttempts) break;
                ctx.Progress.Report($"verify failed (exit {lastVerify.ExitCode}); asking the model to fix it (attempt {attempt}/{fixAttempts})");
                var fix = new StringBuilder(taskBlock);
                var fixRefBudget = Math.Max(0, refBudget - Tokens.Estimate(content) - 1500);
                var (fixRefs, _, _) = ToolHelpers.RenderReferences(references.Files, fixRefBudget);
                if (fixRefs.Length > 0) fix.Append("\nREFERENCE FILES (read-only):\n").Append(fixRefs);
                fix.Append($"\nYOUR PREVIOUS VERSION OF {display}:\n").Append(content);
                fix.Append($"\n\nThe check `{verify}` FAILED (exit {lastVerify.ExitCode}). Relevant output:\n").Append(lastVerify.ForModel(6000));
                fix.Append($"\nFix the file so the check passes. Output ONLY the complete corrected content of {display}.");
                string fixedContent;
                try
                {
                    await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
                    fixedContent = await GenerateAsync(ctx, model, system, fix.ToString(), display, $"fixing {Path.GetFileName(display)}").ConfigureAwait(false);
                }
                catch (ToolException ex)
                {
                    // Неудачная попытка исправления — оставляем предыдущую версию файла.
                    notes.Add("fix attempt failed: " + ex.Message);
                    break;
                }
                content = fixedContent;
                Write(target, content, format, notes);
                ctx.Stats.TokensWritten += Tokens.Estimate(content);
            }
        }
        catch (OperationCanceledException) when (ctx.Ct.IsCancellationRequested)
        {
            JobStore.Finish(job, JobStatus.Cancelled);
            throw;
        }
        catch (Exception)
        {
            // Возвращаем исходное состояние: новый файл удаляем, перезаписанный восстанавливаем из снимка.
            try
            {
                var jf = job.Files[0];
                if (jf.ExistedBefore && JobStore.ReadSnapshot(job, jf) is { } orig) JobStore.WriteBytesAtomic(target, orig);
                else if (!jf.ExistedBefore && File.Exists(target)) File.Delete(target);
            }
            catch
            {
                // Откат не удался — задача остаётся для local_job revert.
            }
            JobStore.Finish(job, JobStatus.Failed);
            throw;
        }

        var status = lastVerify is null || lastVerify.Passed ? JobStatus.Applied : JobStatus.Failed;
        job.VerifySummary = lastVerify?.Summary(attempt, 20, 1500);
        job.Notes.AddRange(notes);
        JobStore.Finish(job, status);

        var bytes = new FileInfo(target).Length;
        var lines = TextCodec.SplitLines(content).Length;
        var sb = new StringBuilder();
        sb.Append($"{(exists ? "overwrote" : "wrote")} {display} ({lines} lines, {FormatSize(bytes)}, {format.Describe()}) · job_id={job.Id}");
        if (status == JobStatus.Failed) sb.Append(" · status: failed verification (file kept)");
        sb.Append('\n');
        if (lastVerify is not null) sb.Append(lastVerify.Summary(attempt)).Append('\n');
        foreach (var n in notes) sb.Append("note: ").Append(n).Append('\n');
        if (preview)
        {
            sb.Append("preview (first 30 lines):\n");
            foreach (var l in TextCodec.SplitLines(content).Take(30)) sb.Append(l.Length > 200 ? l[..200] + "…" : l).Append('\n');
        }
        sb.Append($"undo: local_job action=revert job_id={job.Id}");
        return sb.ToString();
    }

    private static async Task<string> GenerateAsync(ToolContext ctx, LocalModel model, string system, string user, string display, string label)
    {
        var promptTokens = Tokens.Estimate(system) + Tokens.Estimate(user);
        var maxOut = Math.Min(MaxOutputTokens, model.ContextPerSlot - promptTokens - 256);
        if (maxOut < 512)
            throw new ToolException($"Not enough context left for the file (context {model.ContextPerSlot} tok, prompt ≈{promptTokens} tok). Pass fewer/smaller context_paths.");
        var reply = await model.ChatAsync(system, user, maxOut, label, ctx.Ct).ConfigureAwait(false);
        if (reply.Truncated)
            throw new ToolException($"The model's output for {display} was cut off at {maxOut} tokens (file too long for the local model). Split the task into smaller files or write it yourself; nothing was written.");
        var text = OutputCleaner.StripFence(ToolHelpers.StripEchoHeader(reply.Text, display), allowInnerBlock: !ToolHelpers.IsMarkdownLike(display));
        if (text.Trim().Length == 0) throw new ToolException($"The local model returned empty content for {display}; nothing was written.");
        return text;
    }

    private static void Write(string target, string content, TextFormat format, List<string> notes)
    {
        var bytes = TextCodec.Encode(content, format, out var fallback);
        if (fallback && !notes.Contains("written as UTF-8 with BOM (characters not representable in windows-1251)"))
            notes.Add("written as UTF-8 with BOM (characters not representable in windows-1251)");
        JobStore.WriteBytesAtomic(target, bytes);
    }

    /// <summary>Кодировка/переводы строк нового файла: как у перезаписываемого, иначе как у справочных того же типа, иначе UTF-8 LF.</summary>
    internal static TextFormat ChooseFormat(string target, bool exists, IReadOnlyList<GatheredFile> references)
    {
        if (exists)
        {
            try
            {
                var (_, f) = TextCodec.Decode(JobStore.ReadAllBytesShared(target));
                return f with { FinalNewline = true };
            }
            catch
            {
                // Нечитаемый — берём по умолчанию.
            }
        }
        var ext = Path.GetExtension(target);
        var same = references.Where(r => Path.GetExtension(r.FullPath).Equals(ext, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = same.Count > 0 ? same : references.ToList();
        if (pool.Count == 0) return TextFormat.DefaultUtf8Lf;
        var kind = same.Count > 0 ? same.GroupBy(r => r.Format.Kind).OrderByDescending(g => g.Count()).First().Key : TextEncodingKind.Utf8;
        if (kind is TextEncodingKind.Utf16LE or TextEncodingKind.Utf16BE) kind = TextEncodingKind.Utf8Bom;
        var newline = pool.Count(r => r.Format.NewLine == "\r\n") > pool.Count / 2 ? "\r\n" : "\n";
        return new TextFormat(kind, newline, true);
    }

    private static string FormatSize(long bytes) => bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";
}
