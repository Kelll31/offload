using System.Text.RegularExpressions;
using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models;

/// <summary>
/// Выбор видеокарт для llama-server по списку устройств самой llama.cpp («--list-devices»), а не по порядку WMI:
/// порядок CUDA/Vulkan не обязан совпадать с порядком адаптеров Windows.
/// </summary>
/// <remarks>
/// Несколько подходящих карт одного бэкенда — модель делится между ними (--tensor-split пропорционально доступной
/// видеопамяти, --main-gpu — самая большая). Маленькие (&lt; 2 ГБ) и встроенные при наличии дискретных не используются.
/// Одна видеокарта — null: поведение как без этой функции.
/// </remarks>
public static partial class MultiGpuPlanner
{
    /// <param name="devices">Устройства llama.cpp; null или меньше двух — разделения нет.</param>
    /// <param name="hw">Оборудование: признак «встроенная» у видеокарт с тем же названием.</param>
    /// <param name="selection"><see cref="ServerSettings.GpuSelectionAll"/> или <see cref="ServerSettings.GpuSelectionPrimary"/>.</param>
    /// <param name="ignoreOtherUsage">
    /// Не вычитать занятую видеопамять (свободная память опрошена, когда наш сервер уже работал, — иначе модель «вытеснит» сама себя).
    /// </param>
    /// <returns>Разделение, одна выбранная карта (остальные исключены) или null — оставить как есть.</returns>
    public static GpuSplit? Plan(IReadOnlyList<LlamaDevice>? devices, HardwareInfo? hw, string? selection, bool ignoreOtherUsage = false)
    {
        if (devices is null || devices.Count < 2) return null;
        // Одна карта может быть видна через два бэкенда (CUDA и Vulkan в одной сборке) — берём бэкенд с наибольшей памятью.
        var group = devices.Where(d => d.TotalBytes > 0)
            .GroupBy(d => d.Backend, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(d => d.TotalBytes))
            .FirstOrDefault()?.ToList();
        if (group is null || group.Count < 2) return null;

        var hasDiscrete = group.Any(d => !IsIntegrated(d, hw));
        var usable = group.Where(d => d.TotalBytes >= FitCalculator.MinUsableVramBytes && !(hasDiscrete && IsIntegrated(d, hw))).ToList();
        if (usable.Count == 0) return null;

        var cards = usable.Select(d => new GpuSplitDevice(d.Name, d.Description, d.TotalBytes,
            FitCalculator.VramBudget(d.TotalBytes, ignoreOtherUsage ? 0 : Math.Max(0, d.TotalBytes - Math.Max(0, d.FreeBytes))))).ToList();
        var main = cards.Select((d, i) => (d, i)).OrderByDescending(x => x.d.TotalBytes).ThenByDescending(x => x.d.BudgetBytes).First().i;

        var primaryOnly = string.Equals(selection, ServerSettings.GpuSelectionPrimary, StringComparison.OrdinalIgnoreCase);
        // Одна карта из нескольких: явный --device, иначе llama.cpp сам задействовал бы все видимые устройства.
        if (primaryOnly || cards.Count == 1) return new GpuSplit([cards[main]], 0);
        return new GpuSplit(cards, main);
    }

    /// <summary>Встроенная графика: по признаку оборудования с тем же названием или по названию модели.</summary>
    internal static bool IsIntegrated(LlamaDevice d, HardwareInfo? hw)
    {
        if (hw?.Gpus.FirstOrDefault(g => SameName(g.Name, d.Description)) is { } known) return known.IsIntegrated;
        return IntegratedName().IsMatch(d.Description ?? "");
    }

    private static bool SameName(string a, string b)
    {
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        return a.Length > 0 && b.Length > 0
               && (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    // Intel UHD/HD/Iris/Arc без номера модели (Meteor Lake), AMD Radeon(TM) Graphics / Vega / 780M, Qualcomm Adreno.
    [GeneratedRegex(@"\b(UHD|HD) Graphics|\bIris\b|Arc\(TM\) Graphics|^Intel\(R\) Graphics|Radeon\(TM\) Graphics|Radeon Graphics|Radeon(\(TM\))? Vega|Radeon(\(TM\))? \d{3}M\b|Adreno",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntegratedName();
}
