using System.Globalization;
using System.Text;
using Offload.Core.Usage;

namespace Offload.App.Services;

/// <summary>
/// Экспорт статистики в CSV (RFC 4180, запятая, UTF-8 с BOM — чтобы Excel распознал кодировку).
/// Числа и время — в инвариантной культуре; содержимого запросов в записях нет.
/// </summary>
internal static class UsageCsv
{
    private static readonly string[] Header =
    [
        "timestamp_utc", "local_time", "tool", "client", "model",
        "prompt_tokens", "completion_tokens", "estimated_saved_tokens", "duration_ms", "ok",
    ];

    public static string DefaultFileName(DateTime now) =>
        $"offload-usage-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv";

    public static string Build(IEnumerable<UsageRecord> records)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Header)).Append("\r\n");
        foreach (var r in records.OrderBy(r => r.TimestampUtc))
        {
            var utc = r.TimestampUtc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc) : r.TimestampUtc.ToUniversalTime();
            sb.Append(string.Join(",",
                utc.ToString("yyyy-MM-ddTHH:mm:ssZ", c),
                utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", c),
                Escape(r.Tool),
                Escape(r.Client),
                Escape(r.Model),
                r.PromptTokens.ToString(c),
                r.CompletionTokens.ToString(c),
                Math.Max(0, r.EstimatedSavedTokens).ToString(c),
                r.DurationMs.ToString(c),
                r.Ok ? "true" : "false")).Append("\r\n");
        }
        return sb.ToString();
    }

    public static void Write(string path, IEnumerable<UsageRecord> records) =>
        File.WriteAllText(path, Build(records), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>Поле CSV: в кавычках, если есть запятая, кавычка или перевод строки; формулы Excel (=, +, -, @) экранируются.</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value;
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
