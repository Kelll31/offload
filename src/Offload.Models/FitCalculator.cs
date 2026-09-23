using Offload.Core.Hardware;

namespace Offload.Models;

public enum FitLevel
{
    /// <summary>Полностью в видеопамяти — максимальная скорость.</summary>
    FullGpu,
    /// <summary>MoE: эксперты частично на ЦП (--n-cpu-moe), быстро.</summary>
    MoeOffload,
    /// <summary>Часть слоёв на ЦП — заметно медленнее.</summary>
    PartialGpu,
    /// <summary>Только процессор — медленно.</summary>
    CpuOnly,
    /// <summary>Не хватит памяти.</summary>
    TooLarge,
}

public sealed record FitResult(
    FitLevel Level,
    long WeightsBytes,
    long KvCacheBytes,
    long EstimatedVramBytes,
    long EstimatedRamBytes,
    int ContextSize,
    /// <summary>Рекомендуемое значение --n-cpu-moe (0 — не нужно).</summary>
    int CpuMoeLayers,
    /// <summary>Рекомендуемое значение -ngl (-1 — все слои).</summary>
    int GpuLayers,
    /// <summary>Пояснение на русском: «Поместится в видеопамять (≈9,1 из 24 ГБ)».</summary>
    string Explanation)
{
    public bool Usable => Level != FitLevel.TooLarge;
}

/// <summary>Оценка, поместится ли модель, и подбор контекста/выгрузки под оборудование.</summary>
public static class FitCalculator
{
    /// <summary>Резерв видеопамяти под систему/рабочий стол/буферы вычислений, байт.</summary>
    public const long VramReserveBytes = 1L * 1024 * 1024 * 1024;

    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    /// <summary>Видеокарта меньше этого объёма не используется (только процессор).</summary>
    internal const long MinUsableVramBytes = 2 * GiB;

    /// <summary>Контексты-кандидаты при автоподборе (кроме DefaultContext модели).</summary>
    private static readonly int[] AutoContexts = [65536, 32768, 16384];

    /// <summary>Минимум контекста для агентной работы (OpenCode) при автоподборе.</summary>
    public const int MinAgentContext = 16384;

    /// <summary>Абсолютный минимум контекста при автоподборе (когда иначе не помещается).</summary>
    public const int MinContext = 8192;

    /// <summary>При выгрузке на ЦП (медленно) большой контекст бессмыслен: обработка промпта займёт минуты.</summary>
    private const int MaxSlowContext = 32768;


    /// <summary>Байт на элемент KV-кэша для типа (f16 = 2, q8_0 ≈ 1.0625, q4_0 ≈ 0.5625).</summary>
    public static double BytesPerKvElement(string cacheType) => (cacheType ?? "").Trim().ToLowerInvariant() switch
    {
        "f32" => 4,
        "f16" or "bf16" or "" => 2,
        "q8_0" => 34d / 32,
        "q4_0" or "iq4_nl" => 18d / 32,
        "q4_1" => 20d / 32,
        "q5_0" => 22d / 32,
        "q5_1" => 24d / 32,
        // Неизвестный тип — считаем как f16 (с запасом).
        _ => 2,
    };

    /// <summary>KV-кэш на один токен контекста для слоёв полного внимания (K + V), байт.</summary>
    public static double KvBytesPerToken(KvSpec kv, string cacheType)
    {
        var full = kv.FullAttentionLayers > 0 && kv.FullAttentionLayers < kv.Layers ? kv.FullAttentionLayers : kv.Layers;
        return (double)full * kv.KvHeads * kv.HeadDim * 2 * BytesPerKvElement(cacheType);
    }

    /// <summary>
    /// Размер KV-кэша одного слота: слои полного внимания × контекст + слои скользящего окна × min(контекст, окно).
    /// Фиксированное рекуррентное состояние (RecurrentStateBytes) сюда не входит.
    /// </summary>
    public static long KvCacheBytes(KvSpec kv, int contextTokens, string cacheType)
    {
        if (contextTokens <= 0 || kv.Layers <= 0) return 0;
        var perLayerToken = (double)kv.KvHeads * kv.HeadDim * 2 * BytesPerKvElement(cacheType);
        var hasSwa = kv.FullAttentionLayers > 0 && kv.FullAttentionLayers < kv.Layers;
        var full = hasSwa ? kv.FullAttentionLayers : kv.Layers;
        var bytes = full * perLayerToken * contextTokens;
        if (hasSwa)
        {
            var window = kv.SlidingWindow > 0 ? Math.Min(contextTokens, kv.SlidingWindow) : contextTokens;
            bytes += (kv.Layers - full) * perLayerToken * window;
        }
        return (long)Math.Ceiling(bytes);
    }

