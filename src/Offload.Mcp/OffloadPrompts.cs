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
    internal const string Team = "team";
    internal const string DefineRole = "define_role";
    internal const string Docs = "docs";
    internal const string Migrate = "migrate";
    internal const string Perf = "perf";
    internal const string ReleaseNotes = "release_notes";
    internal const string Debug = "debug";
    internal const string Pr = "pr";

    internal static readonly IReadOnlyList<string> All =
        [Delegate, Review, Tests, FixBuild, Explain, Solve, Context, Security, Team, DefineRole, Docs, Migrate, Perf, ReleaseNotes, Debug, Pr];

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

    [McpServerPrompt(Name = Team, Title = "Offload: a team of roles on the same files")]
    [Description("Run several local roles (reviewer, security-auditor, tester...) over the same files and get one merged report; you decide what to act on.")]
    public static string TeamPrompt(
        [Description("Files, folders or globs, comma-separated.")] string paths,
        [Description("The common task or question for the team.")] string task,
        [Description("Roles, comma-separated; default: reviewer, security-auditor, tester.")] string? roles = null)
    {
        var list = string.Join(", ", paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => $"\"{p}\""));
        var team = string.Join(", ", (string.IsNullOrWhiteSpace(roles) ? "reviewer, security-auditor, tester" : roles)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => $"\"{r}\""));
        return $"""
            Get several expert viewpoints from the local Offload team and stay the decision maker:

            {task.Trim()}

            1. Call {McpToolNames.ClaudeCodeName(McpToolNames.Roles)} action=list if you are unsure which roles exist; define a missing one with action=define
               (name, prompt, extends=<similar role>, presets=[language rule sets]).
            2. Call {McpToolNames.ClaudeCodeName(McpToolNames.Team)} paths=[{list}] roles=[{team}] mode="parallel" synthesize=true
               (mode="pipeline" if later roles should build on earlier reports; add "=focus" to a role name to narrow it).
            3. The reports come from a smaller model: verify every finding you will act on by reading only the cited lines. Where roles disagree, decide yourself.
            4. Report agreed points first, then conflicts and your decision, then the next steps.
            """;
    }

    [McpServerPrompt(Name = DefineRole, Title = "Offload: define a custom role")]
    [Description("Create a project role for the local model, optionally inheriting another role and language rules.")]
    public static string DefineRolePrompt(
        [Description("What the role should do, in a sentence.")] string purpose,
        [Description("Role name (lowercase, digits, '-').")] string? name = null,
        [Description("Parent role to inherit from, e.g. reviewer or engineer.")] string? extends = null) =>
        $"""
        Create a role for the local Offload model: {purpose.Trim()}

        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.Roles)} action=list to see existing roles and presets; prefer inheriting (extends) over starting from scratch.
        2. Write a tight prompt (under 1200 characters): the mission, the exact output format, what the role must NOT do, and when to say it found nothing.
        3. Call {McpToolNames.ClaudeCodeName(McpToolNames.Roles)} action=define name="{(string.IsNullOrWhiteSpace(name) ? "<short-kebab-name>" : name.Trim())}"{(string.IsNullOrWhiteSpace(extends) ? "" : $" extends=\"{extends.Trim()}\"")} prompt="..." (add presets=[...] for language rules).
        4. Try it once with {McpToolNames.ClaudeCodeName(McpToolNames.AskFiles)} role="<name>" on a small file and tune the prompt if the output format is off.
        """;

    [McpServerPrompt(Name = Docs, Title = "Offload: document code with the local model")]
    [Description("Have the local documenter write doc comments or a README section from the real code.")]
    public static string DocsPrompt(
        [Description("File, folder or glob to document.")] string path,
        [Description("What to write: doc comments, README section, usage examples. Default: doc comments for public API.")] string? what = null) =>
        $"""
        Document {path.Trim()} with the local model, then check it.
        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.AskFiles)} paths=["{path.Trim()}"] role="documenter" answer_format="detailed" and question:
           "{(string.IsNullOrWhiteSpace(what) ? "Write doc comments for the public API in the comment style used in the code." : what.Trim())} Mark anything unverifiable as TODO."
        2. Compare the text with the code for the signatures, parameters and errors it claims; fix inaccuracies and remove marketing language.
        3. Apply it with {McpToolNames.ClaudeCodeName(McpToolNames.EditFiles)} (listed files only) or your own edit.
        """;

    [McpServerPrompt(Name = Migrate, Title = "Offload: mechanical migration")]
    [Description("Migrate many occurrences to a new API, version or framework with the local agent and verify with the build.")]
    public static string MigratePrompt(
        [Description("What changes: from -> to, with an example of one converted occurrence.")] string change,
        [Description("Allowlisted check, e.g. \"dotnet build\".")] string? verify_command = null) =>
        $"""
        Migrate with the local agent, keeping judgement yourself:

        {change.Trim()}

        1. Find the occurrences cheaply: {McpToolNames.ClaudeCodeName(McpToolNames.SearchCode)} and {McpToolNames.ClaudeCodeName(McpToolNames.Impact)} (do not read the files). Convert ONE occurrence yourself as the reference pattern.
        2. Call {McpToolNames.ClaudeCodeName(McpToolNames.AgentTask)} role="migrator" with the pattern in the brief, allowed_paths limited to the affected folders,
           verify_command={(string.IsNullOrWhiteSpace(verify_command) ? "the project's build command from the allowlist" : $"\"{verify_command.Trim()}\"")} and background=true for large sets.
        3. Review the leftovers the agent lists as not fitting the pattern and handle them yourself; run the tests with {McpToolNames.ClaudeCodeName(McpToolNames.Impact)} run_tests=true.
        """;

    [McpServerPrompt(Name = Perf, Title = "Offload: performance review")]
    [Description("Have the local performance analyst find measurable hot spots; you verify before changing anything.")]
    public static string PerfPrompt(
        [Description("Files, folders or globs, comma-separated.")] string paths) =>
        $"""
        Look for performance problems in {paths.Trim()} without reading the files yourself.
        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.AskFiles)} with the paths, role="perf-analyst", answer_format="bullets".
        2. Verify each finding in the cited lines; keep only those with a real cost (complexity, repeated work, N+1, blocking calls). Ask the user to measure when the impact depends on data size.
        3. Fix the confirmed ones minimally (or delegate with {McpToolNames.ClaudeCodeName(McpToolNames.AgentTask)} and a verify command) and re-run the tests.
        """;

    [McpServerPrompt(Name = ReleaseNotes, Title = "Offload: release notes from git history")]
    [Description("Draft user-facing release notes for a git range with the local model.")]
    public static string ReleaseNotesPrompt(
        [Description("Git range, default: last tag..HEAD.")] string? range = null,
        [Description("Language of the notes, e.g. en or ru.")] string? language = null) =>
        $"""
        Prepare release notes.
        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.GitHistory)} action="changelog" use_model=true{(string.IsNullOrWhiteSpace(range) ? "" : $" range=\"{range.Trim()}\"")}{(string.IsNullOrWhiteSpace(language) ? "" : $" language=\"{language.Trim()}\"")}.
        2. Check the draft against the real changes: drop internal refactors, merge duplicates, put breaking changes first. Do not invent features.
        3. Show the final text; do not commit, tag or publish anything.
        """;

    [McpServerPrompt(Name = Debug, Title = "Offload: debug a failure")]
    [Description("Reproduce a failing command, find the root cause and (optionally) fix it in a sandbox.")]
    public static string DebugPrompt(
        [Description("The symptom: error text, failing test name, stack trace.")] string problem,
        [Description("Allowlisted command that reproduces it, e.g. \"dotnet test --filter FooTests\".")] string? command = null) =>
        $"""
        Debug with the local pipeline, keeping the diagnosis under your control:

        {problem.Trim()}

        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.Debug)} problem="<the symptom>"{(string.IsNullOrWhiteSpace(command) ? "" : $" command=\"{command.Trim()}\"")} fix=false first to get the reproduction, root cause and suspect commit.
        2. Check the root cause against the cited lines; if it is wrong or only a symptom, say so and refine the problem text.
        3. When the cause is right, call it again with fix=true (or fix it yourself if the change is tiny and risky) and review the proof.
        """;

    [McpServerPrompt(Name = Pr, Title = "Offload: is my branch ready for a PR?")]
    [Description("One-call readiness check of the branch: conflicts, tests, secrets, review, PR draft.")]
    public static string PrPrompt(
        [Description("Base branch, default: origin/HEAD, main or master.")] string? @base = null) =>
        $"""
        Check whether this branch can go to review.
        1. Call {McpToolNames.ClaudeCodeName(McpToolNames.PrReady)}{(string.IsNullOrWhiteSpace(@base) ? "" : $" base=\"{@base.Trim()}\"")} and read the verdict and blockers first.
        2. Fix the blockers (or delegate the mechanical ones), then call it again until the verdict is ready.
        3. Show the PR title and body it drafted, corrected by you. Do not push or open the PR yourself.
        """;
}
