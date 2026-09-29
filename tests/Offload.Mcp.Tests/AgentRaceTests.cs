using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;
using Offload.OpenCode;

namespace Offload.Mcp.Tests;

/// <summary>Гонка агентов (race = 2…4): чистая логика выбора победителя и отчёта — без git, модели и OpenCode.</summary>
public sealed class AgentRaceScoringTests
{
    private static RaceCandidate Cand(char letter, bool passed = true, int files = 1, int lines = 10, int attempts = 1, List<string>? review = null,
        string outcome = RaceOutcome.Done, IReadOnlyList<string>? dropped = null)
    {
        var c = new RaceCandidate(letter, AgentRace.Strategies[letter - 'a'])
        {
            Outcome = outcome,
            Verify = new VerifyResult("dotnet test", passed ? 0 : 1, false, ["x"], TimeSpan.FromSeconds(1)),
            Attempts = attempts,
            Review = review,
            Commit = new SandboxCommit(true, dropped ?? []),
        };
        c.Changes = [.. Enumerable.Range(0, files).Select(i => new SandboxChange($"f{i}.cs", i == 0 ? lines : 0, 0, false))];
        return c;
    }

    private static List<RaceCandidate> Scored(int maxFiles, params RaceCandidate[] cs)
    {
        foreach (var c in cs) AgentRace.Score(c, maxFiles);
        return AgentRace.Rank(cs);
    }

    [Fact]
    public void Evaluate_DisqualifiesFailuresAndBudgetBreaches()
    {
        Assert.Null(AgentRace.Evaluate(Cand('a'), 0));
        Assert.StartsWith("verify failed", AgentRace.Evaluate(Cand('a', passed: false), 0));
        Assert.Contains("max_files=2", AgentRace.Evaluate(Cand('a', files: 3), 2));
        Assert.Null(AgentRace.Evaluate(Cand('a', files: 3), 0));
        Assert.Equal("no transferable changes", AgentRace.Evaluate(Cand('a', files: 0), 0));
        Assert.Equal("timed out", AgentRace.Evaluate(Cand('a', outcome: RaceOutcome.TimedOut), 0));
        Assert.Equal("changed files outside allowed_paths", AgentRace.Evaluate(Cand('a', dropped: ["docs/x.md (outside allowed_paths)"]), 0));
        // Отброшенные артефакты сборки не мешают: их и не должно быть в переносе.
        Assert.Null(AgentRace.Evaluate(Cand('a', dropped: ["bin/x.dll (build/cache artifact)"]), 0));
        Assert.Equal("review found a critical issue", AgentRace.Evaluate(Cand('a', review: ["[critical] a.cs:1 - sql injection - fix"]), 0));
    }

    [Fact]
    public void Score_ReviewAndFixRoundsLowerTheScore()
    {
        var clean = Cand('a');
        var high = Cand('b', review: ["[high] a.cs:3 - null deref - check", "[low] a.cs:9 - naming - rename"]);
        var retried = Cand('c', attempts: 3);
        AgentRace.Score(clean, 0);
        AgentRace.Score(high, 0);
        AgentRace.Score(retried, 0);
        Assert.Equal(100, clean.Score);
        Assert.Equal(74, high.Score);
        Assert.Equal(94, retried.Score);
    }

    [Fact]
    public void Rank_TieOnScore_FewerLinesThenFilesThenLetter()
    {
        var ranked = Scored(0, Cand('a', lines: 30), Cand('b', lines: 10, files: 2), Cand('c', lines: 10, files: 1), Cand('d', lines: 10, files: 1));
        Assert.Equal("cdba", string.Concat(ranked.Select(c => c.Letter)));

        var (winner, why) = AgentRace.Choose(ranked, null, judged: false);
        Assert.Equal('c', winner!.Letter);
        Assert.Contains("tie with d", why);
    }

