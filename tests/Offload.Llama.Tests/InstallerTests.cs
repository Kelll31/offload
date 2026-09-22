using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Util;

namespace Offload.Llama.Tests;

/// <summary>
/// Установка из поддельного GitHub: архив собирается из настоящей сборки (OFFLOAD_TEST_LLAMA_BIN),
/// поэтому проверка «llama-server --version» выполняется по-настоящему.
/// </summary>
[Collection("AppPaths")]
public sealed class InstallerTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string _savedApi = GitHubReleases.ApiBase;
    private readonly string _savedWeb = GitHubReleases.WebBase;

    public void Dispose()
    {
        GitHubReleases.ApiBase = _savedApi;
        GitHubReleases.WebBase = _savedWeb;
        _home.Dispose();
    }

    private static readonly Lazy<byte[]> CpuZip = new(() =>
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in Directory.EnumerateFiles(TestEnv.LlamaBin!))
            {
                var name = Path.GetFileName(f);
                // Для запуска llama-server нужны только DLL и сам сервер.
                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && name != "llama-server.exe") continue;
                zip.CreateEntryFromFile(f, name, CompressionLevel.Fastest);
            }
        }
        return ms.ToArray();
    });

    private static byte[] FakeCudart()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("cudart64_13.dll");
            using var w = new StreamWriter(e.Open());
            w.Write("not a real dll");
        }
        return ms.ToArray();
    }

    private sealed record FakeAsset(string Name, byte[] Data, bool WithDigest = true);

    private static FakeHttpServer GitHubWith(string tag, params FakeAsset[] assets)
    {
        FakeHttpServer? self = null;
        var server = new FakeHttpServer(async (req, s, _) =>
        {
            if (req.Path.EndsWith("/releases/latest/download/nightly-tag.txt"))
            {
                await FakeHttpServer.WriteResponseAsync(s, 200, tag, "text/plain");
            }
            else if (req.Path.EndsWith($"/releases/tags/{tag}") || req.Path.Contains("/releases?per_page="))
            {
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = tag,
                    draft = false,
                    prerelease = true,
                    published_at = "2026-09-22T13:23:34Z",
                    assets = assets.Select(a => new
                    {
                        name = a.Name,
                        state = "uploaded",
                        size = a.Data.LongLength,
                        digest = a.WithDigest ? "sha256:" + Convert.ToHexString(SHA256.HashData(a.Data)).ToLowerInvariant() : null,
                        browser_download_url = $"{self!.BaseUrl}/dl/{a.Name}",
                    }),
                });
                if (req.Path.Contains("per_page")) json = "[" + json + "]";
                await FakeHttpServer.WriteResponseAsync(s, 200, json);
            }
            else if (req.Path.StartsWith("/dl/"))
            {
                var name = req.Path[4..];
                var a = assets.FirstOrDefault(x => x.Name == name);
                if (a is null) await FakeHttpServer.WriteResponseAsync(s, 404, "{}");
                else await FakeHttpServer.WriteBytesResponseAsync(s, a.Data);
            }
            else
            {
                await FakeHttpServer.WriteResponseAsync(s, 404, "{}");
            }
        });
        self = server;
        GitHubReleases.ApiBase = server.BaseUrl;
        GitHubReleases.WebBase = server.BaseUrl;
        return server;
    }

    [Fact]
    public async Task Install_Cpu_DownloadExtractValidateConfigCleanup()
    {
        TestEnv.RequireLlama();
        await using var server = GitHubWith("b99999", new FakeAsset("llama-b99999-bin-win-cpu-x64.zip", CpuZip.Value));

        AppPaths.EnsureCreated();
        var old = Directory.CreateDirectory(Path.Combine(AppPaths.LlamaDir, "b11000-vulkan")).FullName;
        File.WriteAllText(Path.Combine(old, "llama-server.exe"), "old");
        var foreign = Directory.CreateDirectory(Path.Combine(AppPaths.LlamaDir, "my-own-build")).FullName;
        var leftover = Directory.CreateDirectory(Path.Combine(AppPaths.LlamaDir, ".tmp-b99999-cpu-deadbeef")).FullName;

        var stages = new List<StepProgress>();
        var result = await LlamaInstaller.InstallAsync(LlamaBackend.Cpu, new SyncProgress(stages), TestContext.Current.CancellationToken);

        Assert.Equal("b99999", result.Tag);
        Assert.Equal(LlamaBackend.Cpu, result.Backend);
        Assert.Equal(Path.Combine(AppPaths.LlamaDir, "b99999-cpu"), result.InstallDir);
        Assert.True(File.Exists(result.ServerExePath));
        Assert.True(File.Exists(Path.Combine(result.InstallDir, "llama-server-impl.dll")));

        var cfg = ConfigStore.Reload();
        Assert.Equal("b99999", cfg.Llama.InstalledTag);
        Assert.Equal(LlamaBackend.Cpu, cfg.Llama.InstalledBackend);
        Assert.Equal(result.InstallDir, cfg.Llama.InstallDir);
        Assert.Equal(LlamaBackend.Cpu, cfg.Llama.Backend); // было Auto
        Assert.True(LlamaInstaller.IsInstalled(cfg));
        Assert.Equal(result.ServerExePath, LlamaInstaller.GetServerExePath(cfg));

        // Старая версия и остатки удалены, чужая папка — нет; главный архив удалён из загрузок.
        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(leftover));
        Assert.True(Directory.Exists(foreign));
        Assert.False(File.Exists(Path.Combine(AppPaths.DownloadsDir, "llama-b99999-bin-win-cpu-x64.zip")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(AppPaths.LlamaDir), d => Path.GetFileName(d).StartsWith('.'));

        // Прогресс — на русском, доля не убывает.
        Assert.Contains(stages, s => s.Stage.StartsWith("Поиск последней версии llama.cpp"));
        Assert.Contains(stages, s => s.Stage.StartsWith("Загрузка llama.cpp b99999"));
        Assert.Contains(stages, s => s.Stage.StartsWith("Распаковка"));
        Assert.Contains(stages, s => s.Stage.StartsWith("Проверка запуска"));
        Assert.EndsWith("установлен", stages[^1].Stage);
        var fractions = stages.Where(s => s.Fraction is not null).Select(s => s.Fraction!.Value).ToList();
        for (var i = 1; i < fractions.Count; i++) Assert.True(fractions[i] >= fractions[i - 1] - 1e-9, $"прогресс убывает: {fractions[i - 1]} → {fractions[i]}");
        Assert.Equal(1.0, fractions[^1]);

        // Повторная установка того же тега — быстрый выход без загрузки.
        var downloads = server.Requests.Count(r => r.Path.StartsWith("/dl/"));
        var again = await LlamaInstaller.InstallAsync(LlamaBackend.Cpu, null, TestContext.Current.CancellationToken);
        Assert.Equal(result.InstallDir, again.InstallDir);
        Assert.Equal(downloads, server.Requests.Count(r => r.Path.StartsWith("/dl/")));

        // Устройства: сборка CPU не видит видеокарт.
        Assert.Empty(await LlamaDevices.ListAsync(result.ServerExePath, TestContext.Current.CancellationToken));

        // Обновлений нет: установлен последний тег.
        var upd = await LlamaInstaller.CheckUpdateAsync(cfg, TestContext.Current.CancellationToken);
        Assert.False(upd.UpdateAvailable);
        Assert.Equal("b99999", upd.LatestTag);
    }

    [Fact]
    public async Task Install_ConfigLost_ReusesExtractedFolder()
    {
        TestEnv.RequireLlama();
        await using var server = GitHubWith("b99998", new FakeAsset("llama-b99998-bin-win-cpu-x64.zip", CpuZip.Value, WithDigest: false));
        var first = await LlamaInstaller.InstallAsync(LlamaBackend.Cpu, null, TestContext.Current.CancellationToken);

        ConfigStore.Update(c =>
        {
            c.Llama.InstallDir = null;
            c.Llama.InstalledTag = null;
        });
        var downloads = server.Requests.Count(r => r.Path.StartsWith("/dl/"));
        var second = await LlamaInstaller.InstallAsync(LlamaBackend.Cpu, null, TestContext.Current.CancellationToken);
        Assert.Equal(first.InstallDir, second.InstallDir);
        Assert.Equal(downloads, server.Requests.Count(r => r.Path.StartsWith("/dl/")));
        Assert.Equal("b99998", ConfigStore.Reload().Llama.InstalledTag);
    }

    [Fact]
    public async Task Install_BrokenFolderWithoutMarker_IsReplaced()
    {
        TestEnv.RequireLlama();
        await using var server = GitHubWith("b99997", new FakeAsset("llama-b99997-bin-win-cpu-x64.zip", CpuZip.Value));
        AppPaths.EnsureCreated();
        var broken = Directory.CreateDirectory(Path.Combine(AppPaths.LlamaDir, "b99997-cpu")).FullName;
        File.WriteAllText(Path.Combine(broken, "llama-server.exe"), "partial");

        var r = await LlamaInstaller.InstallAsync(LlamaBackend.Cpu, null, TestContext.Current.CancellationToken);
        Assert.Equal(broken, r.InstallDir);
        Assert.True(new FileInfo(r.ServerExePath).Length > 1000);
    }

    [Fact]
    public async Task Install_BadDigest_FailsWithoutInstalling()
    {
        TestEnv.RequireLlama();
        var data = CpuZip.Value;
        await using var server = GitHubWith("b99996", new FakeAsset("llama-b99996-bin-win-cpu-x64.zip", data));
        // Подменяем содержимое: хеш из «API» не совпадёт.
        var tampered = (byte[])data.Clone();
        tampered[^10] ^= 0xFF;
        await using var evil = GitHubWith("b99996", new FakeAsset("llama-b99996-bin-win-cpu-x64.zip", tampered));
        // Метаданные — от первого сервера, файл — от второго.
        GitHubReleases.ApiBase = server.BaseUrl;
        GitHubReleases.WebBase = server.BaseUrl;
        var release = await LlamaReleaseResolver.GetLatestAsync(TestContext.Current.CancellationToken);
        var asset = release.Assets[0] with { DownloadUrl = $"{evil.BaseUrl}/dl/{release.Assets[0].Name}" };
        var cache = ReleaseCache.Load()!;
        cache.Releases = [release with { Assets = [asset] }];
        cache.Save();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => LlamaInstaller.InstallAsync(LlamaBackend.Cpu, null, TestContext.Current.CancellationToken));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(AppPaths.LlamaDir, "b99996-cpu")));
        Assert.Null(ConfigStore.Reload().Llama.InstalledTag);
    }

    [Fact]
    public async Task Install_Cuda12Missing_FallsBackToCuda13WhenSupported()
    {
        TestEnv.RequireLlama();
        var hw = await HardwareDetector.DetectAsync(TestContext.Current.CancellationToken);
        Assert.SkipUnless(BackendAdvisor.SupportsCuda13(hw), "Нужна видеокарта NVIDIA с поддержкой CUDA 13");
        // «CUDA 13» здесь — сборка CPU под другим именем (--version работает без видеокарты).
        await using var server = GitHubWith("b99995",
            new FakeAsset("llama-b99995-bin-win-cuda-13.4-x64.zip", CpuZip.Value),
            new FakeAsset("cudart-llama-bin-win-cuda-13.4-x64.zip", FakeCudart()));
        var r = await LlamaInstaller.InstallAsync(LlamaBackend.Cuda12, null, TestContext.Current.CancellationToken);
        Assert.Equal(LlamaBackend.Cuda13, r.Backend);
        Assert.EndsWith("b99995-cuda13", r.InstallDir);
        Assert.True(File.Exists(Path.Combine(r.InstallDir, "cudart64_13.dll")));
        // cudart кэшируется в загрузках для следующих обновлений.
        Assert.True(File.Exists(Path.Combine(AppPaths.DownloadsDir, "cudart-llama-bin-win-cuda-13.4-x64.zip")));
        Assert.Equal(LlamaBackend.Cuda13, ConfigStore.Reload().Llama.InstalledBackend);
    }

    [Fact]
    public void ExtractZip_RejectsZipSlip()
    {
        var zipPath = Path.Combine(_home.Path, "evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("../evil.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("x");
        }
        var dest = Directory.CreateDirectory(Path.Combine(_home.Path, "out")).FullName;
        Assert.Throws<InvalidDataException>(() => InstallerImpl.ExtractZip(zipPath, dest, flatten: false, null, default));
        Assert.False(File.Exists(Path.Combine(_home.Path, "evil.txt")));
        // С flatten берётся только имя файла — выйти за пределы нельзя.
        InstallerImpl.ExtractZip(zipPath, dest, flatten: true, null, default);
        Assert.True(File.Exists(Path.Combine(dest, "evil.txt")));
    }

    [Fact]
    public void TryRemoveDirectory_SkipsFolderWithOpenFile()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home.Path, "b1-cpu")).FullName;
        var file = Path.Combine(dir, "llama.dll");
        File.WriteAllText(file, "x");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(InstallerImpl.TryRemoveDirectory(dir));
            Assert.True(File.Exists(file));
        }
        Assert.True(InstallerImpl.TryRemoveDirectory(dir));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void IsInstalled_FalseWithoutExe()
    {
        var cfg = new AppConfig();
        Assert.False(LlamaInstaller.IsInstalled(cfg));
        cfg.Llama.InstallDir = Path.Combine(_home.Path, "nothing");
        Assert.False(LlamaInstaller.IsInstalled(cfg));
        Assert.Null(LlamaInstaller.GetServerExePath(cfg));
    }

    [Fact]
    public void VcRuntime_StatusDoesNotThrow()
    {
        // На машине разработчика runtime обычно установлен; главное — проверка не падает.
        _ = VcRuntime.IsInstalled();
        Assert.True(VcRuntimeCheck.HasAppLocal(null) == false);
    }

    private sealed class SyncProgress(List<StepProgress> list) : IProgress<StepProgress>
    {
        public void Report(StepProgress value)
        {
            lock (list) list.Add(value);
        }
    }
}
