using System.Diagnostics;
using System.Text.Json;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Llama;

namespace Offload.Mcp.Infrastructure;

/// <summary>Ответ локальной модели после очистки (без размышлений).</summary>
internal sealed record ModelReply(string Text, ChatResult Raw)
{
    public bool Truncated => Raw.Truncated;
}

/// <summary>Ответ со структурированным выводом: текст и разобранный JSON (null — модель выдала не JSON).</summary>
internal sealed record ModelJsonReply(ModelReply Reply, JsonElement? Json);

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

    /// <summary>Символы материала, просмотренного детерминированным инструментом (индекс, поиск, вывод команд) вместо IDE.</summary>
    public long ScannedChars { get; private set; }

    public int ScannedFiles { get; private set; }

    /// <summary>Оценка токенов просмотренного материала (≈3 символа на токен, без обращения к серверу).</summary>
    public long ScannedTokens => (long)Math.Ceiling(ScannedChars / 3.0);

    /// <summary>Суммарное ожидание слота GPU за вызов.</summary>
    public TimeSpan QueueWait { get; private set; }

    /// <summary>Образец материала, отправленного модели (для калибровки оценки токенов по /tokenize).</summary>
    public string? MaterialSample { get; private set; }

    /// <summary>Поправка «точные токены / эвристика» для этого вызова (null — только эвристика).</summary>
    public double? TokenRatio { get; set; }

    /// <summary>Хотя бы один ответ модели в вызове обрезан по max_tokens (такой результат не кэшируется).</summary>
    public bool AnyTruncated { get; private set; }

    public void AddScanned(long chars, int files = 0)
    {
        lock (_lock)
        {
            ScannedChars += Math.Max(0, chars);
            ScannedFiles += Math.Max(0, files);
        }
    }

    /// <summary>Учесть просмотренные файлы индекса: символы строк плюс переводы строк.</summary>
    public void AddScanned(IEnumerable<SourceFile> files)
    {
        long chars = 0;
        var count = 0;
        foreach (var f in files)
        {
            count++;
            chars += f.Lines.Length;
            foreach (var l in f.Lines) chars += l.Length;
        }
        AddScanned(chars, count);
    }

    public void AddQueueWait(TimeSpan waited)
    {
        if (waited <= TimeSpan.Zero) return;
        lock (_lock) QueueWait += waited;
    }

    /// <summary>Запомнить самый объёмный запрос (до 24 тыс. символов) как образец для калибровки.</summary>
    public void NoteSample(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var sample = text.Length <= TokenCounter.MaxSampleChars ? text : text[..TokenCounter.MaxSampleChars];
        lock (_lock)
        {
            if (MaterialSample is null || sample.Length > MaterialSample.Length) MaterialSample = sample;
        }
    }

    public void Add(ChatResult r)
    {
        lock (_lock)
        {
            PromptTokens += r.PromptTokens;
            CompletionTokens += r.CompletionTokens;
            ModelCalls++;
            ModelTime += r.Duration;
            if (r.Truncated) AnyTruncated = true;
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
/// <param name="role">Роль сервера, к которому обращается клиент (<see cref="ModelRouting"/>): его модель задаёт сэмплинг и контекст.</param>
internal sealed class LocalModel(LlamaClient client, AppConfig cfg, ProgressReporter progress, ToolStats stats, ModelRole role = ModelRole.Quality)
{
    /// <summary>Предел одного обращения к модели — защита от зависшего сервера (пульс прогресса иначе держал бы вызов вечно).</summary>
    public static TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public LlamaClient Client => client;

    /// <summary>Роль модели этого вызова (quality — основная, fast — быстрая).</summary>
    public ModelRole Role => role;

    /// <summary>
    /// Установленная модель роли (сэмплинг, отключение размышлений). Удалённый сервер (клиентский режим) — null: там другая
    /// модель, её умолчания задаёт тот сервер, а размышления отключаются по шаблону чата из /props (<see cref="ServerProps.ReasoningHint"/>).
    /// </summary>
    public InstalledModel? Model { get; } = client.IsRemote ? null : cfg.RoleModel(role);
    public int ContextPerSlot { get; private set; } = 8192;
    public ServerProps? Props { get; private set; }

    public string DisplayName
    {
        get
        {
            if (client.IsRemote) return $"{client.Model} @ {RemoteServer.DisplayHost(client.BaseUrl)}";
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
        if (ctx <= 0 && role != ModelRole.Quality && Model is not null) ctx = ModelRoleConfig.AuxContext(role, Model);
        // Удалённый сервер без /props (не llama.cpp): контекст по последней проверке трея, иначе 8192 — с запасом.
        if (ctx <= 0 && client.IsRemote) ctx = cfg.Remote?.ContextSize ?? 0;
        else if (ctx <= 0) ctx = cfg.Server.ContextSize > 0 ? cfg.Server.ContextSize : Model?.RecommendedContext ?? 0;
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
    /// <param name="reasoningHint">Способ отключения размышлений, когда записи о модели нет (удалённый сервер: по шаблону чата из /props).</param>
    internal static ChatRequest BuildRequest(InstalledModel? model, IReadOnlyList<ChatMessage> messages, int maxTokens, ResponseFormat? format = null,
        ReasoningControl? reasoningHint = null)
    {
        var s = model?.Sampling;
        IReadOnlyDictionary<string, object>? kwargs = null;
        string? effort = null;
        switch (model?.Reasoning ?? reasoningHint ?? ReasoningControl.None)
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
            ReasoningEffort: effort,
            ResponseFormat: format);
    }

    public Task<ModelReply> ChatAsync(string system, string user, int maxTokens, string label, CancellationToken ct) =>
        SendAsync(BuildRequest(Model, [ChatMessage.System(system), ChatMessage.User(user)], maxTokens, null, ReasoningHint), system, user, label, ct);

    /// <summary>Отключение размышлений без записи о модели: только для удалённого сервера (по шаблону чата из /props).</summary>
    private ReasoningControl? ReasoningHint => Model is null && client.IsRemote ? Props?.ReasoningHint : null;

    /// <summary>
    /// Запрос со структурированным выводом (ROADMAP §9.2): response_format json_schema — llama-server ограничивает генерацию
    /// грамматикой схемы. Json — разобранный ответ (null, если ответ всё же не JSON: например, обрезан по max_tokens).
    /// </summary>
    public async Task<ModelJsonReply> ChatJsonAsync(string system, string user, ResponseFormat format, int maxTokens, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(format);
        var reply = await SendAsync(BuildRequest(Model, [ChatMessage.System(system), ChatMessage.User(user)], maxTokens, format, ReasoningHint),
            system, user, label, ct).ConfigureAwait(false);
        return new ModelJsonReply(reply, TryParseJson(reply.Text));
    }

    /// <summary>JSON из ответа модели: целиком или внутри ```json … ``` (на случай, если сервер не применил схему).</summary>
    internal static JsonElement? TryParseJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = t.IndexOf('\n');
            var fence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine > 0 && fence > firstLine) t = t[(firstLine + 1)..fence].Trim();
        }
        if (t.Length == 0 || t[0] is not ('{' or '[')) return null;
        try
        {
            using var doc = JsonDocument.Parse(t);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ModelReply> SendAsync(ChatRequest request, string system, string user, string label, CancellationToken ct)
    {
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
                throw MapError(ex, client.IsRemote);
            }
        }
        stats.Add(result);
        stats.NoteSample(user);
        var text = OutputCleaner.StripThink(result.Content);
        if (text.Length == 0 && !string.IsNullOrWhiteSpace(result.Content))
            Log.Debug("mcp", "Ответ модели состоял только из размышлений");
        return new ModelReply(text, result);
    }

    /// <summary>
    /// Ошибки llama-server → понятные IDE сообщения на английском. Классификация — по LlamaApiException.Kind
    /// (его выставляет слой Llama по типу ошибки/кодам), подробности — только из Detail: Message локализован для UI.
    /// </summary>
    /// <param name="ex">Ошибка слоя Llama.</param>
    /// <param name="remote">Удалённый сервер (клиентский режим): советы про сеть и ключ того сервера, а не про трей.</param>
    internal static Exception MapError(LlamaApiException ex, bool remote = false)
    {
        Log.Warn("mcp", $"llama-server: {ex.Kind} {ex.StatusCode}: {ex.Message}");
        var detail = EnglishDetail(ex.Detail);
        var suffix = detail is null ? "" : " Details: " + detail;
        if (remote)
        {
            switch (ex.Kind)
            {
                case LlamaErrorKind.Unauthorized:
                    return new ToolException("The remote model server rejected the API key (401). Copy the key from Offload → Settings → Network access on the host PC into Offload → Settings → Remote server on this PC.");
                case LlamaErrorKind.NotRunning:
                case LlamaErrorKind.ConnectionFailed:
                case LlamaErrorKind.ConnectionLost:
                    return new ToolException("Lost connection to the remote model server (network, VPN or the host PC). Retry once; if it repeats, do the task yourself." + suffix);
            }
        }
        switch (ex.Kind)
        {
            case LlamaErrorKind.Unauthorized:
                return new ToolException("The local model server rejected Offload's API key (401). Restart the server from the Offload tray app so the keys match.");
            case LlamaErrorKind.Loading:
                return new ToolException("The local model is still loading. Retry in a few seconds.");
            case LlamaErrorKind.ContextExceeded:
                var sizes = ex.PromptTokens is int p && ex.ContextSize is int c ? $" ({p} tok requested, context {c} tok)" : "";
                return new ContextExceededException($"The request does not fit into the local model's context window{sizes}.");
            case LlamaErrorKind.NotRunning:
                return new ToolException("The local model server is not running (connection refused). Start it from the Offload tray app, or retry once: it may be restarting." + suffix);
            case LlamaErrorKind.ConnectionFailed:
            case LlamaErrorKind.ConnectionLost:
                return new ToolException("Lost connection to the local model server (it may have restarted or crashed). Retry once; if it repeats, check the Offload tray app." + suffix);
        }
        var status = ex.StatusCode is int code ? $"HTTP {code}" : "no HTTP status";
        if (ex.Kind == LlamaErrorKind.ServerError || ex.StatusCode >= 500)
            return new ToolException($"The local model server failed ({status}).{suffix} Retry once or do the task yourself.");
        return new ToolException($"The local model server rejected the request ({status}).{suffix}");
    }

    /// <summary>
    /// Подробность для IDE (до 300 символов, в одну строку). Текст с нелатинскими буквами (локализованный UI/ОС) отбрасывается —
    /// сообщения для модели только на английском; полный текст остаётся в журнале.
    /// </summary>
    internal static string? EnglishDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        foreach (var ch in detail)
        {
            if (ch > '\u007f' && char.IsLetter(ch)) return null;
        }
        var s = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= 300 ? s : s[..300] + "…";
    }
}
