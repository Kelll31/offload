using System.Text.Json;
using Offload.Core.Config;

namespace Offload.Llama.Tests;

public sealed class ReleaseSelectionTests
{
    internal static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    internal static LlamaRelease Load(string tag)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath(tag + ".json")));
        return GitHubReleases.ParseRelease(doc.RootElement);
    }

    [Fact]
    public void Parse_b11102_ReadsAssetsAndDigest()
    {
        var r = Load("b11102");
        Assert.Equal("b11102", r.Tag);
        Assert.Equal(new DateTime(2026, 9, 22, 13, 23, 34, DateTimeKind.Utc), r.PublishedAtUtc);
        Assert.Equal(35, r.Assets.Count);
        var cuda = r.Assets.Single(a => a.Name == "llama-b11102-bin-win-cuda-13.4-x64.zip");
        Assert.Equal(149_703_743, cuda.Size);
        Assert.Equal("6e03fa9eaf828c0d7e8a55ad33cb0b6f74429f123b794f1eccedd94e797b4b27", cuda.Sha256);
        Assert.Equal("https://github.com/ggml-org/llama.cpp/releases/download/b11102/llama-b11102-bin-win-cuda-13.4-x64.zip", cuda.DownloadUrl);
    }

    [Theory]
    [InlineData(LlamaBackend.Cuda12, false, "llama-b11102-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-12.4-x64.zip")]
    [InlineData(LlamaBackend.Cuda13, false, "llama-b11102-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-x64.zip")]
    [InlineData(LlamaBackend.Cuda13, true, "llama-b11102-bin-win-cuda-13.4-arm64.zip", "cudart-llama-bin-win-cuda-13.4-arm64.zip")]
    [InlineData(LlamaBackend.Vulkan, false, "llama-b11102-bin-win-vulkan-x64.zip", null)]
    [InlineData(LlamaBackend.Cpu, false, "llama-b11102-bin-win-cpu-x64.zip", null)]
    [InlineData(LlamaBackend.Cpu, true, "llama-b11102-bin-win-cpu-arm64.zip", null)]
    [InlineData(LlamaBackend.Rocm, false, "llama-b11102-bin-win-rocm-10.0-x64.zip", null)]
    [InlineData(LlamaBackend.Sycl, false, "llama-b11102-bin-win-sycl-x64.zip", null)]
    public void Select_b11102(LlamaBackend backend, bool arm64, string main, string? runtime)
    {
        var sel = LlamaReleaseResolver.Select(Load("b11102"), backend, arm64);
        Assert.NotNull(sel);
        Assert.Equal(backend, sel.Backend);
        Assert.Equal(main, sel.Main.Name);
        Assert.Equal(runtime, sel.CudaRuntime?.Name);
        Assert.NotNull(sel.Main.Sha256);
    }

    [Theory]
    [InlineData(LlamaBackend.Cuda12, true)]
    [InlineData(LlamaBackend.Vulkan, true)]
    [InlineData(LlamaBackend.Rocm, true)]
    [InlineData(LlamaBackend.Sycl, true)]
    public void Select_MissingArm64Builds_ReturnNull(LlamaBackend backend, bool arm64) =>
        Assert.Null(LlamaReleaseResolver.Select(Load("b11102"), backend, arm64));

    [Fact]
    public void Select_b10000_LabelDrift_Cuda133_HipRadeon()
    {
        var r = Load("b10000");
        var cuda13 = LlamaReleaseResolver.Select(r, LlamaBackend.Cuda13)!;
        Assert.Equal("llama-b10000-bin-win-cuda-13.3-x64.zip", cuda13.Main.Name);
        Assert.Equal("cudart-llama-bin-win-cuda-13.3-x64.zip", cuda13.CudaRuntime!.Name);
        var cuda12 = LlamaReleaseResolver.Select(r, LlamaBackend.Cuda12)!;
        Assert.Equal("llama-b10000-bin-win-cuda-12.4-x64.zip", cuda12.Main.Name);
        Assert.Equal("cudart-llama-bin-win-cuda-12.4-x64.zip", cuda12.CudaRuntime!.Name);
        Assert.Equal("llama-b10000-bin-win-hip-radeon-x64.zip", LlamaReleaseResolver.Select(r, LlamaBackend.Rocm)!.Main.Name);
        Assert.Equal("llama-b10000-bin-win-vulkan-x64.zip", LlamaReleaseResolver.Select(r, LlamaBackend.Vulkan)!.Main.Name);
    }

    [Fact]
    public void Select_b6500_NoCuda13()
    {
        var r = Load("b6500");
        Assert.Null(LlamaReleaseResolver.Select(r, LlamaBackend.Cuda13));
        Assert.Equal("llama-b6500-bin-win-cuda-12.4-x64.zip", LlamaReleaseResolver.Select(r, LlamaBackend.Cuda12)!.Main.Name);
        Assert.Equal("llama-b6500-bin-win-hip-radeon-x64.zip", LlamaReleaseResolver.Select(r, LlamaBackend.Rocm)!.Main.Name);
        Assert.Equal("llama-b6500-bin-win-cpu-arm64.zip", LlamaReleaseResolver.Select(r, LlamaBackend.Cpu, arm64: true)!.Main.Name);
    }

    [Fact]
    public void Select_CudaWithoutMatchingRuntime_ReturnsNull()
    {
        var r = new LlamaRelease("b20000", DateTime.UtcNow,
        [
            new("llama-b20000-bin-win-cuda-13.4-x64.zip", "u", 1, null),
            new("cudart-llama-bin-win-cuda-13.3-x64.zip", "u", 1, null),
        ]);
        Assert.Null(LlamaReleaseResolver.Select(r, LlamaBackend.Cuda13));

        var newerRuntime = r with
        {
            Assets = [new("llama-b20000-bin-win-cuda-13.4-x64.zip", "u", 1, null), new("cudart-llama-bin-win-cuda-13.5-x64.zip", "u", 1, null)],
        };
        Assert.Equal("cudart-llama-bin-win-cuda-13.5-x64.zip", LlamaReleaseResolver.Select(newerRuntime, LlamaBackend.Cuda13)!.CudaRuntime!.Name);
    }

    [Fact]
    public void Select_PicksNewestCudaMinor()
    {
        var r = new LlamaRelease("b20000", DateTime.UtcNow,
        [
            new("llama-b20000-bin-win-cuda-13.4-x64.zip", "u", 1, null),
            new("llama-b20000-bin-win-cuda-13.10-x64.zip", "u", 1, null),
            new("cudart-llama-bin-win-cuda-13.4-x64.zip", "u", 1, null),
            new("cudart-llama-bin-win-cuda-13.10-x64.zip", "u", 1, null),
        ]);
        var sel = LlamaReleaseResolver.Select(r, LlamaBackend.Cuda13)!;
        Assert.Equal("llama-b20000-bin-win-cuda-13.10-x64.zip", sel.Main.Name);
        Assert.Equal("cudart-llama-bin-win-cuda-13.10-x64.zip", sel.CudaRuntime!.Name);
    }

    [Fact]
    public void Select_Auto_Throws() =>
        Assert.Throws<ArgumentException>(() => LlamaReleaseResolver.Select(Load("b11102"), LlamaBackend.Auto));

    [Theory]
    [InlineData("sha256:6E03FA9EAF828C0D7E8A55AD33CB0B6F74429F123B794F1ECCEDD94E797B4B27", "6e03fa9eaf828c0d7e8a55ad33cb0b6f74429f123b794f1eccedd94e797b4b27")]
    [InlineData("sha512:abc", null)]
    [InlineData("sha256:xyz", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ParseDigest(string? digest, string? expected) => Assert.Equal(expected, GitHubReleases.ParseDigest(digest));

    [Fact]
    public void ParseReleaseList_SkipsDraftsAndSemverTags()
    {
        var json = """
        [
          {"tag_name":"v0.4.1","draft":false,"assets":[{"name":"nightly-tag.txt","browser_download_url":"u","size":7}]},
          {"tag_name":"b11103","draft":true,"assets":[]},
          {"tag_name":"b11102","draft":false,"prerelease":true,"published_at":"2026-09-22T13:23:34Z",
           "assets":[{"name":"llama-b11102-bin-win-cpu-x64.zip","browser_download_url":"u","size":5,"state":"uploaded"},
                     {"name":"llama-b11102-bin-win-vulkan-x64.zip","browser_download_url":"u","size":5,"state":"starter"}]}
        ]
        """;
        var list = GitHubReleases.ParseReleaseList(json);
        var r = Assert.Single(list);
        Assert.Equal("b11102", r.Tag);
        // Незагруженный (state=starter) архив пропускается.
        Assert.Equal("llama-b11102-bin-win-cpu-x64.zip", Assert.Single(r.Assets).Name);
    }

    [Fact]
    public void DisplayNames_AreRussian()
    {
        Assert.Equal("Только процессор", LlamaReleaseResolver.DisplayName(LlamaBackend.Cpu));
        Assert.Equal("Vulkan (любая видеокарта)", LlamaReleaseResolver.DisplayName(LlamaBackend.Vulkan));
        Assert.StartsWith("NVIDIA CUDA 12", LlamaReleaseResolver.DisplayName(LlamaBackend.Cuda12));
        Assert.StartsWith("NVIDIA CUDA 13", LlamaReleaseResolver.DisplayName(LlamaBackend.Cuda13));
        foreach (var b in Enum.GetValues<LlamaBackend>())
            Assert.False(string.IsNullOrWhiteSpace(LlamaReleaseResolver.DisplayName(b)));
    }
}

/// <summary>GetLatestAsync против поддельного GitHub: стабильный путь, запасной путь, кэш, работа без сети.</summary>
[Collection("AppPaths")]
public sealed class GitHubReleasesTests : IDisposable
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

    private static string Fixture(string tag) => File.ReadAllText(ReleaseSelectionTests.FixturePath(tag + ".json"));

    private static Func<FakeRequest, Stream, CancellationToken, Task> GitHub(string stableTag, Func<string>? list = null) =>
        async (req, s, _) =>
        {
            if (req.Path.EndsWith("/releases/latest/download/nightly-tag.txt"))
                await FakeHttpServer.WriteResponseAsync(s, 200, stableTag + "\n", "text/plain");
            else if (req.Path.Contains("/releases/tags/"))
            {
                var tag = req.Path[(req.Path.LastIndexOf('/') + 1)..];
                var path = ReleaseSelectionTests.FixturePath(tag + ".json");
                if (File.Exists(path)) await FakeHttpServer.WriteResponseAsync(s, 200, File.ReadAllText(path));
                else await FakeHttpServer.WriteResponseAsync(s, 404, "{\"message\":\"Not Found\"}");
            }
            else if (req.Path.Contains("/releases?per_page="))
                await FakeHttpServer.WriteResponseAsync(s, 200, list?.Invoke() ?? "[]");
            else
                await FakeHttpServer.WriteResponseAsync(s, 404, "{}");
        };

    private static void Point(FakeHttpServer server)
    {
        GitHubReleases.ApiBase = server.BaseUrl;
        GitHubReleases.WebBase = server.BaseUrl;
    }

    [Fact]
    public async Task StablePath_UsesNightlyTagAndCaches()
    {
        await using var server = new FakeHttpServer(GitHub("b10000"));
        Point(server);

        var r = await LlamaReleaseResolver.GetLatestAsync();
        Assert.Equal("b10000", r.Tag);
        Assert.True(File.Exists(GitHubReleases.CacheFile));
        Assert.DoesNotContain(server.Requests, q => q.Path.Contains("per_page"));

        // Повторный вызов в пределах часа — из кэша, без обращений к серверу.
        var count = server.Requests.Count;
        var again = await LlamaReleaseResolver.GetLatestAsync();
        Assert.Equal("b10000", again.Tag);
        Assert.Equal(count, server.Requests.Count);
    }

    [Fact]
    public async Task StableWithoutWantedBuild_FallsBackToNightlyList()
    {
        // В b6500 нет CUDA 13 — ищем в списке последних релизов.
        var list = "[" + Fixture("b11102") + "," + Fixture("b10000") + "]";
        await using var server = new FakeHttpServer(GitHub("b6500", () => list));
        Point(server);

        var r = await GitHubReleases.GetLatestAsync(x => AssetSelector.Select(x, LlamaBackend.Cuda13, false) is not null, "CUDA 13", default);
        Assert.Equal("b11102", r.Tag);
        Assert.Contains(server.Requests, q => q.Path.Contains("per_page=10"));
    }

    [Fact]
    public async Task Offline_UsesStaleCache()
    {
        await using (var server = new FakeHttpServer(GitHub("b11102")))
        {
            Point(server);
            Assert.Equal("b11102", (await LlamaReleaseResolver.GetLatestAsync()).Tag);
        }
        // Кэш устарел, а сети нет (порт закрыт).
        var cache = ReleaseCache.Load()!;
        cache.FetchedAtUtc = DateTime.UtcNow.AddDays(-2);
        cache.Save();
        GitHubReleases.ApiBase = GitHubReleases.WebBase = "http://127.0.0.1:1";

        var r = await LlamaReleaseResolver.GetLatestAsync();
        Assert.Equal("b11102", r.Tag);
    }

    [Fact]
    public async Task NoBuildAnywhere_ThrowsRussianNotFound()
    {
        await using var server = new FakeHttpServer(GitHub("b6500", () => "[" + Fixture("b6500") + "]"));
        Point(server);
        var ex = await Assert.ThrowsAsync<LlamaBuildNotFoundException>(() =>
            GitHubReleases.GetLatestAsync(x => AssetSelector.Select(x, LlamaBackend.Cuda13, false) is not null, "NVIDIA CUDA 13", default));
        Assert.Contains("нет сборки", ex.Message);
    }

    [Fact]
    public async Task Offline_NoCache_ThrowsRussian()
    {
        GitHubReleases.ApiBase = GitHubReleases.WebBase = "http://127.0.0.1:1";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LlamaReleaseResolver.GetLatestAsync());
        Assert.Contains("Не удалось получить сведения о релизах llama.cpp", ex.Message);
    }

    [Fact]
    public async Task RateLimit_MessageIsRussian()
    {
        await using var server = new FakeHttpServer(async (req, s, _) =>
        {
            if (req.Path.EndsWith("nightly-tag.txt")) await FakeHttpServer.WriteResponseAsync(s, 200, "b11102", "text/plain");
            else
                await FakeHttpServer.WriteResponseAsync(s, 403, "{\"message\":\"API rate limit exceeded\"}", headers: new Dictionary<string, string>
                {
                    ["X-RateLimit-Remaining"] = "0",
                    ["X-RateLimit-Reset"] = "1790000000",
                });
        });
        Point(server);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LlamaReleaseResolver.GetLatestAsync());
        Assert.Contains("лимит запросов к GitHub API", ex.Message);
    }
}
