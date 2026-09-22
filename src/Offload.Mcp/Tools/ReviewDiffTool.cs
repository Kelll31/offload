using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_review_diff: первичное ревью git diff (по файлам/ханкам, с объединением находок).</summary>
internal static class ReviewDiffTool
{
    public const int MaxChunks = 8;
    private const string NoIssues = "No significant issues found";

    public static async Task<string> RunAsync(ToolContext ctx, string? workingDirectory, string? target, string? focus, int maxAnswerTokens)
    {
        var maxAnswer = Math.Clamp(maxAnswerTokens <= 0 ? 1200 : maxAnswerTokens, 64, 4096);
        var repo = GitDiffs.ResolveRepo(ctx, workingDirectory);
        ctx.Progress.Report("Collecting git diff…");
        var set = await GitDiffs.CollectAsync(ctx, repo, target ?? "all", includeUntracked: true).ConfigureAwait(false);
        var header = $"Diff: {set.Description} · {ToolHelpers.Plural(set.Files.Count, "file")} +{set.Added} −{set.Removed}";
        if (set.Files.Count == 0)
        {
            var nothing = $"{header}. Nothing to review.";
            if (set.Untracked.Count > 0) nothing += $"\nuntracked (not reviewed, contents not in diff): {string.Join(", ", set.Untracked.Take(20))}";
            return nothing;
        }

        var model = await ctx.GetModelAsync().ConfigureAwait(false);
        var focusText = string.IsNullOrWhiteSpace(focus) ? "" : $"\nFocus especially on: {focus.Trim()[..Math.Min(focus.Trim().Length, 300)]}.";
        var system = model.SystemPrompt(
            "You review a code diff (a first pass for another AI agent). Report only real problems: bugs, wrong logic, missing error " +
            "handling, security issues, resource leaks, unhandled edge cases, broken API usage, obvious typos. No style nitpicks.\n" +
            "Output one finding per line, most severe first, in exactly this format:\n" +
            "[critical|high|medium|low] path:line - issue - suggestion\n" +
            "Line numbers are the new-file numbers at the start of each diff line ('  42|+ code'). Lines marked '|-' were removed.\n" +
            $"If there are no significant issues, output exactly: {NoIssues}." + focusText);

        var budget = model.MaterialBudget(maxAnswer, system, "DIFF:\n");
        if (budget < 400) throw new ToolException($"The local model context ({model.ContextPerSlot} tok) is too small; lower max_answer_tokens.");

        // Раскладка по частям: файлы целиком, большие — по ханкам.
        var pieces = set.Files.SelectMany(f => GitDiffs.SplitAnnotated(GitDiffs.Annotate(f), budget).Select(p => (f.Path, Text: p))).ToList();
        var chunks = new List<(List<string> Paths, StringBuilder Text, int Tokens)>();
        var notCovered = new List<string>();
        foreach (var (p, text) in pieces)
        {
            var t = Tokens.Estimate(text);
            if (chunks.Count > 0 && chunks[^1].Tokens + t <= budget)
            {
                var c = chunks[^1];
                c.Text.Append(text);
                if (!c.Paths.Contains(p)) c.Paths.Add(p);
                chunks[^1] = (c.Paths, c.Text, c.Tokens + t);
            }
            else if (chunks.Count < MaxChunks)
            {
                chunks.Add(([p], new StringBuilder(text), t));
            }
            else if (!notCovered.Contains(p))
            {
                notCovered.Add(p);
            }
        }
        var covered = chunks.SelectMany(c => c.Paths).Distinct().ToList();
        ctx.Stats.FilesRead = covered.Count;
        ctx.Stats.TokensRead = chunks.Sum(c => c.Tokens);

        await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        var findings = new List<string>();
        var truncated = false;
        for (var i = 0; i < chunks.Count; i++)
        {
            var label = chunks.Count == 1 ? "reviewing" : $"reviewing part {i + 1}/{chunks.Count}";
            var reply = await model.ChatAsync(system, "DIFF:\n" + chunks[i].Text, maxAnswer, label, ctx.Ct).ConfigureAwait(false);
            truncated |= reply.Truncated;
            var text = reply.Text.Trim();
            if (text.Length == 0 || text.StartsWith(NoIssues, StringComparison.OrdinalIgnoreCase)) continue;
            findings.Add(text);
        }

        string result;
        if (findings.Count == 0) result = NoIssues + ".";
        else if (findings.Count == 1) result = findings[0];
        else
        {
            var mergeSystem = model.SystemPrompt(
                "Merge these code-review findings into one list: remove duplicates, sort by severity (critical, high, medium, low), " +
                "keep the exact format '[severity] path:line - issue - suggestion'. Output only the list.");
            var joined = string.Join("\n", findings);
            if (Tokens.Estimate(joined) < model.MaterialBudget(maxAnswer, mergeSystem))
            {
                var merged = await model.ChatAsync(mergeSystem, joined, maxAnswer, "merging findings", ctx.Ct).ConfigureAwait(false);
                result = merged.Text.Trim().Length > 0 ? merged.Text.Trim() : joined;
            }
            else
            {
                result = joined;
            }
        }

        var sb = new StringBuilder();
        sb.Append("First-pass review by the local model (verify before acting). ").Append(header).Append('\n');
        sb.Append(result.TrimEnd()).Append('\n');
        if (truncated) sb.Append($"[some findings cut at max_answer_tokens={maxAnswer}]\n");
        var cov = notCovered.Count == 0 && !set.Truncated
            ? $"coverage: full ({ToolHelpers.Plural(covered.Count, "file")})"
            : $"coverage: {covered.Count}/{set.Files.Count} files{(notCovered.Count > 0 ? "; not reviewed (too large): " + string.Join(", ", notCovered.Take(10)) : "")}{(set.Truncated ? "; diff output truncated" : "")}";
        sb.Append(cov);
        if (set.Excluded.Count > 0) sb.Append($"\nexcluded (secret files): {string.Join(", ", set.Excluded.Take(10))}");
        if (set.Untracked.Count > 0) sb.Append($"\nuntracked files (not reviewed): {string.Join(", ", set.Untracked.Take(15))}{(set.Untracked.Count > 15 ? ", …" : "")}");
        return sb.ToString();
    }
}
