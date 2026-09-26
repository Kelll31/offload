using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Processes;
using Offload.Core.Util;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Описание задачи агента (local_agent_task / local_solve / local_refactor), достаточное, чтобы выполнить её в другом процессе
/// (в трее) или повторить после сбоя (local_job action=retry). Хранится в jobs\&lt;id&gt;\spec.json и передаётся трею по IPC.
/// Любой процесс-исполнитель проверяет описание заново (<see cref="ValidateForHost"/>): это данные, а не команда.
/// </summary>
internal sealed record BackgroundJobSpec
{
    /// <summary>
    /// Версия формата. Трей отклоняет незнакомую (MCP тогда выполняет задачу сам). 2 — появился снимок окружения IDE
    /// (<see cref="Environment"/>): старый трей не знает о нём и запустил бы задачу со своим окружением, поэтому версия поднята —
    /// такой трей отклонит описание, и задача выполнится в MCP-процессе с окружением IDE.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>Потолок размера JSON описания (задача ≤ 16 000 символов + списки путей).</summary>
    public const int MaxJsonChars = 256 * 1024;

    public const string FileName = "spec.json";

    /// <summary>Инструменты, задачи которых можно выполнять по описанию: только движок задач агента.</summary>
    internal static readonly IReadOnlySet<string> HostableTools =
        new HashSet<string>(StringComparer.Ordinal) { McpToolNames.AgentTask, McpToolNames.Solve, McpToolNames.Refactor };

    public int Version { get; init; } = CurrentVersion;
    public string JobId { get; init; } = "";
    public string Tool { get; init; } = McpToolNames.AgentTask;

    /// <summary>Корни рабочей области вызвавшей сессии (абсолютные канонические пути).</summary>
    public string[] Roots { get; init; } = [];

    public string Task { get; init; } = "";
    public string? VerifyCommand { get; init; }
    public string[]? ContextPaths { get; init; }
    public string Merge { get; init; } = "apply";
    public int FixAttempts { get; init; }
    public int TimeoutMinutes { get; init; }
    public string[]? AllowedPaths { get; init; }
    public int MaxFiles { get; init; }
    public bool Review { get; init; }
    public string? Preamble { get; init; }
    public string? ToolUseId { get; init; }

    /// <summary>
    /// Снимок окружения сессии IDE (белый список <see cref="CallerEnvironment"/>, без секретов): PATH, venv/conda, nvm, JAVA_HOME,
    /// переменные VsDevCmd и т.п. Исполнитель-трей запускает с ним проверочную команду и OpenCode. null — описание от старого MCP:
    /// трей его отклоняет (задача выполняется в MCP-процессе, где окружение и так верное).
    /// </summary>
    public Dictionary<string, string>? Environment { get; init; }

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public static BackgroundJobSpec From(JobInfo job, AgentTaskRequest r, IReadOnlyList<string> roots) => new()
    {
        JobId = job.Id,
        Tool = r.Tool,
        Roots = [.. roots],
        Task = r.Task,
        VerifyCommand = r.VerifyCommand,
        ContextPaths = r.ContextPaths,
        Merge = r.Merge,
        FixAttempts = r.FixAttempts,
        TimeoutMinutes = r.TimeoutMinutes,
        AllowedPaths = r.AllowedPaths,
        MaxFiles = r.MaxFiles,
        Review = r.Review,
        Preamble = r.Preamble,
        ToolUseId = job.ToolUseId,
        Environment = CallerEnvironment.Capture(),
    };

    public AgentTaskRequest ToRequest(bool background) => new()
    {
        Task = Task,
        VerifyCommand = VerifyCommand,
        ContextPaths = ContextPaths,
        Merge = Merge,
        FixAttempts = FixAttempts,
        TimeoutMinutes = TimeoutMinutes,
        Background = background,
        AllowedPaths = AllowedPaths,
        MaxFiles = MaxFiles,
        Review = Review,
        Tool = Tool,
        Preamble = Preamble,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json.Compact);

