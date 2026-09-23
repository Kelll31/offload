using Offload.Core;

namespace Offload.Integrations.Claude;

/// <summary>Тексты файлов для Claude Code (на английском — их читает модель). Метка управления — «x-offload: managed».</summary>
internal static class ClaudeTexts
{
    public const string Marker = "x-offload: managed";

    private static string N(string tool) => McpToolNames.ClaudeCodeName(tool);

    /// <summary>Описание навыка (вместе с when_to_use Claude Code обрезает до 1536 символов).</summary>
    public const string SkillDescription =
        "Offload bulky, low-risk coding work to the free local Offload model and agent (mcp__offload__* tools) to save Claude tokens - " +
        "finding the relevant code for a task, code navigation (symbols, references, call graph), questions about files without reading them, " +
        "running builds/tests and digesting errors, impact analysis and targeted tests, static scans, diff and security review, " +
        "whole coding tasks done by the local agent in an isolated git sandbox and merged back with proof, safe patches and renames, " +
        "git history, dependency checks, project memory, commit/PR text.";

    public const string SkillWhenToUse =
        "User says \"use the local model\", \"save tokens\", \"offload\" or \"offload\"; or you are about to Read files over ~300 lines, " +
        "Glob/Grep/Read your way through an unfamiliar area, run a build/test and read its output, review a large diff, " +
        "or write many lines of well-specified code.";

    public static string Skill() =>
        $$"""
        ---
        name: offload
        description: {{SkillDescription}}
        when_to_use: {{SkillWhenToUse}}
        argument-hint: [task to offload]
        allowed-tools: {{string.Join(" ", McpToolNames.ReadOnly.Select(N))}}
        {{Marker}}
        ---
        # Delegating to Offload (local LLM)

        If $ARGUMENTS is non-empty, offload that task now using the playbook below.
        If the mcp__offload__* tools are not available in this session, say so and do the task yourself.
        If they are deferred, load them with ToolSearch query "offload".

        ## Decide
        Delegate when the work is bounded, checkable, and token-heavy (big inputs or lots of routine output).
        Keep it yourself when it needs cross-module reasoning, security judgement, or is faster to do than to brief.

        ## Pick the tool
        | Need | Tool |
        |---|---|
        | Start a task in unfamiliar code: which files/symbols matter | {{McpToolNames.FindContext}}(task, budget_tokens; mode=plan for a plan) |
        | Repo overview, entry points, routes, config/env keys, CI, conventions, repo rules | {{McpToolNames.ProjectMap}}(section) |
        | Find text/regex/identifier/file | {{McpToolNames.SearchCode}}(query, mode) |
        | Outline, definition, references, callers/callees, tests, public API, one function body | {{McpToolNames.Symbols}}(action, name/path) |
        | Understand/summarize/review files you haven't read | {{McpToolNames.AskFiles}}(paths, question) |
        | Why is the code like this, who changed it, related commits, changelog | {{McpToolNames.GitHistory}}(action) |
        | Remember/recall project facts and decisions across sessions | {{McpToolNames.Memory}}(action) |
        | Run build/tests/lint, get only the outcome | {{McpToolNames.Verify}}(kind or command) -> pass/fail, errors, diagnosis |
        | Structured compiler errors, stack trace -> code | {{McpToolNames.Diagnostics}}(kind or log_path) |
        | Other big log (summary or grep by id) | {{McpToolNames.SummarizeLog}}(path, focus or pattern) |
        | What does my change affect, run only related tests | {{McpToolNames.Impact}}(target or symbol, run_tests) |
        | TODOs, leaked secrets, risky APIs, dead code, duplicates, complexity, hotspots | {{McpToolNames.CodeScan}}(check) |
        | Review of a diff/branch; security gate for changes | {{McpToolNames.ReviewDiff}}(target, focus) / {{McpToolNames.SecurityReview}}(target) |
        | Packages: list, why A depends on B, outdated, vulnerable, licenses, unused | {{McpToolNames.Dependencies}}(action) |
        | Whole task end-to-end (feature, bug, refactor, tests, issue) | {{McpToolNames.Solve}}(task, kind) -> proof: files, check, review, open questions |
        | Coding task with your own brief and autonomy budget | {{McpToolNames.AgentTask}}(task, verify_command, allowed_paths, max_files, background) |
        | Apply your own small diff safely | {{McpToolNames.ApplyPatch}}(patch, verify_command) |
        | Rename a symbol everywhere; extract/move/inline | {{McpToolNames.Refactor}}(name, action, new_name) |
        | New file from a spec (tests, DTOs, fixtures, docs) | {{McpToolNames.WriteFile}}(path, task, context_paths, verify_command) |
        | Mechanical edit of listed files | {{McpToolNames.EditFiles}}(task, files, verify_command) |
        | Jobs: list, background status, diff, merge/discard a sandbox, undo | {{McpToolNames.Job}}(action, job_id) |
        | Commit message, PR description, commit split | {{McpToolNames.CommitMessage}}(kind) |
        | Is the local model up, how fast is it? | {{McpToolNames.Status}}() |

        ## Brief like it can't see anything
        Pass paths, never pasted file content; state the exact output format and constraints ("no preamble", "match the style of X"); one task per call.
        Good: task="Write 4 xUnit tests for ParseHeader: empty input, >10MB, invalid UTF-8, concurrent calls. Match the style of tests/ParserTests.cs."

        ## Verify proportionally
        Docs/messages: skim. Code: rely on the verify_command exit code; spot-check one file or ask for a diff of <=100 lines.
        Never re-read every touched file. If quality is poor twice, do it yourself and tell the user.
        """.ReplaceLineEndings("\n") + "\n";

