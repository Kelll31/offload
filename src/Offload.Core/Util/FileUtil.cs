using System.Text;

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

    /// <summary>Резервная копия файла в папку backups (с меткой времени). Возвращает путь копии.</summary>
    public static string? Backup(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            Directory.CreateDirectory(AppPaths.BackupsDir);
            var safeName = path.Replace(':', '_').Replace('\\', '_').Replace('/', '_');
            if (safeName.Length > 150) safeName = safeName[^150..];
            var dest = Path.Combine(AppPaths.BackupsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safeName}");
            File.Copy(path, dest, overwrite: true);
            return dest;
        }
        catch
        {
            return null;
        }
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
