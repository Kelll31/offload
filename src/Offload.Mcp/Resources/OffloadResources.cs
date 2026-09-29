using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Resources;

/// <summary>Адреса ресурсов Offload (offload://…) и проверка идентификаторов в них.</summary>
internal static partial class ResourceUris
{
    public const string RunTemplate = "offload://runs/{id}";
    public const string JobDiffTemplate = "offload://jobs/{id}/diff";
    public const string ProjectMap = "offload://project/map";
    public const string Memory = "offload://memory";

    /// <summary>Лог прогона: имя файла .offload/runs/&lt;id&gt;.log без расширения.</summary>
    public static string Run(string logPath) => "offload://runs/" + Path.GetFileNameWithoutExtension(logPath);

    public static string JobDiff(string jobId) => $"offload://jobs/{jobId}/diff";

    /// <summary>Diff кандидата гонки агентов (race &gt; 1): сохраняется в папке задачи до удаления его песочницы.</summary>
    public const string RaceDiffTemplate = "offload://jobs/{id}/race/{candidate}";

    public static string RaceDiff(string jobId, char candidate) => $"offload://jobs/{jobId}/race/{candidate}";

    /// <summary>Идентификатор прогона — как его создаёт VerifyTool.RunLoggedAsync: «20260924-101500-123-dotnet-test».</summary>
    public static bool IsValidRunId(string? id) => id is not null && RunId().IsMatch(id);

