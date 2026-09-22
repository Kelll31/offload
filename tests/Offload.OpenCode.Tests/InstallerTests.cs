using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Net;

namespace Offload.OpenCode.Tests;

[Collection("AppPaths")]
public class InstallerTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.18.32",
          "assets": [
            { "name": "opencode-windows-x64.zip", "size": 62101772, "state": "uploaded",
              "browser_download_url": "https://github.com/anomalyco/opencode/releases/download/v1.18.32/opencode-windows-x64.zip",
              "digest": "sha256:1483C72D5ADCED825590A0ECF8CC18B3E87E535960A125DBF539D33BCE135D0F" },
            { "name": "opencode-windows-x64-baseline.zip", "size": 62101771, "state": "uploaded",
              "browser_download_url": "https://example/baseline.zip", "digest": "md5:abc" },
            { "name": "opencode-windows-arm64.zip", "size": 1, "state": "starter",
              "browser_download_url": "https://example/arm64.zip" }
          ]
        }
        """;

    [Theory]
    [InlineData(Architecture.X64, true, "opencode-windows-x64.zip")]
    [InlineData(Architecture.X64, false, "opencode-windows-x64-baseline.zip")]
    [InlineData(Architecture.Arm64, false, "opencode-windows-arm64.zip")]
    public void PreferredAsset_ByCpu(Architecture arch, bool avx2, string expected) =>
        Assert.Equal(expected, OpenCodeReleases.PreferredAssets(arch, avx2)[0]);

    [Fact]
    public void ParseRelease_DigestAndStates()
    {
        var r = OpenCodeReleases.ParseRelease(ReleaseJson);
        Assert.Equal("1.18.32", r.Version);
        Assert.True(r.FromApi);
        Assert.Equal(2, r.Assets.Count); // arm64 ещё загружается — пропущен
        Assert.Equal("1483c72d5adced825590a0ecf8cc18b3e87e535960a125dbf539d33bce135d0f", r.Assets[0].Sha256);
        Assert.Null(r.Assets[1].Sha256);

        Assert.Equal("opencode-windows-x64.zip", OpenCodeReleases.SelectAsset(r, Architecture.X64, true)!.Name);
        Assert.Equal("opencode-windows-x64-baseline.zip", OpenCodeReleases.SelectAsset(r, Architecture.X64, false)!.Name);
        // Нет ARM64-сборки — берём x64-baseline под эмуляцией.
        Assert.Equal("opencode-windows-x64-baseline.zip", OpenCodeReleases.SelectAsset(r, Architecture.Arm64, false)!.Name);
        Assert.Null(OpenCodeReleases.SelectAsset(r, Architecture.X86, true));
    }

    [Fact]
    public void Fallback_UsesDirectUrls()
    {
        var pinned = OpenCodeReleases.FallbackRelease("v1.18.32");
        Assert.Equal("1.18.32", pinned.Version);
        Assert.False(pinned.FromApi);
        Assert.Equal("https://github.com/anomalyco/opencode/releases/download/v1.18.32/opencode-windows-x64.zip",
            OpenCodeReleases.SelectAsset(pinned, Architecture.X64, true)!.Url);
        var latest = OpenCodeReleases.FallbackRelease(null);
        Assert.Null(latest.Version);
        Assert.EndsWith("/releases/latest/download/opencode-windows-x64-baseline.zip",
            OpenCodeReleases.SelectAsset(latest, Architecture.X64, false)!.Url);
        Assert.Equal("v1.18.32", OpenCodeReleases.TagFromReleaseUrl(new Uri("https://github.com/anomalyco/opencode/releases/tag/v1.18.32")));
        Assert.Null(OpenCodeReleases.TagFromReleaseUrl(new Uri("https://github.com/anomalyco/opencode/releases")));
    }

    [Fact]
    public async Task GetLatest_UsesFreshCacheWithoutNetwork()
    {
        using var home = new TempHome();
        var saved = OpenCodeReleases.ApiBase;
        OpenCodeReleases.ApiBase = "http://127.0.0.1:1"; // сеть не должна понадобиться
        try
        {
            new ReleaseCacheFile { FetchedAtUtc = DateTime.UtcNow, Release = OpenCodeReleases.ParseRelease(ReleaseJson) }.Save();
            Assert.True(File.Exists(Path.Combine(AppPaths.DataDir, "opencode-release-cache.json")));
            var r = await OpenCodeReleases.GetLatestAsync(CancellationToken.None);
            Assert.Equal("1.18.32", r.Version);
            Assert.Equal(2, r.Assets.Count);
            Assert.Equal("1.18.32", await OpenCodeInstaller.GetLatestVersionAsync());
        }
        finally
        {
            OpenCodeReleases.ApiBase = saved;
        }
    }

    [Theory]
    [InlineData("1.18.32\r\n", "1.18.32")]
    [InlineData("v1.0.0-beta.1\n", "1.0.0-beta.1")]
    [InlineData("\u001b[0m1.2.3\n", "1.2.3")]
    [InlineData("error: boom\n", null)]
    public void ParseVersionOutput(string output, string? expected) =>
        Assert.Equal(expected, OpenCodeInstaller.ParseVersionOutput(output));

    [Fact]
    public void ExtractEntry_PrefersRootEntry()
    {
        using var home = new TempHome();
        var zip = Path.Combine(home.Path, "a.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "nested/opencode.exe", "nested");
            Write(archive, "opencode.exe", "root");
        }
        var dest = Path.Combine(home.Path, "out", "opencode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        OpenCodeInstaller.ExtractEntry(zip, "opencode.exe", dest, CancellationToken.None);
        Assert.Equal("root", File.ReadAllText(dest));
        Assert.Throws<InvalidDataException>(() => OpenCodeInstaller.ExtractEntry(zip, "rg.exe", dest, CancellationToken.None));

        static void Write(ZipArchive a, string name, string content)
        {
            using var w = new StreamWriter(a.CreateEntry(name).Open());
            w.Write(content);
        }
    }

    [Fact]
    public void ReplaceExecutable_WorksWhileRunningButFailsWhenFullyLocked()
    {
        using var home = new TempHome();
        var target = Path.Combine(home.Path, "bin", "opencode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "old");

        // Запущенный exe: запись запрещена, переименование разрешено (FILE_SHARE_DELETE).
        var staged = Path.Combine(home.Path, "new1.exe");
        File.WriteAllText(staged, "new1");
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            OpenCodeInstaller.ReplaceExecutable(staged, target);
        Assert.Equal("new1", File.ReadAllText(target));

        var staged2 = Path.Combine(home.Path, "new2.exe");
        File.WriteAllText(staged2, "new2");
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => OpenCodeInstaller.ReplaceExecutable(staged2, target));
            Assert.Contains("Закройте все окна OpenCode", ex.Message);
        }
        Assert.Equal("new1", File.ReadAllText(target));
    }

    [Fact]
    public void FindExecutable_ConfigThenManaged()
    {
        using var home = new TempHome();
        var cfg = new AppConfig();
        var custom = Path.Combine(home.Path, "custom", "opencode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.WriteAllText(custom, "");
        cfg.OpenCode.ExecutablePath = custom;
        Assert.Equal(custom, OpenCodeInstaller.FindExecutable(cfg));

        cfg.OpenCode.ExecutablePath = Path.Combine(home.Path, "missing.exe");
        Directory.CreateDirectory(OpenCodeInstaller.BinDir);
        File.WriteAllText(OpenCodeInstaller.ManagedExePath, "");
        Assert.Equal(OpenCodeInstaller.ManagedExePath, OpenCodeInstaller.FindExecutable(cfg));
        Assert.StartsWith(home.Path, OpenCodeInstaller.ManagedExePath);
    }

    [Fact]
    public void ToStep_FormatsDownloadProgress()
    {
        var s = OpenCodeInstaller.ToStep("OpenCode 1.0.0",
            new DownloadProgress(DownloadStage.Downloading, 10L << 20, 60L << 20, 5L << 20, "x.zip"));
        Assert.Equal("Загрузка OpenCode 1.0.0…", s.Stage);
        Assert.NotNull(s.Fraction);
        Assert.Contains("из", s.Detail);
        Assert.Contains("осталось", s.Detail);
        Assert.StartsWith("Проверка контрольной суммы",
            OpenCodeInstaller.ToStep("x", new DownloadProgress(DownloadStage.Verifying, 0, 1, 0, "x.zip")).Stage);
    }

    [Fact]
    public void RipgrepPath_IsInIsolatedCache()
    {
        using var home = new TempHome();
        Assert.Equal(Path.Combine(AppPaths.OpenCodeDir, "cache", "opencode", "bin", "rg.exe"), OpenCodeInstaller.RipgrepPath);
    }

    [Fact]
    public void ReleaseCache_RoundTrips()
    {
        using var home = new TempHome();
        var rel = OpenCodeReleases.ParseRelease(ReleaseJson);
        new ReleaseCacheFile { FetchedAtUtc = DateTime.UtcNow, Release = rel }.Save();
        var back = ReleaseCacheFile.Load();
        Assert.NotNull(back);
        Assert.Equal(JsonSerializer.Serialize(rel), JsonSerializer.Serialize(back!.Release));
    }
}
