using System.Globalization;
using System.Security.Cryptography;
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
    long EstimatedSavedTokens = 0)
{
    // Поля ниже добавлены позже: в старых записях их нет (читаются как значения по умолчанию), а старые версии трея
    // при разборе записи по IPC неизвестные свойства пропускают.

    /// <summary>Рабочая папка: имя + короткий хэш полного пути (сам путь не пишется), например «offload#1a2b3c4d».</summary>
    public string? Workspace { get; init; }

    /// <summary>Сколько вызов ждал слот GPU в очереди (мс).</summary>
    public long QueueWaitMs { get; init; }

    /// <summary>Скорость генерации последнего ответа модели (ток/с); null — модель не использовалась или скорость неизвестна.</summary>
    public double? GenerationTps { get; init; }
}

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
/// Журнал использования в формате JSONL. Пишут MCP-процессы (их может быть несколько одновременно) или трей по IPC, читает трей.
/// </summary>
/// <remarks>
/// Текущий месяц — %LOCALAPPDATA%\Offload\usage.jsonl (имя прежнее: совместимость со старыми версиями и проверкой простоя
/// сервера по времени записи). Когда начинается новый месяц, перед первой записью файл переименовывается в архив
/// usage-ГГГГ-ММ.jsonl (месяц последней записи в нём). Существующий у пользователя большой usage.jsonl просто уходит в архив
/// целиком. Читается всё: архивы и текущий файл. Запись и ротация — под именованным мьютексом, общим для процессов.
/// </remarks>
public static class UsageLog
{
    /// <summary>Ключ «клиент неизвестен» в <see cref="UsageSummary.CallsByClient"/> (переводится только при показе).</summary>
    public const string UnknownClient = "(unknown)";

