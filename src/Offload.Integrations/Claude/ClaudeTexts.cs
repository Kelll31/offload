using Offload.Core;

namespace Offload.Integrations.Claude;

/// <summary>Тексты файлов для Claude Code (на английском — их читает модель). Метка управления — «x-offload: managed».</summary>
internal static class ClaudeTexts
{
    public const string Marker = "x-offload: managed";

    private static string N(string tool) => McpToolNames.ClaudeCodeName(tool);

    /// <summary>Описание навыка (вместе с when_to_use Claude Code обрезает до 1536 символов).</summary>
    public const string SkillDescription =
        "Offload bulky, low-risk coding work to the free local Offload model (mcp__offload__* tools) to save Claude tokens - " +
        "answering questions about large files without reading them, digesting big logs and test output, a first-pass review of a git diff, " +
        "writing tests/boilerplate into new files, mechanical multi-file edits verified by a command, commit messages.";

    public const string SkillWhenToUse =
        "User says \"use the local model\", \"save tokens\", \"offload\" or \"offload\"; or you are about to Read files over ~300 lines, " +
        "read a long log/test output, review a large diff, or generate many lines of routine code.";

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
        | Understand/summarize/review files you haven't read | {{McpToolNames.AskFiles}}(paths, question) |
        | Big log / test / build output | redirect it to a file (e.g. .offload/build.log), then {{McpToolNames.SummarizeLog}}(path, focus) |
        | First-pass review of uncommitted changes | {{McpToolNames.ReviewDiff}}(target, focus) |
        | New file from a spec (tests, DTOs, fixtures, docs) | {{McpToolNames.WriteFile}}(path, task, context_paths, verify_command) |
        | Mechanical edit across files | {{McpToolNames.EditFiles}}(task, files, verify_command) -> review the diffstat; {{McpToolNames.Job}}(job_id, action=diff or revert) |
        | Commit message | {{McpToolNames.CommitMessage}}() |
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
        - Before reading a file over ~300 lines or a log/test output over ~200 lines, prefer {{N(McpToolNames.AskFiles)}} / {{N(McpToolNames.SummarizeLog)}}.
        - For a first-pass review of a large diff use {{N(McpToolNames.ReviewDiff)}}; for commit messages use {{N(McpToolNames.CommitMessage)}}.
        - For new boilerplate files (tests, DTOs, fixtures) and mechanical multi-file edits that a build/test command can verify, prefer {{N(McpToolNames.WriteFile)}} / {{N(McpToolNames.EditFiles)}}, then review the returned summary instead of re-reading files.
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
        2. For each item call {{N(McpToolNames.WriteFile)}} (new files) or {{N(McpToolNames.EditFiles)}} (existing files) with explicit paths, a precise spec and a verify_command.
        3. If verify fails after the tool's own retries, try once more with a sharper spec that quotes the error; then stop and report the item as failed.
        4. Use Read/Grep only to spot-check a few lines, never to review whole files. Use {{N(McpToolNames.Job)}} action=diff to inspect a change and action=revert to undo a bad one.
        Report: items done/failed, files changed (+/- lines), verify results, job_ids for revert, and anything the main agent must decide.
        """.ReplaceLineEndings("\n") + "\n";
}
