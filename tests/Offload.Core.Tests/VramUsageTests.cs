using Offload.Core.Hardware;

namespace Offload.Core.Tests;

/// <summary>Занятость видеопамяти: разбор счётчиков PDH и вычитание доли собственного llama-server.</summary>
public sealed class VramUsageTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void MaxAdapter_PicksBusiestAdapter()
    {
        Assert.Null(VramUsageProbe.MaxAdapter([]));
        Assert.Equal(6 * GiB, VramUsageProbe.MaxAdapter(
        [
            ("luid_0x00000000_0x0000C5E3_phys_0", 6 * GiB),
            ("luid_0x00000000_0x0000D1A2_phys_0", 300L * 1024 * 1024),
            ("luid_0x00000000_0x0000FFFF_phys_0", -1),
        ]));
    }

    [Fact]
    public void SumForPid_OnlyThatProcess_AllAdapters()
    {
        (string, long)[] items =
        [
            ("pid_1234_luid_0x00000000_0x0000C5E3_phys_0", 10 * GiB),
            ("pid_1234_luid_0x00000000_0x0000D1A2_phys_0", GiB),
            ("pid_12345_luid_0x00000000_0x0000C5E3_phys_0", 5 * GiB),
            ("pid_4321_luid_0x00000000_0x0000C5E3_phys_0", 2 * GiB),
        ];
        Assert.Equal(11 * GiB, VramUsageProbe.SumForPid(items, 1234));
        Assert.Equal(0, VramUsageProbe.SumForPid(items, 99));
    }

    [Fact]
    public void Build_OtherBytesExcludeOwnServer()
    {
        var running = VramUsageProbe.Build(24 * GiB, 20 * GiB, 17 * GiB, "pdh");
        Assert.Equal(3 * GiB, running.OtherBytes);
        Assert.True(running.OwnKnown);
        Assert.True(running.OthersSignificant);

        var idle = VramUsageProbe.Build(24 * GiB, GiB, 0, "nvidia-smi");
        Assert.Equal(GiB, idle.OtherBytes);
        Assert.False(idle.OthersSignificant); // фон рабочего стола

        // Долю сервера узнать не удалось — предупреждать о «чужой» памяти нельзя.
        var unknown = VramUsageProbe.Build(24 * GiB, 20 * GiB, null, "nvidia-smi");
        Assert.False(unknown.OwnKnown);
        Assert.Equal(20 * GiB, unknown.OtherBytes);
        Assert.False(unknown.OthersSignificant);

        // Счётчики разошлись во времени: своя доля не больше занятого.
        Assert.Equal(0, VramUsageProbe.Build(24 * GiB, 5 * GiB, 7 * GiB, "pdh").OtherBytes);
    }

    [Fact]
    public async Task Query_NoDiscreteGpu_Null()
    {
        var hw = new HardwareInfo([new GpuInfo("Intel UHD", GpuVendor.Intel, 128L * 1024 * 1024, IsIntegrated: true)],
            16 * GiB, 8 * GiB, "cpu", 8, true, false);
        Assert.Null(await VramUsageProbe.QueryAsync(hw, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Counters_ReadWithoutErrors()
    {
        // Только чтение счётчиков Windows: на машине без WDDM-счётчиков — null, но не исключение и не мусор.
        var adapter = GpuPerfCounters.MaxAdapterDedicatedBytes();
        Assert.True(adapter is null or >= 0);
        var own = GpuPerfCounters.ProcessDedicatedBytes(Environment.ProcessId);
        Assert.True(own is null or >= 0);

        var hw = await HardwareDetector.DetectAsync(TestContext.Current.CancellationToken);
        var usage = await VramUsageProbe.QueryAsync(hw, Environment.ProcessId, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"PDH: адаптер {adapter}, процесс {own}; {usage}");
        if (usage is not null)
        {
            Assert.True(usage.TotalBytes > 0);
            Assert.InRange(usage.UsedBytes, 0, Math.Max(usage.TotalBytes, usage.UsedBytes));
            Assert.InRange(usage.OwnBytes, 0, usage.UsedBytes);
        }
    }
}
