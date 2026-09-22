using System.Text;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Usage;
using Offload.Llama;
using Offload.Mcp.Infrastructure;
using Offload.OpenCode;

namespace Offload.Mcp.Tools;

/// <summary>local_status: состояние без автозапуска сервера.</summary>
internal static class StatusTool
{
    public static async Task<string> RunAsync(ToolContext ctx)
    {
        var cfg = ctx.Cfg;
        var model = cfg.ActiveModel();
        var client = LlamaClient.FromConfig(cfg);
        var sb = new StringBuilder();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        HealthState health;
        ServerProps? props = null;
        try
        {
            health = await client.GetHealthAsync(cts.Token).ConfigureAwait(false);
            if (health == HealthState.Ready) props = await client.GetPropsAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ctx.Ct.IsCancellationRequested)
        {
            health = HealthState.Down;
        }

        var name = model is null ? props?.ModelAlias ?? "none" : model.DisplayName + (string.IsNullOrWhiteSpace(model.Quant) ? "" : $" ({model.Quant})");
        switch (health)
        {
            case HealthState.Ready:
                var ctxSize = props?.ContextPerSlot ?? 0;
                sb.Append($"local model: READY · {name}");
                if (ctxSize > 0) sb.Append($" · context {ctxSize} tok per request × {props!.TotalSlots} slot(s)");
                if (props?.IsSleeping == true) sb.Append(" · asleep (reloads on the next call, a few seconds)");
                sb.Append('\n');
                break;
            case HealthState.Loading:
                sb.Append($"local model: LOADING · {name} (calls will wait for it)\n");
                break;
            default:
                if (!cfg.SetupCompleted || model is null)
                {
                    sb.Append("local model: NOT SET UP · ").Append(ServerEnsurer.NotSetUpMessage).Append('\n');
                }
                else
                {
                    var tray = IpcClient.IsTrayRunning() ? "the tray app is running" : "the tray app will be launched";
                    sb.Append($"local model: OFFLINE · {name} · it starts automatically on the first real call ({tray}; loading takes up to ~{Math.Clamp(cfg.Mcp.ServerStartTimeoutSeconds, 10, 1800)} s)\n");
                }
                break;
        }

        var records = UsageLog.ReadAll(DateTime.UtcNow.AddDays(-30));
        var speed = ctx.State.LastGenerationTps;
        if (speed is double tps) sb.Append($"speed: {tps:0} tok/s (last call)");
        else
        {
            var recent = records.Where(r => r.Ok && r.CompletionTokens > 50 && r.DurationMs > 0).TakeLast(20).ToList();
            if (recent.Count > 0)
                sb.Append($"speed: ≈{recent.Sum(r => r.CompletionTokens) * 1000.0 / recent.Sum(r => r.DurationMs):0} tok/s (recent calls, incl. reading input)");
            else sb.Append("speed: not measured yet");
        }
        var slots = Math.Max(1, cfg.Server.Parallel);
        sb.Append($" · queue: {GpuQueue.CountBusy(slots)}/{slots} slot(s) busy\n");

        sb.Append("agent mode for local_edit_files (OpenCode): ").Append(OpenCodeState(cfg)).Append('\n');

        var summary = UsageLog.Summarize(UsageLog.ReadAll());
        sb.Append($"cloud tokens avoided: this session ≈{Tokens.Format(ctx.State.SavedTokens)} ({ctx.State.ModelCalls} calls) · " +
                  $"lifetime ≈{Tokens.Format(summary.EstimatedSavedTokens)} ({summary.Calls} calls)\n");
        sb.Append("workspace: ").Append(string.Join("; ", ctx.Roots)).Append('\n');
        sb.Append($"offload {AppInfo.Version}");
        return sb.ToString();
    }

    internal static string OpenCodeState(AppConfig cfg)
    {
        if (!cfg.OpenCode.Enabled) return "disabled in settings (rewrite mode still works for small file sets)";
        return FindOpenCode(cfg) is null ? "not installed (rewrite mode still works for small file sets)" : "available";
    }

    /// <summary>Путь к opencode.exe или null (модуль OpenCode может быть ещё не реализован — это не ошибка).</summary>
    internal static string? FindOpenCode(AppConfig cfg)
    {
        try { return OpenCodeInstaller.FindExecutable(cfg); }
        catch (Exception) { return null; }
    }
}
