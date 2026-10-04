using System.Text;
using Offload.Core.Roles;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_team: «команда» ролей локальной модели над одними и теми же файлами. Роли работают параллельно (по числу слотов
/// сервера) или цепочкой — каждая видит отчёты предыдущих; затем при <c>synthesize</c> ведущая роль сводит итог: что
/// подтверждают несколько ролей, где они расходятся, что сделать первым. Решения принимает вызывающий агент.
/// </summary>
internal static class TeamTool
{
    public const int MaxRoles = 6;
    private const string FailedPrefix = "(role failed: ";
    private const int QuestionLimit = 3900; // AskFilesTool принимает вопрос до 4000 символов

    private sealed record Member(ResolvedRole Role, string? Focus);

    public static async Task<string> RunAsync(ToolContext ctx, string[]? paths, string? task, string[]? roles, string? mode,
        bool synthesize, int maxAnswerTokens)
    {
        var root = RolesTool.ReadRoot(ctx.Roots);
        var question = ToolHelpers.RequireText(task, "task", 2000);
        var members = ParseMembers(root, roles);
        var pipeline = string.Equals((mode ?? "parallel").Trim(), "pipeline", StringComparison.OrdinalIgnoreCase);
        if (!pipeline && !string.Equals((mode ?? "parallel").Trim(), "parallel", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("mode must be parallel or pipeline.");
        var maxAnswer = Math.Clamp(maxAnswerTokens <= 0 ? 600 : maxAnswerTokens, 128, 2048);
        ToolHelpers.RequireList(paths, "paths", 64);

        // Ведущая роль и модель — до запуска участников: ошибка в них не должна всплыть после всей работы команды,
        // а параллельные участники не создают каждый свою модель.
        var lead = members.Count > 1 && synthesize ? RolesTool.Resolve(null, "engineer") : null;
        await ctx.GetModelAsync().ConfigureAwait(false);

        ctx.Progress.Report($"Team of {members.Count} ({(pipeline ? "pipeline" : "parallel")}): {string.Join(", ", members.Select(m => m.Role.Name))}…");
        var reports = new string?[members.Count];
        var coverage = "";

        if (pipeline)
        {
            for (var i = 0; i < members.Count; i++)
            {
                var prior = Enumerable.Range(0, i).Select(j => (members[j].Role.Name, reports[j] ?? "")).ToList();
                reports[i] = await RunMemberAsync(ctx, paths, members[i], BuildQuestion(question, members[i], prior), maxAnswer).ConfigureAwait(false);
            }
        }
        else
        {
            var tasks = members.Select(m => RunMemberAsync(ctx, paths, m, BuildQuestion(question, m, []), maxAnswer)).ToArray();
            var done = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (var i = 0; i < done.Length; i++) reports[i] = done[i];
        }

        var failed = reports.Count(r => r is { } text && text.StartsWith(FailedPrefix, StringComparison.Ordinal));
        if (failed == members.Count)
            throw new ToolException("Every role failed: " + reports[0]!.Trim('(', ')'));

        var sb = new StringBuilder();
        sb.Append($"Team report · {members.Count} roles · {(pipeline ? "pipeline" : "parallel")}\n");
        for (var i = 0; i < members.Count; i++)
        {
            var (body, cov) = SplitCoverage(reports[i] ?? "");
            if (coverage.Length == 0 && cov.Length > 0) coverage = cov;
            sb.Append("\n## ").Append(members[i].Role.Name);
            if (members[i].Role.Chain.Count > 1) sb.Append(" (").Append(string.Join(" -> ", members[i].Role.Chain)).Append(')');
            sb.Append('\n').Append(body.Trim()).Append('\n');
        }

        if (lead is not null && members.Count - failed >= 2)
        {
            ctx.Progress.Report("Lead is merging the reports…");
            var summary = await SynthesizeAsync(ctx, question, members, lead, reports.Select(r => SplitCoverage(r ?? "").Body).ToList(), maxAnswer).ConfigureAwait(false);
            sb.Append("\n## Lead synthesis\n").Append(summary.Trim()).Append('\n');
        }
        if (coverage.Length > 0) sb.Append('\n').Append(coverage);
        return sb.ToString().TrimEnd();
    }

    private static List<Member> ParseMembers(string? root, string[]? roles)
    {
        var entries = (roles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
        if (entries.Count == 0) throw new ToolException($"'roles' needs 1-{MaxRoles} role names (see local_roles action=list), optionally 'name=extra focus'.");
        if (entries.Count > MaxRoles) throw new ToolException($"'roles' has {entries.Count} entries (max {MaxRoles}).");
        var members = new List<Member>();
        foreach (var entry in entries)
        {
            var eq = entry.IndexOf('=');
            var name = (eq > 0 ? entry[..eq] : entry).Trim();
            var focus = eq > 0 ? entry[(eq + 1)..].Trim() : null;
            if (focus is { Length: > 300 }) focus = focus[..300];
            members.Add(new Member(RolesTool.Resolve(root, name), string.IsNullOrWhiteSpace(focus) ? null : focus));
        }
        return members;
    }

    private static string BuildQuestion(string task, Member member, List<(string Role, string Report)> prior)
    {
        var q = task.Trim();
        if (member.Focus is not null) q += $"\nYour focus as {member.Role.Name}: {member.Focus}";
        if (prior.Count == 0) return q;

        // Отчёты предыдущих ролей делят остаток лимита вопроса поровну (но не меньше 300 символов каждому).
        var share = Math.Max(300, (QuestionLimit - q.Length - 120) / prior.Count);
        var sb = new StringBuilder(q).Append("\n\nReports of previous roles (build on them, do not repeat them):");
        foreach (var (role, report) in prior)
        {
            var text = SplitCoverage(report).Body.Trim();
            if (text.Length > share) text = text[..share] + "…";
            sb.Append("\n### ").Append(role).Append('\n').Append(text);
        }
        return sb.Length > QuestionLimit + 100 ? sb.ToString(0, QuestionLimit + 100) : sb.ToString();
    }

    private static async Task<string> RunMemberAsync(ToolContext ctx, string[]? paths, Member member, string question, int maxAnswer)
    {
        try
        {
            return await AskFilesTool.RunAsync(ctx, paths, question, "brief", maxAnswer, fresh: false, member.Role).ConfigureAwait(false);
        }
        catch (ToolException ex)
        {
            return $"{FailedPrefix}{ex.Message})";
        }
    }

    private static async Task<string> SynthesizeAsync(ToolContext ctx, string task, List<Member> members, ResolvedRole lead, List<string> bodies, int maxAnswer)
    {
        var model = await ctx.GetModelAsync().ConfigureAwait(false);
        var system = model.SystemPrompt(
            "You are the lead of a small review team. Several roles analysed the same files and wrote reports. Merge them: " +
            "(1) Agreed - points two or more roles confirm; (2) Conflicts - where roles disagree, and which side the evidence supports; " +
            "(3) Next steps - at most 5 concrete actions, most important first, each with path:line when the reports give one. " +
            "Use only facts from the reports; do not add new findings. Be brief.",
            lead);
        var budget = model.MaterialBudget(maxAnswer, system, "TASK:\n" + task);
        var perReportChars = Math.Max(400, budget * 3 / members.Count);
        var user = new StringBuilder("TASK:\n").Append(task).Append("\n\nREPORTS:");
        for (var i = 0; i < members.Count; i++)
        {
            var text = bodies[i].Trim();
            if (text.Length > perReportChars) text = text[..perReportChars] + "…";
            user.Append("\n### ").Append(members[i].Role.Name).Append('\n').Append(text);
        }
        await using var slot = await GpuQueue.AcquireAsync(ctx, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, user.ToString(), maxAnswer, "synthesis", ctx.Ct).ConfigureAwait(false);
        return reply.Text;
    }

    /// <summary>Ответ <c>local_ask_files</c> = текст + пустая строка + строка охвата файлов; разделяет их.</summary>
    internal static (string Body, string Coverage) SplitCoverage(string text)
    {
        var t = text.TrimEnd();
        var i = t.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (i < 0) return (t, "");
        var tail = t[(i + 2)..];
        return tail.StartsWith("coverage:", StringComparison.Ordinal) ? (t[..i], tail) : (t, "");
    }
}
