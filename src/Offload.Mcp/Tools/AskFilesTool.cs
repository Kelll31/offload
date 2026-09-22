using System.Text;
using System.Text.Json;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>local_ask_files: вопрос по файлам, которые читает сервер; map-reduce, если материал не помещается в контекст.</summary>
internal static class AskFilesTool
{
    public const int MaxChunks = 8;
    private const string NothingRelevant = "NOTHING RELEVANT";

    public static async Task<string> RunAsync(ToolContext ctx, string[]? paths, string? question, string? answerFormat, int maxAnswerTokens)
    {
        var specs = ToolHelpers.RequireList(paths, "paths", 64);
        var q = ToolHelpers.RequireText(question, "question", 4000);
        var format = (answerFormat ?? "brief").Trim().ToLowerInvariant();
        if (format is not ("brief" or "detailed" or "bullets" or "json")) format = "brief";
        var maxAnswer = Math.Clamp(maxAnswerTokens <= 0 ? 800 : maxAnswerTokens, 64, 4096);

        ctx.Progress.Report($"Reading {ToolHelpers.Plural(specs.Length, "path")}…");
        var gathered = await FileGatherer.GatherAsync(specs, ctx.Roots, ctx.GatherOptions, ctx.Ct).ConfigureAwait(false);
        if (gathered.Files.Count == 0)
            throw new ToolException("No readable files for the given paths. " + gathered.CoverageLine());

        var model = await ctx.GetModelAsync().ConfigureAwait(false);
        var system = model.SystemPrompt(FormatRules(format) +
            "\nThe material is numbered as '<line>| <code>'. Cite locations as path:line (for example src/app.cs:42).");
        var questionBlock = "QUESTION:\n" + q;

        await using var slot = await GpuQueue.AcquireAsync(ctx.Cfg.Server.Parallel, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        string answer;
        ChunkPlan plan;
        var budget = model.MaterialBudget(maxAnswer, system, questionBlock);
        try
        {
            (answer, plan) = await AnswerAsync(ctx, model, gathered, system, q, format, maxAnswer, budget).ConfigureAwait(false);
        }
        catch (ContextExceededException)
        {
            // Оценка токенов оказалась оптимистичной — повторяем с половинным бюджетом.
            ctx.Progress.Report("Input did not fit the context; retrying in smaller parts…");
            (answer, plan) = await AnswerAsync(ctx, model, gathered, system, q, format, maxAnswer, budget / 2).ConfigureAwait(false);
        }

        var coveredParts = plan.Chunks.SelectMany(c => c.Parts).ToList();
        ctx.Stats.FilesRead = coveredParts.Select(p => p.File).Distinct().Count();
        ctx.Stats.TokensRead = coveredParts.Sum(p => p.Tokens);

        var notes = new List<string>();
        notes.AddRange(plan.Partial.Select(f => $"{f.Display} (partially read: too large)"));
        notes.AddRange(plan.Uncovered.Select(f => $"{f.Display} (not read: exceeded {MaxChunks} parts)"));
        var coverage = gathered.CoverageLine(plan.CoveredFiles(gathered.Files), notes);
        if (plan.Chunks.Count > 1) coverage += $" · answered in {plan.Chunks.Count} parts and merged";
        return answer.TrimEnd() + "\n\n" + coverage;
    }

    private static async Task<(string Answer, ChunkPlan Plan)> AnswerAsync(ToolContext ctx, LocalModel model, GatherResult gathered,
        string system, string question, string format, int maxAnswer, int budget)
    {
        if (budget < 400)
            throw new ToolException($"The local model context ({model.ContextPerSlot} tok) is too small for this request; lower max_answer_tokens or ask about fewer files.");
        var plan = ChunkPlanner.Plan(gathered.Files, budget, MaxChunks);
        if (plan.Chunks.Count == 0) throw new ToolException("Nothing to read: all files were skipped. " + gathered.CoverageLine());

        if (plan.Chunks.Count == 1)
        {
            var user = "MATERIAL:\n" + plan.Chunks[0].Render() + "\nQUESTION:\n" + question;
            var reply = await model.ChatAsync(system, user, maxAnswer, "answer", ctx.Ct).ConfigureAwait(false);
            return (Finalize(reply.Text, reply.Truncated, format, maxAnswer), plan);
        }

        // Map: частичные ответы по каждой части.
        var mapTokens = Math.Clamp(maxAnswer, 200, 700);
        var mapSystem = system + $"\nYou see only PART of the material. Answer using ONLY this part. If this part has nothing relevant, reply exactly: {NothingRelevant}";
        var partials = new List<string>();
        for (var i = 0; i < plan.Chunks.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var user = $"MATERIAL (part {i + 1} of {plan.Chunks.Count}):\n" + plan.Chunks[i].Render() + "\nQUESTION:\n" + question;
            var reply = await model.ChatAsync(mapSystem, user, mapTokens, $"part {i + 1}/{plan.Chunks.Count}", ctx.Ct).ConfigureAwait(false);
            var text = reply.Text.Trim();
            if (text.Length == 0 || text.Contains(NothingRelevant, StringComparison.OrdinalIgnoreCase) && text.Length < 60) continue;
            partials.Add(text);
        }
        if (partials.Count == 0) return ("The material does not contain an answer to the question (no part had relevant information).", plan);
        if (partials.Count == 1) return (Finalize(partials[0], false, format, maxAnswer), plan);

        // Reduce: слияние частичных ответов (иерархически, если они сами не помещаются).
        var mergeSystem = system + "\nYou merge partial answers that were produced from different parts of the material. " +
                          "Combine them into ONE final answer to the question in the required format; remove duplicates; keep path:line citations; do not invent anything new.";
        var mergeBudget = model.MaterialBudget(maxAnswer, mergeSystem, question);
        while (true)
        {
            var joined = Join(partials);
            if (Tokens.Estimate(joined) <= mergeBudget || partials.Count <= 1)
            {
                var reply = await model.ChatAsync(mergeSystem, "PARTIAL ANSWERS:\n" + joined + "\nQUESTION:\n" + question, maxAnswer, "merging", ctx.Ct)
                    .ConfigureAwait(false);
                return (Finalize(reply.Text, reply.Truncated, format, maxAnswer), plan);
            }
            var groups = new List<List<string>>();
            var current = new List<string>();
            var used = 0;
            foreach (var p in partials)
            {
                var t = Tokens.Estimate(p) + 16;
                if (current.Count > 0 && used + t > mergeBudget)
                {
                    groups.Add(current);
                    current = [];
                    used = 0;
                }
                current.Add(p);
                used += t;
            }
            if (current.Count > 0) groups.Add(current);
            if (groups.Count >= partials.Count) groups = partials.Chunk(2).Select(c => c.ToList()).ToList();
            var next = new List<string>();
            foreach (var g in groups)
            {
                if (g.Count == 1) { next.Add(g[0]); continue; }
                var r = await model.ChatAsync(mergeSystem, "PARTIAL ANSWERS:\n" + Join(g) + "\nQUESTION:\n" + question, mapTokens, "merging", ctx.Ct)
                    .ConfigureAwait(false);
                next.Add(r.Text.Trim());
            }
            partials = next;
        }
    }

    private static string Join(IEnumerable<string> partials)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var p in partials) sb.Append($"--- partial answer {i++} ---\n").Append(p).Append('\n');
        return sb.ToString();
    }

    private static string Finalize(string replyText, bool cut, string format, int maxAnswer)
    {
        var text = replyText.Trim();
        if (text.Length == 0)
            throw new ToolException(cut
                ? $"The local model used the whole budget (max_answer_tokens={maxAnswer}) without producing an answer. Raise max_answer_tokens or ask a narrower question."
                : "The local model returned an empty answer. Rephrase the question or do it yourself.");
        if (format == "json")
        {
            text = OutputCleaner.StripFence(text, allowInnerBlock: true).Trim();
            try { using var _ = JsonDocument.Parse(text); }
            catch (JsonException) { text += "\n(warning: the answer is not valid JSON)"; }
        }
        if (cut) text += $"\n[answer cut at max_answer_tokens={maxAnswer}]";
        return text;
    }

    private static string FormatRules(string format) => format switch
    {
        "detailed" => "Answer thoroughly but without filler; use short sections or lists where useful.",
        "bullets" => "Answer as a bullet list: one fact per bullet, each with a path:line citation where applicable.",
        "json" => "Answer with ONE valid JSON value only: no code fences, no commentary before or after.",
        _ => "Answer briefly: a few sentences or a short list, most important first.",
    };
}
