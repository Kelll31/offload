using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
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

    /// <summary>
    /// Структурированный результат (structuredContent) для инструментов с outputSchema; текст ответа при этом не меняется.
    /// Задаётся только при успехе — при ошибке (isError) не отправляется.
    /// </summary>
    public object? Structured { get; set; }

    /// <summary>Ссылки на ресурсы (resource_link) с полным текстом, который в ответ не помещается (лог прогона, diff задачи).</summary>
    public List<ResourceLinkBlock> ResourceLinks { get; } = [];

    /// <summary>Добавить resource_link (повторная ссылка на тот же uri не дублируется).</summary>
    public void AddResourceLink(string uri, string name, string? description = null, string mimeType = "text/plain", long? size = null)
    {
        if (ResourceLinks.Any(l => l.Uri == uri)) return;
        ResourceLinks.Add(new ResourceLinkBlock { Uri = uri, Name = name, Description = description, MimeType = mimeType, Size = size });
    }

    /// <summary>Модель, если к ней уже обращались в этом вызове (для подвала).</summary>
    public LocalModel? ModelIfUsed => _model;

    /// <summary>Пометка для подвала ответа (например, «cached · inputs unchanged since …» при попадании в кэш результатов).</summary>
    public string? FooterNote { get; set; }

    public GatherOptions GatherOptions => new(
        Math.Max(4096, Cfg.Mcp.MaxFileBytes),
        Math.Max(16 * 1024, Cfg.Mcp.MaxTotalBytes),
        Cfg.Mcp.SecretFilePatterns ?? [],
        RedactSecrets: Cfg.Mcp.RedactSecrets);

    public int MaxResponseChars => Math.Clamp(Cfg.Mcp.MaxResponseChars, 2000, 200_000);

    /// <summary>
    /// Запустить сервер при необходимости и получить клиента модели (один раз за вызов). Сервер — по маршруту инструмента
    /// (<see cref="ModelRouting"/>): быстрая модель для коротких задач, если назначена, иначе основная.
    /// </summary>
    public async Task<LocalModel> GetModelAsync()
    {
        if (_model is not null) return _model;
        var (client, role) = await ServerEnsurer.EnsureRoutedAsync(Cfg, State, Progress, ModelRouting.Resolve(Cfg, Tool), Ct).ConfigureAwait(false);
        var model = new LocalModel(client, Cfg, Progress, Stats, role);
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

    /// <summary>Контекст для фоновой задачи: свой прогресс (без клиента), свой токен отмены и своя статистика.</summary>
    public ToolContext ForBackground(ProgressReporter progress, CancellationToken ct) => new()
    {
        Tool = Tool,
        Cfg = Cfg,
        State = State,
        Progress = progress,
        Roots = Roots,
        Server = null,
        ToolUseId = ToolUseId,
        Ct = ct,
    };
}