    [Fact]
    public void Choose_AllFail_NoWinnerWithClearReason()
    {
        var ranked = Scored(0, Cand('a', passed: false), Cand('b', outcome: RaceOutcome.Failed), Cand('c', files: 0));
        Assert.Empty(ranked);
        var (winner, why) = AgentRace.Choose(ranked, null, judged: false);
        Assert.Null(winner);
        Assert.Equal("no candidate passed verify within the budget", why);
    }

    [Fact]
    public void Choose_SingleEligible_WinsWithoutJudge()
    {
        var ranked = Scored(0, Cand('a', passed: false), Cand('b'));
        Assert.False(AgentRace.NeedsJudge(ranked));
        var (winner, why) = AgentRace.Choose(ranked, null, judged: false);
        Assert.Equal('b', winner!.Letter);
        Assert.Contains("only candidate", why);
    }

    [Fact]
    public void Choose_JudgeVerdictPicksAmongTopTwo_MalformedFallsBackToHeuristic()
    {
        var ranked = Scored(0, Cand('a', lines: 5), Cand('b', lines: 50));
        Assert.True(AgentRace.NeedsJudge(ranked));

        var (judgedWinner, judgedWhy) = AgentRace.Choose(ranked, new RaceVerdict('b', "adds the regression test"), judged: true);
        Assert.Equal('b', judgedWinner!.Letter);
        Assert.Contains("judge preferred b over a: adds the regression test", judgedWhy);

        var (fallback, fallbackWhy) = AgentRace.Choose(ranked, null, judged: true);
        Assert.Equal('a', fallback!.Letter);
        Assert.Contains("verdict was unusable", fallbackWhy);

        // Буква не из пары лучших — тоже неразборчивый вердикт.
        Assert.Equal('a', AgentRace.Choose(ranked, new RaceVerdict('d', "?"), judged: true).Winner!.Letter);
    }

    [Fact]
    public void NeedsJudge_OnlyWhenScoresAreClose()
    {
        var ranked = Scored(0, Cand('a'), Cand('b', review: ["[high] x:1 - bug - fix"]));
        Assert.Equal(25, ranked[0].Score - ranked[1].Score);
        Assert.False(AgentRace.NeedsJudge(ranked));
        Assert.Contains("highest score (100 vs 75", AgentRace.Choose(ranked, null, false).Why);
    }

