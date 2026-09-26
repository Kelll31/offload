using System.Diagnostics;
using System.Security.Cryptography;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Update;

namespace Offload.App.Services;

/// <summary>Как обновляется эта копия Offload.</summary>
internal enum AppUpdateMode
{
    /// <summary>Не обновляется сама (режим разработчика, dev-сборка с Offload.dll).</summary>
    None,

    /// <summary>Установленная «для себя» копия: тихий запуск Offload-Setup-&lt;версия&gt;.exe.</summary>
    Installer,

    /// <summary>Портативный exe: замена файла через Offload.exe.old и перезапуск.</summary>
    Portable,

    /// <summary>Установка «для всех пользователей» (старые установщики): только вручную со страницы релиза.</summary>
    Manual,
}

/// <summary>Найденное обновление Offload.</summary>
internal sealed record AppUpdateInfo(AppRelease Release, AppUpdateMode Mode)
{
    public string Version => Release.Version;

    /// <summary>Файл релиза для этого способа обновления.</summary>
    public string AssetName => Mode == AppUpdateMode.Installer ? AppReleases.SetupAssetName(Release.Version) : AppReleases.PortableAsset;

    /// <summary>Страница релиза: только адрес репозитория Offload (html_url из ответа или кэша не открывается как есть).</summary>
    public string PageUrl => Release.HtmlUrl is { } url && url.StartsWith(AppInfo.RepositoryUrl + "/", StringComparison.OrdinalIgnoreCase)
        ? url
        : $"{AppInfo.RepositoryUrl}/releases/tag/{Uri.EscapeDataString(Release.Tag)}";

    public bool CanInstall => Mode is AppUpdateMode.Installer or AppUpdateMode.Portable;
}

/// <summary>
/// Самообновление Offload из GitHub Releases: проверка → загрузка (SHA-256 из SHA256SUMS.txt релиза) → подпись
/// Authenticode (если у текущего exe есть подпись) → после выхода из трея повторная проверка суммы и подписи под
/// блокировкой файла непосредственно перед заменой exe или запуском установщика.
/// Установщик и перезапуск стартуют из Program.Main, когда трей закрыт и мьютексы освобождены (<see cref="LaunchPending"/>):
/// иначе установщик (AppMutex) или новая копия (мьютекс единственного экземпляра) увидели бы старый процесс.
/// </summary>
internal static class AppUpdater
{
    /// <summary>Ключи тихой установки Inno Setup.</summary>
    internal const string SilentSetupArgs = "/SILENT /SUPPRESSMSGBOXES /NORESTART";

    private static PendingLaunch? _pending;

    /// <summary>Последний файл обновления, прошедший проверку в <see cref="DownloadAsync"/>.</summary>
    private static VerifiedUpdate? _verified;

    /// <summary>
    /// Проверенный файл обновления: полный путь, SHA-256 на момент проверки и издатель, чья подпись обязательна
    /// (null — текущий exe не подписан, подпись не требуется).
    /// </summary>
    internal sealed record VerifiedUpdate(string Path, string Sha256, string? Publisher);

    /// <summary>
    /// Что сделать после выхода: запустить установщик (Installer) или заменить exe (File → TargetExe), затем запустить TargetExe.
    /// </summary>
    internal sealed record PendingLaunch(VerifiedUpdate File, bool Installer, string TargetExe);

    /// <summary>После выхода из трея нужно запустить установщик или новую версию.</summary>
    public static bool HasPendingLaunch => _pending is not null;

    // ---------- Решение ----------

    /// <summary>Способ обновления по свойствам этой копии.</summary>
    internal static AppUpdateMode DecideMode(bool devMode, bool installedHere, bool perMachine, bool singleFile)
    {
        if (devMode) return AppUpdateMode.None;
        if (installedHere) return perMachine ? AppUpdateMode.Manual : AppUpdateMode.Installer;
        return singleFile ? AppUpdateMode.Portable : AppUpdateMode.None;
    }