/// <summary>
/// Обёртка каждого инструмента: свежий конфиг, корни, прогресс; ошибки → isError с понятным текстом;
/// подвал со статистикой и запись в usage.jsonl (вызовы модели и материал, обработанный сервером); ограничение размера ответа.
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
            Log.Info("mcp", $"{tool}: начат (клиент {server?.ClientInfo?.Name ?? "?"}, корень {(roots.Count > 0 ? roots[0] : null)})");
            var text = await body(ctx).ConfigureAwait(false);
            await CalibrateTokensAsync(ctx).ConfigureAwait(false);
            var final = Finish(text, ctx, ok: true);
            Log.Info("mcp", $"{tool}: готово за {sw.Elapsed.TotalSeconds:0.0} с, {final.Length} симв.");
            await progress.FlushAsync().ConfigureAwait(false);
            return Success(final, ctx);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Info("mcp", $"{tool}: отменён клиентом");
            if (ctx is not null && HasUsage(ctx)) RecordUsage(ctx, ok: false, resultText: "");
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

    /// <summary>Успешный ответ: текст (как раньше) + resource_link на полный текст + structuredContent, если инструмент его задал.</summary>
    internal static CallToolResult Success(string text, ToolContext ctx)
    {
        var result = new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        foreach (var link in ctx.ResourceLinks) result.Content.Add(link);
        if (ctx.Structured is { } structured)
        {
            try
            {
                result.StructuredContent = JsonSerializer.SerializeToElement(structured, structured.GetType(), StructuredJson);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                Log.Warn("mcp", $"{ctx.Tool}: structuredContent не сериализован: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Сериализация structuredContent — теми же настройками, что и схема (McpJsonUtilities), имена полей заданы атрибутами.</summary>
    internal static JsonSerializerOptions StructuredJson => McpJsonUtilities.DefaultOptions;

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

    /// <summary>
    /// Поправка оценки токенов по токенизатору модели (/tokenize, с кэшем) — только если модель уже использовалась в вызове:
    /// сервер запущен, а запрос короткий. Детерминированным инструментам задержку не добавляет.
    /// </summary>
    private static async Task CalibrateTokensAsync(ToolContext ctx)
    {
        if (ctx.ModelIfUsed is not { } model || ctx.Stats.ModelCalls == 0) return;
        ctx.Stats.TokenRatio = await TokenCounter.CalibrateAsync(model.Client, ModelKey(model), ctx.Stats.MaterialSample, ctx.Ct).ConfigureAwait(false);
    }

    internal static string ModelKey(LocalModel model) => model.Model?.Id ?? model.DisplayName;

    /// <summary>Поправка токенизатора для вызова: своя, иначе уже известная для модели, иначе нет (только эвристика).</summary>
    private static double? RatioOf(ToolContext ctx) =>
        ctx.Stats.TokenRatio ?? (ctx.ModelIfUsed is { } m && ctx.Stats.ModelCalls > 0 ? TokenCounter.RatioFor(ModelKey(m)) : null);

    /// <summary>Было ли что записывать в статистику: обращения к модели или материал, обработанный сервером вместо IDE.</summary>
    internal static bool HasUsage(ToolContext ctx) =>
        ctx.ModelIfUsed is not null && ctx.Stats.ModelCalls > 0
        || ctx.Stats.ScannedChars > 0 || ctx.Stats.TokensRead > 0 || ctx.Stats.TokensWritten > 0;

    /// <summary>Ограничить размер, добавить подвал и записать статистику (если была модель или просмотренный материал).</summary>
    internal static string Finish(string text, ToolContext ctx, bool ok)
    {
        var usage = HasUsage(ctx);
        var footer = usage || ctx.Stats.QueueWait > FooterWaitThreshold ? BuildFooter(ctx, ok && usage ? EstimateSaved(ctx, text) : 0) : "";
        var capped = Cap(text, ctx.MaxResponseChars - footer.Length, ok ? ctx.ResourceLinks.FirstOrDefault()?.Uri : null);
        if (usage) RecordUsage(ctx, ok, capped);
        return capped + footer;
    }

    /// <summary>Ожидание слота GPU дольше этого попадает в подвал ответа.</summary>
    internal static readonly TimeSpan FooterWaitThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Оценка сэкономленных облачных токенов — см. <see cref="Savings"/>.</summary>
    internal static long EstimateSaved(ToolContext ctx, string resultText) => Savings.Estimate(ctx.Stats, resultText, RatioOf(ctx));

    internal static string BuildFooter(ToolContext ctx, long saved)
    {
        var s = ctx.Stats;
        var model = s.ModelCalls > 0 ? ctx.ModelIfUsed : null;
        var parts = new List<string> { "offload" };
        if (!string.IsNullOrWhiteSpace(ctx.FooterNote)) parts.Add(ctx.FooterNote);
        if (model is not null)
        {
            var name = model.DisplayName;
            if (name.Length > 40) name = name[..40] + "…";
            parts.Add(name);
        }
        if (s.FilesRead > 0) parts.Add($"read {s.FilesRead} file{(s.FilesRead == 1 ? "" : "s")} (≈{Tokens.Format(s.TokensRead)} tok)");
        else if (s.ScannedChars > 0)
            parts.Add(s.ScannedFiles > 0
                ? $"scanned {s.ScannedFiles} file{(s.ScannedFiles == 1 ? "" : "s")} (≈{Tokens.Format(s.ScannedTokens)} tok)"
                : $"scanned ≈{Tokens.Format(s.ScannedTokens)} tok");
        if (model is not null && s.LastTps is double tps) parts.Add($"{tps:0} tok/s");
        parts.Add($"{ctx.Stopwatch.Elapsed.TotalSeconds:0} s");
        if (s.QueueWait > FooterWaitThreshold) parts.Add($"waited {s.QueueWait.TotalSeconds:0} s for a GPU slot");
        if (saved > 0 || model is not null) parts.Add($"≈{Tokens.Format(saved)} cloud tokens avoided");
        var line = string.Join(" · ", parts);
        if (line.Length > 196) line = line[..196];
        return "\n---\n" + line;
    }

    /// <summary>Обрезать ответ до лимита с понятной пометкой (если полный текст доступен ресурсом — со ссылкой на него).</summary>
    internal static string Cap(string text, int maxChars, string? fullTextUri = null)
    {
        maxChars = Math.Max(500, maxChars);
        if (text.Length <= maxChars) return text;
        var marker = fullTextUri is null
            ? $"\n…[truncated by Offload: {text.Length - maxChars} more chars. Ask a narrower question or lower max_answer_tokens.]"
            : $"\n…[truncated by Offload: {text.Length - maxChars} more chars. Full output: resource {fullTextUri} (resources/read).]";
        var cut = Math.Max(0, maxChars - marker.Length);
        // Не рвём суррогатную пару.
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + marker;
    }

    internal static void RecordUsage(ToolContext ctx, bool ok, string resultText)
    {
        try
        {
            var saved = ok ? EstimateSaved(ctx, resultText) : 0;
            if (ok) ctx.State.RecordCall(saved);
            var usedModel = ctx.ModelIfUsed is not null && ctx.Stats.ModelCalls > 0;
            if (usedModel && ctx.Stats.LastTps is double tps) ctx.State.LastGenerationTps = tps;
            var rec = new UsageRecord(
                DateTime.UtcNow,
                ctx.Tool,
                ctx.Server?.ClientInfo?.Name,
                ctx.Stats.PromptTokens,
                ctx.Stats.CompletionTokens,
                (long)ctx.Stopwatch.Elapsed.TotalMilliseconds,
                ok,
                usedModel ? ctx.ModelIfUsed!.DisplayName : null,
                saved)
            {
                Workspace = Savings.WorkspaceId(ctx.Roots.Count > 0 ? ctx.Roots[0] : null),
                QueueWaitMs = (long)ctx.Stats.QueueWait.TotalMilliseconds,
                GenerationTps = usedModel && ctx.Stats.LastTps is double g ? Math.Round(g, 1) : null,
            };
            // Append может ждать трей по IPC до 2 с — пишем в фоне, ответ не задерживается;
            // незавершённые записи дожидаются при остановке сервера (UsageRecorder.FlushAsync).
            UsageRecorder.Enqueue(rec);
        }
        catch (Exception ex)
        {
            Log.Debug("mcp", $"usage не записан: {ex.Message}");
        }
    }
}
