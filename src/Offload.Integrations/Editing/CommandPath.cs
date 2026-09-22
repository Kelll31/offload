using System.Runtime.InteropServices;
using System.Text;
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
        if (s.Contains('~')) s = ExpandShortName(s);
        return s.Length > 3 ? s.TrimEnd('\\') : s;
    }

    public static bool Same(string? a, string? b)
    {
        var na = Normalize(a);
        return na.Length > 0 && string.Equals(na, Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Похоже ли, что команда — это Offload (по имени файла или текущему exe).</summary>
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
            var sb = new StringBuilder(1024);
            var len = GetLongPathName(path, sb, (uint)sb.Capacity);
            if (len > sb.Capacity)
            {
                sb = new StringBuilder((int)len + 1);
                len = GetLongPathName(path, sb, (uint)sb.Capacity);
            }
            return len > 0 && len <= sb.Capacity ? sb.ToString() : path;
        }
        catch
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferSize);
}
