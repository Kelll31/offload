using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Core.Processes;
using Offload.Core.Util;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Фоновые задачи агента (local_agent_task / local_solve background=true). IDE получает job_id сразу, а задача выполняется:
/// в процессе трея (IPC <see cref="IpcCommands.JobStart"/>) — тогда она переживает закрытие IDE; если трей не запущен или
/// не принял задачу (старая версия) — в этом MCP-процессе, как раньше. В обоих случаях работает один и тот же движок
/// (<see cref="Engine"/>), а состояние видно из любого процесса через папку задачи: job.json (статус, хозяин — PID),
/// progress.jsonl (ход работы), result.txt (итог); отмена из другого процесса — файл cancel.request.
/// </summary>
internal static class BackgroundJobs
{
    private sealed record Entry(Task<string> Task, ProgressReporter Progress, CancellationTokenSource Cts, DateTime StartedUtc, string Host);

    private static readonly ConcurrentDictionary<string, Entry> Running = new(StringComparer.Ordinal);

    public const string ResultFile = "result.txt";

    /// <summary>Ход работы: строки JSON {"t": время UTC, "m": сообщение}.</summary>
    public const string ProgressFile = "progress.jsonl";

    /// <summary>Просьба отменить задачу из другого процесса (исполнитель проверяет раз в <see cref="PumpInterval"/>).</summary>
    public const string CancelFile = "cancel.request";

    /// <summary>Кто взял задачу на исполнение: создаётся атомарно (CreateNew) — задачу не выполнят два процесса сразу.</summary>
    public const string ClaimFile = "host.claim";

    private const int MaxProgressLines = 2000;

    /// <summary>Движок задачи по описанию. Тесты подменяют (настоящий требует OpenCode и git).</summary>
    internal static Func<ToolContext, JobInfo, BackgroundJobSpec, Task<string>> Engine { get; set; } = AgentTaskTool.RunSpecAsync;

    /// <summary>Период записи прогресса и проверки просьбы об отмене.</summary>
    internal static TimeSpan PumpInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Период опроса job.json при ожидании задачи другого процесса.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Сколько ждать ответа трея на job-start (проверка описания и запись job.json — быстрые).</summary>
    internal static TimeSpan TrayStartTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Задач, выполняемых этим процессом.</summary>
    public static int RunningCount => Running.Count;

    // ───────────────────────── запуск ─────────────────────────

    /// <summary>
    /// Запустить фоновую задачу (описание уже проверено и сохранено): в трее, если он запущен и принял её, иначе здесь.
    /// Возвращает ответ инструмента.
    /// </summary>
    public static async Task<string> LaunchAsync(ToolContext ctx, JobInfo job, BackgroundJobSpec spec)
    {
        var inTray = await TryStartInTrayAsync(spec, ctx.Ct).ConfigureAwait(false);
        // Ответ трея мог потеряться (таймаут), а задачу он уже взял: тогда файл-заявка существует и здесь не запускаем.
        if (inTray || !TryClaim(job.Id, JobHost.Mcp))
        {
            return $"job_id: {job.Id} · status: running in the background in the Offload tray app (sandbox branch {GitSandbox.BranchPrefix}{job.Id})\n" +
                   "The local agent works in an isolated git worktree; the job keeps running even if this IDE session ends.\n" +
                   $"Check: local_job action=status job_id={job.Id} wait_seconds=120 · cancel: local_job action=cancel job_id={job.Id}";
        }

        JobStore.SetHost(job, JobHost.Mcp);
        var progress = new ProgressReporter(null, null);
        var cts = new CancellationTokenSource();
        // Здесь окружение процесса — уже окружение IDE: снимок не нужен.
        Start(job, spec, ctx.ForBackground(progress, cts.Token), cts, JobHost.Mcp, null, null);
        return $"job_id: {job.Id} · status: running in the background (sandbox branch {GitSandbox.BranchPrefix}{job.Id})\n" +
               "The local agent works in an isolated git worktree; you can keep working meanwhile. " +
               "It runs inside this IDE session (the Offload tray app is not running or did not accept it), so it stops if the session ends " +
               "(then: local_job action=retry).\n" +
               $"Check: local_job action=status job_id={job.Id} wait_seconds=120 · cancel: local_job action=cancel job_id={job.Id}";
    }

