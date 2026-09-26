using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp;

/// <summary>Итог фоновой задачи, выполненной в трее (для уведомления).</summary>
/// <param name="Status">Итоговый статус задачи (applied, committed, pending_merge, no_changes, failed, cancelled, conflict…).</param>
/// <param name="Result">Первая строка итога (для журнала).</param>
public sealed record HostedJobFinished(string JobId, string Tool, string Status, string Task, string Result);

/// <summary>
/// Точка входа для трея: выполнение фоновых задач агента, присланных MCP-процессами по IPC (<see cref="IpcCommands.JobStart"/>),
/// и восстановление после сбоя. Трей выполняет только движок задач агента по описанию, проверенному заново
/// (корни, белый список команды проверки, лимиты) — произвольные команды по IPC не принимаются.
/// </summary>
public static class BackgroundJobHosting
{
    /// <summary>Задач, выполняемых этим процессом.</summary>
    public static int RunningCount => BackgroundJobs.RunningCount;

    /// <summary>
    /// Принять задачу из IPC-запроса job-start (Args["spec"]). Отказ (неверное описание, задача уже взята) — Ok=false
    /// с причиной; MCP тогда выполнит задачу сам. onFinished вызывается из фонового потока.
    /// </summary>
    public static IpcResponse Start(IpcRequest request, Action<HostedJobFinished>? onFinished)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var id = BackgroundJobs.Host(request.Args?.GetValueOrDefault("spec"), JobHost.Tray, onFinished is null ? null : (job, text) =>
                onFinished(new HostedJobFinished(job.Id, job.Tool, job.Status, job.Task, FirstLine(text))));
            return new IpcResponse(true, null, new Dictionary<string, string> { ["job_id"] = id, ["pid"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("jobs", $"Фоновая задача из IDE отклонена: {ex.Message}");
            return new IpcResponse(false, ex.Message, new Dictionary<string, string> { ["error"] = "rejected" });
        }
    }

    /// <summary>При старте трея: задачи «running» без живого процесса-хозяина помечаются interrupted. Возвращает их id.</summary>
    public static IReadOnlyList<string> RecoverInterrupted()
    {
        try
        {
            var marked = BackgroundJobs.RecoverOrphans();
            return [.. marked.Select(j => j.Id)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
        {
            Log.Warn("jobs", $"Восстановление фоновых задач: {ex.Message}");
            return [];
        }
    }

    /// <summary>Выход из трея: его фоновые задачи помечаются прерванными (их можно повторить local_job action=retry).</summary>
    public static void MarkRunningInterrupted() =>
        BackgroundJobs.MarkAllInterrupted("the Offload tray app was closed while the job was running");

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
