using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_commit_message: сообщение коммита по diff, прочитанному сервером.</summary>
internal static class CommitMessageTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? source, string? style, string? language, string? workingDirectory)
    {
        var src = (source ?? "staged").Trim().ToLowerInvariant();
        if (src is not ("staged" or "unstaged" or "all")) throw new ToolException("source must be staged, unstaged or all.");
        var conventional = !string.Equals((style ?? "conventional").Trim(), "plain", StringComparison.OrdinalIgnoreCase);
        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        if (lang.Length > 30 || !lang.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')) lang = "en";

        var repo = GitDiffs.ResolveRepo(ctx, workingDirectory);
        var set = await GitDiffs.CollectAsync(ctx, repo, src, includeUntracked: src != "staged").ConfigureAwait(false);
        if (set.Files.Count == 0)
        {
            return src == "staged"
                ? "Nothing is staged (git diff --cached is empty). Stage the files first, or call with source=all."
                : "No changes found for source=" + src + ".";
        }

        var model = await ctx.GetModelAsync().ConfigureAwait(false);
        var rules = "Write a git commit message for the diff. Output ONLY the message, nothing else (no quotes, no code fences, no explanations).\n" +
                    "Line 1: the subject, at most 72 characters, imperative mood" +
                    (conventional ? ", format 'type(scope): subject' with type one of feat, fix, refactor, perf, docs, test, build, ci, chore, style" : "") + ".\n" +
                    "Then optionally a blank line and a short body (2-5 bullet lines) saying what changed and why. " +
                    $"Write the message in this language: {lang}.";
        var system = model.SystemPrompt(rules);

        // Сводка + diff файлов, ужатый под бюджет (для сообщения коммита полный diff не обязателен).
        var summary = new StringBuilder("CHANGED FILES:\n");
        foreach (var f in set.Files.Take(200)) summary.Append($"{f.Path} +{f.Added} -{f.Removed}\n");
        if (set.Untracked.Count > 0) summary.Append("new untracked files: ").Append(string.Join(", ", set.Untracked.Take(30))).Append('\n');
        var budget = model.MaterialBudget(400, system, summary.ToString());
        var perFile = Math.Max(200, budget / Math.Max(1, set.Files.Count));
        var diff = new StringBuilder("\nDIFF (may be shortened):\n");
        var used = 0;
        foreach (var f in set.Files)
        {
            var text = f.Text;
            var t = Tokens.Estimate(text);
            if (t > perFile) text = text[..Math.Min(text.Length, perFile * 3)] + "\n… (shortened)";
            var tt = Tokens.Estimate(text);
            if (used + tt > budget) break;
            diff.Append(text).Append('\n');
            used += tt;
        }
        ctx.Stats.FilesRead = set.Files.Count;
        ctx.Stats.TokensRead = set.Files.Sum(f => (long)Tokens.Estimate(f.Text));

        await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        var reply = await model.ChatAsync(system, summary + diff.ToString(), 400, "writing commit message", ctx.Ct).ConfigureAwait(false);
        var msg = Clean(reply.Text);
        if (msg.Length == 0) throw new ToolException("The local model returned an empty commit message; write it yourself.");
        return msg;
    }

    /// <summary>Снять обёртки/кавычки и ограничить тему 72 символами (по границе слова).</summary>
    internal static string Clean(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal)) t = OutputCleaner.StripFence(t, allowInnerBlock: false).Trim();
        if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\''))) t = t[1..^1].Trim();
        var lines = t.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].StartsWith("Subject:", StringComparison.OrdinalIgnoreCase)) lines[0] = lines[0][8..].Trim();
        if (lines.Count == 0) return "";
        var subject = lines[0].Trim().TrimEnd('.');
        if (subject.Length > 72)
        {
            var cut = subject.LastIndexOf(' ', 72);
            subject = subject[..(cut > 40 ? cut : 72)].TrimEnd();
        }
        lines[0] = subject;
        if (lines.Count > 1 && lines[1].Trim().Length > 0) lines.Insert(1, "");
        return string.Join('\n', lines).TrimEnd();
    }
}
