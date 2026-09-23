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
    // ───────────────────────── описания (читает модель) ─────────────────────────

    internal const string StatusDescription =
        "Offload health check: whether the free local model is online (or how it will auto-start), model name and quant, " +
        "context size per request, measured speed, GPU queue, whether agent mode (OpenCode) is available for local_edit_files, " +
        "and cloud tokens saved (this session and lifetime). Cheap and side-effect free. Call it once before a long delegation " +
        "or when another Offload tool fails.";

    internal const string AskFilesDescription =
        "Ask the free local model a question about files WITHOUT reading them into your context. The server reads the files itself " +
        "(paths, directories or globs like \"src/**/*.cs\", relative to the project root or absolute; respects .gitignore; skips " +
        "binaries and secrets) and returns only the answer - typically 20-50x fewer tokens than Read. Answers cite path:line. " +
        "Good for: explain or summarize a file/folder/module/symbol, find where X is done, first-pass bug review, compare files. " +
        "Not for decisions that need whole-repo reasoning. Material larger than the local context is split and merged; coverage is " +
        "always reported. Answers come from a smaller model: spot-check what matters.";

    internal const string FindContextDescription =
        "START HERE for a task in an unfamiliar area instead of many Glob/Grep/Read calls. Give the task in natural language (any " +
        "language); the server finds the relevant files, classes and methods itself (task terms x symbol names x paths x content; " +
        "the local model expands the query, e.g. Russian words to code identifiers, and reranks with reasons) and returns a compact " +
        "context pack that fits budget_tokens: why each file was chosen, its outline and the relevant code with line numbers, plus " +
        "related tests. mode=rank lists files with reasons only; mode=plan adds a draft implementation plan (files, steps, tests, " +
        "risks). Read-only.";

    internal const string SearchCodeDescription =
        "Search the project on the server and get compact results: mode=text (substring), regex, word (whole identifier) or file " +
        "(file name/glob). Returns path:line matches with optional context lines, grouped by file, capped by max_results; respects " +
        ".gitignore and never reads secret files. Use files_only=true to just list matching files. Cheaper than Grep+Read when you " +
        "only need locations and short snippets. Read-only.";

    internal const string SymbolsDescription =
        "IDE-style code navigation without reading whole files (heuristic parser for C#, TS/JS, Python, Go, Java, Kotlin, Rust, " +
        "C/C++, Pascal, PHP, Ruby, Swift). action: outline (path: declarations with line ranges), find (name substring), definition, " +
        "references (textual, with the enclosing function), implementations (subtypes/implementers or same-named methods), callers / " +
        "callees (call graph, depth 1-4), tests (tests for a symbol or file), api (public API surface of paths), slice (just the body " +
        "of a function/class with line numbers). Names may be qualified: \"OrderService.Submit\". Read-only.";

    internal const string ProjectMapDescription =
        "Understand a repository in one call. section=overview (default): projects/modules with frameworks, references and packages, " +
        "languages, entry points, test projects, detected build/test/lint/format commands (marked if allowed for local_verify), top " +
        "folders, CI and Docker files, repo rule files. Other sections: entrypoints (incl. hosted services/workers), routes (HTTP " +
        "endpoints -> handlers), config (settings keys and where they are read), env (environment variables used), cli (commands and " +
        "options), ci, docker, rules (CLAUDE.md/AGENTS.md/CONTRIBUTING/.editorconfig contents), conventions (indentation, naming, " +
        "braces, async suffix, test style, a representative file to imitate). Read-only.";

    internal const string SummarizeLogDescription =
        "Digest a large log / build / test-output FILE with the local model instead of reading it. The server pre-filters it " +
        "(head, tail, windows around error/exception/fail/traceback/CS1234 lines, repeats collapsed) and returns: failing items, " +
        "the first root error with path:line, likely cause and next step. With pattern (regex or text, e.g. a request/job/thread id " +
        "or an error code) it instead returns the matching lines with line numbers and their most frequent shapes - no model, for " +
        "finding and correlating events. UTF-8/UTF-16/ANSI logs and color codes are handled.";

    internal const string ReviewDiffDescription =
        "First-pass code review of a git diff by the local model; the server runs git itself, so you don't read the diff. " +
        "target: \"all\" (default: staged + unstaged vs HEAD, plus untracked file names), \"staged\", \"unstaged\", or a git " +
        "ref/range (\"main\", \"HEAD~3\", \"main...HEAD\" for a whole branch). Returns prioritized findings: [severity] path:line - " +
        "issue - suggestion. Use focus for a specific lens (\"risky changes only\", \"architecture\", \"tests\"). Large diffs are " +
        "reviewed per file and merged; secret files are excluded. A cheap first pass: verify findings before acting.";

    internal const string CommitMessageDescription =
        "Write git text with the local model from a diff read server-side, so you don't read the diff. kind=commit (default): " +
        "a commit message for staged changes (subject <=72 chars, optional body; style conventional or plain). kind=pr: a pull " +
        "request title + Summary/Changes/Testing/Risks for source=all or a range like \"main...HEAD\". kind=split: how to split a " +
        "big diff into small reviewable commits. source: staged | unstaged | all | <git ref/range>. Returns only the text plus a " +
        "one-line stats footer after ---.";

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
        "local_job action=diff). Every job is snapshotted: undo with local_job action=revert. Slow (minutes); not for design work.";

    internal const string ApplyPatchDescription =
        "Apply a unified diff (git diff format; @@ line numbers may be approximate) to the project atomically: every hunk is matched " +
        "first (tolerant to shifted line numbers, CRLF/LF and trailing whitespace); if any hunk does not match, nothing is written " +
        "and the failing hunk is reported. Then files are snapshotted, written (encoding and line endings preserved; new, deleted " +
        "and renamed files supported), verify_command runs and, if it fails, everything is rolled back automatically. dry_run=true " +
        "only validates. Cheaper and safer than rewriting whole files: send just the hunks. Undo: local_job action=revert.";

    internal const string RefactorDescription =
        "Structured refactoring. action=rename (deterministic, no model): renames a symbol at its definition and all references " +
        "(whole-word, outside strings/comments, same language), renames the type's file (Foo.cs -> Bar.cs), refuses on ambiguity or " +
        "name conflicts unless force=true, snapshots, runs verify_command and rolls back on failure; dry_run shows the plan. " +
        "action=extract_method | extract_class | move_symbol | inline | change_signature: a precise brief is executed by the local " +
        "agent in an isolated git sandbox with a build check and merged only if it passes. Undo: local_job action=revert.";

    internal const string ImpactDescription =
        "Change impact analysis before or after an edit. Source: target (git diff: all | staged | unstaged | ref/range) or symbol. " +
        "Returns the changed symbols, their callers outside tests, related test files/classes (by references and naming) and the " +
        "affected projects. run_tests=true then runs ONLY the related tests with allowlisted commands (dotnet test with a class " +
        "filter, jest/vitest/pytest on the test files, go test on the packages) and reports pass/fail - much faster than the full " +
        "suite. Heuristic (textual references): confirm important callers yourself.";

    internal const string VerifyDescription =
        "Run a build/test/lint/format check on the server and get only the outcome instead of the whole output. Pass an allowlisted " +
        "command (e.g. \"dotnet test --filter FooTests\", \"npm test\", \"pytest -x\") or just kind=build|test|lint|format to use " +
        "the project's detected command. Returns PASSED/FAILED, test/build summary lines, warning count, distinct structured errors " +
        "(path:line code message), stack frames in project code and, on failure, a local-model diagnosis (root error, likely cause, " +
        "next step). The full log is saved under .offload/runs/ (git-ignored) for local_diagnostics or local_summarize_log.";

    internal const string DiagnosticsDescription =
        "Compiler/linter diagnostics as structured data: runs the project's build (kind=build, default) or lint (kind=lint) or an " +
        "allowlisted command, or parses an existing log (log_path), and returns errors/warnings as path:line:col severity code " +
        "message with the enclosing symbol and the source line, grouped by file and filterable by paths and severity. Also resolves " +
        "stack traces (.NET, Python, Node, Java, Go, Rust) to project files, functions and code lines. Understands MSBuild/csc, " +
        "tsc, eslint, gcc/clang, rustc/cargo, go, mypy/ruff output. No model.";

    internal const string CodeScanDescription =
        "Static project scans without a model, over the whole project or paths. check: todo (TODO/FIXME/HACK by tag), secrets " +
        "(leaked keys/tokens/passwords; values are masked), unsafe_api (shell/process execution, eval, SQL string building, unsafe " +
        "deserialization, disabled TLS, XSS sinks...), async (sync-over-async, async void, un-awaited promises), dead_code " +
        "(declarations never referenced), duplicates (copy-pasted blocks), complexity (most complex/long/nested functions), " +
        "hotspots (complexity x git churn: refactor/test first), generated (files not to edit by hand). Results cite path:line.";

    internal const string SecurityReviewDescription =
        "Security gate for CHANGED code only (git diff: all | staged | unstaged | ref/range, plus new untracked files). Rule-based " +
        "checks on added lines: leaked secrets (masked), dangerous APIs (command/SQL injection sinks, unsafe deserialization, TLS " +
        "disabled, XSS), removed validation/authorization checks, new dependencies and install scripts, security-sensitive files " +
        "touched; then (use_model=true) a local-model review focused on injection, authz, secrets, SSRF, crypto misuse. Secret files " +
        "are never read. Verify findings before acting.";

    internal const string GitHistoryDescription =
        "Git history without raw git output in your context. action=file (commits touching a file), symbol (commits that changed a " +
        "function/class body, via git log -L), blame (who/which commit wrote a line range or symbol, grouped by commit), related " +
        "(commits whose message or code changes mention a word: great for bugs), changelog (commits in a range grouped by type; " +
        "use_model=true writes user-facing release notes). Add question + use_model=true to have the local model answer \"why is the " +
        "code like this / which commit likely introduced this bug\" from the actual diffs. Read-only.";

    internal const string DependencyCheckDescription =
        "Dependency maintenance. action=list (direct packages per project), graph (project reference graph; with from and to: " +
        "the path explaining why A depends on B), licenses (from the local NuGet cache / node_modules, offline; flags copyleft), " +
        "unused (packages never imported: heuristic), outdated and vulnerable (run dotnet list package / npm outdated / npm audit / " +
        "pip list: these contact package registries and may restore packages). Returns compact tables, not raw tool output.";

    internal const string MemoryDescription =
        "Persistent project memory across sessions, stored in Offload's data folder (not in the repo). action=store (text + kind: " +
        "fact | decision | convention | note | todo, optional tags) to remember facts, architectural decisions (mini-ADR) and " +
        "conventions you discovered; recall (query) returns the most relevant entries; list; forget (id). Secrets are refused. " +
        "Recall at the start of a task in a known project to avoid re-discovering things; store decisions and non-obvious facts " +
        "at the end. Entries are notes, not truth: verify against the code.";

    internal const string AgentTaskDescription =
        "Delegate a CODING task to the free local agent (OpenCode + local model) in an isolated git sandbox: a snapshot of your " +
        "working tree (uncommitted files included, secrets excluded) becomes a worktree on branch offload/<job>; the agent edits and " +
        "creates files there, runs verify_command, fixes failures and commits. Autonomy budget: allowed_paths, max_files, " +
        "timeout_minutes, fix_attempts; review=true adds a local review before merge. Merges only if verify passes, the budget " +
        "holds and the patch applies: merge=apply (uncommitted changes, undo via local_job revert), commit (fast-forward), none. " +
        "background=true returns a job_id at once. Result = proof: files, verify, diagnostics, review, open risks.";

    internal const string SolveDescription =
        "One-call local coding loop for a whole task: finds the relevant code, briefs the local agent with a template for kind " +
        "(feature | bug: root cause + failing test + fix | refactor | tests | issue), lets it work in an isolated git sandbox, runs the " +
        "project's test/build command (auto-detected or verify_command) with fix rounds, reviews the change with the local model, " +
        "then merges (apply | commit | none) and returns a compact proof: changed files, verify result, diagnostics, review findings, " +
        "open questions. Bounded by allowed_paths, max_files and max_minutes; background=true to keep working. Review before accepting.";

    internal const string JobDescription =
        "Manage Offload jobs (local_write_file / local_edit_files / local_agent_task / local_solve / local_apply_patch / " +
        "local_refactor): action=list (recent jobs in this workspace), status (summary; wait_seconds>0 waits for a background job), " +
        "diff (unified diff; for an unmerged agent sandbox - its branch vs the snapshot), merge (apply an unmerged agent sandbox; " +
        "commit=true fast-forwards with a git commit), discard (drop an unmerged sandbox), cancel (stop a background job), revert " +
        "(restore the pre-job snapshot; files created by the job are deleted; refuses if a file changed after the job unless force=true).";

    // ───────────────────────── состояние и чтение ─────────────────────────

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

    [McpServerTool(Name = McpToolNames.FindContext, Title = "Offload: контекст под задачу",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/alwaysLoad", true)]
    [McpMeta("anthropic/searchHint", "find relevant files code context for a task rank budget pack plan where is implemented")]
    [Description(FindContextDescription)]
    public Task<CallToolResult> LocalFindContext(
        [Description("The task or question in natural language, e.g. \"add rate limiting to the login endpoint\".")] string task,
        RequestContext<CallToolRequestParams> context,
        [Description("Optional scope: folders/globs to search (default: whole project).")] string[]? paths = null,
        [Description("Files to include, 1-25.")] int max_files = 8,
        [Description("Size of the returned pack in tokens, 500-15000.")] int budget_tokens = 3000,
        [Description("pack | rank | plan")] string mode = "pack",
        [Description("Use the local model to expand the query and rerank (slower, better; auto-falls back if offline).")] bool use_model = true,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.FindContext, state, context,
            ctx => FindContextTool.RunAsync(ctx, task, paths, max_files, budget_tokens, mode, use_model), cancellationToken);

    [McpServerTool(Name = McpToolNames.SearchCode, Title = "Offload: поиск по коду",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "search code grep regex text word file name glob find usages snippets")]
    [Description(SearchCodeDescription)]
    public Task<CallToolResult> LocalSearchCode(
        [Description("What to search: text, regex, identifier or file name/glob depending on mode.")] string query,
        RequestContext<CallToolRequestParams> context,
        [Description("text | regex | word | file")] string mode = "text",
        [Description("Optional scope: folders/files/globs.")] string[]? paths = null,
        [Description("Case-sensitive match.")] bool case_sensitive = false,
        [Description("Context lines around each match, 0-5.")] int context_lines = 0,
        [Description("Maximum matches, 1-500.")] int max_results = 60,
        [Description("Only list files with match counts.")] bool files_only = false,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.SearchCode, state, context,
            ctx => SearchCodeTool.RunAsync(ctx, query, mode, paths, case_sensitive, context_lines, max_results, files_only), cancellationToken);

    [McpServerTool(Name = McpToolNames.Symbols, Title = "Offload: символы и граф вызовов",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "outline definition references implementations callers callees call graph tests api surface symbol")]
    [Description(SymbolsDescription)]
    public Task<CallToolResult> LocalSymbols(
        [Description("outline | find | definition | references | implementations | callers | callees | tests | api | slice")] string action,
        RequestContext<CallToolRequestParams> context,
        [Description("Symbol name, optionally qualified (\"OrderService.Submit\"); for find - a substring.")] string? name = null,
        [Description("File for outline/tests, or a folder/file scope for api/find.")] string? path = null,
        [Description("Optional scope: folders/globs to index (default: whole project).")] string[]? paths = null,
        [Description("callers/callees: depth 1-4.")] int depth = 1,
        [Description("Maximum results (for slice: roughly max lines / 3).")] int max_results = 50,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Symbols, state, context,
            ctx => SymbolsTool.RunAsync(ctx, action, name, path, paths, depth, max_results), cancellationToken);

    [McpServerTool(Name = McpToolNames.ProjectMap, Title = "Offload: карта проекта",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "project map structure modules entry points routes endpoints config env vars cli ci docker conventions rules")]
    [Description(ProjectMapDescription)]
    public Task<CallToolResult> LocalProjectMap(
        RequestContext<CallToolRequestParams> context,
        [Description("overview | entrypoints | routes | config | env | cli | ci | docker | rules | conventions")] string section = "overview",
        [Description("Maximum items for list sections.")] int max_results = 80,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.ProjectMap, state, context, ctx => ProjectMapTool.RunAsync(ctx, section, max_results), cancellationToken);

    [McpServerTool(Name = McpToolNames.SummarizeLog, Title = "Offload: разбор лога",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "summarize big log build output test failures stack trace grep correlate request id")]
    [Description(SummarizeLogDescription)]
    public Task<CallToolResult> LocalSummarizeLog(
        [Description("Log file path (relative to the project root or absolute).")] string path,
        RequestContext<CallToolRequestParams> context,
        [Description("What to look for.")] string focus = "errors, failing tests, root cause",
        [Description("Only consider the last N lines (0 = whole file).")] int tail_lines = 0,
        [Description("Answer length limit, 64-4096 tokens.")] int max_answer_tokens = 600,
        [Description("Optional regex/text: return matching lines (no model) instead of a summary.")] string? pattern = null,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.SummarizeLog, state, context,
            ctx => SummarizeLogTool.RunAsync(ctx, path, focus, tail_lines, max_answer_tokens, pattern), cancellationToken);

    [McpServerTool(Name = McpToolNames.ReviewDiff, Title = "Offload: ревью diff",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "code review git diff changes branch pr local model first pass risk")]
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

    [McpServerTool(Name = McpToolNames.CommitMessage, Title = "Offload: коммит / описание PR",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "generate git commit message pull request description split commits from diff")]
    [Description(CommitMessageDescription)]
    public Task<CallToolResult> LocalCommitMessage(
        RequestContext<CallToolRequestParams> context,
        [Description("staged | unstaged | all | <git ref/range> (default: staged for commit, all for pr).")] string? source = null,
        [Description("conventional | plain")] string style = "conventional",
        [Description("Message language, e.g. en or ru.")] string language = "en",
        [Description("Git repository folder (default: project root).")] string? working_directory = null,
        [Description("commit | pr | split")] string kind = "commit",
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.CommitMessage, state, context,
            ctx => CommitMessageTool.RunAsync(ctx, source, style, language, working_directory, kind), cancellationToken);

    [McpServerTool(Name = McpToolNames.CodeScan, Title = "Offload: статический анализ",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "scan todo secrets unsafe api dead code duplicates complexity hotspots async issues generated files")]
    [Description(CodeScanDescription)]
    public Task<CallToolResult> LocalCodeScan(
        [Description("todo | secrets | unsafe_api | async | dead_code | duplicates | complexity | hotspots | generated")] string check,
        RequestContext<CallToolRequestParams> context,
        [Description("Optional scope: folders/globs.")] string[]? paths = null,
        [Description("Maximum findings.")] int max_results = 50,
        [Description("Also scan test files.")] bool include_tests = false,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.CodeScan, state, context, ctx => CodeScanTool.RunAsync(ctx, check, paths, max_results, include_tests), cancellationToken);

    [McpServerTool(Name = McpToolNames.SecurityReview, Title = "Offload: безопасность изменений",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "security review diff secrets injection unsafe api authorization vulnerable changes gate")]
    [Description(SecurityReviewDescription)]
    public Task<CallToolResult> LocalSecurityReview(
        RequestContext<CallToolRequestParams> context,
        [Description("all | staged | unstaged | <git ref or range>")] string target = "all",
        [Description("Add a local-model security review after the rule-based checks.")] bool use_model = true,
        [Description("Maximum rule findings.")] int max_results = 60,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.SecurityReview, state, context, ctx => SecurityReviewTool.RunAsync(ctx, target, use_model, max_results), cancellationToken);

    [McpServerTool(Name = McpToolNames.GitHistory, Title = "Offload: история git",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "git history log blame who changed why related commits changelog release notes regression")]
    [Description(GitHistoryDescription)]
    public Task<CallToolResult> LocalGitHistory(
        RequestContext<CallToolRequestParams> context,
        [Description("file | symbol | blame | related | changelog")] string action = "file",
        [Description("File path (file/blame; optional scope for symbol).")] string? path = null,
        [Description("Symbol name for symbol/blame, e.g. \"OrderService.Submit\".")] string? symbol = null,
        [Description("blame: line range like 120-160.")] string? lines = null,
        [Description("related: word to find in commit messages and code changes.")] string? query = null,
        [Description("changelog: git range, default last tag..HEAD.")] string? range = null,
        [Description("Optional question for the local model about this history.")] string? question = null,
        [Description("Use the local model (question answering, release notes).")] bool use_model = false,
        [Description("Maximum commits, 1-200.")] int max_commits = 15,
        [Description("Release notes language, e.g. en or ru.")] string? language = null,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.GitHistory, state, context,
            ctx => GitHistoryTool.RunAsync(ctx, action, path, symbol, lines, query, range, question, use_model, max_commits, language), cancellationToken);

    // ───────────────────────── запуск проверок ─────────────────────────

    [McpServerTool(Name = McpToolNames.Verify, Title = "Offload: сборка/тесты",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "run build test lint format command summarize failures errors warnings")]
    [Description(VerifyDescription)]
    public Task<CallToolResult> LocalVerify(
        RequestContext<CallToolRequestParams> context,
        [Description("Allowlisted command, e.g. \"dotnet test --filter FooTests\", \"npm test\". Omit to use kind.")] string? command = null,
        [Description("build | test | lint | format: auto-detects the project's command when command is omitted.")] string kind = "test",
        [Description("Timeout, seconds (10-3600).")] int timeout_sec = 900,
        [Description("On failure: let the local model analyze the log.")] bool analyze = true,
        [Description("What the analysis should focus on.")] string? focus = null,
        [Description("Analysis length limit, 64-4096 tokens.")] int max_answer_tokens = 500,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Verify, state, context,
            ctx => VerifyTool.RunAsync(ctx, command, kind, timeout_sec, focus, analyze, max_answer_tokens), cancellationToken);

    [McpServerTool(Name = McpToolNames.Diagnostics, Title = "Offload: диагностика сборки",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "compiler errors warnings lint diagnostics stack trace resolve file line symbol build failure")]
    [Description(DiagnosticsDescription)]
    public Task<CallToolResult> LocalDiagnostics(
        RequestContext<CallToolRequestParams> context,
        [Description("build | lint (used when command and log_path are omitted).")] string kind = "build",
        [Description("Optional allowlisted command to run instead of the detected one.")] string? command = null,
        [Description("Parse this existing log file instead of running anything.")] string? log_path = null,
        [Description("Only diagnostics in these files/folders/globs.")] string[]? paths = null,
        [Description("error | warning (errors+warnings) | all")] string severity = "error",
        [Description("Maximum diagnostics.")] int max_results = 40,
        [Description("Timeout, seconds.")] int timeout_sec = 900,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Diagnostics, state, context,
            ctx => DiagnosticsTool.RunAsync(ctx, command, kind, log_path, paths, severity, max_results, timeout_sec), cancellationToken);

    [McpServerTool(Name = McpToolNames.Impact, Title = "Offload: влияние изменений",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "change impact affected callers related tests run only affected tests test impact projects")]
    [Description(ImpactDescription)]
    public Task<CallToolResult> LocalImpact(
        RequestContext<CallToolRequestParams> context,
        [Description("all | staged | unstaged | <git ref or range> (ignored when symbol is set).")] string target = "all",
        [Description("Analyze this symbol instead of the diff, e.g. \"OrderService.Submit\".")] string? symbol = null,
        [Description("Run only the related tests.")] bool run_tests = false,
        [Description("Maximum items per list.")] int max_results = 40,
        [Description("Timeout per test command, seconds.")] int timeout_sec = 900,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Impact, state, context, ctx => ImpactTool.RunAsync(ctx, target, symbol, run_tests, max_results, timeout_sec), cancellationToken);

    [McpServerTool(Name = McpToolNames.Dependencies, Title = "Offload: зависимости",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [McpMeta("anthropic/searchHint", "dependencies packages outdated vulnerable cve licenses unused dependency graph why depends")]
    [Description(DependencyCheckDescription)]
    public Task<CallToolResult> LocalDependencyCheck(
        RequestContext<CallToolRequestParams> context,
        [Description("list | graph | licenses | unused | outdated | vulnerable")] string action = "list",
        [Description("graph: start project/package for the dependency path.")] string? from = null,
        [Description("graph: target project/package.")] string? to = null,
        [Description("Maximum rows.")] int max_results = 80,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Dependencies, state, context, ctx => DependencyCheckTool.RunAsync(ctx, action, from, to, max_results), cancellationToken);

    // ───────────────────────── запись ─────────────────────────

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

    [McpServerTool(Name = McpToolNames.ApplyPatch, Title = "Offload: применить патч",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "apply unified diff patch atomically hunks verify rollback")]
    [Description(ApplyPatchDescription)]
    public Task<CallToolResult> LocalApplyPatch(
        [Description("Unified diff text (--- a/path, +++ b/path, @@ hunks).")] string patch,
        RequestContext<CallToolRequestParams> context,
        [Description("Optional allowlisted check run after applying, e.g. \"dotnet build\".")] string? verify_command = null,
        [Description("Only check that the patch applies; write nothing.")] bool dry_run = false,
        [Description("Restore all files automatically if verify fails.")] bool rollback_on_failure = true,
        [Description("Verify timeout, seconds.")] int timeout_sec = 900,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.ApplyPatch, state, context,
            ctx => ApplyPatchTool.RunAsync(ctx, patch, verify_command, dry_run, rollback_on_failure, timeout_sec), cancellationToken);

    [McpServerTool(Name = McpToolNames.Refactor, Title = "Offload: рефакторинг",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "refactor rename symbol safe rename extract method extract class move symbol inline change signature")]
    [Description(RefactorDescription)]
    public Task<CallToolResult> LocalRefactor(
        [Description("Symbol to refactor, optionally qualified (\"OrderService.Submit\").")] string name,
        RequestContext<CallToolRequestParams> context,
        [Description("rename | extract_method | extract_class | move_symbol | inline | change_signature")] string action = "rename",
        [Description("New name (rename, extract_method, extract_class).")] string? new_name = null,
        [Description("Optional scope: folders/globs where references are updated.")] string[]? paths = null,
        [Description("move_symbol/extract_class: target file.")] string? target_file = null,
        [Description("extract_method: line range, e.g. 120-140.")] string? lines = null,
        [Description("Extra details for agent-based actions.")] string? instructions = null,
        [Description("Allowlisted check, e.g. \"dotnet build\" (rename: rolls back on failure).")] string? verify_command = null,
        [Description("rename: show the plan only.")] bool dry_run = false,
        [Description("rename: proceed despite same-named symbols or name conflicts.")] bool force = false,
        [Description("rename: also rename Foo.cs to NewName.cs for types.")] bool rename_file = true,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Refactor, state, context,
            ctx => RefactorTool.RunAsync(ctx, action, name, new_name, paths, target_file, lines, instructions, verify_command, dry_run, force, rename_file),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.AgentTask, Title = "Offload: задача агенту (git-песочница)",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "delegate coding task local agent opencode git worktree sandbox branch implement feature write code merge")]
    [Description(AgentTaskDescription)]
    public Task<CallToolResult> LocalAgentTask(
        [Description("The coding task: goal, acceptance criteria, constraints, style to follow. Name files/classes when known.")] string task,
        RequestContext<CallToolRequestParams> context,
        [Description("Allowlisted check run in the sandbox after the agent, e.g. \"dotnet test --filter FooTests\". Strongly recommended.")] string? verify_command = null,
        [Description("Files/folders/globs (relative) the agent should read first.")] string[]? context_paths = null,
        [Description("apply | commit | none")] string merge = "apply",
        [Description("On verify failure: fix rounds (0-4).")] int fix_attempts = 2,
        [Description("Overall time limit, 2-90 minutes.")] int timeout_minutes = 20,
        [Description("Return a job_id immediately and run in the background (poll with local_job action=status wait_seconds=...).")] bool background = false,
        [Description("Only changes under these folders/files/globs are merged.")] string[]? allowed_paths = null,
        [Description("Do not auto-merge if more files changed (0 = no limit).")] int max_files = 0,
        [Description("Review the change with the local model before merging; critical/high findings block the auto-merge.")] bool review = false,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.AgentTask, state, context,
            ctx => AgentTaskTool.RunAsync(ctx, new AgentTaskRequest
            {
                Task = task,
                VerifyCommand = verify_command,
                ContextPaths = context_paths,
                Merge = merge,
                FixAttempts = fix_attempts,
                TimeoutMinutes = timeout_minutes,
                Background = background,
                AllowedPaths = allowed_paths,
                MaxFiles = max_files,
                Review = review,
            }),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.Solve, Title = "Offload: решить задачу локально",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "solve issue fix bug implement feature refactor write tests end to end local agent pipeline proof")]
    [Description(SolveDescription)]
    public Task<CallToolResult> LocalSolve(
        [Description("The task, bug report (with stack trace/steps) or issue text, with acceptance criteria.")] string task,
        RequestContext<CallToolRequestParams> context,
        [Description("feature | bug | refactor | tests | issue")] string kind = "feature",
        [Description("Allowlisted check; default: the project's test (or build) command.")] string? verify_command = null,
        [Description("Only changes under these folders/files/globs are merged.")] string[]? allowed_paths = null,
        [Description("Extra files/folders the agent should read first.")] string[]? context_paths = null,
        [Description("apply | commit | none")] string merge = "apply",
        [Description("Do not auto-merge if more files changed.")] int max_files = 25,
        [Description("Overall time limit, minutes.")] int max_minutes = 30,
        [Description("Run in the background and return a job_id.")] bool background = false,
        [Description("Review the change with the local model before merging.")] bool review = true,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Solve, state, context,
            ctx => SolveTool.RunAsync(ctx, task, kind, verify_command, allowed_paths, context_paths, merge, max_files, max_minutes, background, review),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.Memory, Title = "Offload: память проекта",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "project memory remember facts decisions conventions recall notes adr")]
    [Description(MemoryDescription)]
    public Task<CallToolResult> LocalMemory(
        RequestContext<CallToolRequestParams> context,
        [Description("store | recall | list | forget")] string action = "recall",
        [Description("store: the fact/decision to remember (one or two sentences).")] string? text = null,
        [Description("fact | decision | convention | note | todo")] string? kind = null,
        [Description("store: optional tags.")] string[]? tags = null,
        [Description("recall: what you are looking for.")] string? query = null,
        [Description("forget: entry id.")] string? id = null,
        [Description("Maximum entries.")] int max_results = 10,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Memory, state, context,
            ctx => Task.FromResult(MemoryTool.Run(ctx, action, text, kind, tags, query, id, max_results)), cancellationToken);

    [McpServerTool(Name = McpToolNames.Job, Title = "Offload: задачи (список/diff/слияние/откат)",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/searchHint", "offload job list status diff merge discard cancel revert undo sandbox branch")]
    [Description(JobDescription)]
    public Task<CallToolResult> LocalJob(
        [Description("list | status | diff | merge | discard | cancel | revert")] string action,
        RequestContext<CallToolRequestParams> context,
        [Description("job_id from a tool result or action=list (not needed for list).")] string? job_id = null,
        [Description("diff: maximum lines to return.")] int max_lines = 300,
        [Description("diff: only these files (paths or globs).")] string[]? paths = null,
        [Description("revert: overwrite files even if they changed after the job.")] bool force = false,
        [Description("merge: fast-forward with a git commit instead of applying uncommitted changes.")] bool commit = false,
        [Description("status: wait up to N seconds (max 600) for a background job to finish.")] int wait_seconds = 0,
        CancellationToken cancellationToken = default) =>
        ToolRunner.RunAsync(McpToolNames.Job, state, context,
            ctx => JobTool.RunAsync(ctx, job_id, action, max_lines, paths, force, commit, wait_seconds), cancellationToken);
}
