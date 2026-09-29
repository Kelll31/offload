using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Util;
using Offload.Llama;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Resources;
using static Offload.Mcp.Infrastructure.TextUtil;

namespace Offload.Mcp.Tools;

/// <summary>Стратегия кандидата гонки: имя для таблицы, подсказка в бриф агенту, сдвиг температуры от настроек модели.</summary>
internal sealed record RaceStrategy(string Name, string Hint, double TemperatureDelta);

/// <summary>Параметры одного запуска агента-кандидата: буква, дополнение брифа, температура (null — из настроек), свой журнал.</summary>
internal sealed record RaceRunOptions(char Letter, string Brief, double? Temperature, string LogPath);

/// <summary>Вердикт судьи-модели: буква победителя и короткая причина.</summary>
internal sealed record RaceVerdict(char Winner, string Reason);

internal static class RaceOutcome
{
    public const string NotStarted = "not started";
    public const string Running = "running";
    public const string Done = "done";
    public const string NoChanges = "no changes";
    public const string Failed = "failed";
    public const string TimedOut = "timed out";
    public const string Cancelled = "cancelled";
}

/// <summary>Кандидат гонки агентов: его песочница, проверка, изменения, ревью и оценка.</summary>
internal sealed class RaceCandidate(char letter, RaceStrategy strategy)
{
    public char Letter { get; } = letter;
    public RaceStrategy Strategy { get; } = strategy;
    public string Outcome { get; set; } = RaceOutcome.NotStarted;
    public SandboxInfo? Sandbox { get; set; }
    public SandboxCommit? Commit { get; set; }
    public VerifyResult? Verify { get; set; }
    public int Attempts { get; set; }
    public List<SandboxChange> Changes { get; set; } = [];
    public List<string> Summary { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>Находки ревью локальной моделью; null — ревью не проводилось.</summary>
    public List<string>? Review { get; set; }

    public TimeSpan AgentTime { get; set; }
    public string? Error { get; set; }
    public int Score { get; set; }

    /// <summary>Почему кандидат выбывает (null — допущен к выбору).</summary>
    public string? Disqualified { get; set; }

    /// <summary>MCP-ресурс с diff кандидата (сохраняется до удаления песочниц).</summary>
    public string? DiffUri { get; set; }

    public int Files => Changes.Count;
    public int Added => Changes.Sum(c => c.Added);
    public int Removed => Changes.Sum(c => c.Removed);
    public int Lines => Added + Removed;
}

/// <summary>
/// Гонка агентов (race = 2…4 у local_agent_task / local_solve): несколько агентов OpenCode решают одну задачу независимо —
/// каждый в своей git-песочнице от одного и того же снимка (ветки offload/&lt;job&gt;-a, -b…) и со своей стратегией
/// (минимальная правка; первопричина + регрессионный тест; чистое решение; минимум файлов) и температурой. Каждый кандидат
/// проходит проверку с раундами исправлений; допущенные (проверка пройдена, бюджет соблюдён, ревью без critical)
/// ранжируются по оценке, двух лучших при близкой оценке сравнивает судья — локальная модель (JSON-вердикт, при сбое —
/// эвристика). Победитель вливается обычным путём (AgentTaskTool.FinalizeAsync: apply|commit|none, снимки JobStore,
/// JobConfirmation), песочницы остальных удаляются, их diff остаются ресурсами задачи.
/// </summary>
/// <remarks>
/// Параллельность: «в работе» одновременно не больше кандидатов, чем слотов агента на GPU (Parallel − 1; при одном слоте —
/// по очереди). Сами запуски OpenCode идут по одному (OpenCode не выдерживает одновременных запусков на одной папке данных —
/// RunQueueLock), поэтому параллельно идут проверка одного кандидата и работа агента другого. Каждому кандидату — своя доля
/// времени агента; timeout_minutes ограничивает всю гонку по настенным часам; отмена останавливает всех.
/// </remarks>
internal static class AgentRace
{
    public const int MaxCandidates = 4;

    /// <summary>Разрыв оценок, при котором судья не нужен: эвристика уже однозначна.</summary>
    internal const int JudgeMargin = 20;

    /// <summary>Потолок сохраняемого diff кандидата (ресурс задачи).</summary>
    internal const int MaxDiffFileChars = 1_000_000;

