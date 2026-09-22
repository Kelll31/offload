using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp;

/// <summary>
/// MCP-инструменты Offload. Здесь только объявления (имена, аннотации, _meta, описания для модели);
/// реализация — в Offload.Mcp.Tools. Все подсказки аннотаций заданы явно: Claude Code трактует
/// отсутствующие destructive/openWorld как false, а спецификация — как true.
/// </summary>
[McpServerToolType]
public sealed class OffloadTools(SessionState state)
{
    internal const string StatusDescription =
        "Offload health check: whether the free local model is online (or how it will auto-start), model name and quant, " +
        "context size per request, measured speed, GPU queue, whether agent mode (OpenCode) is available for local_edit_files, " +
        "and cloud tokens saved (this session and lifetime). Cheap and side-effect free. Call it once before a long delegation " +
        "or when another Offload tool fails.";

    internal const string AskFilesDescription =
        "Ask the free local model a question about files WITHOUT reading them into your context. The server reads the files itself " +
        "(paths, directories or globs like \"src/**/*.cs\", relative to the project root or absolute; respects .gitignore; skips " +
        "binaries and secrets) and returns only the answer - typically 20-50x fewer tokens than Read. Answers cite path:line. " +
        "Good for: explain or summarize a module, find where X is done, list public API, locate usages, first-pass bug review, " +
        "compare files. Not for decisions that need whole-repo reasoning. If the material exceeds the local context window the " +
        "server splits it into parts and merges the answers; coverage is always reported. Answers come from a smaller model: " +
        "spot-check what matters.";

    internal const string SummarizeLogDescription =
        "Digest a large log / build / test-output FILE with the local model instead of reading it. The server pre-filters it " +
        "(head, tail, windows around error/exception/fail/traceback/CS1234 lines, repeats collapsed) and returns: failing items, " +
        "the first root error with path:line, likely cause and next step. Redirect output to a file first (e.g. " +
        "`dotnet test > .offload/test.log 2>&1`), then pass its path. UTF-8/UTF-16/ANSI logs and color codes are handled.";

    internal const string ReviewDiffDescription =
        "First-pass code review of a git diff by the local model; the server runs git itself, so you don't read the diff. " +
        "target: \"all\" (default: staged + unstaged vs HEAD, plus untracked file names), \"staged\", \"unstaged\", or a git " +
        "ref/range (\"main\", \"HEAD~3\", \"main...HEAD\"). Returns prioritized findings: [severity] path:line - issue - suggestion. " +
        "Large diffs are reviewed per file and merged; coverage is reported; secret files are excluded. It is a cheap first pass " +
        "by a smaller model: verify findings before acting and keep the final review yourself.";

    internal const string CommitMessageDescription =
        "Write a commit message with the local model from the git diff, read server-side (staged changes by default), so you don't " +
        "need to read the diff. Returns only the message (subject <=72 chars, optional body), followed by a one-line stats footer " +
        "after ---. style: conventional (feat/fix/...: subject) or plain. If nothing is staged it says so.";

    internal const string WriteFileDescription =
        "Have the local model write ONE new file from a precise spec (unit tests, DTOs/mappers, fixtures, config, docs, boilerplate) " +
        "using context_paths as reference; the server writes it to disk, optionally runs verify_command and lets the model fix " +
        "failures. Returns path, size and the verify result (exit code + failing tail) - NOT the code; spot-check instead of " +
        "re-reading it. Use this instead of typing boilerplate yourself (your output tokens are the most expensive). Refuses to " +
        "overwrite an existing file unless overwrite=true. verify_command must match the Offload allowlist (e.g. " +
        "\"dotnet test --filter FooTests\", \"npm test\", \"pytest tests/test_foo.py\"); shell operators are rejected. " +
        "Undo with local_job action=revert.";

    internal const string EditFilesDescription =
        "Delegate a mechanical, precisely specified edit of EXISTING files to the local model (rename, add logging/null checks, " +
        "apply one pattern to N files, fix listed warnings). It may ONLY modify `files` (inside the project, max 50); changes " +
        "elsewhere are reverted. mode auto: small file sets are rewritten directly by the model; larger ones go to the local " +
        "OpenCode agent if installed. Runs verify_command (allowlisted, e.g. \"dotnet build\") and retries fixes up to " +
        "fix_attempts. Returns job_id, status, per-file +/- line counts, verify result and a short summary - not the diff (use " +
        "local_job action=diff). Every job is snapshotted: undo with local_job action=revert. Slow (minutes); don't edit the " +
        "same files meanwhile. Not for design work or vague tasks.";

    internal const string JobDescription =
        "Inspect or undo a local_write_file / local_edit_files job: action=status (summary), diff (unified diff against the " +
        "snapshot, capped by max_lines), revert (restore the snapshot; files created by the job are deleted). Revert refuses " +
        "if a file changed after the job finished, unless force=true.";

