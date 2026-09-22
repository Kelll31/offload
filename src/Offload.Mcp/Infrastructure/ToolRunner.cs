using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Usage;

namespace Offload.Mcp.Infrastructure;

/// <summary>Всё, что нужно реализации инструмента на время одного вызова.</summary>
internal sealed class ToolContext
{
    public required string Tool { get; init; }
    public required AppConfig Cfg { get; init; }
    public required SessionState State { get; init; }
    public required ProgressReporter Progress { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    public McpServer? Server { get; init; }
    public string? ToolUseId { get; init; }
    public CancellationToken Ct { get; init; }
    public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
    public ToolStats Stats { get; } = new();

    private LocalModel? _model;

    /// <summary>Модель, если к ней уже обращались в этом вызове (для подвала).</summary>
    public LocalModel? ModelIfUsed => _model;

    public GatherOptions GatherOptions => new(
        Math.Max(4096, Cfg.Mcp.MaxFileBytes),
        Math.Max(16 * 1024, Cfg.Mcp.MaxTotalBytes),
        Cfg.Mcp.SecretFilePatterns ?? []);

    public int MaxResponseChars => Math.Clamp(Cfg.Mcp.MaxResponseChars, 2000, 200_000);

    /// <summary>Запустить сервер при необходимости и получить клиента модели (один раз за вызов).</summary>
    public async Task<LocalModel> GetModelAsync()
    {
        if (_model is not null) return _model;
        var client = await ServerEnsurer.EnsureAsync(Cfg, State, Progress, Ct).ConfigureAwait(false);
        var model = new LocalModel(client, Cfg, Progress, Stats);
        await model.InitAsync(Ct).ConfigureAwait(false);
        _model = model;
        return model;
    }

    /// <summary>Разрешить путь для чтения одного файла (с проверкой секретов/ссылок).</summary>
    public string ResolveRead(string raw)
    {
        var full = PathGuard.Resolve(raw, Roots);
        PathGuard.CheckReadExplicit(full, Cfg.Mcp.SecretFilePatterns, raw);
        if (PathGuard.EntryExists(full) && PathGuard.CheckReadResolved(full, Roots, Cfg.Mcp.SecretFilePatterns) is { } why)
            throw new ToolException($"Refusing to read '{raw}': {why}.");
        return full;
    }

    /// <summary>Разрешить путь для записи: полный путь и канонический (куда реально пишем).</summary>
    public (string Full, string Canonical) ResolveWrite(string raw)
    {
        var writeRoots = Cfg.Mcp.RestrictWritesToWorkspace ? Workspace.WriteRoots(Roots) : Roots;
        var full = PathGuard.Resolve(raw, Roots);
        var canonical = PathGuard.CheckWrite(full, writeRoots, Cfg.Mcp.RestrictWritesToWorkspace, Cfg.Mcp.SecretFilePatterns, raw);
        if (Workspace.IsForbiddenWriteLocation(canonical) || PathGuard.IsOffloadData(canonical))
            throw new ToolException($"Refusing to write '{raw}': system or application-data location.");
        return (full, canonical);
    }

    public string Display(string full) => PathGuard.Display(full, Roots);
}

/// <summary>
/// Обёртка каждого инструмента: свежий конфиг, корни, прогресс; ошибки → isError с понятным текстом;
/// подвал со статистикой и запись в usage.jsonl для вызовов модели; ограничение размера ответа.
/// </summary>
internal static class ToolRunner
{
    public static async Task<CallToolResult> RunAsync(string tool, SessionState state, RequestContext<CallToolRequestParams>? rc,
        Func<ToolContext, Task<string>> body, CancellationToken ct)
    {
        var server = rc?.Server;
        var progress = new ProgressReporter(server, rc?.Params?.ProgressToken);
        ToolContext? ctx = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var cfg = ConfigStore.Reload();
            var roots = await Workspace.GetRootsAsync(server, state, ct).ConfigureAwait(false);
            ctx = new ToolContext
            {
                Tool = tool,
                Cfg = cfg,
                State = state,
                Progress = progress,
                Roots = roots,
                Server = server,
                ToolUseId = ReadToolUseId(rc),
                Ct = ct,
            };
            Log.Info("mcp", $"{tool}: начат (клиент {server?.ClientInfo?.Name ?? "?"}, корень {roots.FirstOrDefault()})");
            var text = await body(ctx).ConfigureAwait(false);
            var final = Finish(text, ctx, ok: true);
            Log.Info("mcp", $"{tool}: готово за {sw.Elapsed.TotalSeconds:0.0} с, {final.Length} симв.");
            await progress.FlushAsync().ConfigureAwait(false);
            return new CallToolResult { Content = [new TextContentBlock { Text = final }] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Info("mcp", $"{tool}: отменён клиентом");
            if (ctx is not null) RecordUsage(ctx, ok: false, resultText: "");
            throw;
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException)
        {
            Log.Warn("mcp", $"{tool}: {ex.Message}");
            var msg = ex is ContextExceededException
                ? ex.Message + " Pass fewer or smaller files (or a narrower glob), or do this task yourself."
                : ex.Message;
            await progress.FlushAsync().ConfigureAwait(false);
            return Error(ctx is null ? msg : Finish(msg, ctx, ok: false));
        }
        catch (Exception ex)
        {
            Log.Error("mcp", $"{tool}: внутренняя ошибка", ex);
            await progress.FlushAsync().ConfigureAwait(false);
            var msg = $"Offload internal error in {tool}: {ex.GetType().Name}: {ex.Message}. Details: {Log.CurrentFile ?? "Offload logs"}. Do this task yourself.";
            return Error(ctx is null ? msg : Finish(msg, ctx, ok: false));
        }
    }

