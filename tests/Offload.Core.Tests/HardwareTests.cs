using Offload.Core.Hardware;

namespace Offload.Core.Tests;

public class HardwareTests
{
    [Fact]
    public async Task Detect_ReturnsSaneValues()
    {
        var hw = await HardwareDetector.DetectAsync(TestContext.Current.CancellationToken);
        Assert.True(hw.TotalRamBytes > 512L * 1024 * 1024);
        Assert.True(hw.LogicalCores > 0);
        Assert.False(string.IsNullOrWhiteSpace(hw.CpuName));
        var smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(smi))
        {
            Assert.True(hw.HasNvidia);
            Assert.True(hw.PrimaryVramBytes > 1L * 1024 * 1024 * 1024);
            Assert.NotNull(hw.NvidiaDriverVersion);
        }
        TestContext.Current.SendDiagnosticMessage($"GPU: {hw.PrimaryGpu?.Name} {hw.PrimaryVramGb:0.0} GB, driver {hw.NvidiaDriverVersion}, cc {hw.PrimaryGpu?.ComputeCapability}");
    }
}
