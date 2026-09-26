using Offload.Core.Net;
using Offload.Core.Update;

namespace Offload.Core.Tests;

public sealed class AppReleasesParsingTests
{
    [Theory]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("1.3", "1.2.9", 1)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3", "1.2.3-beta", 1)]
    [InlineData("1.2.3-beta", "1.2.3", -1)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.1", 1)]
    [InlineData("1.2.3+abc", "1.2.3", 0)]
    [InlineData("0.9.0", "1.0.0", -1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    public void CompareVersions_OrdersSemver(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(AppReleases.CompareVersions(a, b)));
    }

    [Theory]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("V2.0.1", "2.0.1")]
    [InlineData("1.0.0-rc1", "1.0.0-rc1")]
    [InlineData("latest", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeVersion_StripsPrefix(string? tag, string? expected)
    {
        Assert.Equal(expected, AppReleases.NormalizeVersion(tag));
    }

    [Theory]
    [InlineData("1.0/../../x")]
    [InlineData("v1.0/../../x")]
    [InlineData("1.0\\..\\x")]
    [InlineData("1.0&calc")]
    [InlineData("1.0\"x")]
    [InlineData("1.0%PATH%")]
    [InlineData("1.0!x!")]
    [InlineData("1.0 x")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+build")]
    [InlineData("１.２.３")]
    [InlineData("1.2.3-rc_1")]
    public void NormalizeVersion_RejectsUnsafeTags(string tag)
    {
        Assert.Null(AppReleases.NormalizeVersion(tag));
        Assert.Null(AppReleases.ParseRelease($$"""{"tag_name":{{System.Text.Json.JsonSerializer.Serialize(tag)}}}"""));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-beta.2")]
    public void IsValidVersion_AcceptsReleaseVersions(string version)
    {
        Assert.True(AppReleases.IsValidVersion(version));
        Assert.Equal(version, AppReleases.NormalizeVersion("v" + version));
    }

    [Fact]
    public void ParseChecksums_SupportsSha256sumFormats()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var c = new string('c', 64);
        var text = "﻿" + $"{a}  Offload.exe\r\n{b} *Offload-Setup-1.2.0.exe\n# комментарий\nnot-a-hash  x.exe\n{c}  artifacts/Offload/sub.exe\n\n";
        var map = AppReleases.ParseChecksums(text);
        Assert.Equal(3, map.Count);
        Assert.Equal(a, map["Offload.exe"]);
        Assert.Equal(b.ToLowerInvariant(), map["offload-setup-1.2.0.exe"]);
        Assert.Equal(c, map["sub.exe"]);
    }

    [Fact]
    public void ParseRelease_SkipsDraftsAndPrereleases_KeepsUploadedAssets()
    {
        Assert.Null(AppReleases.ParseRelease("""{"tag_name":"v1.0.0","draft":true}"""));
        Assert.Null(AppReleases.ParseRelease("""{"tag_name":"v1.0.0","prerelease":true}"""));
        Assert.Null(AppReleases.ParseRelease("""{"tag_name":"nightly"}"""));

        var r = AppReleases.ParseRelease("""
            {"tag_name":"v1.5.0","published_at":"2026-09-01T10:00:00Z","html_url":"https://github.com/Kelll31/offload/releases/tag/v1.5.0",
             "assets":[
               {"name":"Offload.exe","browser_download_url":"https://github.com/Kelll31/offload/releases/download/v1.5.0/Offload.exe","size":100,"state":"uploaded"},
               {"name":"Offload-Setup-1.5.0.exe","browser_download_url":"https://github.com/Kelll31/offload/releases/download/v1.5.0/Offload-Setup-1.5.0.exe","size":200,"state":"starter"},
               {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/Kelll31/offload/releases/download/v1.5.0/SHA256SUMS.txt"}
             ]}
            """);
        Assert.NotNull(r);
        Assert.Equal("1.5.0", r.Version);
        Assert.Equal("v1.5.0", r.Tag);
        Assert.Equal(2, r.Assets.Count);
        Assert.NotNull(r.Asset("offload.exe"));
        Assert.Null(r.Asset("Offload-Setup-1.5.0.exe"));
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), r.PublishedUtc);
    }
}

[Collection("AppPaths")]
public sealed class AppReleasesNetworkTests
{
    private static string ReleaseJson(string baseUrl, string version, bool withSums = true) => $$"""
        {"tag_name":"v{{version}}","html_url":"{{baseUrl}}/page","assets":[
          {"name":"Offload.exe","browser_download_url":"{{baseUrl}}/Kelll31/offload/releases/download/vtest/Offload.exe","size":5,"state":"uploaded"}
          {{(withSums ? $",{{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"{baseUrl}/Kelll31/offload/releases/download/vtest/SHA256SUMS.txt\",\"size\":10}}" : "")}}
        ]}
        """;

    [Fact]
    public async Task GetLatest_CachesForCheckInterval_ForceRefreshes()
    {
        using var home = new TempHome();
        await using var http = new FakeHttp();
        http.Map("/repos/Kelll31/offload/releases/latest", ReleaseJson(http.BaseUrl, "9.1.0"));
        var saved = AppReleases.ApiBase;
        AppReleases.ApiBase = http.BaseUrl;
        AppReleases.WebBase = http.BaseUrl;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var first = await AppReleases.GetLatestAsync(force: false, ct);
            Assert.Equal("9.1.0", first?.Version);
            var second = await AppReleases.GetLatestAsync(force: false, ct);
            Assert.Equal("9.1.0", second?.Version);
            Assert.True(http.Requests.Count == 1, "повторная проверка в течение 12 ч должна взять ответ из кэша");

            http.Map("/repos/Kelll31/offload/releases/latest", ReleaseJson(http.BaseUrl, "9.2.0"));
            var forced = await AppReleases.GetLatestAsync(force: true, ct);
            Assert.Equal("9.2.0", forced?.Version);
            Assert.Equal(2, http.Requests.Count);
        }
        finally
        {
            AppReleases.ApiBase = saved;
            AppReleases.WebBase = "https://github.com";
        }
    }

    [Fact]
    public async Task GetLatest_TamperedCachedVersion_Ignored()
    {
        using var home = new TempHome();
        await using var http = new FakeHttp();
        http.Map("/repos/Kelll31/offload/releases/latest", ReleaseJson(http.BaseUrl, "9.1.0"));
        var saved = AppReleases.ApiBase;
        AppReleases.ApiBase = http.BaseUrl;
        AppReleases.WebBase = http.BaseUrl;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal("9.1.0", (await AppReleases.GetLatestAsync(force: false, ct))?.Version);
            // Версия в кэше подменена (тег прежний): кэш не используется, сведения берутся из сети заново.
            var text = File.ReadAllText(AppReleases.CacheFile);
            Assert.Contains("\"9.1.0\"", text, StringComparison.Ordinal);
            File.WriteAllText(AppReleases.CacheFile, text.Replace("\"9.1.0\"", "\"9.1.0/../../x\"", StringComparison.Ordinal));
            http.Map("/repos/Kelll31/offload/releases/latest", ReleaseJson(http.BaseUrl, "9.2.0"));
            Assert.Equal("9.2.0", (await AppReleases.GetLatestAsync(force: false, ct))?.Version);
            Assert.Equal(2, http.Requests.Count);
        }
        finally
        {
            AppReleases.ApiBase = saved;
            AppReleases.WebBase = "https://github.com";
        }
    }

