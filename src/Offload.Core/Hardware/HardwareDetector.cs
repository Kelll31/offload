using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Offload.Core.Logging;
using Offload.Core.Processes;

namespace Offload.Core.Hardware;

/// <summary>
/// Определение оборудования: видеокарты (nvidia-smi + реестр драйверов дисплея), ОЗУ, ЦП.
/// WMI AdapterRAM ограничен 4 ГБ, поэтому объём видеопамяти берётся из
/// HardwareInformation.qwMemorySize в ветке класса дисплейных адаптеров.
/// </summary>
public static class HardwareDetector
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static async Task<HardwareInfo> DetectAsync(CancellationToken ct = default)
    {
        var gpus = new List<GpuInfo>();

        var nvidia = await QueryNvidiaSmiAsync(ct);
        gpus.AddRange(nvidia);

        foreach (var g in QueryRegistryAdapters())
        {
            // NVIDIA-карты уже известны точно из nvidia-smi.
            if (g.Vendor == GpuVendor.Nvidia && nvidia.Count > 0) continue;
            if (gpus.Any(x => string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase))) continue;
            gpus.Add(g);
        }

        var (total, avail) = GetMemory();
        var info = new HardwareInfo(
            gpus,
            total,
            avail,
            GetCpuName(),
            Environment.ProcessorCount,
            System.Runtime.Intrinsics.X86.Avx2.IsSupported,
            RuntimeInformation.OSArchitecture == Architecture.Arm64);

        Log.Info("hardware",
            $"ЦП: {info.CpuName} ({info.LogicalCores} потоков, AVX2={info.CpuHasAvx2}); ОЗУ: {info.TotalRamGb:0.0} ГБ; " +
            "GPU: " + (gpus.Count == 0 ? "нет" : string.Join("; ", gpus.Select(g => $"{g.Name} {g.DedicatedMemoryGb:0.0} ГБ{(g.IsIntegrated ? " (встроенная)" : "")}"))));
        return info;
    }

    /// <summary>Текущее использование видеопамяти NVIDIA (для панели статуса). null — недоступно.</summary>
    public static async Task<(long Used, long Total)?> QueryNvidiaMemoryAsync(CancellationToken ct = default)
    {
        var list = await QueryNvidiaSmiAsync(ct);
        var g = list.OrderByDescending(x => x.DedicatedMemoryBytes).FirstOrDefault();
        return g?.MemoryUsedBytes is long used ? (used, g.DedicatedMemoryBytes) : null;
    }

    private static async Task<List<GpuInfo>> QueryNvidiaSmiAsync(CancellationToken ct)
    {
        var result = new List<GpuInfo>();
        var exe = FindNvidiaSmi();
        if (exe is null) return result;
        try
        {
            // В новых драйверах driver_version помечен устаревшим в пользу kmd_version; на старых kmd_version нет.
            var r = await ProcessRunner.RunAsync(exe,
                ["--query-gpu=name,memory.total,memory.used,kmd_version,compute_cap", "--format=csv,noheader,nounits"],
                timeout: TimeSpan.FromSeconds(10), ct: ct);
            if (!r.Success)
                r = await ProcessRunner.RunAsync(exe,
                    ["--query-gpu=name,memory.total,memory.used,driver_version,compute_cap", "--format=csv,noheader,nounits"],
                    timeout: TimeSpan.FromSeconds(10), ct: ct);
            if (!r.Success) return result;
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 4) continue;
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMib);
                long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedMib);
                result.Add(new GpuInfo(
                    parts[0],
                    GpuVendor.Nvidia,
                    totalMib * 1024 * 1024,
                    IsIntegrated: false,
                    DriverVersion: parts[3],
                    ComputeCapability: parts.Length > 4 ? parts[4] : null,
                    MemoryUsedBytes: usedMib * 1024 * 1024));
            }
        }
        catch (Exception ex)
        {
            Log.Debug("hardware", $"nvidia-smi недоступен: {ex.Message}");
        }
        return result;
    }

    private static string? FindNvidiaSmi()
    {
        var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(sys)) return sys;
        var nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        if (File.Exists(nvsmi)) return nvsmi;
        return ProcessRunner.FindOnPath("nvidia-smi.exe");
    }

    private static IEnumerable<GpuInfo> QueryRegistryAdapters()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return list;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!int.TryParse(sub, out _)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k is null) continue;
                var name = k.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var matching = (k.GetValue("MatchingDeviceId") as string ?? "").ToUpperInvariant();
                if (matching.Length == 0 || matching.Contains("ROOT\\") || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Remote", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    continue;

                long mem = 0;
                var q = k.GetValue("HardwareInformation.qwMemorySize");
                if (q is long ql) mem = ql;
                else if (q is byte[] qb && qb.Length >= 8) mem = BitConverter.ToInt64(qb, 0);
                if (mem <= 0)
                {
                    var m = k.GetValue("HardwareInformation.MemorySize");
                    if (m is int mi) mem = (uint)mi;
                    else if (m is long ml) mem = ml;
                    else if (m is byte[] mb && mb.Length >= 4) mem = BitConverter.ToUInt32(mb, 0);
                }

                var vendor = matching.Contains("VEN_10DE") ? GpuVendor.Nvidia
                    : matching.Contains("VEN_1002") || matching.Contains("VEN_1022") ? GpuVendor.Amd
                    : matching.Contains("VEN_8086") ? GpuVendor.Intel
                    : GpuVendor.Other;

                var integrated = vendor == GpuVendor.Intel && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
                                 || vendor == GpuVendor.Amd && mem < 2L * 1024 * 1024 * 1024
                                 || mem < 1L * 1024 * 1024 * 1024;

                list.Add(new GpuInfo(name.Trim(), vendor, mem, integrated, k.GetValue("DriverVersion") as string));
            }
        }
        catch (Exception ex)
        {
            Log.Debug("hardware", $"Не удалось прочитать реестр видеоадаптеров: {ex.Message}");
        }
        return list;
    }

    private static string GetCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? L.T("Неизвестный процессор");
        }
        catch
        {
            return L.T("Неизвестный процессор");
        }
    }

    private static (long Total, long Available) GetMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status)
            ? ((long)status.ullTotalPhys, (long)status.ullAvailPhys)
            : (0, 0);
    }

    /// <summary>Свободное место на диске, где расположен путь.</summary>
    public static long GetFreeDiskBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
