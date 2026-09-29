using System.Globalization;

namespace Offload.Core.Hardware;

/// <summary>Устройство llama.cpp из вывода «llama-server --list-devices»: CUDA0, Vulkan1, SYCL0…</summary>
/// <param name="Name">Имя устройства для --device (CUDA0).</param>
/// <param name="Description">Название видеокарты (NVIDIA GeForce RTX 4090).</param>
/// <param name="TotalBytes">Объём видеопамяти.</param>
/// <param name="FreeBytes">Свободно в момент опроса.</param>
public sealed record LlamaDevice(string Name, string Description, long TotalBytes, long FreeBytes)
{
    /// <summary>Бэкенд — имя без номера: CUDA, Vulkan, SYCL, ROCm.</summary>
    public string Backend => Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
}

/// <summary>Видеокарта в разделении модели: имя устройства llama.cpp и доступный модели объём.</summary>
/// <param name="BudgetBytes">Сколько видеопамяти карты можно отдать модели (объём минус резерв и занятое другими).</param>
public sealed record GpuSplitDevice(string Name, string Description, long TotalBytes, long BudgetBytes);

/// <summary>
/// Разделение модели между видеокартами: --device, --tensor-split (пропорционально доступной видеопамяти),
/// --main-gpu (самая большая карта) и --split-mode. Одно устройство — только --device (остальные карты не используются).
/// </summary>
/// <param name="Devices">Устройства в порядке llama.cpp (индексы --main-gpu и --tensor-split — по этому списку).</param>
/// <param name="MainIndex">Индекс основной карты в <paramref name="Devices"/>.</param>
/// <param name="SplitMode">--split-mode: layer (по умолчанию) или row.</param>
public sealed record GpuSplit(IReadOnlyList<GpuSplitDevice> Devices, int MainIndex, string SplitMode = GpuSplit.LayerMode)
{
    public const string LayerMode = "layer";
    public const string RowMode = "row";

    /// <summary>Модель делится между несколькими картами (иначе — только выбор одной карты).</summary>
    public bool IsMulti => Devices.Count > 1;

    public long TotalBytes => Devices.Sum(d => d.TotalBytes);

    public long BudgetBytes => Devices.Sum(d => d.BudgetBytes);

    /// <summary>Доли карт в процентах (целые, в сумме 100, каждая не меньше 1).</summary>
    public IReadOnlyList<int> Percents
    {
        get
        {
            var n = Devices.Count;
            if (n == 0) return [];
            var weights = Devices.Select(d => (double)Math.Max(0, d.BudgetBytes)).ToArray();
            var sum = weights.Sum();
            if (sum <= 0) weights = Devices.Select(d => (double)Math.Max(1, d.TotalBytes)).ToArray();
            sum = weights.Sum();
            // Метод наибольшего остатка: сумма ровно 100.
            var exact = weights.Select(w => w / sum * 100).ToArray();
            var result = exact.Select(e => Math.Max(1, (int)Math.Floor(e))).ToArray();
            var order = Enumerable.Range(0, n).OrderByDescending(i => exact[i] - Math.Floor(exact[i])).ThenBy(i => i).ToArray();
            for (var k = 0; result.Sum() < 100; k++) result[order[k % n]]++;
            for (var k = 0; result.Sum() > 100; k++)
            {
                var i = Array.IndexOf(result, result.Max());
                result[i]--;
            }
            return result;
        }
    }

    /// <summary>Значение --device: «CUDA0,CUDA1».</summary>
    public string DeviceArg => string.Join(",", Devices.Select(d => d.Name));

    /// <summary>Значение --tensor-split: «60,40».</summary>
    public string TensorSplitArg => string.Join(",", Percents.Select(p => p.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Для интерфейса: «RTX 4090 60% · RTX 3090 40%».</summary>
    public string Describe()
    {
        if (!IsMulti) return Devices.Count == 1 ? ShortName(Devices[0].Description) : "";
        var p = Percents;
        return string.Join(" · ", Devices.Select((d, i) => $"{ShortName(d.Description)} {p[i]}%"));
    }

    /// <summary>Название карты без производителя: «NVIDIA GeForce RTX 4090» → «RTX 4090».</summary>
    public static string ShortName(string description)
    {
        var s = (description ?? "").Trim();
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) ", "Intel " })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && s.Length > prefix.Length)
                return s[prefix.Length..].Trim();
        }
        return s;
    }
}
