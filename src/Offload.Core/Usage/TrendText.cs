namespace Offload.Core.Usage;

/// <summary>Сравнение показателя с базой (например, сегодня — со средним за прошлую неделю).</summary>
/// <param name="Text">«+35 %», «−12 %», «как обычно».</param>
/// <param name="Direction">1 — больше базы, −1 — меньше, 0 — в пределах ±5 %.</param>
public readonly record struct Trend(string Text, int Direction);

public static class TrendText
{
    /// <summary>Порог «как обычно»: изменение меньше 5 % не показывается как рост или падение.</summary>
    public const double FlatBand = 0.05;

    /// <summary>Тренд или null, если сравнивать не с чем (база нулевая и текущее тоже, либо база неизвестна).</summary>
    public static Trend? Compare(double current, double baseline)
    {
        if (double.IsNaN(current) || double.IsNaN(baseline) || baseline < 0 || current < 0) return null;
        if (baseline <= 0) return current > 0 ? new Trend(L.T("новое"), 1) : null;
        var change = (current - baseline) / baseline;
        if (Math.Abs(change) < FlatBand) return new Trend(L.T("как обычно"), 0);
        var pct = Math.Round(change * 100);
        if (pct > 999) return new Trend("×" + Math.Round(current / baseline).ToString("0", L.Culture), 1);
        var sign = pct > 0 ? "+" : "−";
        return new Trend(L.F("{0}{1} %", sign, Math.Abs(pct).ToString("0", L.Culture)), pct > 0 ? 1 : -1);
    }

    /// <summary>Среднее за предыдущие дни (без сегодняшнего): значения по дням, старые → новые, последний — сегодня.</summary>
    public static double PreviousAverage(IReadOnlyList<double> daily, int days = 7)
    {
        ArgumentNullException.ThrowIfNull(daily);
        if (daily.Count < 2) return 0;
        var prev = daily.Take(daily.Count - 1).TakeLast(days).ToList();
        return prev.Count == 0 ? 0 : prev.Average();
    }
}
