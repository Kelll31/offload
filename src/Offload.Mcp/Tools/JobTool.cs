using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_job: list / status / diff / revert задач записи и правки; merge / discard / cancel песочниц local_agent_task.</summary>
internal static class JobTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? jobId, string? action, int maxLines, string[]? paths, bool force,
        bool commit, int waitSeconds)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();
        if (act == "list") return List(ctx);

        var id = (jobId ?? "").Trim();
        if (id.Length == 0) throw new ToolException("job_id is required for action=" + (act.Length == 0 ? "?" : act) + " (use action=list to find it).");
        var job = JobStore.Load(id);
        switch (act)
        {
            case "status":
                if (waitSeconds > 0 && BackgroundJobs.IsRunning(job.Id))
                {
                    ctx.Progress.Report($"waiting for job {job.Id}…");
                    await BackgroundJobs.WaitAsync(job.Id, TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 600)), ctx.Ct).ConfigureAwait(false);
                    job = JobStore.Load(id);
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
                var merged = await AgentTaskTool.MergeAsync(ctx, job, commit, ctx.Ct).ConfigureAwait(false);
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
                return BackgroundJobs.Cancel(job.Id)
                    ? $"Cancellation requested for job {job.Id}; the sandbox is discarded and your project stays unchanged. Check with action=status."
                    : $"Job {job.Id} is not running in this session (status {job.Status}).";
            default:
                throw new ToolException("action must be one of: list, status, diff, merge, discard, cancel, revert.");
        }
    }

    private static void RequireSameWorkspace(ToolContext ctx, JobInfo job, string what)
    {
        // Изменение файлов — только для задач текущей рабочей области.
        if (!ctx.Roots.Any(r => PathGuard.IsInside(job.Root, r) || PathGuard.IsInside(r, job.Root)))
            throw new ToolException($"Job {job.Id} belongs to another workspace ({job.Root}); {what} it from a session opened in that project.");
    }

    private static void RequireIdleSandbox(JobInfo job, string what)
    {
        if (BackgroundJobs.IsRunning(job.Id))
            throw new ToolException($"Job {job.Id} is still running; wait (action=status wait_seconds=120) or action=cancel before {what}.");
    }

    private static string List(ToolContext ctx)
    {
        var jobs = JobStore.List(20, j => ctx.Roots.Any(r => PathGuard.IsInside(j.Root, r) || PathGuard.IsInside(r, j.Root)));
        if (jobs.Count == 0) return "No Offload jobs for this workspace yet.";
        var sb = new StringBuilder("recent jobs (newest first):\n");
        foreach (var j in jobs)
        {
            var status = BackgroundJobs.IsRunning(j.Id) ? "running" : j.Status;
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
        var running = BackgroundJobs.Progress(job.Id);
        var status = running is not null ? "running" : job.Status;
        sb.Append($"job {job.Id} · {job.Tool} · status {status}");
        if (job.Mode is not null) sb.Append($" · mode {job.Mode}");
        sb.Append($" · created {job.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n");
        sb.Append("task: ").Append(job.Task.Length > 200 ? job.Task[..200] + "…" : job.Task).Append('\n');
        if (running is { } r)
        {
            sb.Append($"running for {r.Elapsed.TotalMinutes:0.0} min");
            if (r.LastMessage is { } m) sb.Append(" · last step: ").Append(m.Length > 160 ? m[..160] + "…" : m);
            sb.Append('\n');
            return sb.ToString().TrimEnd();
        }
        if (job.Status == JobStatus.Running)
            sb.Append("not running in this MCP session: it runs in another IDE session or was interrupted (IDE restarted). " +
                      "If interrupted, discard its sandbox (action=discard) or revert with force=true.\n");

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
        if (job.Sandbox is { } s && SandboxState.IsOpen(s.State))
            sb.Append($"sandbox: branch {s.Branch} ({s.State}) — action=diff to review, action=merge to apply, action=discard to drop\n");
        if (job.VerifySummary is not null) sb.Append(job.VerifySummary).Append('\n');
        if (!string.IsNullOrWhiteSpace(job.Summary)) sb.Append("summary: ").Append(job.Summary.Replace('\n', ' ')).Append('\n');
        foreach (var n in job.Notes.Take(10)) sb.Append("note: ").Append(n).Append('\n');
        if (job.Status is not (JobStatus.Reverted or JobStatus.DryRun or JobStatus.Running or JobStatus.PendingMerge or JobStatus.Conflict
            or JobStatus.Discarded))
        {
            var changed = JobStore.ChangedSinceFinish(job).Where(f => f.Allowlisted).ToList();
            if (changed.Count > 0)
                sb.Append($"changed after the job: {string.Join(", ", changed.Take(10).Select(f => f.Display))} (revert needs force=true)\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string CapLines(string text, int maxLines)
    {
        var lines = text.Split('\n');
        return lines.Length <= maxLines ? text : string.Join('\n', lines.Take(maxLines)) + $"\n…[{lines.Length - maxLines} more lines; raise max_lines or pass paths]";
    }
}