    [McpServerTool(Name = McpToolNames.Status, Title = "Offload: состояние",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/alwaysLoad", true)]
    [McpMeta("anthropic/searchHint", "offload local model status health speed queue tokens saved")]
    [Description(StatusDescription)]
    public Task<CallToolResult> LocalStatus(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken) =>
        ToolRunner.RunAsync(McpToolNames.Status, state, context, StatusTool.RunAsync, cancellationToken);

    [McpServerTool(Name = McpToolNames.AskFiles, Title = "Offload: вопрос по файлам",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/alwaysLoad", true)]
    [McpMeta("anthropic/searchHint", "offload to local llm: explain summarize review search files without reading them")]
    [Description(AskFilesDescription)]
    public Task<CallToolResult> LocalAskFiles(
        [Description("Files, directories or globs (e.g. \"src/**/*.cs\"), relative to the project root or absolute. 1-64 entries.")] string[] paths,
        [Description("One precise question or task, plus the output shape you want.")] string question,
        RequestContext<CallToolRequestParams> context,
        [Description("brief | detailed | bullets | json")] string answer_format = "brief",
        [Description("Answer length limit, 64-4096 tokens.")] int max_answer_tokens = 800,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.AskFiles, state, context,
            ctx => AskFilesTool.RunAsync(ctx, paths, question, answer_format, max_answer_tokens), cancellationToken);

    [McpServerTool(Name = McpToolNames.SummarizeLog, Title = "Offload: разбор лога",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "summarize big log build output test failures stack trace")]
    [Description(SummarizeLogDescription)]
    public Task<CallToolResult> LocalSummarizeLog(
        [Description("Log file path (relative to the project root or absolute).")] string path,
        RequestContext<CallToolRequestParams> context,
        [Description("What to look for.")] string focus = "errors, failing tests, root cause",
        [Description("Only consider the last N lines (0 = whole file).")] int tail_lines = 0,
        [Description("Answer length limit, 64-4096 tokens.")] int max_answer_tokens = 600,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.SummarizeLog, state, context,
            ctx => SummarizeLogTool.RunAsync(ctx, path, focus, tail_lines, max_answer_tokens), cancellationToken);

    [McpServerTool(Name = McpToolNames.ReviewDiff, Title = "Offload: ревью diff",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "code review git diff changes local model first pass")]
    [Description(ReviewDiffDescription)]
    public Task<CallToolResult> LocalReviewDiff(
        RequestContext<CallToolRequestParams> context,
        [Description("Git repository folder (default: project root).")] string? working_directory = null,
        [Description("all | staged | unstaged | <git ref or range>")] string target = "all",
        [Description("Optional review focus, e.g. \"error handling\" or \"thread safety\".")] string? focus = null,
        [Description("Answer length limit, 64-4096 tokens.")] int max_answer_tokens = 1200,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.ReviewDiff, state, context,
            ctx => ReviewDiffTool.RunAsync(ctx, working_directory, target, focus, max_answer_tokens), cancellationToken);

    [McpServerTool(Name = McpToolNames.CommitMessage, Title = "Offload: сообщение коммита",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "generate git commit message from staged diff")]
    [Description(CommitMessageDescription)]
    public Task<CallToolResult> LocalCommitMessage(
        RequestContext<CallToolRequestParams> context,
        [Description("staged | unstaged | all")] string source = "staged",
        [Description("conventional | plain")] string style = "conventional",
        [Description("Message language, e.g. en or ru.")] string language = "en",
        [Description("Git repository folder (default: project root).")] string? working_directory = null,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.CommitMessage, state, context,
            ctx => CommitMessageTool.RunAsync(ctx, source, style, language, working_directory), cancellationToken);

    [McpServerTool(Name = McpToolNames.WriteFile, Title = "Offload: написать файл",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "generate unit tests boilerplate new file with local model")]
    [Description(WriteFileDescription)]
    public Task<CallToolResult> LocalWriteFile(
        [Description("New file to create, inside the project.")] string path,
        [Description("Exact spec: what to write, what to cover, style to follow.")] string task,
        RequestContext<CallToolRequestParams> context,
        [Description("Reference files/globs the model reads (the class under test, an existing file for style).")] string[]? context_paths = null,
        [Description("Optional allowlisted check, e.g. \"dotnet test --filter FooTests\".")] string? verify_command = null,
        [Description("On verify failure: how many times the model may rewrite the file (0-3).")] int fix_attempts = 1,
        [Description("Timeout of each verify run, seconds.")] int timeout_sec = 600,
        [Description("Allow replacing an existing file.")] bool overwrite = false,
        [Description("Include the first 30 lines of the written file in the result.")] bool preview = false,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.WriteFile, state, context,
            ctx => WriteFileTool.RunAsync(ctx, path, task, context_paths, verify_command, fix_attempts, timeout_sec, overwrite, preview),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.EditFiles, Title = "Offload: правка файлов",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "delegate mechanical multi-file edit refactor rename to local agent opencode")]
    [Description(EditFilesDescription)]
    public Task<CallToolResult> LocalEditFiles(
        [Description("Precise, mechanical spec; include a before/after example if possible.")] string task,
        [Description("The ONLY existing files/globs it may modify (max 50).")] string[] files,
        RequestContext<CallToolRequestParams> context,
        [Description("Reference files/globs to read (not modified).")] string[]? context_paths = null,
        [Description("Optional allowlisted check, e.g. \"dotnet build\".")] string? verify_command = null,
        [Description("On verify failure: fix rounds (0-4).")] int fix_attempts = 2,
        [Description("Overall time limit, 1-30 minutes.")] int timeout_minutes = 10,
        [Description("Compute the change, report it, then restore the originals.")] bool dry_run = false,
        [Description("auto | rewrite | agent")] string mode = "auto",
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.EditFiles, state, context,
            ctx => EditFilesTool.RunAsync(ctx, task, files, context_paths, verify_command, fix_attempts, timeout_minutes, dry_run, mode),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.Job, Title = "Offload: задача (diff/откат)",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "offload job diff revert undo local edit")]
    [Description(JobDescription)]
    public Task<CallToolResult> LocalJob(
        [Description("job_id from a local_write_file / local_edit_files result.")] string job_id,
        [Description("status | diff | revert")] string action,
        RequestContext<CallToolRequestParams> context,
        [Description("diff: maximum lines to return.")] int max_lines = 300,
        [Description("diff: only these files (paths or globs).")] string[]? paths = null,
        [Description("revert: overwrite files even if they changed after the job.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Job, state, context,
            ctx => Task.FromResult(JobTool.Run(ctx, job_id, action, max_lines, paths, force)), cancellationToken);
}
