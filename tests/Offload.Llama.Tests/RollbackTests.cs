using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Util;

namespace Offload.Llama.Tests;

/// <summary>
/// Хранение предыдущих сборок, закрепление версии, переключение и откат. Настоящая сборка llama.cpp не нужна:
/// «llama-server.exe» в поддельных архивах — текстовый файл, проверку запуска заменяет <see cref="InstallerImpl.Validator"/>
/// (файл «bad…» не запускается).
/// </summary>
[Collection("AppPaths")]
public sealed class RollbackTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string _savedApi = GitHubReleases.ApiBase;
    private readonly string _savedWeb = GitHubReleases.WebBase;

    /// <summary>Путь файлов релиза на поддельном GitHub: адреса вне …/releases/download/ отбрасываются при разборе.</summary>
    private const string Dl = "/" + LlamaReleaseResolver.Repo + "/releases/download/b0/";
    private readonly Func<string, CancellationToken, Task<string>> _savedValidator = InstallerImpl.Validator;

    public RollbackTests()
    {
        InstallerImpl.Validator = (exe, _) =>
        {
            var text = File.ReadAllText(exe);
            if (text.StartsWith("bad", StringComparison.Ordinal))
                throw new InvalidOperationException("Установленный llama-server не запускается (код 0xC0000005).");
            return Task.FromResult("fake " + text);
        };
    }

    public void Dispose()
    {
        InstallerImpl.Validator = _savedValidator;
        GitHubReleases.ApiBase = _savedApi;
        GitHubReleases.WebBase = _savedWeb;
        _home.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] FakeZip(string exeText)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // DLL VC++ рядом с сервером — чтобы проверка runtime не зависела от машины.
            foreach (var name in new[] { "llama-server.exe", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(name == "llama-server.exe" ? exeText : "dll");
            }
        }
        return ms.ToArray();
    }

    /// <summary>Поддельный GitHub: несколько релизов со сборкой CPU x64; «последний» можно менять.</summary>
    private sealed class FakeGitHub : IAsyncDisposable
    {
        private readonly Dictionary<string, byte[]> _zips = new(StringComparer.Ordinal);
        private readonly FakeHttpServer _server;

        public FakeGitHub()
        {
            _server = new FakeHttpServer(HandleAsync);
            GitHubReleases.ApiBase = _server.BaseUrl;
            GitHubReleases.WebBase = _server.BaseUrl;
        }

        public string Latest { get; set; } = "";

        public IReadOnlyList<FakeRequest> Requests => _server.Requests;

        public void Add(string tag, string exeText = "ok")
        {
            _zips[tag] = FakeZip(exeText + ":" + tag);
            Latest = tag;
        }

        private static string AssetName(string tag) => $"llama-{tag}-bin-win-cpu-x64.zip";

        private object Release(string tag) => new
        {
            tag_name = tag,
            draft = false,
            prerelease = true,
            published_at = "2026-09-22T13:23:34Z",
            assets = new[]
            {
                new
                {
                    name = AssetName(tag),
                    state = "uploaded",
                    size = _zips[tag].LongLength,
                    digest = "sha256:" + Convert.ToHexString(SHA256.HashData(_zips[tag])).ToLowerInvariant(),
                    browser_download_url = $"{_server.BaseUrl}{Dl}{AssetName(tag)}",
                },
            },
        };

        private async Task HandleAsync(FakeRequest req, Stream s, CancellationToken ct)
        {
            if (req.Path.EndsWith("/releases/latest/download/nightly-tag.txt", StringComparison.Ordinal))
            {
                await FakeHttpServer.WriteResponseAsync(s, 200, Latest, "text/plain");
                return;
            }
            if (req.Path.Contains("/releases?per_page=", StringComparison.Ordinal))
            {
                var list = _zips.Keys.OrderByDescending(GitHubReleases.BuildNumber).Select(Release).ToList();
                await FakeHttpServer.WriteResponseAsync(s, 200, JsonSerializer.Serialize(list));
                return;
            }
            var tagIdx = req.Path.IndexOf("/releases/tags/", StringComparison.Ordinal);
            if (tagIdx >= 0)
            {
                var tag = req.Path[(tagIdx + "/releases/tags/".Length)..];
                if (_zips.ContainsKey(tag)) await FakeHttpServer.WriteResponseAsync(s, 200, JsonSerializer.Serialize(Release(tag)));
                else await FakeHttpServer.WriteResponseAsync(s, 404, "{}");
                return;
            }
            if (req.Path.StartsWith(Dl, StringComparison.Ordinal))
            {
                var tag = _zips.Keys.FirstOrDefault(t => AssetName(t) == req.Path[Dl.Length..]);
                if (tag is null) await FakeHttpServer.WriteResponseAsync(s, 404, "{}");
                else await FakeHttpServer.WriteBytesResponseAsync(s, _zips[tag]);
                return;
            }
            await FakeHttpServer.WriteResponseAsync(s, 404, "{}");
        }

        public ValueTask DisposeAsync() => _server.DisposeAsync();
    }

    /// <summary>Установка «последней» сборки; кэш релизов сбрасывается, иначе он час отдаёт прежний тег.</summary>
    private static Task<LlamaInstallResult> InstallLatestAsync()
    {
        if (File.Exists(GitHubReleases.CacheFile)) File.Delete(GitHubReleases.CacheFile);
        return LlamaInstaller.InstallAsync(LlamaBackend.Cpu, null, Ct);
    }

    private static string Dir(string tag, LlamaBackend backend = LlamaBackend.Cpu) =>
        Path.Combine(AppPaths.LlamaDir, InstallerImpl.DirName(tag, backend));

    /// <summary>Полная установка на диске без загрузки (метка + llama-server.exe).</summary>
    private static string MakeBuild(string tag, LlamaBackend backend, DateTime installedAtUtc, string exeText = "ok")
    {
        var dir = Directory.CreateDirectory(Dir(tag, backend)).FullName;
        File.WriteAllText(Path.Combine(dir, "llama-server.exe"), exeText);
        var marker = new { tag, backend, mainAsset = $"llama-{tag}-bin-win-x.zip", runtimeAsset = (string?)null, installedAtUtc };
        File.WriteAllText(Path.Combine(dir, InstallerImpl.MarkerFileName), JsonSerializer.Serialize(marker, Json.Options));
        return dir;
    }

    [Fact]
    public async Task Install_KeepsCurrentAndTwoPrevious_DeletesOlder()
    {
        await using var gh = new FakeGitHub();
        foreach (var tag in new[] { "b100", "b101", "b102", "b103" })
        {
            gh.Add(tag);
            var r = await InstallLatestAsync();
            Assert.Equal(tag, r.Tag);
        }

        Assert.True(Directory.Exists(Dir("b103")));
        Assert.True(Directory.Exists(Dir("b102")), "предыдущая сборка должна сохраниться");
        Assert.True(Directory.Exists(Dir("b101")), "вторая предыдущая сборка должна сохраниться");
        Assert.False(Directory.Exists(Dir("b100")), "третья предыдущая сборка должна быть удалена");

        var builds = LlamaInstaller.ListInstalledBuilds(ConfigStore.Current);
        Assert.Equal(["b103", "b102", "b101"], builds.Select(b => b.Tag));
        Assert.True(builds[0].IsCurrent);
        Assert.False(builds[1].IsCurrent);

        // Цель автоматического отката — сборка, работавшая до обновления.
        Assert.Equal(Dir("b102"), ConfigStore.Current.Llama.PreviousInstallDir);
    }

    [Fact]
    public void Cleanup_KeepsPinnedCurrentRollbackTargetAndTwoNewest_RemovesIncomplete()
    {
        AppPaths.EnsureCreated();
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var pinned = MakeBuild("b90", LlamaBackend.Vulkan, t0);
        var old = MakeBuild("b91", LlamaBackend.Cpu, t0.AddDays(1));
        var prev1 = MakeBuild("b95", LlamaBackend.Cpu, t0.AddDays(2));
        var prev2 = MakeBuild("b96", LlamaBackend.Cuda12, t0.AddDays(3));
        var current = MakeBuild("b97", LlamaBackend.Cpu, t0.AddDays(4));
        var noMarker = Directory.CreateDirectory(Path.Combine(AppPaths.LlamaDir, "b98-cpu")).FullName;
        File.WriteAllText(Path.Combine(noMarker, "llama-server.exe"), "partial");
        var foreign = Directory.CreateDirectory(Path.Combine(AppPaths.LlamaDir, "my-build")).FullName;
        ConfigStore.Update(c =>
        {
            c.Llama.InstallDir = current;
            c.Llama.InstalledTag = "b97";
            c.Llama.PinnedTag = "b90";
        });

        InstallerImpl.CleanupOldVersions(current);

        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(pinned), "закреплённая сборка не удаляется");
        Assert.True(Directory.Exists(prev1));
        Assert.True(Directory.Exists(prev2));
        Assert.False(Directory.Exists(old), "лишняя старая сборка удаляется");
        Assert.False(Directory.Exists(noMarker), "неполная установка удаляется");
        Assert.True(Directory.Exists(foreign), "чужие папки не трогаем");

        var builds = LlamaInstaller.ListInstalledBuilds(ConfigStore.Current);
        Assert.Equal(["b97", "b96", "b95", "b90"], builds.Select(b => b.Tag));
        Assert.True(builds.Single(b => b.Tag == "b90").IsPinned);
        Assert.Equal(LlamaBackend.Vulkan, builds.Single(b => b.Tag == "b90").Backend);
    }

    [Fact]
    public void ListInstalledBuilds_RecognizesNewBackendFolders()
    {
        AppPaths.EnsureCreated();
        var t = DateTime.UtcNow;
        MakeBuild("b11102", LlamaBackend.OpenVino, t);
        MakeBuild("b11102", LlamaBackend.OpenClAdreno, t);
        var builds = LlamaInstaller.ListInstalledBuilds(new AppConfig());
        Assert.Contains(builds, b => b.Backend == LlamaBackend.OpenVino && b.InstallDir.EndsWith("b11102-openvino", StringComparison.Ordinal));
        Assert.Contains(builds, b => b.Backend == LlamaBackend.OpenClAdreno && b.InstallDir.EndsWith("b11102-opencladreno", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pinned_InstallUsesPinnedTag_CheckUpdateOffline()
    {
        await using var gh = new FakeGitHub();
        gh.Add("b150");
        gh.Add("b200");
        LlamaInstaller.SetPinnedTag("b150");

        var r = await InstallLatestAsync();
        Assert.Equal("b150", r.Tag);
        Assert.Equal("b150", ConfigStore.Current.Llama.InstalledTag);

        var before = gh.Requests.Count;
        var upd = await LlamaInstaller.CheckUpdateAsync(ConfigStore.Current, Ct);
        Assert.True(upd.Pinned);
        Assert.False(upd.UpdateAvailable);
        Assert.Equal("b150", upd.LatestTag);
        Assert.Equal(before, gh.Requests.Count);

        LlamaInstaller.SetPinnedTag(null);
        var free = await LlamaInstaller.CheckUpdateAsync(ConfigStore.Current, Ct);
        Assert.False(free.Pinned);
        Assert.True(free.UpdateAvailable);
        Assert.Equal("b200", free.LatestTag);

        Assert.Throws<ArgumentException>(() => LlamaInstaller.SetPinnedTag("latest"));
    }

    [Fact]
    public async Task Pinned_LocalBuild_SwitchesWithoutNetwork()
    {
        await using var gh = new FakeGitHub();
        gh.Add("b300");
        gh.Add("b301");
        await InstallLatestAsync(); // b301
        // Сохранённая предыдущая сборка b300.
        MakeBuild("b300", LlamaBackend.Cpu, DateTime.UtcNow.AddDays(-1));
        Assert.Equal("b301", ConfigStore.Current.Llama.InstalledTag);

        LlamaInstaller.SetPinnedTag("b300");
        var requests = gh.Requests.Count;
        var r = await LlamaInstaller.InstallAsync(LlamaBackend.Cpu, null, Ct);
        Assert.Equal("b300", r.Tag);
        Assert.Equal(Dir("b300"), ConfigStore.Current.Llama.InstallDir);
        Assert.Equal(requests, gh.Requests.Count);
    }

    [Fact]
    public async Task Pinned_TagWithoutBackend_ExplainsError()
    {
        await using var gh = new FakeGitHub();
        gh.Add("b400");
        LlamaInstaller.SetPinnedTag("b400");
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => LlamaInstaller.InstallAsync(LlamaBackend.Vulkan, null, Ct));
        Assert.Contains("b400", ex.Message);
        Assert.Contains("закреп", ex.Message);
    }

    [Fact]
    public async Task Install_NewBuildFailsValidation_PreviousStays_FailedTagNotOffered()
    {
        await using var gh = new FakeGitHub();
        gh.Add("b500");
        await InstallLatestAsync();
        gh.Add("b501", exeText: "bad");

        var ex = await Assert.ThrowsAsync<LlamaInstallRolledBackException>(() => InstallLatestAsync());
        Assert.Null(ex.SwitchedTo);
        Assert.Contains("b501", ex.Message);
        Assert.Contains("b500", ex.Message);
        Assert.NotNull(ex.InnerException);

        var cfg = ConfigStore.Current;
        Assert.Equal("b500", cfg.Llama.InstalledTag);
        Assert.Equal(Dir("b500"), cfg.Llama.InstallDir);
        Assert.Equal("b501", cfg.Llama.FailedTag);
        Assert.False(Directory.Exists(Dir("b501")), "сборка, не прошедшая проверку, удаляется");

        // Обновление до неудачной сборки больше не предлагается.
        if (File.Exists(GitHubReleases.CacheFile)) File.Delete(GitHubReleases.CacheFile);
        var upd = await LlamaInstaller.CheckUpdateAsync(cfg, Ct);
        Assert.False(upd.UpdateAvailable);
        Assert.Equal("b501", upd.LatestTag);
    }

    [Fact]
    public async Task Install_NewBuildFailsValidation_CurrentMissing_RollsBackToKeptBuild()
    {
        await using var gh = new FakeGitHub();
        AppPaths.EnsureCreated();
        var kept = MakeBuild("b600", LlamaBackend.Cpu, DateTime.UtcNow.AddDays(-2));
        gh.Add("b601", exeText: "bad");

        var ex = await Assert.ThrowsAsync<LlamaInstallRolledBackException>(() => InstallLatestAsync());
        Assert.NotNull(ex.SwitchedTo);
        Assert.Equal("b600", ex.SwitchedTo.Tag);
        Assert.Equal(kept, ConfigStore.Current.Llama.InstallDir);
        Assert.Equal("b600", ConfigStore.Current.Llama.InstalledTag);
    }

    [Fact]
    public async Task Install_FailsValidation_NothingToRollBackTo_OriginalError()
    {
        await using var gh = new FakeGitHub();
        gh.Add("b650", exeText: "bad");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InstallLatestAsync());
        Assert.Contains("не запускается", ex.Message);
        Assert.Null(ConfigStore.Current.Llama.InstalledTag);
    }

    [Fact]
    public async Task FirstStartFails_RollsBack_ThenConfirmedByNextStart()
    {
        await using var gh = new FakeGitHub();
        gh.Add("b700");
        await InstallLatestAsync();
        // Первый запуск со сборкой без прежней — откатываться некуда.
        Assert.Null(ConfigStore.Current.Llama.PreviousInstallDir);
        Assert.Null(await LlamaInstaller.ReportServerStartAsync(false, Ct));
        Assert.Equal("b700", ConfigStore.Current.Llama.InstalledTag);

        gh.Add("b701");
        await InstallLatestAsync();
        Assert.Equal(Dir("b700"), ConfigStore.Current.Llama.PreviousInstallDir);

        var back = await LlamaInstaller.ReportServerStartAsync(false, Ct);
        Assert.NotNull(back);
        Assert.Equal("b700", back.Tag);
        var l = ConfigStore.Current.Llama;
        Assert.Equal("b700", l.InstalledTag);
        Assert.Equal(Dir("b700"), l.InstallDir);
        Assert.Equal("b701", l.FailedTag);
        Assert.Equal(Dir("b701"), l.RolledBackFromDir);
        Assert.Null(l.PreviousInstallDir);
        Assert.True(Directory.Exists(Dir("b701")), "новая сборка остаётся на диске — на неё можно вернуться вручную");

        // Прежняя сборка запустилась — откат подтверждён.
        Assert.Null(await LlamaInstaller.ReportServerStartAsync(true, Ct));
        l = ConfigStore.Current.Llama;
        Assert.Null(l.RolledBackFromDir);
        Assert.Equal("b700", l.InstalledTag);
        Assert.Equal("b701", l.FailedTag);

        // Дальше отчёты о запуске ничего не меняют.
        Assert.Null(await LlamaInstaller.ReportServerStartAsync(false, Ct));
        Assert.Equal("b700", ConfigStore.Current.Llama.InstalledTag);
    }

    [Fact]
    public async Task FirstStartFails_RollbackAlsoFails_ReturnsToNewBuild()
    {
        await using var gh = new FakeGitHub();
        gh.Add("b800");
        await InstallLatestAsync();
        gh.Add("b801");
        await InstallLatestAsync();

        Assert.NotNull(await LlamaInstaller.ReportServerStartAsync(false, Ct));
        Assert.Equal("b800", ConfigStore.Current.Llama.InstalledTag);

        // И с прежней сборкой сервер не запустился — дело не в сборке: возвращаем новую, повтор не нужен.
        Assert.Null(await LlamaInstaller.ReportServerStartAsync(false, Ct));
        var l = ConfigStore.Current.Llama;
        Assert.Equal("b801", l.InstalledTag);
        Assert.Null(l.FailedTag);
        Assert.Null(l.RolledBackFromDir);
        Assert.Null(l.PreviousInstallDir);
    }

    [Fact]
    public async Task SuccessfulFirstStart_ClearsRollbackTarget()
    {
        await using var gh = new FakeGitHub();
        gh.Add("b900");
        await InstallLatestAsync();
        gh.Add("b901");
        await InstallLatestAsync();

        Assert.Null(await LlamaInstaller.ReportServerStartAsync(true, Ct));
        Assert.Null(ConfigStore.Current.Llama.PreviousInstallDir);
        Assert.Null(await LlamaInstaller.ReportServerStartAsync(false, Ct));
        Assert.Equal("b901", ConfigStore.Current.Llama.InstalledTag);
    }

    [Fact]
    public async Task SwitchTo_KeptBuild_UpdatesConfig_UnknownOrBrokenRejected()
    {
        AppPaths.EnsureCreated();
        var cpu = MakeBuild("b50", LlamaBackend.Cpu, DateTime.UtcNow.AddDays(-1));
        var vulkan = MakeBuild("b51", LlamaBackend.Vulkan, DateTime.UtcNow);
        var broken = MakeBuild("b52", LlamaBackend.Cpu, DateTime.UtcNow, exeText: "bad");

        var r = await LlamaInstaller.SwitchToAsync(vulkan, Ct);
        Assert.Equal("b51", r.Tag);
        var l = ConfigStore.Current.Llama;
        Assert.Equal(vulkan, l.InstallDir);
        Assert.Equal(LlamaBackend.Vulkan, l.InstalledBackend);
        Assert.Equal(LlamaBackend.Vulkan, l.Backend);

        await LlamaInstaller.SwitchToAsync(cpu, Ct);
        Assert.Equal("b50", ConfigStore.Current.Llama.InstalledTag);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LlamaInstaller.SwitchToAsync(Path.Combine(AppPaths.LlamaDir, "b1-cpu"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LlamaInstaller.SwitchToAsync(broken, Ct));
        Assert.Equal("b50", ConfigStore.Current.Llama.InstalledTag);
    }
}
