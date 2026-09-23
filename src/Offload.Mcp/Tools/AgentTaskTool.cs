using System.Text;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Mcp.Infrastructure;
using Offload.OpenCode;

namespace Offload.Mcp.Tools;

/// <summary>Параметры задачи агенту (local_agent_task / local_solve / local_refactor) — включая «бюджет автономности».</summary>
internal sealed record AgentTaskRequest
{
    public required string Task { get; init; }
    public string? VerifyCommand { get; init; }
    public string[]? ContextPaths { get; init; }
    public string Merge { get; init; } = "apply";
    public int FixAttempts { get; init; } = 2;
    public int TimeoutMinutes { get; init; } = 20;
    public bool Background { get; init; }

    /// <summary>Пути/маски, которые агенту разрешено менять (остальные изменения не переносятся). null — весь проект.</summary>
    public string[]? AllowedPaths { get; init; }

    /// <summary>Не вливать автоматически, если изменено больше файлов.</summary>
    public int MaxFiles { get; init; }

    /// <summary>Перед слиянием — ревью изменений песочницы локальной моделью; critical/high — не вливать автоматически.</summary>
    public bool Review { get; init; }

    /// <summary>Инструмент-владелец задачи (для job.json и статистики).</summary>
    public string Tool { get; init; } = McpToolNames.AgentTask;

    /// <summary>Дополнительный текст для итогового отчёта (например, найденный контекст local_solve).</summary>
    public string? Preamble { get; init; }
}

/// <summary>
/// local_agent_task: локальный агент OpenCode выполняет задачу программирования в git-песочнице (worktree на ветке offload/&lt;job&gt;),
/// проверяет её командой, коммитит в свою ветку, по желанию проходит ревью локальной моделью, а результат вливается в проект:
/// патчем в рабочее дерево (apply), fast-forward коммитом (commit) или остаётся на ревью (none). Может выполняться в фоне.
/// Итог — «доказательство результата»: изменённые файлы, проверка, диагностика, находки ревью, нерешённые риски.
/// </summary>
internal static class AgentTaskTool
{
    public static Task<string> RunAsync(ToolContext ctx, string? task, string? verifyCommand, string[]? contextPaths, string? merge,
        int fixAttempts, int timeoutMinutes, bool background) =>
        RunAsync(ctx, new AgentTaskRequest
        {
            Task = task ?? "",
            VerifyCommand = verifyCommand,
            ContextPaths = contextPaths,
            Merge = merge ?? "apply",
            FixAttempts = fixAttempts,
            TimeoutMinutes = timeoutMinutes,
            Background = background,
        });

