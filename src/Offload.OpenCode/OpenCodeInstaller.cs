using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Processes;
using Offload.Core.Util;

namespace Offload.OpenCode;

/// <summary>
/// Установка OpenCode (standalone-бинарник с GitHub Releases, без Node.js)
/// в %LOCALAPPDATA%\Offload\opencode\bin\opencode.exe.
/// </summary>
public static class OpenCodeInstaller
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    /// <summary>Проверка «opencode --version»: первый запуск свежего exe может задержать антивирус.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(90);

    // ripgrep, который OpenCode иначе скачает сам при первом поиске (релиз неизменяемый — хэши зафиксированы).
    internal const string RipgrepVersion = "15.1.0";

    internal static string BinDir => Path.Combine(AppPaths.OpenCodeDir, "bin");
    internal static string ManagedExePath => Path.Combine(BinDir, "opencode.exe");

    /// <summary>Куда OpenCode ищет ripgrep: &lt;XDG_CACHE_HOME&gt;\opencode\bin\rg.exe.</summary>
    internal static string RipgrepPath => Path.Combine(OpenCodeConfigWriter.CacheHome, "opencode", "bin", "rg.exe");

    /// <summary>Проверенная версия OpenCode (канал «stable»).</summary>
    public static string PinnedVersion => OpenCodeReleases.PinnedVersion;

    /// <summary>Предыдущий opencode.exe, сохранённый при обновлении (для отката): &lt;bin&gt;\previous\opencode.exe.</summary>
    internal static string PreviousDir => Path.Combine(BinDir, "previous");

    internal static string PreviousExePath => Path.Combine(PreviousDir, "opencode.exe");

    private static string PreviousVersionFile => Path.Combine(PreviousDir, "version.txt");

    /// <summary>
    /// Установить OpenCode из канала настроек (<see cref="OpenCodeSettings.Channel"/>; обновить, если уже стоит).
    /// Прежний exe сохраняется для отката (<see cref="RollbackAsync"/>). Обновляет конфиг.
    /// </summary>
    public static Task<OpenCodeInstallResult> InstallAsync(IProgress<StepProgress>? progress = null, CancellationToken ct = default) =>
        InstallAsync(null, progress, ct);

    /// <param name="channel">"stable" или "latest"; null — из настроек.</param>
    /// <param name="progress">Ход установки.</param>
    /// <param name="ct">Отмена.</param>
    public static Task<OpenCodeInstallResult> InstallAsync(string? channel, IProgress<StepProgress>? progress, CancellationToken ct = default) =>
        InstallAsync(channel, allowDowngrade: false, progress, ct);

    /// <param name="channel">"stable" или "latest"; null — из настроек (не выбранный явно канал записывается, см. <see cref="OpenCodeChannels.Persist"/>).</param>
    /// <param name="allowDowngrade">
    /// Разрешить установить версию старее уже установленной (пользователь подтвердил откат). Без этого более новая
    /// установленная версия сохраняется, а установка лишь перенастраивает её.
    /// </param>
    /// <param name="progress">Ход установки.</param>
    /// <param name="ct">Отмена.</param>
    public static async Task<OpenCodeInstallResult> InstallAsync(
        string? channel, bool allowDowngrade, IProgress<StepProgress>? progress, CancellationToken ct = default)
    {
        await InstallGate.WaitAsync(ct);
        try
        {
            var effective = channel is null ? OpenCodeChannels.Persist() : OpenCodeChannels.Normalize(channel);
            return await InstallCoreAsync(effective, allowDowngrade, progress, ct);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static async Task<OpenCodeInstallResult> InstallCoreAsync(string channel, bool allowDowngrade, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (arch is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(L.T("OpenCode работает только на 64-разрядной Windows (x64 или ARM64)."));

        OpenCodeRelease release;
        if (channel == OpenCodeChannels.Stable)
        {
            release = OpenCodeReleases.Pinned();
            Log.Info("opencode", $"Канал stable: проверенная версия OpenCode {release.Version}");
        }
        else
        {
            progress?.Report(new StepProgress(L.T("Поиск последней версии OpenCode…")));
            release = await OpenCodeReleases.GetLatestAsync(ct);
        }
        var asset = OpenCodeReleases.SelectAsset(release, arch, Avx2.IsSupported)
                    ?? throw new InvalidOperationException(
                        L.F("В релизе OpenCode {0} нет сборки для Windows {1}.", release.Tag, arch == Architecture.Arm64 ? "ARM64" : "x64"));

        var target = ManagedExePath;
        var current = File.Exists(target) ? await TryGetVersionAsync(target, ct) : null;
        string version;
        if (current is not null && release.Version is not null && SameVersion(current, release.Version))
        {
            version = current;
            Log.Info("opencode", $"OpenCode {version} уже установлен");
            progress?.Report(new StepProgress(L.F("OpenCode {0} уже установлен — обновление не требуется", version), 1));
        }
        else if (!allowDowngrade && current is not null && OpenCodeChannels.IsDowngrade(current, release.Version))
        {
            // Молча не откатываем: установленная версия новее выбранной (например, канал stable, а стоит более новая).
            version = current;
            Log.Warn("opencode", $"OpenCode {current} новее версии канала {channel} ({release.Version}) — оставлен без изменений");
            progress?.Report(new StepProgress(L.F("Установлена более новая версия OpenCode {0}, чем {1} — она оставлена без изменений.", current, release.Version ?? "?"), 1));
        }
        else
        {
            version = await DownloadAndInstallAsync(release, asset, target, current, progress, ct);
        }

        await PreseedRipgrepAsync(arch, progress, ct);
        return Configure(target, version, progress);
    }

    /// <summary>Записать путь и версию в настройки и обновить управляемый конфиг OpenCode.</summary>
    private static OpenCodeInstallResult Configure(string target, string version, IProgress<StepProgress>? progress)
    {
        progress?.Report(new StepProgress(L.T("Настройка OpenCode…")));
        ConfigStore.Update(c =>
        {
            c.OpenCode.ExecutablePath = target;
            c.OpenCode.InstalledVersion = version;
        });
        var cfg = ConfigStore.Current;
        if (cfg.ActiveModel() is not null)
        {
            try
            {
                OpenCodeConfigWriter.WriteManagedConfig(cfg);
            }
            catch (Exception ex)
            {
                Log.Warn("opencode", $"Конфигурация OpenCode не записана: {ex.Message}");
            }
        }

        Log.Info("opencode", $"OpenCode {version} установлен: {target}");
        progress?.Report(new StepProgress(L.F("OpenCode {0} установлен", version), 1));
        return new OpenCodeInstallResult(version, target);
    }

    private static async Task<string> DownloadAndInstallAsync(
        OpenCodeRelease release, OpenCodeAsset asset, string target, string? currentVersion, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        var label = release.Version is null ? "OpenCode" : $"OpenCode {release.Version}";
        var zip = Path.Combine(AppPaths.DownloadsDir, $"opencode-{release.Version ?? "latest"}-{asset.Name}");
        if (release.Version is null)
        {
            // Версия неизвестна — докачивать старый обрывок нельзя (мог остаться от другой версии).
            TryDelete(zip);
            TryDelete(zip + ".part");
        }
        DownloadPolicy.RequireChecksum(asset.Sha256, asset.Name, "OpenCode");

        await HttpDownloader.DownloadFileAsync(
            asset.Url, zip,
            asset.Size > 0 ? asset.Size : null,
            asset.Sha256,
            new InlineProgress<DownloadProgress>(p => progress?.Report(ToStep(label, p))),
            ct);

        progress?.Report(new StepProgress(L.F("Распаковка {0}…", label)));
        Directory.CreateDirectory(BinDir);
        var staging = Path.Combine(BinDir, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(staging);
            var staged = Path.Combine(staging, "opencode.exe");
            await Task.Run(() => ExtractEntry(zip, "opencode.exe", staged, ct), ct);

            progress?.Report(new StepProgress(L.F("Проверка запуска {0}…", label)));
            var version = await TryGetVersionAsync(staged, ct)
                          ?? throw new InvalidOperationException(
                              L.T("Загруженный OpenCode не запускается (opencode --version завершился ошибкой). Возможно, файл заблокировал антивирус — проверьте карантин и повторите установку."));

            if (currentVersion is not null && !SameVersion(currentVersion, version)) KeepPrevious(target, currentVersion);
            ReplaceExecutable(staged, target);
            TryDelete(zip);
            return version;
        }
        finally
        {
            FileUtil.TryDeleteDirectory(staging);
        }
    }

    /// <summary>Сведения о сохранённой предыдущей версии (null — отката нет).</summary>
    public static OpenCodePrevious? GetPrevious()
    {
        try
        {
            if (!File.Exists(PreviousExePath)) return null;
            var version = File.Exists(PreviousVersionFile) ? File.ReadAllText(PreviousVersionFile).Trim() : "";
            return new OpenCodePrevious(version.Length == 0 ? "?" : version, PreviousExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Сохранить текущий exe как предыдущую версию (копией: запущенный exe копировать можно). Одна предыдущая версия;
    /// ошибка сохранения не мешает обновлению.
    /// </summary>
    internal static void KeepPrevious(string target, string version)
    {
        if (!File.Exists(target)) return;
        try
        {
            Directory.CreateDirectory(PreviousDir);
            var tmp = PreviousExePath + ".tmp";
            File.Copy(target, tmp, overwrite: true);
            File.Move(tmp, PreviousExePath, overwrite: true);
            FileUtil.WriteAllTextAtomic(PreviousVersionFile, version);
            Log.Info("opencode", $"Предыдущая версия OpenCode {version} сохранена для отката: {PreviousExePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("opencode", $"Не удалось сохранить предыдущую версию OpenCode: {ex.Message}");
        }
    }

    /// <summary>
    /// Откат к сохранённой предыдущей версии: она проверяется запуском («--version»), текущая становится «предыдущей»
    /// (можно вернуться обратно тем же действием).
    /// </summary>
    public static async Task<OpenCodeInstallResult> RollbackAsync(IProgress<StepProgress>? progress = null, CancellationToken ct = default)
    {
        await InstallGate.WaitAsync(ct);
        try
        {
            var previous = GetPrevious() ?? throw new InvalidOperationException(L.T("Предыдущая версия OpenCode не сохранена — откатывать не к чему."));
            progress?.Report(new StepProgress(L.F("Проверка запуска OpenCode {0}…", previous.Version)));
            var target = ManagedExePath;
            Directory.CreateDirectory(BinDir);
            var staging = Path.Combine(BinDir, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(staging);
                var staged = Path.Combine(staging, "opencode.exe");
                File.Copy(previous.ExecutablePath, staged);
                var version = await TryGetVersionAsync(staged, ct)
                              ?? throw new InvalidOperationException(L.T("Сохранённая предыдущая версия OpenCode не запускается — откат невозможен."));
                var current = File.Exists(target) ? await TryGetVersionAsync(target, ct) : null;
                if (current is not null) KeepPrevious(target, current);
                else TryDelete(PreviousExePath);
                ReplaceExecutable(staged, target);
                Log.Info("opencode", $"OpenCode откачен к версии {version} (была {current ?? "?"})");
                return Configure(target, version, progress);
            }
            finally
            {
                FileUtil.TryDeleteDirectory(staging);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    internal static StepProgress ToStep(string label, DownloadProgress p)
    {
        switch (p.Stage)
        {
            case DownloadStage.Verifying:
                return new StepProgress(L.F("Проверка контрольной суммы {0}…", p.FileName), p.Fraction);
            case DownloadStage.Completed:
                return new StepProgress(L.F("Загрузка {0} завершена", label), 1);
            default:
            {
                var detail = p.TotalBytes is > 0
                    ? L.F("{0} из {1}", FileUtil.FormatBytes(p.BytesReceived), FileUtil.FormatBytes(p.TotalBytes.Value))
                    : FileUtil.FormatBytes(p.BytesReceived);
                if (p.BytesPerSecond > 1) detail += $" · {FileUtil.FormatSpeed(p.BytesPerSecond)}";
                if (p.Eta is { } eta) detail += " · " + L.F("осталось {0}", FileUtil.FormatDuration(eta));
                return new StepProgress(L.F("Загрузка {0}…", label), p.Fraction, detail);
            }
        }
    }

    /// <summary>Извлечь из архива файл с именем fileName (корень архива предпочтительнее) во временный файл и затем в dest.</summary>
    internal static void ExtractEntry(string zipPath, string fileName, string dest, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries
                        .Where(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.FullName.Length)
                        .FirstOrDefault()
                    ?? throw new InvalidDataException(L.F("В архиве {0} нет файла {1}.", Path.GetFileName(zipPath), fileName));
        ct.ThrowIfCancellationRequested();
        var tmp = dest + ".tmp";
        entry.ExtractToFile(tmp, overwrite: true);
        File.Move(tmp, dest, overwrite: true);
    }

    /// <summary>
    /// Замена opencode.exe. Запущенный exe нельзя перезаписать, но можно переименовать —
    /// так обновление проходит даже при открытом OpenCode; иначе — понятная ошибка.
    /// </summary>
    internal static void ReplaceExecutable(string staged, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(target)!, Path.GetFileName(target) + ".old-*"))
            TryDelete(old);

        try
        {
            File.Move(staged, target, overwrite: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info("opencode", $"opencode.exe занят ({ex.Message}), заменяем через переименование");
        }

        var aside = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Move(target, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                L.T("Не удалось заменить opencode.exe: файл используется. Закройте все окна OpenCode и повторите установку."), ex);
        }
        try
        {
            File.Move(staged, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Move(aside, target); } catch { /* вернуть не удалось — останется .old */ }
            throw new InvalidOperationException(
                L.T("Не удалось заменить opencode.exe: файл используется. Закройте все окна OpenCode и повторите установку."), ex);
        }
        TryDelete(aside); // занятый файл удалится при следующей установке
    }

    /// <summary>«opencode --version» (stdin закрыт, окружение изолировано). null — не запускается.</summary>
    internal static async Task<string?> TryGetVersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(
                exe, ["--version"],
                workingDirectory: EnsureDirectory(AppPaths.OpenCodeDir),
                environment: OpenCodeConfigWriter.BuildEnvironment(null, interactive: false),
                timeout: VersionTimeout,
                ct: ct);
            if (!r.Success)
            {
                Log.Warn("opencode", $"«{exe} --version»: код {r.ExitCode}{(r.TimedOut ? " (таймаут)" : "")}. {Tail(r.StdErr)}");
                return null;
            }
            var v = ParseVersionOutput(r.StdOut);
            if (v is null) Log.Warn("opencode", $"«{exe} --version» вывел неожиданное: {Tail(r.StdOut)}");
            return v;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("opencode", $"Не удалось запустить {exe}: {ex.Message}");
            return null;
        }
    }

    internal static string? ParseVersionOutput(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var m = Regex.Match(RunEvents.StripAnsi(raw).Trim(), @"^v?(?<v>\d+\.\d+\.\d+[0-9A-Za-z.\-+]*)$", RegexOptions.CultureInvariant);
            if (m.Success) return m.Groups["v"].Value;
        }
        return null;
    }

    internal static bool SameVersion(string a, string b) =>
        string.Equals(a.Trim().TrimStart('v', 'V'), b.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Предзагрузка rg.exe в изолированный кэш OpenCode (иначе он скачает его сам при первом grep/glob). Не критично.</summary>
    internal static async Task PreseedRipgrepAsync(Architecture arch, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        try
        {
            if (ProcessRunner.FindOnPath("rg.exe") is not null) return; // OpenCode возьмёт системный ripgrep
            var target = RipgrepPath;
            if (File.Exists(target)) return;

            var (platform, size, sha) = arch == Architecture.Arm64
                ? ("aarch64-pc-windows-msvc", 1675460L, "00d931fb5237c9696ca49308818edb76d8eb6fc132761cb2a1bd616b2df02f8e")
                : ("x86_64-pc-windows-msvc", 1810687L, "124510b94b6baa3380d051fdf4650eaa80a302c876d611e9dba0b2e18d87493a");
            var name = $"ripgrep-{RipgrepVersion}-{platform}.zip";
            var url = $"https://github.com/BurntSushi/ripgrep/releases/download/{RipgrepVersion}/{name}";
            var zip = Path.Combine(AppPaths.DownloadsDir, name);

            await HttpDownloader.DownloadFileAsync(url, zip, size, sha,
                new InlineProgress<DownloadProgress>(p => progress?.Report(ToStep(L.T("ripgrep (поиск по файлам)"), p))), ct);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            ExtractEntry(zip, "rg.exe", target, ct);
            TryDelete(zip);
            Log.Info("opencode", $"ripgrep {RipgrepVersion} установлен: {target}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("opencode", $"Не удалось подготовить ripgrep: {ex.Message}. OpenCode скачает его сам при первом поиске.");
            progress?.Report(new StepProgress(L.T("ripgrep не загружен — OpenCode скачает его сам при первом поиске")));
        }
    }

    /// <summary>Путь к opencode.exe: из конфига, из нашей папки или из PATH. null — не установлен.</summary>
    public static string? FindExecutable(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var configured = cfg.OpenCode?.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"')));
                if (File.Exists(full)) return full;
            }
            catch
            {
                // Некорректный путь в конфиге — ищем дальше.
            }
        }
        if (File.Exists(ManagedExePath)) return ManagedExePath;
        return ProcessRunner.FindOnPath("opencode.exe");
    }

    public static async Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        try
        {
            return (await OpenCodeReleases.GetLatestAsync(ct)).Version;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("opencode", $"Не удалось узнать последнюю версию OpenCode: {ex.Message}");
            return null;
        }
    }

    /// <summary>Удалить наш opencode.exe, изолированные данные и управляемый конфиг; очистить поля конфига.</summary>
    public static void Uninstall()
    {
        FileUtil.TryDeleteDirectory(BinDir);
        foreach (var dir in OpenCodeConfigWriter.IsolatedDirs)
            FileUtil.TryDeleteDirectory(dir);
        TryDelete(AppPaths.OpenCodeConfigFile);
        try
        {
            if (Directory.Exists(AppPaths.OpenCodeDir) && !Directory.EnumerateFileSystemEntries(AppPaths.OpenCodeDir).Any())
                Directory.Delete(AppPaths.OpenCodeDir);
        }
        catch
        {
            // Не критично.
        }

        if (File.Exists(ManagedExePath))
            throw new InvalidOperationException(L.T("Не удалось удалить OpenCode: файл используется. Закройте все окна OpenCode и повторите."));

        ConfigStore.Update(c =>
        {
            c.OpenCode.ExecutablePath = null;
            c.OpenCode.InstalledVersion = null;
        });
        Log.Info("opencode", "OpenCode удалён");
    }

    private static string Tail(string s) => s.Length <= 300 ? s.Trim() : s[^300..].Trim();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Занят — удалим в следующий раз.
        }
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>IProgress без SynchronizationContext: вызывается сразу в потоке загрузки.</summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value)
    {
        try { handler(value); } catch { /* сбой обработчика прогресса не прерывает загрузку */ }
    }
}
