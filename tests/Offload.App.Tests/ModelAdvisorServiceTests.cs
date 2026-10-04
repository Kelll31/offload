using Offload.App.Services;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Models;

namespace Offload.App.Tests;

/// <summary>Автоподбор модели и параметров: когда советовать смену, как читаются расхождения, тексты уведомления.</summary>
[Collection("AppPaths")]
public sealed class ModelAdvisorServiceTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static HardwareInfo Hw(int vramGb, int ramGb)
    {
        var gpus = vramGb > 0
            ? new[] { new GpuInfo("Test GPU", GpuVendor.Nvidia, vramGb * GiB - 200 * 1024 * 1024, IsIntegrated: false) }
            : new[] { new GpuInfo("Intel UHD", GpuVendor.Intel, 128L * 1024 * 1024, IsIntegrated: true) };
        var ram = ramGb * GiB - 200 * 1024 * 1024;
        return new HardwareInfo(gpus, ram, ram / 2, "Test CPU", 16, CpuHasAvx2: true, IsArm64: false);
    }

    private static AppConfig WithActive(string id, bool custom = false)
    {
        var cfg = new AppConfig { SetupCompleted = true };
        cfg.Models.Installed.Add(new InstalledModel { Id = id, DisplayName = id, IsCustom = custom });
        cfg.Models.ActiveModelId = id;
        return cfg;
    }

    [Fact]
    public void Describe_ActiveIsBest_NothingToSwitch()
    {
        using var home = new TempHome();
        var hw = Hw(24, 64);
        var best = ModelAdvisorService.Compute(hw, new AppConfig()).Best!;
        var cfg = WithActive(best.Model.Id);

        var text = ModelAdvisorService.Describe(ModelAdvisorService.Compute(hw, cfg), hw, cfg)!;

        Assert.True(text.BestIsActive);
        Assert.False(text.ModelDiffers);
        Assert.Contains(best.Model.LocalizedDisplayName, text.Headline);
    }

    [Fact]
    public void Describe_NoActiveModel_SuggestsBest()
    {
        using var home = new TempHome();
        var hw = Hw(24, 64);
        var cfg = new AppConfig { SetupCompleted = true };

        var text = ModelAdvisorService.Describe(ModelAdvisorService.Compute(hw, cfg), hw, cfg)!;

        Assert.True(text.ModelDiffers);
        Assert.False(text.BestInstalled);
        Assert.False(text.BestIsActive);
    }

    [Fact]
    public void Describe_CustomActiveModel_IsNotNagged()
    {
        using var home = new TempHome();
        var hw = Hw(24, 64);
        var cfg = WithActive("my-own-gguf", custom: true);

        var text = ModelAdvisorService.Describe(ModelAdvisorService.Compute(hw, cfg), hw, cfg)!;

        Assert.False(text.ModelDiffers); // своя модель — выбор пользователя
    }

    [Fact]
    public void Describe_TinyActiveModelOnBigGpu_SuggestsBetter()
    {
        using var home = new TempHome();
        var hw = Hw(24, 64);
        var smallest = ModelCatalog.ChatModels.OrderBy(m => m.ApproxSizeBytes).First();
        var cfg = WithActive(smallest.Id);

        var text = ModelAdvisorService.Describe(ModelAdvisorService.Compute(hw, cfg), hw, cfg)!;

        Assert.True(text.ModelDiffers, smallest.Id);
    }

    [Fact]
    public void Describe_ParamsDiffer_WhenSlotsOrCacheDiffer()
    {
        using var home = new TempHome();
        var hw = Hw(24, 64);
        var cfg = new AppConfig { SetupCompleted = true };
        var advice = ModelAdvisorService.Compute(hw, cfg);
        var p = advice.Params!;

        cfg.Server.Parallel = p.Parallel;
        cfg.Server.CacheType = p.CacheType;
        Assert.False(ModelAdvisorService.Describe(advice, hw, cfg)!.ParamsDiffer);

        cfg.Server.Parallel = p.Parallel == 1 ? 3 : 1;
        Assert.True(ModelAdvisorService.Describe(advice, hw, cfg)!.ParamsDiffer);

        cfg.Server.Parallel = p.Parallel;
        cfg.Server.CacheType = p.CacheType == "f16" ? "q8_0" : "f16";
        Assert.True(ModelAdvisorService.Describe(advice, hw, cfg)!.ParamsDiffer);
    }

    [Fact]
    public void Describe_ManualContext_IsMentioned()
    {
        using var home = new TempHome();
        var hw = Hw(24, 64);
        var cfg = new AppConfig { SetupCompleted = true };
        cfg.Server.ContextSize = 12000;
        var text = ModelAdvisorService.Describe(ModelAdvisorService.Compute(hw, cfg), hw, cfg)!;
        Assert.Contains("вручную", text.Params);
    }

    [Fact]
    public void Notice_NamesTheBestModelAndDistinguishesHardwareChange()
    {
        using var home = new TempHome();
        var hw = Hw(16, 32);
        var cfg = new AppConfig { SetupCompleted = true };
        var advice = ModelAdvisorService.Compute(hw, cfg);
        var text = ModelAdvisorService.Describe(advice, hw, cfg)!;

        var (changedTitle, body) = ModelAdvisorService.Notice(text, advice, hardwareChanged: true);
        var (firstTitle, _) = ModelAdvisorService.Notice(text, advice, hardwareChanged: false);

        Assert.NotEqual(changedTitle, firstTitle);
        Assert.Contains(advice.Best!.Model.LocalizedDisplayName, body);
        Assert.Contains(advice.Best.Quant, body);
    }

    [Fact]
    public void WatchHardware_DefaultsOn_AndFingerprintIsNotStoredYet()
    {
        var m = new ModelSettings();
        Assert.True(m.WatchHardware);
        Assert.Null(m.AdvisorFingerprint);
    }
}
