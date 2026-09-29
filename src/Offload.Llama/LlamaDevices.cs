using System.Globalization;
using System.Text.RegularExpressions;
using Offload.Core.Hardware;
using Offload.Core.Logging;

namespace Offload.Llama;

/// <summary>
/// Видеокарты глазами llama.cpp: «llama-server --list-devices» (имена CUDA0/Vulkan1/SYCL0 для --device, объём и свободная
/// память). Порядок и имена устройств берутся у самой сборки, а не из WMI. Список кэшируется для сборки (путь и время
/// изменения exe); свободная память в кэше — на момент опроса.
/// </summary>
public static partial class LlamaDevices
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<LlamaDevice>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Разбор вывода «--list-devices»: строки «  CUDA0: NVIDIA GeForce RTX 4090 (24563 MiB, 22873 MiB free)».
    /// Прочие строки (журнал загрузки бэкендов, «Available devices:») пропускаются.
    /// </summary>
    public static IReadOnlyList<LlamaDevice> Parse(string? output)
    {
        var result = new List<LlamaDevice>();
        if (string.IsNullOrWhiteSpace(output)) return result;
        foreach (var raw in output.Split('\n'))
        {
            var m = DeviceLine().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            if (!long.TryParse(m.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)) continue;
            if (!long.TryParse(m.Groups["free"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var free)) free = 0;
            var name = m.Groups["name"].Value;
            if (result.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new LlamaDevice(name, m.Groups["desc"].Value.Trim(), total * 1024 * 1024, Math.Min(free, total) * 1024 * 1024));
        }
        return result;
    }

    /// <summary>Список устройств сборки llama.cpp (кэш на сборку); пустой — сборка без видеокарт или старая, без --list-devices.</summary>
    /// <param name="refresh">Опросить заново (свежая свободная память перед запуском сервера).</param>
    public static async Task<IReadOnlyList<LlamaDevice>> QueryAsync(string serverExePath, bool refresh = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExePath);
        var key = CacheKey(serverExePath);
        if (!refresh)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
            }
        }

        IReadOnlyList<LlamaDevice> devices;
        try
        {
            devices = Parse(string.Join("\n", await DeviceList.ListAsync(serverExePath, ct).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("llama", $"llama-server --list-devices: {ex.Message}");
            lock (CacheLock) return Cache.TryGetValue(key, out var old) ? old : [];
        }

        Log.Debug("llama", "Устройства llama.cpp: " + (devices.Count == 0 ? "нет"
            : string.Join("; ", devices.Select(d => $"{d.Name} {d.Description} {d.TotalBytes >> 20} МиБ (свободно {d.FreeBytes >> 20})"))));
        lock (CacheLock) Cache[key] = devices;
        return devices;
    }

    private static string CacheKey(string exe)
    {
        var full = Path.GetFullPath(exe);
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = default;
        }
        return full + "|" + stamp.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    // Описание может содержать скобки («Intel(R) Arc(TM) A770 Graphics») — объём берётся из последних скобок строки.
    [GeneratedRegex(@"^\s*(?<name>[A-Za-z][A-Za-z_\-]*\d+):\s+(?<desc>.+?)\s*\((?<total>\d+)\s*MiB,\s*(?<free>\d+)\s*MiB\s+free\)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();
}