    /// <summary>
    /// Буфер вычислений llama.cpp (логиты n_ubatch × n_vocab, промежуточные тензоры): ≈0,5–1,2 ГБ,
    /// растёт с «шириной» модели (для MoE — по активным параметрам) и немного с контекстом.
    /// </summary>
    public static long ComputeBufferBytes(CatalogModel model, long weightsBytes, int totalContextTokens)
    {
        double sizeB = model.IsMoe && model.ActiveParamsB > 0 ? model.ActiveParamsB * 3
            : model.ParamsB > 0 ? model.ParamsB
            // Неизвестно (пользовательская модель): ≈0,6 байт на параметр у 4-битных квантизаций.
            : weightsBytes / 0.6e9;
        var baseBytes = Math.Clamp(512 * MiB + (long)(20 * MiB * sizeB), 512 * MiB, 1228 * MiB);
        // Маска внимания и прочее на каждый токен контекста (≈1 КиБ при n_ubatch 512).
        return baseBytes + Math.Max(0, totalContextTokens) * 1024L;
    }

    /// <summary>Сколько ОЗУ оставить системе и программам: 25 % памяти, но от 2 до 6 ГиБ.</summary>
    public static long RamReserveBytes(HardwareInfo hw) => Math.Clamp(hw.TotalRamBytes / 4, 2 * GiB, 6 * GiB);

    /// <param name="contextSize">
    /// 0 — подобрать автоматически: наибольший из [DefaultContext, 64K, 32K, 16K], который помещается
    /// (не больше NativeContext; при нехватке памяти — до 8K).
    /// </param>
    public static FitResult Evaluate(CatalogModel model, long weightsBytes, HardwareInfo hw, int contextSize = 0, string cacheType = "q8_0", int parallel = 1)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(hw);
        var weights = weightsBytes > 0 ? weightsBytes : model.ApproxSizeBytes;
        parallel = Math.Max(1, parallel);
        cacheType = string.IsNullOrWhiteSpace(cacheType) ? "f16" : cacheType;

        var vram = hw.PrimaryVramBytes >= MinUsableVramBytes ? hw.PrimaryVramBytes : 0;
        var budget = Math.Max(0, vram - VramReserveBytes);
        var ramTotal = Math.Max(0, hw.TotalRamBytes);
        var ramBudget = Math.Max(0, ramTotal - RamReserveBytes(hw));

        var native = model.NativeContext > 0 ? model.NativeContext : int.MaxValue;
        var explicitCtx = contextSize > 0;
        int[] candidates = explicitCtx
            ? [Math.Min(contextSize, native)]
            : new[] { model.DefaultContext }.Concat(AutoContexts).Append(MinContext)
                .Where(c => c > 0 && c <= native).Distinct().OrderDescending().ToArray();
        if (candidates.Length == 0) candidates = [Math.Min(MinContext, native)];

        // Сколько памяти нужно при данном контексте (без учёта того, где она находится).
        Need NeedFor(int ctx)
        {
            var kv = KvCacheBytes(model.Kv, ctx, cacheType) * parallel;
            var state = model.Kv.RecurrentStateBytes * parallel;
            var compute = ComputeBufferBytes(model, weights, ctx * parallel);
            return new Need(ctx, kv, state, compute, weights + kv + state + compute);
        }

        var moe = model.IsMoe ? MoeFor(model, weights) : null;

        // 1. Видеокарта целиком (MoE — с выгрузкой части экспертов). При автоподборе не ниже 16K.
        if (vram > 0)
        {
            foreach (var ctx in candidates.Where(c => explicitCtx || c >= MinAgentContext))
            {
                var n = NeedFor(ctx);
                if (n.Total <= budget)
                    return Result(FitLevel.FullGpu, n, n.Total, 0, 0, -1,
                        L.F("Полностью в видеопамяти: ≈{0} из {1} ГБ, контекст {2}", Gb(n.Total), Gb(vram), Ctx(ctx)));

                if (moe is not null)
                {
                    var overflow = n.Total - budget;
                    var layers = (int)Math.Ceiling((double)overflow / moe.ExpertBytesPerLayer);
                    var cpuBytes = layers * moe.ExpertBytesPerLayer;
                    if (layers <= moe.MoeLayers && cpuBytes <= ramBudget)
                        return Result(FitLevel.MoeOffload, n, n.Total - cpuBytes, cpuBytes, layers, -1,
                            L.F("MoE: {0} {1} экспертов на ЦП (≈{2} ГБ ОЗУ), видеопамять ≈{3} из {4} ГБ, контекст {5} — быстро",
                                layers, L.PluralWord(layers, "слой", "слоя", "слоёв"), Gb(cpuBytes), Gb(n.Total - cpuBytes), Gb(vram), Ctx(ctx)));
                }
            }
        }

