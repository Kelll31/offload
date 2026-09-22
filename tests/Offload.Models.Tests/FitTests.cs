using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models.Tests;

public class FitTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    /// <summary>Как сообщает система: карта «24 ГБ» = 24564 МиБ, ОЗУ «64 ГБ» ≈ 63,8 ГиБ.</summary>
    internal static HardwareInfo Hw(int vramGb, int ramGb, GpuVendor vendor = GpuVendor.Nvidia)
    {
        var vramMiB = vramGb switch { 24 => 24564, 16 => 16376, 8 => 8188, 0 => 0, _ => vramGb * 1024 };
        var gpus = vramGb > 0
            ? new[] { new GpuInfo("Test GPU", vendor, vramMiB * MiB, IsIntegrated: false) }
            : new[] { new GpuInfo("Intel UHD", GpuVendor.Intel, 128 * MiB, IsIntegrated: true) };
        var ram = ramGb * GiB - 200 * MiB;
        return new HardwareInfo(gpus, ram, ram / 2, "Test CPU", 16, CpuHasAvx2: true, IsArm64: false);
    }

    private static CatalogModel M(string id) => ModelCatalog.Find(id) ?? throw new InvalidOperationException(id);

    [Theory]
    [InlineData("f16", 2.0)]
    [InlineData("bf16", 2.0)]
    [InlineData("f32", 4.0)]
    [InlineData("q8_0", 34.0 / 32)]
    [InlineData("Q8_0", 34.0 / 32)]
    [InlineData("q4_0", 18.0 / 32)]
    [InlineData("q4_1", 20.0 / 32)]
    [InlineData("q5_0", 22.0 / 32)]
    [InlineData("q5_1", 24.0 / 32)]
    [InlineData("iq4_nl", 18.0 / 32)]
    [InlineData("weird", 2.0)]
    public void BytesPerKvElement_Table(string type, double expected) =>
        Assert.Equal(expected, FitCalculator.BytesPerKvElement(type), 10);

    [Fact]
    public void KvCacheBytes_HybridAndSwa()
    {
        var qwen = M("qwen3.6-35b-a3b-q4").Kv;
        Assert.Equal(20480L * 65536, FitCalculator.KvCacheBytes(qwen, 65536, "f16")); // 1,25 ГиБ
        Assert.Equal(10880L * 65536, FitCalculator.KvCacheBytes(qwen, 65536, "q8_0"));
        Assert.Equal(20480L * 131072 * 18 / 64, FitCalculator.KvCacheBytes(qwen, 131072, "q4_0"));
        Assert.Equal(0, FitCalculator.KvCacheBytes(qwen, 0, "f16"));

        // gpt-oss: 12 слоёв полного внимания + 12 слоёв окна 128 токенов.
        var oss = M("gpt-oss-20b").Kv;
        const long perLayerToken = 8 * 64 * 2 * 2;
        Assert.Equal(12 * perLayerToken * 65536 + 12 * perLayerToken * 128, FitCalculator.KvCacheBytes(oss, 65536, "f16"));
        Assert.Equal(24 * perLayerToken * 100, FitCalculator.KvCacheBytes(oss, 100, "f16"));

        var devstral = M("devstral-small-2-q4").Kv;
        Assert.Equal(163840L * 32768, FitCalculator.KvCacheBytes(devstral, 32768, "f16")); // 5 ГиБ
    }

    [Fact]
    public void Rtx24_Ram64_RecommendsFastMoeFullyOnGpu()
    {
        var hw = Hw(24, 64);
        var best = FitCalculator.Recommend(hw);
        Assert.Equal("qwen3.8-27b-q4", best.Id);
        Assert.Equal(FitLevel.FullGpu, FitCalculator.Evaluate(best, best.ApproxSizeBytes, hw).Level);
        var rec = M("qwen3.6-35b-a3b-q4");
        Assert.True(rec.IsMoe);
        var fit = FitCalculator.Evaluate(rec, rec.ApproxSizeBytes, hw);
        Assert.True(fit.Level == FitLevel.FullGpu || fit is { Level: FitLevel.MoeOffload, CpuMoeLayers: <= 4 }, fit.Explanation);
        Assert.Equal(65536, fit.ContextSize);
        Assert.Equal(-1, fit.GpuLayers);
        Assert.StartsWith("Полностью в видеопамяти: ≈", fit.Explanation);
        Assert.Contains("из 24 ГБ, контекст 64K", fit.Explanation);
        Assert.InRange(fit.EstimatedVramBytes, 21 * GiB, 23 * GiB);
    }

    [Fact]
    public void Rtx16_Ram32_MoeOffload()
    {
        var hw = Hw(16, 32);
        Assert.Equal("qwen3.8-27b-iq3", FitCalculator.Recommend(hw).Id);
        var rec = M("qwen3.6-35b-a3b-q4");
        var fit = FitCalculator.Evaluate(rec, rec.ApproxSizeBytes, hw);
        Assert.Equal(FitLevel.MoeOffload, fit.Level);
        Assert.InRange(fit.CpuMoeLayers, 12, 22); // исследование: ncmoe ≈18–20 при fp16 KV
        Assert.Equal(65536, fit.ContextSize);
        Assert.Contains("слоёв экспертов на ЦП", fit.Explanation);
        Assert.EndsWith("— быстро", fit.Explanation);
        Assert.True(fit.EstimatedVramBytes <= 16376 * MiB - FitCalculator.VramReserveBytes);
        Assert.Equal(fit.CpuMoeLayers * M("qwen3.6-35b-a3b-q4").Moe!.ExpertBytesPerLayer, fit.EstimatedRamBytes);
    }

    [Fact]
    public void Rtx16_Ram16_PrefersFastModelThatFits()
    {
        var hw = Hw(16, 16);
        var rec = FitCalculator.Recommend(hw);
        Assert.Equal("qwen3.8-27b-iq3", rec.Id);
        Assert.Equal(FitLevel.FullGpu, FitCalculator.Evaluate(rec, rec.ApproxSizeBytes, hw).Level);
    }

    [Fact]
    public void Rtx12_Ram32_MoeWith25To28Layers()
    {
        var hw = Hw(12, 32);
        var m = M("qwen3.6-35b-a3b-q4");
        var fit = FitCalculator.Evaluate(m, m.ApproxSizeBytes, hw, 0, "f16");
        Assert.Equal(FitLevel.MoeOffload, fit.Level);
        Assert.InRange(fit.CpuMoeLayers, 24, 29);
        Assert.Equal(m.Id, FitCalculator.Recommend(hw).Id);
        Assert.Equal("ornith-1.5-9b-q4", FitCalculator.Recommend(Hw(12, 16)).Id);
    }

    [Fact]
    public void Rtx8_Ram32_MoeOffloadOr9B()
    {
        var hw = Hw(8, 32);
        var rec = FitCalculator.Recommend(hw);
        var fit = FitCalculator.Evaluate(rec, rec.ApproxSizeBytes, hw);
        Assert.True(fit.Usable);
        Assert.True(rec.IsMoe && fit.Level == FitLevel.MoeOffload || rec.ParamsB <= 9.5, rec.Id);
        Assert.Equal("ornith-1.5-9b-q4", rec.Id);
        Assert.Equal(FitLevel.FullGpu, fit.Level);
        Assert.Equal(32768, fit.ContextSize);

        // MoE 35B-A3B на 8 ГБ: около 34–36 слоёв экспертов на ЦП.
        var moe = FitCalculator.Evaluate(M("qwen3.6-35b-a3b-q4"), 0, hw);
        Assert.Equal(FitLevel.MoeOffload, moe.Level);
        Assert.InRange(moe.CpuMoeLayers, 32, 38);
    }

    [Fact]
    public void Gpu4_Ram16_SmallModel()
    {
        var hw = Hw(4, 16);
        var rec = FitCalculator.Recommend(hw);
        Assert.Equal("qwen3.5-4b-q4", rec.Id);
        var fit = FitCalculator.Evaluate(rec, rec.ApproxSizeBytes, hw);
        Assert.True(fit.Usable);
        Assert.True(fit.Level is FitLevel.FullGpu or FitLevel.PartialGpu, fit.Explanation);
    }

    [Fact]
    public void CpuOnly_Ram16_Uses4B()
    {
        var hw = Hw(0, 16);
        Assert.Equal(0, hw.PrimaryVramBytes);
        var rec = FitCalculator.Recommend(hw);
        Assert.Equal("qwen3.5-4b-q4", rec.Id);
        var fit = FitCalculator.Evaluate(rec, rec.ApproxSizeBytes, hw);
        Assert.Equal(FitLevel.CpuOnly, fit.Level);
        Assert.Equal(0, fit.GpuLayers);
        Assert.Equal(32768, fit.ContextSize);
        Assert.StartsWith("Только процессор:", fit.Explanation);
        Assert.EndsWith("— медленно", fit.Explanation);
    }

    [Fact]
    public void CpuOnly_Ram32_UsesMoe()
    {
        var hw = Hw(0, 32);
        var rec = FitCalculator.Recommend(hw);
        Assert.Equal("gpt-oss-20b", rec.Id);
        Assert.Equal(FitLevel.CpuOnly, FitCalculator.Evaluate(rec, 0, hw).Level);
    }

    [Fact]
    public void Gpu2_Ram8_Tiny()
    {
        var hw = Hw(2, 8);
        var rec = FitCalculator.Recommend(hw);
        Assert.Equal("qwen3.5-2b-q8", rec.Id);
        Assert.True(rec.CpuFriendly);
        Assert.True(FitCalculator.Evaluate(rec, 0, hw).Usable);
    }

    [Fact]
    public void Recommend_TinyMachine_ReturnsSmallestEvenIfNothingFits()
    {
        var hw = Hw(0, 2);
        var rec = FitCalculator.Recommend(hw);
        Assert.Equal(ModelCatalog.All.MinBy(m => m.ApproxSizeBytes)!.Id, rec.Id);
    }

    [Fact]
    public void Recommend_AlwaysUsableWhenAnythingIsUsable()
    {
        foreach (var vram in new[] { 0, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 32, 48, 80 })
        foreach (var ram in new[] { 4, 8, 12, 16, 24, 32, 48, 64, 128 })
        foreach (var vendor in new[] { GpuVendor.Nvidia, GpuVendor.Amd })
        {
            var hw = Hw(vram, ram, vendor);
            var anyUsable = ModelCatalog.All.Any(m => FitCalculator.Evaluate(m, 0, hw).Usable);
            var rec = FitCalculator.Recommend(hw);
            if (anyUsable) Assert.True(FitCalculator.Evaluate(rec, 0, hw).Usable, $"{vram}/{ram}: {rec.Id}");
        }
    }

    [Fact]
    public void AutoContext_NeverExceedsNativeAndStaysSensible()
    {
        foreach (var m in ModelCatalog.All)
        foreach (var (vram, ram) in new[] { (0, 16), (8, 32), (12, 32), (16, 64), (24, 64), (48, 128) })
        {
            var fit = FitCalculator.Evaluate(m, 0, Hw(vram, ram));
            Assert.True(fit.ContextSize <= m.NativeContext, m.Id);
            Assert.True(fit.ContextSize >= FitCalculator.MinContext, m.Id);
            if (fit.Level is FitLevel.FullGpu or FitLevel.MoeOffload) Assert.True(fit.ContextSize >= FitCalculator.MinAgentContext, m.Id);
        }
    }

    [Fact]
    public void ExplicitContext_IsClampedToNative()
    {
        var m = M("gpt-oss-20b");
        var fit = FitCalculator.Evaluate(m, 0, Hw(48, 128), contextSize: 1_000_000);
        Assert.Equal(131072, fit.ContextSize);
        Assert.Equal(FitLevel.FullGpu, fit.Level);
        Assert.Contains("контекст 128K", fit.Explanation);

        var small = FitCalculator.Evaluate(m, 0, Hw(24, 64), contextSize: 4096);
        Assert.Equal(4096, small.ContextSize);
    }

    [Fact]
    public void LargerContextAndParallel_IncreaseMemory()
    {
        var m = M("qwen3.8-27b-q4");
        var hw = Hw(80, 256);
        var one = FitCalculator.Evaluate(m, 0, hw, 32768, "q8_0", 1);
        var two = FitCalculator.Evaluate(m, 0, hw, 32768, "q8_0", 2);
        Assert.Equal(2 * one.KvCacheBytes, two.KvCacheBytes);
        // Второй слот: ещё один KV + рекуррентное состояние DeltaNet (150 МиБ) + немного буфера вычислений.
        Assert.InRange(two.EstimatedVramBytes - one.EstimatedVramBytes, one.KvCacheBytes + m.Kv.RecurrentStateBytes, one.KvCacheBytes + m.Kv.RecurrentStateBytes + 64 * MiB);
        var f16 = FitCalculator.Evaluate(m, 0, hw, 32768, "f16", 1);
        Assert.True(f16.KvCacheBytes > one.KvCacheBytes);
    }

    [Fact]
    public void DenseTooBigForGpu_IsPartialAndSlow()
    {
        var m = M("devstral-small-2-q4");
        var fit = FitCalculator.Evaluate(m, 0, Hw(8, 32));
        Assert.Equal(FitLevel.PartialGpu, fit.Level);
        Assert.InRange(fit.GpuLayers, 1, m.BlockCount - 1);
        Assert.StartsWith("Частично на видеокарте:", fit.Explanation);
        Assert.Contains($"из {m.BlockCount} слоёв", fit.Explanation);
        Assert.EndsWith("— медленно", fit.Explanation);
        Assert.True(fit.ContextSize <= 32768);
    }

    [Fact]
    public void Huge_IsTooLarge()
    {
        var m = M("qwen3-coder-next-q4");
        var fit = FitCalculator.Evaluate(m, 0, Hw(8, 16));
        Assert.Equal(FitLevel.TooLarge, fit.Level);
        Assert.False(fit.Usable);
        Assert.StartsWith("Не хватит памяти: нужно ≈", fit.Explanation);
        Assert.True(fit.EstimatedVramBytes > m.ApproxSizeBytes);
    }

    [Fact]
    public void CoderNext_Workstation_MoeOffload()
    {
        var fit = FitCalculator.Evaluate(M("qwen3-coder-next-q4"), 0, Hw(16, 64));
        Assert.Equal(FitLevel.MoeOffload, fit.Level);
        Assert.InRange(fit.CpuMoeLayers, 30, 45);
    }

    [Fact]
    public void AlternativeQuant_UsesItsOwnExpertSplit()
    {
        var m = M("qwen3.6-35b-a3b-q4");
        var q8 = m.FindFile("Q8_0")!;
        Assert.Equal(855638016, FitCalculator.MoeFor(m, q8.Size).ExpertBytesPerLayer);
        // Размер, которого нет в каталоге, — пропорциональный пересчёт.
        var scaled = FitCalculator.MoeFor(m, m.ApproxSizeBytes * 2);
        Assert.Equal(m.Moe!.ExpertBytesPerLayer * 2, scaled.ExpertBytesPerLayer, 1.0);
        var fitQ8 = FitCalculator.Evaluate(m, q8.Size, Hw(24, 64));
        Assert.Equal(FitLevel.MoeOffload, fitQ8.Level);
        Assert.Equal(q8.Size, fitQ8.WeightsBytes);
    }

    [Fact]
    public void SyntheticCustomModel_FromUi_IsEvaluated()
    {
        // Так UI оценивает пользовательскую модель: ParamsB = 0, KvSpec из заголовка GGUF.
        var custom = new CatalogModel("custom-x", "X", "", "", ["Q4_K_M"], 5_000_000_000, 0, 0, false,
            32768, 32768, new KvSpec(32, 8, 128), false, new SamplingSettings(), "", 0);
        var fit = FitCalculator.Evaluate(custom, custom.ApproxSizeBytes, Hw(8, 16));
        Assert.True(fit.Usable);
        var customMoe = custom with { IsMoe = true, ApproxSizeBytes = 20_000_000_000 };
        Assert.Equal(FitLevel.MoeOffload, FitCalculator.Evaluate(customMoe, customMoe.ApproxSizeBytes, Hw(16, 64)).Level);
    }

    [Theory]
    [InlineData(1, "слой")]
    [InlineData(2, "слоя")]
    [InlineData(4, "слоя")]
    [InlineData(5, "слоёв")]
    [InlineData(11, "слоёв")]
    [InlineData(12, "слоёв")]
    [InlineData(21, "слой")]
    [InlineData(22, "слоя")]
    [InlineData(36, "слоёв")]
    public void Plural_Russian(int n, string expected) => Assert.Equal(expected, FitCalculator.Plural(n, "слой", "слоя", "слоёв"));

    [Fact]
    public void Formatting_UsesRussianDecimalComma()
    {
        Assert.Equal("21,4", FitCalculator.Gb((long)(21.4 * GiB)));
        Assert.Equal("24", FitCalculator.Gb(24564 * MiB));
        Assert.Equal("64K", FitCalculator.Ctx(65536));
        Assert.Equal("8K", FitCalculator.Ctx(8192));
    }
}