    private const string ArchivePrefix = "usage-";
    private const string ArchiveExtension = ".jsonl";

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
            using var guard = CrossProcessLock.Acquire();
            try
            {
                RotateIfNeeded(DateTime.Now);
            }
            catch (Exception ex)
            {
                Logging.Log.Debug("usage", $"Ротация usage.jsonl: {ex.Message}");
            }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.UsageFile)!);
                    using var fs = new FileStream(AppPaths.UsageFile, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
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

    /// <summary>
    /// Месячная ротация: если usage.jsonl последний раз записан в прошлом месяце (по местному времени), он становится
    /// архивом usage-ГГГГ-ММ.jsonl. Вызывается под межпроцессной блокировкой. Возвращает путь архива или null.
    /// </summary>
    internal static string? RotateIfNeeded(DateTime nowLocal)
    {
        var current = AppPaths.UsageFile;
        var fi = new FileInfo(current);
        if (!fi.Exists) return null;
        var written = fi.LastWriteTime;
        if (written.Year == nowLocal.Year && written.Month == nowLocal.Month) return null;
        if (written > nowLocal) return null; // часы перевели назад — не архивируем «будущий» файл
        if (fi.Length == 0)
        {
            TryDelete(current);
            return null;
        }

        var month = written.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var dir = Path.GetDirectoryName(current)!;
        var target = Path.Combine(dir, ArchivePrefix + month + ArchiveExtension);
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(dir, $"{ArchivePrefix}{month}-{n}{ArchiveExtension}");
        try
        {
            File.Move(current, target);
            Logging.Log.Info("usage", $"Статистика за {month} перенесена в {Path.GetFileName(target)}");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл держит процесс старой версии — попробуем при следующей записи.
            Logging.Log.Debug("usage", $"Не удалось перенести usage.jsonl в архив: {ex.Message}");
            return null;
        }
    }

    /// <summary>Архивы прошлых месяцев (usage-ГГГГ-ММ*.jsonl) в хронологическом порядке.</summary>
    public static IReadOnlyList<string> ArchiveFiles()
    {
        try
        {
            var dir = Path.GetDirectoryName(AppPaths.UsageFile)!;
            if (!Directory.Exists(dir)) return [];
            return Directory.EnumerateFiles(dir, ArchivePrefix + "*" + ArchiveExtension)
                .Where(f => IsArchiveName(Path.GetFileName(f)))
                .OrderBy(ArchiveSortKey, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsArchiveName(string name)
    {
        // usage-2026-08.jsonl или usage-2026-08-2.jsonl
        if (!name.StartsWith(ArchivePrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(ArchiveExtension, StringComparison.OrdinalIgnoreCase))
            return false;
        var core = name[ArchivePrefix.Length..^ArchiveExtension.Length];
        return core.Length >= 7 && DateTime.TryParseExact(core[..7], "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    /// <summary>«2026-08» + номер части с ведущими нулями: usage-2026-08.jsonl раньше usage-2026-08-2.jsonl.</summary>
    private static string ArchiveSortKey(string path)
    {
        var core = Path.GetFileName(path)[ArchivePrefix.Length..^ArchiveExtension.Length];
        var part = core.Length > 8 && int.TryParse(core[8..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1;
        return core[..7] + "#" + part.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Все записи (архивы и текущий месяц). sinceUtc — только начиная с этого момента.</summary>
    public static IReadOnlyList<UsageRecord> ReadAll(DateTime? sinceUtc = null)
    {
        var list = new List<UsageRecord>();
        foreach (var archive in ArchiveFiles())
        {
            // Архив, записанный до начала периода, целиком старше — не читаем.
            if (sinceUtc is { } since && SafeWriteTimeUtc(archive) < since) continue;
            ReadFile(archive, 0, list, sinceUtc, out _);
        }
        ReadFile(AppPaths.UsageFile, 0, list, sinceUtc, out _);
        return list;
    }

    /// <summary>
    /// Прочитать полные строки файла начиная с offset. consumedTo — позиция после последней полной строки
    /// (недописанная последняя строка будет прочитана в следующий раз). Возвращает false, если файла нет или он недоступен.
    /// </summary>
    internal static bool ReadFile(string path, long offset, List<UsageRecord> into, DateTime? sinceUtc, out long consumedTo)
    {
        consumedTo = offset;
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > fs.Length) return true;
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[64 * 1024];
            var carry = new byte[4096];
            var carryLen = 0;
            long read = 0;
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                read += n;
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] != (byte)'\n') continue;
                    string line;
                    if (carryLen > 0)
                    {
                        Append(ref carry, ref carryLen, buf, start, i - start);
                        line = Encoding.UTF8.GetString(carry, 0, carryLen);
                        carryLen = 0;
                    }
                    else
                    {
                        line = Encoding.UTF8.GetString(buf, start, i - start);
                    }
                    AddLine(line, into, sinceUtc);
                    start = i + 1;
                }
                if (start < n) Append(ref carry, ref carryLen, buf, start, n - start);
            }
            consumedTo = offset + read - carryLen;
            return true;
        }
        catch
        {
            // Нет доступа — пустая статистика.
            return false;
        }
    }

    private static void Append(ref byte[] carry, ref int carryLen, byte[] src, int start, int count)
    {
        if (carryLen + count > carry.Length) Array.Resize(ref carry, Math.Max(carry.Length * 2, carryLen + count));
        Buffer.BlockCopy(src, start, carry, carryLen, count);
        carryLen += count;
    }

    private static void AddLine(string line, List<UsageRecord> into, DateTime? sinceUtc)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        try
        {
            var r = JsonSerializer.Deserialize<UsageRecord>(line, Json.Compact);
            if (r is not null && (sinceUtc is null || r.TimestampUtc >= sinceUtc)) into.Add(r);
        }
        catch (JsonException)
        {
            // Повреждённая строка (например, обрыв записи) — пропускаем.
        }
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
            list.GroupBy(r => string.IsNullOrWhiteSpace(r.Client) ? UnknownClient : r.Client).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>Удалить всю статистику: текущий месяц и архивы.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            using var guard = CrossProcessLock.Acquire();
            TryDelete(AppPaths.UsageFile);
            foreach (var archive in ArchiveFiles()) TryDelete(archive);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static DateTime SafeWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MaxValue; }
    }

    /// <summary>Именованный мьютекс на папку данных: запись и ротация из нескольких процессов не пересекаются.</summary>
    private sealed class CrossProcessLock : IDisposable
    {
        private readonly Mutex? _mutex;

        private CrossProcessLock(Mutex? mutex) => _mutex = mutex;

        public static CrossProcessLock Acquire()
        {
            Mutex? mutex = null;
            try
            {
                var key = Path.GetFullPath(AppPaths.UsageFile).ToUpperInvariant();
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
                mutex = new Mutex(false, @"Local\Offload.Usage." + hash);
                bool owned;
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { owned = true; } // владелец упал — мьютекс наш
                if (owned) return new CrossProcessLock(mutex);
                // Не дождались — пишем без межпроцессной блокировки (повторы при нарушении доступа остаются).
                mutex.Dispose();
                return new CrossProcessLock(null);
            }
            catch
            {
                mutex?.Dispose();
                return new CrossProcessLock(null);
            }
        }

        public void Dispose()
        {
            if (_mutex is null) return;
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }
}
