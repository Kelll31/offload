using Offload.Core.Config;
using Offload.Core.Usage;

namespace Offload.App.Services;

/// <summary>Статистика обращений к локальной модели за периоды и оценка экономии в долларах.</summary>
internal static class UsageStats
{
    public sealed record Period(string Title, UsageSummary Summary, double Dollars);

    public static IReadOnlyList<Period> Build(IReadOnlyList<UsageRecord> all, McpSettings prices, DateTime? nowLocal = null)
    {
        var now = nowLocal ?? DateTime.Now;
        var today = now.Date;
        var weekStart = today.AddDays(-6);

        var todayList = all.Where(r => Local(r).Date == today).ToList();
        var weekList = all.Where(r => Local(r).Date >= weekStart).ToList();

        return
        [
            Make(L.T("Сегодня"), todayList, prices),
            Make(L.T("За 7 дней"), weekList, prices),
            Make(L.T("За всё время"), all, prices),
        ];
    }

    /// <summary>
    /// Оценка в долларах: сгенерированный локально текст (не больше сэкономленного) — по цене выходных токенов
    /// облачной модели, остальные сэкономленные токены (прочитанные локально файлы) — по цене входных.
    /// </summary>
    public static double EstimateDollars(UsageSummary s, McpSettings prices)
    {
        var saved = Math.Max(0, s.EstimatedSavedTokens);
        var savedOut = Math.Min(Math.Max(0, s.CompletionTokens), saved);
        var savedIn = saved - savedOut;
        return savedIn * prices.CloudInputPricePerMTok / 1_000_000d + savedOut * prices.CloudOutputPricePerMTok / 1_000_000d;
    }

    public static string Dollars(double value) =>
        value >= 100 ? $"${value:0}" : value >= 1 ? $"${value:0.00}" : value > 0 ? $"${value:0.000}" : "$0";

    private static Period Make(string title, IReadOnlyCollection<UsageRecord> list, McpSettings prices)
    {
        var s = UsageLog.Summarize(list);
        return new Period(title, s, EstimateDollars(s, prices));
    }

    private static DateTime Local(UsageRecord r)
    {
        var t = r.TimestampUtc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc) : r.TimestampUtc;
        return t.ToLocalTime();
    }
}
