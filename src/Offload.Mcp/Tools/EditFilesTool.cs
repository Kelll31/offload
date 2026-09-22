using System.Text;
using Offload.Core.Logging;
using Offload.Mcp.Infrastructure;
using Offload.OpenCode;

namespace Offload.Mcp.Tools;

/// <summary>Файл из списка разрешённых для правки.</summary>
internal sealed class EditTarget
{
    public required string Path { get; init; }
    public required string Display { get; init; }
    public required string OriginalText { get; init; }
    public required TextFormat Format { get; init; }
    public required int Tokens { get; init; }
    public string CurrentText { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>local_edit_files: механическая правка разрешённых файлов (перезапись моделью или агент OpenCode) со снимком и проверкой.</summary>
internal static class EditFilesTool
{
    public const int MaxFiles = 50;
    public const int MaxRewriteFiles = 8;
    public const double MaxRewriteShare = 0.35;

    public static async Task<string> RunAsync(ToolContext ctx, string? task, string[]? files, string[]? contextPaths, string? verifyCommand,
        int fixAttempts, int timeoutMinutes, bool dryRun, string? mode)
    {
        var spec = ToolHelpers.RequireText(task, "task", 12000);
        var specs = ToolHelpers.RequireList(files, "files", MaxFiles);
        fixAttempts = Math.Clamp(fixAttempts, 0, 4);
        var deadline = TimeSpan.FromMinutes(Math.Clamp(timeoutMinutes <= 0 ? 10 : timeoutMinutes, 1, 30));
        var requestedMode = (mode ?? "auto").Trim().ToLowerInvariant();
        if (requestedMode is not ("auto" or "rewrite" or "agent")) throw new ToolException("mode must be auto, rewrite or agent.");
        var verify = string.IsNullOrWhiteSpace(verifyCommand) ? null : VerifyCommand.Validate(verifyCommand, ctx.Cfg.Mcp.VerifyCommandAllowlist);

        // Разрешённые файлы: только существующие, внутри проекта, прошедшие проверки записи.
        var expandInfo = new GatherResult();
        var candidates = await FileGatherer.ExpandAsync(specs, ctx.Roots, ctx.GatherOptions, expandInfo, ctx.Ct).ConfigureAwait(false);
        if (expandInfo.Skipped.Count > 0)
            throw new ToolException("files must be existing files inside the project (use local_write_file to create new ones). Problems: " +
                                    string.Join(", ", expandInfo.Skipped.Take(10).Select(s => $"{s.Display} ({s.Reason})")));
        var targets = new List<EditTarget>();
        foreach (var (full, _) in candidates)
        {
            var (_, canonical) = ctx.ResolveWrite(full);
            if (targets.Any(t => t.Path.Equals(canonical, StringComparison.OrdinalIgnoreCase))) continue;
            if (FileGatherer.CheckFile(canonical, ctx.GatherOptions, ctx.Roots, isExplicit: true) is { } why)
                throw new ToolException($"Cannot edit '{ctx.Display(canonical)}': {why}.");
            var bytes = JobStore.ReadAllBytesShared(canonical);
            if (bytes.Length > JobStore.MaxSnapshotBytes) throw new ToolException($"'{ctx.Display(canonical)}' is too large to edit safely.");
            if (TextCodec.LooksBinary(bytes)) throw new ToolException($"Cannot edit '{ctx.Display(canonical)}': binary file.");
            var (text, format) = TextCodec.Decode(bytes);
            targets.Add(new EditTarget
            {
                Path = canonical,
                Display = ctx.Display(canonical),
                OriginalText = text,
                CurrentText = text,
                Format = format,
                Tokens = Tokens.Estimate(text),
            });
            if (targets.Count > MaxFiles) throw new ToolException($"files resolve to more than {MaxFiles} files; narrow the globs or split the task.");
        }
        if (targets.Count == 0) throw new ToolException("files did not match any existing file.");
        var root = PathGuard.FindRoot(targets[0].Path, ctx.Roots) ?? ctx.Roots[0];

        var references = new GatherResult();
        if (contextPaths is { Length: > 0 })
            references = await FileGatherer.GatherAsync(contextPaths.Take(64), ctx.Roots, ctx.GatherOptions, ctx.Ct).ConfigureAwait(false);
        var refFiles = references.Files.Where(r => !targets.Any(t => t.Path.Equals(r.FullPath, StringComparison.OrdinalIgnoreCase))).ToList();

        var model = await ctx.GetModelAsync().ConfigureAwait(false);
        var effectiveMode = ChooseMode(ctx, requestedMode, targets, model.ContextPerSlot);

        var job = JobStore.Create("local_edit_files", root, spec, ctx.ToolUseId);
        job.Mode = effectiveMode;
        job.VerifyCommand = verify;
        foreach (var t in targets) JobStore.Snapshot(job, t.Path, t.Display);

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        deadlineCts.CancelAfter(deadline);
        var ct = deadlineCts.Token;
        var summary = new List<string>();
        VerifyResult? lastVerify = null;
        var attempt = 0;
        var notes = new List<string>();
        try
        {
            await using (await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ct).ConfigureAwait(false))
            {
                if (effectiveMode == "rewrite")
                    await RewriteAllAsync(ctx, model, spec, targets, refFiles, null, summary, ct).ConfigureAwait(false);
                else
                    await RunAgentAsync(ctx, job, spec, targets, refFiles, root, null, deadline, summary, notes, ct).ConfigureAwait(false);
            }

            var anyChange = targets.Any(t => t.CurrentText != t.OriginalText) || JobStore.ChangedFromSnapshot(job).Count > 0;
            while (verify is not null && anyChange)
            {
                attempt++;
                lastVerify = await VerifyCommand.RunAsync(verify, root, deadline, ctx.Progress, ct).ConfigureAwait(false);
                if (lastVerify.Passed || attempt > fixAttempts) break;
                ctx.Progress.Report($"verify failed (exit {lastVerify.ExitCode}); fix round {attempt}/{fixAttempts}");
                var feedback = $"The check `{verify}` FAILED (exit {lastVerify.ExitCode}) after your edit. Relevant output:\n{lastVerify.ForModel(5000)}\n" +
                               "Fix the problem while still fulfilling the task.";
                await using (await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ct).ConfigureAwait(false))
                {
                    if (effectiveMode == "rewrite")
                        await RewriteAllAsync(ctx, model, spec, targets, refFiles, feedback, summary, ct).ConfigureAwait(false);
                    else
                        await RunAgentAsync(ctx, job, spec, targets, refFiles, root, feedback, deadline, summary, notes, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ctx.Ct.IsCancellationRequested)
        {
            JobStore.Finish(job, JobStatus.Failed);
            throw new ToolException($"local_edit_files hit timeout_minutes={deadline.TotalMinutes:0}. Changes made so far are kept in job {job.Id}; " +
                                    $"inspect with local_job action=diff or undo with local_job action=revert job_id={job.Id}.");
        }
        catch (OperationCanceledException)
        {
            JobStore.Finish(job, JobStatus.Cancelled);
            throw;
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException)
        {
            JobStore.Finish(job, JobStatus.Failed);
            var changed = JobStore.ChangedFromSnapshot(job);
            throw new ToolException(ex.Message + (changed.Count > 0
                ? $" Partial changes to {changed.Count} file(s) are kept in job {job.Id}: undo with local_job action=revert job_id={job.Id}."
                : ""));
        }

        // dry_run: сохраняем предлагаемое содержимое и возвращаем исходное.
        string status;
        var changedFiles = JobStore.ChangedFromSnapshot(job).Where(f => f.Allowlisted).ToList();
        if (dryRun)
        {
            foreach (var jf in job.Files.Where(f => f.Allowlisted))
            {
                var current = File.Exists(jf.Path) ? JobStore.ReadAllBytesShared(jf.Path) : null;
                var original = JobStore.ReadSnapshot(job, jf);
                if (current is not null) JobStore.SaveProposed(job, jf, current);
                (jf.Added, jf.Removed) = JobStore.CountChanges(original, current);
                if (original is not null) JobStore.WriteBytesAtomic(jf.Path, original);
                else if (File.Exists(jf.Path)) File.Delete(jf.Path);
            }
            job.Status = JobStatus.DryRun;
            job.FinishedUtc = DateTime.UtcNow;
            status = JobStatus.DryRun;
        }
        else
        {
            status = changedFiles.Count == 0 ? JobStatus.NoChanges
                : lastVerify is { Passed: false } ? JobStatus.Failed
                : JobStatus.Applied;
        }
        foreach (var t in targets)
        {
            var jf = job.Files.First(f => f.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase));
            jf.Note = t.Note;
        }
        job.VerifySummary = lastVerify?.Summary(attempt, 20, 1500);
        job.Summary = string.Join("\n", summary.Take(5));
        job.Notes.AddRange(notes);
        if (dryRun) JobStore.Save(job);
        else JobStore.Finish(job, status);

        ctx.Stats.FilesRead = targets.Count + refFiles.Count;
        ctx.Stats.TokensRead = targets.Sum(t => (long)t.Tokens) + refFiles.Sum(r => (long)r.EstTokens);
        return Render(job, status, effectiveMode, lastVerify, attempt, summary, notes, dryRun);
    }

    private static string ChooseMode(ToolContext ctx, string requested, List<EditTarget> targets, int contextPerSlot)
    {
        var fits = targets.Count <= MaxRewriteFiles && targets.All(t => t.Tokens <= contextPerSlot * MaxRewriteShare);
        var agentAvailable = ctx.Cfg.OpenCode.Enabled && StatusTool.FindOpenCode(ctx.Cfg) is not null;
        switch (requested)
        {
            case "rewrite":
                if (targets.Any(t => t.Tokens > contextPerSlot * 0.45))
                    throw new ToolException($"rewrite mode: some files are too large for the local context ({contextPerSlot} tok): " +
                                            string.Join(", ", targets.Where(t => t.Tokens > contextPerSlot * 0.45).Take(5).Select(t => t.Display)));
                return "rewrite";
            case "agent":
                if (!agentAvailable) throw new ToolException("agent mode needs OpenCode: install/enable it in the Offload tray app, or use mode=rewrite for small files.");
                return "agent";
            default:
                if (fits) return "rewrite";
                if (agentAvailable) return "agent";
                throw new ToolException(
                    $"These files are too large or too many for direct rewriting (max {MaxRewriteFiles} files, each ≤{(int)(contextPerSlot * MaxRewriteShare)} tok for context {contextPerSlot}) " +
                    "and the OpenCode agent is not available. Split the task into smaller file sets, install OpenCode in the Offload tray app, or do it yourself.");
        }
    }

    /// <summary>Режим rewrite: по каждому файлу модель возвращает полный новый текст или NO_CHANGES.</summary>
    private static async Task RewriteAllAsync(ToolContext ctx, LocalModel model, string spec, List<EditTarget> targets, List<GatheredFile> refs,
        string? feedback, List<string> summary, CancellationToken ct)
    {
        var system = model.SystemPrompt(
            "You apply a precise, mechanical edit to ONE file for another agent. Output ONLY the COMPLETE updated content of that file - " +
            "every line, including unchanged ones. Never abbreviate with '...', '// rest unchanged' or similar. No explanations, no code fences. " +
            $"Change only what the task requires; keep formatting and style. If this file needs no change, output exactly {OutputCleaner.NoChangesMarker}.");
        var others = string.Join(", ", targets.Select(t => t.Display));
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = targets[i];
            var fileBlock = $"FILE TO EDIT: {t.Display}\n=== {t.Display} ===\n{t.CurrentText}\n=== end of {t.Display} ===\n";
            var head = $"TASK:\n{spec}\n\nFILES IN THIS CHANGE SET (each is edited separately): {others}\n";
            var fixedTokens = Tokens.Estimate(system) + Tokens.Estimate(head) + Tokens.Estimate(fileBlock) + Tokens.Estimate(feedback);
            var maxOut = (int)Math.Min(32768, Math.Max(512, Tokens.Estimate(t.CurrentText) * 1.3 + 512));
            var refBudget = model.ContextPerSlot - fixedTokens - maxOut - 512;
            if (refBudget < 0)
            {
                maxOut = model.ContextPerSlot - fixedTokens - 256;
                refBudget = 0;
                if (maxOut < Tokens.Estimate(t.CurrentText))
                {
                    t.Note = "skipped: too large for the local context";
                    continue;
                }
            }
            var (refText, _, _) = ToolHelpers.RenderReferences(refs, refBudget);
            var user = new StringBuilder(head);
            if (refText.Length > 0) user.Append("\nREFERENCE FILES (read-only):\n").Append(refText);
            if (feedback is not null) user.Append('\n').Append(feedback).Append('\n');
            user.Append('\n').Append(fileBlock);
            user.Append($"\nOutput the complete updated content of {t.Display} (or {OutputCleaner.NoChangesMarker}).");

            var reply = await model.ChatAsync(system, user.ToString(), maxOut, $"editing {t.Display} ({i + 1}/{targets.Count})", ct).ConfigureAwait(false);
            var outcome = ApplyRewrite(t, reply.Text, reply.Truncated);
            t.Note = outcome.Note;
            if (outcome.NewText is not null)
            {
                var bytes = TextCodec.Encode(outcome.NewText, t.Format, out var fallback);
                if (fallback) t.Note += " (saved as UTF-8 with BOM: characters not representable in windows-1251)";
                JobStore.WriteBytesAtomic(t.Path, bytes);
                t.CurrentText = TextCodec.Decode(bytes).Text;
                ctx.Stats.TokensWritten += Tokens.Estimate(outcome.NewText);
                var bullet = DescribeChange(t);
                if (bullet is not null)
                {
                    summary.RemoveAll(s => s.StartsWith(t.Display + ":", StringComparison.Ordinal));
                    summary.Add(bullet);
                }
            }
        }
    }

    internal sealed record RewriteOutcome(string? NewText, string Note);

    /// <summary>Проверка ответа модели для одного файла: NO_CHANGES, обрыв, пустой ответ, «сокращения» кода.</summary>
    internal static RewriteOutcome ApplyRewrite(EditTarget t, string reply, bool truncated)
    {
        if (OutputCleaner.IsNoChanges(reply)) return new(null, "no changes needed (model)");
        if (truncated) return new(null, "skipped: model output was cut off (file too long); file left unchanged");
        var text = OutputCleaner.StripFence(ToolHelpers.StripEchoHeader(reply, t.Display), allowInnerBlock: !ToolHelpers.IsMarkdownLike(t.Display));
        var endMarker = $"=== end of {t.Display} ===";
        var idx = text.LastIndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) text = text[..idx];
        if (text.Trim().Length == 0) return new(null, "skipped: empty model output; file left unchanged");
        if (OutputCleaner.HasElision(text, t.CurrentText))
            return new(null, "skipped: model abbreviated the file ('... rest unchanged'); file left unchanged");
        var origLines = TextCodec.SplitLines(t.CurrentText).Length;
        var newLines = TextCodec.SplitLines(text).Length;
        if (origLines >= 40 && newLines < origLines / 3)
            return new(null, $"skipped: model returned only {newLines} of {origLines} lines (likely incomplete); file left unchanged");
        if (Normalize(text) == Normalize(t.CurrentText)) return new(null, "unchanged");
        return new(text, "edited");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n', ' ');

    private static string? DescribeChange(EditTarget t)
    {
        var a = TextCodec.SplitLines(t.OriginalText);
        var b = TextCodec.SplitLines(t.CurrentText);
        var edits = LineDiff.Compute(a, b);
        var (added, removed) = LineDiff.Stats(edits);
        if (added == 0 && removed == 0) return null;
        var sample = edits.Where(e => e.Op == DiffOp.Insert).Select(e => b[e.NewIndex].Trim()).FirstOrDefault(l => l.Length > 3)
                     ?? edits.Where(e => e.Op == DiffOp.Delete).Select(e => "removed: " + a[e.OldIndex].Trim()).FirstOrDefault(l => l.Length > 12);
        if (sample is not null && sample.Length > 110) sample = sample[..110] + "…";
        return $"{t.Display}: +{added} −{removed}" + (sample is null ? "" : $", e.g. `{sample}`");
    }

    /// <summary>Режим agent: OpenCode правит файлы; всё, что изменено вне списка, откатывается.</summary>
    private static async Task RunAgentAsync(ToolContext ctx, JobInfo job, string spec, List<EditTarget> targets, List<GatheredFile> refs,
        string root, string? feedback, TimeSpan deadline, List<string> summary, List<string> notes, CancellationToken ct)
    {
        var allow = new HashSet<string>(targets.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var guard = await StrayGuard.CaptureAsync(ctx, job, root, allow, ct).ConfigureAwait(false);

        var prompt = new StringBuilder();
        prompt.Append("TASK:\n").Append(spec).Append("\n\n");
        prompt.Append("You may modify ONLY these files (paths relative to the working directory):\n");
        foreach (var t in targets) prompt.Append("- ").Append(Path.GetRelativePath(root, t.Path).Replace('\\', '/')).Append('\n');
        prompt.Append("Do NOT create, delete, rename or modify any other file. Any change outside this list will be reverted automatically.\n");
        if (refs.Count > 0)
        {
            prompt.Append("Read these files for reference (do not modify them):\n");
            foreach (var r in refs.Take(40)) prompt.Append("- ").Append(r.Display).Append('\n');
        }
        prompt.Append("Make the minimal, mechanical changes the task requires; keep the existing style. Do not run long commands. ");
        prompt.Append("When done, reply with 3-5 short bullet points describing what you changed.\n");
        if (feedback is not null) prompt.Append('\n').Append(feedback).Append('\n');

        var lastProgress = DateTime.MinValue;
        OpenCodeRunResult? res = null;
        try
        {
            res = await OpenCodeRunner.RunAsync(ctx.Cfg, prompt.ToString(), root,
                new OpenCodeRunOptions(ctx.Cfg.OpenCode.AllowShellCommands, deadline, "build"),
                msg =>
                {
                    if (DateTime.UtcNow - lastProgress < TimeSpan.FromSeconds(2)) return;
                    lastProgress = DateTime.UtcNow;
                    ctx.Progress.Report("OpenCode: " + (msg.Length > 120 ? msg[..120] + "…" : msg));
                }, ct).ConfigureAwait(false);
        }
        catch (NotImplementedException)
        {
            throw new ToolException("The OpenCode agent is not available in this Offload build; use mode=rewrite for small file sets.");
        }
        finally
        {
            // Посторонние правки откатываем в любом случае (и при ошибке агента).
            var agentChanged = res?.ChangedFiles?.Select(c => ParseChanged(c, root)).Where(p => p is not null).Select(p => p!).ToList();
            var reverted = await guard.RevertStraysAsync(agentChanged, ct.IsCancellationRequested ? CancellationToken.None : ct).ConfigureAwait(false);
            notes.AddRange(reverted);
        }
        if (res is null) return;
        ctx.Stats.AddExternal(res.PromptTokens, res.CompletionTokens, res.Duration);
        if (!res.Success) notes.Add("OpenCode reported a problem: " + Short(res.Error ?? $"exit code {res.ExitCode}", 300));

        foreach (var t in targets)
        {
            if (!File.Exists(t.Path))
            {
                t.Note = "deleted by the agent";
                t.CurrentText = "";
                continue;
            }
            var (text, _) = TextCodec.Decode(JobStore.ReadAllBytesShared(t.Path));
            if (text != t.CurrentText) ctx.Stats.TokensWritten += Tokens.Estimate(text);
            t.CurrentText = text;
            t.Note = text == t.OriginalText ? "unchanged" : "edited";
        }
        summary.Clear();
        foreach (var line in (res.FinalText ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(5))
            summary.Add(Short(line.TrimStart('-', '*', '•', ' '), 200));
    }

    /// <summary>«M src/a.cs» → полный путь.</summary>
    internal static string? ParseChanged(string entry, string root)
    {
        var e = entry.Trim();
        if (e.Length > 2 && e[1] == ' ' && char.IsLetter(e[0])) e = e[2..].Trim();
        if (e.Length == 0) return null;
        try { return Path.GetFullPath(Path.IsPathFullyQualified(e) ? e : Path.Combine(root, e)); }
        catch { return null; }
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Render(JobInfo job, string status, string mode, VerifyResult? verify, int attempt, List<string> summary,
        List<string> notes, bool dryRun)
    {
        var sb = new StringBuilder();
        sb.Append($"job_id: {job.Id} · status: {status} · mode: {mode}\n");
        sb.Append(dryRun ? "files (proposed +added −removed; originals restored):\n" : "files (+added −removed):\n");
        foreach (var f in job.Files.Where(f => f.Allowlisted).Take(50))
        {
            sb.Append("  ").Append(f.Display).Append("  ");
            if (f.Added == 0 && f.Removed == 0) sb.Append(f.Note is { } n && n != "edited" ? n : "no changes");
            else
            {
                sb.Append($"+{f.Added} −{f.Removed}");
                if (f.Note is { } n2 && n2 != "edited") sb.Append(" · ").Append(n2);
            }
            sb.Append('\n');
        }
        if (verify is not null) sb.Append(verify.Summary(attempt)).Append('\n');
        if (summary.Count > 0)
        {
            sb.Append("summary:\n");
            foreach (var s in summary.Take(5)) sb.Append("- ").Append(s).Append('\n');
        }
        foreach (var n in notes.Take(8)) sb.Append("note: ").Append(Short(n, 400)).Append('\n');
        if (status != JobStatus.NoChanges)
            sb.Append($"full diff: local_job action=diff job_id={job.Id}{(dryRun ? "" : $" · undo: local_job action=revert job_id={job.Id}")}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Охрана от правок агента вне списка разрешённых файлов. В git-репозитории: снимок «грязных»/неотслеживаемых файлов до запуска,
/// после — откат по git status (новые файлы удаляются, чистые отслеживаемые восстанавливаются из HEAD, грязные — из снимка).
/// Вне git: сравнение размеров/времён изменения; новые файлы удаляются, изменённые — только сообщаются.
/// </summary>
internal sealed class StrayGuard
{
    private const int MaxGuardSnapshots = 300;
    private readonly ToolContext _ctx;
    private readonly JobInfo _job;
    private readonly string _root;
    private readonly HashSet<string> _allow;
    private string? _gitRoot;
    private Dictionary<string, string>? _statusBefore;
    private Dictionary<string, (long Size, DateTime Mtime)>? _scanBefore;

    private StrayGuard(ToolContext ctx, JobInfo job, string root, HashSet<string> allow)
    {
        _ctx = ctx;
        _job = job;
        _root = root;
        _allow = allow;
    }

    public static async Task<StrayGuard> CaptureAsync(ToolContext ctx, JobInfo job, string root, HashSet<string> allow, CancellationToken ct)
    {
        var g = new StrayGuard(ctx, job, root, allow);
        g._gitRoot = Git.Executable is null ? null : Git.FindWorkTreeRoot(root);
        if (g._gitRoot is not null)
        {
            g._statusBefore = await StatusAsync(g._gitRoot, ct).ConfigureAwait(false);
            if (g._statusBefore is not null)
            {
                var n = 0;
                foreach (var (path, _) in g._statusBefore)
                {
                    if (allow.Contains(path) || !File.Exists(path)) continue;
                    if (++n > MaxGuardSnapshots) break;
                    try { JobStore.Snapshot(job, path, PathGuard.Display(path, ctx.Roots), allowlisted: false); }
                    catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException) { }
                }
                return g;
            }
            g._gitRoot = null;
        }
        g._scanBefore = Scan(root);
        return g;
    }

    /// <summary>Откатить посторонние изменения; возвращает заметки для результата.</summary>
    public async Task<List<string>> RevertStraysAsync(IReadOnlyCollection<string>? agentChanged, CancellationToken ct)
    {
        var reverted = new List<string>();
        var unrevertable = new List<string>();
        var foreign = new List<string>();
        // Если агент сообщил свой список изменённых файлов — прочие изменения сделал кто-то другой (IDE работает параллельно): не трогаем.
        var trustAgent = agentChanged is { Count: > 0 };
        var agentSet = new HashSet<string>(agentChanged ?? [], StringComparer.OrdinalIgnoreCase);
        bool NotOurs(string path) => trustAgent && !agentSet.Contains(path);
        try
        {
            if (_gitRoot is not null && _statusBefore is not null)
            {
                var after = await StatusAsync(_gitRoot, ct).ConfigureAwait(false) ?? [];
                var paths = after.Keys.Union(_statusBefore.Keys, StringComparer.OrdinalIgnoreCase).Where(p => !_allow.Contains(p)).ToList();
                foreach (var path in paths)
                {
                    var display = PathGuard.Display(path, _ctx.Roots);
                    if (NotOurs(path))
                    {
                        _statusBefore.TryGetValue(path, out var b0);
                        after.TryGetValue(path, out var a0);
                        if (b0 != a0) foreign.Add(display);
                        continue;
                    }
                    var guard = _job.Files.FirstOrDefault(f => !f.Allowlisted && f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    var wasDirty = _statusBefore.TryGetValue(path, out var xyBefore);
                    after.TryGetValue(path, out var xyAfter);
                    if (wasDirty)
                    {
                        if (guard?.Snapshot is null)
                        {
                            if (xyBefore != xyAfter) unrevertable.Add(display);
                            continue;
                        }
                        var orig = JobStore.ReadSnapshot(_job, guard)!;
                        if (File.Exists(path) && JobStore.Sha(JobStore.ReadAllBytesShared(path)) == guard.OriginalSha256) continue;
                        JobStore.WriteBytesAtomic(path, orig);
                        reverted.Add(display);
                    }
                    else if (xyAfter == "??")
                    {
                        if (File.Exists(path)) File.Delete(path);
                        reverted.Add(display + " (new file deleted)");
                    }
                    else if (xyAfter is not null)
                    {
                        var rel = Path.GetRelativePath(_gitRoot, path).Replace('\\', '/');
                        var r = await Git.RunAsync(_gitRoot, ["checkout", "HEAD", "--", rel], ct, maxChars: 1000).ConfigureAwait(false);
                        if (r.Success) reverted.Add(display);
                        else unrevertable.Add(display);
                    }
                }
            }
            else if (_scanBefore is not null)
            {
                var after = Scan(_root);
                foreach (var (path, info) in after)
                {
                    if (_allow.Contains(path)) continue;
                    if (NotOurs(path))
                    {
                        if (!_scanBefore.TryGetValue(path, out var b1) || b1 != info) foreign.Add(PathGuard.Display(path, _ctx.Roots));
                        continue;
                    }
                    if (!_scanBefore.TryGetValue(path, out var before))
                    {
                        try { File.Delete(path); reverted.Add(PathGuard.Display(path, _ctx.Roots) + " (new file deleted)"); }
                        catch { unrevertable.Add(PathGuard.Display(path, _ctx.Roots)); }
                    }
                    else if (before != info)
                    {
                        unrevertable.Add(PathGuard.Display(path, _ctx.Roots));
                    }
                }
                foreach (var path in _scanBefore.Keys.Where(p => !_allow.Contains(p) && !after.ContainsKey(p)))
                    unrevertable.Add(PathGuard.Display(path, _ctx.Roots) + " (deleted)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
        {
            Log.Warn("mcp", $"Откат посторонних правок: {ex.Message}");
            unrevertable.Add("(check failed: " + ex.Message + ")");
        }
        var notes = new List<string>();
        if (reverted.Count > 0) notes.Add($"reverted changes outside the allowed files: {string.Join(", ", reverted.Take(15))}");
        if (foreign.Count > 0) notes.Add($"files changed during the job but not by the agent (left as is): {string.Join(", ", foreign.Take(10))}");
        if (unrevertable.Count > 0) notes.Add($"WARNING: files outside the allowed list were changed and could not be reverted automatically: {string.Join(", ", unrevertable.Take(15))}");
        return notes;
    }

    /// <summary>git status --porcelain=v1 -z: полный путь → код XY. null — git не сработал.</summary>
    internal static async Task<Dictionary<string, string>?> StatusAsync(string gitRoot, CancellationToken ct)
    {
        var res = await Git.RunAsync(gitRoot, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct, maxChars: 8_000_000,
            timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!res.Success) return null;
        return ParsePorcelain(res.StdOut, gitRoot);
    }

    internal static Dictionary<string, string> ParsePorcelain(string output, string gitRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = output.TrimEnd('\n').Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i].TrimStart('\n');
            if (e.Length < 4) continue;
            var xy = e[..2];
            var path = e[3..];
            try { map[Path.GetFullPath(Path.Combine(gitRoot, path))] = xy; } catch { }
            // Переименование/копирование: следующий элемент — исходный путь.
            if (xy[0] is 'R' or 'C' || xy[1] is 'R' or 'C')
            {
                i++;
                if (i < entries.Length && entries[i].Length > 0)
                {
                    try { map[Path.GetFullPath(Path.Combine(gitRoot, entries[i]))] = xy; } catch { }
                }
            }
        }
        return map;
    }

    private static Dictionary<string, (long, DateTime)> Scan(string root)
    {
        var map = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited < 20_000)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    visited++;
                    if (e is DirectoryInfo d)
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0 || FileGatherer.IgnoredDirs.Contains(d.Name)) continue;
                        stack.Push(d.FullName);
                    }
                    else if (e is FileInfo f)
                    {
                        map[f.FullName] = (f.Length, f.LastWriteTimeUtc);
                    }
                }
            }
            catch
            {
                // Нет доступа — пропускаем папку.
            }
        }
        return map;
    }
}
