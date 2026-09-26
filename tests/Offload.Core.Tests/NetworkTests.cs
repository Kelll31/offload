using System.Net;
using Offload.Core.Config;
using Offload.Core.Net;
using Offload.Core.Security;

namespace Offload.Core.Tests;

public sealed class DpapiTests
{
    [Fact]
    public void ProtectUnprotect_RoundTrip()
    {
        var secret = "hf_" + Guid.NewGuid().ToString("N") + "ёж";
        var blob = Dpapi.Protect(secret);
        Assert.DoesNotContain(secret, blob);
        Assert.NotEqual(secret, blob);
        Assert.Equal(secret, Dpapi.Unprotect(blob));
    }

    [Fact]
    public void Unprotect_GarbageOrEmpty_ReturnsNull()
    {
        Assert.Null(Dpapi.Unprotect(null));
        Assert.Null(Dpapi.Unprotect(""));
        Assert.Null(Dpapi.Unprotect("это не base64"));
        Assert.Null(Dpapi.Unprotect(Convert.ToBase64String(new byte[64])));
    }
}

/// <summary>Сетевые настройки: токен Hugging Face только хабу, зеркала GitHub, прокси, проверка адресов.</summary>
[Collection("AppPaths")]
public sealed class NetworkOptionsTests
{
    private const string Token = "hf_test_token";

