using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Mcp;

namespace Offload.App.Services;

/// <summary>
/// Фоновые задачи агента в процессе трея: MCP-процессы IDE передают их по IPC (<see cref="IpcCommands.JobStart"/>), и они
/// продолжаются после закрытия IDE. Выполнение, проверка описания и восстановление — в <see cref="BackgroundJobHosting"/>;
/// здесь — связь с треем: уведомление о завершении, восстановление при старте, занятость при выходе.
/// </summary>
/// <param name="notify">Уведомление трея (заголовок, текст, значок); щелчок открывает «Журнал».</param>
internal sealed class BackgroundJobHost(Action<string, string, ToolTipIcon> notify) : IDisposable
{
    /// <summary>Принять задачу из IPC-запроса (описание проверяется заново; отказ — Ok=false, MCP выполнит задачу сам).</summary>
    public IpcResponse Start(IpcRequest request)
    {
        var resp = BackgroundJobHosting.Start(request, OnFinished);
        if (resp.Ok) Log.Info("jobs", $"Фоновая задача {resp.Data?.GetValueOrDefault("job_id")} принята от IDE и выполняется в трее");
        return resp;
    }

    /// <summary>Задач, выполняемых треем сейчас.</summary>
    public static int RunningCount => BackgroundJobHosting.RunningCount;

    /// <summary>Дополнить описание занятости перед выходом (null — ничего не выполняется).</summary>
    public static string? AddBusy(string? busy)
    {
        var n = RunningCount;
        if (n == 0) return busy;
        var jobs = L.F("фоновые задачи агента: {0}", n);
        return busy is null ? jobs : busy + ", " + jobs;
    }

    /// <summary>При старте трея: задачи, прерванные закрытием трея или сбоем, помечаются interrupted (повтор — local_job retry).</summary>
    public void RecoverInterrupted()
    {
        var ids = BackgroundJobHosting.RecoverInterrupted();
        if (ids.Count == 0) return;
        Log.Info("jobs", $"Прерванные фоновые задачи: {string.Join(", ", ids)}");
        notify(L.T("Фоновые задачи прерваны"),
            L.F("Задач, прерванных закрытием Offload или IDE: {0}. Повторить можно из IDE: local_job action=retry.", ids.Count), ToolTipIcon.Warning);
    }

    private void OnFinished(HostedJobFinished f)
    {
        Log.Info("jobs", $"Фоновая задача {f.JobId} ({f.Tool}) завершена: {f.Status} — {f.Result}");
        var (title, icon) = Outcome(f.Status);
        var task = f.Task.ReplaceLineEndings(" ");
        if (task.Length > 120) task = task[..120] + "…";
        notify(title, L.F("{0}. Задача: {1}", StatusText(f.Status), task), icon);
    }

    internal static (string Title, ToolTipIcon Icon) Outcome(string status) => status switch
    {
        "applied" or "committed" or "no_changes" or "pending_merge" => (L.T("Фоновая задача агента завершена"), ToolTipIcon.Info),
        "failed" => (L.T("Фоновая задача агента не выполнена"), ToolTipIcon.Error),
        _ => (L.T("Фоновая задача агента остановлена"), ToolTipIcon.Warning),
    };

    internal static string StatusText(string status) => status switch
    {
        "applied" => L.T("изменения применены к проекту"),
        "committed" => L.T("изменения влиты коммитом"),
        "pending_merge" => L.T("изменения ждут проверки в IDE"),
        "no_changes" => L.T("изменений нет"),
        "conflict" => L.T("конфликт при слиянии"),
        "cancelled" => L.T("отменена"),
        "interrupted" => L.T("прервана"),
        "failed" => L.T("ошибка"),
        _ => status,
    };

    /// <summary>Выход из трея: выполняемые задачи помечаются прерванными (процессы агента завершатся вместе с треем).</summary>
    public void Dispose()
    {
        if (RunningCount > 0) BackgroundJobHosting.MarkRunningInterrupted();
    }
}
