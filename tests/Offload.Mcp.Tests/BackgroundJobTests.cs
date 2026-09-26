using System.Diagnostics;
using Offload.Core;
using Offload.Core.Ipc;
using Offload.Core.Processes;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>
/// Долговечные фоновые задачи: выполнение в трее по IPC (job-start) с запасным вариантом в MCP-процессе, повторная проверка
/// описания исполнителем, состояние из «другого процесса» (только через папку задачи), восстановление после сбоя, retry.
/// Настоящий движок (OpenCode + git) подменяется: проверяется хостинг, а не агент.
/// </summary>
[Collection("AppPaths")]
public sealed class BackgroundJobTests
{
    /// <summary>Подмена движка и ускорение опросов на время теста.</summary>
    private sealed class EngineScope : IDisposable
    {
        private readonly Func<ToolContext, JobInfo, BackgroundJobSpec, Task<string>> _engine = BackgroundJobs.Engine;
        private readonly TimeSpan _pump = BackgroundJobs.PumpInterval;
        private readonly TimeSpan _poll = BackgroundJobs.PollInterval;

        public EngineScope(Func<ToolContext, JobInfo, BackgroundJobSpec, Task<string>> engine)
        {
            BackgroundJobs.Engine = engine;
            BackgroundJobs.PumpInterval = TimeSpan.FromMilliseconds(40);
            BackgroundJobs.PollInterval = TimeSpan.FromMilliseconds(40);
        }

        public void Dispose()
        {
            BackgroundJobs.Engine = _engine;
            BackgroundJobs.PumpInterval = _pump;
            BackgroundJobs.PollInterval = _poll;
        }
    }

    /// <summary>Движок-заглушка: сообщает о ходе работы, ставит итоговый статус и возвращает отчёт.</summary>
    private static async Task<string> FakeEngine(ToolContext ctx, JobInfo job, BackgroundJobSpec spec)
    {
        ctx.Progress.Report("agent: editing Calc.cs");
        await Task.Delay(150, ctx.Ct);
        job.Status = JobStatus.NoChanges;
        job.FinishedUtc = DateTime.UtcNow;
        JobStore.Save(job);
        return $"job_id: {job.Id} · status: {job.Status} · done ({spec.Task})";
    }

    /// <summary>TestEnv, где OpenCode «установлен» (пустой файл) — описание проходит проверку исполнителя.</summary>
    private static TestEnv NewEnv(int? port = null)
    {
        var exe = Path.Combine(Path.GetTempPath(), "pc-fake-opencode-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.WriteAllText(exe, "");
        return new TestEnv(port, configure: c =>
        {
            c.OpenCode.Enabled = true;
            c.OpenCode.ExecutablePath = exe;
        });
    }

    private static void RequireGit() => Assert.SkipUnless(Git.Executable is not null, "нужен git в PATH (проверка описания требует его, как и движок)");

    private static (JobInfo Job, BackgroundJobSpec Spec) NewAgentJob(TestEnv env, Func<BackgroundJobSpec, BackgroundJobSpec>? tweak = null)
    {
        var job = JobStore.Create(McpToolNames.AgentTask, env.Workspace, "add a Sum method", null);
        var spec = BackgroundJobSpec.From(job, new AgentTaskRequest { Task = "add a Sum method", Merge = "apply", FixAttempts = 1, TimeoutMinutes = 5 },
            [env.Workspace]);
        if (tweak is not null) spec = tweak(spec);
        BackgroundJobSpec.Save(spec);
        return (job, spec);
    }

    private static async Task<JobInfo> WaitFinishedAsync(string id)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (!BackgroundJobs.IsRunning(id) && JobStore.Load(id).Status != JobStatus.Running) return JobStore.Load(id);
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("фоновая задача не завершилась");
    }

