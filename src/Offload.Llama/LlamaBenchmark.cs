using System.Text;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.Llama;

/// <summary>Результат замера скорости: обработка промпта и генерация, токенов/с.</summary>
public sealed record BenchmarkResult(
    double PromptTokensPerSecond,
    double GenerationTokensPerSecond,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Duration);

/// <summary>
/// Встроенный замер скорости работающего llama-server: короткий прогрев и один фиксированный запрос через ChatAsync
/// (скорости — из timings ответа). Промпт начинается с уникальной строки, чтобы кэш промпта не завышал результат.
/// Перебор флагов (--n-cpu-moe, -ub/-b, тип кэша, MTP) на основе этого замера — <see cref="LlamaAutoTune"/>.
/// </summary>
public static class LlamaBenchmark
{
    /// <summary>Сколько токенов генерировать: достаточно для устойчивой оценки и быстро даже на процессоре.</summary>
    public const int GenerationTokens = 160;

    /// <summary>Сколько раз повторить фрагмент кода в промпте (≈1500 токенов — заметная обработка промпта).</summary>
    private const int PromptRepeats = 12;

    private const string Snippet = """
        public static int BinarySearch(int[] items, int value)
        {
            var lo = 0;
            var hi = items.Length - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (items[mid] == value) return mid;
                if (items[mid] < value) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }

        """;

    /// <summary>Текст запроса замера (фиксированный, кроме строки-метки в начале).</summary>
    internal static string BuildPrompt(string nonce)
    {
        var sb = new StringBuilder();
        sb.Append("Benchmark run ").Append(nonce).Append(".\n\n```csharp\n"); // l10n-ignore: запрос к модели
        for (var i = 0; i < PromptRepeats; i++) sb.Append(Snippet);
        sb.Append("```\n\nExplain in detail, step by step, what this code does, its complexity and possible edge cases."); // l10n-ignore: запрос к модели
        return sb.ToString();
    }

    /// <summary>Прогреть сервер и замерить скорость. Нужен запущенный и готовый сервер.</summary>
    /// <exception cref="LlamaApiException">Сервер недоступен или вернул ошибку.</exception>
    /// <exception cref="InvalidOperationException">Сервер не сообщил скорость (старая сборка llama.cpp).</exception>
    public static async Task<BenchmarkResult> RunAsync(LlamaClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        // Прогрев: первая генерация после загрузки медленнее (инициализация ядер, графов CUDA).
        await client.ChatAsync(new ChatRequest([ChatMessage.User("Hi")], MaxTokens: 1, Temperature: 0), ct: ct).ConfigureAwait(false); // l10n-ignore: запрос к модели

        var prompt = BuildPrompt(Guid.NewGuid().ToString("N"));
        var r = await client.ChatAsync(new ChatRequest([ChatMessage.User(prompt)], MaxTokens: GenerationTokens, Temperature: 0), ct: ct)
            .ConfigureAwait(false);
        if (r.GenerationTokensPerSecond is not { } gen || gen <= 0 || r.PromptTokensPerSecond is not { } pp || pp <= 0)
            throw new InvalidOperationException(L.T("llama-server не сообщил скорость обработки (timings) — обновите llama.cpp."));
        Log.Info("llama", $"Замер скорости: промпт {pp:0.0} ток/с ({r.PromptTokens} ток.), генерация {gen:0.0} ток/с ({r.CompletionTokens} ток.)");
        return new BenchmarkResult(pp, gen, r.PromptTokens, r.CompletionTokens, r.Duration);
    }

    /// <summary>Запись для конфига: результат и параметры запуска, при которых он получен.</summary>
    public static BenchmarkRecord ToRecord(BenchmarkResult result, ServerLaunchPlan? plan, string? llamaTag, DateTime measuredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new BenchmarkRecord
        {
            MeasuredAtUtc = measuredAtUtc,
            PromptTokensPerSecond = Math.Round(result.PromptTokensPerSecond, 1),
            GenerationTokensPerSecond = Math.Round(result.GenerationTokensPerSecond, 1),
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            ContextSize = plan?.ContextSize ?? 0,
            Parallel = plan?.Parallel ?? 0,
            CpuMoeLayers = plan?.CpuMoeLayers ?? -1,
            LlamaTag = llamaTag,
        };
    }

    /// <summary>Замерить и сохранить результат в конфиг (Server.LastBenchmark[modelId]).</summary>
    public static async Task<BenchmarkRecord> MeasureAndSaveAsync(LlamaClient client, string modelId, ServerLaunchPlan? plan, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var result = await RunAsync(client, ct).ConfigureAwait(false);
        var record = ToRecord(result, plan, ConfigStore.Current.Llama.InstalledTag, DateTime.UtcNow);
        ConfigStore.Update(c =>
        {
            c.Server.LastBenchmark ??= [];
            c.Server.LastBenchmark[modelId] = record;
        });
        return record;
    }

    /// <summary>Последний замер для модели или null.</summary>
    public static BenchmarkRecord? Last(AppConfig cfg, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return modelId is not null && cfg.Server.LastBenchmark is { } all && all.TryGetValue(modelId, out var r) ? r : null;
    }
}