    /// <summary>
    /// Есть ли обновление: релиз новее текущей версии и способ обновления известен. Если в релизе нет нужного файла
    /// (установщика или exe) — обновление только вручную со страницы релиза.
    /// </summary>
    internal static AppUpdateInfo? Evaluate(AppRelease? latest, string currentVersion, AppUpdateMode mode)
    {
        if (latest is null || mode == AppUpdateMode.None) return null;
        // Версия попадает в имя файла и командную строку установщика — только строгий вид (кэш мог быть подменён).
        if (!AppReleases.IsValidVersion(latest.Version)) return null;
        if (AppReleases.CompareVersions(latest.Version, currentVersion) <= 0) return null;
        var info = new AppUpdateInfo(latest, mode);
        if (info.CanInstall && latest.Asset(info.AssetName) is null) return info with { Mode = AppUpdateMode.Manual };
        return info;
    }

    /// <summary>Способ обновления этой копии.</summary>
    public static AppUpdateMode CurrentMode
    {
        get
        {
            var installed = InstallInfo.Installed;
            return DecideMode(DevMode.Active, installed?.IsCurrentProcess == true, installed?.PerMachine == true, InstallGuard.IsSingleFile());
        }
    }

    /// <summary>Проверить обновление. force=false — не чаще раза в 12 ч (между проверками — ответ из кэша).</summary>
    public static async Task<AppUpdateInfo?> CheckAsync(bool force, CancellationToken ct)
    {
        var mode = CurrentMode;
        if (mode == AppUpdateMode.None)
        {
            Log.Debug("update", DevMode.Active ? "Режим разработчика — самообновление выключено" : "Сборка не однофайловая — самообновление выключено");
            return null;
        }
        var latest = await AppReleases.GetLatestAsync(force, ct).ConfigureAwait(false);
        var info = Evaluate(latest, AppInfo.Version, mode);
        if (info is not null) Log.Info("update", $"Доступна новая версия Offload: {info.Version} (установлена {AppInfo.Version}, способ: {info.Mode})");
        return info;
    }

    // ---------- Загрузка и проверка ----------

    /// <summary>Папка загрузки установщика обновления.</summary>
    internal static string InstallerDownloadDir => Path.Combine(AppPaths.DownloadsDir, "offload-update");

    /// <summary>Куда скачивается файл обновления.</summary>
    internal static string DownloadPath(AppUpdateInfo info, string currentExe) => info.Mode == AppUpdateMode.Portable
        ? currentExe + ".new"
        : Path.Combine(InstallerDownloadDir, info.AssetName);

    /// <summary>
    /// Путь загрузки не выходит за свою папку: версия строгого вида, имя файла — ровно ожидаемое (установщик —
    /// <see cref="AppUpdateInfo.AssetName"/>, портативный exe — «Offload.exe.new» рядом с программой).
    /// </summary>
    internal static void EnsureDownloadPath(AppUpdateInfo info, string dest, string currentExe)
    {
        var portable = info.Mode == AppUpdateMode.Portable;
        var full = Path.GetFullPath(dest);
        var expectedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(portable ? Path.GetDirectoryName(Path.GetFullPath(currentExe))! : InstallerDownloadDir));
        var expectedName = portable ? Path.GetFileName(currentExe) + ".new" : info.AssetName;
        var ok = AppReleases.IsValidVersion(info.Version)
                 && string.Equals(Path.GetFileName(info.AssetName), info.AssetName, StringComparison.Ordinal)
                 && string.Equals(Path.GetFileName(full), expectedName, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(Path.GetDirectoryName(full), expectedDir, StringComparison.OrdinalIgnoreCase);
        if (!ok) throw new DownloadException(L.F("Недопустимое имя файла обновления ({0}) — оно не будет загружено.", info.AssetName));
    }

