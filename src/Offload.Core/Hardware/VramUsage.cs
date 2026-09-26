using System.Globalization;
using System.Runtime.InteropServices;
using Offload.Core.Logging;

namespace Offload.Core.Hardware;

/// <summary>Занятость видеопамяти основной видеокарты.</summary>
/// <param name="TotalBytes">Объём видеопамяти карты.</param>
/// <param name="UsedBytes">Занято всеми процессами.</param>
/// <param name="OwnBytes">Из занятого — наш llama-server (0 — не запущен или неизвестно).</param>
/// <param name="OwnKnown">Долю нашего сервера удалось определить (или сервер не запущен и вычитать нечего).</param>
/// <param name="Source">Откуда данные: nvidia-smi или счётчики Windows (PDH).</param>
public sealed record VramUsage(long TotalBytes, long UsedBytes, long OwnBytes, bool OwnKnown, string Source)
{
    /// <summary>Занято другими программами (рабочий стол, браузер, игры, другие модели).</summary>
    public long OtherBytes => Math.Max(0, UsedBytes - OwnBytes);

    /// <summary>
    /// Другие программы заняли заметно больше обычного фона рабочего стола — модели достанется меньше,
    /// чем «объём карты − резерв». Без известной доли нашего сервера вывод ненадёжен.
    /// </summary>
    public bool OthersSignificant => OwnKnown && OtherBytes >= VramUsageProbe.SignificantOtherBytes;
}

/// <summary>
/// Текущая занятость видеопамяти: у NVIDIA — nvidia-smi (memory.used), у AMD/Intel — счётчики производительности Windows
/// «GPU Adapter Memory\Dedicated Usage» (WDDM 2.x, Windows 10 1709+). Долю своего llama-server — «GPU Process Memory» по PID.
/// </summary>
/// <remarks>
/// DXGI IDXGIAdapter3::QueryVideoMemoryInfo не подходит: он сообщает использование и бюджет только вызывающего процесса,
/// а нужна занятость карты другими программами. Счётчики PDH дают её для любой видеокарты без дополнительных пакетов.
/// Имена экземпляров PDH содержат LUID адаптера, а не название, поэтому для AMD/Intel берётся адаптер с наибольшей
/// занятостью выделенной памяти (у встроенной графики её почти нет).
/// </remarks>
public static class VramUsageProbe
{
    /// <summary>
    /// Порог «заметной» занятости другими программами: обычный фон рабочего стола укладывается в резерв FitCalculator (1 ГБ),
    /// заметная занятость — от 1,5 ГБ.
    /// </summary>
    public const long SignificantOtherBytes = 1536L * 1024 * 1024;

    /// <summary>Занятость видеопамяти основной дискретной карты; null — нет дискретной карты или данных.</summary>
    /// <param name="ownServerPid">PID нашего llama-server (если запущен): его доля не считается «чужой».</param>
    public static async Task<VramUsage?> QueryAsync(HardwareInfo hw, int? ownServerPid = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hw);
        if (hw.PrimaryGpu is not { IsIntegrated: false } gpu || gpu.DedicatedMemoryBytes <= 0) return null;
        try
        {
            long? own = 0;
            if (ownServerPid is int pid)
                own = await Task.Run(() => GpuPerfCounters.ProcessDedicatedBytes(pid), ct).ConfigureAwait(false);

            if (gpu.Vendor == GpuVendor.Nvidia && await HardwareDetector.QueryNvidiaMemoryAsync(ct).ConfigureAwait(false) is { } nv)
                return Build(nv.Total, nv.Used, own, "nvidia-smi");

            var used = await Task.Run(GpuPerfCounters.MaxAdapterDedicatedBytes, ct).ConfigureAwait(false);
            return used is long u ? Build(gpu.DedicatedMemoryBytes, u, own, "pdh") : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug("hardware", $"Занятость видеопамяти недоступна: {ex.Message}");
            return null;
        }
    }

    internal static VramUsage Build(long total, long used, long? own, string source)
    {
        used = Math.Max(0, used);
        var ownBytes = Math.Clamp(own ?? 0, 0, used);
        return new VramUsage(total, used, ownBytes, own is not null, source);
    }

    /// <summary>Наибольшее значение среди адаптеров (экземпляры «luid_…_phys_N»).</summary>
    internal static long? MaxAdapter(IEnumerable<(string Instance, long Value)> items)
    {
        long? max = null;
        foreach (var (_, v) in items)
        {
            if (v >= 0 && (max is null || v > max)) max = v;
        }
        return max;
    }

    /// <summary>Сумма по экземплярам процесса: «pid_1234_luid_0x…_phys_0» (по всем адаптерам).</summary>
    internal static long SumForPid(IEnumerable<(string Instance, long Value)> items, int pid)
    {
        var prefix = "pid_" + pid.ToString(CultureInfo.InvariantCulture) + "_";
        long sum = 0;
        foreach (var (name, v) in items)
        {
            if (v > 0 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) sum += v;
        }
        return sum;
    }
}

/// <summary>Счётчики производительности видеопамяти (pdh.dll): мгновенные значения, один сбор.</summary>
internal static class GpuPerfCounters
{
    private const string AdapterDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string ProcessDedicated = @"\GPU Process Memory(*)\Dedicated Usage";

    private const uint PdhFmtLarge = 0x00000400;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0;
    private const uint PdhCstatusNewData = 1;

    /// <summary>Наибольшая занятость выделенной видеопамяти среди адаптеров; null — счётчики недоступны.</summary>
    public static long? MaxAdapterDedicatedBytes() => Read(AdapterDedicated) is { } items ? VramUsageProbe.MaxAdapter(items) : null;

    /// <summary>Выделенная видеопамять процесса (по всем адаптерам); null — счётчики недоступны.</summary>
    public static long? ProcessDedicatedBytes(int pid) => Read(ProcessDedicated) is { } items ? VramUsageProbe.SumForPid(items, pid) : null;

    private static List<(string Instance, long Value)>? Read(string path)
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return null;
        try
        {
            if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var counter) != 0) return null;
            if (PdhCollectQueryData(query) != 0) return null;

            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, out _, IntPtr.Zero);
            if (status != PdhMoreData || size == 0) return status == 0 ? [] : null;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, out var count, buffer) != 0) return null;
                var itemSize = Marshal.SizeOf<PdhItem>();
                var result = new List<(string, long)>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhItem>(buffer + i * itemSize);
                    if (item.Value.CStatus is not (PdhCstatusValidData or PdhCstatusNewData)) continue;
                    result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.LargeValue));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = PdhCloseQuery(query);
        }
    }

    // PDH_FMT_COUNTERVALUE: DWORD CStatus + объединение (выравнивание 8).
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhValue
    {
        public uint CStatus;
        public long LargeValue;
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W.
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhItem
    {
        public IntPtr Name;
        public PdhValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
