using System.Text.Json;
using Offload.Core.Config;
using Offload.Core.Security;
using Offload.Core.Util;

namespace Offload.Core.Tests;

/// <summary>Удалённый сервер (клиентский режим) и «Доступ из сети»: настройки, проверка адреса, выбор адреса основной модели.</summary>
[Collection("AppPaths")]
public sealed class RemoteServerConfigTests
{
    private const string RemoteKey = "olan-remote-secret-0123456789";

    [Fact]
    public void Config_RoundTrip_KeepsRemoteAndLanSettings_KeysOnlyEncrypted()
    {
        using var home = new TempHome();
        ConfigStore.Update(c =>
        {
            c.Remote.Enabled = true;
            c.Remote.Url = "http://192.168.1.10:8765";
            c.Remote.ApiKeyProtected = Dpapi.Protect(RemoteKey);
            c.Remote.ModelId = "qwen-coder";
            c.Remote.ContextSize = 65536;
            c.Server.LanAccess = true;
            c.Server.LanBindAddress = "192.168.1.20";
            c.Server.LanApiKeyProtected = Dpapi.Protect("olan-host-secret");
        });

        var cfg = ConfigStore.Reload();
        Assert.True(cfg.Remote.Enabled);
        Assert.Equal("http://192.168.1.10:8765", cfg.Remote.Url);
        Assert.Equal("qwen-coder", cfg.Remote.ModelId);
        Assert.Equal(65536, cfg.Remote.ContextSize);
        Assert.Equal(RemoteKey, cfg.RemoteApiKey());
        Assert.True(cfg.Server.LanAccess);
        Assert.Equal("192.168.1.20", cfg.Server.LanBindAddress);
        Assert.Equal("olan-host-secret", cfg.Server.LanApiKey());

        var text = File.ReadAllText(AppPaths.ConfigFile);
        Assert.DoesNotContain(RemoteKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("olan-host-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_WithoutRemoteSection_DefaultsOff()
    {
        using var home = new TempHome();
        File.WriteAllText(AppPaths.ConfigFile, """{ "schemaVersion": 1, "remote": null, "server": { "port": 8765 } }""");
        var cfg = ConfigStore.Reload();
        Assert.NotNull(cfg.Remote);
        Assert.False(cfg.Remote.Enabled);
        Assert.False(cfg.IsRemote());
        Assert.False(cfg.Server.LanAccess);
        Assert.Equal("127.0.0.1", cfg.Server.ListenHost());
        Assert.Equal("http://127.0.0.1:8765", cfg.Server.BaseUrl);
    }

    [Theory]
    [InlineData("http://192.168.1.10:8765", "http://192.168.1.10:8765")]
    [InlineData("  http://192.168.1.10:8765/  ", "http://192.168.1.10:8765")]
    [InlineData("https://GPU-PC.local:8443/v1/", "https://gpu-pc.local:8443")]
    [InlineData("http://gpu-pc/v1", "http://gpu-pc")]
    [InlineData("http://127.0.0.1:9000", "http://127.0.0.1:9000")]
    [InlineData("http://[fd7a:115c:a1e0::1]:8765", "http://[fd7a:115c:a1e0::1]:8765")]
    public void TryNormalizeUrl_Accepts(string input, string expected)
    {
        Assert.True(RemoteServer.TryNormalizeUrl(input, out var url, out var error), error);
        Assert.Equal(expected, url);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.10:8765")]
    [InlineData("gpu-pc")]
    [InlineData("ftp://192.168.1.10/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ws://192.168.1.10:8765")]
    [InlineData("http://user:pass@192.168.1.10:8765")]
    [InlineData("http://user@192.168.1.10:8765")]
    [InlineData("http://evil.example@192.168.1.10:8765")]
    [InlineData("http://192.168.1.10:8765/api")]
    [InlineData("http://192.168.1.10:8765/v1/chat/completions")]
    [InlineData("http://192.168.1.10:8765/v1/../../admin")]
    [InlineData("http://192.168.1.10:8765/?key=secret")]
    [InlineData("http://192.168.1.10:8765/#frag")]
    [InlineData("http://192.168.1.10:8765\\x")]
    [InlineData("http://192.168.1.10:8765/ x")]
    [InlineData("http://192.168.1.10:87\n65")]
    [InlineData("http://0.0.0.0:8765")]
    public void TryNormalizeUrl_Rejects(string input)
    {
        Assert.False(RemoteServer.TryNormalizeUrl(input, out var url, out var error), $"адрес «{input}» принят как {url}");
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal("", url);
    }

    [Theory]
    [InlineData("olan-0123abcd", true)]
    [InlineData("pc-abc_DEF.123~", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("ключ", false)]
    public void ValidateApiKey(string key, bool ok) =>
        Assert.Equal(ok, RemoteServer.ValidateApiKey(key) is null);

    [Fact]
    public void MainEndpoint_Local_UsesOwnServerAndKey()
    {
        var cfg = new AppConfig();
        cfg.Server.Port = 18765;
        cfg.Server.ApiKey = "pc-local";
        var ep = cfg.MainEndpoint();
        Assert.False(ep.IsRemote);
        Assert.Equal("http://127.0.0.1:18765", ep.BaseUrl);
        Assert.Equal("http://127.0.0.1:18765/v1", ep.OpenAiBaseUrl);
        Assert.Equal("pc-local", ep.ApiKey);
        Assert.Equal(RemoteServer.DefaultModel, ep.Model);
    }

    [Fact]
    public void MainEndpoint_Remote_UsesRemoteUrlKeyAndModel()
    {
        var cfg = new AppConfig();
        cfg.Server.ApiKey = "pc-local";
        cfg.Remote.Enabled = true;
        cfg.Remote.Url = "http://192.168.1.10:8765/v1";
        cfg.Remote.ApiKeyProtected = Dpapi.Protect(RemoteKey);
        var ep = cfg.MainEndpoint();
        Assert.True(cfg.IsRemote());
        Assert.True(ep.IsRemote);
        Assert.Equal("http://192.168.1.10:8765", ep.BaseUrl);
        Assert.Equal(RemoteKey, ep.ApiKey);
        Assert.Equal(RemoteServer.DefaultModel, ep.Model);
        Assert.Equal("192.168.1.10:8765", ep.Host);

        cfg.Remote.ModelId = "served-id";
        Assert.Equal("served-id", cfg.MainEndpoint().Model);
    }

    [Fact]
    public void MainEndpoint_RemoteWithInvalidUrlOrUndecryptableKey()
    {
        var cfg = new AppConfig();
        cfg.Remote.Enabled = true;
        cfg.Remote.Url = "ftp://192.168.1.10";
        Assert.False(cfg.IsRemote());
        Assert.False(cfg.MainEndpoint().IsRemote);

        cfg.Remote.Url = "http://192.168.1.10:8765";
        cfg.Remote.ApiKeyProtected = Convert.ToBase64String(new byte[64]);
        Assert.True(cfg.IsRemote());
        Assert.Null(cfg.RemoteApiKey());
        Assert.Equal("", cfg.MainEndpoint().ApiKey);
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("gpu-pc", false)]
    [InlineData("1", false)]
    [InlineData("010.1.1.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidBindAddress(string? address, bool ok) => Assert.Equal(ok, LanServer.IsValidBindAddress(address));

    [Fact]
    public void Lan_RequiresKey_BeforeListeningOnNetwork()
    {
        var s = new ServerSettings { ApiKey = "pc-local", Port = 8765, LanAccess = true, LanBindAddress = "0.0.0.0" };
        Assert.False(s.IsActive());
        Assert.Equal("127.0.0.1", s.ListenHost());
        Assert.Equal("pc-local", LanServer.ApiKeysForServer(s));

        s.LanApiKeyProtected = Dpapi.Protect("olan-lan");
        Assert.True(s.IsActive());
        Assert.Equal("0.0.0.0", s.ListenHost());
        Assert.Equal("http://127.0.0.1:8765", s.BaseUrl);
        Assert.Equal("pc-local,olan-lan", LanServer.ApiKeysForServer(s));

        s.LanBindAddress = "192.168.1.20";
        Assert.Equal("192.168.1.20", s.ListenHost());
        // При конкретном адресе петлевой не отвечает — свои клиенты идут на него же.
        Assert.Equal("http://192.168.1.20:8765", s.BaseUrl);

        s.LanBindAddress = "127.0.0.1";
        Assert.False(s.IsActive());
        Assert.Equal("127.0.0.1", s.ListenHost());

        s.LanBindAddress = "0.0.0.0";
        s.LanAccess = false;
        Assert.Equal("127.0.0.1", s.ListenHost());
        Assert.Equal("pc-local", LanServer.ApiKeysForServer(s));
    }

    [Fact]
    public void Lan_UndecryptableKey_RefusesToStart()
    {
        var s = new ServerSettings
        {
            ApiKey = "pc-local",
            LanAccess = true,
            LanBindAddress = "0.0.0.0",
            LanApiKeyProtected = Convert.ToBase64String(new byte[64]),
        };
        Assert.Throws<InvalidOperationException>(() => LanServer.ApiKeysForServer(s));
    }

    [Fact]
    public void NewLanApiKey_StrongAndCommaFree()
    {
        var a = LanServer.NewLanApiKey();
        var b = LanServer.NewLanApiKey();
        Assert.NotEqual(a, b);
        Assert.StartsWith("olan-", a, StringComparison.Ordinal);
        Assert.Equal(5 + 64, a.Length);
        Assert.DoesNotContain(',', a);
        Assert.Null(RemoteServer.ValidateApiKey(a));
    }

    [Fact]
    public void ConnectUrls_AllInterfacesOrChosenAddress()
    {
        var s = new ServerSettings { Port = 8765, LanBindAddress = "0.0.0.0" };
        Assert.Equal(["http://192.168.1.20:8765", "http://100.64.0.5:8765"], LanServer.ConnectUrls(s, ["192.168.1.20", "100.64.0.5"]));
        s.LanBindAddress = "192.168.1.20";
        Assert.Equal(["http://192.168.1.20:8765"], LanServer.ConnectUrls(s, ["192.168.1.20", "100.64.0.5"]));
    }

    [Fact]
    public void LocalIPv4Addresses_NoLoopbackOrLinkLocal()
    {
        foreach (var a in LanServer.LocalIPv4Addresses())
        {
            Assert.True(LanServer.IsValidBindAddress(a), a);
            Assert.False(a.StartsWith("127.", StringComparison.Ordinal) || a.StartsWith("169.254.", StringComparison.Ordinal), a);
        }
    }

    [Fact]
    public void Serialized_ServerSettings_DoesNotExposeLanKeyInPlainText()
    {
        var s = new ServerSettings { ApiKey = "pc-local", LanAccess = true, LanApiKeyProtected = Dpapi.Protect("olan-hidden") };
        var json = JsonSerializer.Serialize(s, Json.Options);
        Assert.DoesNotContain("olan-hidden", json, StringComparison.Ordinal);
        Assert.Contains("lanApiKeyProtected", json, StringComparison.Ordinal);
    }
}
