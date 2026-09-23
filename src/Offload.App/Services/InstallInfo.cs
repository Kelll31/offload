using System.Diagnostics;
using Microsoft.Win32;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.App.Services;

/// <summary>Установленная через установщик копия Offload (запись в «Программы и компоненты»).</summary>
internal sealed record InstalledCopy(string Directory, string ExePath, string? Version, bool PerMachine, string? UninstallString)
{
    /// <summary>Эта копия запущена из папки установки.</summary>
    public bool IsCurrentProcess => InstallInfo.SamePath(ExePath, AppPaths.ExecutablePath);
}

/// <summary>Поиск установленной копии по ключу удаления Inno Setup (AppInfo.InstallerAppId).</summary>
internal static class InstallInfo
{
    private static readonly Lazy<InstalledCopy?> Cached = new(Find);

    /// <summary>Установленная копия (при старте процесса; null — программа не установлена).</summary>
    public static InstalledCopy? Installed => Cached.Value;

    /// <summary>
    /// Установлена другая копия, а эта запущена не из папки установки (портативная, из загрузок, dev-сборка).
    /// Такая копия не должна сама перенастраивать подключения IDE и автозапуск на себя.
    /// </summary>
    public static InstalledCopy? Foreign => Installed is { IsCurrentProcess: false } c ? c : null;

    public static InstalledCopy? Find()
    {
        // HKCU — установка «для себя» (текущий установщик); HKLM — «для всех пользователей» (старые установщики).
        return Read(RegistryHive.CurrentUser, RegistryView.Default, perMachine: false)
               ?? Read(RegistryHive.LocalMachine, RegistryView.Registry64, perMachine: true)
               ?? Read(RegistryHive.LocalMachine, RegistryView.Registry32, perMachine: true);
    }

    private static InstalledCopy? Read(RegistryHive hive, RegistryView view, bool perMachine)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(AppInfo.UninstallRegistryKey);
            if (key is null) return null;
            var dir = (key.GetValue("Inno Setup: App Path") as string) ?? (key.GetValue("InstallLocation") as string);
            if (string.IsNullOrWhiteSpace(dir)) return null;
            dir = dir.Trim().TrimEnd('\\', '/');
            var exe = Path.Combine(dir, "Offload.exe");
            return new InstalledCopy(dir, exe, key.GetValue("DisplayVersion") as string, perMachine, key.GetValue("UninstallString") as string);
        }
        catch (Exception ex)
        {
            Log.Debug("install", $"Чтение ключа установки ({hive}/{view}): {ex.Message}");
            return null;
        }
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Сравнение версий вида 1.2.3 (суффиксы вроде -beta не учитываются). &lt;0 — a старше b.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        static int[] Parse(string? v) =>
            (v ?? "0").Split('+', '-')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat([0, 0, 0, 0]).Take(4).ToArray();
        var x = Parse(a);
        var y = Parse(b);
        for (var i = 0; i < 4; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    /// <summary>Записать новую версию в «Программы и компоненты» после замены файла (только установка «для себя»).</summary>
    public static void SetDisplayVersion(string version)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallRegistryKey, writable: true);
        key?.SetValue("DisplayVersion", version, RegistryValueKind.String);
    }

    /// <summary>
    /// Запустить деинсталлятор и дождаться, пока запись об установке исчезнет. true — программа удалена.
    /// Деинсталлятор Inno Setup сам запросит права администратора, если установка была «для всех».
    /// </summary>
    public static bool Uninstall(InstalledCopy copy, TimeSpan timeout)
    {
        var (file, args) = SplitCommand(copy.UninstallString);
        if (file is null || !File.Exists(file)) return false;
        using (var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }))
            p?.WaitForExit();
        // Первая фаза деинсталлятора может завершиться раньше второй — ждём исчезновения ключа.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Find() is null || !File.Exists(copy.ExePath)) return true;
            Thread.Sleep(500);
        }
        return Find() is null;
    }

    private static (string? File, string Args) SplitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return (null, "");
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end < 0 ? (command.Trim('"'), "") : (command[1..end], command[(end + 1)..].Trim());
        }
        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }
}