    /// <summary>Сколько запусков OpenCode гонка делает одновременно (см. RunQueueLock в Offload.OpenCode).</summary>
    private const int AgentRunsAtOnce = 1;

    internal static readonly RaceStrategy[] Strategies =
    [
        new("minimal", "make the smallest targeted change that fully satisfies the task; do not refactor or touch unrelated code.", 0),
        new("root-cause", "first find the root cause by reading the code paths involved, fix the cause (not the symptom), and add or extend " +
                          "a regression test that would fail without your change.", 0),
        new("clean", "write a clean, idiomatic solution that follows the project's architecture; a small local refactoring is fine " +
                     "when it makes the result clearer.", 0.2),
        new("conservative", "touch as few files as possible, keep public APIs and existing behaviour unchanged, prefer additive changes.", -0.2),
    ];

    /// <summary>Проверка параметра race (1…4).</summary>
    internal static void ValidateCount(int race)
    {
        if (race is < 1 or > MaxCandidates)
            throw new ToolException($"race must be 1-{MaxCandidates}: 1 = one agent (default); 2-{MaxCandidates} = that many agents solve the task " +
                                    "independently with different strategies and the best verified diff is merged.");
    }

    /// <summary>Сколько кандидатов «в работе» одновременно: слоты агента на GPU (Parallel − 1), при одном слоте — по одному.</summary>
    internal static int Concurrency(int parallel, int candidates) => parallel <= 2 ? 1 : Math.Clamp(parallel - 1, 1, Math.Max(1, candidates));

    /// <summary>Ветки песочниц задачи для ответа: «branch offload/&lt;job&gt;» или «branches offload/&lt;job&gt;-a…d».</summary>
    internal static string BranchLabel(string jobId, int race) =>
        race > 1 ? $"branches {GitSandbox.BranchPrefix}{jobId}-a…{(char)('a' + Math.Clamp(race, 2, MaxCandidates) - 1)}" : $"branch {GitSandbox.BranchPrefix}{jobId}";

    /// <summary>Файл diff кандидата в папке задачи.</summary>
    internal static string DiffFile(string jobId, char letter) => Path.Combine(JobStore.DirOf(jobId), $"race-{letter}.diff");

    public static async Task<string> ExecuteAsync(ToolContext ctx, JobInfo job, AgentTaskRequest r, List<string> hints, List<Regex>? allowed)
    {
        var n = Math.Clamp(r.Race, 2, MaxCandidates);
        var candidates = Enumerable.Range(0, n).Select(i => new RaceCandidate((char)('a' + i), Strategies[i])).ToList();
        var parallel = ctx.Cfg.Server.Parallel;
        var concurrency = Concurrency(parallel, n);
        var raceNotes = new List<string>();
        if (concurrency == 1)
            raceNotes.Add(parallel <= 1
                ? "candidates ran one after another: the model server has 1 slot (raise Parallel in the Offload tray app to overlap them)"
                : $"candidates ran one after another: with {parallel} server slots only 1 is for agents (one stays free for interactive calls)");

        // Срок всей гонки; запас после него — на ревью, судью и слияние.
        var total = TimeSpan.FromMinutes(r.TimeoutMinutes);
        var reserve = TimeSpan.FromSeconds(Math.Clamp(total.TotalSeconds * 0.1, 30, 180));
        var raceDl = new TaskDeadline(total - reserve);
        // Доля времени агента на кандидата (запуски OpenCode идут по одному), 20% — на проверки.
        var share = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, (long)(raceDl.Total.Ticks * 0.8 / n)));
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        raceCts.CancelAfter(raceDl.Total);
        var ct = raceCts.Token;

