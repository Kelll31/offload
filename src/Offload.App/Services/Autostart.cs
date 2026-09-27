using Microsoft.Win32;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.App.Services;

/// <summary>Автозапуск вместе с Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run, значение «Offload».</summary>
internal static class Autostart
{
    public const string BackgroundArg = "--background";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Offload";

    /// <summary>Команда автозапуска для текущего exe.</summary>
    public static string Command => $"\"{AppPaths.ExecutablePath}\" {BackgroundArg}";

    /// <summary>Текущее значение в реестре (null — автозапуск выключен).</summary>
    public static string? CurrentValue
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return k?.GetValue(ValueName) as string;
            }
            catch (Exception ex)
            {
                Log.Warn("autostart", $"Не удалось прочитать автозапуск: {ex.Message}");
                return null;
            }
        }
    }

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(CurrentValue);

    /// <summary>Значение указывает на этот exe (после перемещения программы — нет).</summary>
    public static bool IsCurrent => string.Equals(CurrentValue?.Trim(), Command, StringComparison.OrdinalIgnoreCase);

    /// <summary>Путь к exe из значения автозапуска («"C:\…\Offload.exe" --background» или без кавычек до пробела); null — значения нет.</summary>
    internal static string? ExeOf(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v[0] == '"')
        {
            var end = v.IndexOf('"', 1);
            return end > 1 ? v[1..end] : null;
        }
        var space = v.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? v : v[..space];
    }

    /// <summary>
    /// Решение при старте трея. Значение в реестре — источник истины. Путь переписывается на этот exe, только если автозапуск
    /// включён в настройках и прежний exe больше не существует (программу переместили) — автозапуск другой существующей копии
    /// Offload (установленной, портативной, dev-сборки) не перехватывается, как и её подключения к IDE.
    /// Repoint — переписать путь; Enabled — включён ли автозапуск именно этой копии после решения.
    /// </summary>
    internal static (bool Repoint, bool Enabled) Decide(bool setupCompleted, bool wanted, string? value, string command, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(value)) return (false, false);
        if (string.Equals(value.Trim(), command, StringComparison.OrdinalIgnoreCase)) return (false, true);
        var exe = ExeOf(value);
        var moved = exe is not null && !exists(exe);
        return setupCompleted && wanted && moved ? (true, true) : (false, false);
    }

    /// <summary>
    /// Значение автозапуска принадлежит этой копии: указывает на <paramref name="exe"/> или на уже несуществующий файл
    /// (его можно убрать при удалении программы). Автозапуск другой существующей копии — нет.
    /// </summary>
    internal static bool OwnedBy(string? value, string exe, Func<string, bool> exists)
    {
        if (ExeOf(value) is not { } target) return false;
        return string.Equals(Full(target), Full(exe), StringComparison.OrdinalIgnoreCase) || !exists(target);

        static string Full(string p)
        {
            try
            {
                return Path.GetFullPath(p);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return p;
            }
        }
    }

    public static void Set(bool enabled)
    {
        if (DevMode.Active)
        {
            Log.Info("autostart", $"Режим разработчика ({DevMode.EnvVar}) — автозапуск в реестре не меняется");
            return;
        }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            k.SetValue(ValueName, Command, RegistryValueKind.String);
            Log.Info("autostart", $"Автозапуск включён: {Command}");
        }
        else if (k.GetValue(ValueName) is not null)
        {
            k.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("autostart", "Автозапуск выключен");
        }
    }
}