    private static bool EnvClean =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NetworkOptions.HfTokenEnvVar)) &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NetworkOptions.HfEndpointEnvVar));

    private static void Configure(Action<NetworkSettings> change)
    {
        ConfigStore.Reload();
        ConfigStore.Update(c => change(c.Network ??= new NetworkSettings()));
    }

    [Fact]
    public void AuthorizationFor_TokenGoesOnlyToOfficialHubOverHttps()
    {
        Assert.SkipUnless(EnvClean, "заданы HF_TOKEN/HF_ENDPOINT — тест проверяет настройки");
        using var home = new TempHome();
        NetworkOptions.HfEndpointOverride = null;
        Configure(n => n.HfTokenProtected = Dpapi.Protect(Token));

        Assert.Equal("https://huggingface.co", NetworkOptions.HfEndpoint);
        var auth = NetworkOptions.AuthorizationFor(new Uri("https://huggingface.co/api/models/a/b/tree/main"));
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(Token, auth.Parameter);

        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("https://evil.example/huggingface.co")));
        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("http://huggingface.co/a")));
        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("https://cdn-lfs.huggingface.co/a")));
        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("https://huggingface.co:8443/a")));
        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("http://127.0.0.1:8765/v1/models")));
    }

    [Fact]
    public void AuthorizationFor_NoToken_ReturnsNull()
    {
        Assert.SkipUnless(EnvClean, "заданы HF_TOKEN/HF_ENDPOINT — тест проверяет настройки");
        using var home = new TempHome();
        NetworkOptions.HfEndpointOverride = null;
        ConfigStore.Reload();
        Assert.Null(NetworkOptions.HfToken);
        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("https://huggingface.co/x")));
    }

    [Fact]
    public void AuthorizationFor_Mirror_OnlyWhenAllowed()
    {
        Assert.SkipUnless(EnvClean, "заданы HF_TOKEN/HF_ENDPOINT — тест проверяет настройки");
        using var home = new TempHome();
        NetworkOptions.HfEndpointOverride = null;
        Configure(n =>
        {
            n.HfTokenProtected = Dpapi.Protect(Token);
            n.HfMirror = "https://hf-mirror.example/";
        });
        Assert.Equal("https://hf-mirror.example", NetworkOptions.HfEndpoint);
        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("https://hf-mirror.example/a/b/resolve/main/x.gguf")));
        // Хаб теперь — зеркало: официальному адресу токен тоже не уходит (запросов к нему нет).
        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("https://huggingface.co/a")));

        Configure(n => n.SendHfTokenToMirror = true);
        Assert.Equal(Token, NetworkOptions.AuthorizationFor(new Uri("https://hf-mirror.example/a/b/resolve/main/x.gguf"))?.Parameter);

        // Зеркало по http:// — токен не отправляется даже с разрешением.
        Configure(n => n.HfMirror = "http://hf-mirror.example");
        Assert.Null(NetworkOptions.AuthorizationFor(new Uri("http://hf-mirror.example/a")));
    }

    [Fact]
    public async Task HttpClient_AddsTokenForHub_AndDropsItOnRedirect()
    {
        Assert.SkipUnless(EnvClean, "заданы HF_TOKEN/HF_ENDPOINT — тест проверяет настройки");
        using var home = new TempHome();
        using var hub = new RangeServer("data"u8.ToArray());
        using var other = new RangeServer("data"u8.ToArray());
        Configure(n => n.HfTokenProtected = Dpapi.Protect(Token));
        NetworkOptions.HfEndpointOverride = hub.BaseUrl;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            using (var r = await Http.Api.GetAsync(hub.BaseUrl + "/api", ct)) r.EnsureSuccessStatusCode();
            using (var r = await Http.Api.GetAsync(hub.BaseUrl + "/redirect", ct)) r.EnsureSuccessStatusCode();
            using (var r = await Http.Api.GetAsync(other.BaseUrl + "/api", ct)) r.EnsureSuccessStatusCode();

            var seen = hub.Requests.ToList();
            Assert.Equal("Bearer " + Token, seen.Single(r => r.Path == "/api").Authorization);
            Assert.Equal("Bearer " + Token, seen.Single(r => r.Path == "/redirect").Authorization);
            Assert.Null(seen.Single(r => r.Path == "/file.bin").Authorization); // после перенаправления токен не передаётся
            Assert.Null(Assert.Single(other.Requests).Authorization);
        }
        finally
        {
            NetworkOptions.HfEndpointOverride = null;
        }
    }

    [Fact]
    public void RewriteForMirror_ReplacesGitHubHostsOnly()
    {
        using var home = new TempHome();
        Configure(_ => { });
        var api = new Uri("https://api.github.com/repos/ggml-org/llama.cpp/releases/latest?per_page=1");
        var asset = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b1/x.zip");
        Assert.Equal(api, NetworkOptions.RewriteForMirror(api));
        Assert.Equal(asset, NetworkOptions.RewriteForMirror(asset));

        Configure(n =>
        {
            n.GitHubApiMirror = "https://gh-api.example/prefix/";
            n.GitHubMirror = "https://ghproxy.example/https://github.com";
        });
        Assert.Equal("https://gh-api.example/prefix/repos/ggml-org/llama.cpp/releases/latest?per_page=1",
            NetworkOptions.RewriteForMirror(api).AbsoluteUri);
        Assert.Equal("https://ghproxy.example/https://github.com/ggml-org/llama.cpp/releases/download/b1/x.zip",
            NetworkOptions.RewriteForMirror(asset).OriginalString);
        var other = new Uri("https://objects.githubusercontent.com/x");
        Assert.Equal(other, NetworkOptions.RewriteForMirror(other));
        var plain = new Uri("http://api.github.com/repos");
        Assert.Equal(plain, NetworkOptions.RewriteForMirror(plain));
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("https://u:p@evil.example")]
    [InlineData("https://evil.example/?x=1")]
    [InlineData("ftp://evil.example")]
    public void RewriteForMirror_InvalidMirrorFromConfig_OriginalUri(string mirror)
    {
        // config.json можно поправить вручную: проверка зеркала — не только в окне настроек.
        using var home = new TempHome();
        Configure(n =>
        {
            n.GitHubApiMirror = mirror;
            n.GitHubMirror = mirror;
        });
        var api = new Uri("https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");
        var asset = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b1/x.zip");

        Assert.Equal(api, NetworkOptions.RewriteForMirror(api));
        Assert.Equal(asset, NetworkOptions.RewriteForMirror(asset));
    }

    [Theory]
    [InlineData("https://github.com/ggml-org/llama.cpp/releases/download/b1/x.zip", true)]
    [InlineData("https://GitHub.com/ggml-org/llama.cpp/releases/download/b1/x.zip", true)]
    [InlineData("http://github.com/ggml-org/llama.cpp/releases/download/b1/x.zip", false)]
    [InlineData("http://evil/x.zip", false)]
    [InlineData("https://evil.example/ggml-org/llama.cpp/releases/download/b1/x.zip", false)]
    [InlineData("https://github.com/evil/llama.cpp/releases/download/b1/x.zip", false)]
    [InlineData("https://github.com/ggml-org/llama.cpp/releases/download/", false)]
    [InlineData("https://github.com/ggml-org/llama.cpp/releases/download/../../../evil/r/releases/download/b1/x.zip", false)]
    [InlineData("https://github.com/ggml-org/llama.cpp/releases/download/b1/%2e%2e/x.zip", false)]
    [InlineData("https://u:p@github.com/ggml-org/llama.cpp/releases/download/b1/x.zip", false)]
    [InlineData("https://github.com/ggml-org/llama.cpp/releases/download/b1/x.zip?y=1", false)]
    [InlineData("https://github.com:8443/ggml-org/llama.cpp/releases/download/b1/x.zip", false)]
    [InlineData("u", false)]
    [InlineData("", false)]
    public void IsReleaseAssetUrl_OnlyGitHubReleaseDownloadsOfRepo(string url, bool expected) =>
        Assert.Equal(expected, NetworkOptions.IsReleaseAssetUrl(url, "ggml-org/llama.cpp"));

    [Fact]
    public void IsReleaseAssetUrl_TestWebBase_SamePathAccepted()
    {
        Assert.True(NetworkOptions.IsReleaseAssetUrl("http://127.0.0.1:5000/o/r/releases/download/t/x.zip", "o/r", "http://127.0.0.1:5000"));
        Assert.False(NetworkOptions.IsReleaseAssetUrl("http://127.0.0.1:5001/o/r/releases/download/t/x.zip", "o/r", "http://127.0.0.1:5000"));
        // http:// на внешний адрес не принимается даже как «подменённый github.com».
        Assert.False(NetworkOptions.IsReleaseAssetUrl("http://evil.example/o/r/releases/download/t/x.zip", "o/r", "http://evil.example"));
    }

    [Fact]
    public void Validate_MirrorAndProxyAddresses()
    {
        Assert.Null(NetworkOptions.ValidateMirror(null));
        Assert.Null(NetworkOptions.ValidateMirror("  "));
        Assert.Null(NetworkOptions.ValidateMirror("https://hf-mirror.com"));
        Assert.Null(NetworkOptions.ValidateMirror("http://127.0.0.1:8080/hf/"));
        Assert.NotNull(NetworkOptions.ValidateMirror("http://hf-mirror.com"));
        Assert.NotNull(NetworkOptions.ValidateMirror("ftp://x"));
        Assert.NotNull(NetworkOptions.ValidateMirror("hf-mirror.com"));
        Assert.NotNull(NetworkOptions.ValidateMirror("https://x.example/?a=1"));
        Assert.NotNull(NetworkOptions.ValidateMirror("https://user:pw@x.example"));

        Assert.Null(NetworkOptions.ValidateProxy("http://proxy.local:3128"));
        Assert.Null(NetworkOptions.ValidateProxy("socks5://127.0.0.1:1080"));
        Assert.NotNull(NetworkOptions.ValidateProxy(""));
        Assert.NotNull(NetworkOptions.ValidateProxy("proxy.local:3128"));
        Assert.NotNull(NetworkOptions.ValidateProxy("ftp://proxy.local"));
    }

    [Fact]
    public void ConfiguredProxy_FollowsSettings()
    {
        using var home = new TempHome();
        var proxy = ConfiguredProxy.Instance;
        var target = new Uri("https://huggingface.co/x");

        Configure(n => n.ProxyMode = NetworkOptions.ProxyNone);
        Assert.True(proxy.IsBypassed(target));
        Assert.Null(proxy.GetProxy(target));

        Configure(n =>
        {
            n.ProxyMode = NetworkOptions.ProxyCustom;
            n.ProxyUrl = "http://user:p%40ss@proxy.example:3128";
        });
        Assert.False(proxy.IsBypassed(target));
        Assert.Equal(new Uri("http://proxy.example:3128/"), proxy.GetProxy(target));
        var cred = proxy.Credentials!.GetCredential(new Uri("http://proxy.example:3128/"), "Basic");
        Assert.Equal("user", cred!.UserName);
        Assert.Equal("p@ss", cred.Password);

        // Локальные адреса (llama-server) — всегда напрямую.
        Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1:8765/v1")));
        Assert.Null(proxy.GetProxy(new Uri("http://127.0.0.1:8765/v1")));

        // Неверный адрес своего прокси — как «системный», без исключений.
        Configure(n => n.ProxyUrl = "not a url");
        Assert.Equal(NetworkOptions.ProxySystem, ConfiguredProxy.Current().Mode);
        _ = proxy.IsBypassed(target);
        Assert.Same(CredentialCache.DefaultNetworkCredentials, proxy.Credentials!.GetCredential(target, "Negotiate"));
    }
}