    /// <summary>Разобрать описание (из IPC или файла). Ошибка формата — ToolException.</summary>
    public static BackgroundJobSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ToolException("Background job spec is empty.");
        if (json.Length > MaxJsonChars) throw new ToolException("Background job spec is too large.");
        try
        {
            return JsonSerializer.Deserialize<BackgroundJobSpec>(json, Json.Compact) ?? throw new ToolException("Background job spec is empty.");
        }
        catch (JsonException ex)
        {
            throw new ToolException("Background job spec is not valid JSON: " + ex.Message);
        }
    }

    public static void Save(BackgroundJobSpec spec) =>
        FileUtil.WriteAllTextAtomic(Path.Combine(JobStore.DirOf(spec.JobId), FileName), JsonSerializer.Serialize(spec, Json.Options));

    /// <summary>Сохранённое описание задачи или null (задача не агентная или создана до появления описаний).</summary>
    public static BackgroundJobSpec? TryLoad(string jobId)
    {
        var file = Path.Combine(JobStore.DirOf(jobId), FileName);
        if (!File.Exists(file)) return null;
        try
        {
            return Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Проверка описания в процессе-исполнителе (трей): версия, задача существует и ещё никем не взята, инструмент — движок
    /// задач агента, корни — существующие абсолютные локальные папки, корень задачи совпадает с корнем записи, а параметры
    /// проходят те же проверки, что и в MCP (белый список команды проверки, allowed_paths, context_paths, лимиты).
    /// Возвращает задачу, канонические корни и нормализованный запрос.
    /// </summary>
    public static (JobInfo Job, IReadOnlyList<string> Roots, AgentTaskRequest Request) ValidateForHost(BackgroundJobSpec spec, AppConfig cfg)
    {
        if (spec.Version is < 1 or > CurrentVersion) throw new ToolException($"Unsupported background job spec version {spec.Version}.");
        if (!JobStore.IsValidId(spec.JobId)) throw new ToolException("Background job spec has an invalid job id.");
        if (!HostableTools.Contains(spec.Tool)) throw new ToolException($"Tool '{spec.Tool}' cannot run as a hosted background job.");
        if (spec.Environment is null)
            throw new ToolException("Background job spec has no IDE environment snapshot (older Offload MCP server); the job runs in the IDE session instead.");
        var roots = ValidateRoots(spec.Roots);

        var job = JobStore.Load(spec.JobId);
        if (job.Status != JobStatus.Running) throw new ToolException($"Job {job.Id} is not waiting to run (status {job.Status}).");
        if (!string.Equals(job.Tool, spec.Tool, StringComparison.Ordinal)) throw new ToolException($"Job {job.Id} belongs to another tool.");
        if (job.Host is not null) throw new ToolException($"Job {job.Id} is already hosted ({job.Host}).");
        if (job.Sandbox is not null) throw new ToolException($"Job {job.Id} has already started.");
        var writeRoot = Workspace.WriteRoots(roots)[0];
        if (!string.Equals(PathGuard.TrimTrailingSeparator(job.Root), PathGuard.TrimTrailingSeparator(writeRoot), StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"Job {job.Id} root does not match the spec workspace.");

        var (request, _, _) = AgentTaskTool.Prepare(cfg, spec.ToRequest(background: true));
        return (job, roots, request);
    }

    /// <summary>Корни: 1–16 абсолютных путей к существующим локальным папкам (не UNC, не устройства); возвращаются канонические.</summary>
    internal static IReadOnlyList<string> ValidateRoots(string[]? roots)
    {
        if (roots is not { Length: > 0 and <= 16 }) throw new ToolException("Background job spec must list 1-16 workspace roots.");
        var list = new List<string>();
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1024) throw new ToolException("Background job spec has an empty or too long root.");
            if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
                throw new ToolException($"Workspace root '{raw}' must be a local folder (UNC and device paths are refused).");
            if (!Path.IsPathFullyQualified(raw) || raw.Contains(':', StringComparison.Ordinal) && raw.IndexOf(':') != 1)
                throw new ToolException($"Workspace root '{raw}' must be an absolute local path.");
            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ToolException($"Workspace root '{raw}' is not a valid path.");
            }
            if (!Directory.Exists(full)) throw new ToolException($"Workspace root '{raw}' does not exist.");
            var canonical = PathGuard.Canonicalize(full); // Неразрешимая ссылка — ToolException (отказ).
            if (!list.Contains(canonical, StringComparer.OrdinalIgnoreCase)) list.Add(canonical);
        }
        return list;
    }
}
