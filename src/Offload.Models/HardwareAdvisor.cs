using System.Globalization;
using System.Text.RegularExpressions;
using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models;

/// <summary>Модель каталога с квантом, который лучше всего подходит этому компьютеру, и оценка размещения.</summary>
/// <param name="Score">Условная оценка «качество × скорость × запас контекста» (сравнивать можно только внутри одного расчёта).</param>
/// <param name="Why">Короткое объяснение выбора на языке интерфейса.</param>
public sealed record ModelPick(CatalogModel Model, string Quant, long WeightsBytes, FitResult Fit, double Score, string Why);

/// <summary>Рекомендуемые параметры сервера для выбранной модели.</summary>
/// <param name="ContextPerSlot">Контекст одного слота.</param>
/// <param name="Parallel">Число слотов.</param>
/// <param name="CacheType">Тип KV-кэша: f16, q8_0 или q4_0.</param>
public sealed record ParamAdvice(int ContextPerSlot, int Parallel, string CacheType, FitResult Fit, string Summary);

/// <summary>Совет по оборудованию: лучшая модель, запасные варианты, быстрая модель для коротких задач и параметры сервера.</summary>
/// <param name="Fingerprint">Отпечаток оборудования: по его смене Offload пересчитывает совет.</param>
public sealed record HardwareAdvice(string Fingerprint, ModelPick? Best, IReadOnlyList<ModelPick> Alternatives, ModelPick? Fast, ParamAdvice? Params);

/// <summary>
/// Подбор модели и параметров под оборудование по данным, а не по зашитому списку: для каждой модели каталога и каждого кванта
/// считается размещение (<see cref="FitCalculator"/>), затем оценка «качество × скорость размещения × запас контекста × вызов
/// инструментов × порядок каталога». Недоступные модели (не помещаются) и контекст ниже агентского минимума отсеиваются.
/// Расчёт чистый и быстрый (миллисекунды), поэтому выполняется при запуске и при смене оборудования.
/// </summary>
public static partial class HardwareAdvisor
{
    /// <summary>Контекст, с которого модель годится агенту (как <c>FitCalculator.MinAgentContext</c>).</summary>
    public const int MinAgentContext = 16384;

    /// <summary>Контекст, выше которого запас уже не добавляет баллов.</summary>
    private const int ComfortContext = 32768;

    private const int MaxAlternatives = 3;

