using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Llama.Tests;

public sealed class RecommendTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static HardwareInfo Hw(bool arm64 = false, params GpuInfo[] gpus) =>
        new(gpus, 32 * Gb, 16 * Gb, "Test CPU", 16, true, arm64);

    private static GpuInfo Nvidia(string name, string driver, string? cc, long vramGb = 12) =>
        new(name, GpuVendor.Nvidia, vramGb * Gb, false, driver, cc);

    [Theory]
    // RTX 30/40 (готовый код в CUDA 12.4) → CUDA 12 при драйвере ≥ 527.41.
    [InlineData("NVIDIA GeForce RTX 3090", "616.92", "8.6", LlamaBackend.Cuda12)]
    [InlineData("NVIDIA GeForce RTX 3060", "530.30", "8.6", LlamaBackend.Cuda12)]
    [InlineData("NVIDIA GeForce RTX 4090", "581.15", "8.9", LlamaBackend.Cuda12)]
    [InlineData("NVIDIA GeForce RTX 3060", "520.00", "8.6", LlamaBackend.Vulkan)]
    // PTX JIT (Pascal/Turing) → CUDA 12 при драйвере ≥ 551.78.
    [InlineData("NVIDIA GeForce GTX 1080", "560.94", "6.1", LlamaBackend.Cuda12)]
    [InlineData("NVIDIA GeForce RTX 2070", "551.78", "7.5", LlamaBackend.Cuda12)]
    [InlineData("NVIDIA GeForce GTX 1080", "540.00", "6.1", LlamaBackend.Vulkan)]
    // Blackwell → только CUDA 13 (драйвер ≥ 580), иначе Vulkan.
    [InlineData("NVIDIA GeForce RTX 5090", "590.10", "12.0", LlamaBackend.Cuda13)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", "570.10", "12.0", LlamaBackend.Vulkan)]
    // Kepler — слишком старая для CUDA-сборок.
    [InlineData("NVIDIA GeForce GTX 780", "470.00", "3.5", LlamaBackend.Vulkan)]
    public void Nvidia_Matrix(string name, string driver, string? cc, LlamaBackend expected)
    {
        var rec = LlamaReleaseResolver.Recommend(Hw(false, Nvidia(name, driver, cc)));
        Assert.Equal(expected, rec.Backend);
        Assert.False(string.IsNullOrWhiteSpace(rec.ReasonRu));
        Assert.Contains(rec.Backend == LlamaBackend.Vulkan ? "Vulkan" : "CUDA", rec.ReasonRu);
    }

    [Fact]
    public void Rtx3090_ReasonMentionsIqQuantIssue()
    {
        var rec = LlamaReleaseResolver.Recommend(Hw(false, Nvidia("NVIDIA GeForce RTX 3090", "616.92", "8.6", 24)));
        Assert.Equal(LlamaBackend.Cuda12, rec.Backend);
        Assert.Contains("21255", rec.ReasonRu);
        Assert.Contains("CUDA 12.4", rec.ReasonRu);
    }

    [Fact]
    public void Nvidia_FromRegistry_WindowsDriverFormatAndNameHeuristics()
    {
        // Без nvidia-smi: версия драйвера в формате Windows, compute capability неизвестна.
        var g = new GpuInfo("NVIDIA GeForce RTX 4070", GpuVendor.Nvidia, 12 * Gb, false, "32.0.15.6109");
        Assert.Equal(new Version(561, 9), BackendAdvisor.ParseDriver("32.0.15.6109"));
        Assert.Equal(LlamaBackend.Cuda12, LlamaReleaseResolver.Recommend(Hw(false, g)).Backend);

        var bw = new GpuInfo("NVIDIA GeForce RTX 5080", GpuVendor.Nvidia, 16 * Gb, false, "32.0.15.8100");
        Assert.Equal(LlamaBackend.Cuda13, LlamaReleaseResolver.Recommend(Hw(false, bw)).Backend);
    }

    [Theory]
    [InlineData("616.92", 616, 92)]
    [InlineData("560", 560, 0)]
    [InlineData("31.0.15.5222", 552, 22)]
    [InlineData("", -1, -1)]
    [InlineData("abc", -1, -1)]
    public void ParseDriver(string s, int major, int minor)
    {
        var v = BackendAdvisor.ParseDriver(s);
        if (major < 0) Assert.Null(v);
        else Assert.Equal(new Version(major, minor), v);
    }

    [Fact]
    public void MixedNvidia_NeedsPtxDriver()
    {
        // RTX 3090 + GTX 1080: для Pascal нужен PTX JIT → драйвер ≥ 551.78.
        var hw = Hw(false, Nvidia("NVIDIA GeForce RTX 3090", "540.00", "8.6", 24), Nvidia("NVIDIA GeForce GTX 1080", "540.00", "6.1", 8));
        Assert.Equal(LlamaBackend.Vulkan, LlamaReleaseResolver.Recommend(hw).Backend);
    }

    [Fact]
    public void Amd_Rx7900_Vulkan_RocmOffered()
    {
        var hw = Hw(false, new GpuInfo("AMD Radeon RX 7900 XTX", GpuVendor.Amd, 24 * Gb, false, "31.0.24033.1003"));
        var rec = LlamaReleaseResolver.Recommend(hw);
        Assert.Equal(LlamaBackend.Vulkan, rec.Backend);
        Assert.Contains("ROCm", rec.ReasonRu);
        var list = LlamaReleaseResolver.AvailableBackends(hw);
        Assert.Contains(LlamaBackend.Rocm, list);
        Assert.Contains(LlamaBackend.Vulkan, list);
        Assert.DoesNotContain(LlamaBackend.Cuda12, list);
    }

    [Fact]
    public void Amd_Polaris_NoRocm()
    {
        var hw = Hw(false, new GpuInfo("Radeon RX 580 Series", GpuVendor.Amd, 8 * Gb, false));
        Assert.Equal(LlamaBackend.Vulkan, LlamaReleaseResolver.Recommend(hw).Backend);
        Assert.DoesNotContain(LlamaBackend.Rocm, LlamaReleaseResolver.AvailableBackends(hw));
    }

    [Fact]
    public void IntelArc_Vulkan_SyclOffered()
    {
        var hw = Hw(false, new GpuInfo("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel, 16 * Gb, false));
        Assert.Equal(LlamaBackend.Vulkan, LlamaReleaseResolver.Recommend(hw).Backend);
        Assert.Contains(LlamaBackend.Sycl, LlamaReleaseResolver.AvailableBackends(hw));
    }

    [Fact]
    public void NoGpu_Cpu()
    {
        var hw = Hw();
        var rec = LlamaReleaseResolver.Recommend(hw);
        Assert.Equal(LlamaBackend.Cpu, rec.Backend);
        Assert.Equal([LlamaBackend.Cpu], LlamaReleaseResolver.AvailableBackends(hw));
    }

    [Fact]
    public void IntegratedOnly_Cpu_VulkanOffered()
    {
        var hw = Hw(false, new GpuInfo("Intel(R) UHD Graphics 770", GpuVendor.Intel, 512L * 1024 * 1024, true));
        Assert.Equal(LlamaBackend.Cpu, LlamaReleaseResolver.Recommend(hw).Backend);
        Assert.Contains(LlamaBackend.Vulkan, LlamaReleaseResolver.AvailableBackends(hw));
    }

    [Fact]
    public void Arm64_CpuOrCuda13()
    {
        Assert.Equal(LlamaBackend.Cpu, LlamaReleaseResolver.Recommend(Hw(true)).Backend);
        Assert.Equal(LlamaBackend.Cpu, LlamaReleaseResolver.Recommend(
            Hw(true, new GpuInfo("Qualcomm(R) Adreno(TM) X1-85 GPU", GpuVendor.Other, 0, true))).Backend);
        var spark = Hw(true, Nvidia("NVIDIA GB10", "616.41", "12.1", 128));
        Assert.Equal(LlamaBackend.Cuda13, LlamaReleaseResolver.Recommend(spark).Backend);
        Assert.Equal([LlamaBackend.Cuda13, LlamaBackend.Cpu], LlamaReleaseResolver.AvailableBackends(spark));
    }

    [Fact]
    public void AvailableBackends_Nvidia()
    {
        var ampere = LlamaReleaseResolver.AvailableBackends(Hw(false, Nvidia("NVIDIA GeForce RTX 3090", "616.92", "8.6")));
        Assert.Equal([LlamaBackend.Cuda12, LlamaBackend.Cuda13, LlamaBackend.Vulkan, LlamaBackend.Cpu], ampere);

        var pascal = LlamaReleaseResolver.AvailableBackends(Hw(false, Nvidia("NVIDIA GeForce GTX 1080", "560.94", "6.1")));
        Assert.Equal([LlamaBackend.Cuda12, LlamaBackend.Vulkan, LlamaBackend.Cpu], pascal);

        var blackwell = LlamaReleaseResolver.AvailableBackends(Hw(false, Nvidia("NVIDIA GeForce RTX 5090", "590.10", "12.0")));
        Assert.Equal([LlamaBackend.Cuda13, LlamaBackend.Vulkan, LlamaBackend.Cpu], blackwell);
    }

    [Fact]
    public void SupportsCuda13()
    {
        Assert.True(BackendAdvisor.SupportsCuda13(Hw(false, Nvidia("NVIDIA GeForce RTX 3090", "616.92", "8.6"))));
        Assert.False(BackendAdvisor.SupportsCuda13(Hw(false, Nvidia("NVIDIA GeForce RTX 3090", "560.00", "8.6"))));
        Assert.False(BackendAdvisor.SupportsCuda13(Hw(false, Nvidia("NVIDIA GeForce GTX 1080", "616.92", "6.1"))));
        Assert.False(BackendAdvisor.SupportsCuda13(Hw()));
    }

    [Theory]
    [InlineData("AMD Radeon RX 6800 XT", true)]
    [InlineData("AMD Radeon RX 9070 XT", true)]
    [InlineData("AMD Radeon RX 5700 XT", true)]
    [InlineData("AMD Radeon PRO W7900", true)]
    [InlineData("AMD Radeon(TM) 780M", true)]
    [InlineData("AMD Radeon(TM) 8060S Graphics", true)]
    [InlineData("Radeon RX 580 Series", false)]
    [InlineData("AMD Radeon RX Vega 64", false)]
    public void RocmHeuristics(string name, bool expected) => Assert.Equal(expected, BackendAdvisor.IsRocmCapable(name));
}