        ctx.Progress.Report($"Agent race: {n} candidates, {concurrency} at a time; creating the git sandbox (snapshot of the working tree)…");
        job.RaceSandboxes = [];
        SandboxInfo? keep = null;
        try
        {
            try
            {
                candidates[0].Sandbox = await GitSandbox.CreateAsync(job.Root, job.Id, JobStore.DirOf(job.Id), ctx.Cfg.Mcp.SecretFilePatterns, ct, 'a')
                    .ConfigureAwait(false);
                Track(job, candidates[0].Sandbox!);
            }
            catch (OperationCanceledException) when (ctx.Ct.IsCancellationRequested)
            {
                AgentTaskTool.Finish(job, null, JobStatus.Cancelled, null, 0, [], []);
                throw;
            }
            catch (Exception ex) when (ex is ToolException or OperationCanceledException)
            {
                AgentTaskTool.Finish(job, null, JobStatus.Failed, null, 0, [], [ex.Message]);
                throw new ToolException((ex is ToolException ? ex.Message : "Creating the git sandbox timed out.") + " Nothing was changed in your project.");
            }

            using var inflight = new SemaphoreSlim(concurrency);
            using var agentGate = new SemaphoreSlim(AgentRunsAtOnce);
            using var gitLock = new SemaphoreSlim(1);
            var baseSb = candidates[0].Sandbox!;
            var tasks = candidates.Select(c => RunGuardedAsync(ctx, job, r, hints, allowed, c, baseSb, share, raceDl, inflight, agentGate, gitLock, ct)).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (ctx.Ct.IsCancellationRequested)
            {
                await DiscardCandidatesAsync(job, null).ConfigureAwait(false);
                AgentTaskTool.Finish(job, null, JobStatus.Cancelled, null, 0, [], ["agent race cancelled"]);
                ctx.Ct.ThrowIfCancellationRequested();
            }

            // Diff каждого кандидата — ресурс задачи (песочницы проигравших будут удалены).
            foreach (var c in candidates.Where(c => c.Changes.Count > 0 && c.Sandbox is not null))
                c.DiffUri = await SaveDiffAsync(ctx, job, c).ConfigureAwait(false);

            // Ревью допущенных по проверке кандидатов (по одному — модель одна).
            if (r.Review)
            {
                foreach (var c in candidates.Where(c => Evaluate(c, r.MaxFiles) is null))
                {
                    ctx.Progress.Report($"Reviewing candidate {c.Letter}…");
                    c.Review = await AgentTaskTool.ReviewSandboxAsync(ctx, c.Sandbox!, r.Task).ConfigureAwait(false);
                }
            }

            foreach (var c in candidates) Score(c, r.MaxFiles);
            var ranked = Rank(candidates);
            RaceVerdict? verdict = null;
            var judged = false;
            if (NeedsJudge(ranked))
            {
                judged = true;
                ctx.Progress.Report($"Judging candidates {ranked[0].Letter} and {ranked[1].Letter}…");
                verdict = await JudgeAsync(ctx, r.Task, ranked[0], ranked[1]).ConfigureAwait(false);
            }
            var (winner, why) = Choose(ranked, verdict, judged);

            // Проигравшие: песочницы и ветки удаляются (их diff уже сохранены).
            await DiscardCandidatesAsync(job, winner?.Sandbox).ConfigureAwait(false);
            var report = RenderReport(candidates, winner, why, concurrency, raceNotes);

            if (winner is null)
            {
                var status = candidates.All(c => c.Outcome == RaceOutcome.NoChanges) ? JobStatus.NoChanges : JobStatus.Failed;
                AgentTaskTool.Finish(job, null, status, null, 0, [], [$"agent race: {why}"]);
                var head = string.IsNullOrWhiteSpace(r.Preamble) ? "" : r.Preamble.TrimEnd() + "\n\n";
                return head + report + $"\njob_id: {job.Id} · status: {job.Status} · NOT merged: {why}. Nothing was changed in your project." +
                       (candidates.Any(c => c.DiffUri is not null) ? " Candidate diffs stay readable as resources (listed above)." : "");
            }

            // Песочница победителя становится песочницей задачи: дальше — обычные merge/diff/discard через local_job.
            keep = winner.Sandbox!;
            lock (job)
            {
                job.RaceSandboxes?.RemoveAll(s => s.Branch == keep.Branch);
                job.Sandbox = keep;
                JobStore.Save(job);
            }
            var rw = r with { Preamble = (string.IsNullOrWhiteSpace(r.Preamble) ? "" : r.Preamble.TrimEnd() + "\n\n") + report };
            var notes = new List<string>(winner.Notes);
            try
            {
                return await AgentTaskTool.FinalizeAsync(ctx, job, rw, keep, winner.Commit!, winner.Verify, winner.Attempts, winner.Summary, notes,
                    winner.Review).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && job.Status is not (JobStatus.Failed or JobStatus.Applied or JobStatus.Committed))
            {
                // Ошибка после выбора: работа победителя сохраняется для ручного решения (как у обычной задачи).
                notes.Add($"finishing failed: {ex.Message}");
                AgentTaskTool.Finish(job, keep, JobStatus.PendingMerge, winner.Verify, winner.Attempts, winner.Summary, notes);
                throw new ToolException($"{ex.Message} The winning candidate's work is kept on branch {keep.Branch}: local_job action=diff job_id={job.Id}, " +
                                        "then action=merge or action=discard.");
            }
        }
        catch (Exception ex) when (ex is not (ToolException or OperationCanceledException) && job.Status == JobStatus.Running)
        {
            // Непредвиденная ошибка: задача не должна остаться «running» с открытыми песочницами.
            AgentTaskTool.Finish(job, null, JobStatus.Failed, null, 0, [], [$"internal error: {ex.GetType().Name}: {ex.Message}"]);
            throw;
        }
        finally
        {
            // Страховка: при любом исходе на диске остаётся не больше одной песочницы — победителя, ждущего слияния.
            await DiscardCandidatesAsync(job, keep).ConfigureAwait(false);
        }
    }

    /// <summary>Кандидат целиком: ожидание своей очереди, песочница, агент, проверка, фиксация. Исключения не выпускает.</summary>
    private static async Task RunGuardedAsync(ToolContext ctx, JobInfo job, AgentTaskRequest r, List<string> hints, List<Regex>? allowed, RaceCandidate c,
        SandboxInfo baseSb, TimeSpan share, TaskDeadline raceDl, SemaphoreSlim inflight, SemaphoreSlim agentGate, SemaphoreSlim gitLock, CancellationToken ct)
    {
        try
        {
            await inflight.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                c.Outcome = RaceOutcome.Running;
                if (c.Sandbox is null)
                {
                    // git worktree add/remove в одном репозитории — по одному (блокировки .git/worktrees и ссылок).
                    await gitLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        c.Sandbox = await GitSandbox.CreateFromAsync(baseSb, job.Id, c.Letter, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        gitLock.Release();
                    }
                    Track(job, c.Sandbox);
                }
                await RunCandidateAsync(ctx, job, r, hints, allowed, c, share, raceDl, agentGate, ct).ConfigureAwait(false);
            }
            finally
            {
                // Worktree больше не нужен: коммит кандидата остаётся в его ветке до выбора. Удаляется до освобождения места
                // «в работе» — на диске одновременно не больше копий проекта, чем кандидатов в работе.
                if (c.Sandbox is { } sb)
                {
                    await gitLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        await GitSandbox.RemoveAsync(sb, deleteBranch: false, CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        gitLock.Release();
                    }
                }
                inflight.Release();
            }
        }
        catch (OperationCanceledException) when (ctx.Ct.IsCancellationRequested)
        {
            c.Outcome = RaceOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            c.Outcome = c.Outcome == RaceOutcome.NotStarted ? RaceOutcome.NotStarted : RaceOutcome.TimedOut;
            c.Error = "the race hit timeout_minutes";
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException)
        {
            c.Outcome = RaceOutcome.Failed;
            c.Error = ex.Message;
        }
        catch (Exception ex)
        {
            c.Outcome = RaceOutcome.Failed;
            c.Error = $"internal error: {ex.GetType().Name}: {ex.Message}";
            Log.Warn("mcp", $"Гонка агентов {job.Id}, кандидат {c.Letter}: {ex}");
        }
    }

    private static async Task RunCandidateAsync(ToolContext ctx, JobInfo job, AgentTaskRequest r, List<string> hints, List<Regex>? allowed, RaceCandidate c,
        TimeSpan share, TaskDeadline raceDl, SemaphoreSlim agentGate, CancellationToken ct)
    {
        var sb = c.Sandbox!;
        var opts = new RaceRunOptions(c.Letter, Brief(c, r.Race), Temperature(ctx.Cfg, c.Strategy), Path.Combine(JobStore.DirOf(job.Id), $"opencode-{c.Letter}.log"));

        // Раунд агента: запуски OpenCode — по одному; время ожидания очереди в долю кандидата не входит.
        async Task<bool> AgentRoundAsync(string? feedback)
        {
            await agentGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var left = share - c.AgentTime;
                if (raceDl.Remaining < left) left = raceDl.Remaining;
                if (left <= TimeSpan.Zero)
                {
                    c.Notes.Add("agent time share used up");
                    return false;
                }
                var sw = Stopwatch.StartNew();
                ctx.Progress.Report($"agent {c.Letter} ({c.Strategy.Name}): {(feedback is null ? "working" : "fixing the check")}…");
                await AgentTaskTool.RunAgentAsync(ctx, job, sb, r, hints, feedback, new TaskDeadline(left), c.Summary, c.Notes, ct, opts).ConfigureAwait(false);
                c.AgentTime += sw.Elapsed;
                return true;
            }
            finally
            {
                agentGate.Release();
            }
        }

        await AgentRoundAsync(null).ConfigureAwait(false);
        while (await GitSandbox.IsDirtyAsync(sb, ct).ConfigureAwait(false))
        {
            c.Attempts++;
            // Проверке — остаток срока гонки (не меньше минуты); её ограничивает свой таймаут, как у обычной задачи.
            if (raceDl.ForStep(TaskDeadline.MinVerifyTime) is not { } verifyTimeout) throw new OperationCanceledException(ct);
            c.Verify = await VerifyCommand.RunAsync(r.VerifyCommand!, sb.AgentDir, verifyTimeout, ctx.Progress, ctx.Ct).ConfigureAwait(false);
            if (c.Verify.Passed || c.Attempts > r.FixAttempts) break;
            var feedback = $"The check `{r.VerifyCommand}` FAILED (exit {c.Verify.ExitCode}) after your changes. Relevant output:\n" +
                           $"{c.Verify.ForModel(5000)}\nFix the problem while still fulfilling the task.";
            if (!await AgentRoundAsync(feedback).ConfigureAwait(false)) break;
        }

        // Фиксация — короткая локальная операция (не прерывается сроком гонки).
        var first = r.Task.Split('\n', 2)[0].Trim();
        c.Commit = await GitSandbox.CommitAsync(sb, $"offload ({c.Letter}): {first}", p => AgentTaskTool.WhyNotWritable(ctx, sb, p, allowed),
            ctx.Cfg.Mcp.SecretFilePatterns, ctx.Ct).ConfigureAwait(false);
        c.Notes.AddRange(c.Commit.Dropped.Select(d => "not transferred: " + d));
        c.Changes = c.Commit.HasChanges ? await GitSandbox.ChangesAsync(sb, ctx.Ct).ConfigureAwait(false) : [];
        c.Outcome = c.Changes.Count > 0 ? RaceOutcome.Done : RaceOutcome.NoChanges;
    }

    /// <summary>Дополнение брифа кандидата: гонка и его стратегия.</summary>
    internal static string Brief(RaceCandidate c, int candidates) =>
        $"APPROACH: {candidates} agents solve this same task independently and only the best verified solution is merged. " +
        $"You are candidate {c.Letter} ({c.Strategy.Name}): {c.Strategy.Hint}";

    /// <summary>Температура кандидата: настройка модели + сдвиг стратегии (0…1.5); null — без переопределения.</summary>
    internal static double? Temperature(AppConfig cfg, RaceStrategy s)
    {
        if (s.TemperatureDelta == 0) return null;
        var baseT = cfg.ActiveModel()?.Sampling?.Temperature ?? new SamplingSettings().Temperature;
        return Math.Round(Math.Clamp(baseT + s.TemperatureDelta, 0, 1.5), 2);
    }

    // ───────────────────────── выбор победителя (чистая логика) ─────────────────────────

    /// <summary>Почему кандидат не допускается к выбору; null — допущен (проверка пройдена, бюджет соблюдён).</summary>
    internal static string? Evaluate(RaceCandidate c, int maxFiles)
    {
        if (c.Outcome != RaceOutcome.Done)
            return c.Outcome switch
            {
                RaceOutcome.Failed => "failed" + (c.Error is { Length: > 0 } e ? ": " + Short(e, 120) : ""),
                RaceOutcome.NoChanges => "no transferable changes",
                _ => c.Outcome,
            };
        if (c.Changes.Count == 0) return "no transferable changes";
        if (c.Verify is null) return "not verified";
        if (!c.Verify.Passed) return c.Verify.TimedOut ? "verify timed out" : $"verify failed (exit {c.Verify.ExitCode})";
        if (maxFiles > 0 && c.Files > maxFiles) return $"{c.Files} files > max_files={maxFiles}";
        // Проверка шла с правками вне allowed_paths, а переносится diff без них — такой результат не проверен.
        if (c.Commit?.Dropped.Any(d => d.EndsWith("(outside allowed_paths)", StringComparison.Ordinal)) == true)
            return "changed files outside allowed_paths";
        if (c.Review is { } rv && rv.Any(f => Severity(f) == "critical")) return "review found a critical issue";
        return null;
    }

    /// <summary>Оценка и допуск: 100 − ревью (high −25, medium −5, low −1) − раунды исправлений (−3 за каждый).</summary>
    internal static void Score(RaceCandidate c, int maxFiles)
    {
        c.Disqualified = Evaluate(c, maxFiles);
        if (c.Disqualified is not null)
        {
            c.Score = 0;
            return;
        }
        var penalty = 0;
        foreach (var f in c.Review ?? [])
        {
            penalty += Severity(f) switch
            {
                "high" => 25,
                "medium" => 5,
                "low" => 1,
                _ => 0,
            };
        }
        penalty += 3 * Math.Max(0, c.Attempts - 1);
        c.Score = Math.Max(1, 100 - penalty);
    }

    /// <summary>Допущенные кандидаты от лучшего: оценка, затем меньше строк, меньше файлов, меньше раундов, буква.</summary>
    internal static List<RaceCandidate> Rank(IEnumerable<RaceCandidate> candidates) =>
        [.. candidates.Where(c => c.Disqualified is null)
            .OrderByDescending(c => c.Score).ThenBy(c => c.Lines).ThenBy(c => c.Files).ThenBy(c => c.Attempts).ThenBy(c => c.Letter)];

    /// <summary>Судья нужен, если допущены двое и больше, а разрыв оценок первых двух меньше <see cref="JudgeMargin"/>.</summary>
    internal static bool NeedsJudge(IReadOnlyList<RaceCandidate> ranked) => ranked.Count >= 2 && ranked[0].Score - ranked[1].Score < JudgeMargin;

    /// <summary>Победитель и объяснение выбора.</summary>
    internal static (RaceCandidate? Winner, string Why) Choose(IReadOnlyList<RaceCandidate> ranked, RaceVerdict? verdict, bool judged)
    {
        if (ranked.Count == 0) return (null, "no candidate passed verify within the budget");
        var top = ranked[0];
        if (ranked.Count == 1) return (top, "the only candidate that passed verify within the budget");
        var second = ranked[1];
        if (verdict is not null && (verdict.Winner == top.Letter || verdict.Winner == second.Letter))
        {
            var w = verdict.Winner == top.Letter ? top : second;
            var other = w == top ? second : top;
            var reason = string.IsNullOrWhiteSpace(verdict.Reason) ? "" : ": " + Short(verdict.Reason, 240);
            return (w, $"the local judge preferred {w.Letter} over {other.Letter}{reason}");
        }
        var heuristic = top.Score > second.Score
            ? $"highest score ({top.Score} vs {second.Score} for {second.Letter})"
            : top.Lines < second.Lines ? $"same score as {second.Letter}, smaller change ({top.Lines} vs {second.Lines} changed lines)"
            : top.Files < second.Files ? $"same score as {second.Letter}, fewer files ({top.Files} vs {second.Files})"
            : top.Attempts < second.Attempts ? $"same score as {second.Letter}, fewer fix rounds"
            : $"tie with {second.Letter}; the earlier candidate wins";
        return (top, judged ? heuristic + "; the judge's verdict was unusable, so the heuristic order stands" : heuristic);
    }

    /// <summary>Вердикт судьи из ответа модели: JSON {"winner":"a","reason":"…"} с буквой одного из двух кандидатов; иначе null.</summary>
    internal static RaceVerdict? ParseVerdict(string? text, char first, char second)
    {
        if (string.IsNullOrWhiteSpace(text) || LocalModel.TryParseJson(text) is not { ValueKind: JsonValueKind.Object } json) return null;
        if (!json.TryGetProperty("winner", out var w) || w.ValueKind != JsonValueKind.String) return null;
        var letter = (w.GetString() ?? "").Trim().ToLowerInvariant();
        if (letter.StartsWith("candidate", StringComparison.Ordinal)) letter = letter["candidate".Length..].Trim();
        if (letter.Length != 1 || (letter[0] != first && letter[0] != second)) return null;
        var reason = json.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? (rs.GetString() ?? "").Trim() : "";
        return new RaceVerdict(letter[0], reason.Replace('\n', ' '));
    }

    /// <summary>Уровень находки ревью «[high] path:line - …» (в нижнем регистре) или "".</summary>
    internal static string Severity(string finding)
    {
        var f = finding.TrimStart();
        if (!f.StartsWith('[')) return "";
        var end = f.IndexOf(']');
        return end > 1 ? f[1..end].Trim().ToLowerInvariant() : "";
    }

    // ───────────────────────── судья, отчёт, уборка ─────────────────────────

    private const string VerdictSchema =
        """{"type":"object","properties":{"winner":{"type":"string"},"reason":{"type":"string"}},"required":["winner","reason"],"additionalProperties":false}""";

    /// <summary>Судья — локальная модель сравнивает diff двух лучших кандидатов с задачей. Сбой или мусор в ответе — null.</summary>
    private static async Task<RaceVerdict?> JudgeAsync(ToolContext ctx, string task, RaceCandidate a, RaceCandidate b)
    {
        try
        {
            var model = await ctx.GetModelAsync().ConfigureAwait(false);
            var system = model.SystemPrompt(
                $"You judge two candidate changes (git diffs) made by different agents for the same task. Both pass the project's check. " +
                "Pick the one that solves the task more completely and correctly, with fewer risks and without unnecessary changes. " +
                $"Reply with JSON only: {{\"winner\":\"{a.Letter}\" or \"{b.Letter}\",\"reason\":\"one short sentence\"}}");
            var budget = model.MaterialBudget(200, system, task);
            var perDiff = Math.Max(1500, budget * 3 / 2);
            var diffA = await GitSandbox.DiffTextAsync(a.Sandbox!, perDiff, null, ctx.Ct).ConfigureAwait(false);
            var diffB = await GitSandbox.DiffTextAsync(b.Sandbox!, perDiff, null, ctx.Ct).ConfigureAwait(false);
            var user = $"TASK:\n{task}\n\nCANDIDATE {a.Letter}:\n{diffA}\n\nCANDIDATE {b.Letter}:\n{diffB}";
            await using var slot = await GpuQueue.AcquireAsync(ctx, ctx.Ct).ConfigureAwait(false);
            var reply = await model.ChatJsonAsync(system, user, ResponseFormat.JsonSchema("race_verdict", VerdictSchema), 200, "judging race candidates", ctx.Ct)
                .ConfigureAwait(false);
            var verdict = ParseVerdict(reply.Reply.Text, a.Letter, b.Letter);
            if (verdict is null) Log.Warn("mcp", $"Судья гонки агентов вернул неразборчивый ответ: {Short(reply.Reply.Text, 200)}");
            return verdict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("mcp", $"Судья гонки агентов недоступен: {ex.Message}");
            return null;
        }
    }

    /// <summary>Сохранить diff кандидата в папку задачи; ссылка на ресурс или null при ошибке.</summary>
    private static async Task<string?> SaveDiffAsync(ToolContext ctx, JobInfo job, RaceCandidate c)
    {
        try
        {
            var diff = await GitSandbox.DiffTextAsync(c.Sandbox!, MaxDiffFileChars, null, ctx.Ct).ConfigureAwait(false);
            FileUtil.WriteAllTextAtomic(DiffFile(job.Id, c.Letter), $"candidate {c.Letter} ({c.Strategy.Name}) · branch {c.Sandbox!.Branch}\n{diff}");
            return ResourceUris.RaceDiff(job.Id, c.Letter);
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("mcp", $"Diff кандидата {c.Letter} задачи {job.Id} не сохранён: {ex.Message}");
            return null;
        }
    }

    /// <summary>Таблица гонки для отчёта: кандидат, стратегия, проверка, ревью, размер, оценка, итог; почему выбран победитель.</summary>
    internal static string RenderReport(IReadOnlyList<RaceCandidate> candidates, RaceCandidate? winner, string why, int concurrency, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
        sb.Append($"agent race: {candidates.Count} candidates, {(concurrency == 1 ? "one at a time" : $"up to {concurrency} at a time")}\n");
        sb.Append("cand  strategy      verify            review          files  lines        score  result\n");
        foreach (var c in candidates)
        {
            var verify = c.Verify is null ? "-" : c.Verify.Passed ? $"passed ({c.Attempts})" : c.Verify.TimedOut ? $"timed out ({c.Attempts})" : $"failed ({c.Attempts})";
            var size = c.Changes.Count == 0 ? "-" : $"+{c.Added} −{c.Removed}";
            var result = c == winner ? "WINNER" : c.Disqualified is { } d ? "out: " + d : "runner-up";
            sb.Append($"{c.Letter,-4}  {c.Strategy.Name,-12}  {verify,-16}  {ReviewCell(c.Review),-14}  {(c.Changes.Count == 0 ? "-" : c.Files.ToString(System.Globalization.CultureInfo.InvariantCulture)),5}  " +
                      $"{size,-11}  {(c.Disqualified is null ? c.Score.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-"),5}  {Short(result, 140)}\n");
        }
        sb.Append(winner is null ? $"no winner: {why}\n" : $"winner {winner.Letter} ({winner.Strategy.Name}): {why}\n");
        var others = candidates.Where(c => c != winner && c.DiffUri is not null).Select(c => $"{c.Letter} {c.DiffUri}").ToList();
        if (others.Count > 0) sb.Append("other candidates' diffs (sandboxes removed): ").Append(string.Join(" · ", others)).Append('\n');
        foreach (var n in notes) sb.Append("note: ").Append(n).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static readonly string[] SeverityOrder = ["critical", "high", "medium", "low"];

    private static string ReviewCell(List<string>? review)
    {
        if (review is null) return "-";
        if (review.Count == 0 || review.Any(f => f.StartsWith("no issues", StringComparison.OrdinalIgnoreCase))) return "no issues";
        if (review.Any(f => f.StartsWith("review skipped", StringComparison.OrdinalIgnoreCase))) return "skipped";
        var counts = review.Select(Severity).Where(s => s.Length > 0).GroupBy(s => s)
            .OrderBy(g => Array.IndexOf(SeverityOrder, g.Key) is var i and >= 0 ? i : 9)
            .Select(g => $"{g.Count()} {g.Key}");
        var text = string.Join(", ", counts);
        return text.Length == 0 ? "no issues" : text;
    }

    /// <summary>Запомнить песочницу кандидата в задаче (для уборки после сбоя процесса: retry/discard).</summary>
    private static void Track(JobInfo job, SandboxInfo sb)
    {
        lock (job)
        {
            job.RaceSandboxes ??= [];
            job.RaceSandboxes.Add(sb);
            JobStore.Save(job);
        }
    }

    /// <summary>
    /// Удалить открытые песочницы кандидатов (worktree и ветку), кроме <paramref name="keep"/>. Возвращает число удалённых.
    /// Вызывается по итогам гонки, при отмене, а также из local_job discard/retry для задачи, прерванной посреди гонки.
    /// </summary>
    internal static async Task<int> DiscardCandidatesAsync(JobInfo job, SandboxInfo? keep)
    {
        List<SandboxInfo> open;
        lock (job) open = [.. (job.RaceSandboxes ?? []).Where(s => SandboxState.IsOpen(s.State) && (keep is null || s.Branch != keep.Branch))];
        if (open.Count == 0) return 0;
        foreach (var s in open)
        {
            await GitSandbox.RemoveAsync(s, deleteBranch: true, CancellationToken.None).ConfigureAwait(false);
            s.State = SandboxState.Discarded;
        }
        lock (job) JobStore.Save(job);
        return open.Count;
    }
}
