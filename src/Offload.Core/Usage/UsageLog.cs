using System.Text;
using System.Text.Json;
using Offload.Core.Ipc;
using Offload.Core.Util;

namespace Offload.Core.Usage;

/// <summary>Одна запись об обращении IDE к локальной модели через MCP.</summary>
public sealed record UsageRecord(
    DateTime TimestampUtc,
    string Tool,
    string? Client,
    long PromptTokens,
    long CompletionTokens,
    long DurationMs,
    bool Ok,
    string? Model = null,
    /// <summary>
    /// Оценка токенов, которые облачной модели НЕ пришлось прочитать/написать самой
    /// (объём прочитанных сервером файлов + сгенерированный локально код), минус размер ответа.
    /// </summary>
    long EstimatedSavedTokens = 0);

public sealed record UsageSummary(
    int Calls,
    int Failed,
    long PromptTokens,
    long CompletionTokens,
    long EstimatedSavedTokens,
    TimeSpan TotalDuration,
    IReadOnlyDictionary<string, int> CallsByTool,
    IReadOnlyDictionary<string, int> CallsByClient);

/// <summary>
/// Журнал использования в формате JSONL (%LOCALAPPDATA%\Offload\usage.jsonl).
/// Пишут MCP-процессы (их может быть несколько одновременно), читает трей.
/// </summary>
public static class UsageLog
{
    private static readonly object Lock = new();

    /// <summary>
    /// Записать обращение. Если трей запущен — запись передаётся ему по IPC (так статистика не теряется
    /// при виртуализации файловой системы MSIX), иначе дописывается в файл напрямую.
    /// </summary>
    public static void Append(UsageRecord record)
    {
        try
        {
            if (IpcClient.IsTrayRunning())
            {
                var json = JsonSerializer.Serialize(record, Json.Compact);
                var resp = Task.Run(() => IpcClient.SendAsync(
                    new IpcRequest(IpcCommands.RecordUsage, new Dictionary<string, string> { ["record"] = json }),
                    TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
                if (resp is { Ok: true }) return;
            }
        }
        catch
        {
            // Нет связи с треем — пишем сами.
        }
        AppendLocal(record);
    }

    /// <summary>Разбор записи, пришедшей по IPC (для обработчика в трее).</summary>
    public static UsageRecord? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<UsageRecord>(json, Json.Compact); }
        catch (JsonException) { return null; }
    }

    /// <summary>Дописать запись в usage.jsonl этого процесса (трей вызывает для записей из IPC).</summary>
    public static void AppendLocal(UsageRecord record)
    {
        var line = JsonSerializer.Serialize(record, Json.Compact) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (Lock)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.UsageFile)!);
                    using var fs = new FileStream(AppPaths.UsageFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                    fs.Write(bytes);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
                catch
                {
                    return;
                }
            }
        }
    }

    public static IReadOnlyList<UsageRecord> ReadAll(DateTime? sinceUtc = null)
    {
        var list = new List<UsageRecord>();
        try
        {
            if (!File.Exists(AppPaths.UsageFile)) return list;
            using var fs = new FileStream(AppPaths.UsageFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<UsageRecord>(line, Json.Compact);
                    if (r is not null && (sinceUtc is null || r.TimestampUtc >= sinceUtc)) list.Add(r);
                }
                catch (JsonException)
                {
                    // Повреждённая строка (например, обрыв записи) — пропускаем.
                }
            }
        }
        catch
        {
            // Нет доступа — пустая статистика.
        }
        return list;
    }

    public static UsageSummary Summarize(IEnumerable<UsageRecord> records)
    {
        var list = records as IReadOnlyCollection<UsageRecord> ?? records.ToList();
        return new UsageSummary(
            list.Count,
            list.Count(r => !r.Ok),
            list.Sum(r => r.PromptTokens),
            list.Sum(r => r.CompletionTokens),
            list.Sum(r => Math.Max(0, r.EstimatedSavedTokens)),
            TimeSpan.FromMilliseconds(list.Sum(r => r.DurationMs)),
            list.GroupBy(r => r.Tool).ToDictionary(g => g.Key, g => g.Count()),
            list.GroupBy(r => r.Client ?? "неизвестно").ToDictionary(g => g.Key, g => g.Count()));
    }

    public static void Clear()
    {
        lock (Lock)
        {
            try { File.Delete(AppPaths.UsageFile); } catch { }
        }
    }
}
