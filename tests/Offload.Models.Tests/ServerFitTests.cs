using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models.Tests;

/// <summary>Размещение «Авто», свободная видеопамять и подбор числа слотов (ServerFit поверх FitCalculator).</summary>
public sealed class ServerFitTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static CatalogModel M(string id) => ModelCatalog.Find(id) ?? throw new InvalidOperationException(id);

    private static FitModel Fm(string id) => new(M(id), M(id).ApproxSizeBytes, M(id).DefaultContext);

    private static ServerSettings Auto(Action<ServerSettings>? edit = null)
    {
        var s = new ServerSettings();
        edit?.Invoke(s);
        return s;
    }

    [Fact]
    public void VramBudget_ReserveOrOtherPrograms_WhicheverIsLarger()
    {
        Assert.Equal(23 * GiB, FitCalculator.VramBudget(24 * GiB));
        Assert.Equal(23 * GiB, FitCalculator.VramBudget(24 * GiB, GiB / 2)); // фон рабочего стола входит в резерв
        Assert.Equal(21 * GiB, FitCalculator.VramBudget(24 * GiB, 3 * GiB));
        Assert.Equal(0, FitCalculator.VramBudget(GiB, 5 * GiB));
        Assert.Equal(23 * GiB, FitCalculator.VramBudget(24 * GiB, -1));
    }

    [Fact]
    public void OtherPrograms_ShrinkPlacement()
    {
        var hw = FitTests.Hw(24, 64);
        var fm = Fm("qwen3.8-27b-q4");
        var free = ServerFit.Evaluate(fm, hw, Auto());
        var busy = ServerFit.Evaluate(fm, hw, Auto(), otherVramBytes: 8 * GiB);
        Assert.Equal(FitLevel.FullGpu, free.Level);
        Assert.True(busy.Level > free.Level || busy.ContextSize < free.ContextSize,
            $"занятая другими видеопамять должна ухудшить размещение: {free.Explanation} → {busy.Explanation}");
    }

    [Fact]
    public void AutoContext_NotAboveRecommended_ExplicitAsIs()
    {
        var hw = FitTests.Hw(24, 64);
        var model = M("ornith-1.5-9b-q4");
        Assert.Equal(32768, model.DefaultContext);
        // Сам FitCalculator при свободной памяти берёт и больше рекомендованного…
        Assert.True(FitCalculator.Evaluate(model, model.ApproxSizeBytes, hw).ContextSize > 32768);
        // …а для сервера «Авто» не поднимает контекст выше рекомендации (как LlamaServerArgs.ResolveContext).
        var fm = new FitModel(model, model.ApproxSizeBytes, 32768);
        Assert.Equal(32768, ServerFit.Evaluate(fm, hw, Auto()).ContextSize);
        Assert.Equal(16384, ServerFit.Evaluate(fm, hw, Auto(s => s.ContextSize = 16384)).ContextSize);
        Assert.Equal(131072, ServerFit.Evaluate(fm, hw, Auto(s => s.ContextSize = 131072)).ContextSize);
    }

    [Fact]
    public void AutoContext_ShrinksWhenMemoryIsShort()
    {
        // 27B на 16 ГБ: рекомендованные 64K целиком не помещаются — «Авто» уменьшает контекст, а не уходит в медленный режим.
        var fm = Fm("qwen3.8-27b-iq3");
        var fit = ServerFit.Evaluate(fm, FitTests.Hw(16, 32), Auto());
        Assert.True(fit.ContextSize <= 65536);
        Assert.True(fit.Usable);
    }

    [Fact]
    public void RecommendedContext_MatchesLaunchDefaults()
    {
        Assert.Equal(65536, ServerFit.RecommendedContext(65536, 262144));
        Assert.Equal(ServerFit.FallbackContext, ServerFit.RecommendedContext(0, 0));
        Assert.Equal(8192, ServerFit.RecommendedContext(0, 8192));
        Assert.Equal(512, ServerFit.RecommendedContext(100, 0));
    }

    [Fact]
    public void AutoPlacement_MoeOffload_PassesCpuMoeAndContext()
    {
        var fit = ServerFit.Evaluate(Fm("qwen3.6-35b-a3b-q4"), FitTests.Hw(16, 32), Auto());
        Assert.Equal(FitLevel.MoeOffload, fit.Level);
        var p = ServerFit.AutoPlacement(fit, Auto());
        Assert.NotNull(p);
        Assert.Equal(fit.ContextSize, p.ContextPerSlot);
        Assert.Equal(fit.CpuMoeLayers, p.CpuMoeLayers);
        Assert.True(p.CpuMoeLayers > 0);
        Assert.Equal(fit.Explanation, p.Reason);
    }

    [Fact]
    public void AutoPlacement_FullGpu_NoCpuMoe()
    {
        var fit = ServerFit.Evaluate(Fm("qwen3.6-35b-a3b-q4"), FitTests.Hw(24, 64), Auto());
        Assert.Equal(FitLevel.FullGpu, fit.Level);
        var p = ServerFit.AutoPlacement(fit, Auto());
        Assert.NotNull(p);
        Assert.Equal(0, p.CpuMoeLayers);
        Assert.Equal(65536, p.ContextPerSlot);
    }

    [Fact]
    public void AutoPlacement_OnlyInAutoMode_AndWhenUsable()
    {
        var fit = ServerFit.Evaluate(Fm("qwen3.6-35b-a3b-q4"), FitTests.Hw(16, 32), Auto());
        Assert.Null(ServerFit.AutoPlacement(fit, Auto(s => s.ContextSize = 32768)));
        Assert.Null(ServerFit.AutoPlacement(fit, Auto(s => s.CpuMoeLayers = 0)));
        Assert.Null(ServerFit.AutoPlacement(fit, Auto(s => s.CpuMoeLayers = 12)));
        Assert.Null(ServerFit.AutoPlacement(null, Auto()));

        var huge = ServerFit.Evaluate(Fm("qwen3.8-27b-q6"), FitTests.Hw(0, 8), Auto());
        Assert.Equal(FitLevel.TooLarge, huge.Level);
        Assert.Null(ServerFit.AutoPlacement(huge, Auto()));
        Assert.True(ServerFit.IsAutoPlacement(Auto()));
    }

    [Fact]
    public void RecommendSlots_Hybrid27bOn24Gb_TwoSlots()
    {
        // Qwen3.8-27B Q4 (15,7 ГиБ, гибрид DeltaNet): слот 64K с q8_0 ≈ 2,1 ГиБ KV — 2 слота ≈ 21,4 ГиБ, 3 уже не помещаются в 23 ГиБ.
        var hw = FitTests.Hw(24, 64);
        var advice = ServerFit.RecommendSlots(Fm("qwen3.8-27b-q4"), hw, Auto());
        Assert.Equal(ServerSettings.MaxParallel, advice.Options.Count);
        Assert.Equal(2, advice.Recommended);
        Assert.Equal(65536, advice.ContextPerSlot);
        Assert.Equal(FitLevel.FullGpu, advice.Chosen.Fit.Level);
        Assert.InRange(advice.Chosen.Fit.EstimatedVramBytes, 21 * GiB, 22 * GiB);
        Assert.NotEqual(FitLevel.FullGpu, advice.Options[2].Fit.Level);
        // KV-кэш растёт пропорционально числу слотов.
        Assert.Equal(advice.Options[0].Fit.KvCacheBytes * 3, advice.Options[2].Fit.KvCacheBytes);
        Assert.All(advice.Options, o => Assert.Equal(advice.ContextPerSlot, o.ContextPerSlot));
    }

    [Fact]
    public void RecommendSlots_SmallModel_CappedAtSuggestedMax()
    {
        var advice = ServerFit.RecommendSlots(Fm("qwen3.5-2b-q8"), FitTests.Hw(24, 64), Auto());
        Assert.Equal(ServerFit.SuggestedMaxParallel, advice.Recommended);
        Assert.Equal(FitLevel.FullGpu, advice.Options[^1].Fit.Level); // поместилось бы и 8, но больше 4 не предлагаем
    }

    [Fact]
    public void RecommendSlots_MoeWithoutSlack_OneSlot()
    {
        // 35B-A3B Q4 занимает 24 ГБ почти целиком: второй слот потребовал бы выгрузки экспертов.
        var advice = ServerFit.RecommendSlots(Fm("qwen3.6-35b-a3b-q4"), FitTests.Hw(24, 64), Auto());
        Assert.Equal(1, advice.Recommended);
        Assert.Equal(FitLevel.FullGpu, advice.Chosen.Fit.Level);
    }

    [Fact]
    public void RecommendSlots_OtherProgramsOrSlowPlacement_OneSlot()
    {
        // Только процессор — параллельность замедлит каждый запрос.
        var cpu = ServerFit.RecommendSlots(Fm("qwen3.5-4b-q4"), FitTests.Hw(0, 16), Auto());
        Assert.Equal(1, cpu.Recommended);
        Assert.Equal(1, cpu.Chosen.Parallel);

        // Занятые другими 4 ГБ видеопамяти не оставляют места второму слоту.
        var fm = Fm("qwen3.8-27b-q4");
        Assert.Equal(2, ServerFit.RecommendSlots(fm, FitTests.Hw(24, 64), Auto()).Recommended);
        Assert.Equal(1, ServerFit.RecommendSlots(fm, FitTests.Hw(24, 64), Auto(), otherVramBytes: 4 * GiB).Recommended);
    }

    [Fact]
    public void RecommendSlots_RespectsMaxParallel()
    {
        var advice = ServerFit.RecommendSlots(Fm("qwen3.5-2b-q8"), FitTests.Hw(24, 64), Auto(), maxParallel: 2);
        Assert.Equal(2, advice.Options.Count);
        Assert.InRange(advice.Recommended, 1, 2);
    }

    [Fact]
    public void ModelFor_CatalogById_OrCustomFromHeader()
    {
        var catalog = ModelCatalog.All;
        var c = M("qwen3.5-9b-q4");
        var installed = new InstalledModel { Id = c.Id + ":Q4_K_M", FilePath = @"C:\m\x.gguf", SizeBytes = 5 * GiB };
        var fm = ServerFit.ModelFor(installed, catalog);
        Assert.NotNull(fm);
        Assert.Same(c, fm.Model);
        Assert.Equal(5 * GiB, fm.WeightsBytes);
        Assert.Equal(c.DefaultContext, fm.RecommendedContext);

        var custom = new InstalledModel { Id = "custom:my", FilePath = @"C:\m\my.gguf", SizeBytes = 4 * GiB, IsCustom = true, RecommendedContext = 16384 };
        Assert.Null(ServerFit.ModelFor(custom, catalog));
        Assert.Null(ServerFit.ModelFor(custom, catalog, _ => null));

        var header = new GgufInfo("qwen35", "My", 262144, 32, 4, 16, 4096, 256, false, 0, null, ValueLength: 256, FullAttentionInterval: 4);
        var synthetic = ServerFit.ModelFor(custom, catalog, _ => header);
        Assert.NotNull(synthetic);
        Assert.Equal(32, synthetic.Model.BlockCount);
        Assert.Equal(8, synthetic.Model.Kv.Layers); // гибрид: KV-кэш только у каждого 4-го слоя
        Assert.Equal(16384, synthetic.RecommendedContext);
        Assert.Equal(262144, synthetic.Model.NativeContext);
        Assert.True(ServerFit.Evaluate(synthetic, FitTests.Hw(24, 64), Auto()).Usable);
    }
}
