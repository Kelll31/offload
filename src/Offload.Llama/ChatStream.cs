using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Offload.Llama;

/// <summary>
/// Разбор потока SSE от /v1/chat/completions (stream=true).
/// Строки вида «data: {...}», завершение — «data: [DONE]»; прочие строки (пинги «:», event:, пустые) игнорируются.
/// Итоговые токены — из чанка usage (stream_options.include_usage), скорость — из timings последнего чанка.
/// </summary>
internal sealed class ChatStreamParser(Action<string>? onDelta = null)
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();

    public bool Done { get; private set; }
    public string? FinishReason { get; private set; }
    public int? UsagePromptTokens { get; private set; }
    public int? UsageCompletionTokens { get; private set; }
    public int? TimingsPromptN { get; private set; }
    public int? TimingsCacheN { get; private set; }
    public int? TimingsPredictedN { get; private set; }
    public double? PromptPerSecond { get; private set; }
    public double? PredictedPerSecond { get; private set; }
    public int ChunkCount { get; private set; }

    public string Content => _content.ToString();
    public string? Reasoning => _reasoning.Length > 0 ? _reasoning.ToString() : null;

    /// <summary>
    /// Обработать одну строку потока. false — получен [DONE], дальше читать не нужно.
    /// Обычно событие — одна строка «data: {...}»; если JSON разбит на несколько строк data:, они склеиваются
    /// (по правилам SSE — через перевод строки) до успешного разбора или пустой строки (конец события).
    /// </summary>
    /// <exception cref="LlamaApiException">Сервер прислал ошибку внутри потока.</exception>
    public bool ProcessLine(string line)
    {
        if (Done) return false;
        if (line.Length == 0)
        {
            FlushPending();
            return !Done;
        }
        if (line[0] == ':') return true;
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return true;

        var payload = line.AsSpan(5);
        if (payload.Length > 0 && payload[0] == ' ') payload = payload[1..];

        if (_pending.Length == 0)
        {
            var trimmed = payload.Trim();
            if (trimmed.Length == 0) return true;
            if (trimmed.SequenceEqual("[DONE]"))
            {
                Done = true;
                return false;
            }
            if (TryProcessJson(trimmed.ToString())) return true;
            _pending.Append(payload);
            return true;
        }

        _pending.Append('\n').Append(payload);
        if (TryProcessJson(_pending.ToString())) _pending.Clear();
        else if (_pending.Length > MaxPendingChars) _pending.Clear();
        return true;
    }

    /// <summary>Конец потока: разобрать незавершённое событие, если оно есть.</summary>
    public void Complete() => FlushPending();

    private const int MaxPendingChars = 4 * 1024 * 1024;
    private readonly StringBuilder _pending = new();

    private void FlushPending()
    {
        if (_pending.Length == 0) return;
        var text = _pending.ToString().Trim();
        _pending.Clear();
        if (text == "[DONE]")
        {
            Done = true;
            return;
        }
        TryProcessJson(text);
    }

    private bool TryProcessJson(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray()) ProcessChunk(item);
            }
            else
            {
                ProcessChunk(root);
            }
        }
        return true;
    }

    private void ProcessChunk(JsonElement chunk)
    {
        if (chunk.ValueKind != JsonValueKind.Object) return;
        ChunkCount++;

        if (chunk.TryGetProperty("error", out var err))
            throw LlamaErrorText.FromErrorJson(err, statusCode: null);

        if (chunk.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object) continue;
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var text = c.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            _content.Append(text);
                            onDelta?.Invoke(text);
                        }
                    }
                    if (delta.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String)
                        _reasoning.Append(r.GetString());
                }
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    FinishReason = fr.GetString();
            }
        }

        if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            UsagePromptTokens = GetInt(usage, "prompt_tokens") ?? UsagePromptTokens;
            UsageCompletionTokens = GetInt(usage, "completion_tokens") ?? UsageCompletionTokens;
        }

        if (chunk.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            TimingsPromptN = GetInt(t, "prompt_n") ?? TimingsPromptN;
            TimingsCacheN = GetInt(t, "cache_n") ?? TimingsCacheN;
            TimingsPredictedN = GetInt(t, "predicted_n") ?? TimingsPredictedN;
            PromptPerSecond = GetDouble(t, "prompt_per_second") ?? PromptPerSecond;
            PredictedPerSecond = GetDouble(t, "predicted_per_second") ?? PredictedPerSecond;
        }
    }

    public ChatResult ToResult(TimeSpan duration)
    {
        var prompt = UsagePromptTokens ?? (TimingsPromptN is int pn ? pn + (TimingsCacheN ?? 0) : 0);
        var completion = UsageCompletionTokens ?? TimingsPredictedN ?? 0;
        return new ChatResult(
            Content,
            Reasoning,
            FinishReason ?? "stop",
            prompt,
            completion,
            Positive(PromptPerSecond),
            Positive(PredictedPerSecond),
            duration);
    }

    private static double? Positive(double? v) => v is > 0 and < double.PositiveInfinity ? v : null;

    internal static int? GetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out var i)) return i;
            if (v.TryGetDouble(out var d)) return (int)Math.Round(d);
        }
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }

    internal static double? GetDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}

