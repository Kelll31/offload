using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models;

/// <summary>Модель в том виде, в каком её оценивает FitCalculator: параметры архитектуры, размер файла, рекомендованный контекст.</summary>
public sealed record FitModel(CatalogModel Model, long WeightsBytes, int RecommendedContext);

/// <summary>Вариант числа слотов: сколько слотов, какой контекст на слот и во что это обойдётся по памяти.</summary>
public sealed record SlotOption(int Parallel, int ContextPerSlot, FitResult Fit);

/// <summary>Рекомендация числа параллельных слотов.</summary>
/// <param name="Recommended">Рекомендуемое число слотов (1, если параллельность не помещается или замедлит работу).</param>
/// <param name="ContextPerSlot">Контекст одного слота, с которым считались варианты.</param>
/// <param name="Options">Варианты 1..N слотов с оценкой памяти.</param>
public sealed record SlotAdvice(int Recommended, int ContextPerSlot, IReadOnlyList<SlotOption> Options)
{
    public SlotOption Chosen => Options.FirstOrDefault(o => o.Parallel == Recommended) ?? Options[0];
}

/// <summary>
/// Оценка размещения для настроек сервера — общая точка для интерфейса (списки моделей, страница «Сервер», мастер)
/// и запуска llama-server (режим «Авто»), чтобы показанный контекст совпадал с фактическим.
/// </summary>
public static class ServerFit
{
    /// <summary>
    /// Больше слотов по умолчанию не предлагаем: на одной видеокарте выигрыш в пропускной способности дальше мал,
    /// а скорость каждого запроса падает. Вручную можно поставить до <see cref="ServerSettings.MaxParallel"/>.
    /// </summary>
    public const int SuggestedMaxParallel = 4;

    /// <summary>Контекст по умолчанию, если модель его не задаёт (как LlamaServerArgs.FallbackContext).</summary>
    public const int FallbackContext = 32768;

    /// <summary>Режим «Авто»: контекст и выгрузку экспертов MoE подбирает Offload (иначе — значения пользователя).</summary>
    public static bool IsAutoPlacement(ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.ContextSize <= 0 && s.CpuMoeLayers < 0;
    }

    /// <summary>
    /// Рекомендованный контекст слота — верхняя граница автоподбора (как LlamaServerArgs.ResolveContext при ContextSize = 0):
    /// авторежим уменьшает контекст при нехватке памяти, но не поднимает выше рекомендации модели.
    /// </summary>
    public static int RecommendedContext(int recommended, int native)
    {
        var ctx = Math.Max(recommended > 0 ? recommended : FallbackContext, 512);
        return native > 0 ? Math.Min(ctx, native) : ctx;
    }

    /// <summary>Модель для оценки: из каталога (по идентификатору) или по заголовку GGUF; null — данных нет.</summary>
    /// <param name="readHeader">Чтение заголовка GGUF (для пользовательских моделей); null — такие модели не оцениваются.</param>
    public static FitModel? ModelFor(InstalledModel m, IReadOnlyList<CatalogModel> catalog, Func<string, GgufInfo?>? readHeader = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(catalog);
        var c = catalog.FirstOrDefault(x => string.Equals(m.Id, x.Id, StringComparison.OrdinalIgnoreCase)
                                            || m.Id.StartsWith(x.Id + ":", StringComparison.OrdinalIgnoreCase));
        if (c is not null)
            return new FitModel(c, m.SizeBytes > 0 ? m.SizeBytes : c.ApproxSizeBytes, m.RecommendedContext > 0 ? m.RecommendedContext : c.DefaultContext);

        if (readHeader is null || string.IsNullOrWhiteSpace(m.FilePath) || m.SizeBytes <= 0) return null;
        if (readHeader(m.FilePath) is not { } h) return null;
        var native = m.NativeContext > 0 ? m.NativeContext : h.ContextLength;
        var ctx = m.RecommendedContext > 0 ? m.RecommendedContext : Math.Min(FallbackContext, Math.Max(4096, native));
        var synthetic = new CatalogModel(
            m.Id, m.DisplayName, "", m.Repo ?? "", [m.Quant ?? ""], m.SizeBytes, 0, 0, m.IsMoe || h.IsMoe,
            native > 0 ? native : ctx, ctx, h.ToKvSpec(), m.GoodToolCalling, m.Sampling, "", 0,
            BlockCount: h.BlockCount);
        return new FitModel(synthetic, m.SizeBytes, ctx);
    }

