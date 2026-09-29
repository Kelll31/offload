using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models.Tests;

/// <summary>Несколько видеокарт: выбор устройств (MultiGpuPlanner) и оценка памяти по сумме карт.</summary>
public sealed class MultiGpuTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    private static CatalogModel M(string id) => ModelCatalog.Find(id) ?? throw new InvalidOperationException(id);

    private static HardwareInfo Hw(params GpuInfo[] gpus) => new(gpus, 64 * GiB, 32 * GiB, "Test CPU", 16, CpuHasAvx2: true, IsArm64: false);

    private static LlamaDevice Dev(string name, string desc, long totalMiB, long freeMiB) => new(name, desc, totalMiB * MiB, freeMiB * MiB);

    private static readonly LlamaDevice[] TwoCuda =
    [
        Dev("CUDA0", "NVIDIA GeForce RTX 3090", 24576, 20480),
        Dev("CUDA1", "NVIDIA GeForce RTX 4090", 24576, 24064),
    ];

    [Fact]
    public void Plan_TwoCards_ProportionalToFreeMemory_MainIsBiggest()
    {
        var split = MultiGpuPlanner.Plan(TwoCuda, null, ServerSettings.GpuSelectionAll);
        Assert.NotNull(split);
        Assert.True(split.IsMulti);
        Assert.Equal("CUDA0,CUDA1", split.DeviceArg);
        // 3090: занято 4 ГБ другими → бюджет 20 ГБ; 4090: занято 0,5 ГБ → резерв 1 ГБ → 23 ГБ.
        Assert.Equal(20 * GiB, split.Devices[0].BudgetBytes);
        Assert.Equal(23 * GiB, split.Devices[1].BudgetBytes);
        Assert.Equal("47,53", split.TensorSplitArg);
        Assert.Equal(1, split.MainIndex); // равный объём — у кого больше доступно
        Assert.Equal(GpuSplit.LayerMode, split.SplitMode);
        Assert.Equal(48 * GiB, split.TotalBytes);

        // Сервер уже работает (свободная память занижена им самим) — занятость не вычитается.
        var running = MultiGpuPlanner.Plan(TwoCuda, null, ServerSettings.GpuSelectionAll, ignoreOtherUsage: true)!;
        Assert.Equal("50,50", running.TensorSplitArg);
    }

    [Fact]
    public void Plan_PrimaryOnly_SingleDevice()
    {
        var devices = new[] { Dev("CUDA0", "NVIDIA GeForce RTX 3060", 12288, 11000), Dev("CUDA1", "NVIDIA GeForce RTX 4090", 24576, 23000) };
        var split = MultiGpuPlanner.Plan(devices, null, ServerSettings.GpuSelectionPrimary);
        Assert.NotNull(split);
        Assert.False(split.IsMulti);
        Assert.Equal("CUDA1", split.DeviceArg);
    }

    [Fact]
    public void Plan_SingleOrNoDevice_Null()
    {
        Assert.Null(MultiGpuPlanner.Plan(null, null, ServerSettings.GpuSelectionAll));
        Assert.Null(MultiGpuPlanner.Plan([], null, ServerSettings.GpuSelectionAll));
        Assert.Null(MultiGpuPlanner.Plan([TwoCuda[1]], null, ServerSettings.GpuSelectionAll));
        // Две карты разных бэкендов (одна и та же карта через CUDA и Vulkan) — не разделение.
        Assert.Null(MultiGpuPlanner.Plan([TwoCuda[1], Dev("Vulkan0", "NVIDIA GeForce RTX 4090", 24000, 23000)], null, ServerSettings.GpuSelectionAll));
    }

    [Fact]
    public void Plan_ExcludesTinyAndIntegrated()
    {
        // Маленькая карта исключена — остаётся одна: явный --device, чтобы llama.cpp не задействовал маленькую.
        var tiny = MultiGpuPlanner.Plan([Dev("CUDA0", "NVIDIA GeForce RTX 4090", 24576, 23000), Dev("CUDA1", "NVIDIA GeForce GT 1030", 2000, 1900)],
            null, ServerSettings.GpuSelectionAll);
        Assert.NotNull(tiny);
        Assert.Equal("CUDA0", tiny.DeviceArg);
        Assert.False(tiny.IsMulti);

        // Встроенная графика при дискретных — по названию…
        var vulkan = new[]
        {
            Dev("Vulkan0", "AMD Radeon RX 7900 XTX", 24560, 23800),
            Dev("Vulkan1", "AMD Radeon(TM) Graphics", 4096, 3500),
            Dev("Vulkan2", "Intel(R) Arc(TM) A770 Graphics", 16032, 15800),
        };
        var split = MultiGpuPlanner.Plan(vulkan, null, ServerSettings.GpuSelectionAll)!;
        Assert.Equal("Vulkan0,Vulkan2", split.DeviceArg);
        Assert.Equal(0, split.MainIndex);

        // …и по признаку оборудования с тем же названием.
        var hw = Hw(new GpuInfo("AMD Radeon RX 7900 XTX", GpuVendor.Amd, 24 * GiB, false),
            new GpuInfo("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel, 16 * GiB, IsIntegrated: true));
        Assert.Equal("Vulkan0", MultiGpuPlanner.Plan(vulkan, hw, ServerSettings.GpuSelectionAll)!.DeviceArg);

        Assert.True(MultiGpuPlanner.IsIntegrated(Dev("Vulkan0", "Intel(R) UHD Graphics 770", 8000, 7000), null));
        Assert.True(MultiGpuPlanner.IsIntegrated(Dev("Vulkan0", "AMD Radeon 780M Graphics", 8000, 7000), null));
        Assert.True(MultiGpuPlanner.IsIntegrated(Dev("Vulkan0", "Intel(R) Arc(TM) Graphics", 8000, 7000), null));
        Assert.False(MultiGpuPlanner.IsIntegrated(Dev("Vulkan0", "Intel(R) Arc(TM) B580 Graphics", 12000, 11000), null));
        Assert.False(MultiGpuPlanner.IsIntegrated(Dev("CUDA0", "NVIDIA GeForce RTX 4090", 24000, 23000), null));
    }

    [Fact]
    public void Fit_BySumOfCards_WithComputeBufferPerCard()
    {
        // 27B Q6 не помещается в одну 24-ГБ карту целиком, а в две — да.
        var model = M("qwen3.8-27b-q6");
        var hw = FitTests.Hw(24, 64);
        var single = FitCalculator.Evaluate(model, model.ApproxSizeBytes, hw, contextSize: 65536);
        Assert.NotEqual(FitLevel.FullGpu, single.Level);

        var split = MultiGpuPlanner.Plan(TwoCuda, null, ServerSettings.GpuSelectionAll, ignoreOtherUsage: true)!;
        var multi = FitCalculator.Evaluate(model, model.ApproxSizeBytes, hw, contextSize: 65536, split: split);
        Assert.Equal(FitLevel.FullGpu, multi.Level);
        // Буфер вычислений второй карты учтён сверх потребности одной карты.
        var compute = FitCalculator.ComputeBufferBytes(model, model.ApproxSizeBytes, 65536);
        var need = model.ApproxSizeBytes + multi.KvCacheBytes + model.Kv.RecurrentStateBytes + compute;
        Assert.Equal(need + compute, multi.EstimatedVramBytes);
        Assert.Contains("2", multi.Explanation);
    }

    [Fact]
    public void Fit_SingleCardSplit_SameAsWithout()
    {
        var model = M("qwen3.6-35b-a3b-q4");
        var hw = FitTests.Hw(16, 32);
        var one = new GpuSplit([new GpuSplitDevice("CUDA0", "RTX", 16 * GiB, 15 * GiB)], 0);
        Assert.Equal(FitCalculator.Evaluate(model, model.ApproxSizeBytes, hw), FitCalculator.Evaluate(model, model.ApproxSizeBytes, hw, split: one));
        var fm = new FitModel(model, model.ApproxSizeBytes, model.DefaultContext);
        var s = new ServerSettings();
        Assert.Equal(ServerFit.Evaluate(fm, hw, s), ServerFit.Evaluate(fm, hw, s, split: one));
    }

    [Fact]
    public void ServerFit_TwoCards_MoeWithoutOffload()
    {
        // 35B-A3B на 16 ГБ требует выгрузки экспертов, на 2×16 ГБ — нет.
        var fm = new FitModel(M("qwen3.6-35b-a3b-q4"), M("qwen3.6-35b-a3b-q4").ApproxSizeBytes, 65536);
        var hw = FitTests.Hw(16, 32);
        var s = new ServerSettings();
        Assert.Equal(FitLevel.MoeOffload, ServerFit.Evaluate(fm, hw, s).Level);
        var cards = new[] { Dev("CUDA0", "RTX 4080", 16384, 16000), Dev("CUDA1", "RTX 4080", 16384, 16000) };
        var split = MultiGpuPlanner.Plan(cards, null, ServerSettings.GpuSelectionAll)!;
        var fit = ServerFit.Evaluate(fm, hw, s, split: split);
        Assert.Equal(FitLevel.FullGpu, fit.Level);
        Assert.Equal(0, ServerFit.AutoPlacement(fit, s)!.CpuMoeLayers);
        Assert.True(ServerFit.RecommendSlots(fm, hw, s, split: split).Chosen.Fit.Level == FitLevel.FullGpu);
    }

    [Fact]
    public void HardwareFingerprint_StableAndSensitive()
    {
        var a = Hw(new GpuInfo("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia, 24 * GiB, false), new GpuInfo("Intel UHD", GpuVendor.Intel, 128 * MiB, true));
        var b = Hw(new GpuInfo("Intel UHD", GpuVendor.Intel, 256 * MiB, true), new GpuInfo("nvidia geforce rtx 4090", GpuVendor.Nvidia, 24 * GiB, false));
        Assert.Equal(HardwareFingerprint.Of(a), HardwareFingerprint.Of(b)); // порядок, регистр и встроенная графика не важны
        Assert.Equal(12, HardwareFingerprint.Of(a).Length);
        var c = Hw(new GpuInfo("NVIDIA GeForce RTX 3090", GpuVendor.Nvidia, 24 * GiB, false));
        Assert.NotEqual(HardwareFingerprint.Of(a), HardwareFingerprint.Of(c));
        Assert.NotEqual(HardwareFingerprint.Of(a), HardwareFingerprint.Of(a with { TotalRamBytes = 128 * GiB }));
    }
}
