using Offload.Core.Config;
using Offload.Llama;

namespace Offload.App.Services;

/// <summary>Короткий тестовый запрос к локальной модели (кнопка «Проверить модель» и шаг мастера).</summary>
internal static class ModelCheck
{
    public sealed record Result(string Answer, double? TokensPerSecond, TimeSpan Duration, int CompletionTokens);

    public static async Task<Result> RunAsync(AppConfig cfg, CancellationToken ct = default)
    {
        var client = LlamaClient.FromConfig(cfg);
        var request = new ChatRequest(
            [
                ChatMessage.System("Ты — помощник программиста. Отвечай очень кратко, без пояснений."),
                ChatMessage.User("Напиши на Python однострочную функцию add(a, b), возвращающую сумму."),
            ],
            MaxTokens: 160,
            Temperature: 0.2);
        var r = await client.ChatAsync(request, null, ct);
        var answer = (string.IsNullOrWhiteSpace(r.Content) ? r.Reasoning ?? "" : r.Content).Trim();
        if (answer.Length == 0) answer = "(пустой ответ)";
        return new Result(answer, r.GenerationTokensPerSecond, r.Duration, r.CompletionTokens);
    }

    public static string Describe(Result r)
    {
        var text = r.Answer.Replace("\r", "").Replace("\n", " ⏎ ");
        if (text.Length > 240) text = text[..239] + "…";
        var speed = r.TokensPerSecond is double tps ? $"{tps:0.0} ток/с" : "скорость неизвестна";
        return $"Ответ модели: «{text}» · {speed} · {r.Duration.TotalSeconds:0.0} с";
    }
}