    /// <summary>Передать задачу трею. false — трея нет, он старый (не знает команды) или отказал: выполнять здесь.</summary>
    internal static async Task<bool> TryStartInTrayAsync(BackgroundJobSpec spec, CancellationToken ct)
    {
        if (!IpcClient.IsTrayRunning()) return false;
        var request = new IpcRequest(IpcCommands.JobStart, new Dictionary<string, string> { ["spec"] = spec.ToJson() });
        var resp = await IpcClient.SendAsync(request, TrayStartTimeout, ct).ConfigureAwait(false);
        if (resp is { Ok: true })
        {
            Log.Info("mcp", $"Фоновая задача {spec.JobId} передана трею");
            return true;
        }
        Log.Info("mcp", $"Трей не принял задачу {spec.JobId} ({resp?.Message ?? "нет ответа"}) — выполняется в процессе MCP");
        return false;
    }

    /// <summary>
    /// Выполнить задачу по описанию в этом процессе как её хозяин (трей). Описание проверяется заново; ToolException — отказ
    /// (в том числе описание без снимка окружения IDE — от старого MCP: он тогда выполнит задачу сам). Дочерние процессы задачи
    /// (проверочная команда, OpenCode, git) запускаются с окружением IDE из снимка поверх окружения трея.
    /// onFinished вызывается после завершения (из фонового потока). Возвращает id задачи.
    /// </summary>
    public static string Host(string? specJson, string host, Action<JobInfo, string>? onFinished)
    {
        var spec = BackgroundJobSpec.Parse(specJson);
        var cfg = ConfigStore.Current;
        var (job, roots, _) = BackgroundJobSpec.ValidateForHost(spec, cfg);
        // Снимок — данные из описания: у исполнителя очищается заново (белый список, без секретов, лимиты).
        var environment = CallerEnvironment.Sanitize(spec.Environment);
        if (Running.ContainsKey(job.Id) || !TryClaim(job.Id, host)) throw new ToolException($"Job {job.Id} is already taken by another process.");
        JobStore.SetHost(job, host);
        var progress = new ProgressReporter(null, null);
        var cts = new CancellationTokenSource();
        var ctx = new ToolContext
        {
            Tool = spec.Tool,
            Cfg = cfg,
            State = new SessionState(),
            Progress = progress,
            Roots = roots,
            ToolUseId = spec.ToolUseId,
            Ct = cts.Token,
        };
        Start(job, spec, ctx, cts, host, environment, onFinished);
        return job.Id;
    }