    [Theory]
    [InlineData("""{"winner":"b","reason":"cleaner"}""", 'b', "cleaner")]
    [InlineData("""{"winner":"Candidate A","reason":"covers the edge case"}""", 'a', "covers the edge case")]
    [InlineData("```json\n{\"winner\":\"a\",\"reason\":\"x\"}\n```", 'a', "x")]
    [InlineData("""{"winner":"b"}""", 'b', "")]
    public void ParseVerdict_AcceptsJson(string text, char winner, string reason)
    {
        var v = AgentRace.ParseVerdict(text, 'a', 'b');
        Assert.NotNull(v);
        Assert.Equal(winner, v!.Winner);
        Assert.Equal(reason, v.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I prefer b")]
    [InlineData("""{"winner":"c","reason":"not in the pair"}""")]
    [InlineData("""{"winner":2}""")]
    [InlineData("""{"choice":"a"}""")]
    [InlineData("""["a"]""")]
    [InlineData("""{"winner":"a","reason":""")]
    public void ParseVerdict_MalformedIsNull(string text) => Assert.Null(AgentRace.ParseVerdict(text, 'a', 'b'));

    [Theory]
    [InlineData(1, 4, 1)]
    [InlineData(2, 4, 1)]
    [InlineData(3, 4, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(8, 4, 4)]
    [InlineData(0, 3, 1)]
    public void Concurrency_BoundedByAgentSlots(int parallel, int candidates, int expected) =>
        Assert.Equal(expected, AgentRace.Concurrency(parallel, candidates));

    [Fact]
    public void ValidateCount_OutOfRange_Throws()
    {
        AgentRace.ValidateCount(1);
        AgentRace.ValidateCount(4);
        Assert.Contains("race must be 1-4", Assert.Throws<ToolException>(() => AgentRace.ValidateCount(0)).Message);
        Assert.Throws<ToolException>(() => AgentRace.ValidateCount(5));
        Assert.Throws<ToolException>(() => AgentRace.ValidateCount(-1));
    }

    [Fact]
    public void Temperature_ShiftsFromModelSampling()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.Add(new InstalledModel { Id = "m", Sampling = new SamplingSettings { Temperature = 1.4 } });
        cfg.Models.ActiveModelId = "m";
        Assert.Null(AgentRace.Temperature(cfg, AgentRace.Strategies[0]));
        Assert.Equal(1.5, AgentRace.Temperature(cfg, AgentRace.Strategies[2]));
        Assert.Equal(1.2, AgentRace.Temperature(cfg, AgentRace.Strategies[3]));
    }

    [Fact]
    public void RenderReport_TableWinnerAndDiffLinks()
    {
        var a = Cand('a', passed: false);
        var b = Cand('b', lines: 12, review: ["[medium] b.cs:2 - edge case - handle"]);
        var c = Cand('c', lines: 40);
        foreach (var x in new[] { a, b, c }) AgentRace.Score(x, 0);
        a.DiffUri = "offload://jobs/20260927-101010-abcd/race/a";
        c.DiffUri = "offload://jobs/20260927-101010-abcd/race/c";
        var text = AgentRace.RenderReport([a, b, c], b, "why-text", 1, ["candidates ran one after another: test"]);

        Assert.Contains("agent race: 3 candidates, one at a time", text);
        Assert.Contains("out: verify failed (exit 1)", text);
        Assert.Contains("WINNER", text);
        Assert.Contains("runner-up", text);
        Assert.Contains("1 medium", text);
        Assert.Contains("winner b (root-cause): why-text", text);
        Assert.Contains("a offload://jobs/20260927-101010-abcd/race/a · c offload://jobs/20260927-101010-abcd/race/c", text);
        Assert.Contains("note: candidates ran one after another", text);
    }

    [Fact]
    public void SandboxNames_CandidateSuffix_MapsBackToJob()
    {
        const string id = "20260927-101010-abcd";
        Assert.Equal(id, GitSandbox.SandboxName(id, null));
        Assert.Equal(id + "-b", GitSandbox.SandboxName(id, 'b'));
        Assert.Equal(id, GitSandbox.OwnerJobId(id + "-b"));
        Assert.Equal(id, GitSandbox.OwnerJobId(id));
        Assert.Equal("foo-b", GitSandbox.OwnerJobId("foo-b"));
        Assert.Equal("branches offload/" + id + "-a…c", AgentRace.BranchLabel(id, 3));
        Assert.Equal("branch offload/" + id, AgentRace.BranchLabel(id, 1));
    }
}

/// <summary>
/// Гонка агентов целиком: настоящий git и проверочная команда, поддельный агент OpenCode (<see cref="AgentTaskTool.Runner"/>)
/// и поддельный llama-server (судья). Выбор, слияние победителя, уборка песочниц, параллельность, отмена, фоновое описание.
/// </summary>
[Collection("AppPaths")]
public sealed class AgentRaceTests
{
    private const string Verify = "findstr /c:\"ok\" result.txt";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Подмена агента OpenCode на время теста.</summary>
    private sealed class RunnerScope : IDisposable
    {
        private readonly Func<AppConfig, string, string, OpenCodeRunOptions, Action<string>?, CancellationToken, Task<OpenCodeRunResult>> _saved = AgentTaskTool.Runner;

        public RunnerScope(Func<AppConfig, string, string, OpenCodeRunOptions, Action<string>?, CancellationToken, Task<OpenCodeRunResult>> runner) =>
            AgentTaskTool.Runner = runner;

        public void Dispose() => AgentTaskTool.Runner = _saved;
    }

    private static OpenCodeRunResult Ok(string text = "Result: done") => new(true, text, [], [], null, 0, TimeSpan.FromMilliseconds(10), 100, 20);

    /// <summary>Буква кандидата по папке песочницы («…\&lt;job&gt;-b»); '-' — обычная задача без гонки.</summary>
    private static char LetterOf(string wd)
    {
        var name = Path.GetFileName(wd.TrimEnd('\\'));
        return name.Length > 2 && name[^2] == '-' ? name[^1] : '-';
    }

    private static TestEnv NewEnv(int port, int parallel = 1)
    {
        var exe = Path.Combine(Path.GetTempPath(), "pc-fake-opencode-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.WriteAllText(exe, "");
        return new TestEnv(port, configure: c =>
        {
            c.OpenCode.Enabled = true;
            c.OpenCode.ExecutablePath = exe;
            c.Mcp.VerifyCommandAllowlist = ["findstr *"];
            c.Server.Parallel = parallel;
        });
    }

    private static void RequireGit() => Assert.SkipUnless(Git.Executable is not null, "нужен git в PATH");

    private static async Task InitRepoAsync(TestEnv env)
    {
        foreach (var args in new[]
                 {
                     new[] { "init", "-q" }, ["config", "core.autocrlf", "false"], ["config", "user.name", "Test"], ["config", "user.email", "t@example.com"],
                 })
            await GitAsync(env.Workspace, args);
        env.WriteFile("a.txt", "one\n");
        await GitAsync(env.Workspace, "add", "-A");
        await GitAsync(env.Workspace, "commit", "-q", "-m", "init");
    }

    private static async Task<string> GitAsync(string dir, params string[] args)
    {
        var r = await Git.RunAsync(dir, args, Ct);
        Assert.True(r.Success, $"git {string.Join(' ', args)}: {r.StdErr}");
        return r.StdOut.Trim();
    }

    private static List<string> SandboxDirsOf(string jobId) =>
        Directory.Exists(GitSandbox.SandboxesDir)
            ? [.. Directory.EnumerateDirectories(GitSandbox.SandboxesDir).Select(Path.GetFileName).OfType<string>().Where(n => n.StartsWith(jobId, StringComparison.Ordinal))]
            : [];

    private static JobInfo LatestJob() => JobStore.List(1)[0];

    /// <summary>Кандидат a проваливает проверку, b — маленькое верное решение, c — верное, но больше (лишний файл).</summary>
    private static Task<OpenCodeRunResult> ThreeWayAgent(AppConfig cfg, string task, string wd, OpenCodeRunOptions o, Action<string>? p, CancellationToken ct)
    {
        switch (LetterOf(wd))
        {
            case 'a':
                File.WriteAllText(Path.Combine(wd, "result.txt"), "bad\n");
                break;
            case 'b':
                File.WriteAllText(Path.Combine(wd, "result.txt"), "ok\n");
                break;
            default:
                File.WriteAllText(Path.Combine(wd, "result.txt"), "ok\n");
                File.WriteAllText(Path.Combine(wd, "extra.txt"), "more\nlines\n");
                break;
        }
        return Task.FromResult(Ok());
    }

    [Fact]
    public async Task Race_PicksSmallestVerifiedDiff_WhenJudgeIsMalformed_AndCleansUp()
    {
        RequireGit();
        using var llama = new FakeLlamaServer { Responder = _ => "I think b is nicer" };
        using var env = NewEnv(llama.Port);
        await InitRepoAsync(env);
        var prompts = new List<(char Letter, string Task, double? Temperature)>();
        using var scope = new RunnerScope((cfg, task, wd, o, p, ct) =>
        {
            lock (prompts) prompts.Add((LetterOf(wd), task, o.Temperature));
            return ThreeWayAgent(cfg, task, wd, o, p, ct);
        });

        var text = await AgentTaskTool.RunAsync(env.Context(ct: Ct), new AgentTaskRequest
        {
            Task = "make result.txt say ok",
            VerifyCommand = Verify,
            FixAttempts = 0,
            TimeoutMinutes = 5,
            Race = 3,
        });

        var job = LatestJob();
        Assert.Equal(JobStatus.Applied, job.Status);
        Assert.Contains("agent race: 3 candidates, one at a time", text);
        Assert.Contains("winner b (root-cause)", text);
        Assert.Contains("verdict was unusable", text);
        Assert.Contains("out: verify failed", text);
        Assert.Contains("merged: applied", text);
        Assert.Equal("ok\n", File.ReadAllText(env.PathOf("result.txt")));
        Assert.False(File.Exists(env.PathOf("extra.txt")), "изменения проигравшего кандидата не должны попасть в проект");
        Assert.Contains("candidates ran one after another", text);

        // Бриф: у каждого своя стратегия; температура — только у стратегий со сдвигом.
        Assert.Equal("abc", string.Concat(prompts.Select(x => x.Letter).Order()));
        Assert.Contains(prompts, x => x.Letter == 'a' && x.Task.Contains("candidate a (minimal)", StringComparison.Ordinal) && x.Temperature is null);
        Assert.Contains(prompts, x => x.Letter == 'c' && x.Task.Contains("candidate c (clean)", StringComparison.Ordinal) && x.Temperature is not null);
        // Судья получил diff двух допущенных кандидатов.
        Assert.Contains(llama.Requests, r => r.Contains("CANDIDATE b", StringComparison.Ordinal) && r.Contains("CANDIDATE c", StringComparison.Ordinal));

        // Уборка: ни одной песочницы и ветки кандидатов; diff проигравших сохранены ресурсами.
        Assert.Empty(SandboxDirsOf(job.Id));
        Assert.Equal("", await GitAsync(env.Workspace, "branch", "--list", "offload/*"));
        Assert.True(File.Exists(AgentRace.DiffFile(job.Id, 'a')));
        Assert.True(File.Exists(AgentRace.DiffFile(job.Id, 'c')));
        Assert.Contains($"c offload://jobs/{job.Id}/race/c", text);
        Assert.Equal("sandbox/apply/race3", job.Mode);
        Assert.All(job.RaceSandboxes!, s => Assert.Equal(SandboxState.Discarded, s.State));
    }

    [Fact]
    public async Task Race_JudgeVerdict_OverridesSizeTiebreak()
    {
        RequireGit();
        using var llama = new FakeLlamaServer { Responder = _ => """{"winner":"c","reason":"also documents the change"}""" };
        using var env = NewEnv(llama.Port);
        await InitRepoAsync(env);
        using var scope = new RunnerScope(ThreeWayAgent);

        var text = await AgentTaskTool.RunAsync(env.Context(ct: Ct), new AgentTaskRequest
        {
            Task = "make result.txt say ok",
            VerifyCommand = Verify,
            FixAttempts = 0,
            TimeoutMinutes = 5,
            Race = 3,
            Merge = "none",
        });

        var job = LatestJob();
        Assert.Contains("the local judge preferred c over b: also documents the change", text);
        // merge=none: победитель ждёт ревью на своей ветке, остальные убраны.
        Assert.Equal(JobStatus.PendingMerge, job.Status);
        Assert.Equal("offload/" + job.Id + "-c", job.Sandbox!.Branch);
        Assert.Equal("offload/" + job.Id + "-c", await GitAsync(env.Workspace, "branch", "--list", "--format=%(refname:short)", "offload/*"));
        Assert.False(File.Exists(env.PathOf("result.txt")));

        // Отложенное слияние победителя — обычным путём local_job.
        var merged = await JobTool.RunAsync(env.Context(ct: Ct), job.Id, "merge", 0, null, false, false, 0);
        Assert.Contains("merged", merged);
        Assert.True(File.Exists(env.PathOf("extra.txt")));
        Assert.Equal("", await GitAsync(env.Workspace, "branch", "--list", "offload/*"));
    }

    [Fact]
    public async Task Race_AllCandidatesFail_NothingMerged_ClearMessage()
    {
        RequireGit();
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port);
        await InitRepoAsync(env);
        using var scope = new RunnerScope((cfg, task, wd, o, p, ct) =>
        {
            File.WriteAllText(Path.Combine(wd, "result.txt"), "bad\n");
            return Task.FromResult(Ok());
        });

        var text = await AgentTaskTool.RunAsync(env.Context(ct: Ct), new AgentTaskRequest
        {
            Task = "make result.txt say ok",
            VerifyCommand = Verify,
            FixAttempts = 1,
            TimeoutMinutes = 5,
            Race = 2,
        });

        var job = LatestJob();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("NOT merged: no candidate passed verify within the budget", text);
        Assert.Contains("Nothing was changed in your project", text);
        Assert.Contains("failed (2)", text); // одна попытка исправления у каждого
        Assert.False(File.Exists(env.PathOf("result.txt")));
        Assert.Null(job.Sandbox);
        Assert.Empty(SandboxDirsOf(job.Id));
        Assert.Equal("", await GitAsync(env.Workspace, "branch", "--list", "offload/*"));
        Assert.DoesNotContain(llama.Requests, r => r.Contains("CANDIDATE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task Race_Parallelism_BoundedByAgentSlots_OneOpenCodeRunAtATime(int parallel, int maxInFlight)
    {
        RequireGit();
        using var llama = new FakeLlamaServer { Responder = _ => """{"winner":"a","reason":"x"}""" };
        using var env = NewEnv(llama.Port, parallel);
        await InitRepoAsync(env);
        var running = 0;
        var maxRunning = 0;
        var maxWorktrees = 0;
        using var scope = new RunnerScope(async (cfg, task, wd, o, p, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (env) maxRunning = Math.Max(maxRunning, now);
            var jobId = GitSandbox.OwnerJobId(Path.GetFileName(wd));
            lock (env) maxWorktrees = Math.Max(maxWorktrees, SandboxDirsOf(jobId).Count);
            await Task.Delay(250, ct);
            File.WriteAllText(Path.Combine(wd, "result.txt"), $"ok {LetterOf(wd)}\n");
            Interlocked.Decrement(ref running);
            return Ok();
        });

        var text = await AgentTaskTool.RunAsync(env.Context(ct: Ct), new AgentTaskRequest
        {
            Task = "make result.txt say ok",
            VerifyCommand = Verify,
            FixAttempts = 0,
            TimeoutMinutes = 5,
            Race = 3,
        });

        Assert.Equal(1, maxRunning); // OpenCode не выдерживает одновременных запусков на одной папке данных
        Assert.InRange(maxWorktrees, 1, maxInFlight);
        Assert.Contains(parallel == 1 ? "one at a time" : "up to 2 at a time", text);
        Assert.Equal(JobStatus.Applied, LatestJob().Status);
        Assert.Empty(SandboxDirsOf(LatestJob().Id));
    }

    [Fact]
    public async Task Race_Cancelled_StopsAllAndRemovesEverySandbox()
    {
        RequireGit();
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port, parallel: 3);
        await InitRepoAsync(env);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = 0;
        using var scope = new RunnerScope(async (cfg, task, wd, o, p, ct) =>
        {
            File.WriteAllText(Path.Combine(wd, "result.txt"), "ok\n");
            // Запуски OpenCode идут по одному: отмена — пока первый агент «работает», остальные кандидаты ждут.
            if (Interlocked.Increment(ref started) == 1) cts.CancelAfter(TimeSpan.FromMilliseconds(400));
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentTaskTool.RunAsync(env.Context(ct: cts.Token), new AgentTaskRequest
        {
            Task = "make result.txt say ok",
            VerifyCommand = Verify,
            FixAttempts = 0,
            TimeoutMinutes = 5,
            Race = 4,
        }));

        var job = LatestJob();
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Empty(SandboxDirsOf(job.Id));
        Assert.Equal("", await GitAsync(env.Workspace, "branch", "--list", "offload/*"));
        Assert.False(File.Exists(env.PathOf("result.txt")));
    }

    [Fact]
    public async Task RaceOne_KeepsSingleAgentPath()
    {
        RequireGit();
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port);
        await InitRepoAsync(env);
        string? seenTask = null;
        string? seenWd = null;
        using var scope = new RunnerScope((cfg, task, wd, o, p, ct) =>
        {
            seenTask = task;
            seenWd = wd;
            File.WriteAllText(Path.Combine(wd, "result.txt"), "ok\n");
            return Task.FromResult(Ok());
        });

        var text = await AgentTaskTool.RunAsync(env.Context(ct: Ct), new AgentTaskRequest { Task = "make result.txt say ok", VerifyCommand = Verify, TimeoutMinutes = 5 });

        var job = LatestJob();
        Assert.Equal(JobStatus.Applied, job.Status);
        Assert.Equal("sandbox/apply", job.Mode);
        Assert.Null(job.RaceSandboxes);
        Assert.Equal(job.Id, Path.GetFileName(seenWd));
        Assert.DoesNotContain("APPROACH", seenTask);
        Assert.DoesNotContain("agent race", text);
        Assert.Equal("ok\n", File.ReadAllText(env.PathOf("result.txt")));
        Assert.Equal(AgentTaskTool.AgentLogPath(job), Path.Combine(JobStore.DirOf(job.Id), "opencode.log"));
    }

    [Fact]
    public void Prepare_RaceValidation()
    {
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port);
        var cfg = env.Context().Cfg;
        Assert.Contains("race must be 1-4", Assert.Throws<ToolException>(() => AgentTaskTool.Prepare(cfg, new AgentTaskRequest { Task = "do it", Race = 0 })).Message);
        Assert.Contains("race must be 1-4", Assert.Throws<ToolException>(() => AgentTaskTool.Prepare(cfg, new AgentTaskRequest { Task = "do it", Race = 5, VerifyCommand = Verify })).Message);
        Assert.Contains("needs a verify_command", Assert.Throws<ToolException>(() => AgentTaskTool.Prepare(cfg, new AgentTaskRequest { Task = "do it", Race = 2 })).Message);
    }

    [Fact]
    public async Task Solve_RaceOutOfRange_FailsBeforeAnyWork()
    {
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port);
        var ex = await Assert.ThrowsAsync<ToolException>(() => SolveTool.RunAsync(env.Context(ct: Ct), "fix it", "bug", Verify, null, null, null, 0, 0, false, false, race: 7));
        Assert.Contains("race must be 1-4", ex.Message);
        Assert.Empty(JobStore.List(5));
    }

    [Fact]
    public void BackgroundSpec_RoundTripsRace_AndVersionsIt()
    {
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port);
        var job = JobStore.Create(McpToolNames.Solve, env.Workspace, "t", null);

        var race = BackgroundJobSpec.From(job, new AgentTaskRequest { Task = "t", VerifyCommand = Verify, Race = 3 }, [env.Workspace]);
        Assert.Equal(BackgroundJobSpec.CurrentVersion, race.Version);
        var back = BackgroundJobSpec.Parse(race.ToJson());
        Assert.Equal(3, back.Race);
        Assert.Equal(3, back.ToRequest(background: true).Race);
        BackgroundJobSpec.Save(race);
        Assert.Equal(3, BackgroundJobSpec.TryLoad(job.Id)!.Race);

        // Без гонки — прежняя версия (её принимает и трей прошлой версии); старое описание без поля — один агент.
        var single = BackgroundJobSpec.From(job, new AgentTaskRequest { Task = "t" }, [env.Workspace]);
        Assert.Equal(2, single.Version);
        var legacy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(single.ToJson())!;
        legacy.Remove(legacy.Keys.First(k => k.Equals("race", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, BackgroundJobSpec.Parse(JsonSerializer.Serialize(legacy)).Race);
    }

    [Fact]
    public void BackgroundSpec_HostValidatesRace()
    {
        RequireGit();
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port);
        var job = JobStore.Create(McpToolNames.AgentTask, env.Workspace, "t", null);
        var spec = BackgroundJobSpec.From(job, new AgentTaskRequest { Task = "t", VerifyCommand = Verify, Race = 2 }, [env.Workspace]);

        var (_, _, request) = BackgroundJobSpec.ValidateForHost(spec, env.Context().Cfg);
        Assert.Equal(2, request.Race);
        Assert.Throws<ToolException>(() => BackgroundJobSpec.ValidateForHost(spec with { Race = 9 }, env.Context().Cfg));
        Assert.Throws<ToolException>(() => BackgroundJobSpec.ValidateForHost(spec with { VerifyCommand = null }, env.Context().Cfg));
    }

    [Fact]
    public async Task RaceDiffResource_ReadsSavedCandidateDiff_OnlyInItsWorkspace()
    {
        using var env = new TestEnv();
        var job = JobStore.Create(McpToolNames.AgentTask, env.Workspace, "t", null);
        File.WriteAllText(AgentRace.DiffFile(job.Id, 'b'), "candidate b (root-cause)\n+fixed line\n");
        var foreign = JobStore.Create(McpToolNames.AgentTask, Path.Combine(Path.GetTempPath(), "other-ws-" + Guid.NewGuid().ToString("N")), "t", null);
        File.WriteAllText(AgentRace.DiffFile(foreign.Id, 'a'), "x");
        await using var h = await McpHarness.StartAsync(env.Workspace);

        var read = await h.Client.ReadResourceAsync(Resources.ResourceUris.RaceDiff(job.Id, 'b'), cancellationToken: Ct);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextResourceContents>(Assert.Single(read.Contents)).Text;
        Assert.Contains("+fixed line", text);

        await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(() => h.Client.ReadResourceAsync($"offload://jobs/{job.Id}/race/z", cancellationToken: Ct).AsTask());
        await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(() => h.Client.ReadResourceAsync(Resources.ResourceUris.RaceDiff(job.Id, 'c'), cancellationToken: Ct).AsTask());
        await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(() => h.Client.ReadResourceAsync(Resources.ResourceUris.RaceDiff(foreign.Id, 'a'), cancellationToken: Ct).AsTask());
    }

    [Fact]
    public async Task Discard_InterruptedRace_RemovesCandidateSandboxes()
    {
        RequireGit();
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port);
        await InitRepoAsync(env);
        var job = JobStore.Create(McpToolNames.AgentTask, env.Workspace, "t", null);
        var a = await GitSandbox.CreateAsync(env.Workspace, job.Id, JobStore.DirOf(job.Id), null, Ct, 'a');
        var b = await GitSandbox.CreateFromAsync(a, job.Id, 'b', Ct);
        Assert.Equal(a.BaseCommit, b.BaseCommit);
        job.RaceSandboxes = [a, b];
        JobStore.MarkInterrupted(job, "test");

        var text = await JobTool.RunAsync(env.Context(ct: Ct), job.Id, "discard", 0, null, false, false, 0);

        Assert.Contains("2 race candidate sandbox(es)", text);
        Assert.Equal(JobStatus.Discarded, JobStore.Load(job.Id).Status);
        Assert.Empty(SandboxDirsOf(job.Id));
        Assert.Equal("", await GitAsync(env.Workspace, "branch", "--list", "offload/*"));
    }
}
