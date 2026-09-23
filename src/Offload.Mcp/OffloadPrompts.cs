using System.ComponentModel;
using ModelContextProtocol.Server;
using Offload.Core;

namespace Offload.Mcp;

/// <summary>
/// MCP-подсказки (prompts) Offload. В Claude Code они видны как слэш-команды /mcp__offload__&lt;имя&gt; и разворачиваются
/// в инструкцию для модели: какой инструмент Offload вызвать и как проверить результат. Тексты — на английском (их читает модель).
/// </summary>
[McpServerPromptType]
public sealed class OffloadPrompts
{
    internal const string Delegate = "delegate";
    internal const string Review = "review";
    internal const string Tests = "tests";
    internal const string FixBuild = "fix_build";
    internal const string Explain = "explain";
    internal const string Solve = "solve";
    internal const string Context = "context";
    internal const string Security = "security";

    internal static readonly IReadOnlyList<string> All = [Delegate, Review, Tests, FixBuild, Explain, Solve, Context, Security];

    [McpServerPrompt(Name = Solve, Title = "Offload: solve a task end-to-end locally")]
    [Description("Let the local agent do the whole loop (context, code, tests, review) in a git sandbox and check its proof.")]
    public static string SolvePrompt(
        [Description("The task, bug report or issue text.")] string task,
        [Description("feature | bug | refactor | tests | issue")] string? kind = null) =>
        $"""
        Solve this with the local Offload pipeline, keeping your own work to briefing and review:

        {task.Trim()}

        1. If the task is vague, first call {McpToolNames.ClaudeCodeName(McpToolNames.FindContext)} (mode=rank) to see where it lands, and recall
           project facts with {McpToolNames.ClaudeCodeName(McpToolNames.Memory)} action=recall. Add acceptance criteria and constraints to the task text.
        2. Call {McpToolNames.ClaudeCodeName(McpToolNames.Solve)} with kind="{(string.IsNullOrWhiteSpace(kind) ? "feature" : kind.Trim())}" (add allowed_paths if the change
           must stay in one area; background=true if you have other work).
        3. Read the returned proof: verify result, review findings, open questions. If merged, spot-check the riskiest changed file; if not merged,
           inspect {McpToolNames.ClaudeCodeName(McpToolNames.Job)} action=diff and merge, fix or discard. Run {McpToolNames.ClaudeCodeName(McpToolNames.Impact)} run_tests=true if unsure.
        4. Store any non-obvious decision with {McpToolNames.ClaudeCodeName(McpToolNames.Memory)} action=store kind=decision.
        """;

    [McpServerPrompt(Name = Context, Title = "Offload: gather context for a task")]
    [Description("Find the code relevant to a task without Glob/Grep/Read round-trips.")]
    public static string ContextPrompt(
        [Description("What you are about to work on.")] string task) =>
        $"""
        Before touching code for this task, gather context cheaply:

        {task.Trim()}

        1. {McpToolNames.ClaudeCodeName(McpToolNames.Memory)} action=recall query="<key words>" — known facts and decisions.
        2. {McpToolNames.ClaudeCodeName(McpToolNames.FindContext)} task="<the task>" budget_tokens=3000 (mode=plan if you want a draft plan).
        3. For anything still unclear use {McpToolNames.ClaudeCodeName(McpToolNames.Symbols)} (definition/references/callers/slice) instead of reading whole files.
        Then summarize: the files and symbols involved, how they connect, and the plan.
        """;

    [McpServerPrompt(Name = Security, Title = "Offload: security gate for current changes")]
    [Description("Rule-based + local-model security review of the current diff; you confirm each finding.")]
    public static string SecurityPrompt(
        [Description("all | staged | unstaged | <git ref or range>, default all.")] string? target = null) =>
        $"""
        Run a security gate on the changes.
        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.SecurityReview)} target="{(string.IsNullOrWhiteSpace(target) ? "all" : target.Trim())}".
        2. For dependency changes also call {McpToolNames.ClaudeCodeName(McpToolNames.Dependencies)} action=vulnerable (asks the package registry) if the user agrees.
        3. Verify every finding by reading only the cited lines; drop false positives; never print secret values.
        4. Report confirmed issues most severe first with a concrete fix.
        """;

    [McpServerPrompt(Name = Delegate, Title = "Offload: delegate a coding task to the local agent")]
    [Description("Have the free local agent implement a coding task in an isolated git sandbox and merge it back; you review the result.")]
    public static string DelegatePrompt(
        [Description("What to implement or change, with acceptance criteria.")] string task,
        [Description("Optional allowlisted check, e.g. \"dotnet test\".")] string? verify_command = null) =>
        $"""
        Delegate this coding task to the local Offload agent instead of writing the code yourself:

        {task.Trim()}

        Steps:
        1. Turn the task into a precise brief for a smaller model: goal, files/classes to touch (look them up quickly if unknown),
           constraints, code style, acceptance criteria. Keep design decisions yourself and put them into the brief.
        2. Call {McpToolNames.ClaudeCodeName(McpToolNames.AgentTask)} with that brief, context_paths with the key files, merge="apply"
           and verify_command={(string.IsNullOrWhiteSpace(verify_command) ? "the project's build/test command from the Offload allowlist" : $"\"{verify_command.Trim()}\"")}.
           Use background=true if you have other work meanwhile, then poll {McpToolNames.ClaudeCodeName(McpToolNames.Job)} action=status wait_seconds=120.
        3. Review the merged change (git diff on the listed files), fix what the agent got wrong yourself, and report briefly.
           If nothing was merged (verify failed or conflict), inspect with action=diff and either merge, fix, or discard.
        """;