/// <summary>Русские тексты ошибок API llama-server.</summary>
internal static class LlamaErrorText
{
    public static string Unauthorized =>
        L.T("Неверный ключ API: llama-server отклонил запрос (401). Перезапустите сервер из Offload, чтобы ключи совпали.");

    public static string Loading => L.T("Модель загружается — повторите запрос через несколько секунд.");

    public static string NotRunning(string baseUrl) =>
        L.F("Сервер не запущен: нет соединения с {0}. Запустите сервер в Offload.", baseUrl);

    /// <summary>Ошибка по HTTP-коду и телу ответа ({"error":{...}}).</summary>
    public static LlamaApiException FromResponse(int status, string? body)
    {
        if (status == 401) return new LlamaApiException(Unauthorized, status);
        if (status == 503) return new LlamaApiException(Loading, status);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err))
                    return FromErrorJson(err, status);
            }
            catch (JsonException)
            {
                // Не JSON — покажем как есть.
            }
        }
        var tail = string.IsNullOrWhiteSpace(body) ? "" : ": " + Shorten(body.Trim(), 300);
        return new LlamaApiException(L.F("llama-server вернул ошибку {0}{1}", status, tail), status);
    }

    public static LlamaApiException FromErrorJson(JsonElement err, int? statusCode)
    {
        string? message = null, type = null;
        int? code = statusCode, nPrompt = null, nCtx = null;
        if (err.ValueKind == JsonValueKind.Object)
        {
            if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
            if (err.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString();
            code ??= ChatStreamParser.GetInt(err, "code");
            nPrompt = ChatStreamParser.GetInt(err, "n_prompt_tokens");
            nCtx = ChatStreamParser.GetInt(err, "n_ctx");
        }
        else if (err.ValueKind == JsonValueKind.String)
        {
            message = err.GetString();
        }

        if (code == 401 || type == "authentication_error") return new LlamaApiException(Unauthorized, 401);
        if (code == 503 || type == "unavailable_error") return new LlamaApiException(Loading, 503);
        if (type == "exceed_context_size_error" || (message?.Contains("context size", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            var detail = nPrompt is not null && nCtx is not null ? " " + L.F("({0} токенов при контексте {1})", nPrompt, nCtx) : "";
            return new LlamaApiException(
                L.F("Запрос не помещается в контекст модели{0}. Сократите объём передаваемых файлов или увеличьте размер контекста в настройках.", detail),
                code ?? 400);
        }
        var text = string.IsNullOrWhiteSpace(message) ? L.T("неизвестная ошибка") : Shorten(message!, 500);
        return code is >= 500
            ? new LlamaApiException(L.F("Внутренняя ошибка llama-server: {0}", text), code)
            : new LlamaApiException(L.F("llama-server отклонил запрос: {0}", text), code);
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
