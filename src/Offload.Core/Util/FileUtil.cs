using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Offload.Core.Util;

public static class FileUtil
{
    public static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Атомарная запись текста: во временный файл рядом, затем замена. Защищает от
    /// повреждения конфигов при сбое посреди записи.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content, Encoding? encoding = null)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, content, encoding ?? Utf8NoBom);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(full))
                    File.Replace(tmp, full, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, full);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            finally
            {
                if (attempt >= 10 && File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
        }
    }

    /// <summary>Сколько последних резервных копий одного файла хранится в папке backups.</summary>
    public const int BackupsPerFile = 10;

    /// <summary>Копии старше этого срока удаляются (при очередном вызове <see cref="Backup"/>).</summary>
    public static readonly TimeSpan BackupMaxAge = TimeSpan.FromDays(30);

    /// <summary>Сколько последних копий каждого файла хранится независимо от срока.</summary>
    public const int BackupsAlwaysKept = 3;

    private const string BackupStampFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>
    /// Имя копии: «ГГГГММДД-ЧЧММСС[-мс][-N]_путь». Старый формат (без миллисекунд) тоже распознаётся —
    /// для очистки ранее сделанных копий.
    /// </summary>
    private static readonly Regex BackupName =
        new(@"^(?<stamp>\d{8}-\d{6}(?:-\d{3})?)(?:-(?<n>\d+))?_(?<name>.+)$",
            RegexOptions.CultureInvariant);

    /// <summary>
    /// Резервная копия файла в папку backups (с меткой времени до миллисекунд; при совпадении имени — со счётчиком).
    /// Существующие копии никогда не перезаписываются. Заодно чистит папку: у каждого файла остаётся не больше
    /// <see cref="BackupsPerFile"/> последних копий, копии старше <see cref="BackupMaxAge"/> удаляются.
    /// Возвращает путь копии или null, если копию сделать не удалось.
    /// </summary>
    public static string? Backup(string path)
    {
        string dest;
        string safeName;
        try
        {
            if (!File.Exists(path)) return null;
            Directory.CreateDirectory(AppPaths.BackupsDir);
            safeName = path.Replace(':', '_').Replace('\\', '_').Replace('/', '_');
            // Длинный путь обрезается с начала; хеш полного пути не даёт двум файлам с общим хвостом делить одну квоту копий.
            if (safeName.Length > 150) safeName = ShortHash(path) + "~" + safeName[^140..];
            var stamp = DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture);
            dest = CopyToUnique(path, AppPaths.BackupsDir, stamp, safeName);
        }
        catch
        {
            return null;
        }
        PruneBackups(AppPaths.BackupsDir, safeName, keep: dest);
        return dest;
    }

    /// <summary>Копирование без перезаписи: если имя занято (в т.ч. другим процессом в ту же миллисекунду), добавляется счётчик.</summary>
    private static string CopyToUnique(string source, string dir, string stamp, string safeName)
    {
        const int maxAttempts = 1000;
        for (var n = 0; n < maxAttempts; n++)
        {
            var dest = Path.Combine(dir, n == 0 ? $"{stamp}_{safeName}" : $"{stamp}-{n}_{safeName}");
            if (File.Exists(dest)) continue;
            try
            {
                File.Copy(source, dest, overwrite: false);
                return dest;
            }
            catch (IOException) when (File.Exists(dest))
            {
                // Имя заняли между проверкой и копированием — берём следующее.
            }
        }
        throw new IOException(L.F("Не удалось подобрать свободное имя для резервной копии {0}", source));
    }

    /// <summary>
    /// Очистка папки копий: для файла <paramref name="safeName"/> — оставить <see cref="BackupsPerFile"/> последних;
    /// для всех файлов — удалить копии старше <see cref="BackupMaxAge"/>. Только что созданная копия не удаляется.
    /// Ошибки игнорируются (копия уже сделана, очистка — попутная).
    /// </summary>
    private static void PruneBackups(string dir, string safeName, string keep)
    {
        try
        {
            var now = DateTime.Now;
            var entries = new List<(string Path, string Name, DateTime Stamp, int N)>();
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var m = BackupName.Match(Path.GetFileName(file));
                if (!m.Success) continue;
                var s = m.Groups["stamp"].Value;
                if (!DateTime.TryParseExact(s, s.Length > 15 ? BackupStampFormat : "yyyyMMdd-HHmmss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
                    continue;
                var n = m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out var parsed) ? parsed : 0;
                entries.Add((file, m.Groups["name"].Value, stamp, n));
            }

            // По сроку удаляются только копии сверх BackupsAlwaysKept последних у каждого файла:
            // единственную копию давно не менявшегося конфига IDE терять нельзя.
            var toDelete = entries
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.OrderByDescending(e => e.Stamp).ThenByDescending(e => e.N).Skip(BackupsAlwaysKept))
                .Where(e => now - e.Stamp > BackupMaxAge)
                .Select(e => e.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries
                         .Where(e => string.Equals(e.Name, safeName, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(e => e.Stamp).ThenByDescending(e => e.N)
                         .Skip(BackupsPerFile))
                toDelete.Add(e.Path);
            toDelete.Remove(keep);
            // Самая старая копия каждого файла — обычно состояние «до Offload»: её не удаляем никогда.
            foreach (var g in entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                toDelete.Remove(g.OrderBy(e => e.Stamp).ThenBy(e => e.N).First().Path);

            foreach (var file in toDelete)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Занят или нет прав — удалим в следующий раз.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папка недоступна — не критично.
        }
    }

    /// <summary>8 hex-символов SHA-256 от полного пути без учёта регистра — различитель длинных имён копий.</summary>
    private static string ShortHash(string path)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Convert.ToHexStringLower(bytes, 0, 4);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = [L.T("Б"), L.T("КБ"), L.T("МБ"), L.T("ГБ"), L.T("ТБ")];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} {units[0]}" : $"{v:0.0} {units[i]}";
    }

    public static string FormatSpeed(double bytesPerSecond) => L.F("{0}/с", FormatBytes((long)bytesPerSecond));

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? L.F("{0} ч {1} мин", (int)t.TotalHours, t.Minutes)
        : t.TotalMinutes >= 1 ? L.F("{0} мин {1} с", t.Minutes, t.Seconds)
        : L.F("{0} с", Math.Max(0, t.Seconds));

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Может быть занята запущенным процессом — не критично.
        }
    }
}
