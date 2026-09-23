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
                ChatMessage.System("Ты — помощник программиста. Отвечай очень кратко, без пояснений."), // l10n-ignore
                ChatMessage.User("Напиши на Python однострочную функцию add(a, b), возвращающую сумму."), // l10n-ignore
            ],
            MaxTokens: 160,
            Temperature: 0.2);
        var r = await client.ChatAsync(request, null, ct);
        var answer = (string.IsNullOrWhiteSpace(r.Content) ? r.Reasoning ?? "" : r.Content).Trim();
        if (answer.Length == 0) answer = L.T("(пустой ответ)");
        return new Result(answer, r.GenerationTokensPerSecond, r.Duration, r.CompletionTokens);
    }

    public static string Describe(Result r)
    {
        var text = r.Answer.Replace("\r", "").Replace("\n", " ⏎ ");
        if (text.Length > 240) text = text[..239] + "…";
        var speed = r.TokensPerSecond is double tps ? L.F("{0:0.0} ток/с", tps) : L.T("скорость неизвестна");
        return L.F("Ответ модели: «{0}» · {1} · {2:0.0} с", text, speed, r.Duration.TotalSeconds);
    }
}