    public static async Task<string> RunAsync(ToolContext ctx, AgentTaskRequest req)
    {
        var spec = ToolHelpers.RequireText(req.Task, "task", 16000);
        var mergeMode = (req.Merge ?? "apply").Trim().ToLowerInvariant();
        if (mergeMode is not ("apply" or "commit" or "none")) throw new ToolException("merge must be apply, commit or none.");
        var verify = string.IsNullOrWhiteSpace(req.VerifyCommand) ? null : VerifyCommand.Validate(req.VerifyCommand, ctx.Cfg.Mcp.VerifyCommandAllowlist);
        if (!ctx.Cfg.OpenCode.Enabled || StatusTool.FindOpenCode(ctx.Cfg) is null)
            throw new ToolException("This needs the OpenCode agent: install/enable it in the Offload tray app (OpenCode tab). " +
                                    "For small mechanical edits use local_edit_files mode=rewrite or local_apply_patch.");
        if (Git.Executable is null) throw new ToolException("The git sandbox needs git: install Git for Windows and restart the IDE.");
        var allowed = CompileAllowed(req.AllowedPaths);
        var r = req with
        {
            Task = spec,
            VerifyCommand = verify,
            Merge = mergeMode,
            FixAttempts = Math.Clamp(req.FixAttempts, 0, 4),
            TimeoutMinutes = Math.Clamp(req.TimeoutMinutes <= 0 ? 20 : req.TimeoutMinutes, 2, 90),
            MaxFiles = Math.Max(0, req.MaxFiles),
        };
        var hints = ContextHints(req.ContextPaths);
        // Песочница снимает всю рабочую папку: корень диска или профиль пользователя не годятся (WriteRoots бросает понятную ошибку).
        var root = Workspace.WriteRoots(ctx.Roots)[0];

        // Сервер модели поднимаем сразу: ошибки конфигурации видны в ответе, а не в фоне.
        await ctx.GetModelAsync().ConfigureAwait(false);

        var job = JobStore.Create(r.Tool, root, spec, ctx.ToolUseId);
        job.Mode = "sandbox/" + mergeMode;
        job.VerifyCommand = verify;
        JobStore.Save(job);

        if (!r.Background) return await ExecuteAsync(ctx, job, r, hints, allowed).ConfigureAwait(false);

        var progress = new ProgressReporter(null, null);
        var cts = new CancellationTokenSource();
        var bg = ctx.ForBackground(progress, cts.Token);
        BackgroundJobs.Start(job.Id, progress, cts, async () =>
        {
            var ok = false;
            var text = "";
            try
            {
                text = await ExecuteAsync(bg, job, r, hints, allowed).ConfigureAwait(false);
                ok = true;
                return text;
            }
            finally
            {
                if (bg.ModelIfUsed is not null && bg.Stats.ModelCalls > 0) ToolRunner.RecordUsage(bg, ok, text);
            }
        });
        return $"job_id: {job.Id} · status: running in the background (sandbox branch {GitSandbox.BranchPrefix}{job.Id})\n" +
               "The local agent works in an isolated git worktree; you can keep working meanwhile.\n" +
               $"Check: local_job action=status job_id={job.Id} wait_seconds=120 · cancel: local_job action=cancel job_id={job.Id}";
    }