    [Fact]
    public async Task GetLatest_NetworkErrorWithCache_ReturnsCached_NoReleases_ReturnsNull()
    {
        using var home = new TempHome();
        var saved = AppReleases.ApiBase;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await using (var http = new FakeHttp())
            {
                AppReleases.ApiBase = http.BaseUrl;
                AppReleases.WebBase = http.BaseUrl;
                // 404 — релизов ещё нет.
                Assert.Null(await AppReleases.GetLatestAsync(force: true, ct));
                http.Map("/repos/Kelll31/offload/releases/latest", ReleaseJson(http.BaseUrl, "3.0.0"));
                Assert.Equal("3.0.0", (await AppReleases.GetLatestAsync(force: true, ct))?.Version);
                // Сервер отвечает ошибкой — берётся сохранённый ответ.
                http.Map("/repos/Kelll31/offload/releases/latest", "oops", 500);
                Assert.Equal("3.0.0", (await AppReleases.GetLatestAsync(force: true, ct))?.Version);
            }
        }
        finally
        {
            AppReleases.ApiBase = saved;
            AppReleases.WebBase = "https://github.com";
        }
    }

    [Fact]
    public async Task GetSha256_FromSumsFile_MissingSumsOrEntry_Refused()
    {
        Assert.SkipWhen(DownloadPolicy.AllowUnverified, "задан OFFLOAD_ALLOW_UNVERIFIED");
        using var home = new TempHome();
        await using var http = new FakeHttp();
        var ct = TestContext.Current.CancellationToken;
        var hash = new string('d', 64);
        http.Map("/Kelll31/offload/releases/download/vtest/SHA256SUMS.txt", $"{hash}  Offload.exe\n{new string('e', 64)}  Offload-Setup-9.1.0.exe\n");

        AppReleases.WebBase = http.BaseUrl;
        try
        {
            var release = AppReleases.ParseRelease(ReleaseJson(http.BaseUrl, "9.1.0"))!;
            Assert.Equal(hash, await AppReleases.GetSha256Async(release, "Offload.exe", ct));
            await Assert.ThrowsAsync<DownloadException>(() => AppReleases.GetSha256Async(release, "Other.exe", ct));

            var noSums = AppReleases.ParseRelease(ReleaseJson(http.BaseUrl, "9.1.0", withSums: false))!;
            var ex = await Assert.ThrowsAsync<DownloadException>(() => AppReleases.GetSha256Async(noSums, "Offload.exe", ct));
            Assert.Contains("SHA256SUMS.txt", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            AppReleases.WebBase = "https://github.com";
        }
    }

    [Theory]
    [InlineData("https://evil.example/Kelll31/offload/releases/download/v9/Offload.exe")]
    [InlineData("http://github.com/Kelll31/offload/releases/download/v9/Offload.exe")]
    [InlineData("https://github.com/Other/offload/releases/download/v9/Offload.exe")]
    [InlineData("https://github.com/Kelll31/offload/raw/main/Offload.exe")]
    public void ParseRelease_ForeignAssetUrl_Dropped(string url)
    {
        // Ответ API (или зеркала API) не выбирает, откуда качать обновление.
        var r = AppReleases.ParseRelease($$"""{"tag_name":"v9.0.0","assets":[{"name":"Offload.exe","browser_download_url":"{{url}}","size":1,"state":"uploaded"}]}""");
        Assert.NotNull(r);
        Assert.Null(r.Asset("Offload.exe"));
    }
}
