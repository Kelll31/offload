using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.Llama;

public enum HealthState { Down, Loading, Ready }

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string text) => new("system", text);
    public static ChatMessage User(string text) => new("user", text);
    public static ChatMessage Assistant(string text) => new("assistant", text);
}

public sealed record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    int? MaxTokens = null,
    double? Temperature = null,
    double? TopP = null,
    int? TopK = null,
    double? MinP = null,
    double? RepeatPenalty = null,
    IReadOnlyList<string>? Stop = null,
    double? PresencePenalty = null,
    /// <summary>
    /// Параметры шаблона чата (chat_template_kwargs), например {"enable_thinking": false} для Qwen3.5+/Ornith —
    /// отключает «размышления» для быстрых разовых задач. Значения: bool/string/число.
    /// </summary>
    IReadOnlyDictionary<string, object>? ChatTemplateKwargs = null,
    /// <summary>reasoning_effort: для gpt-oss — "low"/"medium"/"high"; "none" отключает рассуждения на стороне llama-server.</summary>
    string? ReasoningEffort = null,
    /// <summary>Структурированный ответ (response_format с json_schema): llama-server ограничивает генерацию грамматикой схемы.</summary>
    ResponseFormat? ResponseFormat = null);

/// <summary>
/// response_format {"type":"json_schema","json_schema":{"name":…,"strict":…,"schema":{…}}} (OpenAI) — llama-server
/// превращает схему в грамматику, и модель не может выдать ничего, кроме подходящего JSON.
/// </summary>
public sealed record ResponseFormat(string Name, JsonElement Schema, bool Strict = true)
{
    /// <summary>Формат по тексту JSON-схемы. Неверный JSON — JsonException.</summary>
    public static ResponseFormat JsonSchema(string name, string schemaJson, bool strict = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var doc = JsonDocument.Parse(schemaJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("JSON-схема должна быть объектом", nameof(schemaJson)); // l10n-ignore: ошибка программиста
        return new ResponseFormat(name, doc.RootElement.Clone(), strict);
    }
}

/// <summary>Результат /v1/embeddings: векторы в порядке входных текстов.</summary>
public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, int PromptTokens, TimeSpan Duration);

/// <summary>Оценка документа реранкером: индекс во входном списке и релевантность (чем больше, тем ближе к запросу).</summary>
public sealed record RerankScore(int Index, double Score);

/// <summary>Результат /v1/rerank: оценки по убыванию релевантности.</summary>
public sealed record RerankResult(IReadOnlyList<RerankScore> Scores, int PromptTokens, TimeSpan Duration);

public sealed record ChatResult(
    string Content,
    /// <summary>Рассуждения модели (reasoning_content), если модель «думающая».</summary>
    string? Reasoning,
    /// <summary>stop / length / …</summary>
    string FinishReason,
    int PromptTokens,
    int CompletionTokens,
    double? PromptTokensPerSecond,
    double? GenerationTokensPerSecond,
    TimeSpan Duration)
{
    public bool Truncated => FinishReason == "length";
}

/// <summary>Сведения о работающем сервере (/props, /v1/models).</summary>
public sealed record ServerProps(
    /// <summary>
    /// Контекст, гарантированный одному запросу (токенов). При общем KV-кэше (-kvu) — доля слота: n_ctx / total_slots.
    /// </summary>
    int ContextPerSlot,
    int TotalSlots,
    string? ModelPath,
    string? ModelAlias,
    string? BuildInfo)
{
    /// <summary>
    /// Общий KV-кэш, который делят слоты (-kvu, n_ctx из /props при нескольких слотах); null — у каждого слота свой контекст.
    /// Один запрос может занять больше ContextPerSlot, только если остальные слоты свободны.
    /// </summary>
    public int? SharedContext { get; init; }

    /// <summary>Шаблон чата модели поддерживает вызов инструментов (chat_template_caps.supports_tool_calls).</summary>
    public bool? SupportsToolCalls { get; init; }

    /// <summary>Модель выгружена по простою (--sleep-idle-seconds) и загрузится при следующем запросе.</summary>
    public bool? IsSleeping { get; init; }

    /// <summary>
    /// Как отключать «размышления» по шаблону чата из /props (chat_template): enable_thinking — Qwen3.5+ и подобные,
    /// reasoning_effort — gpt-oss. Нужен удалённому серверу: у клиента нет записи о его модели. null — шаблон неизвестен.
    /// </summary>
    public ReasoningControl? ReasoningHint { get; init; }
}