        var slow = explicitCtx ? candidates : candidates.Where(c => c <= Math.Max(MaxSlowContext, candidates.Min())).ToArray();

        // 2. Часть слоёв на видеокарте (для MoE это случай, когда даже без экспертов не помещается).
        if (vram > 0)
        {
            var blocks = BlockCountOf(model);
            foreach (var ctx in slow)
            {
                var n = NeedFor(ctx);
                var fixedGpu = n.Compute + n.State;
                var perLayer = (double)(weights + n.Kv) / blocks;
                var gpuLayers = (int)Math.Floor((budget - fixedGpu) / perLayer);
                if (gpuLayers < 1) break; // видеокарта ничего не даёт — только процессор
                gpuLayers = Math.Min(gpuLayers, blocks - 1);
                var gpuBytes = (long)(gpuLayers * perLayer) + fixedGpu;
                var cpuBytes = n.Total - gpuBytes;
                if (cpuBytes <= ramBudget)
                    return Result(FitLevel.PartialGpu, n, gpuBytes, cpuBytes, 0, gpuLayers,
                        L.F("Частично на видеокарте: {0} из {1} {2}, ≈{3} ГБ в ОЗУ, контекст {4} — медленно",
                            gpuLayers, blocks, L.PluralWord(blocks, "слоя", "слоёв", "слоёв"), Gb(cpuBytes), Ctx(ctx)));
            }
        }

        // 3. Только процессор.
        foreach (var ctx in slow)
        {
            var n = NeedFor(ctx);
            if (n.Total <= ramBudget)
                return Result(FitLevel.CpuOnly, n, 0, n.Total, 0, 0,
                    vram > 0
                        ? L.F("Только процессор (видеокарты мало): ≈{0} ГБ ОЗУ из {1} ГБ, контекст {2} — медленно", Gb(n.Total), Gb(ramTotal), Ctx(ctx))
                        : L.F("Только процессор: ≈{0} ГБ ОЗУ из {1} ГБ, контекст {2} — медленно", Gb(n.Total), Gb(ramTotal), Ctx(ctx)));
        }

        // 4. Не помещается даже с минимальным контекстом.
        var min = NeedFor(slow.Length > 0 ? slow.Min() : candidates.Min());
        var have = vram > 0
            ? L.F("видеопамять {0} ГБ, ОЗУ {1} ГБ", Gb(vram), Gb(ramTotal))
            : L.F("ОЗУ {0} ГБ, видеокарты нет", Gb(ramTotal));
        return Result(FitLevel.TooLarge, min, min.Total, 0, 0, 0,
            L.F("Не хватит памяти: нужно ≈{0} ГБ ({1})", Gb(min.Total), have));

