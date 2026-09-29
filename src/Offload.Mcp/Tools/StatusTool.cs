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

        // Серверы ролей опрашиваются параллельно с основным.
        var rolesTask = RoleStatusAsync(cfg, ctx.Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        HealthState health;
        ServerProps? props = null;
        RemoteHealth? remote = null;
        try
        {
            // Клиентский режим: /health удалённого сервера с ключом (и задержкой ответа), без автозапуска.
            if (client.IsRemote) remote = await RemoteProbe.HealthAsync(client, cts.Token).ConfigureAwait(false);
            health = remote?.State ?? await client.GetHealthAsync(cts.Token).ConfigureAwait(false);
            if (health == HealthState.Ready) props = await client.GetPropsAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ctx.Ct.IsCancellationRequested)
        {
            health = HealthState.Down;
        }

        var remoteHost = client.IsRemote ? RemoteServer.DisplayHost(client.BaseUrl) : null;
        var name = remoteHost is not null ? $"{client.Model} @ {remoteHost}"
            : model is null ? props?.ModelAlias ?? "none" : model.DisplayName + (string.IsNullOrWhiteSpace(model.Quant) ? "" : $" ({model.Quant})");
        var state = health switch
        {
            HealthState.Ready => "ready",
            HealthState.Loading => "loading",
            _ => remoteHost is not null ? "offline" : !cfg.SetupCompleted || model is null ? "not_set_up" : "offline",
        };
        switch (health)
        {
            case HealthState.Ready:
                sb.Append($"local model: READY · {name}");
                if (props is not null) sb.Append(ContextLine(props));
                if (props?.IsSleeping == true) sb.Append(" · asleep (reloads on the next call, a few seconds)");
                sb.Append('\n');
                break;
            case HealthState.Loading:
                sb.Append($"local model: LOADING · {name} (calls will wait for it)\n");
                break;
            default:
                if (remoteHost is not null)
                {
                    sb.Append($"local model: UNREACHABLE · {name} · calls fail until the remote server answers (check the network/VPN and Offload on the host PC)\n");
                }
                else if (!cfg.SetupCompleted || model is null)
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
        if (remoteHost is not null)
            sb.Append(RemoteLine(remoteHost, health, remote?.Latency, remote?.Detail)).Append('\n');

        var roles = await rolesTask.ConfigureAwait(false);
        sb.Append(RolesLine(roles)).Append('\n');

        var records = UsageLog.ReadAll(DateTime.UtcNow.AddDays(-30));
        var speed = ctx.State.LastGenerationTps;
        if (speed is double tps) sb.Append($"speed: {tps:0} tok/s (last call)");
        else
        {
            var recent = records.Where(r => r.Ok && r.CompletionTokens > 50 && r.DurationMs > 0).TakeLast(20).ToList();
            if (recent.Count > 0)
            {
                speed = recent.Sum(r => r.CompletionTokens) * 1000.0 / recent.Sum(r => r.DurationMs);
                sb.Append($"speed: ≈{speed:0} tok/s (recent calls, incl. reading input)");
            }
            else sb.Append("speed: not measured yet");
        }
        var slots = Math.Clamp(cfg.Server.Parallel, 1, 16);
        var busy = GpuQueue.CountBusy(slots);
        var board = GpuQueueBoard.ReadSafe();
        sb.Append(QueueLine(cfg.Server.Parallel, busy, board, DateTime.UtcNow));

        var agentMode = OpenCodeState(cfg);
        sb.Append("agent mode for local_edit_files (OpenCode): ").Append(agentMode).Append('\n');

        var summary = UsageLog.Summarize(UsageLog.ReadAll());
        sb.Append($"cloud tokens avoided: this session ≈{Tokens.Format(ctx.State.SavedTokens)} ({ctx.State.ModelCalls} calls) · " +
                  $"lifetime ≈{Tokens.Format(summary.EstimatedSavedTokens)} ({summary.Calls} calls)\n");
        sb.Append(CacheLine(ctx)).Append('\n');
        sb.Append("workspace: ").Append(string.Join("; ", ctx.Roots)).Append('\n');
        sb.Append($"offload {AppInfo.Version}");
        ctx.Structured = new StatusOutput
        {
            Model = new StatusModel
            {
                State = state,
                Name = name,
                ContextPerRequest = props is { ContextPerSlot: > 0 } ? props.ContextPerSlot : null,
                Slots = props is { TotalSlots: > 0 } ? props.TotalSlots : null,
                Asleep = props?.IsSleeping == true,
                Remote = remoteHost,
                LatencyMs = remote is { State: not HealthState.Down } r ? (int)Math.Round(r.Latency.TotalMilliseconds) : null,
            },
            SpeedTps = speed is double v ? Math.Round(v, 1) : null,
            Queue = new StatusQueue { Busy = busy, Slots = slots, Waiting = board.Count(e => e.State == GpuQueueEntry.Waiting) },
            AgentMode = agentMode.StartsWith("available", StringComparison.Ordinal) ? "available"
                : agentMode.StartsWith("disabled", StringComparison.Ordinal) ? "disabled" : "not_installed",
            SavedTokensSession = ctx.State.SavedTokens,
            CallsSession = ctx.State.ModelCalls,
            SavedTokensLifetime = summary.EstimatedSavedTokens,
            CallsLifetime = summary.Calls,
            Workspace = ctx.Roots,
            Version = AppInfo.Version,
            Roles = roles,
        };
        return sb.ToString();
    }

    /// <summary>
    /// Кэш результатов модели: попадания за сессию (из них по похожему вопросу), не сгенерированные повторно токены,
    /// размер кэша рабочей папки. Выключен в настройках или вместе с маскированием секретов — так и сказано.
    /// </summary>
    internal static string CacheLine(ToolContext ctx)
    {
        var m = ctx.Cfg.Mcp;
        if (!m.WorkCache) return "result cache: off (Offload tray app → Prompt → MCP limits)";
        if (!m.RedactSecrets) return "result cache: off (needs secret redaction enabled)";
        var c = ctx.State.WorkCache;
        var sb = new StringBuilder($"result cache: this session {c.Hits}/{c.Lookups} hits");
        if (c.SimilarHits > 0) sb.Append($" ({c.SimilarHits} by a similar question)");
        if (c.SkippedTokens > 0) sb.Append($", ≈{Tokens.Format(c.SkippedTokens)} local tokens not regenerated");
        if (ctx.Roots.Count > 0 && WorkCacheStore.TryStats(ctx.Roots[0]) is { } s)
            sb.Append($" · this workspace: {s.Entries} results, {s.Bytes / (1024.0 * 1024):0.0}/{Math.Clamp(m.WorkCacheMaxMb, 1, 4096)} MB, {s.Hits} hits total");
        sb.Append($" · kept {Math.Clamp(m.WorkCacheTtlDays, 1, 365)} days; bypass: fresh=true");
        return sb.ToString();
    }

    /// <summary>
    /// Вспомогательные серверы ролей: назначенная модель, порт и состояние (/health, без автозапуска; серверы опрашиваются
    /// параллельно, не дольше 3 секунд). fast, совпадающая с активной моделью, считается не настроенной: короткие задачи
    /// тогда идут на основной сервер.
    /// </summary>
    internal static async Task<IReadOnlyList<StatusRole>> RoleStatusAsync(AppConfig cfg, CancellationToken ct)
    {
        var active = cfg.ActiveModel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        var tasks = ModelRoleConfig.Auxiliary.Select(async role =>
        {
            var model = cfg.RoleModel(role);
            if (model is null || (role == ModelRole.Fast && string.Equals(model.Id, active?.Id, StringComparison.OrdinalIgnoreCase)))
                return new StatusRole { Role = role.Key(), State = "not_configured" };
            HealthState health;
            try
            {
                health = await LlamaClient.ForRole(cfg, role).GetHealthAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                health = HealthState.Down;
            }
            return new StatusRole
            {
                Role = role.Key(),
                State = health switch
                {
                    HealthState.Ready => "running",
                    HealthState.Loading => "loading",
                    _ => "idle",
                },
                Model = model.DisplayName + (string.IsNullOrWhiteSpace(model.Quant) ? "" : $" ({model.Quant})"),
                Port = cfg.Server.AuxPort(role),
            };
        }).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Одна строка о ролях: «roles: fast — Qwen 4B (running, :8766) · embed — not configured · …»;
    /// если ни одна роль не назначена — короткая подсказка.
    /// </summary>
    internal static string RolesLine(IReadOnlyList<StatusRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.All(r => r.State == "not_configured"))
            return "roles: fast/embed/rerank not configured (the main model serves everything; assign them in the tray app, Models tab)";
        return "roles: " + string.Join(" · ", roles.Select(r => r.State == "not_configured"
            ? $"{r.Role} — not configured"
            : $"{r.Role} — {r.Model} ({(r.State == "idle" ? "idle, starts on the first call" : r.State)}, :{r.Port})"));
    }

    /// <summary>
    /// Очередь GPU: занятые слоты и ждущие, затем по строке на держателя (инструмент, клиент, сколько держит).
    /// busy — по мьютексам (истина для исключительного владения), список — с доски очереди.
    /// </summary>
    internal static string QueueLine(int parallel, int busy, IReadOnlyList<GpuQueueEntry> board, DateTime nowUtc)
    {
        var slots = Math.Clamp(parallel, 1, 16);
        var waiting = board.Count(e => e.State == GpuQueueEntry.Waiting);
        var sb = new StringBuilder($" · queue: {busy}/{slots} slot(s) busy");
        if (waiting > 0) sb.Append($", {waiting} waiting");
        sb.Append(slots == 1
            ? " (agent runs share the single slot with other calls via llama-server's own queue)"
            : $" (agent runs use at most {slots - 1}; 1 slot is kept for interactive calls)");
        sb.Append('\n');
        sb.Append(GpuQueueBoard.Describe(board, nowUtc));
        var listed = board.Count(e => e.State == GpuQueueEntry.Holding && e.Slot >= 0);
        if (busy > listed) sb.Append($"  {busy - listed} slot(s) held without details (e.g. an older Offload version in another IDE)\n");
        return sb.ToString();
    }

    /// <summary>
    /// Контекст на запрос (доля слота) и, при общем KV-кэше (-kvu), весь буфер отдельно:
    /// «context 65536 tok per request (shared KV 196608 across 3 slots)».
    /// </summary>
    internal static string ContextLine(ServerProps props)
    {
        if (props.ContextPerSlot <= 0) return "";
        var slots = Math.Max(1, props.TotalSlots);
        if (props.SharedContext is int shared && slots > 1)
            return $" · context {props.ContextPerSlot} tok per request (shared KV {shared} across {slots} slots)";
        return slots > 1
            ? $" · context {props.ContextPerSlot} tok per request × {slots} slots"
            : $" · context {props.ContextPerSlot} tok per request";
    }

    /// <summary>«remote: 192.168.1.10:8765 · reachable · latency 4 ms» (клиентский режим: модель на другом компьютере).</summary>
    internal static string RemoteLine(string host, HealthState health, TimeSpan? latency, string? detail)
    {
        var state = health switch
        {
            HealthState.Ready => "reachable",
            HealthState.Loading => "reachable, loading its model",
            _ => "NOT reachable" + (LocalModel.EnglishDetail(detail) is { } d ? $" ({d})" : ""),
        };
        var ms = health != HealthState.Down && latency is { } l ? $" · latency {Math.Round(l.TotalMilliseconds):0} ms" : "";
        return $"remote: {host} · {state}{ms} (the main model runs on another PC; auxiliary roles stay local)";
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