    private static CallToolResult Error(string text) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    private static string? ReadToolUseId(RequestContext<CallToolRequestParams>? rc)
    {
        try
        {
            return rc?.Params?.Meta?["claudecode/toolUseId"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ограничить размер, добавить подвал и записать статистику (если была модель).</summary>
    internal static string Finish(string text, ToolContext ctx, bool ok)
    {
        var model = ctx.ModelIfUsed;
        var footer = model is null || ctx.Stats.ModelCalls == 0 ? "" : BuildFooter(ctx, model, EstimateSaved(ctx, text));
        var capped = Cap(text, ctx.MaxResponseChars - footer.Length);
        if (model is not null && ctx.Stats.ModelCalls > 0) RecordUsage(ctx, ok, capped);
        return capped + footer;
    }

    /// <summary>
    /// Эвристика «сэкономленных облачных токенов»:
    /// чтение (ask/log/review/commit) — токены материала, прочитанного сервером, минус размер ответа;
    /// запись/правка — токены написанного локальной моделью кода × 5 (выходные токены облака в 5 раз дороже входных)
    /// плюс прочитанный контекст, минус размер результата. Не меньше нуля.
    /// </summary>
    internal static long EstimateSaved(ToolContext ctx, string resultText) =>
        Math.Max(0, ctx.Stats.TokensWritten * 5 + ctx.Stats.TokensRead - Tokens.Estimate(resultText));

    private static string BuildFooter(ToolContext ctx, LocalModel model, long saved)
    {
        var name = model.DisplayName;
        if (name.Length > 40) name = name[..40] + "…";
        var parts = new List<string> { "offload", name };
        if (ctx.Stats.FilesRead > 0) parts.Add($"read {ctx.Stats.FilesRead} file{(ctx.Stats.FilesRead == 1 ? "" : "s")} (≈{Tokens.Format(ctx.Stats.TokensRead)} tok)");
        if (ctx.Stats.LastTps is double tps) parts.Add($"{tps:0} tok/s");
        parts.Add($"{ctx.Stopwatch.Elapsed.TotalSeconds:0} s");
        parts.Add($"≈{Tokens.Format(saved)} cloud tokens avoided");
        var line = string.Join(" · ", parts);
        if (line.Length > 196) line = line[..196];
        return "\n---\n" + line;
    }

    /// <summary>Обрезать ответ до лимита с понятной пометкой.</summary>
    internal static string Cap(string text, int maxChars)
    {
        maxChars = Math.Max(500, maxChars);
        if (text.Length <= maxChars) return text;
        var marker = $"\n…[truncated by Offload: {text.Length - maxChars} more chars. Ask a narrower question or lower max_answer_tokens.]";
        var cut = Math.Max(0, maxChars - marker.Length);
        // Не рвём суррогатную пару.
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + marker;
    }

    private static void RecordUsage(ToolContext ctx, bool ok, string resultText)
    {
        try
        {
            var saved = ok ? EstimateSaved(ctx, resultText) : 0;
            if (ok) ctx.State.RecordCall(saved);
            if (ctx.Stats.LastTps is double tps) ctx.State.LastGenerationTps = tps;
            var rec = new UsageRecord(
                DateTime.UtcNow,
                ctx.Tool,
                ctx.Server?.ClientInfo?.Name,
                ctx.Stats.PromptTokens,
                ctx.Stats.CompletionTokens,
                (long)ctx.Stopwatch.Elapsed.TotalMilliseconds,
                ok,
                ctx.ModelIfUsed?.DisplayName,
                saved);
            // Append может ждать трей по IPC до 2 с — не держим ответ дольше 3 с.
            Task.Run(() => UsageLog.Append(rec)).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Debug("mcp", $"usage не записан: {ex.Message}");
        }
    }
}
