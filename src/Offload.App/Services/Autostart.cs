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

    public static void Set(bool enabled)
    {
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
