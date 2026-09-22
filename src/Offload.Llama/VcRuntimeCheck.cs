using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.Llama;

internal enum VcRuntimeStatus
{
    /// <summary>Нет записи в реестре или DLL в System32.</summary>
    Missing,
    /// <summary>Установлена версия старше 14.40 — сборки на STL VS 2022 17.10+ падают в std::mutex.</summary>
    Outdated,
    Ok,
}

/// <summary>Проверка и установка Visual C++ 2015–2022 Redistributable.</summary>
internal static class VcRuntimeCheck
{
    /// <summary>Минимальная версия: STL из VS 2022 17.10+ несовместим со старыми msvcp140.dll (падение в std::mutex).</summary>
    internal static readonly Version MinVersion = new(14, 40);

    internal static readonly string[] RequiredDlls = ["msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"];

    internal const string DownloadUrlArm64 = "https://aka.ms/vs/17/release/vc_redist.arm64.exe";

    private static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    private static string Arch => IsArm64 ? "arm64" : "x64";

    public static VcRuntimeStatus GetStatus()
    {
        var (registered, regVersion) = ReadRegistry(Arch);
        var sys = SystemDirectory();
        var dllsPresent = RequiredDlls.All(d => File.Exists(Path.Combine(sys, d)));
        if (!registered || !dllsPresent) return VcRuntimeStatus.Missing;
        var version = FileVersion(Path.Combine(sys, "msvcp140.dll")) ?? regVersion;
        return version is not null && version < MinVersion ? VcRuntimeStatus.Outdated : VcRuntimeStatus.Ok;
    }

    /// <summary>DLL лежат рядом с exe (app-local) — системный runtime не нужен.</summary>
    public static bool HasAppLocal(string? dir) =>
        !string.IsNullOrEmpty(dir) && RequiredDlls.All(d => File.Exists(Path.Combine(dir, d)));

    /// <summary>
    /// Перед запуском llama-server: если runtime отсутствует, Windows показала бы системное окно
    /// «не найден MSVCP140.dll» — поэтому проверяем заранее и бросаем понятное исключение.
    /// </summary>
    public static void EnsureAvailable(string exeDir)
    {
        if (HasAppLocal(exeDir)) return;
        if (GetStatus() == VcRuntimeStatus.Missing)
            throw new LlamaVcRuntimeMissingException(NtStatus.VcMissingMessage);
    }

    private static (bool Registered, Version? Version) ReadRegistry(string arch)
    {
        var registered = false;
        Version? best = null;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch);
                if (k is null) continue;
                if (ToInt(k.GetValue("Installed")) != 1) continue;
                registered = true;
                var major = ToInt(k.GetValue("Major"));
                var minor = ToInt(k.GetValue("Minor"));
                var bld = ToInt(k.GetValue("Bld"));
                if (major > 0)
                {
                    var v = new Version(major, Math.Max(0, minor), Math.Max(0, bld));
                    if (best is null || v > best) best = v;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("llama", $"Реестр VC++ ({view}): {ex.Message}");
            }
        }
        return (registered, best);
    }

    private static int ToInt(object? v) => v switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var p) => p,
        _ => 0,
    };

    private static Version? FileVersion(string path)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            return fvi.FileMajorPart > 0 ? new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SystemDirectory()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
        {
            // 32-битный процесс видит SysWOW64 вместо System32.
            var sysnative = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
            if (Directory.Exists(sysnative)) sys = sysnative;
        }
        return sys;
    }

    public static async Task<bool> InstallAsync(IProgress<StepProgress>? progress, CancellationToken ct)
    {
        const string title = "Microsoft Visual C++ Redistributable";
        var url = IsArm64 ? DownloadUrlArm64 : VcRuntime.DownloadUrl;
        var dest = Path.Combine(AppPaths.DownloadsDir, IsArm64 ? "vc_redist.arm64.exe" : "vc_redist.x64.exe");
        Directory.CreateDirectory(AppPaths.DownloadsDir);

        // Размер и хеш заранее неизвестны (aka.ms перенаправляет на актуальную версию) — всегда качаем заново.
        TryDelete(dest);
        TryDelete(dest + ".part");

        Report(progress, $"Загрузка {title}…", 0);
        await HttpDownloader.DownloadFileAsync(url, dest,
            progress: new ActionProgress<DownloadProgress>(p =>
                Report(progress, $"Загрузка {title}…", p.Fraction is double f ? f * 0.5 : null, InstallerImpl.DownloadDetail(p))),
            ct: ct).ConfigureAwait(false);

        Report(progress, $"Установка {title}… Подтвердите запрос контроля учётных записей Windows.", 0.6);
        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo(dest, "/install /passive /norestart")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppPaths.DownloadsDir,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("llama", "Установка VC++ Redistributable отменена пользователем (UAC)");
            Report(progress, "Установка Visual C++ отменена: запрос контроля учётных записей отклонён.", null);
            return false;
        }
        if (proc is null) return false;

        int code;
        using (proc)
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            code = proc.ExitCode;
        }

        // 0 — установлено, 1638 — уже установлена более новая версия, 3010 — нужна перезагрузка.
        if (code is 0 or 1638 or 3010)
        {
            Log.Info("llama", $"VC++ Redistributable установлен (код {code}){(code == 3010 ? ", требуется перезагрузка" : "")}");
            TryDelete(dest);
            Report(progress, code == 3010
                ? $"{title} установлен. Для завершения может потребоваться перезагрузка."
                : $"{title} установлен.", 1);
            return true;
        }

        var why = code switch
        {
            1602 => "установка отменена",
            1603 => "ошибка установки",
            1618 => "уже выполняется другая установка — дождитесь её окончания",
            _ => $"код {code}",
        };
        Log.Warn("llama", $"Установщик VC++ Redistributable завершился с кодом {code}");
        Report(progress, $"Не удалось установить {title}: {why}.", null);
        return false;
    }

    private static void Report(IProgress<StepProgress>? progress, string stage, double? fraction, string? detail = null)
    {
        try { progress?.Report(new StepProgress(stage, fraction, detail)); }
        catch (Exception ex) { Log.Debug("llama", $"Обработчик прогресса: {ex.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>IProgress без захвата SynchronizationContext (в отличие от Progress&lt;T&gt;).</summary>
internal sealed class ActionProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
