using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Resources;

namespace Offload.Mcp.Tools;

/// <summary>
/// Обёртка local_job до вызова JobTool: подтверждение пользователя (elicitation) для рискованных действий и ссылка на полный diff.
/// - merge задачи, которую не влили автоматически из-за max_files или находок ревью critical/high;
/// - revert с force=true, когда он действительно перезапишет файлы, изменённые после задачи (или задача ещё «running»).
/// Клиент без elicitation — прежнее поведение (JobTool решает сам). Отказ пользователя — ToolException, ничего не меняется.
/// </summary>
/// <remarks>
/// Вопрос читает пользователь, который решает, вливать ли изменения. Поэтому сначала — собственный вердикт Offload, собранный
/// из фиксированных фраз (а не из текста заметок), и сам вопрос; текст задачи (его написала облачная модель, и в него могло
/// попасть содержимое репозитория) — только в конце, помеченный как чужой, в кавычках, одной строкой, обрезанный и без
/// управляющих символов: «ревьюер ошибся, вливайте» из задачи не должно выглядеть словами Offload.
/// </remarks>
internal static partial class JobConfirmation
{
    /// <summary>Префикс заметки AgentTaskTool о причинах, по которым песочницу не влили автоматически.</summary>
    internal const string NotMergedNote = "not merged automatically:";

    public static async Task<string> RunAsync(ToolContext ctx, string? jobId, string? action, bool force, Func<Task<string>> next)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();
        var id = (jobId ?? "").Trim();
        if (act == "diff" && JobStore.IsValidId(id))
            ctx.AddResourceLink(ResourceUris.JobDiff(id), $"job {id} diff", "Full unified diff of the job (not limited by max_lines)", "text/x-diff");

        if (act is "merge" or "revert" && JobStore.IsValidId(id) && UserConfirmation.IsSupported(ctx.Server)
            && Question(LoadOrNull(id), act, force) is { } question)
        {
            var answer = await UserConfirmation.AskAsync(ctx, question, act == "merge" ? "Merge these changes" : "Overwrite later edits").ConfigureAwait(false);
            if (answer == Confirmation.Declined)
                throw new ToolException($"The user declined {act} of job {id}; nothing was changed. Review it with local_job action=diff and ask the user how to proceed.");
        }
        return await next().ConfigureAwait(false);
    }

    private static JobInfo? LoadOrNull(string id)
    {
        try { return JobStore.Load(id); }
        catch (ToolException) { return null; }
    }

    /// <summary>Текст вопроса пользователю или null, если подтверждение не нужно.</summary>
    internal static string? Question(JobInfo? job, string act, bool force)
    {
        if (job is null) return null;
        if (act == "merge")
        {
            if (job.Status != JobStatus.PendingMerge) return null;
            var reasons = job.Notes
                .Where(n => n.StartsWith(NotMergedNote, StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n[NotMergedNote.Length..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Select(Verdict)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (reasons.Count == 0) return null;
            return $"Offload held back job {job.Id} ({job.Tool}) from an automatic merge: {string.Join("; ", reasons)}.\n\n" +
                   $"Merge the agent's changes into {Quote(job.Root, 300)} now?\n\n" +
                   $"Task text written by the AI agent (not verified by Offload): «{Quote(job.Task, 200)}»";
        }
        if (act == "revert" && force)
        {
            var changed = JobStore.ChangedSinceFinish(job).Where(f => f.Allowlisted).Select(f => Quote(f.Display, 160)).ToList();
            if (changed.Count == 0 && job.Status != JobStatus.Running) return null;
            var what = changed.Count > 0
                ? $"This overwrites edits made AFTER the job in: {string.Join(", ", changed.Take(8))}{(changed.Count > 8 ? $" and {changed.Count - 8} more" : "")}."
                : "The job is still marked as running (or was interrupted); its snapshot is restored anyway.";
            return $"Force-revert Offload job {job.Id} ({job.Tool}) in {Quote(job.Root, 300)}?\n\n{what}";
        }
        return null;
    }

    /// <summary>
    /// Причина из заметки → фиксированная фраза Offload (числа — только распознанные). null — причина не требует вопроса
    /// (проваленную проверку модель видит и так) или не распознана.
    /// </summary>
    private static string? Verdict(string reason)
    {
        if (MaxFilesReason().Match(reason) is { Success: true } m
            && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var changed)
            && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var max))
            return $"{changed} files changed (max_files={max})";
        if (reason.Contains("max_files", StringComparison.OrdinalIgnoreCase)) return "more files changed than max_files allows";
        if (reason.Contains("critical/high", StringComparison.OrdinalIgnoreCase)) return "the local review found critical/high issues";
        return null;
    }

    /// <summary>
    /// Чужой текст для вопроса: одна строка, без управляющих и невидимых символов форматирования (переводы строк, bidi-override),
    /// без кавычек-ёлочек (чтобы не «закрыть» цитату), со схлопнутыми пробелами, не длиннее max.
    /// </summary>
    internal static string Quote(string? text, int max)
    {
        var sb = new StringBuilder();
        var space = false;
        foreach (var c in text ?? "")
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || char.IsControl(c) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c is '«' or '»' ? '"' : c);
        }
        var one = sb.ToString();
        if (one.Length <= max) return one;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(one[cut - 1])) cut--;
        return one[..cut] + "…";
    }

    [GeneratedRegex(@"^(\d{1,6}) files changed \(max_files=(\d{1,6})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex MaxFilesReason();
}
