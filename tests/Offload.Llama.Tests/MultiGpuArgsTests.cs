using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Llama.Tests;

/// <summary>Устройства llama.cpp (--list-devices), аргументы разделения по видеокартам и подобранных параметров.</summary>
public sealed class MultiGpuArgsTests
{
    private const string Exe = @"C:\pc\llama.cpp\b11102-cuda12\llama-server.exe";

    private static AppConfig Cfg(Action<ServerSettings>? edit = null)
    {
        var cfg = new AppConfig();
        cfg.Server.Port = 18080;
        edit?.Invoke(cfg.Server);
        return cfg;
    }

    private static InstalledModel Model(Action<InstalledModel>? edit = null)
    {
        var m = new InstalledModel { Id = "qwen3.6-35b-a3b", FilePath = @"C:\pc\models\q.gguf", NativeContext = 262144, RecommendedContext = 65536, IsMoe = true };
        edit?.Invoke(m);
        return m;
    }

    private static string? ValueOf(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private const string CudaOutput = """
        ggml_cuda_init: GGML_CUDA_FORCE_MMQ:    no
        ggml_cuda_init: found 2 CUDA devices:
          Device 0: NVIDIA GeForce RTX 3090, compute capability 8.6, VMM: yes
          Device 1: NVIDIA GeForce RTX 4090, compute capability 8.9, VMM: yes
        load_backend: loaded CUDA backend from C:\llama\ggml-cuda.dll
        load_backend: loaded CPU backend from C:\llama\ggml-cpu-haswell.dll
        Available devices:
          CUDA0: NVIDIA GeForce RTX 3090 (24575 MiB, 23306 MiB free)
          CUDA1: NVIDIA GeForce RTX 4090 (24563 MiB, 22873 MiB free)
        """;

    private const string VulkanOutput = "Available devices:\r\n" +
                                        "  Vulkan0: AMD Radeon RX 7900 XTX (24560 MiB, 23800 MiB free)\r\n" +
                                        "  Vulkan1: Intel(R) Arc(TM) A770 Graphics (16032 MiB, 15800 MiB free)\r\n" +
                                        "  Vulkan2: AMD Radeon(TM) Graphics (512 MiB, 400 MiB free)\r\n";

    [Fact]
    public void Parse_CudaListDevices()
    {
        var d = LlamaDevices.Parse(CudaOutput);
        Assert.Equal(2, d.Count);
        Assert.Equal("CUDA0", d[0].Name);
        Assert.Equal("CUDA", d[0].Backend);
        Assert.Equal("NVIDIA GeForce RTX 3090", d[0].Description);
        Assert.Equal(24575L * 1024 * 1024, d[0].TotalBytes);
        Assert.Equal(23306L * 1024 * 1024, d[0].FreeBytes);
        Assert.Equal("CUDA1", d[1].Name);
        Assert.Equal("NVIDIA GeForce RTX 4090", d[1].Description);
    }

    [Fact]
    public void Parse_VulkanWithParenthesesAndCrLf()
    {
        var d = LlamaDevices.Parse(VulkanOutput);
        Assert.Equal(["Vulkan0", "Vulkan1", "Vulkan2"], d.Select(x => x.Name).ToArray());
        Assert.Equal("Intel(R) Arc(TM) A770 Graphics", d[1].Description);
        Assert.Equal("Vulkan", d[1].Backend);
        Assert.Equal(16032L * 1024 * 1024, d[1].TotalBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Available devices:\n")]
    [InlineData("error: invalid argument: --list-devices\nusage: llama-server [options]")]
    [InlineData("  RPC[192.168.1.2:50052]: RPC backend (8000 MiB, 7000 MiB free)")]
    public void Parse_NoDevices(string output)
    {
        Assert.Empty(LlamaDevices.Parse(output));
    }

    [Fact]
    public void Parse_Sycl_AndDuplicatesIgnored()
    {
        var d = LlamaDevices.Parse("  SYCL0: Intel(R) Arc(TM) B580 Graphics (12216 MiB, 11000 MiB free)\n  SYCL0: dup (1 MiB, 1 MiB free)\n");
        var one = Assert.Single(d);
        Assert.Equal("SYCL", one.Backend);
    }

    private static GpuSplit TwoCards() => AutoTuneTests.TwoCards();

    [Fact]
    public void Split_MultiGpu_DeviceTensorSplitMainGpu()
    {
        var placement = ServerPlacement.Manual with { Split = TwoCards() };
        var plan = LlamaServerArgs.Build(Cfg(), Model(), Exe, placement);
        var a = plan.Arguments;
        Assert.Equal("CUDA0,CUDA1", ValueOf(a, "--device"));
        Assert.Equal("45,55", ValueOf(a, "-ts"));
        Assert.Equal("1", ValueOf(a, "-mg"));
        Assert.Equal("layer", ValueOf(a, "-sm"));
        Assert.Same(placement.Split, plan.Split);
        // Размещения «Авто» нет — контекст и выгрузка как без него.
        Assert.Null(plan.Placement);
        Assert.Equal("65536", ValueOf(a, "-c"));
        Assert.DoesNotContain("--n-cpu-moe", a);
    }

    [Fact]
    public void Split_SingleSelectedCard_OnlyDevice()
    {
        var one = new GpuSplit([new GpuSplitDevice("CUDA1", "NVIDIA GeForce RTX 4090", 24L << 30, 22L << 30)], 0);
        var a = LlamaServerArgs.Build(Cfg(), Model(), Exe, ServerPlacement.Manual with { Split = one }).Arguments;
        Assert.Equal("CUDA1", ValueOf(a, "--device"));
        Assert.DoesNotContain("-ts", a);
        Assert.DoesNotContain("-mg", a);
        Assert.DoesNotContain("-sm", a);
    }

    [Fact]
    public void NoSplit_SameAsBefore()
    {
        var before = LlamaServerArgs.Build(Cfg(), Model(), Exe).Arguments;
        Assert.DoesNotContain("--device", before);
        Assert.DoesNotContain("-ts", before);
        Assert.Equal(before, LlamaServerArgs.Build(Cfg(), Model(), Exe, ServerPlacement.Manual).Arguments);
    }

    [Fact]
    public void Split_UserDeviceFlagWins_OtherFlagsIndividually()
    {
        var placement = ServerPlacement.Manual with { Split = TwoCards() };
        var dev = LlamaServerArgs.Build(Cfg(s => s.ExtraArgs = "--device=CUDA1"), Model(), Exe, placement);
        Assert.DoesNotContain("-ts", dev.Arguments);
        Assert.DoesNotContain("-mg", dev.Arguments);
        Assert.Single(dev.Arguments, x => x.StartsWith("--device", StringComparison.Ordinal));
        Assert.Null(dev.Split);

        var sm = LlamaServerArgs.Build(Cfg(s => s.ExtraArgs = "-sm row -ts 1,1"), Model(), Exe, placement).Arguments;
        Assert.Equal("CUDA0,CUDA1", ValueOf(sm, "--device"));
        Assert.Single(sm, x => x == "-sm");
        Assert.Equal("row", ValueOf(sm, "-sm"));
        Assert.Equal("1,1", ValueOf(sm, "-ts"));
        Assert.Equal("1", ValueOf(sm, "-mg"));
    }

    [Fact]
    public void Tuned_AppliedOverSettings()
    {
        var tuned = new TunedProfile { FlashAttention = "on", CacheType = "f16", UBatch = 1024, Batch = 2048, CpuMoeDelta = -2, SplitMode = "row", Mtp = true };
        var placement = new ServerPlacement(32768, 12, "оценка") { Split = TwoCards(), Tuned = tuned };
        var plan = LlamaServerArgs.Build(Cfg(), Model(m => m.HasMtp = true), Exe, placement);
        var a = plan.Arguments;
        Assert.Equal("on", ValueOf(a, "-fa"));
        Assert.Equal("f16", ValueOf(a, "-ctk"));
        Assert.Equal("f16", ValueOf(a, "-ctv"));
        Assert.Equal("1024", ValueOf(a, "-ub"));
        Assert.Equal("2048", ValueOf(a, "-b"));
        Assert.Equal("10", ValueOf(a, "--n-cpu-moe"));
        Assert.Equal(10, plan.CpuMoeLayers);
        Assert.Equal("row", ValueOf(a, "-sm"));
        Assert.Equal("draft-mtp", ValueOf(a, "--spec-type"));
        Assert.Same(tuned, plan.Tuned);
        Assert.Equal("32768", ValueOf(a, "-c")); // контекст не меняется
    }

    [Fact]
    public void Tuned_CpuMoeDelta_OnlyWithAutoOffload()
    {
        var tuned = new TunedProfile { CpuMoeDelta = -3 };
        // Все эксперты в видеопамяти — поправка не применяется.
        Assert.DoesNotContain("--n-cpu-moe", LlamaServerArgs.Build(Cfg(), Model(), Exe, new ServerPlacement(32768, 0, "") { Tuned = tuned }).Arguments);
        // Поправка не опускает ниже нуля.
        var low = LlamaServerArgs.Build(Cfg(), Model(), Exe, new ServerPlacement(32768, 2, "") { Tuned = tuned });
        Assert.DoesNotContain("--n-cpu-moe", low.Arguments);
        Assert.Equal(0, low.CpuMoeLayers);
        // Выгрузку задал пользователь — его значение.
        var user = LlamaServerArgs.Build(Cfg(s => s.CpuMoeLayers = 7), Model(), Exe, new ServerPlacement(32768, 12, "") { Tuned = tuned });
        Assert.Equal("7", ValueOf(user.Arguments, "--n-cpu-moe"));
    }

    [Fact]
    public void Tuned_UserExtraArgsWin()
    {
        var tuned = new TunedProfile { FlashAttention = "on", UBatch = 2048, Batch = 2048 };
        var a = LlamaServerArgs.Build(Cfg(s => s.ExtraArgs = "--ubatch-size 256 -b=512 -fa off"), Model(), Exe, ServerPlacement.Manual with { Tuned = tuned }).Arguments;
        Assert.DoesNotContain("-ub", a);
        Assert.DoesNotContain("2048", a);
        Assert.Equal("256", ValueOf(a, "--ubatch-size"));
        Assert.Contains("-b=512", a);
        // -fa задан пользователем после подобранного: llama.cpp берёт последний.
        Assert.Equal("off", a[a.ToList().LastIndexOf("-fa") + 1]);
    }

    [Fact]
    public void TensorSplit_PercentsSumTo100()
    {
        var three = new GpuSplit(
        [
            new GpuSplitDevice("CUDA0", "A", 1, 10),
            new GpuSplitDevice("CUDA1", "B", 1, 10),
            new GpuSplitDevice("CUDA2", "C", 1, 10),
        ], 0);
        Assert.Equal(100, three.Percents.Sum());
        Assert.Equal("34,33,33", three.TensorSplitArg);
        Assert.Equal("RTX 3090 45% · RTX 4090 55%", TwoCards().Describe());
        Assert.Equal("RTX 4090", GpuSplit.ShortName("NVIDIA GeForce RTX 4090"));
        Assert.Equal("RX 7900 XTX", GpuSplit.ShortName("AMD Radeon RX 7900 XTX"));
    }
}
