using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Mcp.Infrastructure;
using static Offload.Mcp.Infrastructure.TextUtil;

namespace Offload.Mcp.Tools;

/// <summary>Итог local_debug.</summary>
internal sealed record DebugOutput
{
    [JsonPropertyName("reproduced"), Description("true: the command failed as reported; false: it passed (could not reproduce); null: nothing was run.")]
    public bool? Reproduced { get; init; }

    [JsonPropertyName("command"), Description("Reproduction command (allowlisted), null when none could be resolved.")]
    public string? Command { get; init; }

    [JsonPropertyName("command_source"), Description("given | failing test | project test command | none")]
    public required string CommandSource { get; init; }

    [JsonPropertyName("root_cause"), Description("Local-model hypothesis citing path:line (a draft: verify).")]
    public string? RootCause { get; init; }

    [JsonPropertyName("suspect_commit"), Description("Commit that likely introduced the bug (hash + subject), when the model identified one from the history.")]
    public string? SuspectCommit { get; init; }

    [JsonPropertyName("implicated"), Description("Implicated code: path:line in Symbol - why.")]
    public required IReadOnlyList<string> Implicated { get; init; }

    [JsonPropertyName("fix_status"), Description("not_attempted | failed | the job status (applied | pending_merge | no_changes | running | ...).")]
    public required string FixStatus { get; init; }

    [JsonPropertyName("job_id"), Description("local_job id of the fix (diff/merge/revert).")]
    public string? JobId { get; init; }

    [JsonPropertyName("changed_files"), Description("Files changed by the fix with +/- lines.")]
    public required IReadOnlyList<string> ChangedFiles { get; init; }

    [JsonPropertyName("verification"), Description("Runs: reproduction (before the fix), recheck (after merge), related (impacted tests after merge).")]
    public required IReadOnlyList<PrCheckRun> Verification { get; init; }

    [JsonPropertyName("review"), Description("Local-model review findings of the fix.")]
    public required IReadOnlyList<string> Review { get; init; }

    [JsonPropertyName("open_questions")]
    public required IReadOnlyList<string> OpenQuestions { get; init; }
}

