using System.Diagnostics;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Llama;

namespace Offload.Mcp.Infrastructure;

/// <summary>Ответ локальной модели после очистки (без размышлений).</summary>
internal sealed record ModelReply(string Text, ChatResult Raw)
{
    public bool Truncated => Raw.Truncated;
}

/// <summary>Запрос не помещается в контекст модели (сервер вернул ошибку контекста).</summary>
internal sealed class ContextExceededException(string message) : Exception(message);

/// <summary>Статистика одного вызова инструмента (для подвала результата и usage.jsonl).</summary>
internal sealed class ToolStats
{
    private readonly object _lock = new();
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public int ModelCalls { get; private set; }
    public double? LastTps { get; private set; }
    public TimeSpan ModelTime { get; private set; }

    /// <summary>Токены материала, прочитанного сервером вместо облачной модели.</summary>
    public long TokensRead { get; set; }
    public int FilesRead { get; set; }

    /// <summary>Токены кода, записанного на диск локальной моделью (облачной не пришлось его генерировать).</summary>
    public long TokensWritten { get; set; }

    public void Add(ChatResult r)
    {
        lock (_lock)
        {
            PromptTokens += r.PromptTokens;
            CompletionTokens += r.CompletionTokens;
            ModelCalls++;
            ModelTime += r.Duration;
            var tps = r.GenerationTokensPerSecond;
            if (tps is null && r.CompletionTokens > 0 && r.Duration.TotalSeconds > 0) tps = r.CompletionTokens / r.Duration.TotalSeconds;
            if (tps is > 0) LastTps = tps;
        }
    }

    public void AddExternal(long prompt, long completion, TimeSpan duration)
    {
        lock (_lock)
        {
            PromptTokens += prompt;
            CompletionTokens += completion;
            ModelCalls++;
            ModelTime += duration;
        }
    }
}

/// <summary>
/// Обращение к локальной модели: сборка ChatRequest из настроек модели (сэмплинг, отключение размышлений),
/// потоковая генерация с «пульсом» прогресса, очистка ответа.
/// </summary>
internal sealed class LocalModel(LlamaClient client, AppConfig cfg, ProgressReporter progress, ToolStats stats)
{
    /// <summary>Предел одного обращения к модели — защита от зависшего сервера (пульс прогресса иначе держал бы вызов вечно).</summary>
    public static TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public LlamaClient Client => client;
    public InstalledModel? Model { get; } = cfg.ActiveModel();
    public int ContextPerSlot { get; private set; } = 8192;
    public ServerProps? Props { get; private set; }

    public string DisplayName
    {
        get
        {
            var name = Model?.DisplayName;
            if (string.IsNullOrWhiteSpace(name)) name = Model?.Id;
            if (string.IsNullOrWhiteSpace(name)) name = Props?.ModelAlias ?? "local model";
            if (!string.IsNullOrWhiteSpace(Model?.Quant) && !name.Contains(Model.Quant, StringComparison.OrdinalIgnoreCase))
                name += " " + Model.Quant;
            return name;
        }
    }

    public async Task InitAsync(CancellationToken ct)
    {
        Props = await client.GetPropsAsync(ct).ConfigureAwait(false);
        var ctx = Props?.ContextPerSlot ?? 0;
        if (ctx <= 0) ctx = cfg.Server.ContextSize > 0 ? cfg.Server.ContextSize : Model?.RecommendedContext ?? 0;
        if (ctx <= 0) ctx = 8192;
        ContextPerSlot = ctx;
    }

    /// <summary>
    /// Бюджет токенов под материал: контекст − ответ − системный промпт/вопрос − запас (шаблон чата, 5% на погрешность оценки).
    /// </summary>
    public int MaterialBudget(int answerTokens, params string[] fixedParts)
    {
        var fixedTokens = fixedParts.Sum(Tokens.Estimate);
        var budget = ContextPerSlot - answerTokens - fixedTokens - 256 - ContextPerSlot / 20;
        return Math.Max(0, budget);
    }

    /// <summary>Системный промпт: общая роль + правила задачи + «Project rules» из настроек.</summary>
    public string SystemPrompt(string taskRules)
    {
        var s = "You are Offload, a local coding model doing a delegated sub-task for another AI coding agent. " +
                "Be precise and literal. Use ONLY the material in the prompt; never invent code, APIs, file names or line numbers. " +
                "If the material does not contain something, say so briefly. No preamble, no apologies, no restating the task.\n\n" + taskRules;
        var extra = cfg.Mcp.ExtraSystemPrompt;
        if (!string.IsNullOrWhiteSpace(extra)) s += "\n\nProject rules:\n" + extra.Trim();
        return s;
    }