    [Fact]
    public async Task Launch_TrayAbsent_FallsBackToInProcess()
    {
        using var env = NewEnv();
        using var scope = new EngineScope(FakeEngine);
        var (job, spec) = NewAgentJob(env);

        Assert.False(IpcClient.IsTrayRunning());
        var text = await BackgroundJobs.LaunchAsync(env.Context(), job, spec);

        Assert.Contains("runs inside this IDE session", text);
        var done = await WaitFinishedAsync(job.Id);
        Assert.Equal(JobHost.Mcp, done.Host);
        Assert.Equal(Environment.ProcessId, done.HostPid);
        Assert.Equal(JobStatus.NoChanges, done.Status);
        Assert.Contains("done (add a Sum method)", BackgroundJobs.ReadResult(job.Id));
        Assert.StartsWith("mcp ", File.ReadAllText(Path.Combine(JobStore.DirOf(job.Id), BackgroundJobs.ClaimFile)));
    }

    [Fact]
    public async Task Launch_TrayRunning_RoundTripViaIpc_HostRunsJob()
    {
        RequireGit();
        using var env = NewEnv();
        using var scope = new EngineScope(FakeEngine);
        var (job, spec) = NewAgentJob(env);
        IpcRequest? received = null;
        var finished = new TaskCompletionSource<HostedJobFinished>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var instance = new Mutex(false, IpcNames.MutexName);
        using var tray = new IpcServer(req =>
        {
            received = req;
            return Task.FromResult(req.Command == IpcCommands.JobStart
                ? BackgroundJobHosting.Start(req, f => finished.TrySetResult(f))
                : new IpcResponse(false, "unknown"));
        });
        tray.Start();

        var text = await BackgroundJobs.LaunchAsync(env.Context(), job, spec);

        Assert.Contains("in the Offload tray app", text);
        Assert.NotNull(received);
        var sent = BackgroundJobSpec.Parse(received!.Args!["spec"]);
        Assert.Equal(job.Id, sent.JobId);
        Assert.Equal("add a Sum method", sent.Task);
        var f = await finished.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.NoChanges, f.Status);
        var done = JobStore.Load(job.Id);
        Assert.Equal(JobHost.Tray, done.Host);
        Assert.StartsWith("tray ", File.ReadAllText(Path.Combine(JobStore.DirOf(job.Id), BackgroundJobs.ClaimFile)));
        var progress = File.ReadAllText(Path.Combine(JobStore.DirOf(job.Id), BackgroundJobs.ProgressFile));
        Assert.Contains("agent: editing Calc.cs", progress);
        Assert.Contains("finished: no_changes", progress);
    }

    [Fact]
    public async Task Launch_OldTrayWithoutCommand_FallsBackToInProcess()
    {
        using var env = NewEnv();
        using var scope = new EngineScope(FakeEngine);
        var (job, spec) = NewAgentJob(env);
        using var instance = new Mutex(false, IpcNames.MutexName);
        // Трей прошлой версии: команды job-start не знает.
        using var tray = new IpcServer(req => Task.FromResult(new IpcResponse(false, "Неизвестная команда: " + req.Command)));
        tray.Start();

        var text = await BackgroundJobs.LaunchAsync(env.Context(), job, spec);

        Assert.Contains("runs inside this IDE session", text);
        var done = await WaitFinishedAsync(job.Id);
        Assert.Equal(JobHost.Mcp, done.Host);
    }

    [Fact]
    public async Task Launch_TrayAlreadyClaimed_DoesNotRunTwice()
    {
        using var env = NewEnv();
        var calls = 0;
        using var scope = new EngineScope((c, j, s) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("x");
        });
        var (job, spec) = NewAgentJob(env);
        // Ответ трея потерялся, но задачу он уже взял — MCP не запускает её у себя.
        Assert.True(BackgroundJobs.TryClaim(job.Id, JobHost.Tray));

        var text = await BackgroundJobs.LaunchAsync(env.Context(), job, spec);

        Assert.Contains("in the Offload tray app", text);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        Assert.False(BackgroundJobs.IsRunning(job.Id));
    }

    public static TheoryData<string> BadSpecs => new()
    {
        "relative-root", "missing-root", "unc-root", "foreign-tool", "root-mismatch", "verify-not-allowlisted",
        "allowed-escape", "context-escape", "future-version", "bad-id", "no-environment",
    };

    [Theory]
    [MemberData(nameof(BadSpecs))]
    public void Host_RejectsTamperedSpec(string kind)
    {
        RequireGit();
        using var env = NewEnv();
        using var scope = new EngineScope(FakeEngine);
        var other = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pc-other-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var (job, spec) = NewAgentJob(env);
            spec = kind switch
            {
                "relative-root" => spec with { Roots = ["src"] },
                "missing-root" => spec with { Roots = [Path.Combine(env.Workspace, "nope")] },
                "unc-root" => spec with { Roots = [@"\\server\share\proj"] },
                "foreign-tool" => spec with { Tool = McpToolNames.WriteFile },
                "root-mismatch" => spec with { Roots = [other] },
                "verify-not-allowlisted" => spec with { VerifyCommand = "powershell -c Remove-Item C:\\ -Recurse" },
                "allowed-escape" => spec with { AllowedPaths = ["../outside"] },
                "context-escape" => spec with { ContextPaths = ["C:\\Windows\\win.ini"] },
                "future-version" => spec with { Version = BackgroundJobSpec.CurrentVersion + 1 },
                "bad-id" => spec with { JobId = "..\\..\\evil" },
                // Описание от старого MCP (без снимка окружения IDE): трей отказывает, MCP выполнит задачу сам.
                "no-environment" => spec with { Environment = null },
                _ => spec,
            };

            Assert.Throws<ToolException>(() => BackgroundJobs.Host(spec.ToJson(), JobHost.Tray, null));
            var resp = BackgroundJobHosting.Start(new IpcRequest(IpcCommands.JobStart, new() { ["spec"] = spec.ToJson() }), null);
            Assert.False(resp.Ok, "подделанное описание не должно приниматься исполнителем");
            Assert.False(BackgroundJobs.IsRunning(job.Id));
            Assert.Null(JobStore.Load(job.Id).Host);
        }
        finally
        {
            try { Directory.Delete(other, true); } catch { }
        }
    }

    [Fact]
    public void Spec_CapturesIdeEnvironment_RoundTrip()
    {
        using var env = NewEnv();
        var (_, spec) = NewAgentJob(env);
        Assert.NotNull(spec.Environment);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), spec.Environment!["PATH"]);
        Assert.DoesNotContain(spec.Environment.Keys, CallerEnvironment.LooksSecret);

        var custom = spec with { Environment = new() { ["PATH"] = @"C:\ide\node;C:\Windows\system32", ["VIRTUAL_ENV"] = @"C:\p\.venv" } };
        var back = BackgroundJobSpec.Parse(custom.ToJson());
        Assert.Equal(@"C:\ide\node;C:\Windows\system32", back.Environment!["PATH"]);
        Assert.Equal(@"C:\p\.venv", back.Environment["VIRTUAL_ENV"]);
        // В файле задачи (spec.json) снимок тоже сохраняется — для повтора по описанию.
        BackgroundJobSpec.Save(custom);
        Assert.Equal(@"C:\p\.venv", BackgroundJobSpec.TryLoad(spec.JobId)!.Environment!["VIRTUAL_ENV"]);
    }

    [Fact]
    public async Task Host_ChildProcessesGetIdeEnvironment_ForcedVariablesWin()
    {
        RequireGit();
        using var env = NewEnv();
        var marker = @"C:\ide-env-" + Guid.NewGuid().ToString("N")[..8];
        var sys = Environment.SystemDirectory;
        string? seen = null;
        using var scope = new EngineScope(async (ctx, job, spec) =>
        {
            var cmd = Path.Combine(sys, "cmd.exe");
            var plain = await ChildProcess.RunRawAsync(cmd, "/d /s /c \"echo %PATH%^|%JAVA_HOME%^|%npm_config__authToken%\"", new ChildProcess.Options(), ctx.Ct);
            var forced = await ChildProcess.RunRawAsync(cmd, "/d /s /c \"echo %JAVA_HOME%\"",
                new ChildProcess.Options { Environment = new Dictionary<string, string?> { ["JAVA_HOME"] = "forced" } }, ctx.Ct);
            seen = plain.StdOut.Trim() + "\n" + forced.StdOut.Trim();
            job.Status = JobStatus.NoChanges;
            JobStore.Save(job);
            return "ok";
        });
        var (job, spec) = NewAgentJob(env, s => s with
        {
            Environment = new()
            {
                ["PATH"] = marker + ";" + sys,
                ["JAVA_HOME"] = marker,
                // Секрет в подделанном описании исполнитель отбрасывает.
                ["npm_config__authToken"] = "npm_secret",
            },
        });

        BackgroundJobs.Host(spec.ToJson(), JobHost.Tray, null);
        await WaitFinishedAsync(job.Id);

        var lines = seen!.Split('\n');
        Assert.Equal($"{marker};{sys}|{marker}|%npm_config__authToken%", lines[0]);
        Assert.Equal("forced", lines[1]);
        // Снимок действует только внутри задачи: процесс-хозяин и его другие дочерние процессы его не видят.
        Assert.Null(CallerEnvironment.Current);
        var outside = await ChildProcess.RunRawAsync(Path.Combine(sys, "cmd.exe"), "/d /s /c \"echo %PATH%\"", new ChildProcess.Options(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(marker, outside.StdOut);
    }

    [Fact]
    public void Host_RejectsJobAlreadyTakenOrFinished()
    {
        RequireGit();
        using var env = NewEnv();
        using var scope = new EngineScope(FakeEngine);
        var (taken, takenSpec) = NewAgentJob(env);
        JobStore.SetHost(taken, JobHost.Mcp);
        Assert.Throws<ToolException>(() => BackgroundJobs.Host(takenSpec.ToJson(), JobHost.Tray, null));

        var (finished, finishedSpec) = NewAgentJob(env);
        finished.Status = JobStatus.Applied;
        JobStore.Save(finished);
        Assert.Throws<ToolException>(() => BackgroundJobs.Host(finishedSpec.ToJson(), JobHost.Tray, null));

        Assert.Throws<ToolException>(() => BackgroundJobs.Host("{not json", JobHost.Tray, null));
        Assert.Throws<ToolException>(() => BackgroundJobs.Host(new string('x', BackgroundJobSpec.MaxJsonChars + 1), JobHost.Tray, null));
    }

    [Fact]
    public async Task Status_FromAnotherProcess_ReadsJobFolder_AndDetectsHostExit()
    {
        using var env = NewEnv();
        using var scope = new EngineScope(FakeEngine);
        var (job, _) = NewAgentJob(env);
        // «Трей» — другой живой процесс; этот процесс видит задачу только через папку задачи.
        using var host = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            job.Host = JobHost.Tray;
            job.HostPid = host.Id;
            job.HostStartedUtc = host.StartTime.ToUniversalTime();
            JobStore.Save(job);
            BackgroundJobs.AppendProgress(job.Id, "agent: running tests");

            var view = JobStore.Load(job.Id);
            Assert.True(BackgroundJobs.IsActive(view));
            Assert.False(BackgroundJobs.IsRunning(view.Id));
            var text = JobTool.Describe(BackgroundJobs.Reconcile(view));
            Assert.Contains("status running", text);
            Assert.Contains("running in the Offload tray app", text);
            Assert.Contains("agent: running tests", text);

            // Ожидание задачи другого процесса: опрос job.json до смены статуса.
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                var j = JobStore.Load(job.Id);
                j.Status = JobStatus.PendingMerge;
                JobStore.Save(j);
            }, TestContext.Current.CancellationToken);
            Assert.True(await BackgroundJobs.WaitAsync(view, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(JobStatus.PendingMerge, JobStore.Load(job.Id).Status);

            // Хозяин исчез посреди работы → interrupted с причиной.
            var again = JobStore.Load(job.Id);
            again.Status = JobStatus.Running;
            JobStore.Save(again);
            host.Kill();
            await host.WaitForExitAsync(TestContext.Current.CancellationToken);
            var reconciled = BackgroundJobs.Reconcile(JobStore.Load(job.Id));
            Assert.Equal(JobStatus.Interrupted, reconciled.Status);
            Assert.Contains(reconciled.Notes, n => n.Contains("tray app", StringComparison.Ordinal));
            Assert.Contains("action=retry", JobTool.Describe(reconciled));
        }
        finally
        {
            try { host.Kill(); } catch { }
        }
    }

    [Fact]
    public void Recovery_MarksOnlyJobsWhoseHostIsGone()
    {
        using var env = NewEnv();
        JobInfo Make(Action<JobInfo> setup)
        {
            var j = JobStore.Create(McpToolNames.AgentTask, env.Workspace, "t", null);
            setup(j);
            JobStore.Save(j);
            return j;
        }
        // PID этого процесса, но другое время запуска — PID достался другому процессу: хозяин мёртв.
        var reused = Make(j => { j.Host = JobHost.Tray; j.HostStartedUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc); });
        var alive = Make(j => j.Host = JobHost.Mcp);
        var legacyOld = Make(j => { j.HostPid = null; j.HostStartedUtc = null; j.CreatedUtc = DateTime.UtcNow - TimeSpan.FromHours(4); });
        var legacyNew = Make(j => { j.HostPid = null; j.HostStartedUtc = null; });
        var done = Make(j => { j.Status = JobStatus.Applied; j.HostStartedUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc); });

        var marked = BackgroundJobHosting.RecoverInterrupted();

        Assert.Equal(new[] { reused.Id, legacyOld.Id }.Order(), marked.Order());
        Assert.Equal(JobStatus.Interrupted, JobStore.Load(reused.Id).Status);
        Assert.Contains(JobStore.Load(reused.Id).Notes, n => n.StartsWith("interrupted: ", StringComparison.Ordinal));
        Assert.Equal(JobStatus.Running, JobStore.Load(alive.Id).Status);
        Assert.Equal(JobStatus.Running, JobStore.Load(legacyNew.Id).Status);
        Assert.Equal(JobStatus.Applied, JobStore.Load(done.Id).Status);
        Assert.Empty(BackgroundJobHosting.RecoverInterrupted());

        // Откат прерванной задачи без изменений — не ошибка и не требует force.
        Assert.True(JobStore.Revert(JobStore.Load(reused.Id), force: false).Ok);
    }

    [Fact]
    public async Task Cancel_FromAnotherProcess_ThroughCancelFile()
    {
        RequireGit();
        using var env = NewEnv();
        using var scope = new EngineScope(async (ctx, job, spec) =>
        {
            ctx.Progress.Report("agent: thinking");
            await Task.Delay(Timeout.Infinite, ctx.Ct);
            return "unreachable";
        });
        var (job, spec) = NewAgentJob(env);
        var finished = new TaskCompletionSource<HostedJobFinished>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resp = BackgroundJobHosting.Start(new IpcRequest(IpcCommands.JobStart, new() { ["spec"] = spec.ToJson() }), f => finished.TrySetResult(f));
        Assert.True(resp.Ok, resp.Message);

        Assert.True(BackgroundJobs.RequestCancel(JobStore.Load(job.Id)));

        var f = await finished.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.Cancelled, f.Status);
        Assert.Equal("Cancelled.", BackgroundJobs.ReadResult(job.Id));
    }

    [Fact]
    public async Task Retry_InterruptedJob_RunsStoredSpecAsNewJob()
    {
        RequireGit();
        using var llama = new FakeLlamaServer();
        using var env = NewEnv(llama.Port);
        var seen = new TaskCompletionSource<BackgroundJobSpec>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = new EngineScope(async (ctx, job, spec) =>
        {
            seen.TrySetResult(spec);
            return await FakeEngine(ctx, job, spec);
        });
        var (job, _) = NewAgentJob(env, s => s with { VerifyCommand = "dotnet test", AllowedPaths = ["src"] });
        JobStore.MarkInterrupted(job, "test");

        var text = await JobTool.RunAsync(env.Context(), job.Id, "retry", 0, null, false, false, 0);

        Assert.Contains($"Retrying job {job.Id}", text);
        var spec = await seen.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.NotEqual(job.Id, spec.JobId);
        Assert.Equal("add a Sum method", spec.Task);
        Assert.Equal("dotnet test", spec.VerifyCommand);
        Assert.Equal(new[] { "src" }, spec.AllowedPaths);
        Assert.Contains(JobStore.Load(job.Id).Notes, n => n == $"retried as job {spec.JobId}");
        await WaitFinishedAsync(spec.JobId);

        // Завершённую задачу повторять нельзя.
        await Assert.ThrowsAsync<ToolException>(() => JobTool.RunAsync(env.Context(), spec.JobId, "retry", 0, null, false, false, 0));
    }
}
