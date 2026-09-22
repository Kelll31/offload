namespace Offload.Core.Hardware;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Other }

public sealed record GpuInfo(
    string Name,
    GpuVendor Vendor,
    long DedicatedMemoryBytes,
    bool IsIntegrated,
    string? DriverVersion = null,
    string? ComputeCapability = null,
    long? MemoryUsedBytes = null)
{
    public double DedicatedMemoryGb => DedicatedMemoryBytes / 1024d / 1024d / 1024d;
}

public sealed record HardwareInfo(
    IReadOnlyList<GpuInfo> Gpus,
    long TotalRamBytes,
    long AvailableRamBytes,
    string CpuName,
    int LogicalCores,
    bool CpuHasAvx2,
    bool IsArm64)
{
    public double TotalRamGb => TotalRamBytes / 1024d / 1024d / 1024d;

    /// <summary>Лучшая дискретная видеокарта (с наибольшим объёмом памяти).</summary>
    public GpuInfo? PrimaryGpu =>
        Gpus.Where(g => !g.IsIntegrated).OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault()
        ?? Gpus.OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault();

    /// <summary>Объём видеопамяти основной дискретной карты, байт (0 — нет дискретной карты).</summary>
    public long PrimaryVramBytes => PrimaryGpu is { IsIntegrated: false } g ? g.DedicatedMemoryBytes : 0;

    public double PrimaryVramGb => PrimaryVramBytes / 1024d / 1024d / 1024d;

    public bool HasNvidia => Gpus.Any(g => g.Vendor == GpuVendor.Nvidia && !g.IsIntegrated);

    /// <summary>Версия драйвера NVIDIA (например, 576.52) или null.</summary>
    public Version? NvidiaDriverVersion
    {
        get
        {
            var s = Gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia)?.DriverVersion;
            return s is not null && Version.TryParse(s, out var v) ? v : null;
        }
    }
}
