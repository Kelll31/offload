using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_job: list / status / diff / revert задач записи и правки; merge / discard / cancel / retry песочниц local_agent_task.
/// Фоновые задачи могут выполняться в другом процессе (трей, другая IDE) — состояние берётся из папки задачи.
/// </summary>
internal static class JobTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? jobId, string? action, int maxLines, string[]? paths, bool force,
        bool commit, int waitSeconds)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();
        if (act == "list") return List(ctx);

        var id = (jobId ?? "").Trim();
        if (id.Length == 0) throw new ToolException("job_id is required for action=" + (act.Length == 0 ? "?" : act) + " (use action=list to find it).");
        // Задача «running», чей процесс-хозяин исчез (закрыли IDE или трей), сразу становится interrupted.
        var job = BackgroundJobs.Reconcile(JobStore.Load(id));
        switch (act)
        {
            case "status":
                if (waitSeconds > 0 && BackgroundJobs.IsActive(job))
                {
                    ctx.Progress.Report($"waiting for job {job.Id}…");
                    await BackgroundJobs.WaitAsync(job, TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 600)), ctx.Ct).ConfigureAwait(false);
                    job = BackgroundJobs.Reconcile(JobStore.Load(id));
                }
                return Describe(job);
            case "diff":
                RequireSameWorkspace(ctx, job, "diff");
                maxLines = Math.Clamp(maxLines <= 0 ? 300 : maxLines, 10, 5000);
                var header = $"job {job.Id} · {job.Tool} · status {job.Status}\n";
                if (job.Sandbox is { } open && SandboxState.IsOpen(open.State) && job.Status != JobStatus.Applied)
                {
                    var text = await GitSandbox.DiffTextAsync(open, ctx.MaxResponseChars - header.Length - 300, paths, ctx.Ct).ConfigureAwait(false);
                    return header + CapLines(text, maxLines);
                }
                return header + JobStore.Diff(job, maxLines, ctx.MaxResponseChars - header.Length - 300, paths);
            case "revert":
                RequireSameWorkspace(ctx, job, "revert");
                var outcome = JobStore.Revert(job, force);
                if (!outcome.Ok) throw new ToolException(outcome.Message);
                return outcome.Message;
            case "merge":
                RequireSameWorkspace(ctx, job, "merge");
                RequireIdleSandbox(job, "merge");
                // allow_build_files, заданный при создании задачи, действует и на её отложенное слияние.
                var merged = await PathGuard.WithJobBuildFilePolicy(job, () => AgentTaskTool.MergeAsync(ctx, job, commit, ctx.Ct)).ConfigureAwait(false);
                return $"job {job.Id} · status {job.Status}\n{merged}";
            case "discard":
                RequireSameWorkspace(ctx, job, "discard");
                RequireIdleSandbox(job, "discard");
                if (job.Sandbox is not { } sb || !SandboxState.IsOpen(sb.State))
                    throw new ToolException($"Job {job.Id} has no open sandbox (status {job.Status}); to undo applied changes use action=revert.");
                await AgentTaskTool.DiscardAsync(job, sb).ConfigureAwait(false);
                job.Status = JobStatus.Discarded;
                job.FinishedUtc ??= DateTime.UtcNow;
                JobStore.Save(job);
                return $"Discarded job {job.Id}: sandbox and branch {sb.Branch} removed; your project was not changed.";
            case "cancel":
                RequireSameWorkspace(ctx, job, "cancel");
                if (BackgroundJobs.Cancel(job.Id))
                    return $"Cancellation requested for job {job.Id}; the sandbox is discarded and your project stays unchanged. Check with action=status.";
                if (BackgroundJobs.RequestCancel(job))
                    return $"Cancellation requested for job {job.Id} (it runs in {HostName(job.Host)}); it stops within a few seconds, the sandbox is discarded " +
                           "and your project stays unchanged. Check with action=status wait_seconds=30.";
                return $"Job {job.Id} is not running (status {job.Status}).";
            case "retry":
                RequireSameWorkspace(ctx, job, "retry");
                return await RetryAsync(ctx, job).ConfigureAwait(false);
            default:
                throw new ToolException("action must be one of: list, status, diff, merge, discard, cancel, retry, revert.");
        }
    }

    private static void RequireSameWorkspace(ToolContext ctx, JobInfo job, string what)
    {
        // Изменение файлов — только для задач текущей рабочей области: корень задачи внутри корней сессии
        // (сессия в подпапке не трогает задачи родительского проекта — их diff касается файлов вне её корней).
        if (!PathGuard.JobVisible(job.Root, ctx.Roots))
            throw new ToolException($"Job {job.Id} belongs to another workspace ({job.Root}); {what} it from a session opened in that project.");
    }

    private static void RequireIdleSandbox(JobInfo job, string what)
    {
        if (BackgroundJobs.IsActive(job))
            throw new ToolException($"Job {job.Id} is still running; wait (action=status wait_seconds=120) or action=cancel before {what}.");
    }

    private static string List(ToolContext ctx)
    {
        var jobs = JobStore.List(20, j => PathGuard.JobVisible(j.Root, ctx.Roots));
        if (jobs.Count == 0) return "No Offload jobs for this workspace yet.";
        var sb = new StringBuilder("recent jobs (newest first):\n");
        foreach (var j in jobs)
        {
            var status = BackgroundJobs.Reconcile(j).Status;
            if (status == JobStatus.Running && j.Host is { } host) status += $" ({HostName(host)})";
            var task = j.Task.Replace('\n', ' ');
            if (task.Length > 90) task = task[..90] + "…";
            sb.Append($"{j.Id} · {j.Tool} · {status}");
            if (j.Sandbox is { } s && SandboxState.IsOpen(s.State)) sb.Append($" · branch {s.Branch}");
            sb.Append(" · ").Append(task).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    internal static string Describe(JobInfo job)
    {
        var sb = new StringBuilder();
        var running = BackgroundJobs.Progress(job);
        var status = running is not null ? "running" : job.Status;
        sb.Append($"job {job.Id} · {job.Tool} · status {status}");
        if (job.Mode is not null) sb.Append($" · mode {job.Mode}");
        sb.Append($" · created {job.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n");
        sb.Append("task: ").Append(job.Task.Length > 200 ? job.Task[..200] + "…" : job.Task).Append('\n');
        if (running is { } r)
        {
            sb.Append($"running in {HostName(r.Host)} for {r.Elapsed.TotalMinutes:0.0} min");
            if (r.LastMessage is { } m) sb.Append(" · last step: ").Append(m.Length > 160 ? m[..160] + "…" : m);
            sb.Append('\n');
            return sb.ToString().TrimEnd();
        }
        if (job.Status == JobStatus.Running)
            sb.Append($"still running in another Offload process (pid {job.HostPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}, " +
                      "a call from another IDE session); check again later.\n");
        if (job.Status == JobStatus.Interrupted)
        {
            var reason = job.Notes.LastOrDefault(n => n.StartsWith("interrupted: ", StringComparison.Ordinal));
            sb.Append(reason ?? "interrupted: the process that ran it ended").Append('\n');
            sb.Append(BackgroundJobSpec.TryLoad(job.Id) is not null
                ? $"next: local_job action=retry job_id={job.Id} (run the same task again)"
                : "next: this job cannot be retried (no stored task spec)");
            if (job.Sandbox is { } isb && SandboxState.IsOpen(isb.State)) sb.Append($" or action=discard (drop sandbox branch {isb.Branch})");
            sb.Append('\n');
            if (job.Files.Count == 0) return sb.ToString().TrimEnd();
        }

        // Итог фоновой задачи — целиком (он уже короткий).
        if (job.Sandbox is not null && BackgroundJobs.ReadResult(job.Id) is { } result && job.Status != JobStatus.Reverted)
        {
            sb.Append("result:\n").Append(result.Trim()).Append('\n');
            if (job.Sandbox is { } open && SandboxState.IsOpen(open.State)) sb.Append($"sandbox: branch {open.Branch} ({open.State})\n");
            return sb.ToString().TrimEnd();
        }

        foreach (var f in job.Files.Where(f => f.Allowlisted).Take(60))
        {
            sb.Append("  ").Append(f.Display).Append("  ");
            sb.Append(!f.ExistedBefore ? "(new) " : "");
            sb.Append($"+{f.Added} −{f.Removed}");
            if (f.Note is not null) sb.Append(" · ").Append(f.Note);
            sb.Append('\n');
        }
        if (job.Sandbox is { } s && SandboxState.IsOpen(s.State) && job.Status != JobStatus.Interrupted)
            sb.Append($"sandbox: branch {s.Branch} ({s.State}) — action=diff to review, action=merge to apply, action=discard to drop\n");
        if (job.VerifySummary is not null) sb.Append(job.VerifySummary).Append('\n');
        if (!string.IsNullOrWhiteSpace(job.Summary)) sb.Append("summary: ").Append(job.Summary.Replace('\n', ' ')).Append('\n');
        foreach (var n in job.Notes.Take(10)) sb.Append("note: ").Append(n).Append('\n');
        if (job.Status is not (JobStatus.Reverted or JobStatus.DryRun or JobStatus.Running or JobStatus.PendingMerge or JobStatus.Conflict
            or JobStatus.Discarded or JobStatus.Interrupted))
        {
            var changed = JobStore.ChangedSinceFinish(job).Where(f => f.Allowlisted).ToList();
            if (changed.Count > 0)
                sb.Append($"changed after the job: {string.Join(", ", changed.Take(10).Select(f => f.Display))} (revert needs force=true)\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string HostName(string? host) => host switch
    {
        JobHost.Tray => "the Offload tray app",
        JobHost.Mcp => "an IDE session",
        _ => "another Offload process",
    };

    /// <summary>
    /// Повтор задачи агента по сохранённому описанию (spec.json): прерванной, неудавшейся или отменённой. Старая песочница
    /// удаляется, новая задача выполняется в фоне (в трее, если он запущен) в текущей рабочей области.
    /// </summary>
    private static async Task<string> RetryAsync(ToolContext ctx, JobInfo job)
    {
        if (BackgroundJobs.IsActive(job))
            throw new ToolException($"Job {job.Id} is still running; wait (action=status wait_seconds=120) or action=cancel before retry.");
        if (job.Status is not (JobStatus.Interrupted or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Reverted))
            throw new ToolException($"Job {job.Id} has status {job.Status}; only interrupted, failed, cancelled or reverted agent jobs can be retried.");
        var spec = BackgroundJobSpec.TryLoad(job.Id)
                   ?? throw new ToolException($"Job {job.Id} has no stored task spec: only local_agent_task / local_solve jobs created by this Offload version can be retried.");
        if (job.Files.Count > 0 && job.Status != JobStatus.Reverted)
            throw new ToolException($"Job {job.Id} had already started changing project files; check them (action=diff) and revert with force=true before retrying.");
        // Проверка описания по текущим настройкам — до удаления старой песочницы (при отказе она остаётся).
        AgentTaskTool.Prepare(ctx.Cfg, spec.ToRequest(background: true));
        var dropped = "";
        if (job.Sandbox is { } old && SandboxState.IsOpen(old.State))
        {
            await AgentTaskTool.DiscardAsync(job, old).ConfigureAwait(false);
            dropped = $" Its sandbox branch {old.Branch} was discarded.";
        }
        string? newId = null;
        // allow_build_files исходной задачи действует и на повтор; описание проверяется заново по текущим настройкам.
        var text = await PathGuard.WithJobBuildFilePolicy(job,
            () => AgentTaskTool.RunAsync(ctx, spec.ToRequest(background: true), id => newId = id)).ConfigureAwait(false);
        if (newId is not null)
        {
            job.Notes.Add($"retried as job {newId}");
            JobStore.Save(job);
        }
        return $"Retrying job {job.Id} as a new job.{dropped}\n{text}";
    }

    private static string CapLines(string text, int maxLines)
    {
        var lines = text.Split('\n');
        return lines.Length <= maxLines ? text : string.Join('\n', lines.Take(maxLines)) + $"\n…[{lines.Length - maxLines} more lines; raise max_lines or pass paths]";
    }
}
