using System.Runtime.InteropServices;
using Offload.Core;

namespace Offload.Integrations.Editing;

/// <summary>Сравнение путей к exe из чужих конфигов: регистр, прямые/обратные слеши, кавычки, «..», короткие имена 8.3.</summary>
internal static class CommandPath
{
    private static readonly string[] OurFileNames = ["Offload.exe", "Offload.Mcp.exe", "Offload"];

    public static string Normalize(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var s = command.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        s = s.Replace('/', '\\');
        try
        {
            if (Path.IsPathFullyQualified(s)) s = Path.GetFullPath(s);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Оставляем как есть — сравним «как строку».
        }
        // Короткие имена 8.3 раскрываются только для локальных путей: обращение к \\server\share выдало бы учётные данные NTLM.
        if (s.Contains('~') && !IsUnc(s)) s = ExpandShortName(s);
        return s.Length > 3 ? s.TrimEnd('\\') : s;
    }

    public static bool Same(string? a, string? b)
    {
        var na = Normalize(a);
        return na.Length > 0 && string.Equals(na, Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Сетевой или «аппаратный» путь (\\server\share\…, //server/…, \\?\…, \\.\…). К таким путям Offload не обращается
    /// (ни File.Exists, ни чтение версии, ни запуск) — это проверяется до любого доступа к файловой системе.
    /// </summary>
    public static bool IsUnc(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var s = command.Trim().Trim('"').TrimStart();
        return s.Length >= 2 && s[0] is '\\' or '/' && s[1] is '\\' or '/';
    }

    /// <summary>
    /// Локальный абсолютный путь, по которому файла нет (программу переместили или удалили). Сетевые и относительные пути
    /// (поиск по PATH) — false: их состояние не проверяем.
    /// </summary>
    public static bool IsMissingLocalFile(string? command)
    {
        if (IsUnc(command)) return false;
        var n = Normalize(command);
        if (n.Length == 0 || IsUnc(n)) return false;
        try
        {
            return Path.IsPathFullyQualified(n) && !File.Exists(n);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Похоже ли, что команда — это Offload (по имени файла или текущему exe). Это только признак «нашей» записи для правки
    /// конфигов (обновить/удалить свою запись), но не основание что-либо запускать: файл с именем Offload.exe может лежать
    /// где угодно. Запуск (доктор интеграций) разрешается только для точного пути текущей или установленной копии.
    /// </summary>
    public static bool IsOffload(string? command, string? specCommand = null)
    {
        var n = Normalize(command);
        if (n.Length == 0) return false;
        if (specCommand is not null && Same(n, specCommand)) return true;
        if (Same(n, AppPaths.ExecutablePath)) return true;
        var name = Path.GetFileName(n);
        return OurFileNames.Any(f => string.Equals(name, f, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExpandShortName(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            var buffer = new char[1024];
            var len = GetLongPathName(path, buffer, (uint)buffer.Length);
            if (len > buffer.Length)
            {
                buffer = new char[(int)len + 1];
                len = GetLongPathName(path, buffer, (uint)buffer.Length);
            }
            return len > 0 && len <= buffer.Length ? new string(buffer, 0, (int)len) : path;
        }
        catch
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathName(string shortPath, char[] longPath, uint bufferSize);
}