    public static string StrongRule() =>
        $$"""
        <!-- {{Marker}}: installed by Offload ("strong mode"); remove it in Offload settings. -->
        # Local model delegation (Offload)
        If mcp__offload__* tools are available in this session:
        - To find the code relevant to a task, start with {{N(McpToolNames.FindContext)}} instead of many Glob/Grep/Read calls; navigate with {{N(McpToolNames.Symbols)}} / {{N(McpToolNames.SearchCode)}}; get a repo overview with {{N(McpToolNames.ProjectMap)}}.
        - Before reading a file over ~300 lines or a log/test output over ~200 lines, prefer {{N(McpToolNames.AskFiles)}} / {{N(McpToolNames.SummarizeLog)}}.
        - To run a build or tests, prefer {{N(McpToolNames.Verify)}} over running them in the shell; for structured errors use {{N(McpToolNames.Diagnostics)}}; before/after edits check {{N(McpToolNames.Impact)}}.
        - For a first-pass review of a large diff use {{N(McpToolNames.ReviewDiff)}} (and {{N(McpToolNames.SecurityReview)}} for risky changes); for commit/PR text use {{N(McpToolNames.CommitMessage)}}.
        - For well-specified coding tasks a build/test command can verify, prefer {{N(McpToolNames.Solve)}} or {{N(McpToolNames.AgentTask)}} (isolated git sandbox, merged back only if checks pass; background=true while you work); for your own small diffs {{N(McpToolNames.ApplyPatch)}}, for renames {{N(McpToolNames.Refactor)}}, for single new files or edits of listed files {{N(McpToolNames.WriteFile)}} / {{N(McpToolNames.EditFiles)}}. Review the returned proof/diffstat instead of re-reading files.
        - Recall project facts with {{N(McpToolNames.Memory)}} at the start of a task and store non-obvious decisions at the end.
        - Keep architecture, debugging, security-sensitive code and final review for yourself.
        If those tools are not available, ignore this file.
        """.ReplaceLineEndings("\n") + "\n";

    public static string RunnerAgent() =>
        $$"""
        ---
        name: offload-runner
        description: Use proactively for bulk mechanical chores that a command can verify (generate tests for N classes, apply one pattern across many files, fix a batch of lint warnings). Drives the free local Offload model and returns a <=150-word report. Not for design, debugging or security work.
        tools: mcp__{{AppInfo.McpServerId}}, Read, Grep, Glob
        model: haiku
        effort: low
        maxTurns: 12
        color: green
        {{Marker}}
        ---
        You coordinate the local Offload model; you do not write code yourself.
        1. Split the task into small, independent items (one file or one pattern each).
        2. For each item call {{N(McpToolNames.WriteFile)}} (new files), {{N(McpToolNames.EditFiles)}} (listed existing files), {{N(McpToolNames.Refactor)}} (renames) or {{N(McpToolNames.AgentTask)}} (anything spanning unknown files) with explicit paths, a precise spec and a verify_command. Use {{N(McpToolNames.FindContext)}} or {{N(McpToolNames.Symbols)}} to locate code.
        3. If verify fails after the tool's own retries, try once more with a sharper spec that quotes the error; then stop and report the item as failed.
        4. Use Read/Grep only to spot-check a few lines, never to review whole files. Use {{N(McpToolNames.Job)}} action=diff to inspect a change and action=revert to undo a bad one; {{N(McpToolNames.Verify)}} for a final build/test run.
        Report: items done/failed, files changed (+/- lines), verify results, job_ids for revert, and anything the main agent must decide.
        """.ReplaceLineEndings("\n") + "\n";
}