    /// <summary>
    /// Единственное место сборки ChatRequest. Размышления отключаются по типу модели
    /// (ChatRequest.ChatTemplateKwargs {"enable_thinking": false} или ReasoningEffort "low");
    /// ответ дополнительно очищается от &lt;think&gt; в OutputCleaner.
    /// </summary>
    internal static ChatRequest BuildRequest(InstalledModel? model, IReadOnlyList<ChatMessage> messages, int maxTokens)
    {
        var s = model?.Sampling;
        IReadOnlyDictionary<string, object>? kwargs = null;
        string? effort = null;
        switch (model?.Reasoning ?? ReasoningControl.None)
        {
            case ReasoningControl.EnableThinkingKwarg:
                kwargs = new Dictionary<string, object> { ["enable_thinking"] = false };
                break;
            case ReasoningControl.ReasoningEffort:
                effort = "low";
                break;
        }
        return new ChatRequest(
            messages,
            MaxTokens: Math.Max(16, maxTokens),
            Temperature: s?.Temperature,
            TopP: s?.TopP,
            TopK: s?.TopK,
            MinP: s?.MinP,
            RepeatPenalty: s?.RepeatPenalty,
            Stop: null,
            PresencePenalty: s is { PresencePenalty: > 0 } ? s.PresencePenalty : null,
            ChatTemplateKwargs: kwargs,
            ReasoningEffort: effort);
    }

    public async Task<ModelReply> ChatAsync(string system, string user, int maxTokens, string label, CancellationToken ct)
    {
        var request = BuildRequest(Model, [ChatMessage.System(system), ChatMessage.User(user)], maxTokens);
        var promptEstimate = Tokens.Estimate(system) + Tokens.Estimate(user);
        long deltas = 0;
        var firstTokenTicks = 0L;
        var sw = Stopwatch.StartNew();

        string Heartbeat()
        {
            var d = Interlocked.Read(ref deltas);
            var first = Interlocked.Read(ref firstTokenTicks);
            if (d == 0 || first == 0) return $"{label}: reading ≈{Tokens.Format(promptEstimate)} tok of input… {sw.Elapsed.TotalSeconds:0} s";
            var genSeconds = Math.Max(0.5, (sw.ElapsedTicks - first) / (double)Stopwatch.Frequency);
            return $"{label}: generating… {d} tok, {d / genSeconds:0} tok/s";
        }

        progress.Report($"{label}: sending ≈{Tokens.Format(promptEstimate)} tok to the local model");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        ChatResult result;
        await using (progress.StartHeartbeat(Heartbeat))
        {
            try
            {
                result = await client.ChatAsync(request, _ =>
                {
                    if (Interlocked.Increment(ref deltas) == 1) Interlocked.Exchange(ref firstTokenTicks, sw.ElapsedTicks);
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ToolException($"The local model did not finish within {CallTimeout.TotalMinutes:0} min; the task is too big for it. Split it or do it yourself.");
            }
            catch (LlamaApiException ex)
            {
                throw MapError(ex);
            }
        }
        stats.Add(result);
        var text = OutputCleaner.StripThink(result.Content);
        if (text.Length == 0 && !string.IsNullOrWhiteSpace(result.Content))
            Log.Debug("mcp", "Ответ модели состоял только из размышлений");
        return new ModelReply(text, result);
    }

    /// <summary>Ошибки llama-server (тексты на русском) → понятные IDE сообщения на английском.</summary>
    internal static Exception MapError(LlamaApiException ex)
    {
        Log.Warn("mcp", $"llama-server: {ex.StatusCode}: {ex.Message}");
        var msg = ex.Message;
        if (ex.StatusCode == 401)
            return new ToolException("The local model server rejected Offload's API key (401). Restart the server from the Offload tray app so the keys match.");
        if (ex.StatusCode == 503)
            return new ToolException("The local model is still loading. Retry in a few seconds.");
        if (msg.Contains("контекст", StringComparison.OrdinalIgnoreCase) || msg.Contains("context", StringComparison.OrdinalIgnoreCase))
            return new ContextExceededException("The request does not fit into the local model's context window.");
        if (ex.StatusCode is null)
            return new ToolException("Lost connection to the local model server (it may have restarted or crashed). Retry once; if it repeats, check the Offload tray app. Details: " + msg);
        if (ex.StatusCode >= 500)
            return new ToolException($"The local model server failed (HTTP {ex.StatusCode}): {msg}. Retry once or do the task yourself.");
        return new ToolException($"The local model server rejected the request (HTTP {ex.StatusCode}): {msg}");
    }
}
