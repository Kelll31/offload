using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Offload.Core.Hardware;

/// <summary>
/// Короткий отпечаток оборудования для настроек «модель × железо» (подобранные параметры сервера): видеокарты
/// с объёмом памяти, объём ОЗУ, процессор. Другая видеокарта или добавленная память — другой отпечаток.
/// </summary>
public static class HardwareFingerprint
{
    public static string Of(HardwareInfo hw)
    {
        ArgumentNullException.ThrowIfNull(hw);
        var sb = new StringBuilder();
        foreach (var g in hw.Gpus.Where(g => !g.IsIntegrated)
                     .Select(g => $"{g.Name.Trim().ToUpperInvariant()}/{g.DedicatedMemoryBytes / (1024 * 1024)}")
                     .Order(StringComparer.Ordinal))
            sb.Append("gpu:").Append(g).Append(';');
        // ОЗУ — в целых ГиБ: доступная системе память немного плавает от загрузки к загрузке.
        sb.Append("ram:").Append(Math.Round(hw.TotalRamBytes / (1024d * 1024 * 1024)).ToString(CultureInfo.InvariantCulture)).Append(';');
        sb.Append("cpu:").Append(hw.CpuName.Trim().ToUpperInvariant()).Append(';');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