    /// <summary>
    /// Оценка при настройках сервера: явный контекст — как задан; «Авто» — наибольший подходящий, не выше рекомендованного.
    /// </summary>
    /// <param name="parallel">Число слотов (по умолчанию — из настроек).</param>
    public static FitResult Evaluate(FitModel fm, HardwareInfo hw, ServerSettings s, long otherVramBytes = 0, int? parallel = null)
    {
        ArgumentNullException.ThrowIfNull(fm);
        ArgumentNullException.ThrowIfNull(s);
        var p = Math.Clamp(parallel ?? s.Parallel, 1, ServerSettings.MaxParallel);
        var cache = string.IsNullOrWhiteSpace(s.CacheType) ? "f16" : s.CacheType;
        return s.ContextSize > 0
            ? FitCalculator.Evaluate(fm.Model, fm.WeightsBytes, hw, s.ContextSize, cache, p, otherVramBytes: otherVramBytes)
            : FitCalculator.Evaluate(fm.Model, fm.WeightsBytes, hw, 0, cache, p,
                maxContext: RecommendedContext(fm.RecommendedContext, fm.Model.NativeContext), otherVramBytes: otherVramBytes);
    }

    /// <summary>
    /// Размещение для запуска в режиме «Авто»: явный -c из оценки и --n-cpu-moe для MoE с выгрузкой экспертов.
    /// null — оставить решение llama-server (--fit): не авторежим, модель не помещается или оценки нет.
    /// </summary>
    public static ServerPlacement? AutoPlacement(FitResult? fit, ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (fit is null || !IsAutoPlacement(s) || !fit.Usable || fit.ContextSize <= 0) return null;
        return new ServerPlacement(fit.ContextSize, fit.Level == FitLevel.MoeOffload ? Math.Max(0, fit.CpuMoeLayers) : 0, fit.Explanation);
    }

    /// <summary>
    /// Сколько параллельных слотов предложить. Контекст слота — как при одном слоте (явный или подобранный);
    /// слот добавляется, пока модель остаётся на том же уровне размещения: целиком в видеопамяти или MoE с тем же
    /// числом слоёв экспертов на ЦП (иначе параллельность замедлит каждый запрос). У гибридных моделей (DeltaNet)
    /// KV-кэш на слот мал — им обычно помещается больше слотов.
    /// </summary>
    public static SlotAdvice RecommendSlots(FitModel fm, HardwareInfo hw, ServerSettings s, long otherVramBytes = 0,
        int maxParallel = ServerSettings.MaxParallel)
    {
        ArgumentNullException.ThrowIfNull(fm);
        ArgumentNullException.ThrowIfNull(s);
        maxParallel = Math.Clamp(maxParallel, 1, ServerSettings.MaxParallel);
        var single = Evaluate(fm, hw, s, otherVramBytes, parallel: 1);
        var ctx = single.ContextSize;
        var cache = string.IsNullOrWhiteSpace(s.CacheType) ? "f16" : s.CacheType;

        var options = new List<SlotOption> { new(1, ctx, single) };
        for (var p = 2; p <= maxParallel; p++)
            options.Add(new SlotOption(p, ctx, FitCalculator.Evaluate(fm.Model, fm.WeightsBytes, hw, ctx, cache, p, otherVramBytes: otherVramBytes)));

        var recommended = 1;
        if (single.Level is FitLevel.FullGpu or FitLevel.MoeOffload)
        {
            foreach (var o in options.Skip(1).Where(o => o.Parallel <= SuggestedMaxParallel))
            {
                if (o.Fit.Level != single.Level || o.Fit.CpuMoeLayers > single.CpuMoeLayers) break;
                recommended = o.Parallel;
            }
        }
        return new SlotAdvice(recommended, ctx, options);
    }
}
