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
        "Offload = a free local coder LLM on this PC (llama.cpp). Weaker than you and 3-30x slower, but costs no cloud tokens. " +
        "Use it for BOUNDED, low-risk, bulky work; keep planning, design, debugging and final judgement yourself.\n\n" +
        "Use it instead of doing the work yourself when:\n" +
        "- You need facts from files you have NOT read (explain, summarize, find usages, first-pass review): local_ask_files with paths + one precise question. " +
        "The server reads the files; only the answer enters your context. Prefer it to Read for files >300 lines.\n" +
        "- A log/build/test output is large: redirect it to a file, then local_summarize_log.\n" +
        "- First-pass review of a git diff: local_review_diff. Commit messages: local_commit_message (reads the diff itself).\n" +
        "- New file from a clear spec (unit tests, DTOs, mappers, fixtures, docs): local_write_file writes it to disk and runs verify_command; " +
        "don't re-type drafted code yourself.\n" +
        "- Mechanical edits with an exact spec (rename, add logging/null checks, one pattern across N files): local_edit_files with an explicit " +
        "file list + verify_command. It returns a diffstat and the check result; diff/undo via local_job.\n\n" +
        "Don't use it for: security/auth/crypto, concurrency, cross-module design, tasks quicker to do than to describe, or when a " +
        "plausible-but-wrong answer is costly.\n\n" +
        "Briefing: it sees ONLY what you pass. Give file paths (never paste file contents into arguments), the exact output format and " +
        "constraints; one task per call. Results are drafts: verify in proportion to risk, but don't re-read whole files to double-check.\n\n" +
        "While a local_edit_files job runs, don't edit the same files. If a tool reports the model offline/busy or quality is poor twice, " +
        "do the work yourself. local_status shows health and savings.\n\n" +
        "If these tools are deferred, load them all with ToolSearch query \"offload\" (max_results 10).";

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
