using System.Diagnostics;
using System.Text.Json;
using Offload.Core.Config;

namespace Offload.Llama;

/// <summary>Эмбеддинги и реранк (вспомогательные серверы ролей embed/rerank, ROADMAP §6.3).</summary>
public sealed partial class LlamaClient
{
    /// <summary>Потолок одного запроса эмбеддингов/реранка, если вызывающий не отменил раньше.</summary>
    internal static TimeSpan VectorRequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Клиент сервера роли из настроек: адрес роли, общий ключ, псевдоним роли.</summary>
    public static LlamaClient ForRole(AppConfig cfg, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (role == ModelRole.Quality) return FromConfig(cfg);
        return new LlamaClient(cfg.RoleBaseUrl(role), cfg.Server.ApiKey) { Model = AuxServerArgs.AliasFor(role) };
    }

    /// <summary>
    /// POST /v1/embeddings (OpenAI): векторы в порядке <paramref name="inputs"/>. Сервер должен быть запущен с --embeddings.
    /// Ошибки — LlamaApiException (как у ChatAsync).
    /// </summary>
    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return new EmbeddingResult([], 0, TimeSpan.Zero);
        var body = WriteJson(w =>
        {
            w.WriteString("model", Model);
            w.WriteStartArray("input");
            foreach (var text in inputs) w.WriteStringValue(text ?? "");
            w.WriteEndArray();
            w.WriteString("encoding_format", "float");
        });
        var sw = Stopwatch.StartNew();
        var json = await PostJsonAsync("/v1/embeddings", body, ct).ConfigureAwait(false);
        return ParseEmbeddings(json, inputs.Count, sw.Elapsed);
    }

    /// <summary>
    /// POST /v1/rerank (формат Jina/Cohere, который реализует llama-server с --reranking): оценки документов по убыванию
    /// релевантности. <paramref name="topN"/> — сколько лучших вернуть (null — все).
    /// </summary>
    public async Task<RerankResult> RerankAsync(string query, IReadOnlyList<string> documents, int? topN = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0) return new RerankResult([], 0, TimeSpan.Zero);
        var body = WriteJson(w =>
        {
            w.WriteString("model", Model);
            w.WriteString("query", query);
            w.WriteStartArray("documents");
            foreach (var d in documents) w.WriteStringValue(d ?? "");
            w.WriteEndArray();
            if (topN is int n) w.WriteNumber("top_n", Math.Max(1, n));
        });
        var sw = Stopwatch.StartNew();
        var json = await PostJsonAsync("/v1/rerank", body, ct).ConfigureAwait(false);
        return ParseRerank(json, documents.Count, sw.Elapsed);
    }

    /// <summary>Разбор ответа /v1/embeddings: data[].embedding по полю index (если его нет — по порядку).</summary>
    internal static EmbeddingResult ParseEmbeddings(string json, int expected, TimeSpan duration)
    {
        using var doc = ParseBody(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err)) throw LlamaErrorText.FromErrorJson(err, null);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw Malformed("embeddings: no data array");

        var vectors = new float[expected][];
        var position = 0;
        foreach (var item in data.EnumerateArray())
        {
            var index = item.ValueKind == JsonValueKind.Object ? ChatStreamParser.GetInt(item, "index") ?? position : position;
            position++;
            if (index < 0 || index >= expected) throw Malformed($"embeddings: index {index} out of range");
            if (!item.TryGetProperty("embedding", out var emb) || emb.ValueKind != JsonValueKind.Array)
                throw Malformed("embeddings: item without embedding array");
            var vector = new float[emb.GetArrayLength()];
            var i = 0;
            foreach (var v in emb.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Number) throw Malformed("embeddings: non-numeric value");
                vector[i++] = v.GetSingle();
            }
            vectors[index] = vector;
        }
        if (vectors.Any(v => v is null)) throw Malformed($"embeddings: expected {expected} vectors, got {position}");
        return new EmbeddingResult(vectors, UsagePromptTokens(root), duration);
    }

    /// <summary>Разбор ответа /v1/rerank: results[] с index и relevance_score, по убыванию оценки.</summary>
    internal static RerankResult ParseRerank(string json, int documentCount, TimeSpan duration)
    {
        using var doc = ParseBody(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err)) throw LlamaErrorText.FromErrorJson(err, null);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw Malformed("rerank: no results array");

        var scores = new List<RerankScore>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var index = ChatStreamParser.GetInt(item, "index");
            var score = ChatStreamParser.GetDouble(item, "relevance_score") ?? ChatStreamParser.GetDouble(item, "score");
            if (index is not int i || i < 0 || i >= documentCount || score is not double s || !double.IsFinite(s))
                throw Malformed("rerank: result without valid index/relevance_score");
            scores.Add(new RerankScore(i, s));
        }
        return new RerankResult([.. scores.OrderByDescending(x => x.Score).ThenBy(x => x.Index)], UsagePromptTokens(root), duration);
    }

    private static int UsagePromptTokens(JsonElement root) =>
        root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? ChatStreamParser.GetInt(u, "prompt_tokens") ?? 0 : 0;

    private static JsonDocument ParseBody(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new LlamaApiException(L.T("llama-server прислал ответ, который не удалось разобрать."), null, ex)
            {
                Kind = LlamaErrorKind.Other,
                Detail = "the server response is not valid JSON",
            };
        }
    }

    private static LlamaApiException Malformed(string detail) =>
        new(L.T("llama-server прислал ответ, который не удалось разобрать."))
        {
            Kind = LlamaErrorKind.Other,
            Detail = "unexpected response format (" + detail + ")",
        };

    private static byte[] WriteJson(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>POST JSON с ключом, ответ целиком. Сетевые ошибки и коды HTTP → LlamaApiException, как в ChatAsync.</summary>
    private async Task<string> PostJsonAsync(string path, byte[] body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(VectorRequestTimeout);
        using var req = LocalHttp.Request(HttpMethod.Post, BaseUrl + path, ApiKey);
        req.Content = JsonContent(body);
        try
        {
            using var resp = await LocalHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await ReadLimitedAsync(resp, 64 * 1024, cts.Token).ConfigureAwait(false);
                throw LlamaErrorText.FromResponse((int)resp.StatusCode, text);
            }
            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
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
            throw new LlamaApiException(L.F("Нет связи с llama-server ({0}): {1}", BaseUrl, L.T("превышено время ожидания ответа")), null, ex)
            {
                Kind = LlamaErrorKind.ConnectionFailed,
                Detail = $"timed out waiting for {BaseUrl}{path}",
            };
        }
    }
}