    private static async Task<string> ExecuteAsync(ToolContext ctx, JobInfo job, AgentTaskRequest r, List<string> hints, List<Regex>? allowed)
    {
        var deadline = TimeSpan.FromMinutes(r.TimeoutMinutes);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        deadlineCts.CancelAfter(deadline);
        var ct = deadlineCts.Token;
        var summary = new List<string>();
        var notes = new List<string>();
        VerifyResult? lastVerify = null;
        var attempt = 0;
        SandboxInfo? sb = null;
        SandboxCommit? commit = null;
        try
        {
            ctx.Progress.Report("Creating the git sandbox (snapshot of the working tree)…");
            sb = await GitSandbox.CreateAsync(job.Root, job.Id, JobStore.DirOf(job.Id), ctx.Cfg.Mcp.SecretFilePatterns, ct).ConfigureAwait(false);
            job.Sandbox = sb;
            JobStore.Save(job);

            await RunAgentAsync(ctx, sb, r, hints, null, deadline, summary, notes, ct).ConfigureAwait(false);
            while (r.VerifyCommand is not null && await GitSandbox.IsDirtyAsync(sb, ct).ConfigureAwait(false))
            {
                attempt++;
                lastVerify = await VerifyCommand.RunAsync(r.VerifyCommand, sb.AgentDir, deadline, ctx.Progress, ct).ConfigureAwait(false);
                if (lastVerify.Passed || attempt > r.FixAttempts) break;
                ctx.Progress.Report($"verify failed (exit {lastVerify.ExitCode}); fix round {attempt}/{r.FixAttempts}");
                var feedback = $"The check `{r.VerifyCommand}` FAILED (exit {lastVerify.ExitCode}) after your changes. Relevant output:\n" +
                               $"{lastVerify.ForModel(5000)}\nFix the problem while still fulfilling the task.";
                await RunAgentAsync(ctx, sb, r, hints, feedback, deadline, summary, notes, ct).ConfigureAwait(false);
            }

            ctx.Progress.Report("Committing the agent's work in the sandbox…");
            commit = await CommitAsync(ctx, job, sb, r.Task, allowed, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ctx.Ct.IsCancellationRequested)
        {
            // Таймаут: фиксируем то, что агент успел сделать, чтобы это можно было посмотреть и влить вручную.
            var kept = false;
            if (sb is not null)
            {
                try
                {
                    using var saveCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    kept = (await CommitAsync(ctx, job, sb, r.Task, allowed, saveCts.Token).ConfigureAwait(false)).HasChanges;
                }
                catch (Exception ex) when (ex is ToolException or OperationCanceledException)
                {
                    notes.Add("could not save partial work: " + ex.Message);
                }
            }
            Finish(job, sb, kept ? JobStatus.PendingMerge : JobStatus.Failed, lastVerify, attempt, summary, notes);
            if (!kept && sb is not null) await DiscardAsync(job, sb).ConfigureAwait(false);
            throw new ToolException($"The agent hit timeout_minutes={deadline.TotalMinutes:0}." + (kept
                ? $" Partial work is kept on branch {sb!.Branch}: review with local_job action=diff job_id={job.Id}, then merge or discard it."
                : " Nothing was changed in your project."));
        }
        catch (OperationCanceledException)
        {
            Finish(job, sb, JobStatus.Cancelled, lastVerify, attempt, summary, notes);
            if (sb is not null) await DiscardAsync(job, sb).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException)
        {
            Finish(job, sb, JobStatus.Failed, lastVerify, attempt, summary, notes);
            if (sb is not null) await DiscardAsync(job, sb).ConfigureAwait(false);
            throw new ToolException(ex.Message + " Nothing was changed in your project.");
        }
        catch (Exception ex)
        {
            // Непредвиденная ошибка (диск, процесс): задача не должна остаться «running» с открытой песочницей.
            notes.Add($"internal error: {ex.GetType().Name}: {ex.Message}");
            Finish(job, sb, JobStatus.Failed, lastVerify, attempt, summary, notes);
            if (sb is not null) await DiscardAsync(job, sb).ConfigureAwait(false);
            throw;
        }

        ArgumentNullException.ThrowIfNull(sb);
        try
        {
            return await FinalizeAsync(ctx, job, r, sb, commit!, lastVerify, attempt, summary, notes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && job.Status is not (JobStatus.Failed or JobStatus.Applied or JobStatus.Committed))
        {
            // Ошибка после фиксации работы агента: песочница сохраняется для ручного решения.
            notes.Add($"finishing failed: {ex.Message}");
            Finish(job, sb, JobStatus.PendingMerge, lastVerify, attempt, summary, notes);
            throw new ToolException($"{ex.Message} The agent's work is kept on branch {sb.Branch}: local_job action=diff job_id={job.Id}, then action=merge or action=discard.");
        }
    }

    private static async Task<string> FinalizeAsync(ToolContext ctx, JobInfo job, AgentTaskRequest r, SandboxInfo sb, SandboxCommit commit, VerifyResult? lastVerify,
        int attempt, List<string> summary, List<string> notes)
    {
        notes.AddRange(commit.Dropped.Select(d => "not transferred: " + d));
        var changes = commit.HasChanges ? await GitSandbox.ChangesAsync(sb, ctx.Ct).ConfigureAwait(false) : [];
        ctx.Stats.TokensWritten += changes.Sum(c => (long)c.Added) * 10;
        var proof = new Proof(r, changes, lastVerify, attempt, summary, notes);
        if (changes.Count == 0)
        {
            Finish(job, sb, JobStatus.NoChanges, lastVerify, attempt, summary, notes);
            await DiscardAsync(job, sb).ConfigureAwait(false);
            return proof.Render(job, "the agent made no transferable changes; nothing to merge");
        }

        // Ревью песочницы локальной моделью (maker/checker) до слияния.
        if (r.Review)
        {
            ctx.Progress.Report("Reviewing the agent's changes…");
            proof.Review = await ReviewSandboxAsync(ctx, sb, r.Task).ConfigureAwait(false);
        }

        var blockers = new List<string>();
        if (lastVerify is { Passed: false }) blockers.Add("the check failed");
        if (r.MaxFiles > 0 && changes.Count > r.MaxFiles) blockers.Add($"{changes.Count} files changed (max_files={r.MaxFiles})");
        if (proof.Review.Any(f => f.StartsWith("[critical]", StringComparison.OrdinalIgnoreCase) || f.StartsWith("[high]", StringComparison.OrdinalIgnoreCase)))
            blockers.Add("the review found critical/high issues");
        if (r.Merge == "none" || blockers.Count > 0)
        {
            if (blockers.Count > 0) notes.Add("not merged automatically: " + string.Join("; ", blockers));
            Finish(job, sb, JobStatus.PendingMerge, lastVerify, attempt, summary, notes);
            return proof.Render(job,
                $"waiting for review on branch {sb.Branch}: local_job action=diff job_id={job.Id}; then action=merge (commit=true for a git commit) or action=discard");
        }

        var merged = await MergeAsync(ctx, job, r.Merge == "commit", ctx.Ct).ConfigureAwait(false);
        job.VerifySummary = lastVerify?.Summary(attempt, 20, 1500);
        job.Summary = string.Join("\n", summary.Take(5));
        JobStore.Save(job);
        notes.AddRange(job.Notes.Where(n => !notes.Contains(n)));
        return proof.Render(job, merged);
    }

    /// <summary>Сводка-доказательство: что изменено, прошла ли проверка, что нашло ревью, что осталось нерешённым.</summary>
    private sealed class Proof(AgentTaskRequest r, List<SandboxChange> changes, VerifyResult? verify, int attempt, List<string> summary, List<string> notes)
    {
        public List<string> Review { get; set; } = [];

        public string Render(JobInfo job, string outcome)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(r.Preamble)) sb.Append(r.Preamble.TrimEnd()).Append("\n\n");
            sb.Append($"job_id: {job.Id} · status: {job.Status} · {outcome}\n");
            if (changes.Count > 0)
            {
                sb.Append($"changed files ({changes.Count}, +{changes.Sum(c => c.Added)} −{changes.Sum(c => c.Removed)}):\n");
                foreach (var c in changes.Take(60))
                    sb.Append("  ").Append(c.Path).Append("  ").Append(c.Binary ? "binary" : $"+{c.Added} −{c.Removed}").Append('\n');
                if (changes.Count > 60) sb.Append($"  … and {changes.Count - 60} more\n");
            }
            sb.Append(verify is not null ? verify.Summary(attempt) : r.VerifyCommand is null ? "verify: none requested (unverified!)" : "verify: not run (no changes)").Append('\n');
            if (verify is { Passed: false })
            {
                var diags = DiagnosticParser.Parse(verify.Tail).Where(d => d.Severity == "error").Take(8).ToList();
                if (diags.Count > 0)
                {
                    sb.Append("diagnostics:\n");
                    foreach (var d in diags) sb.Append($"  {d.File}:{d.Line} {d.Code} {Short(d.Message, 160)}\n");
                }
            }
            if (Review.Count > 0)
            {
                sb.Append("local review of the change:\n");
                foreach (var f in Review.Take(10)) sb.Append("  ").Append(Short(f, 240)).Append('\n');
            }
            if (summary.Count > 0)
            {
                sb.Append("agent summary:\n");
                foreach (var s in summary.Take(8)) sb.Append("- ").Append(s).Append('\n');
            }
            var risks = notes.Distinct().ToList();
            if (verify is null && r.VerifyCommand is null && changes.Count > 0) risks.Add("changes were not verified by any command");
            if (risks.Count > 0)
            {
                sb.Append("unresolved / notes:\n");
                foreach (var n in risks.Take(12)) sb.Append("- ").Append(Short(n, 400)).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Ревью diff песочницы локальной моделью: строки «[severity] path:line - issue - suggestion».</summary>
    private static async Task<List<string>> ReviewSandboxAsync(ToolContext ctx, SandboxInfo sb, string task)
    {
        try
        {
            var model = await ctx.GetModelAsync().ConfigureAwait(false);
            var system = model.SystemPrompt(
                "You review a change made by another agent for the task below. Report only real problems: bugs, logic errors, missing parts of the task, " +
                "broken error handling, security issues, tests that do not test anything. One finding per line: [critical|high|medium|low] path:line - issue - fix. " +
                "If the change is fine, output exactly: NO ISSUES");
            var budget = model.MaterialBudget(600, system, task);
            var diff = await GitSandbox.DiffTextAsync(sb, Math.Max(2000, budget * 3), null, ctx.Ct).ConfigureAwait(false);
            await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
            var reply = await model.ChatAsync(system, "TASK:\n" + task + "\n\nDIFF:\n" + diff, 600, "reviewing the change", ctx.Ct).ConfigureAwait(false);
            if (reply.Text.Contains("NO ISSUES", StringComparison.OrdinalIgnoreCase)) return ["no issues found"];
            return reply.Text.Split('\n').Select(l => l.Trim().TrimStart('-', '*', ' ')).Where(l => l.StartsWith('[')).Take(12).ToList();
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException)
        {
            return ["review skipped: " + ex.Message];
        }
    }

    /// <summary>Зафиксировать работу агента; в проект разрешено переносить только то, что прошло бы проверку записи и allowed_paths.</summary>
    private static async Task<SandboxCommit> CommitAsync(ToolContext ctx, JobInfo job, SandboxInfo sb, string spec, List<Regex>? allowed, CancellationToken ct)
    {
        var first = spec.Split('\n', 2)[0].Trim();
        var result = await GitSandbox.CommitAsync(sb, "offload: " + first, p => WhyNotWritable(ctx, sb, p, allowed), ctx.Cfg.Mcp.SecretFilePatterns, ct)
            .ConfigureAwait(false);
        JobStore.Save(job);
        return result;
    }

    private static string? WhyNotWritable(ToolContext ctx, SandboxInfo sb, string gitPath, List<Regex>? allowed)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(sb.RepoRoot, gitPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!ctx.Roots.Any(r => PathGuard.IsInside(full, r))) return "outside the workspace";
            if (allowed is not null)
            {
                var rel = Path.GetRelativePath(ctx.Roots[0], full).Replace('\\', '/');
                if (!allowed.Any(a => a.IsMatch(rel))) return "outside allowed_paths";
            }
            ctx.ResolveWrite(full);
            return null;
        }
        catch (ToolException ex)
        {
            return ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "invalid path";
        }
    }

    /// <summary>allowed_paths: папка («src/Auth»), файл или маска («src/**/*.cs») относительно корня проекта.</summary>
    internal static List<Regex>? CompileAllowed(string[]? allowedPaths)
    {
        var list = (allowedPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().Replace('\\', '/')).ToList();
        if (list.Count == 0) return null;
        var result = new List<Regex>();
        foreach (var raw in list.Take(50))
        {
            if (raw.Split('/').Any(s => s == "..") || raw.Contains(':') || raw.StartsWith('/'))
                throw new ToolException($"allowed_paths entry '{raw}' must be relative to the project root.");
            var p = raw;
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
            if (p is "" or ".") throw new ToolException("allowed_paths entry '.' allows everything; omit allowed_paths instead.");
            result.Add(Glob.HasWildcards(p) ? Glob.ToRegex(p)
                : new Regex("^" + Regex.Escape(p.TrimEnd('/')) + "(/.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
        return result;
    }

    /// <summary>
    /// Влить ветку песочницы в проект: commit=true — fast-forward (если возможно, иначе патч), иначе патч в рабочее дерево.
    /// Перед записью — снимки затронутых файлов в задаче (local_job revert). Возвращает строку об итоге.
    /// </summary>
    internal static async Task<string> MergeAsync(ToolContext ctx, JobInfo job, bool commit, CancellationToken ct)
    {
        var sb = job.Sandbox ?? throw new ToolException($"Job {job.Id} has no sandbox to merge.");
        if (!SandboxState.IsOpen(sb.State)) throw new ToolException($"Job {job.Id} sandbox is already {sb.State}.");
        if (!Directory.Exists(sb.WorktreeDir) && sb.HeadCommit is null) throw new ToolException($"Job {job.Id} sandbox no longer exists.");
        var changes = await GitSandbox.ChangesAsync(sb, ct).ConfigureAwait(false);
        if (changes.Count == 0) throw new ToolException($"Job {job.Id} has no changes to merge.");

        // Каждый путь заново проходит проверку записи (конфигурация или корни могли измениться).
        var targets = new List<(string Canonical, string Display)>();
        foreach (var c in changes)
        {
            var full = Path.GetFullPath(Path.Combine(sb.RepoRoot, c.Path.Replace('/', Path.DirectorySeparatorChar)));
            var (_, canonical) = ctx.ResolveWrite(full);
            targets.Add((canonical, ctx.Display(canonical)));
        }

        if (commit)
        {
            var ffError = sb.ShadowGitDir is not null ? "the project is not a git repository"
                : !sb.BaseIsHead ? "the working tree had uncommitted changes when the task started" : null;
            if (ffError is null)
            {
                var snapshotsBefore = job.Files.Count;
                foreach (var (canonical, display) in targets) JobStore.Snapshot(job, canonical, display);
                ffError = await GitSandbox.FastForwardAsync(sb, ct).ConfigureAwait(false);
                // Не получилось — снимки этой попытки убираем: иначе повторное слияние (после ручных правок) откатывалось бы к ним.
                if (ffError is not null) job.Files.RemoveRange(snapshotsBefore, job.Files.Count - snapshotsBefore);
                if (ffError is null)
                {
                    sb.State = SandboxState.Merged;
                    JobStore.Finish(job, JobStatus.Committed);
                    await GitSandbox.RemoveAsync(sb, deleteBranch: true, ct).ConfigureAwait(false);
                    JobStore.Save(job);
                    return $"merged: fast-forwarded your current branch to the agent's commit {Short(sb.HeadCommit, 10)} (undo: git reset --keep {Short(sb.OriginalHead, 10)})";
                }
            }
            job.Notes.Add($"git commit merge not possible ({ffError}); applied as uncommitted changes instead");
        }

        var patch = Path.Combine(JobStore.DirOf(job.Id), "changes.patch");
        await GitSandbox.WritePatchAsync(sb, patch, ct).ConfigureAwait(false);
        var conflict = await GitSandbox.CheckApplyAsync(sb, patch, ct).ConfigureAwait(false);
        if (conflict is not null)
        {
            sb.State = SandboxState.Conflict;
            job.Status = JobStatus.Conflict;
            job.Notes.Add("patch does not apply: " + conflict.Replace('\n', ' '));
            JobStore.Save(job);
            return $"NOT merged: the agent's patch conflicts with the current files:\n{conflict}\n" +
                   $"The work is kept on branch {sb.Branch}. Review with local_job action=diff job_id={job.Id}; resolve the listed files and " +
                   "retry local_job action=merge, or apply the changes yourself, or local_job action=discard.";
        }
        foreach (var (canonical, display) in targets) JobStore.Snapshot(job, canonical, display);
        var error = await GitSandbox.ApplyAsync(sb, patch, ct).ConfigureAwait(false);
        if (error is not null)
        {
            JobStore.Finish(job, JobStatus.Failed);
            throw new ToolException($"git apply failed after a successful check: {error}. Undo partial changes with local_job action=revert job_id={job.Id}.");
        }
        sb.State = SandboxState.Merged;
        JobStore.Finish(job, JobStatus.Applied);
        await GitSandbox.RemoveAsync(sb, deleteBranch: true, ct).ConfigureAwait(false);
        JobStore.Save(job);
        return "merged: applied to your working tree as uncommitted changes (review with git diff; undo: local_job action=revert job_id=" + job.Id + ")";
    }

    internal static async Task DiscardAsync(JobInfo job, SandboxInfo sb)
    {
        await GitSandbox.RemoveAsync(sb, deleteBranch: true, CancellationToken.None).ConfigureAwait(false);
        sb.State = SandboxState.Discarded;
        JobStore.Save(job);
    }

    private static async Task RunAgentAsync(ToolContext ctx, SandboxInfo sb, AgentTaskRequest r, List<string> hints, string? feedback, TimeSpan deadline,
        List<string> summary, List<string> notes, CancellationToken ct)
    {
        var prompt = new StringBuilder();
        prompt.Append("TASK:\n").Append(r.Task).Append("\n\n");
        prompt.Append("You work in an isolated copy (git worktree) of the project; the working directory is the project root. ");
        prompt.Append("Your changes are reviewed and merged back automatically, so implement the task completely: create, modify or delete whatever files it needs.\n");
        if (r.AllowedPaths is { Length: > 0 })
            prompt.Append("Change ONLY files under: ").Append(string.Join(", ", r.AllowedPaths)).Append(" (changes elsewhere are discarded).\n");
        if (r.MaxFiles > 0) prompt.Append($"Keep the change small: at most {r.MaxFiles} files.\n");
        prompt.Append("Rules: do NOT run git commands (Offload commits your work itself). Secret files (.env, keys) are not present - do not create them. ");
        prompt.Append(sb.LinkedDirs.Count > 0
            ? $"Dependency folders ({string.Join(", ", sb.LinkedDirs)}) are linked from the real project: never modify them or install packages. Build output folders (bin, obj) start empty.\n"
            : "Dependency and build folders ignored by git (node_modules, bin, obj, .venv) are not copied.\n");
        if (r.VerifyCommand is not null) prompt.Append($"Your work is accepted only if `{r.VerifyCommand}` passes.\n");
        if (hints.Count > 0)
        {
            prompt.Append("Start by reading these files/folders:\n");
            foreach (var h in hints) prompt.Append("- ").Append(h).Append('\n');
        }
        if (feedback is not null) prompt.Append('\n').Append(feedback).Append('\n');

        var lastProgress = DateTime.MinValue;
        OpenCodeRunResult res;
        await using (await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ct).ConfigureAwait(false))
        {
            try
            {
                res = await OpenCodeRunner.RunAsync(ctx.Cfg, prompt.ToString(), sb.AgentDir,
                    new OpenCodeRunOptions(ctx.Cfg.OpenCode.AllowShellCommands, deadline, "build"),
                    msg =>
                    {
                        if (DateTime.UtcNow - lastProgress < TimeSpan.FromSeconds(2)) return;
                        lastProgress = DateTime.UtcNow;
                        ctx.Progress.Report("agent: " + (msg.Length > 120 ? msg[..120] + "…" : msg));
                    }, ct).ConfigureAwait(false);
            }
            catch (NotImplementedException)
            {
                throw new ToolException("The OpenCode agent is not available in this Offload build.");
            }
        }
        ct.ThrowIfCancellationRequested();
        ctx.Stats.AddExternal(res.PromptTokens, res.CompletionTokens, res.Duration);
        if (!res.Success) notes.Add("agent reported a problem: " + Short(res.Error ?? $"exit code {res.ExitCode}", 300));
        summary.Clear();
        foreach (var line in (res.FinalText ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(8))
            summary.Add(Short(line.TrimStart('-', '*', '•', ' '), 200));
    }

    /// <summary>context_paths — только подсказки агенту, что прочитать: относительные пути/маски внутри проекта.</summary>
    internal static List<string> ContextHints(string[]? contextPaths)
    {
        var list = new List<string>();
        foreach (var raw in (contextPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(40))
        {
            var p = raw.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(p) || p.Contains(':') || p.Split('/').Any(s => s == ".."))
                throw new ToolException($"context_paths entry '{raw}' must be relative to the project root (no drive letters or '..').");
            list.Add(p);
        }
        return list;
    }

    private static void Finish(JobInfo job, SandboxInfo? sb, string status, VerifyResult? verify, int attempt, List<string> summary, List<string> notes)
    {
        job.Status = status;
        job.FinishedUtc = DateTime.UtcNow;
        job.VerifySummary = verify?.Summary(attempt, 20, 1500);
        job.Summary = string.Join("\n", summary.Take(5));
        job.Notes.AddRange(notes.Where(n => !job.Notes.Contains(n)));
        job.Sandbox = sb;
        JobStore.Save(job);
    }

    private static string Short(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";
}
