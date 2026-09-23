using Offload.Core.Config;

namespace Offload.Mcp;

/// <summary>
/// Инструкции сервера (initialize.instructions). Claude Code обрезает их на 2048 символах — держим текст короче
/// и важное ставим в начало. Строка статуса считается при старте без сети — только из config.json.
/// </summary>
internal static class ServerInstructions
{
    public const int MaxLength = 2048;

    public const string Body =
        "Offload = free local coder LLM + coding agent on this PC. Slower and weaker than you, but costs no cloud tokens: give it bulky, " +
        "checkable work; keep design and final judgement.\n\n" +
        "Instead of reading/grepping yourself:\n" +
        "- New task/area: local_find_context(task) = ranked files + code in a token budget (mode=plan adds a plan). Repo overview, " +
        "routes, config, env, conventions: local_project_map.\n" +
        "- Navigate: local_search_code, local_symbols (outline/definition/references/callers/callees/tests/slice). Questions about " +
        "unread files: local_ask_files. History: local_git_history. Project memory: local_memory (recall first, store decisions).\n" +
        "Checks:\n" +
        "- local_verify(kind=build|test|lint) returns the outcome and errors, not the output; local_diagnostics = structured errors " +
        "and stack traces; logs: local_summarize_log.\n" +
        "- local_impact (callers, related tests, run_tests), local_code_scan, local_review_diff, local_security_review, " +
        "local_dependency_check.\n" +
        "Writing (snapshotted, undo: local_job revert):\n" +
        "- Whole task: local_solve(task, kind) or local_agent_task - the agent codes in an isolated git worktree, runs the check and " +
        "merges only if it passes; background=true lets you keep working.\n" +
        "- local_apply_patch (your diff; atomic, rollback), local_refactor (rename everywhere), local_write_file, local_edit_files. " +
        "Git text: local_commit_message (commit/pr/split). Jobs: local_job.\n\n" +
        "Brief it like it sees nothing: paths, acceptance criteria, an allowlisted verify command; one task per call. Results are " +
        "drafts: check the proof/diffstat and risky spots, not every line. Not for security-critical design or tasks quicker to do " +
        "yourself; if it fails twice, do it yourself. local_status: health and savings.\n\n" +
        "If these tools are deferred, load them all with ToolSearch query \"offload\" (max_results 25).";

    public static string Build(AppConfig? cfg)
    {
        var line = StatusLine(cfg);
        // Строку статуса укорачиваем, а не выбрасываем: основной текст важнее.
        var room = MaxLength - Body.Length - 1;
        if (line.Length > room) line = room > 20 ? line[..room] : "";
        return line.Length == 0 ? Body[..Math.Min(Body.Length, MaxLength)] : Body + "\n" + line;
    }

    internal static string StatusLine(AppConfig? cfg)
    {
        try
        {
            var model = cfg?.ActiveModel();
            if (cfg is null || !cfg.SetupCompleted || model is null)
                return "Status at startup: Offload is not set up yet (tools will say how to fix it).";
            var name = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName;
            if (!string.IsNullOrWhiteSpace(model.Quant) && !name.Contains(model.Quant, StringComparison.OrdinalIgnoreCase)) name += " " + model.Quant;
            if (name.Length > 60) name = name[..60];
            var ctx = cfg.Server.ContextSize > 0 ? cfg.Server.ContextSize : model.RecommendedContext;
            return $"Status at startup: model {name}{(ctx > 0 ? $", context {ctx} tok" : "")}; the server auto-starts on the first call.";
        }
        catch
        {
            return "Status at startup: unknown (call local_status).";
        }
    }
}