/// <summary>
/// local_debug: отладка одним вызовом — воспроизведение (разрешённая команда: явная, по имени упавшего теста или команда
/// тестов проекта) → диагностика (ошибки компилятора и кадры стека → файлы, функции, срезы кода; гипотеза первопричины
/// от локальной модели) → история (последние коммиты затронутого кода, «вероятно внесено коммитом») → исправление агентом
/// в git-песочнице с приёмкой «команда воспроизведения проходит» (как local_solve kind=bug; слияние — только если проверка
/// прошла, со снимком JobStore) → повторный прогон команды и связанных тестов после слияния + находки ревью.
/// fix=false — только диагностика, файлы проекта не меняются.
/// </summary>
internal static partial class DebugTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? problem, string? command, bool fix, string? merge, string[]? paths,
        int maxMinutes, bool background)
    {
        var text = ToolHelpers.RequireText(problem, "problem", 12000);
        var mergeMode = (merge ?? "apply").Trim().ToLowerInvariant();
        if (mergeMode is not ("apply" or "none")) throw new ToolException("merge must be apply or none.");
        if (fix) AgentTaskTool.CompileAllowed(paths); // Ошибку в paths — сразу, до долгих шагов.
        var deadline = new TaskDeadline(TimeSpan.FromMinutes(Math.Clamp(maxMinutes <= 0 ? 30 : maxMinutes, 2, 90)));
        var notes = new List<string>();
        var runs = new List<PrCheckRun>();
        var sb = new StringBuilder();

        // 1. Команда воспроизведения и прогон.
        var (cmd, source) = await ResolveReproductionAsync(ctx, command, text, notes).ConfigureAwait(false);
        sb.Append("local_debug · reproduction: ").Append(cmd is null ? "none" : $"`{cmd}` ({source})").Append('\n');
        LoggedRun? repro = null;
        bool? reproduced = null;
        if (cmd is not null)
        {
            ctx.Progress.Report($"Reproducing: `{cmd}`…");
            repro = await VerifyTool.RunLoggedAsync(ctx, cmd, StepTimeout(deadline, TimeSpan.FromMinutes(15))).ConfigureAwait(false);
            VerifyTool.LinkRun(ctx, repro);
            runs.Add(RunOf(ctx, "reproduction", repro));
            reproduced = !repro.Result.Passed && !repro.Result.TimedOut;
            var r = repro.Result;
            sb.Append("Reproduced? ").Append(r.TimedOut ? $"UNKNOWN — timed out after {r.Duration.TotalSeconds:0} s"
                : reproduced == true ? $"YES — FAILED (exit {r.ExitCode}, {r.Duration.TotalSeconds:0} s)"
                : $"NO — the command PASSED ({r.Duration.TotalSeconds:0} s): could not reproduce").Append($" · log: {repro.Display}\n");
            if (reproduced != true && fix)
            {
                sb.Append(r.TimedOut
                    ? "Stopped: the reproduction did not finish, so a fix could not be verified. Raise max_minutes or pass a narrower command (e.g. one failing test)."
                    : "Stopped: could not reproduce. Pass the exact failing command (e.g. a test filter) or call with fix=false to diagnose from the text only.");
                ctx.Structured = Output(reproduced, cmd, source, null, null, [], "not_attempted", null, [], runs, [], notes);
                return sb.ToString().TrimEnd();
            }
        }
        else
        {
            sb.Append("Reproduced? NOT RUN — ").Append(notes.LastOrDefault() ?? "no allowlisted command").Append('\n');
        }

        // 2. Диагностика: ошибки и кадры стека из текста проблемы и вывода команды → код проекта.
        ctx.Progress.Report("Locating the implicated code…");
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (reproduced == true && repro is not null) lines.AddRange(DiagnosticsTool.ReadTail(repro.LogPath, 2 * 1024 * 1024));
        var implicated = await ImplicateAsync(ctx, text, lines).ConfigureAwait(false);
        var implicatedLines = implicated.Select(i => i.Describe()).ToList();

        // 3. История затронутого кода.
        var history = await HistoryAsync(ctx, implicated, notes).ConfigureAwait(false);

        // Гипотеза первопричины и «подозрительный» коммит — одним запросом к модели.
        var (rootCause, suspect, fixIdea, questions) = await HypothesisAsync(ctx, text, repro, implicated, history, notes).ConfigureAwait(false);
        sb.Append("Root cause (local model hypothesis, verify): ").Append(rootCause ?? "not determined").Append('\n');
        sb.Append("Suspect commit: ").Append(suspect ?? "none identified").Append('\n');
        if (implicated.Count > 0)
        {
            sb.Append("Implicated code:\n");
            foreach (var i in implicatedLines) sb.Append("  ").Append(i).Append('\n');
        }
        else
        {
            sb.Append("Implicated code: not found in the project (no project stack frames/diagnostics and no matching code)\n");
        }
        if (history.Count > 0)
        {
            sb.Append("Recent history:\n");
            foreach (var h in history.Take(10)) sb.Append("  ").Append(h).Append('\n');
        }

        // 4. Исправление в песочнице.
        var fixStatus = "not_attempted";
        string? jobId = null;
        var changed = new List<string>();
        var review = new List<string>();
        if (!fix)
        {
            sb.Append("Fix: not attempted (fix=false, diagnosis only; nothing was changed)\n");
        }
        else if (cmd is null)
        {
            sb.Append("Fix: not attempted — no reproduction command to verify it; pass command (an allowlisted failing test/build command)\n");
        }
        else
        {
            var brief = Brief(text, cmd, rootCause, fixIdea, suspect, implicated, history);
            string? agentText = null;
            try
            {
                agentText = await AgentTaskTool.RunAsync(ctx, new AgentTaskRequest
                {
                    Task = brief,
                    VerifyCommand = cmd,
                    ContextPaths = [.. implicated.Select(i => i.File.Display).Distinct(StringComparer.OrdinalIgnoreCase).Take(8)],
                    Merge = mergeMode,
                    FixAttempts = 3,
                    TimeoutMinutes = Math.Max(2, (int)Math.Floor(deadline.Remaining.TotalMinutes)),
                    Background = background,
                    AllowedPaths = paths,
                    MaxFiles = 25,
                    Review = true,
                    Tool = McpToolNames.Debug,
                }).ConfigureAwait(false);
            }
            catch (ToolException ex)
            {
                fixStatus = "failed";
                sb.Append("Fix: failed — ").Append(ex.Message).Append('\n');
            }
            if (agentText is not null)
            {
                jobId = JobIdPattern().Match(agentText) is { Success: true } m ? m.Groups[1].Value : null;
                fixStatus = jobId is not null && TryLoadJob(jobId) is { } job ? job.Status : "unknown";
                changed = [.. ChangedFilePattern().Matches(agentText).Select(x => $"{x.Groups[1].Value} {x.Groups[2].Value}")];
                review = [.. agentText.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('[') || l == "no issues found").Take(12)];
                sb.Append("Fix (local agent in a git sandbox):\n").Append(Indent(agentText.Trim())).Append('\n');
            }
        }

        // 5. Проверка после слияния: команда воспроизведения + связанные тесты.
        if (fixStatus is JobStatus.Applied or JobStatus.Committed && cmd is not null)
        {
            ctx.Progress.Report($"Re-running `{cmd}` after the fix…");
            var recheck = await VerifyTool.RunLoggedAsync(ctx, cmd, StepTimeout(deadline, TimeSpan.FromMinutes(15))).ConfigureAwait(false);
            runs.Add(RunOf(ctx, "recheck", recheck));
            if (!recheck.Result.Passed) notes.Add($"`{cmd}` still fails after the merge: inspect, then local_job action=revert job_id={jobId} if the fix is wrong");
            if (deadline.Remaining >= TaskDeadline.MinVerifyTime)
            {
                try
                {
                    await ImpactTool.RunAsync(ctx, "all", null, runTests: true, 40, (int)StepTimeout(deadline, TimeSpan.FromMinutes(10)).TotalSeconds).ConfigureAwait(false);
                    if (ctx.Structured is ImpactOutput impact)
                        foreach (var t in impact.TestRuns)
                            runs.Add(new PrCheckRun { Scope = "related", Command = t.Command, Status = t.Status, ExitCode = t.ExitCode, DurationSec = t.DurationSec, Log = t.Log, Errors = t.Errors });
                }
                catch (Exception ex) when (ex is ToolException or ContextExceededException)
                {
                    notes.Add("related tests not run: " + Short(ex.Message, 200));
                }
            }
            else
            {
                notes.Add("related tests not run (max_minutes used up): run local_impact run_tests=true");
            }
            if (runs.Any(r => r.Scope == "related" && r.Status != "passed")) notes.Add("related tests fail after the fix: check for a regression before accepting it");
        }
        else if (background && jobId is not null && fixStatus == JobStatus.Running)
        {
            notes.Add($"the fix runs in the background: local_job action=status job_id={jobId} wait_seconds=600; after it merges, re-run `{cmd}` (local_verify) and local_impact run_tests=true");
        }
        else if (fixStatus == JobStatus.PendingMerge)
        {
            notes.Add($"the fix was not merged: review local_job action=diff job_id={jobId}, then action=merge or action=discard");
        }

        var verification = runs.Where(r => r.Scope != "reproduction").ToList();
        if (verification.Count > 0)
        {
            sb.Append("Verification after the fix:\n");
            foreach (var r in verification)
            {
                sb.Append($"  {r.Scope} `{r.Command}` → {r.Status.ToUpperInvariant()} (exit {r.ExitCode}, {r.DurationSec:0} s) · log: {r.Log}\n");
                foreach (var e in r.Errors.Take(5)) sb.Append("      ").Append(Short(e, 220)).Append('\n');
            }
        }
        if (review.Count > 0)
        {
            sb.Append("Review findings (local model, verify):\n");
            foreach (var r in review) sb.Append("  ").Append(Short(r, 240)).Append('\n');
        }
        var open = questions.Concat(notes).Distinct().ToList();
        if (open.Count > 0)
        {
            sb.Append("Open questions / notes:\n");
            foreach (var q in open.Take(12)) sb.Append("  - ").Append(Short(q, 400)).Append('\n');
        }
        ctx.Structured = Output(reproduced, cmd, source, rootCause, suspect, implicatedLines, fixStatus, jobId, changed, runs, review, open);
        return sb.ToString().TrimEnd();
    }

    private static DebugOutput Output(bool? reproduced, string? cmd, string source, string? rootCause, string? suspect, List<string> implicated,
        string fixStatus, string? jobId, List<string> changed, List<PrCheckRun> runs, List<string> review, List<string> open) => new()
    {
        Reproduced = reproduced,
        Command = cmd,
        CommandSource = source,
        RootCause = rootCause,
        SuspectCommit = suspect,
        Implicated = implicated,
        FixStatus = fixStatus,
        JobId = jobId,
        ChangedFiles = changed,
        Verification = [.. runs],
        Review = review,
        OpenQuestions = open,
    };

    private static TimeSpan StepTimeout(TaskDeadline deadline, TimeSpan cap)
    {
        var left = deadline.ForStep(TaskDeadline.MinVerifyTime) ?? TaskDeadline.MinVerifyTime;
        return left < cap ? left : cap;
    }

    private static PrCheckRun RunOf(ToolContext ctx, string scope, LoggedRun run) => new()
    {
        Scope = scope,
        Command = run.Command,
        Status = run.Result.Passed ? "passed" : run.Result.TimedOut ? "timed_out" : "failed",
        ExitCode = run.Result.ExitCode,
        DurationSec = Math.Round(run.Result.Duration.TotalSeconds, 1),
        Log = run.Display,
        Errors = run.Result.Passed ? [] : PrReadyTool.ErrorsOf(ctx, run),
    };

    private static JobInfo? TryLoadJob(string id)
    {
        try
        {
            return JobStore.IsValidId(id) ? JobStore.Load(id) : null;
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Indent(string text) => string.Join('\n', text.Split('\n').Select(l => "  " + l));

    [GeneratedRegex(@"job_id:\s*(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex JobIdPattern();

    /// <summary>Строки «  path  +3 −1» / «  path  binary» из доказательства агента.</summary>
    [GeneratedRegex(@"(?m)^  (\S.*?)  (\+\d+ −\d+|binary)\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChangedFilePattern();

    // ───────────────────────── 1. воспроизведение ─────────────────────────

    /// <summary>
    /// Команда воспроизведения: явная (белый список; одноразовое разрешение — как в local_verify), иначе — по упавшему тесту
    /// из текста проблемы (dotnet/pytest/jest), иначе — команда тестов проекта. null — ничего подходящего (причина в notes).
    /// </summary>
    private static async Task<(string? Command, string Source)> ResolveReproductionAsync(ToolContext ctx, string? command, string problem, List<string> notes)
    {
        if (!string.IsNullOrWhiteSpace(command))
            return (await VerifyTool.ResolveCommandAsync(ctx, command, "test").ConfigureAwait(false), "given");
        try
        {
            if (await FailingTestCommandAsync(ctx, problem).ConfigureAwait(false) is { } derived) return (derived, "failing test");
        }
        catch (ToolException ex)
        {
            notes.Add("could not map the failing test to a command: " + Short(ex.Message, 200));
        }
        try
        {
            return (await VerifyTool.ResolveCommandAsync(ctx, null, "test").ConfigureAwait(false), "project test command");
        }
        catch (ToolException ex)
        {
            notes.Add("no reproduction command: " + Short(ex.Message, 300));
            return (null, "none");
        }
    }

    /// <summary>Упавшие тесты в тексте: (класс, файл) — «Failed Ns.Class.Method», «FAILED tests/x.py::test», «FAIL src/x.test.ts».</summary>
    internal static List<(string? Class, string? File)> FailingTests(string problem)
    {
        var result = new List<(string?, string?)>();
        foreach (Match m in DotnetFailed().Matches(problem))
        {
            var parts = m.Groups[1].Value.Split('.');
            if (parts.Length >= 2) result.Add((parts[^2], null));
        }
        foreach (Match m in PytestFailed().Matches(problem)) result.Add((null, m.Groups[1].Value.Replace('\\', '/')));
        foreach (Match m in JestFailed().Matches(problem)) result.Add((null, m.Groups[1].Value.Replace('\\', '/')));
        return [.. result.Distinct().Take(6)];
    }

    [GeneratedRegex(@"(?mi)^\s*(?:failed|\[fail\])\s+([A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+)(?=\s|\(|\[|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetFailed();

    [GeneratedRegex(@"(?m)FAILED\s+([\w./\\-]+\.py)::", RegexOptions.CultureInvariant)]
    private static partial Regex PytestFailed();

    [GeneratedRegex(@"(?m)^\s*FAIL\s+([\w./\\-]+\.(?:test|spec)\.[cm]?[jt]sx?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex JestFailed();

    /// <summary>Команда только упавших тестов — теми же построителями, что local_impact run_tests (белый список).</summary>
    private static async Task<string?> FailingTestCommandAsync(ToolContext ctx, string problem)
    {
        var failing = FailingTests(problem);
        if (failing.Count == 0) return null;
        var (map, index) = await ProjectMap.BuildAsync(ctx, ctx.Ct).ConfigureAwait(false);
        var tests = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cls, file) in failing)
        {
            if (cls is not null)
            {
                foreach (var (f, s) in SymbolsTool.Definitions(index.Files, cls).Where(d => d.Symbol.IsType && d.File.IsTest).Take(1))
                {
                    if (!tests.TryGetValue(f.Display, out var set)) tests[f.Display] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(s.Name);
                }
            }
            else if (file is not null && index.Files.FirstOrDefault(f => f.Display.Equals(file, StringComparison.OrdinalIgnoreCase)
                         || f.Display.EndsWith("/" + file.TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase)) is { } tf)
            {
                if (!tests.ContainsKey(tf.Display)) tests[tf.Display] = [];
            }
        }
        return tests.Count == 0 ? null : ImpactTool.TestCommands(ctx, map, tests).FirstOrDefault();
    }

    // ───────────────────────── 2. диагностика ─────────────────────────

    /// <summary>Место в коде проекта, связанное с ошибкой.</summary>
    internal sealed record Implicated(SourceFile File, int Line, CodeSymbol? Symbol, string Why)
    {
        public string Describe()
        {
            var code = Line >= 1 && Line <= File.Lines.Length ? " — " + Short(File.Lines[Line - 1].Trim(), 140) : "";
            return $"{File.Display}:{Line}{(Symbol is not null ? " in " + Symbol.QualifiedName : "")} ({Short(Why, 120)}){code}";
        }

        /// <summary>Срез с номерами строк: тело функции (до 60 строк) или окно ±15 строк; строка ошибки помечена «>».</summary>
        public string Slice()
        {
            int from, to;
            if (Symbol is not null && Symbol.EndLine - Symbol.Line <= 60) (from, to) = (Symbol.Line, Symbol.EndLine);
            else (from, to) = (Math.Max(1, Line - 15), Math.Min(File.Lines.Length, Line + 15));
            var sb = new StringBuilder($"=== {File.Display}:{from}-{to}{(Symbol is not null ? " (" + Symbol.QualifiedName + ")" : "")} ===\n");
            for (var i = Math.Max(1, from); i <= Math.Min(to, File.Lines.Length); i++)
                sb.Append(i == Line ? ">" : " ").Append($"{i,5}| ").Append(File.Lines[i - 1]).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Ошибки компилятора и кадры стека из текста/вывода → файлы проекта (до 5 мест в 4 файлах). Ничего не нашлось — файлы,
    /// лучше всего совпавшие со словами проблемы (как local_find_context).
    /// </summary>
    private static async Task<List<Implicated>> ImplicateAsync(ToolContext ctx, string problem, List<string> lines)
    {
        var result = new List<Implicated>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(SourceFile? f, int line, string why)
        {
            if (f is null || line < 1 || result.Count >= 5) return;
            if (!seen.Add(f.Display + ":" + line)) return;
            if (result.Select(r => r.File.Display).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 4 && !result.Any(r => r.File.Display == f.Display)) return;
            var inner = f.Symbols.Where(s => !s.IsType).ToList();
            result.Add(new Implicated(f, line, Symbols.Enclosing(inner, line) ?? Symbols.Enclosing(f.Symbols, line), why));
        }
        foreach (var d in DiagnosticParser.Parse(lines).Where(d => d.Severity == "error").Take(10))
            Add(DiagnosticsTool.TryLoad(ctx, d.File), d.Line, $"{d.Code} {d.Message}".Trim());
        foreach (var fr in DiagnosticParser.ParseFrames(lines).Take(60))
            Add(DiagnosticsTool.TryLoad(ctx, fr.File), fr.Line, "stack frame");
        if (result.Count > 0) return result;

        try
        {
            using var index = await CodeIndex.OpenAsync(ctx, null, codeOnly: true, ctx.Ct).ConfigureAwait(false);
            foreach (var s in FindContextTool.Rank(index, FindContextTool.ExtractTerms(problem), [], false, 3))
                Add(s.File, s.HitLines.Count > 0 ? s.HitLines[0] : s.Symbols.Count > 0 ? s.Symbols[0].Line : 1, "matches the problem text");
        }
        catch (ToolException ex)
        {
            Offload.Core.Logging.Log.Debug("mcp", "local_debug: поиск по словам не удался: " + ex.Message);
        }
        return result;
    }

    // ───────────────────────── 3. история ─────────────────────────

    /// <summary>Последние коммиты затронутого кода: тело функции (git log -L) или файл — до двух мест, по 5 коммитов.</summary>
    private static async Task<List<string>> HistoryAsync(ToolContext ctx, List<Implicated> implicated, List<string> notes)
    {
        var lines = new List<string>();
        if (implicated.Count == 0) return lines;
        if (Git.Executable is null || Git.FindWorkTreeRoot(ctx.Roots[0]) is null)
        {
            notes.Add("history skipped: the project is not a git repository");
            return lines;
        }
        foreach (var i in implicated.DistinctBy(i => i.File.Display).Take(2))
        {
            string? text = null;
            if (i.Symbol is { } s)
            {
                try
                {
                    text = await GitHistoryTool.RunAsync(ctx, "symbol", i.File.Display, s.QualifiedName, null, null, null, null, false, 5, null).ConfigureAwait(false);
                }
                catch (ToolException)
                {
                    // Символ не найден индексом истории — ниже история файла.
                }
            }
            try
            {
                text ??= await GitHistoryTool.RunAsync(ctx, "file", i.File.Display, null, null, null, null, null, false, 5, null).ConfigureAwait(false);
            }
            catch (ToolException ex)
            {
                notes.Add($"history of {i.File.Display} unavailable: {Short(ex.Message, 160)}");
                continue;
            }
            var all = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
            if (all.Count == 0) continue;
            lines.Add(all[0].Trim());
            lines.AddRange(all.Skip(1).Take(5).Select(l => "  " + l.Trim()));
        }
        return lines;
    }

    // ───────────────────────── гипотеза ─────────────────────────

    private static async Task<(string? RootCause, string? Suspect, string? FixIdea, List<string> Questions)> HypothesisAsync(ToolContext ctx, string problem,
        LoggedRun? repro, List<Implicated> implicated, List<string> history, List<string> notes)
    {
        try
        {
            var model = await ctx.GetModelAsync().ConfigureAwait(false);
            var system = model.SystemPrompt(
                "You debug a failure for another AI agent. From the problem report, the reproduction output, the implicated code (line numbers; " +
                "the failing line is marked '>') and the recent git history, find the most likely ROOT CAUSE (not the symptom). Output exactly these lines:\n" +
                "ROOT CAUSE: <1-3 sentences, cite path:line>\n" +
                "SUSPECT COMMIT: <short hash from the history that most likely introduced it, or none>\n" +
                "FIX IDEA: <one sentence>\n" +
                "OPEN QUESTIONS: <what is unclear, separated by ';', or none>");
            var budget = model.MaterialBudget(500, system);
            var material = new StringBuilder();
            material.Append("PROBLEM:\n").Append(Short(problem, Math.Max(1500, budget * 3 / 4))).Append("\n\n");
            if (repro is { Result.Passed: false }) material.Append($"REPRODUCTION `{repro.Command}` (exit {repro.Result.ExitCode}):\n").Append(repro.Result.ForModel(4000)).Append("\n\n");
            if (implicated.Count > 0)
            {
                material.Append("IMPLICATED CODE:\n");
                foreach (var i in implicated.DistinctBy(i => (i.File.Display, i.Symbol?.Line ?? i.Line))) material.Append(i.Slice());
                material.Append('\n');
            }
            if (history.Count > 0) material.Append("RECENT GIT HISTORY:\n").Append(string.Join('\n', history)).Append('\n');
            var input = material.ToString();
            if (Tokens.Estimate(input) > budget) input = input[..Math.Min(input.Length, budget * 3)] + "\n…[truncated]";
            ctx.Stats.TokensRead += Tokens.Estimate(input);
            ctx.Stats.FilesRead += implicated.Select(i => i.File.Display).Distinct().Count();
            await using var slot = await GpuQueue.AcquireAsync(ctx, ctx.Ct).ConfigureAwait(false);
            var reply = await model.ChatAsync(system, input, 500, "root cause", ctx.Ct).ConfigureAwait(false);
            var answer = reply.Text.Replace("\r\n", "\n");
            var rootCause = Field(answer, "ROOT CAUSE");
            var fixIdea = Field(answer, "FIX IDEA");
            var questions = Field(answer, "OPEN QUESTIONS") is { } q && !q.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? q.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).Take(5).ToList()
                : [];
            string? suspect = null;
            if (Field(answer, "SUSPECT COMMIT") is { } sc && ShaPattern().Match(sc) is { Success: true } sha)
            {
                // Только коммит, который действительно есть в показанной истории (модель могла выдумать хэш).
                var line = history.FirstOrDefault(h => h.TrimStart().StartsWith(sha.Value[..Math.Min(7, sha.Value.Length)], StringComparison.OrdinalIgnoreCase));
                if (line is not null) suspect = "likely introduced by " + line.Trim();
            }
            if (rootCause is null && answer.Trim().Length > 0) rootCause = Short(answer.Trim().Replace('\n', ' '), 600);
            return (rootCause, suspect, fixIdea, questions);
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException)
        {
            notes.Add("root-cause hypothesis unavailable (local model): " + Short(ex.Message, 200));
            return (null, null, null, []);
        }
    }

    private static string? Field(string answer, string name)
    {
        foreach (var raw in answer.Split('\n'))
        {
            var l = raw.Trim().TrimStart('-', '*', ' ').Replace("**", "");
            if (!l.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            var colon = l.IndexOf(':');
            if (colon < 0) continue;
            var v = l[(colon + 1)..].Trim();
            return v.Length == 0 ? null : v;
        }
        return null;
    }

    [GeneratedRegex(@"\b[0-9a-f]{7,40}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ShaPattern();

    // ───────────────────────── 4. бриф агенту ─────────────────────────

    private static string Brief(string problem, string cmd, string? rootCause, string? fixIdea, string? suspect, List<Implicated> implicated, List<string> history)
    {
        var b = new StringBuilder();
        b.Append("Fix this bug. The reproduction command currently FAILS: `").Append(cmd).Append("`.\n");
        b.Append("Acceptance: that command must pass; existing tests related to the changed code must keep passing. Fix the root cause, not the symptom, ");
        b.Append("with a minimal change. If feasible and the command is not already a test that covers this bug, add a regression test.\n\n");
        b.Append("PROBLEM REPORT:\n").Append(Short(problem.Trim(), 5000)).Append("\n\n");
        if (rootCause is not null) b.Append("DIAGNOSIS (hypothesis from a smaller model; verify it):\n").Append(rootCause).Append('\n');
        if (fixIdea is not null) b.Append("Fix idea: ").Append(fixIdea).Append('\n');
        if (suspect is not null) b.Append("Suspect: ").Append(suspect).Append('\n');
        if (implicated.Count > 0)
        {
            b.Append("\nIMPLICATED CODE:\n");
            foreach (var i in implicated) b.Append("- ").Append(i.Describe()).Append('\n');
        }
        if (history.Count > 0) b.Append("\nRECENT HISTORY OF THAT CODE:\n").Append(Short(string.Join('\n', history), 2000)).Append('\n');
        b.Append("\nWhen done, list: Root cause, Fix, Regression test, and any open questions.");
        return Short(b.ToString(), 15000);
    }
}