    /// <summary>
    /// Отпечаток оборудования: видеокарты (название и видеопамять в ГиБ), ОЗУ с округлением до 2 ГиБ, число потоков ЦП.
    /// Драйверы и свободная память не входят — иначе совет пересчитывался бы без причины.
    /// </summary>
    public static string Fingerprint(HardwareInfo hw)
    {
        ArgumentNullException.ThrowIfNull(hw);
        var gpus = hw.Gpus
            .Where(g => !g.IsIntegrated)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Name.Trim()}:{Math.Round(g.DedicatedMemoryBytes / 1073741824d)}");
        var ram = (long)Math.Round(hw.TotalRamGb / 2) * 2;
        return string.Create(CultureInfo.InvariantCulture, $"gpu={string.Join('+', gpus)};ram={ram};cpu={hw.LogicalCores}");
    }

    /// <summary>Совет для оборудования по моделям каталога (<paramref name="server"/> — настройки сервера для числа слотов и KV-кэша).</summary>
    public static HardwareAdvice Advise(HardwareInfo hw, IReadOnlyList<CatalogModel> catalog, ServerSettings? server = null)
    {
        ArgumentNullException.ThrowIfNull(hw);
        ArgumentNullException.ThrowIfNull(catalog);
        var chat = catalog.Where(m => m.IsChat).ToList();
        var fingerprint = Fingerprint(hw);
        var maxPriority = Math.Max(1, chat.Count == 0 ? 1 : chat.Max(m => m.Priority) - chat.Min(m => m.Priority));
        var minPriority = chat.Count == 0 ? 0 : chat.Min(m => m.Priority);

        var picks = chat
            .Select(m => BestQuant(m, hw, maxPriority, minPriority))
            .Where(p => p is not null)
            .Select(p => p!)
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Model.Priority)
            .ToList();

        // Агенту нужен контекст не меньше минимального; если ни одна модель его не даёт — лучшая из имеющихся.
        var agentReady = picks.Where(p => p.Fit.ContextSize >= MinAgentContext).ToList();
        var ranked = agentReady.Count > 0 ? agentReady : picks;
        var best = ranked.FirstOrDefault();
        var alternatives = ranked.Skip(1).Take(MaxAlternatives).ToList();
        var fast = best is null ? null : FastPick(hw, picks, best);
        var param = best is null ? null : Parameters(best, hw, server ?? new ServerSettings());
        return new HardwareAdvice(fingerprint, best, alternatives, fast, param);
    }

    /// <summary>
    /// Оценка одной модели (её лучший квант) на этом оборудовании теми же весами, что и в <see cref="Advise"/>;
    /// null — модель не помещается. Нужна, чтобы сравнить активную модель с подобранной.
    /// </summary>
    public static ModelPick? PickFor(CatalogModel model, HardwareInfo hw, IReadOnlyList<CatalogModel> catalog)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(hw);
        ArgumentNullException.ThrowIfNull(catalog);
        var chat = catalog.Where(m => m.IsChat).ToList();
        var min = chat.Count == 0 ? model.Priority : Math.Min(chat.Min(m => m.Priority), model.Priority);
        var max = chat.Count == 0 ? model.Priority : Math.Max(chat.Max(m => m.Priority), model.Priority);
        return BestQuant(model, hw, Math.Max(1, max - min), min);
    }

    /// <summary>Лучший квант одной модели: наибольшая оценка среди квантов, которые помещаются.</summary>
    private static ModelPick? BestQuant(CatalogModel m, HardwareInfo hw, int priorityRange, int minPriority)
    {
        ModelPick? best = null;
        foreach (var quant in m.Quants)
        {
            var size = SizeOf(m, quant);
            if (size <= 0) continue;
            var fit = FitCalculator.Evaluate(m, size, hw);
            if (!fit.Usable) continue;
            var score = Score(m, quant, fit, priorityRange, minPriority);
            if (best is null || score > best.Score)
                best = new ModelPick(m, quant, size, fit, score, Reason(m, fit));
        }
        return best;
    }

    private static long SizeOf(CatalogModel m, string quant)
    {
        var file = m.Files?.FirstOrDefault(f => string.Equals(f.Quant, quant, StringComparison.OrdinalIgnoreCase));
        if (file is { TotalSize: > 0 }) return file.TotalSize;
        return m.Quants.Count > 0 && string.Equals(m.Quants[0], quant, StringComparison.OrdinalIgnoreCase) ? m.ApproxSizeBytes : 0;
    }

    /// <summary>Оценка: качество (с убывающей отдачей от числа параметров) × потеря от кванта × скорость размещения × контекст × инструменты × порядок каталога.</summary>
    internal static double Score(CatalogModel m, string quant, FitResult fit, int priorityRange, int minPriority)
    {
        var effective = EffectiveParamsB(m);
        var quality = Math.Sqrt(effective) * QuantFactor(quant);
        // Скорость: оценка токенов/с (полоса памяти ÷ читаемые за токен веса); от ≈25 ток/с модель считается комфортной.
        var speed = Math.Pow(Math.Min(1.0, EstimateTokensPerSecond(m, fit) / ComfortTokensPerSecond), 0.6);
        // Разбор длинного промпта и стабильность хуже там, где часть модели вне видеопамяти (ток/с этого не видит).
        speed *= fit.Level switch
        {
            FitLevel.FullGpu => 1.0,
            FitLevel.MoeOffload => 0.95,
            FitLevel.PartialGpu => 0.85,
            _ => 0.8,
        };
        var context = 0.7 + 0.3 * Math.Min(1.0, fit.ContextSize / (double)ComfortContext);
        var tools = m.GoodToolCalling ? 1.15 : 1.0;
        // Порядок каталога — мнение мейнтейнера о качестве: до ±10 % к оценке.
        var curated = 1.1 - 0.2 * ((m.Priority - minPriority) / (double)priorityRange);
        return quality * speed * context * tools * curated;
    }

    /// <summary>Скорость генерации, ток/с, ниже которой оценка падает (выше — не важна).</summary>
    private const double ComfortTokensPerSecond = 25;

    /// <summary>Условная полоса памяти видеокарты (ГБ/с): среднее по дискретным картам, точность порядка.</summary>
    private const double GpuBandwidthGBps = 400;

    /// <summary>Условная полоса оперативной памяти (ГБ/с) для двухканальной DDR4/DDR5.</summary>
    private const double CpuBandwidthGBps = 40;

    /// <summary>
    /// Оценка скорости генерации: за токен читаются «активные» веса (у MoE — доля активных параметров), часть с видеокарты,
    /// часть из ОЗУ (эксперты MoE на ЦП, слои вне видеопамяти). Порядок величины, а не замер: для сравнения вариантов.
    /// </summary>
    internal static double EstimateTokensPerSecond(CatalogModel m, FitResult fit)
    {
        var activeGb = fit.WeightsBytes / 1073741824d;
        if (m.IsMoe && m.ParamsB > 0 && m.ActiveParamsB > 0) activeGb *= Math.Min(1.0, m.ActiveParamsB / m.ParamsB);
        activeGb = Math.Max(activeGb, 0.2);
        var layers = Math.Max(1, m.BlockCount > 0 ? m.BlockCount : m.Kv.Layers);
        var cpuShare = fit.Level switch
        {
            FitLevel.FullGpu => 0.0,
            // Эксперты на ЦП — основная часть читаемых весов MoE.
            FitLevel.MoeOffload => Math.Clamp(fit.CpuMoeLayers / (double)layers, 0, 1) * 0.9,
            FitLevel.PartialGpu => fit.GpuLayers < 0 ? 0.0 : 1.0 - Math.Clamp(fit.GpuLayers / (double)layers, 0, 1),
            _ => 1.0,
        };
        var secondsPerToken = (1 - cpuShare) * activeGb / GpuBandwidthGBps + cpuShare * activeGb / CpuBandwidthGBps;
        return 1 / Math.Max(secondsPerToken, 1e-6);
    }

    /// <summary>Сопоставимое «число параметров»: dense — как есть; MoE — среднее геометрическое активных и всех (знания в экспертах, скорость от активных).</summary>
    internal static double EffectiveParamsB(CatalogModel m)
    {
        var total = m.ParamsB > 0 ? m.ParamsB : Math.Max(0.5, m.ApproxSizeBytes / 1e9 * 1.8); // размер файла Q4 ≈ 0,55 ГБ на млрд
        if (!m.IsMoe) return total;
        var active = m.ActiveParamsB > 0 ? m.ActiveParamsB : total * 0.15;
        return Math.Sqrt(Math.Min(active, total) * total);
    }

    /// <summary>Потеря качества от кванта по числу бит в метке (Q4_K_M, UD-Q4_K_XL, IQ3_XXS, Q8_0, BF16…).</summary>
    internal static double QuantFactor(string quant)
    {
        var q = quant ?? "";
        if (q.Contains("BF16", StringComparison.OrdinalIgnoreCase) || q.Contains("F16", StringComparison.OrdinalIgnoreCase) || q.Contains("F32", StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (q.Contains("MXFP4", StringComparison.OrdinalIgnoreCase)) return 0.93;
        var m = BitsRegex().Match(q);
        var bits = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 4;
        return bits switch
        {
            >= 8 => 1.0,
            7 => 0.995,
            6 => 0.99,
            5 => 0.97,
            4 => 0.93,
            3 => 0.84,
            _ => 0.65,
        };
    }

    private static string Reason(CatalogModel m, FitResult fit)
    {
        var parts = new List<string>
        {
            fit.Level switch
            {
                FitLevel.FullGpu => L.T("целиком в видеопамяти"),
                FitLevel.MoeOffload => L.T("эксперты MoE частично на процессоре"),
                FitLevel.PartialGpu => L.T("часть слоёв на процессоре"),
                _ => L.T("только на процессоре"),
            },
            L.F("контекст {0}", FitCalculator.Ctx(fit.ContextSize)),
        };
        if (m.GoodToolCalling) parts.Add(L.T("надёжно вызывает инструменты"));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Быстрая модель для коротких задач: маленькая (не больше трети лучшей по параметрам), целиком в видеопамяти
    /// рядом с лучшей, с контекстом не меньше 8K. null — рядом места нет.
    /// </summary>
    private static ModelPick? FastPick(HardwareInfo hw, List<ModelPick> picks, ModelPick best)
    {
        if (best.Fit.Level is not (FitLevel.FullGpu or FitLevel.MoeOffload)) return null;
        var limit = EffectiveParamsB(best.Model) / 3;
        foreach (var p in picks.Where(p => p.Model.Id != best.Model.Id && EffectiveParamsB(p.Model) <= limit).OrderByDescending(p => p.Score))
        {
            var beside = FitCalculator.Evaluate(p.Model, p.WeightsBytes, hw, 0, "q8_0", 1, maxContext: 16384, otherVramBytes: best.Fit.EstimatedVramBytes);
            if (beside.Level == FitLevel.FullGpu && beside.ContextSize >= 8192)
                return p with { Fit = beside, Why = L.F("рядом с основной: контекст {0}, целиком в видеопамяти", FitCalculator.Ctx(beside.ContextSize)) };
        }
        return null;
    }

    /// <summary>
    /// Параметры сервера: контекст — наибольший подходящий (не выше рекомендации модели), число слотов — как у «Сервера»
    /// (<see cref="ServerFit.RecommendSlots"/>), KV-кэш q8_0, а если он не даёт модели её рекомендованного контекста —
    /// q4_0 (вдвое меньше памяти), когда это помогает.
    /// </summary>
    private static ParamAdvice Parameters(ModelPick best, HardwareInfo hw, ServerSettings server)
    {
        var m = best.Model;
        var fm = new FitModel(m, best.WeightsBytes, m.DefaultContext);
        var settings = new ServerSettings { CacheType = "q8_0", ContextSize = 0, CpuMoeLayers = -1, Parallel = 1 };
        var wanted = Math.Min(ServerFit.RecommendedContext(m.DefaultContext, m.NativeContext), ComfortContext);
        var cache = "q8_0";
        var fit = ServerFit.Evaluate(fm, hw, settings);
        if (fit.ContextSize < wanted && fit.Level is FitLevel.FullGpu or FitLevel.MoeOffload)
        {
            var q4 = ServerFit.Evaluate(fm, hw, new ServerSettings { CacheType = "q4_0", ContextSize = 0, CpuMoeLayers = -1, Parallel = 1 });
            if (q4.Usable && q4.ContextSize > fit.ContextSize && q4.Level == fit.Level)
            {
                cache = "q4_0";
                fit = q4;
                settings.CacheType = "q4_0";
            }
        }
        var slots = ServerFit.RecommendSlots(fm, hw, settings, maxParallel: ServerFit.SuggestedMaxParallel);
        var chosen = slots.Chosen;
        var summary = L.F("контекст {0} на слот, слотов {1}, KV-кэш {2}", FitCalculator.Ctx(chosen.ContextPerSlot), slots.Recommended, cache);
        return new ParamAdvice(chosen.ContextPerSlot, slots.Recommended, cache, chosen.Fit, summary);
    }

    [GeneratedRegex(@"(?:IQ|Q)(\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BitsRegex();
}
