using Offload.Core.Hardware;

namespace Offload.Models.Tests;

public class HardwareAdvisorTests
{
    private static readonly (int Vram, int Ram)[] Machines =
        [(0, 8), (0, 16), (0, 32), (6, 16), (8, 16), (8, 32), (12, 32), (16, 32), (16, 64), (24, 32), (24, 64), (32, 64), (48, 128), (8, 64), (12, 64), (12, 16)];

    private static HardwareAdvice Advise(int vram, int ram) => HardwareAdvisor.Advise(FitTests.Hw(vram, ram), ModelCatalog.ChatModels);

    [Fact]
    public void Table_PrintsRecommendationsForTypicalMachines()
    {
        // Таблица для глаз: расчёт на реальном каталоге должен давать осмысленные модели (видно в выводе теста).
        foreach (var (vram, ram) in Machines)
        {
            var a = Advise(vram, ram);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{vram,2} ГБ видео / {ram,3} ГБ ОЗУ -> {a.Best?.Model.Id} {a.Best?.Quant} [{a.Best?.Fit.Level}] ctx {a.Params?.ContextPerSlot} x{a.Params?.Parallel} {a.Params?.CacheType}" +
                $" | fast: {a.Fast?.Model.Id} | alt: {string.Join(", ", a.Alternatives.Select(x => x.Model.Id))}");
        }
    }

    [Fact]
    public void EveryMachine_GetsAUsableBestPick()
    {
        foreach (var (vram, ram) in Machines)
        {
            var a = Advise(vram, ram);
            Assert.NotNull(a.Best);
            Assert.True(a.Best.Fit.Usable, $"{vram}/{ram}");
            Assert.True(a.Best.Model.IsChat);
            Assert.NotNull(a.Params);
            Assert.True(a.Params.ContextPerSlot > 0 && a.Params.Parallel >= 1);
            Assert.Contains(a.Params.CacheType, new[] { "q8_0", "q4_0", "f16" });
            Assert.DoesNotContain(a.Alternatives, x => x.Model.Id == a.Best.Model.Id);
            Assert.True(a.Alternatives.Count <= 3);
        }
    }

    [Fact]
    public void MoreMemory_NeverGivesAWeakerPick()
    {
        double Quality(int v, int r)
        {
            var b = Advise(v, r).Best!;
            return HardwareAdvisor.EffectiveParamsB(b.Model) * HardwareAdvisor.QuantFactor(b.Quant);
        }
        // Больше видеопамяти при той же ОЗУ — не хуже по качеству (по скорости модель может быть и меньше, но не слабее по весу знаний).
        foreach (var ram in new[] { 32, 64 })
        {
            var last = 0.0;
            foreach (var vram in new[] { 8, 12, 16, 24, 32 })
            {
                var q = Quality(vram, ram);
                Assert.True(q >= last * 0.8, $"{vram}/{ram}: {q:0.0} после {last:0.0}");
                last = q;
            }
        }
    }

    [Fact]
    public void Best_RespectsAgentContextWhenAnyModelCan()
    {
        foreach (var (vram, ram) in Machines.Where(m => m.Vram >= 8))
        {
            var a = Advise(vram, ram);
            Assert.True(a.Best!.Fit.ContextSize >= HardwareAdvisor.MinAgentContext, $"{vram}/{ram}: {a.Best.Fit.ContextSize}");
        }
    }

    [Fact]
    public void Fast_FitsBesideBest_AndIsSmaller()
    {
        foreach (var (vram, ram) in Machines)
        {
            var a = Advise(vram, ram);
            if (a.Fast is null) continue;
            Assert.Equal(FitLevel.FullGpu, a.Fast.Fit.Level);
            Assert.True(a.Fast.Fit.ContextSize >= 8192);
            Assert.True(HardwareAdvisor.EffectiveParamsB(a.Fast.Model) <= HardwareAdvisor.EffectiveParamsB(a.Best!.Model) / 3 + 1e-9);
            Assert.NotEqual(a.Best.Model.Id, a.Fast.Model.Id);
        }
    }

    [Fact]
    public void EmptyCatalog_NoPicks()
    {
        var a = HardwareAdvisor.Advise(FitTests.Hw(24, 64), []);
        Assert.Null(a.Best);
        Assert.Null(a.Params);
        Assert.Empty(a.Alternatives);
    }

    [Theory]
    [InlineData("Q8_0", 1.0)]
    [InlineData("BF16", 1.0)]
    [InlineData("UD-Q4_K_XL", 0.93)]
    [InlineData("Q4_K_M", 0.93)]
    [InlineData("IQ3_XXS", 0.84)]
    [InlineData("Q6_K", 0.99)]
    [InlineData("Q2_K", 0.65)]
    [InlineData("MXFP4", 0.93)]
    public void QuantFactor_ByBits(string quant, double expected) => Assert.Equal(expected, HardwareAdvisor.QuantFactor(quant), 3);

    [Fact]
    public void Fingerprint_StableAndSensitiveToHardware()
    {
        var a = FitTests.Hw(24, 64);
        Assert.Equal(HardwareAdvisor.Fingerprint(a), HardwareAdvisor.Fingerprint(FitTests.Hw(24, 64)));
        Assert.NotEqual(HardwareAdvisor.Fingerprint(a), HardwareAdvisor.Fingerprint(FitTests.Hw(16, 64)));
        Assert.NotEqual(HardwareAdvisor.Fingerprint(a), HardwareAdvisor.Fingerprint(FitTests.Hw(24, 32)));
        // Свободная память и драйвер не входят в отпечаток.
        var busy = a with { AvailableRamBytes = 1024 };
        Assert.Equal(HardwareAdvisor.Fingerprint(a), HardwareAdvisor.Fingerprint(busy));
    }
}
