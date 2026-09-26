using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Processes;
using Offload.Core.Util;

namespace Offload.Llama;

/// <summary>
/// Установка сборки llama.cpp: загрузка архивов (с проверкой SHA-256 из digest GitHub), распаковка во временную
/// папку и атомарное переименование в llama.cpp\{tag}-{backend}, проверка запуска, обновление конфига, очистка.
/// Хранятся текущая, закреплённая (<see cref="LlamaSettings.PinnedTag"/>) и <see cref="KeepPreviousBuilds"/> предыдущих
/// сборок: на них можно переключиться без загрузки, на них же выполняется автоматический откат, если новая сборка
/// не прошла проверку «--version» или не смогла впервые запустить сервер (<see cref="ReportServerStartAsync"/>).
/// </summary>
internal static class InstallerImpl
{
    public const string ServerExeName = "llama-server.exe";
    internal const string MarkerFileName = ".offload-install.json";

    /// <summary>Сколько предыдущих сборок хранить сверх текущей и закреплённой.</summary>
    public const int KeepPreviousBuilds = 2;

    /// <summary>Одновременно идёт только одна установка или переключение в процессе.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Имена папок установок Offload (только их разрешено удалять при очистке).</summary>
    private static readonly Regex InstallDirName = new(
        @"^b\d+-(cuda12|cuda13|vulkan|rocm|sycl|cpu|openvino|opencladreno)(-[0-9a-f]{6})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Проверка запуска сборки («llama-server --version»); тесты подменяют её, чтобы не требовать настоящую сборку.</summary>
    internal static Func<string, CancellationToken, Task<string>> Validator = ValidateAsync;

    private static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    public static string DirName(string tag, LlamaBackend backend) => $"{tag}-{backend.ToString().ToLowerInvariant()}";

    public static async Task<LlamaInstallResult> InstallAsync(LlamaBackend backend, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(backend, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<LlamaInstallResult> InstallCoreAsync(LlamaBackend backend, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        var arm64 = IsArm64;
        if (backend == LlamaBackend.Auto)
        {
            Report(progress, L.T("Определение видеокарты…"));
            var hw = await HardwareDetector.DetectAsync(ct).ConfigureAwait(false);
            backend = BackendAdvisor.Recommend(hw).Backend;
            Log.Info("llama", $"Автовыбор сборки llama.cpp: {backend}");
        }

        var pinned = ConfigStore.Current.Llama.PinnedTag;
        if (!GitHubReleases.IsBuildTag(pinned)) pinned = null;

        // Закреплённая сборка уже есть на диске — переключаемся без сети.
        if (pinned is not null
            && ListBuildsCore(ConfigStore.Current).FirstOrDefault(b => b.Backend == backend && TagEquals(b.Tag, pinned)) is { } local)
        {
            var localName = $"llama.cpp {local.Tag} ({BackendAdvisor.DisplayName(local.Backend)})";
            if (!local.IsCurrent)
            {
                Report(progress, L.T("Проверка запуска llama-server…"), 0.96);
                await SwitchCoreAsync(local, ct).ConfigureAwait(false);
            }
            Report(progress, L.F("{0} установлен", localName), 1);
            return new LlamaInstallResult(local.Tag, local.Backend, local.InstallDir, Path.Combine(local.InstallDir, ServerExeName));
        }

        Report(progress, pinned is null ? L.T("Поиск последней версии llama.cpp…") : L.F("Поиск закреплённой версии llama.cpp {0}…", pinned));
        LlamaRelease release;
        try
        {
            release = await FindReleaseAsync(backend, arm64, pinned, ct).ConfigureAwait(false);
        }
        catch (LlamaBuildNotFoundException) when (backend == LlamaBackend.Cuda12)
        {
            // Сборки CUDA 12.4 больше нет в релизах — CUDA 13, если карта и драйвер её поддерживают.
            var hw = await HardwareDetector.DetectAsync(ct).ConfigureAwait(false);
            if (!BackendAdvisor.SupportsCuda13(hw))
                throw new InvalidOperationException(
                    L.T("В последних релизах llama.cpp нет сборки CUDA 12, а сборка CUDA 13 не поддерживается этой видеокартой или драйвером. Выберите сборку Vulkan."));
            Log.Warn("llama", "В релизах llama.cpp нет сборки CUDA 12 — устанавливается CUDA 13");
            backend = LlamaBackend.Cuda13;
            release = await FindReleaseAsync(backend, arm64, pinned, ct).ConfigureAwait(false);
        }

        var sel = AssetSelector.Select(release, backend, arm64)
                  ?? throw new InvalidOperationException(L.F("В релизе {0} нет сборки «{1}».", release.Tag, BackendAdvisor.DisplayName(backend)));
        var name = $"llama.cpp {release.Tag} ({BackendAdvisor.DisplayName(backend)})";

        AppPaths.EnsureCreated();
        var finalDir = Path.Combine(AppPaths.LlamaDir, DirName(release.Tag, backend));
        var cfg = ConfigStore.Current;

        if (IsValidInstall(finalDir, sel))
        {
            var exe = Path.Combine(finalDir, ServerExeName);
            if (PathEquals(cfg.Llama.InstallDir, finalDir) && cfg.Llama.InstalledTag == release.Tag && cfg.Llama.InstalledBackend == backend)
            {
                VcRuntimeCheck.EnsureAvailable(finalDir);
                Log.Info("llama", $"{name} уже установлен");
                Report(progress, L.F("{0} уже установлен", name), 1);
                return new LlamaInstallResult(release.Tag, backend, finalDir, exe);
            }
            Log.Info("llama", $"{name} уже распакован в {finalDir}, загрузка не нужна");
        }
        else
        {
            finalDir = await DownloadAndExtractAsync(sel, finalDir, name, progress, ct).ConfigureAwait(false);
        }

        var serverExe = Path.Combine(finalDir, ServerExeName);
        Report(progress, L.T("Проверка запуска llama-server…"), 0.96);
        string version;
        try
        {
            version = await Validator(serverExe, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not LlamaVcRuntimeMissingException)
        {
            // Нет VC++ Runtime — не вина сборки: пользователь поставит runtime и повторит. Остальное — откат.
            var rolledBack = await RollbackFailedInstallAsync(finalDir, release.Tag, ex, ct).ConfigureAwait(false);
            if (rolledBack is null) throw;
            throw rolledBack;
        }
        Log.Info("llama", $"Установлен {name}: {version}");

        var dir = finalDir;
        var installedBackend = backend;
        // Прежняя рабочая сборка — цель автоматического отката, пока новая ни разу не запустила сервер.
        var before = ConfigStore.Current.Llama.InstallDir;
        var previous = !PathEquals(before, dir) && GetServerExePath(ConfigStore.Current) is not null ? Path.GetFullPath(before!) : null;
        ConfigStore.Update(c =>
        {
            c.Llama.InstalledTag = release.Tag;
            c.Llama.InstalledBackend = installedBackend;
            c.Llama.InstallDir = dir;
            if (c.Llama.Backend == LlamaBackend.Auto) c.Llama.Backend = installedBackend;
            c.Llama.PreviousInstallDir = previous;
            c.Llama.RolledBackFromDir = null;
            if (TagEquals(c.Llama.FailedTag, release.Tag)) c.Llama.FailedTag = null;
        });

        CleanupDownloads(sel);
        CleanupOldVersions(finalDir);

        Report(progress, L.F("{0} установлен", name), 1);
        return new LlamaInstallResult(release.Tag, backend, finalDir, serverExe);
    }

    private static async Task<LlamaRelease> FindReleaseAsync(LlamaBackend backend, bool arm64, string? pinned, CancellationToken ct)
    {
        if (pinned is null)
            return await GitHubReleases.GetLatestAsync(r => AssetSelector.Select(r, backend, arm64) is not null, BackendAdvisor.DisplayName(backend), ct)
                .ConfigureAwait(false);
        var rel = await GitHubReleases.GetByTagAsync(pinned, ct).ConfigureAwait(false);
        if (AssetSelector.Select(rel, backend, arm64) is null)
            throw new LlamaBuildNotFoundException(
                L.F("В закреплённой версии llama.cpp {0} нет сборки «{1}». Снимите закрепление или выберите другую сборку.", pinned, BackendAdvisor.DisplayName(backend)));
        return rel;
    }

    // ---------- Сохранённые сборки, переключение и откат ----------

    internal static bool TagEquals(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string FullDir(string dir) => Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Полностью установленные сборки в папке llama.cpp (есть метка и llama-server.exe), новые первыми.</summary>
    internal static List<LlamaInstalledBuild> ListBuildsCore(AppConfig cfg)
    {
        var list = new List<LlamaInstalledBuild>();
        try
        {
            if (!Directory.Exists(AppPaths.LlamaDir)) return list;
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.LlamaDir))
            {
                if (!InstallDirName.IsMatch(Path.GetFileName(dir)) || !File.Exists(Path.Combine(dir, ServerExeName))) continue;
                if (ReadMarker(dir) is not { } m) continue;
                list.Add(new LlamaInstalledBuild(m.Tag, m.Backend, FullDir(dir), m.InstalledAtUtc,
                    PathEquals(dir, cfg.Llama.InstallDir), TagEquals(m.Tag, cfg.Llama.PinnedTag)));
            }
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Список сборок llama.cpp: {ex.Message}");
        }
        return list.OrderByDescending(b => GitHubReleases.BuildNumber(b.Tag)).ThenByDescending(b => b.InstalledAtUtc).ToList();
    }

    public static async Task<LlamaInstallResult> SwitchToAsync(string installDir, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var build = ListBuildsCore(ConfigStore.Current).FirstOrDefault(b => PathEquals(b.InstallDir, installDir))
                        ?? throw new InvalidOperationException(L.F("Сборка llama.cpp в папке {0} не найдена или повреждена.", installDir));
            return await SwitchCoreAsync(build, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Проверить сохранённую сборку и сделать её текущей (без загрузки).</summary>
    private static async Task<LlamaInstallResult> SwitchCoreAsync(LlamaInstalledBuild build, CancellationToken ct)
    {
        var exe = Path.Combine(build.InstallDir, ServerExeName);
        var version = await Validator(exe, ct).ConfigureAwait(false);
        ConfigStore.Update(c =>
        {
            c.Llama.InstalledTag = build.Tag;
            c.Llama.InstalledBackend = build.Backend;
            c.Llama.InstallDir = build.InstallDir;
            c.Llama.Backend = build.Backend;
            c.Llama.PreviousInstallDir = null;
            c.Llama.RolledBackFromDir = null;
        });
        Log.Info("llama", $"Текущая сборка llama.cpp: {build.Tag} ({build.Backend}), {version}");
        return new LlamaInstallResult(build.Tag, build.Backend, build.InstallDir, exe);
    }

    public static void SetPinnedTag(string? tag)
    {
        tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        if (tag is not null && !GitHubReleases.IsBuildTag(tag))
            throw new ArgumentException(L.F("Неверный тег сборки llama.cpp: «{0}».", tag), nameof(tag));
        ConfigStore.Update(c => c.Llama.PinnedTag = tag);
        Log.Info("llama", tag is null ? "Закрепление сборки llama.cpp снято" : $"Сборка llama.cpp закреплена: {tag}");
    }

    /// <summary>
    /// Новая сборка не прошла проверку «--version». Если прежняя сборка из конфига цела — она остаётся текущей,
    /// иначе — переключение на самую новую сохранённую. Сломанная папка удаляется, тег запоминается как неудачный.
    /// </summary>
    /// <returns>Исключение с объяснением отката или null — откатиться некуда (пробросить исходную ошибку).</returns>
    private static async Task<Exception?> RollbackFailedInstallAsync(string failedDir, string tag, Exception error, CancellationToken ct)
    {
        Log.Warn("llama", $"Сборка llama.cpp {tag} не прошла проверку: {error.Message}");
        var cfg = ConfigStore.Current;
        ConfigStore.Update(c => c.Llama.FailedTag = tag);
        var isCurrent = PathEquals(failedDir, cfg.Llama.InstallDir);
        if (!isCurrent && TryRemoveDirectory(failedDir)) Log.Info("llama", $"Удалена непроверенная сборка: {failedDir}");

        if (!isCurrent && GetServerExePath(cfg) is not null)
        {
            return new LlamaInstallRolledBackException(
                L.F("Новая сборка llama.cpp {0} не запустилась, оставлена прежняя сборка {1}. Причина: {2}", tag, cfg.Llama.InstalledTag ?? "—", error.Message),
                null, error);
        }

        foreach (var b in ListBuildsCore(ConfigStore.Current).Where(b => !PathEquals(b.InstallDir, failedDir)))
        {
            try
            {
                var r = await SwitchCoreAsync(b, ct).ConfigureAwait(false);
                Log.Warn("llama", $"Откат llama.cpp на сохранённую сборку {b.Tag} ({b.Backend})");
                return new LlamaInstallRolledBackException(
                    L.F("Новая сборка llama.cpp {0} не запустилась, выполнен откат на сохранённую сборку {1}. Причина: {2}", tag, b.Tag, error.Message),
                    r, error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("llama", $"Сохранённая сборка {b.Tag} тоже не запускается: {ex.Message}");
            }
        }
        return null;
    }

    public static async Task<LlamaInstallResult?> ReportServerStartAsync(bool started, CancellationToken ct)
    {
        var l = ConfigStore.Current.Llama;
        if (l.PreviousInstallDir is null && l.RolledBackFromDir is null) return null;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cfg = ConfigStore.Current;
            l = cfg.Llama;
            if (started)
            {
                if (l.PreviousInstallDir is not null || l.RolledBackFromDir is not null)
                {
                    Log.Info("llama", $"Сборка llama.cpp {l.InstalledTag} запустила сервер — откат больше не нужен");
                    ConfigStore.Update(c =>
                    {
                        c.Llama.PreviousInstallDir = null;
                        c.Llama.RolledBackFromDir = null;
                    });
                }
                return null;
            }

            if (l.RolledBackFromDir is { } from)
            {
                // Прежняя сборка тоже не запустилась — дело не в новой сборке: возвращаем её и повторять не предлагаем.
                ConfigStore.Update(c => c.Llama.RolledBackFromDir = null);
                if (ListBuildsCore(cfg).FirstOrDefault(b => PathEquals(b.InstallDir, from)) is not { } back) return null;
                try
                {
                    await SwitchCoreAsync(back, ct).ConfigureAwait(false);
                    ConfigStore.Update(c =>
                    {
                        if (TagEquals(c.Llama.FailedTag, back.Tag)) c.Llama.FailedTag = null;
                    });
                    Log.Warn("llama", $"Откат не помог (сервер не запускается и с прежней сборкой) — возвращена сборка {back.Tag}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn("llama", $"Не удалось вернуть сборку {back.Tag}: {ex.Message}");
                }
                return null;
            }

            var failedDir = l.InstallDir;
            var failedTag = l.InstalledTag;
            var target = ListBuildsCore(cfg).FirstOrDefault(b => PathEquals(b.InstallDir, l.PreviousInstallDir));
            if (target is null)
            {
                Log.Warn("llama", $"Новая сборка llama.cpp {failedTag} не запустила сервер, а прежней сборки уже нет — откат невозможен");
                ConfigStore.Update(c => c.Llama.PreviousInstallDir = null);
                return null;
            }
            try
            {
                var result = await SwitchCoreAsync(target, ct).ConfigureAwait(false);
                ConfigStore.Update(c =>
                {
                    c.Llama.RolledBackFromDir = failedDir;
                    c.Llama.FailedTag = failedTag;
                });
                Log.Warn("llama", $"llama-server не запустился с новой сборкой {failedTag} — выполнен откат на {target.Tag} ({target.Backend})");
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("llama", $"Откат на сборку {target.Tag} не удался: {ex.Message}");
                ConfigStore.Update(c => c.Llama.PreviousInstallDir = null);
                return null;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <returns>Итоговая папка установки.</returns>
    private static async Task<string> DownloadAndExtractAsync(
        LlamaBuildSelection sel, string finalDir, string name, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        var files = new List<(LlamaAsset Asset, string Stage)> { (sel.Main, L.F("Загрузка {0}", name)) };
        if (sel.CudaRuntime is { } rt)
            files.Add((rt, L.F("Загрузка библиотек NVIDIA CUDA {0}", AssetSelector.CudaVersionOf(rt))));

        // Все контрольные суммы — до первой загрузки, чтобы не качать гигабайты зря.
        foreach (var (asset, _) in files) DownloadPolicy.RequireChecksum(asset.Sha256, asset.Name, "llama.cpp");

        var totalBytes = files.Sum(f => Math.Max(0, f.Asset.Size));
        long doneBytes = 0;
        var paths = new List<string>();
        foreach (var (asset, stage) in files)
        {
            var path = Path.Combine(AppPaths.DownloadsDir, asset.Name);
            var before = doneBytes;
            var size = asset.Size > 0 ? asset.Size : (long?)null;
            Log.Info("llama", $"Загрузка {asset.Name} ({FileUtil.FormatBytes(asset.Size)}){(asset.Sha256 is null ? ", без контрольной суммы" : "")}");
            await HttpDownloader.DownloadFileAsync(asset.DownloadUrl, path, size, asset.Sha256,
                new ActionProgress<DownloadProgress>(p =>
                {
                    var overall = totalBytes > 0 ? (before + Math.Min(p.BytesReceived, size ?? p.BytesReceived)) / (double)totalBytes * 0.85 : (double?)null;
                    if (p.Stage == DownloadStage.Verifying)
                        Report(progress, L.T("Проверка контрольной суммы…"), totalBytes > 0 ? (before + (size ?? 0)) / (double)totalBytes * 0.85 : null,
                            p.TotalBytes is > 0 ? L.F("{0} из {1}", FileUtil.FormatBytes(p.BytesReceived), FileUtil.FormatBytes(p.TotalBytes.Value)) : null);
                    else
                        Report(progress, stage, overall, DownloadDetail(p));
                }), ct).ConfigureAwait(false);
            doneBytes += Math.Max(0, asset.Size);
            paths.Add(path);
        }

        ct.ThrowIfCancellationRequested();
        CleanupTempDirs();
        var temp = Path.Combine(AppPaths.LlamaDir, $".tmp-{Path.GetFileName(finalDir)}-{Guid.NewGuid():N}");
        try
        {
            Report(progress, L.T("Распаковка…"), 0.86);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(temp);
                ExtractZip(paths[0], temp, flatten: false, (done, total) =>
                    Report(progress, L.T("Распаковка…"), 0.86 + 0.07 * done / Math.Max(1, total), L.F("{0} из {1}", FileUtil.FormatBytes(done), FileUtil.FormatBytes(total))), ct);
            }, ct).ConfigureAwait(false);

            var exeDir = FindExeDir(temp) ?? throw new InvalidOperationException(
                L.F("В архиве {0} нет файла {1}. Возможно, изменился формат релизов llama.cpp — сообщите разработчикам Offload.", sel.Main.Name, ServerExeName));

            if (paths.Count > 1)
            {
                Report(progress, L.T("Распаковка библиотек CUDA…"), 0.93);
                // Библиотеки CUDA должны лежать рядом с llama-server.exe.
                await Task.Run(() => ExtractZip(paths[1], exeDir, flatten: true, (done, total) =>
                    Report(progress, L.T("Распаковка библиотек CUDA…"), 0.93 + 0.02 * done / Math.Max(1, total),
                        L.F("{0} из {1}", FileUtil.FormatBytes(done), FileUtil.FormatBytes(total))), ct), ct).ConfigureAwait(false);
            }

            WriteMarker(exeDir, sel);

            if (Directory.Exists(finalDir) && !TryRemoveDirectory(finalDir))
            {
                // Папка с тем же именем повреждена и занята — ставим рядом.
                finalDir += "-" + Guid.NewGuid().ToString("N")[..6];
            }
            await MoveDirectoryAsync(exeDir, finalDir, ct).ConfigureAwait(false);
            return finalDir;
        }
        catch (InvalidDataException ex)
        {
            // Повреждённый архив — удаляем, чтобы следующая попытка скачала заново.
            foreach (var p in paths) TryDeleteFile(p);
            throw new InvalidOperationException(L.F("Архив llama.cpp повреждён ({0}). Повторите установку — файл будет загружен заново.", ex.Message), ex);
        }
        finally
        {
            if (Directory.Exists(temp)) TryRemoveDirectory(temp);
        }
    }

    internal static string DownloadDetail(DownloadProgress p)
    {
        var parts = new List<string>
        {
            p.TotalBytes is > 0
                ? L.F("{0} из {1}", FileUtil.FormatBytes(p.BytesReceived), FileUtil.FormatBytes(p.TotalBytes.Value))
                : FileUtil.FormatBytes(p.BytesReceived),
        };
        if (p.BytesPerSecond > 1) parts.Add(FileUtil.FormatSpeed(p.BytesPerSecond));
        if (p.Eta is { } eta) parts.Add(L.F("осталось {0}", FileUtil.FormatDuration(eta)));
        return string.Join(" · ", parts);
    }

    /// <summary>Распаковка с защитой от выхода за пределы папки (zip slip). flatten — только имена файлов.</summary>
    internal static void ExtractZip(string zipPath, string destDir, bool flatten, Action<long, long>? onProgress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var total = zip.Entries.Sum(e => e.Length);
        long done = 0;
        var root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sw = Stopwatch.StartNew();
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var isDir = string.IsNullOrEmpty(e.Name);
            if (isDir && flatten) continue;
            var rel = flatten ? e.Name : e.FullName;
            var target = Path.GetFullPath(Path.Combine(root, rel));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.F("недопустимый путь в архиве: {0}", e.FullName));
            if (isDir)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
            done += e.Length;
            if (sw.ElapsedMilliseconds >= 200)
            {
                onProgress?.Invoke(done, total);
                sw.Restart();
            }
        }
        onProgress?.Invoke(done, total);
    }

    private static string? FindExeDir(string root)
    {
        if (File.Exists(Path.Combine(root, ServerExeName))) return root;
        var found = Directory.EnumerateFiles(root, ServerExeName, SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
        return found is null ? null : Path.GetDirectoryName(found);
    }

    private static async Task MoveDirectoryAsync(string source, string dest, CancellationToken ct)
    {
        // Антивирус может ненадолго держать только что распакованные файлы — повторяем.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, dest);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Log.Debug("llama", $"Переименование {source} → {dest} (попытка {attempt}): {ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    L.F("Не удалось переместить файлы llama.cpp в {0}: {1}. Возможно, папку блокирует антивирус — повторите установку.", dest, ex.Message), ex);
            }
        }
    }

    // ---------- Проверка установленной сборки ----------

    private sealed record InstallMarker(string Tag, LlamaBackend Backend, string MainAsset, string? RuntimeAsset, DateTime InstalledAtUtc);

    private static void WriteMarker(string dir, LlamaBuildSelection sel)
    {
        var marker = new InstallMarker(sel.Release.Tag, sel.Backend, sel.Main.Name, sel.CudaRuntime?.Name, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(dir, MarkerFileName), JsonSerializer.Serialize(marker, Json.Options), FileUtil.Utf8NoBom);
    }

    /// <summary>Метка установки или null (нет, повреждена, неизвестная сборка).</summary>
    private static InstallMarker? ReadMarker(string dir)
    {
        try
        {
            var path = Path.Combine(dir, MarkerFileName);
            if (!File.Exists(path)) return null;
            var m = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path), Json.Options);
            return m is not null && GitHubReleases.IsBuildTag(m.Tag) && m.Backend != LlamaBackend.Auto ? m : null;
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Метка установки {dir}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Папка полностью установлена (метка записывается последней перед переименованием) и в ней есть llama-server.exe.</summary>
    internal static bool IsValidInstall(string dir, LlamaBuildSelection sel)
    {
        if (!File.Exists(Path.Combine(dir, ServerExeName))) return false;
        var m = ReadMarker(dir);
        return m is not null
               && string.Equals(m.Tag, sel.Release.Tag, StringComparison.OrdinalIgnoreCase)
               && m.Backend == sel.Backend
               && string.Equals(m.MainAsset, sel.Main.Name, StringComparison.OrdinalIgnoreCase)
               && string.Equals(m.RuntimeAsset, sel.CudaRuntime?.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Запуск «llama-server --version». Возвращает строку версии.</summary>
    internal static async Task<string> ValidateAsync(string exe, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(exe)!;
        if (!File.Exists(exe)) throw new InvalidOperationException(L.F("Не найден {0} — переустановите llama.cpp.", exe));
        VcRuntimeCheck.EnsureAvailable(dir);

        var r = await ProcessRunner.RunAsync(exe, ["--version"], workingDirectory: dir, timeout: TimeSpan.FromSeconds(60), ct: ct)
            .ConfigureAwait(false);
        if (r.TimedOut)
            throw new InvalidOperationException(L.T("llama-server не ответил на запрос версии за 60 секунд. Возможно, запуск блокирует антивирус."));
        if (r.ExitCode != 0)
        {
            if (NtStatus.StartupFailure(r.ExitCode) is { } known) throw known;
            if (r.ExitCode is NtStatus.AccessViolation or NtStatus.StackBufferOverrun
                && VcRuntimeCheck.GetStatus() == VcRuntimeStatus.Outdated && !VcRuntimeCheck.HasAppLocal(dir))
                throw new LlamaVcRuntimeMissingException(NtStatus.VcOutdatedMessage);
            var tail = OutputTail(r.StdErr + "\n" + r.StdOut, 5);
            throw new InvalidOperationException(
                L.F("Установленный llama-server не запускается (код {0}).", NtStatus.Format(r.ExitCode)) + (tail.Length > 0 ? " " + tail : ""));
        }
        var m = Regex.Match(r.StdOut + "\n" + r.StdErr, @"version:\s*(?<v>[^\r\n]+)", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["v"].Value.Trim() : L.T("версия неизвестна");
    }

    internal static string OutputTail(string text, int lines)
    {
        var list = StripAnsi(text).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", list.TakeLast(lines));
    }

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);

    internal static string StripAnsi(string s) => s.Contains('\x1B') ? Ansi.Replace(s, "") : s;

    // ---------- Очистка ----------

    /// <summary>Главный архив больше не нужен; cudart кэшируется (имя без номера сборки), устаревшие версии той же major удаляются.</summary>
    private static void CleanupDownloads(LlamaBuildSelection sel)
    {
        TryDeleteFile(Path.Combine(AppPaths.DownloadsDir, sel.Main.Name));
        if (sel.CudaRuntime is not { } rt) return;
        var major = AssetSelector.CudaVersionOf(rt)?.Split('.')[0];
        if (major is null) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.DownloadsDir, $"cudart-llama-bin-win-cuda-{major}.*.zip"))
            {
                if (!string.Equals(Path.GetFileName(f), rt.Name, StringComparison.OrdinalIgnoreCase)) TryDeleteFile(f);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Очистка загрузок: {ex.Message}");
        }
    }

    /// <summary>
    /// Удалить прежние версии, кроме текущей, закреплённой, цели отката, <see cref="KeepPreviousBuilds"/> самых новых
    /// из остальных полных установок и тех, из которых сейчас запущен llama-server. Неполные установки (без метки) удаляются.
    /// </summary>
    internal static void CleanupOldVersions(string keepDir)
    {
        try
        {
            var inUse = RunningServerDirs();
            var cfg = ConfigStore.Current;
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FullDir(keepDir) };
            foreach (var d in new[] { cfg.Llama.InstallDir, cfg.Llama.PreviousInstallDir, cfg.Llama.RolledBackFromDir })
            {
                if (!string.IsNullOrWhiteSpace(d)) keep.Add(FullDir(d));
            }
            var builds = ListBuildsCore(cfg);
            foreach (var b in builds.Where(b => b.IsPinned)) keep.Add(b.InstallDir);
            // N предыдущих — самые новые из остальных (цель отката обычно среди них, но хранится в любом случае).
            var previous = builds.Where(b => !b.IsCurrent && !b.IsPinned && !PathEquals(b.InstallDir, keepDir)).Take(KeepPreviousBuilds).ToList();
            foreach (var b in previous) keep.Add(b.InstallDir);

            foreach (var dir in Directory.EnumerateDirectories(AppPaths.LlamaDir))
            {
                var dirName = Path.GetFileName(dir);
                if (keep.Contains(FullDir(dir))) continue;
                var ours = InstallDirName.IsMatch(dirName)
                           || dirName.StartsWith(".tmp-", StringComparison.Ordinal)
                           || dirName.StartsWith(".del-", StringComparison.Ordinal);
                if (!ours) continue;
                if (inUse.Contains(Path.GetFullPath(dir)))
                {
                    Log.Info("llama", $"Старая версия {dirName} не удалена: из неё запущен llama-server");
                    continue;
                }
                if (TryRemoveDirectory(dir)) Log.Info("llama", $"Удалена старая версия llama.cpp: {dirName}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("llama", $"Очистка старых версий llama.cpp: {ex.Message}");
        }
    }

    /// <summary>Остатки прерванных установок (.tmp-*) и недоудалённые папки (.del-*).</summary>
    private static void CleanupTempDirs()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.LlamaDir))
            {
                var n = Path.GetFileName(dir);
                if (n.StartsWith(".tmp-", StringComparison.Ordinal) || n.StartsWith(".del-", StringComparison.Ordinal))
                    TryRemoveDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Очистка временных папок: {ex.Message}");
        }
    }

    /// <summary>
    /// Сначала переименование (не удаётся, если внутри есть файлы запущенного процесса — тогда папка не трогается),
    /// затем удаление. Так занятая папка не остаётся удалённой наполовину.
    /// </summary>
    internal static bool TryRemoveDirectory(string dir)
    {
        var target = dir;
        if (!Path.GetFileName(dir).StartsWith(".del-", StringComparison.Ordinal))
        {
            var trash = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir))!, $".del-{Guid.NewGuid():N}");
            try
            {
                Directory.Move(dir, trash);
                target = trash;
            }
            catch (Exception ex)
            {
                Log.Debug("llama", $"Папка {dir} занята и не удалена: {ex.Message}");
                return false;
            }
        }
        try
        {
            Directory.Delete(target, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Не удалось удалить {target}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Папки, из которых сейчас запущены процессы llama-server (любые экземпляры Offload).</summary>
    private static HashSet<string> RunningServerDirs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ServerExeName)))
        {
            using (p)
            {
                try
                {
                    var file = p.MainModule?.FileName;
                    if (file is not null) set.Add(Path.GetDirectoryName(Path.GetFullPath(file))!);
                }
                catch
                {
                    // Нет доступа к процессу другого пользователя — не наш.
                }
            }
        }
        return set;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Debug("llama", $"Не удалось удалить {path}: {ex.Message}"); }
    }

    internal static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void Report(IProgress<StepProgress>? progress, string stage, double? fraction = null, string? detail = null)
    {
        try { progress?.Report(new StepProgress(stage, fraction, detail)); }
        catch (Exception ex) { Log.Debug("llama", $"Обработчик прогресса: {ex.Message}"); }
    }

    // ---------- Состояние установки ----------

    public static string? GetServerExePath(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var dir = cfg.Llama?.InstallDir;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var exe = Path.Combine(dir, ServerExeName);
            return File.Exists(exe) ? Path.GetFullPath(exe) : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<LlamaUpdateInfo> CheckUpdateAsync(AppConfig cfg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var backend = cfg.Llama.InstalledBackend != LlamaBackend.Auto ? cfg.Llama.InstalledBackend : cfg.Llama.Backend;
        var installed = GetServerExePath(cfg) is not null ? cfg.Llama.InstalledTag : null;
        // Закреплённая сборка: новые не предлагаются (и в сеть ходить незачем).
        if (GitHubReleases.IsBuildTag(cfg.Llama.PinnedTag))
            return new LlamaUpdateInfo(false, installed, cfg.Llama.PinnedTag!, Pinned: true);
        var arm64 = IsArm64;
        Func<LlamaRelease, bool> accept = backend == LlamaBackend.Auto
            ? GitHubReleases.HasWindowsBuild
            : r => AssetSelector.Select(r, backend, arm64) is not null;
        LlamaRelease latest;
        try
        {
            latest = await GitHubReleases.GetLatestAsync(accept, BackendAdvisor.DisplayName(backend), ct).ConfigureAwait(false);
        }
        catch (LlamaBuildNotFoundException ex) when (installed is not null)
        {
            Log.Info("llama", $"Проверка обновлений llama.cpp: {ex.Message}");
            return new LlamaUpdateInfo(false, installed, installed);
        }
        var newer = installed is not null && GitHubReleases.BuildNumber(latest.Tag) > GitHubReleases.BuildNumber(installed);
        if (newer && TagEquals(latest.Tag, cfg.Llama.FailedTag))
        {
            // Эта сборка уже не прошла проверку или первый запуск — повторно не предлагаем, ждём следующую.
            Log.Info("llama", $"Обновление llama.cpp {latest.Tag} не предлагается: эта сборка ранее не запустилась");
            newer = false;
        }
        return new LlamaUpdateInfo(newer, installed, latest.Tag);
    }
}