        FitResult Result(FitLevel level, Need n, long vramBytes, long ramBytes, int cpuMoe, int gpuLayers, string text) =>
            new(level, weights, n.Kv, vramBytes, ramBytes, n.Ctx, cpuMoe, gpuLayers, text);
    }

    /// <summary>Лучшая модель каталога для этого оборудования (баланс качества и скорости).</summary>
    public static CatalogModel Recommend(HardwareInfo hw) => Recommend(hw, ModelCatalog.All);

    /// <summary>
    /// Основной ориентир — Qwen3.8-27B (лучшее качество среди локальных моделей для кода):
    /// Q6 на 32+ ГБ, Q4 на 24 ГБ, IQ3 на 16 ГБ. Ниже — быстрые MoE и 9B-модели.
    /// Всегда возвращает пригодную модель, если такая есть.
    /// </summary>
    internal static CatalogModel Recommend(HardwareInfo hw, IReadOnlyList<CatalogModel> catalog)
    {
        ArgumentNullException.ThrowIfNull(hw);
        if (catalog.Count == 0) throw new InvalidOperationException(L.T("Каталог моделей пуст."));

        var vramGb = hw.PrimaryVramBytes >= MinUsableVramBytes ? hw.PrimaryVramBytes / (double)GiB : 0;
        var ramGb = hw.TotalRamBytes / (double)GiB;

        // Пороги с запасом: «24 ГБ» карта сообщает ≈23,99 ГиБ, «32 ГБ» ОЗУ — ≈31,8 ГиБ.
        string[] preferred = vramGb switch
        {
            >= 30 => ["qwen3.8-27b-q6", "qwen3.8-27b-q4"],
            >= 23 => ["qwen3.8-27b-q4", "qwen3.6-35b-a3b-q4", "ornith-1.5-35b-a3b-q4"],
            >= 15 => ["qwen3.8-27b-iq3", "qwen3.6-35b-a3b-q4", "gpt-oss-20b", "ornith-1.5-9b-q4"],
            >= 11 when ramGb >= 30 => ["qwen3.6-35b-a3b-q4", "ornith-1.5-9b-q4"],
            >= 11 => ["ornith-1.5-9b-q4", "qwen3.5-9b-q4"],
            >= 7.5 => ["ornith-1.5-9b-q4", "qwen3.5-9b-q4", "qwen3.5-4b-q4"],
            >= 5.5 when ramGb >= 15 => ["ornith-1.5-9b-q4", "qwen3.5-4b-q4"],
            >= 5.5 => ["qwen3.5-4b-q4", "qwen3.5-2b-q8"],
            // Слабая видеокарта или только процессор — по объёму ОЗУ.
            _ when ramGb >= 30 => ["gpt-oss-20b", "qwen3.5-4b-q4", "qwen3.5-2b-q8"],
            _ when ramGb >= 15 => ["qwen3.5-4b-q4", "qwen3.5-2b-q8"],
            _ => ["qwen3.5-2b-q8", "qwen3.5-4b-q4"],
        };

        CatalogModel? Get(string id) => catalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        FitResult Fit(CatalogModel m) => Evaluate(m, m.ApproxSizeBytes, hw);

        foreach (var id in preferred)
        {
            if (Get(id) is { } m && Fit(m).Usable) return m;
        }

        // Запасной путь: лучшая по уровню пригодности модель, при равенстве — по приоритету каталога.
        var usable = catalog
            .Select(m => (Model: m, Fit: Fit(m)))
            .Where(x => x.Fit.Usable)
            .OrderBy(x => x.Fit.Level)
            .ThenBy(x => x.Model.Priority)
            .Select(x => x.Model)
            .FirstOrDefault();
        return usable ?? catalog.OrderBy(m => m.ApproxSizeBytes).First();
    }

    /// <summary>Раскладка MoE для файла данного размера (точная из каталога или пересчитанная пропорционально).</summary>
    internal static MoeSpec MoeFor(CatalogModel model, long weightsBytes)
    {
        if (model.Files?.FirstOrDefault(f => f.Size == weightsBytes && f.Moe is not null)?.Moe is { } exact) return exact;
        if (model.Moe is { MoeLayers: > 0, ExpertBytesPerLayer: > 0 } spec)
        {
            if (model.ApproxSizeBytes <= 0 || weightsBytes == model.ApproxSizeBytes) return spec;
            var ratio = (double)weightsBytes / model.ApproxSizeBytes;
            return new MoeSpec(spec.MoeLayers, (long)(spec.ExpertBytesPerLayer * ratio), (long)(spec.NonExpertBytes * ratio));
        }
        // Пользовательская MoE без раскладки: эксперты обычно ≈88 % весов.
        var layers = BlockCountOf(model);
        return new MoeSpec(layers, (long)(weightsBytes * 0.88 / layers), (long)(weightsBytes * 0.12));
    }

    private static int BlockCountOf(CatalogModel model) =>
        model.BlockCount > 0 ? model.BlockCount
        : model.Moe is { MoeLayers: > 0 } moe ? moe.MoeLayers
        : Math.Max(1, model.Kv.Layers);

    private sealed record Need(int Ctx, long Kv, long State, long Compute, long Total);

    /// <summary>Гигабайты (ГиБ) с одним знаком после запятой: «21,4», «24».</summary>
    internal static string Gb(long bytes) => (Math.Round(bytes / (double)GiB, 1)).ToString("0.#", L.Culture);

    /// <summary>Контекст в тысячах токенов: 65536 → «64K».</summary>
    internal static string Ctx(int tokens) =>
        tokens % 1024 == 0 ? $"{tokens / 1024}K" : (tokens / 1024d).ToString("0.#", L.Culture) + "K";

    internal static string Plural(int n, string one, string few, string many) => L.PluralWord(n, one, few, many);
}