/// <summary>
/// Клиент OpenAI-совместимого API llama-server (Bearer = ApiKey).
/// Используется MCP-инструментами, проверкой в мастере и панелью статуса.
/// </summary>
public sealed partial class LlamaClient
{
    /// <summary>Потолок для коротких служебных запросов (/health, /props, /tokenize), если вызывающий не задал свой.</summary>
    private static readonly TimeSpan ShortRequestTimeout = TimeSpan.FromSeconds(30);

    public LlamaClient(string baseUrl, string apiKey)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        ApiKey = apiKey;
    }

    /// <summary>
    /// Клиент основного сервера из настроек (<see cref="RemoteServer.MainEndpoint"/>). Свой сервер: общий KV-кэш — как его
    /// включает LlamaServerArgs.Build (-kvu при нескольких слотах). Удалённый: адрес, ключ и id модели из настроек клиентского
    /// режима; настройки его слотов неизвестны — считаем KV общим (при нескольких слотах контекст запроса = n_ctx / слоты,
    /// оценка с запасом: запрос не выйдет за контекст).
    /// </summary>
    public static LlamaClient FromConfig(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var ep = cfg.MainEndpoint();
        if (ep.IsRemote) return new LlamaClient(ep.BaseUrl, ep.ApiKey) { Model = ep.Model, UnifiedKv = true, IsRemote = true };
        return new LlamaClient(cfg.Server.BaseUrl, cfg.Server.ApiKey) { UnifiedKv = LlamaServerArgs.UsesUnifiedKv(cfg.Server) };
    }

    /// <summary>Тот же сервер и ключ с другим именем модели (id из /v1/models удалённого сервера).</summary>
    public LlamaClient WithModel(string model) =>
        new(BaseUrl, ApiKey) { Model = string.IsNullOrWhiteSpace(model) ? Model : model, UnifiedKv = UnifiedKv, IsRemote = IsRemote };

    public string BaseUrl { get; }
    public string ApiKey { get; }

    /// <summary>Удалённый сервер клиентского режима (не свой llama-server): трей его не запускает, ошибки — про сеть, а не про процесс.</summary>
    public bool IsRemote { get; init; }

    /// <summary>Имя модели в запросах (--alias сервера): offload у основного, offload-fast/-embed/-rerank у вспомогательных.</summary>
    public string Model { get; init; } = LlamaServerArgs.DefaultAlias;

    /// <summary>
    /// Слоты сервера делят общий KV-кэш (-kvu): тогда /props сообщает в n_ctx слота весь буфер (контекст × слоты),
    /// а на один запрос гарантирована только доля n_ctx / total_slots.
    /// </summary>
    public bool UnifiedKv { get; init; }

    /// <summary>GET /health: 200 → Ready, 503 → Loading, нет соединения → Down.</summary>
    public async Task<HealthState> GetHealthAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ShortRequestTimeout);
        try
        {
            using var req = LocalHttp.Request(HttpMethod.Get, BaseUrl + "/health", apiKey: null);
            using var resp = await LocalHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return MapHealth(resp.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return HealthState.Down;
        }
    }

    internal static HealthState MapHealth(HttpStatusCode code) => code switch
    {
        HttpStatusCode.OK => HealthState.Ready,
        HttpStatusCode.ServiceUnavailable => HealthState.Loading,
        _ => HealthState.Down,
    };

    /// <summary>GET /props (нужен ключ). null — сервер недоступен, загружается или отказал.</summary>
    public async Task<ServerProps?> GetPropsAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ShortRequestTimeout);
        try
        {
            using var req = LocalHttp.Request(HttpMethod.Get, BaseUrl + "/props", ApiKey);
            using var resp = await LocalHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Debug("llama", $"/props: HTTP {(int)resp.StatusCode}");
                return null;
            }
            // Ответ ограничен по размеру: удалённый сервер мог бы прислать бесконечный ответ.
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var json = await BoundedRead.ReadTextAsync(s, BoundedRead.MaxJsonChars, cts.Token);
            if (json is null)
            {
                Log.Debug("llama", $"/props: ответ больше {BoundedRead.MaxJsonChars / (1024 * 1024)} МБ — отброшен");
                return null;
            }
            return ParseProps(json, UnifiedKv);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            Log.Debug("llama", $"/props недоступен: {ex.Message}");
            return null;
        }
    }

    /// <param name="unifiedKv">Слоты делят общий KV-кэш: n_ctx из /props — весь буфер, на запрос приходится n_ctx / total_slots.</param>
    internal static ServerProps ParseProps(string json, bool unifiedKv = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var nCtx = 0;
        if (root.TryGetProperty("default_generation_settings", out var dgs) && dgs.ValueKind == JsonValueKind.Object)
            nCtx = ChatStreamParser.GetInt(dgs, "n_ctx") ?? 0;
        if (nCtx == 0) nCtx = ChatStreamParser.GetInt(root, "n_ctx") ?? 0;

        bool? tools = null;
        if (root.TryGetProperty("chat_template_caps", out var caps) && caps.ValueKind == JsonValueKind.Object)
            tools = GetBool(caps, "supports_tool_calls") ?? GetBool(caps, "supports_tools");

        var slots = Math.Max(1, ChatStreamParser.GetInt(root, "total_slots") ?? 1);
        var (perRequest, shared) = SplitContext(nCtx, slots, unifiedKv);
        // Строки сервера (в клиентском режиме — недоверенного) уходят в журнал, интерфейс и ответы MCP: псевдоним — только
        // безопасный id модели, путь и сборка — без управляющих символов и не длиннее 256.
        var alias = GetString(root, "model_alias");
        return new ServerProps(
            perRequest,
            slots,
            SafeText(GetString(root, "model_path")),
            RemoteServer.IsSafeModelId(alias) ? alias : null,
            SafeText(GetString(root, "build_info")))
        {
            SharedContext = shared,
            SupportsToolCalls = tools,
            IsSleeping = GetBool(root, "is_sleeping"),
            ReasoningHint = ReasoningFromTemplate(GetString(root, "chat_template")),
        };
    }

    /// <summary>Способ отключить размышления по тексту шаблона чата (Jinja); null — шаблона нет.</summary>
    internal static ReasoningControl? ReasoningFromTemplate(string? template)
    {
        if (string.IsNullOrEmpty(template)) return null;
        if (template.Contains("enable_thinking", StringComparison.Ordinal)) return ReasoningControl.EnableThinkingKwarg;
        if (template.Contains("reasoning_effort", StringComparison.Ordinal)) return ReasoningControl.ReasoningEffort;
        return ReasoningControl.None;
    }

    /// <summary>
    /// Контекст на запрос и общий буфер. Без -kvu llama-server сам делит -c на слоты и сообщает долю слота;
    /// с -kvu каждый слот «видит» весь буфер, поэтому гарантированная доля — n_ctx / slots.
    /// </summary>
    internal static (int PerRequest, int? Shared) SplitContext(int reportedCtx, int slots, bool unifiedKv)
    {
        if (!unifiedKv || slots <= 1 || reportedCtx <= 0) return (reportedCtx, null);
        return (Math.Max(1, reportedCtx / slots), reportedCtx);
    }

    /// <summary>
    /// POST /v1/chat/completions со stream=true. onDelta получает фрагменты текста ответа
    /// по мере генерации (для уведомлений о прогрессе). Итоговые токены — из поля usage/timings.
    /// Длительность ограничивает только ct. Ошибки — LlamaApiException с текстом на русском.
    /// </summary>
    public async Task<ChatResult> ChatAsync(ChatRequest request, Action<string>? onDelta = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var req = LocalHttp.Request(HttpMethod.Post, BaseUrl + "/v1/chat/completions", ApiKey);
        req.Content = JsonContent(BuildChatBody(request, Model));
        req.Headers.Accept.ParseAdd("text/event-stream");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try
        {
            resp = await LocalHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (LocalHttp.IsConnectionRefused(ex))
        {
            throw new LlamaApiException(LlamaErrorText.NotRunning(BaseUrl), null, ex)
            {
                Kind = LlamaErrorKind.NotRunning,
                Detail = $"nothing is listening on {BaseUrl} ({LocalHttp.InvariantReason(ex)})",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new LlamaApiException(L.F("Нет связи с llama-server ({0}): {1}", BaseUrl, ex.Message), null, ex)
            {
                Kind = LlamaErrorKind.ConnectionFailed,
                Detail = $"cannot connect to {BaseUrl} ({LocalHttp.InvariantReason(ex)})",
            };
        }
        catch (OperationCanceledException ex)
        {
            // Не отмена вызывающего — сработал ConnectTimeout обработчика.
            throw new LlamaApiException(L.F("Нет связи с llama-server ({0}): {1}", BaseUrl, L.T("превышено время подключения")), null, ex)
            {
                Kind = LlamaErrorKind.ConnectionFailed,
                Detail = $"timed out connecting to {BaseUrl}",
            };
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var text = await ReadLimitedAsync(resp, 64 * 1024, ct);
                throw LlamaErrorText.FromResponse((int)resp.StatusCode, text);
            }

            var parser = new ChatStreamParser(onDelta);
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (resp.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    // Сервер ответил без потока — разбираем целиком (message вместо delta), с потолком размера.
                    var body = await BoundedRead.ReadTextAsync(stream, BoundedRead.MaxChatBodyChars, ct)
                               ?? throw new InvalidDataException($"the response is larger than {BoundedRead.MaxChatBodyChars} characters"); // l10n-ignore: подробность для журнала
                    return ParseNonStreaming(body, onDelta, sw.Elapsed);
                }
                // Строка потока — с потолком длины: иначе сервер без переводов строк заставил бы копить ответ в памяти целиком.
                var lines = new BoundedLineReader(reader, BoundedRead.MaxSseLineChars);
                while (await lines.ReadLineAsync(ct) is { } line)
                {
                    if (!parser.ProcessLine(line)) break;
                }
                parser.Complete();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                throw new LlamaApiException(L.T("Ответ сервера слишком большой — он отброшен."), null, ex)
                {
                    Kind = LlamaErrorKind.Other,
                    Detail = ex.Message,
                };
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new LlamaApiException(
                    L.T("Соединение с llama-server прервалось во время генерации (сервер перезапущен или завершился с ошибкой)."), null, ex)
                {
                    Kind = LlamaErrorKind.ConnectionLost,
                    Detail = $"the connection was closed during generation ({LocalHttp.InvariantReason(ex)})",
                };
            }

            if (!parser.Done && parser.FinishReason is null)
                throw new LlamaApiException(
                    L.T("Ответ llama-server оборвался до завершения генерации (сервер перезапущен или завершился с ошибкой)."))
                {
                    Kind = LlamaErrorKind.ConnectionLost,
                    Detail = "the response stream ended before generation finished",
                };
            return parser.ToResult(sw.Elapsed);
        }
    }

    internal static byte[] BuildChatBody(ChatRequest request, string? model = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", string.IsNullOrWhiteSpace(model) ? LlamaServerArgs.DefaultAlias : model);
            w.WriteStartArray("messages");
            foreach (var m in request.Messages)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role);
                w.WriteString("content", m.Content);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("stream", true);
            w.WriteStartObject("stream_options");
            w.WriteBoolean("include_usage", true);
            w.WriteEndObject();
            if (request.MaxTokens is int max) w.WriteNumber("max_tokens", max);
            if (request.Temperature is double t) w.WriteNumber("temperature", t);
            if (request.TopP is double p) w.WriteNumber("top_p", p);
            if (request.TopK is int k) w.WriteNumber("top_k", k);
            if (request.MinP is double mp) w.WriteNumber("min_p", mp);
            if (request.RepeatPenalty is double rp) w.WriteNumber("repeat_penalty", rp);
            if (request.PresencePenalty is double pp) w.WriteNumber("presence_penalty", pp);
            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) w.WriteString("reasoning_effort", request.ReasoningEffort);
            if (request.ChatTemplateKwargs is { Count: > 0 } kwargs)
            {
                w.WritePropertyName("chat_template_kwargs");
                JsonSerializer.Serialize(w, kwargs);
            }
            if (request.Stop is { Count: > 0 } stop)
            {
                w.WriteStartArray("stop");
                foreach (var s in stop) w.WriteStringValue(s);
                w.WriteEndArray();
            }
            if (request.ResponseFormat is { } format)
            {
                w.WriteStartObject("response_format");
                w.WriteString("type", "json_schema");
                w.WriteStartObject("json_schema");
                w.WriteString("name", format.Name);
                w.WriteBoolean("strict", format.Strict);
                w.WritePropertyName("schema");
                format.Schema.WriteTo(w);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static ChatResult ParseNonStreaming(string body, Action<string>? onDelta, TimeSpan duration)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err)) throw LlamaErrorText.FromErrorJson(err, null);
        string content = "", finish = "stop";
        string? reasoning = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.TryGetProperty("message", out var msg))
            {
                content = GetString(msg, "content") ?? "";
                reasoning = GetString(msg, "reasoning_content");
            }
            finish = GetString(c0, "finish_reason") ?? "stop";
        }
        if (content.Length > 0) onDelta?.Invoke(content);
        int prompt = 0, completion = 0;
        double? pps = null, gps = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            prompt = ChatStreamParser.GetInt(u, "prompt_tokens") ?? 0;
            completion = ChatStreamParser.GetInt(u, "completion_tokens") ?? 0;
        }
        if (root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            pps = ChatStreamParser.GetDouble(t, "prompt_per_second");
            gps = ChatStreamParser.GetDouble(t, "predicted_per_second");
        }
        return new ChatResult(content, string.IsNullOrEmpty(reasoning) ? null : reasoning, finish, prompt, completion, pps, gps, duration);
    }

    /// <summary>POST /tokenize — точное число токенов текста. При ошибке — оценка (символы / 3.2).</summary>
    public async Task<int> CountTokensAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ShortRequestTimeout);
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("content", text);
                w.WriteEndObject();
            }
            using var req = LocalHttp.Request(HttpMethod.Post, BaseUrl + "/tokenize", ApiKey);
            req.Content = JsonContent(ms.ToArray());
            using var resp = await LocalHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                if (await BoundedRead.ReadTextAsync(s, (int)Math.Min(int.MaxValue / 2, BoundedRead.MaxJsonChars + (long)text.Length * 16), cts.Token) is { } body)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
                        return tokens.GetArrayLength();
                }
            }
            Log.Debug("llama", $"/tokenize: HTTP {(int)resp.StatusCode}, используется оценка");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            Log.Debug("llama", $"/tokenize недоступен ({ex.Message}), используется оценка");
        }
        return EstimateTokens(text);
    }

    /// <summary>Грубая оценка числа токенов без обращения к серверу.</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 3.2);

    private static ByteArrayContent JsonContent(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, int maxChars, CancellationToken ct)
    {
        try
        {
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var r = new StreamReader(s, Encoding.UTF8);
            var buf = new char[maxChars];
            var n = await r.ReadBlockAsync(buf.AsMemory(), ct);
            return new string(buf, 0, n);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return "";
        }
    }

    /// <summary>Строка от сервера для показа: без управляющих символов, не длиннее 256; пустая — null.</summary>
    internal static string? SafeText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var clean = new string(s.Where(c => !char.IsControl(c)).Take(256).ToArray()).Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