/// <summary>Разбор вывода «llama-server --list-devices».</summary>
internal static class DeviceList
{
    private static readonly Regex LogLine = new(@"^\d+\.\d+\.\d+\.\d+\s+[DIWE]\s", RegexOptions.CultureInvariant);

    public static async Task<IReadOnlyList<string>> ListAsync(string serverExePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverExePath) || !File.Exists(serverExePath))
            throw new FileNotFoundException(L.F("Не найден llama-server.exe: {0}", serverExePath), serverExePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(serverExePath))!;
        VcRuntimeCheck.EnsureAvailable(dir);

        var r = await ProcessRunner.RunAsync(serverExePath, ["--list-devices"], workingDirectory: dir,
            timeout: TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false);
        if (r.TimedOut)
            throw new InvalidOperationException(L.T("llama-server не вернул список устройств за 60 секунд."));
        if (r.ExitCode != 0)
        {
            if (NtStatus.StartupFailure(r.ExitCode) is { } known) throw known;
            throw new InvalidOperationException(
                L.F("Не удалось получить список устройств (код {0}): {1}", NtStatus.Format(r.ExitCode), InstallerImpl.OutputTail(r.StdErr + "\n" + r.StdOut, 4)));
        }
        var list = Parse(r.StdOut);
        if (list.Count == 0 && !r.StdOut.Contains("Available devices", StringComparison.OrdinalIgnoreCase)) list = Parse(r.StdErr);
        Log.Info("llama", "Устройства llama.cpp: " + (list.Count == 0 ? "нет (только процессор)" : string.Join("; ", list)));
        return list;
    }

    /// <summary>Строки после «Available devices:», кроме «(none)» и строк журнала.</summary>
    internal static List<string> Parse(string output)
    {
        var result = new List<string>();
        var inList = false;
        foreach (var raw in InstallerImpl.StripAnsi(output).Split('\n'))
        {
            var line = raw.Trim();
            if (!inList)
            {
                if (line.StartsWith("Available devices", StringComparison.OrdinalIgnoreCase)) inList = true;
                continue;
            }
            if (line.Length == 0 || line == "(none)" || LogLine.IsMatch(line)) continue;
            result.Add(line);
        }
        return result;
    }
}