    [GeneratedRegex(@"^\d{8}-\d{6}-\d{3}-[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex RunId();
}

/// <summary>
/// MCP-ресурсы: полный лог прогона проверки, diff задачи, обзор карты проекта, память проекта. Ответы инструментов ссылаются
/// на них (resource_link), чтобы не обрезать длинный текст по MaxResponseChars. Чтение — только через PathGuard/JobStore
/// (идентификаторы проверяются по шаблону, файлы — только из рабочей области), текст маскируется SecretRedactor
/// (если включён Mcp.RedactSecrets). Описания — английские (их читает модель).
/// </summary>
[McpServerResourceType]
public sealed class OffloadResources(SessionState state)
{
    /// <summary>Потолок текста ресурса (diff задачи).</summary>
    internal const int MaxResourceChars = 4 * 1024 * 1024;

    /// <summary>
    /// Потолок лога прогона: клиенты обрезают ответ MCP (Claude Code — около 25 тыс. токенов), поэтому у больших логов отдаётся
    /// конец (итоги и ошибки почти всегда там) с пометкой и полным путём к файлу.
    /// </summary>
    internal const int MaxRunLogBytes = 200 * 1024;

    [McpServerResource(UriTemplate = ResourceUris.RunTemplate, Name = "run_log", Title = "Offload: full run log", MimeType = "text/plain")]
    [Description("Full output of a check run by local_verify / local_diagnostics / local_impact (stored in <project>/.offload/runs/). " +
                 "The id is in the tool result (log_uri). Logs over 200 KB are returned from the end, marked TRUNCATED with the full file path.")]
    public async Task<TextResourceContents> RunLog(string id, RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(context, cancellationToken).ConfigureAwait(false);
        if (!ResourceUris.IsValidRunId(id)) throw NotFound($"Invalid run id '{TextUtil.Short(id, 80)}'. It looks like 20260924-101500-123-dotnet-test (log_uri in a local_verify result).");
        foreach (var root in ctx.Roots)
        {
            string full;
            try { full = ctx.ResolveRead(Path.Combine(root, ".offload", "runs", id + ".log")); }
            catch (ToolException ex) { throw NotFound(ex.Message); }
            if (!File.Exists(full)) continue;
            var text = ReadTail(full, MaxRunLogBytes, WslPaths.ToClientPath(full));
            return Text(context, ctx.Cfg.Mcp.RedactSecrets ? SecretRedactor.Redact(text, full) : text);
        }
        throw NotFound($"Run log '{id}' not found in this workspace (Offload keeps the last {VerifyTool.KeepLogs} logs).");
    }

    [McpServerResource(UriTemplate = ResourceUris.JobDiffTemplate, Name = "job_diff", Title = "Offload: job diff", MimeType = "text/x-diff")]
    [Description("Unified diff of an Offload job (local_write_file / local_edit_files / local_agent_task / local_solve / local_apply_patch / " +
                 "local_refactor): for an unmerged agent sandbox - its branch vs the snapshot, otherwise the snapshot vs the job's result.")]
    public async Task<TextResourceContents> JobDiff(string id, RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(context, cancellationToken).ConfigureAwait(false);
        JobInfo job;
        try { job = JobStore.Load(id); }
        catch (ToolException ex) { throw NotFound(ex.Message); }
        // Только задачи внутри корней сессии (не задачи родительской папки: их diff затрагивает файлы вне корней).
        if (!PathGuard.JobVisible(job.Root, ctx.Roots))
            throw NotFound($"Job {job.Id} belongs to another workspace; read it from a session opened in that project.");
        string diff;
        try
        {
            diff = job.Sandbox is { } open && SandboxState.IsOpen(open.State) && job.Status != JobStatus.Applied
                ? await GitSandbox.DiffTextAsync(open, MaxResourceChars, null, cancellationToken).ConfigureAwait(false)
                : JobStore.Diff(job, 1_000_000, MaxResourceChars, null);
        }
        catch (ToolException ex)
        {
            throw new McpProtocolException(ex.Message, McpErrorCode.InternalError);
        }
        var text = $"job {job.Id} · {job.Tool} · status {job.Status}\n" + diff;
        return Text(context, ctx.Cfg.Mcp.RedactSecrets ? SecretRedactor.Redact(text) : text, "text/x-diff");
    }

    [McpServerResource(UriTemplate = ResourceUris.RaceDiffTemplate, Name = "race_candidate_diff", Title = "Offload: agent race candidate diff", MimeType = "text/x-diff")]
    [Description("Diff of one candidate (a-d) of an agent race (local_agent_task / local_solve with race>1), kept after its sandbox was removed. " +
                 "The race table in the tool result links every candidate's diff.")]
    public async Task<TextResourceContents> RaceDiff(string id, string candidate, RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(context, cancellationToken).ConfigureAwait(false);
        var c = (candidate ?? "").Trim().ToLowerInvariant();
        if (c.Length != 1 || c[0] is < 'a' or > (char)('a' + AgentRace.MaxCandidates - 1))
            throw NotFound($"Invalid race candidate '{TextUtil.Short(candidate, 20)}': use a letter a-{(char)('a' + AgentRace.MaxCandidates - 1)}.");
        JobInfo job;
        try { job = JobStore.Load(id); }
        catch (ToolException ex) { throw NotFound(ex.Message); }
        if (!PathGuard.JobVisible(job.Root, ctx.Roots))
            throw NotFound($"Job {job.Id} belongs to another workspace; read it from a session opened in that project.");
        var file = AgentRace.DiffFile(job.Id, c[0]);
        if (!File.Exists(file)) throw NotFound($"Job {job.Id} has no saved diff for race candidate {c} (it made no changes or the job is not a race).");
        var text = $"job {job.Id} · {job.Tool} · status {job.Status} · race " + ReadTail(file, MaxResourceChars);
        return Text(context, ctx.Cfg.Mcp.RedactSecrets ? SecretRedactor.Redact(text) : text, "text/x-diff");
    }

    [McpServerResource(UriTemplate = ResourceUris.ProjectMap, Name = "project_map", Title = "Offload: project map", MimeType = "text/plain")]
    [Description("Project overview from local_project_map (section=overview): languages, projects/manifests, entry points, build/test commands.")]
    public async Task<TextResourceContents> ProjectMap(RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(context, cancellationToken).ConfigureAwait(false);
        string text;
        try { text = await ProjectMapTool.RunAsync(ctx, "overview", 80).ConfigureAwait(false); }
        catch (ToolException ex) { throw new McpProtocolException(ex.Message, McpErrorCode.InternalError); }
        return Text(context, ctx.Cfg.Mcp.RedactSecrets ? SecretRedactor.Redact(text) : text);
    }

    [McpServerResource(UriTemplate = ResourceUris.Memory, Name = "project_memory", Title = "Offload: project memory", MimeType = "text/plain")]
    [Description("Project memory stored with local_memory (facts, decisions, conventions), newest first.")]
    public async Task<TextResourceContents> Memory(RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(context, cancellationToken).ConfigureAwait(false);
        string text;
        try { text = MemoryTool.Run(ctx, "list", null, null, null, null, null, 100); }
        catch (ToolException ex) { throw new McpProtocolException(ex.Message, McpErrorCode.InternalError); }
        return Text(context, ctx.Cfg.Mcp.RedactSecrets ? SecretRedactor.Redact(text) : text);
    }

    /// <summary>Контекст как у инструмента: свежий конфиг и корни рабочей области (без прогресса и подвала).</summary>
    private async Task<ToolContext> ContextAsync(RequestContext<ReadResourceRequestParams> rc, CancellationToken ct)
    {
        var cfg = ConfigStore.Reload();
        var roots = await Workspace.GetRootsAsync(rc.Server, state, ct).ConfigureAwait(false);
        Log.Info("mcp", $"ресурс {TextUtil.Short(rc.Params?.Uri, 80)} (клиент {rc.Server?.ClientInfo?.Name ?? "?"})");
        return new ToolContext
        {
            Tool = "resources/read",
            Cfg = cfg,
            State = state,
            Progress = new ProgressReporter(null, null),
            Roots = roots,
            Server = rc.Server,
            Ct = ct,
        };
    }

    private static TextResourceContents Text(RequestContext<ReadResourceRequestParams> rc, string text, string mimeType = "text/plain") =>
        new() { Uri = rc.Params?.Uri ?? "", MimeType = mimeType, Text = text };

    private static McpProtocolException NotFound(string message) => new(message, McpErrorCode.ResourceNotFound);

    /// <summary>
    /// Текст файла (UTF-8, общий доступ на чтение); у больших — последние maxBytes байт с пометкой в начале: сколько пропущено
    /// и где лежит полный файл (<paramref name="displayPath"/>).
    /// </summary>
    internal static string ReadTail(string path, int maxBytes, string? displayPath = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipped = Math.Max(0, fs.Length - maxBytes);
        if (skipped > 0) fs.Seek(skipped, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (skipped > 0) reader.ReadLine(); // неполная первая строка
        var text = reader.ReadToEnd();
        return skipped > 0
            ? $"…[TRUNCATED: the first {skipped} of {skipped + maxBytes} bytes are omitted, only the tail is shown; full log: {displayPath ?? path}]\n" + text
            : text;
    }
}
