using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_job: status / diff / revert задачи записи или правки.</summary>
internal static class JobTool
{
    public static string Run(ToolContext ctx, string jobId, string action, int maxLines, string[]? paths, bool force)
    {
        var id = (jobId ?? "").Trim();
        var job = JobStore.Load(id);
        switch ((action ?? "").Trim().ToLowerInvariant())
        {
            case "status":
                return Describe(job);
            case "diff":
                maxLines = Math.Clamp(maxLines <= 0 ? 300 : maxLines, 10, 5000);
                var header = $"job {job.Id} · {job.Tool} · status {job.Status}\n";
                return header + JobStore.Diff(job, maxLines, ctx.MaxResponseChars - header.Length - 300, paths);
            case "revert":
                // Откат меняет файлы — только для задач текущей рабочей области.
                if (!ctx.Roots.Any(r => PathGuard.IsInside(job.Root, r) || PathGuard.IsInside(r, job.Root)))
                    throw new ToolException($"Job {job.Id} belongs to another workspace ({job.Root}); revert it from a session opened in that project.");
                var outcome = JobStore.Revert(job, force);
                if (!outcome.Ok) throw new ToolException(outcome.Message);
                return outcome.Message;
            default:
                throw new ToolException("action must be one of: status, diff, revert.");
        }
    }

    internal static string Describe(JobInfo job)
    {
        var sb = new StringBuilder();
        sb.Append($"job {job.Id} · {job.Tool} · status {job.Status}");
        if (job.Mode is not null) sb.Append($" · mode {job.Mode}");
        sb.Append($" · created {job.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n");
        sb.Append("task: ").Append(job.Task.Length > 200 ? job.Task[..200] + "…" : job.Task).Append('\n');
        foreach (var f in job.Files.Where(f => f.Allowlisted).Take(60))
        {
            sb.Append("  ").Append(f.Display).Append("  ");
            sb.Append(!f.ExistedBefore ? "(new) " : "");
            sb.Append($"+{f.Added} −{f.Removed}");
            if (f.Note is not null) sb.Append(" · ").Append(f.Note);
            sb.Append('\n');
        }
        if (job.VerifySummary is not null) sb.Append(job.VerifySummary).Append('\n');
        foreach (var n in job.Notes.Take(10)) sb.Append("note: ").Append(n).Append('\n');
        if (job.Status is not (JobStatus.Reverted or JobStatus.DryRun or JobStatus.Running))
        {
            var changed = JobStore.ChangedSinceFinish(job).Where(f => f.Allowlisted).ToList();
            if (changed.Count > 0)
                sb.Append($"changed after the job: {string.Join(", ", changed.Take(10).Select(f => f.Display))} (revert needs force=true)\n");
        }
        return sb.ToString().TrimEnd();
    }
}
