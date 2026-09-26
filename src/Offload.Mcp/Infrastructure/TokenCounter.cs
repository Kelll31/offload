using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Offload.Core.Logging;
using Offload.Llama;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Точный подсчёт токенов токенизатором модели (llama-server /tokenize) с кэшем и калибровка эвристики <see cref="Tokens.Estimate"/>.
/// </summary>
/// <remarks>
/// Весь учёт (прочитано, написано, ответ) ведётся быстрой эвристикой — считать /tokenize каждый файл дорого. После вызова
/// с моделью образец материала (самый объёмный запрос, до <see cref="MaxSampleChars"/> символов) считается точно, и отношение
/// «точно / эвристика» (с поправкой на модель, скользящее среднее) масштабирует итог экономии. Детерминированные инструменты
/// сервер не трогают — у них только эвристика (без задержки). Если /tokenize недоступен — поправки нет (коэффициент 1).
/// Токенизатор локальной модели — приближение к облачному: BPE-словари современных моделей близки по плотности на коде.
/// </remarks>
internal static class TokenCounter
{
    public const int MaxSampleChars = 24_000;

    /// <summary>Меньший образец не калибрует: слишком шумно.</summary>
    private const int MinSampleTokens = 64;

    private const int CacheLimit = 256;
    private const double MinRatio = 0.25;
    private const double MaxRatio = 4.0;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, int> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();
    private static readonly ConcurrentDictionary<string, double> Ratios = new(StringComparer.Ordinal);

    /// <summary>Точное число токенов (с кэшем) или null, если сервер не посчитал.</summary>
    public static async Task<int?> CountExactAsync(LlamaClient client, string modelKey, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var key = modelKey + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        var n = await client.CountTokensAsync(text, ct).ConfigureAwait(false);
        // При недоступном /tokenize LlamaClient возвращает свою оценку «символы / 3,2»: её за точный счёт не принимаем.
        if (n <= 0 || n == LlamaClient.EstimateTokens(text)) return null;
        lock (CacheLock)
        {
            if (Cache.TryAdd(key, n))
            {
                CacheOrder.Enqueue(key);
                while (CacheOrder.Count > CacheLimit) Cache.Remove(CacheOrder.Dequeue());
            }
        }
        return n;
    }

    /// <summary>
    /// Откалибровать эвристику на образце и вернуть поправку для модели (null — не удалось и раньше не было).
    /// Не бросает исключений, кроме отмены вызывающим.
    /// </summary>
    public static async Task<double?> CalibrateAsync(LlamaClient client, string modelKey, string? sample, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sample)) return RatioFor(modelKey);
        var heuristic = Tokens.Estimate(sample);
        if (heuristic < MinSampleTokens) return RatioFor(modelKey);
        try
        {
            if (await CountExactAsync(client, modelKey, sample, ct).ConfigureAwait(false) is not int exact) return RatioFor(modelKey);
            var ratio = Math.Clamp(exact / (double)heuristic, MinRatio, MaxRatio);
            return Ratios.AddOrUpdate(modelKey, ratio, (_, old) => old * 0.7 + ratio * 0.3);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("mcp", $"Калибровка токенов не удалась: {ex.Message}");
            return RatioFor(modelKey);
        }
    }

    /// <summary>Уже известная поправка для модели (без обращения к серверу).</summary>
    public static double? RatioFor(string modelKey) => Ratios.TryGetValue(modelKey, out var r) ? r : null;

    /// <summary>Применить поправку к оценке.</summary>
    public static long Scale(long heuristicTokens, double? ratio) =>
        ratio is double r ? (long)Math.Round(heuristicTokens * r) : heuristicTokens;

    /// <summary>Для тестов: сбросить кэш и калибровки.</summary>
    internal static void Reset()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            CacheOrder.Clear();
        }
        Ratios.Clear();
    }
}