    [McpServerPrompt(Name = Review, Title = "Offload: two-pass code review")]
    [Description("Cheap first-pass review of the current git diff by the local model, then verification of every finding by you.")]
    public static string ReviewPrompt(
        [Description("all | staged | unstaged | <git ref or range>, default all.")] string? target = null,
        [Description("Optional focus, e.g. \"error handling\".")] string? focus = null) =>
        $"""
        Review the current changes in two passes.
        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.ReviewDiff)} with target="{(string.IsNullOrWhiteSpace(target) ? "all" : target.Trim())}"{(string.IsNullOrWhiteSpace(focus) ? "" : $" and focus=\"{focus.Trim()}\"")}.
        2. The findings come from a smaller model: verify EACH one by reading only the cited lines. Drop false positives.
        3. Add anything important it missed in the files you looked at (correctness, security, error handling).
        4. Report confirmed findings, most severe first: [severity] path:line - problem - fix. Do not edit files unless asked.
        """;

    [McpServerPrompt(Name = Tests, Title = "Offload: write tests with the local model")]
    [Description("Generate unit tests for a file with the local model and iterate until they pass.")]
    public static string TestsPrompt(
        [Description("The source file to cover, relative to the project root.")] string path,
        [Description("Optional test command from the allowlist, e.g. \"dotnet test --filter FooTests\".")] string? verify_command = null) =>
        $"""
        Get unit tests for {path.Trim()} written by the local Offload model.
        1. Find the test project and an existing test file to copy the style from; decide the new test file path and the cases to cover
           (happy path, edge cases, errors). Do not write the tests yourself.
        2. Call {McpToolNames.ClaudeCodeName(McpToolNames.AgentTask)} with a brief listing those cases, the new test file path,
           context_paths=["{path.Trim()}", "<existing test file>"] and verify_command={(string.IsNullOrWhiteSpace(verify_command) ? "the matching test command from the Offload allowlist" : $"\"{verify_command.Trim()}\"")}.
           (For a single simple file, {McpToolNames.ClaudeCodeName(McpToolNames.WriteFile)} is faster.)
        3. Spot-check the tests for meaningful assertions (not tautologies) and fix weak ones.
        """;

    [McpServerPrompt(Name = FixBuild, Title = "Offload: fix a failing build or tests")]
    [Description("Run the build/tests on the server, digest failures with the local model, and get them fixed.")]
    public static string FixBuildPrompt(
        [Description("Allowlisted command, e.g. \"dotnet build\" or \"npm test\".")] string command) =>
        $"""
        Make `{command.Trim()}` pass.
        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.Verify)} command="{command.Trim()}" (do not run it yourself and do not read the whole log);
           for many compiler errors use {McpToolNames.ClaudeCodeName(McpToolNames.Diagnostics)} log_path=<the log it reports> to get them structured.
        2. If it fails, decide from the error lines and the analysis whether the fix is mechanical and local
           (compile errors, simple test expectation updates) or needs judgement.
           - Mechanical: delegate to {McpToolNames.ClaudeCodeName(McpToolNames.AgentTask)} with the errors in the brief and verify_command="{command.Trim()}".
           - Otherwise read only the cited lines and fix it yourself.
        3. Re-run {McpToolNames.ClaudeCodeName(McpToolNames.Verify)} until it passes; report what was wrong and what changed.
        """;

    [McpServerPrompt(Name = Explain, Title = "Offload: explain code without reading it")]
    [Description("Ask the local model about files or a folder; only the answer enters your context.")]
    public static string ExplainPrompt(
        [Description("Files, folders or globs, comma-separated (e.g. \"src/Auth, src/**/*Token*.cs\").")] string paths,
        [Description("What you want to know; default: how it works.")] string? question = null)
    {
        var list = string.Join(", ", paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => $"\"{p}\""));
        return $"""
            Call {McpToolNames.ClaudeCodeName(McpToolNames.AskFiles)} with paths=[{list}], answer_format="detailed" and question:
            "{(string.IsNullOrWhiteSpace(question) ? "Explain what this code does: main types and responsibilities, the control/data flow, entry points and notable pitfalls. Cite path:line." : question.Trim())}"
            Then relay the answer concisely. Verify any claim you are going to act on by reading only the cited lines.
            """;
    }
}