    /// <summary>
    /// Скачать файл обновления с проверкой SHA-256 и подписи. Возвращает путь к проверенному файлу; его сумма и
    /// обязательный издатель запоминаются и проверяются ещё раз непосредственно перед заменой exe или запуском установщика.
    /// </summary>
    public static async Task<string> DownloadAsync(AppUpdateInfo info, string currentExe, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!info.CanInstall) throw new InvalidOperationException(L.T("Эту копию Offload нужно обновить вручную со страницы релиза."));
        var asset = info.Release.Asset(info.AssetName)
                    ?? throw new DownloadException(L.F("В релизе {0} нет файла {1}.", info.Release.Tag, info.AssetName));
        var dest = DownloadPath(info, currentExe);
        EnsureDownloadPath(info, dest, currentExe);
        var sha = await AppReleases.GetSha256Async(info.Release, asset.Name, ct).ConfigureAwait(false);
        Log.Info("update", $"Загрузка обновления Offload {info.Version}: {asset.Name} → {dest}");
        _verified = null;
        await HttpDownloader.DownloadFileAsync(asset.Url, dest, asset.Size > 0 ? asset.Size : null, sha, progress, ct).ConfigureAwait(false);
        try
        {
            var publisher = RequiredPublisher(currentExe, Authenticode.IsSignedAndValid, Authenticode.Publisher);
            // sha == null — только при осознанном обходе OFFLOAD_ALLOW_UNVERIFIED: закрепляется сумма скачанного файла.
            var verified = new VerifiedUpdate(Path.GetFullPath(dest), sha ?? ComputeSha256(dest), publisher);
            using (OpenVerified(verified, Authenticode.IsSignedAndValid, Authenticode.Publisher)) { }
            _verified = verified;
        }
        catch
        {
            TryDelete(dest);
            throw;
        }
        return dest;
    }

    /// <summary>
    /// Чья подпись обязательна для обновления: издатель текущего exe, если у него есть подпись Authenticode — даже
    /// если сейчас она не проходит проверку (истёк сертификат без метки времени, сбой WinVerifyTrust). null — текущий exe
    /// не подписан, обновление принимается по SHA-256 из SHA256SUMS.txt.
    /// </summary>
    internal static string? RequiredPublisher(string currentExe, Func<string, bool> isValid, Func<string, string?> publisher)
    {
        var expected = publisher(currentExe);
        var currentValid = isValid(currentExe);
        if (expected is null)
        {
            // Действительная подпись без читаемого издателя (например, подпись каталогом): сравнивать не с чем.
            if (currentValid)
                throw new DownloadException(L.T("Не удалось определить издателя подписи текущей копии Offload — обновление не будет установлено."));
            Log.Info("update", "Текущий Offload.exe не подписан — подпись обновления не проверяется (проверена SHA-256)");
            return null;
        }
        if (!currentValid)
            Log.Warn("update", $"Подпись текущего Offload.exe не проходит проверку — обновление всё равно обязано быть подписано: {expected}");
        return expected;
    }

    /// <summary>Файл обновления подписан действительной подписью ожидаемого издателя.</summary>
    internal static void VerifyCandidateSignature(string candidate, string expectedPublisher, Func<string, bool> isValid, Func<string, string?> publisher)
    {
        if (!isValid(candidate))
            throw new DownloadException(L.T("У загруженного обновления нет действительной цифровой подписи — оно не будет установлено."));
        var actual = publisher(candidate);
        if (!string.Equals(expectedPublisher, actual, StringComparison.Ordinal))
            throw new DownloadException(L.F("Обновление подписано другим издателем ({0}) — оно не будет установлено.", actual ?? "?"));
        Log.Info("update", $"Подпись обновления проверена: {actual}");
    }

    /// <summary>
    /// Подпись проверяется, если у текущего exe есть подпись (<see cref="RequiredPublisher"/>): тогда файл обновления
    /// обязан иметь действительную подпись того же издателя. Неподписанная сборка принимает обновление по SHA-256.
    /// Возвращает обязательного издателя (null — подпись не требуется).
    /// </summary>
    internal static string? VerifySignature(string candidate, string currentExe, Func<string, bool> isValid, Func<string, string?> publisher)
    {
        var expected = RequiredPublisher(currentExe, isValid, publisher);
        if (expected is not null) VerifyCandidateSignature(candidate, expected, isValid, publisher);
        return expected;
    }

    /// <summary>
    /// Открыть проверенный файл обновления и проверить его заново: SHA-256 (по открытому дескриптору) и подпись, если
    /// она обязательна. Дескриптор открыт с FileShare.Read — пока он не закрыт, файл нельзя изменить, удалить или
    /// переименовать, поэтому проверенное содержимое и есть то, что будет запущено. inheritable — дескриптор наследуется
    /// дочерним процессом (cmd.exe держит блокировку до запуска установщика).
    /// </summary>
    internal static FileStream OpenVerified(VerifiedUpdate update, Func<string, bool> isValid, Func<string, string?> publisher, bool inheritable = false)
    {
        var share = inheritable ? FileShare.Read | FileShare.Inheritable : FileShare.Read;
        var fs = new FileStream(update.Path, FileMode.Open, FileAccess.Read, share);
        try
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(fs));
            if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DownloadException(L.T("Файл обновления изменился после проверки — оно не будет установлено."));
            if (update.Publisher is not null) VerifyCandidateSignature(update.Path, update.Publisher, isValid, publisher);
            fs.Position = 0;
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    // ---------- Установка ----------

    /// <summary>Установщик скачан: запустить его после выхода из трея, затем снова открыть Offload.</summary>
    public static void ScheduleInstaller(string setupPath, string installedExe) => Schedule(setupPath, installer: true, installedExe);

    /// <summary>Новый exe портативной копии скачан: заменить файл программы после выхода из трея и запустить новую версию.</summary>
    public static void SchedulePortableSwap(string newExe, string currentExe) => Schedule(newExe, installer: false, currentExe);

    private static void Schedule(string file, bool installer, string targetExe)
    {
        // Запускается только файл, прошедший проверку в DownloadAsync: его сумма и издатель закреплены там.
        if (_verified is not { } v || !string.Equals(v.Path, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
        {
            _pending = null;
            Log.Error("update", $"Файл обновления {file} не проходил проверку — обновление не запланировано");
            return;
        }
        _pending = new PendingLaunch(v, installer, targetExe);
        Log.Info("update", installer ? $"После выхода будет запущен установщик {file}" : $"После выхода файл программы будет заменён: {targetExe}");
    }

    /// <summary>
    /// Замена exe портативной копии: работающий (или только что закрытый) файл переименовывается в Offload.exe.old
    /// (удаляется при следующем запуске), на его место встаёт новый. При ошибке прежний файл возвращается.
    /// </summary>
    internal static void SwapExe(string newExe, string currentExe)
    {
        var old = currentExe + ".old";
        TryDelete(old);
        File.Move(currentExe, old, overwrite: true);
        try
        {
            File.Move(newExe, currentExe, overwrite: false);
        }
        catch
        {
            File.Move(old, currentExe, overwrite: true);
            throw;
        }
        Log.Info("update", $"Файл программы заменён новой версией: {currentExe}");
    }

    /// <summary>Отменить запланированный запуск (например, выход из трея не состоялся).</summary>
    public static void CancelPending() => _pending = null;

    /// <summary>Вызывается из Program.Main после закрытия трея и освобождения мьютексов.</summary>
    public static void LaunchPending()
    {
        var p = _pending;
        _pending = null;
        if (p is null) return;
        RunPending(p, Authenticode.IsSignedAndValid, Authenticode.Publisher, Environment.SystemDirectory,
            psi => Process.Start(psi)?.Dispose(),
            (message, ex) => Util.Ui.ShowError(null, message, ex));
    }

    /// <summary>
    /// Выполнить запланированное обновление. Перед заменой exe и перед запуском установщика файл проверяется заново
    /// (<see cref="OpenVerified"/>), и дескриптор с запретом записи держится, пока процесс не запущен. Не прошедшее
    /// повторную проверку обновление не ставится — запускается прежняя версия.
    /// </summary>
    internal static void RunPending(PendingLaunch p, Func<string, bool> isValid, Func<string, string?> publisher, string systemDir,
        Action<ProcessStartInfo> start, Action<string, Exception> report)
    {
        try
        {
            if (p.Installer) RunInstaller(p, isValid, publisher, systemDir, start, report);
            else RunPortable(p, isValid, publisher, start, report);
        }
        catch (Exception ex)
        {
            Log.Error("update", "Не удалось запустить обновление", ex);
        }
    }

    private static void RunPortable(PendingLaunch p, Func<string, bool> isValid, Func<string, string?> publisher,
        Action<ProcessStartInfo> start, Action<string, Exception> report)
    {
        var target = p.TargetExe;
        try
        {
            // Проверка до замены: изменённый файл не должен вытеснить рабочий exe.
            using (OpenVerified(p.File, isValid, publisher)) { }
            SwapExe(p.File.Path, target);
            FileStream locked;
            try
            {
                // И после замены — уже на месте программы: запускается ровно проверенное содержимое.
                locked = OpenVerified(p.File with { Path = Path.GetFullPath(target) }, isValid, publisher);
            }
            catch (Exception verifyError)
            {
                try
                {
                    File.Move(target + ".old", target, overwrite: true);
                }
                catch (Exception restoreError)
                {
                    // Прежний exe не вернулся, а на месте программы — непроверенный файл: ничего не запускать.
                    Log.Error("update", "Не удалось вернуть прежний файл программы", restoreError);
                    report(L.T("Не удалось установить обновление Offload, прежний файл программы не восстановлен — скачайте Offload со страницы релиза"), verifyError);
                    return;
                }
                throw;
            }
            using (locked)
                start(new ProcessStartInfo(target) { UseShellExecute = true });
            return;
        }
        catch (Exception ex)
        {
            // Прежняя версия остаётся на месте и запускается снова.
            Log.Error("update", "Не удалось заменить файл программы", ex);
            report(L.T("Не удалось установить обновление Offload — запускается прежняя версия"), ex);
        }
        start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static void RunInstaller(PendingLaunch p, Func<string, bool> isValid, Func<string, string?> publisher, string systemDir,
        Action<ProcessStartInfo> start, Action<string, Exception> report)
    {
        var cmd = BuildSetupCommand(p.File.Path, p.TargetExe, systemDir);
        FileStream locked;
        try
        {
            // Через cmd дескриптор наследуется: cmd.exe держит блокировку файла до конца установки (в т. ч. во время паузы).
            locked = OpenVerified(p.File, isValid, publisher, inheritable: cmd is not null);
        }
        catch (Exception ex)
        {
            Log.Error("update", "Установщик обновления не прошёл повторную проверку", ex);
            report(L.T("Не удалось установить обновление Offload — запускается прежняя версия"), ex);
            start(new ProcessStartInfo(p.TargetExe) { UseShellExecute = true });
            return;
        }
        using (locked)
        {
            if (cmd is { } c)
            {
                // Через cmd: дождаться конца установки (start /wait) и снова открыть Offload — установщик в тихом режиме
                // программу не запускает. Пауза (ping) — чтобы этот процесс успел завершиться.
                start(new ProcessStartInfo(c.File, c.Args) { UseShellExecute = false, CreateNoWindow = true });
            }
            else
            {
                start(new ProcessStartInfo(p.File.Path, SilentSetupArgs) { UseShellExecute = true });
            }
        }
        Log.Info("update", $"Запущен установщик обновления: {p.File.Path}");
    }

    /// <summary>
    /// Команда cmd.exe «пауза → тихая установка с ожиданием → запуск программы». null — путь нельзя безопасно
    /// передать через cmd (кавычки, %), тогда установщик запускается напрямую, без перезапуска программы.
    /// </summary>
    internal static (string File, string Args)? BuildSetupCommand(string setup, string exe, string systemDir)
    {
        static bool Safe(string s) => s.IndexOfAny(['"', '%', '!', '\r', '\n']) < 0;
        if (!Safe(setup) || !Safe(exe) || !Safe(systemDir)) return null;
        var ping = Path.Combine(systemDir, "PING.EXE");
        var args = $"/d /v:off /s /c \"\"{ping}\" -n 3 127.0.0.1 >nul & start \"\" /wait \"{setup}\" {SilentSetupArgs} & start \"\" \"{exe}\"\"";
        return (Path.Combine(systemDir, "cmd.exe"), args);
    }

    /// <summary>
    /// При запуске: удалить установщик прошлого обновления, недокачанный или не установленный новый exe (.new, .new.part)
    /// и прежний exe (.old). В режиме разработчика ничего не трогается.
    /// </summary>
    public static void CleanupLeftovers(string currentExe)
    {
        if (DevMode.Active) return;
        TryDelete(currentExe + ".new");
        TryDelete(currentExe + ".new.part");
        TryDelete(currentExe + ".old");
        var dir = InstallerDownloadDir;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Debug("update", $"Не удалена папка {dir}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug("update", $"Не удалён {path}: {ex.Message}");
        }
    }
}