    /// <param name="environment">Окружение IDE для дочерних процессов задачи (<see cref="CallerEnvironment"/>); null — окружение процесса.</param>
    private static void Start(JobInfo job, BackgroundJobSpec spec, ToolContext ctx, CancellationTokenSource cts, string host,
        IReadOnlyDictionary<string, string>? environment, Action<JobInfo, string>? onFinished)
    {
        var jobId = job.Id;
        AppendProgress(jobId, $"started ({host}, pid {Environment.ProcessId})");
        Log.Info("mcp", $"Фоновая задача {jobId} ({spec.Tool}) запущена ({host}, pid {Environment.ProcessId})");
        var task = Task.Run(async () =>
        {
            // Действует только в потоке выполнения этой задачи (AsyncLocal): другие задачи трея его не видят.
            using var envScope = CallerEnvironment.Use(environment);
            using var pumpStop = new CancellationTokenSource();
            var pump = PumpAsync(jobId, ctx.Progress, cts, pumpStop.Token);
            string text;
            var ok = false;
            try
            {
                // Политика файлов сборки — из задачи (allow_build_files при создании), и в трее, и здесь.
                text = await PathGuard.WithBuildFilePolicy(job.AllowBuildFiles, c => Engine(c, job, spec))(ctx).ConfigureAwait(false);
                ok = true;
            }
            catch (OperationCanceledException)
            {
                text = "Cancelled.";
            }
            catch (Exception ex) when (ex is ToolException or ContextExceededException)
            {
                text = "FAILED: " + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("mcp", $"Фоновая задача {jobId} упала", ex);
                text = $"FAILED: Offload internal error: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                await pumpStop.CancelAsync().ConfigureAwait(false);
                try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            if (ctx.ModelIfUsed is not null && ctx.Stats.ModelCalls > 0) ToolRunner.RecordUsage(ctx, ok, text);
            SaveResult(jobId, text);
            var final = CloseIfStillRunning(jobId, ok, text);
            AppendProgress(jobId, $"finished: {final?.Status ?? "?"}");
            if (final is not null && onFinished is not null)
            {
                try { onFinished(final, text); }
                catch (Exception ex) { Log.Warn("mcp", $"Обработчик завершения задачи {jobId}: {ex.Message}"); }
            }
            return text;
        });
        Running[jobId] = new Entry(task, ctx.Progress, cts, DateTime.UtcNow, host);
        _ = task.ContinueWith(_ =>
        {
            Running.TryRemove(jobId, out var e);
            e?.Cts.Dispose();
            TryDelete(Path.Combine(JobStore.DirOf(jobId), CancelFile));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Движок обычно сам ставит итоговый статус; если он упал раньше (например, проверка описания), задача не должна
    /// навсегда остаться «running».
    /// </summary>
    private static JobInfo? CloseIfStillRunning(string jobId, bool ok, string text)
    {
        try
        {
            var job = JobStore.Load(jobId);
            if (job.Status != JobStatus.Running) return job;
            job.Status = ok ? JobStatus.NoChanges : text.StartsWith("Cancelled", StringComparison.Ordinal) ? JobStatus.Cancelled : JobStatus.Failed;
            job.FinishedUtc = DateTime.UtcNow;
            if (!ok) job.Notes.Add(TextUtil.Short(text, 400));
            JobStore.Save(job);
            return job;
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("mcp", $"Итог фоновой задачи {jobId} не записан: {ex.Message}");
            return null;
        }
    }

    /// <summary>Каждые <see cref="PumpInterval"/>: новое сообщение о ходе работы → progress.jsonl; есть cancel.request → отмена.</summary>
    private static async Task PumpAsync(string jobId, ProgressReporter progress, CancellationTokenSource jobCts, CancellationToken stop)
    {
        string? last = null;
        var lines = 0;
        var cancelFile = Path.Combine(JobStore.DirOf(jobId), CancelFile);
        try
        {
            while (true)
            {
                await Task.Delay(PumpInterval, stop).ConfigureAwait(false);
                Flush();
                if (File.Exists(cancelFile) && !jobCts.IsCancellationRequested)
                {
                    Log.Info("mcp", $"Фоновая задача {jobId}: отмена по запросу из другого процесса");
                    try { await jobCts.CancelAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Flush();
        }

        void Flush()
        {
            var msg = progress.LastMessage;
            if (msg is null || msg == last || lines >= MaxProgressLines) return;
            AppendProgress(jobId, msg);
            last = msg;
            lines++;
        }
    }

    /// <summary>Взять задачу на исполнение (атомарно). false — её уже взял другой процесс.</summary>
    internal static bool TryClaim(string jobId, string host)
    {
        var file = Path.Combine(JobStore.DirOf(jobId), ClaimFile);
        try
        {
            using var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes($"{host} {Environment.ProcessId}");
            fs.Write(bytes);
            return true;
        }
        catch (IOException) when (File.Exists(file))
        {
            return false;
        }
    }

    // ───────────────────────── состояние из любого процесса ─────────────────────────

    /// <summary>Задача выполняется этим процессом.</summary>
    public static bool IsRunning(string jobId) => Running.ContainsKey(jobId);

    /// <summary>Задача выполняется — этим или другим живым процессом (трей, другая сессия IDE).</summary>
    public static bool IsActive(JobInfo job) => IsRunning(job.Id) || job.Status == JobStatus.Running && JobStore.IsHostAlive(job);

    /// <summary>
    /// Задача числится выполняемой, но её процесс-хозяин исчез (закрыли IDE или трей, сбой) — пометить прерванной.
    /// Возвращает ту же задачу (с обновлённым статусом).
    /// </summary>
    public static JobInfo Reconcile(JobInfo job)
    {
        if (job.Status != JobStatus.Running || IsRunning(job.Id) || JobStore.IsHostAlive(job)) return job;
        JobStore.MarkInterrupted(job, InterruptReason(job));
        return job;
    }

    internal static string InterruptReason(JobInfo job) => job switch
    {
        { HostPid: null } => "it was left running by an older Offload version and its process is gone",
        { Host: JobHost.Tray } => $"the Offload tray app (pid {job.HostPid}) stopped while the job was running",
        { Host: JobHost.Mcp } => $"the IDE session that ran it (MCP process pid {job.HostPid}) ended",
        _ => $"the process that ran it (pid {job.HostPid}) ended",
    };

    /// <summary>При старте трея: задачи в статусе running без живого хозяина → interrupted. Возвращает помеченные.</summary>
    public static List<JobInfo> RecoverOrphans()
    {
        var marked = new List<JobInfo>();
        foreach (var job in JobStore.List(200, j => j.Status == JobStatus.Running))
        {
            try
            {
                if (Reconcile(job).Status == JobStatus.Interrupted) marked.Add(job);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
            {
                Log.Debug("mcp", $"Задача {job.Id} не восстановлена: {ex.Message}");
            }
        }
        return marked;
    }

    /// <summary>Выход из трея: выполняемые им задачи помечаются прерванными (дочерние процессы агента завершатся вместе с ним).</summary>
    public static void MarkAllInterrupted(string reason)
    {
        foreach (var id in Running.Keys)
        {
            try
            {
                var job = JobStore.Load(id);
                if (job.Status == JobStatus.Running) JobStore.MarkInterrupted(job, reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
            {
                Log.Debug("mcp", $"Задача {id} не помечена прерванной: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Ход выполняемой задачи: последнее сообщение, время с начала и хозяин. null — задача не выполняется
    /// (ни здесь, ни в другом живом процессе как фоновая).
    /// </summary>
    public static (string? LastMessage, TimeSpan Elapsed, string Host)? Progress(JobInfo job)
    {
        if (Running.TryGetValue(job.Id, out var e)) return (e.Progress.LastMessage, DateTime.UtcNow - e.StartedUtc, e.Host);
        if (job.Status != JobStatus.Running || job.Host is null || !JobStore.IsHostAlive(job)) return null;
        return (ReadLastProgress(job.Id), DateTime.UtcNow - job.CreatedUtc, job.Host);
    }

    /// <summary>Дождаться завершения не дольше wait. true — задача завершилась (или не выполняется).</summary>
    public static async Task<bool> WaitAsync(JobInfo job, TimeSpan wait, CancellationToken ct)
    {
        if (Running.TryGetValue(job.Id, out var e))
        {
            if (wait <= TimeSpan.Zero) return e.Task.IsCompleted;
            var done = await Task.WhenAny(e.Task, Task.Delay(wait, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return done == e.Task;
        }
        // Задача другого процесса (трей, другая IDE): опрос job.json.
        var sw = Stopwatch.StartNew();
        while (true)
        {
            JobInfo current;
            try { current = JobStore.Load(job.Id); }
            catch (ToolException) { return true; }
            if (current.Status != JobStatus.Running || !JobStore.IsHostAlive(current)) return true;
            var left = wait - sw.Elapsed;
            if (left <= TimeSpan.Zero) return false;
            await Task.Delay(left < PollInterval ? left : PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Отменить задачу этого процесса.</summary>
    public static bool Cancel(string jobId)
    {
        if (!Running.TryGetValue(jobId, out var e)) return false;
        try { e.Cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>Попросить отменить фоновую задачу другого процесса (файл cancel.request). false — она не выполняется.</summary>
    public static bool RequestCancel(JobInfo job)
    {
        if (job.Host is null || !IsActive(job)) return false;
        try
        {
            File.WriteAllText(Path.Combine(JobStore.DirOf(job.Id), CancelFile), DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolException($"Could not request cancellation of job {job.Id}: {ex.Message}");
        }
    }

    // ───────────────────────── файлы задачи ─────────────────────────

    private sealed record ProgressLine(DateTime T, string M);

    internal static void AppendProgress(string jobId, string message)
    {
        try
        {
            var line = JsonSerializer.Serialize(new ProgressLine(DateTime.UtcNow, TextUtil.Short(message, 300)), Json.Compact);
            File.AppendAllText(Path.Combine(JobStore.DirOf(jobId), ProgressFile), line + "\n", FileUtil.Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("mcp", $"Прогресс задачи {jobId} не записан: {ex.Message}");
        }
    }

    /// <summary>Последнее сообщение из progress.jsonl (читается хвост файла) или null.</summary>
    internal static string? ReadLastProgress(string jobId)
    {
        try
        {
            var file = Path.Combine(JobStore.DirOf(jobId), ProgressFile);
            if (!File.Exists(file)) return null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int Tail = 8192;
            if (fs.Length > Tail) fs.Seek(-Tail, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                try
                {
                    if (JsonSerializer.Deserialize<ProgressLine>(lines[i], Json.Compact) is { M: { } m }) return m;
                }
                catch (JsonException)
                {
                    // Обрезанная первая строка хвоста или строка, дописываемая прямо сейчас.
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ReadResult(string jobId)
    {
        try
        {
            var file = Path.Combine(JobStore.DirOf(jobId), ResultFile);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveResult(string jobId, string text)
    {
        try
        {
            FileUtil.WriteAllTextAtomic(Path.Combine(JobStore.DirOf(jobId), ResultFile), text);
        }
        catch (Exception ex)
        {
            Log.Warn("mcp", $"Результат фоновой задачи {jobId} не сохранён: {ex.Message}");
        }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
