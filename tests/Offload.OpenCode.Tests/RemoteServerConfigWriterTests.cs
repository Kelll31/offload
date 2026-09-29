using System.Net;
using System.Net.Sockets;
using Offload.Core.Config;
using Offload.Core.Security;

namespace Offload.OpenCode.Tests;

/// <summary>OpenCode в клиентском режиме (модель на удалённом сервере) и при «Доступе из сети».</summary>
[Collection("AppPaths")]
public sealed class RemoteServerConfigWriterTests
{
    private const string RemoteKey = "olan-remote-secret-for-opencode";

    private static AppConfig Remote(string url = "http://192.168.1.10:8765")
    {
        var cfg = TestConfig.Make();
        cfg.Remote.Enabled = true;
        cfg.Remote.Url = url;
        cfg.Remote.ApiKeyProtected = Dpapi.Protect(RemoteKey);
        cfg.Remote.ModelId = "big-remote";
        cfg.Remote.ContextSize = 49152;
        return cfg;
    }

    [Fact]
    public void ManagedConfig_Remote_PointsToRemoteServerAndModel_KeyNotInFile()
    {
        using var home = new TempHome();
        var path = OpenCodeConfigWriter.WriteManagedConfig(Remote());

        var text = File.ReadAllText(path);
        Assert.DoesNotContain(RemoteKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestConfig.Key, text, StringComparison.Ordinal);
        var provider = TestConfig.ReadJson(path)["provider"]!["offload"]!;
        Assert.Equal("http://192.168.1.10:8765/v1", (string?)provider["options"]!["baseURL"]);
        Assert.Equal("{env:OFFLOAD_API_KEY}", (string?)provider["options"]!["apiKey"]);
        var model = provider["models"]!["local-coder"]!;
        Assert.Equal("big-remote", (string?)model["id"]);
        Assert.Equal(49152, (int)model["limit"]!["context"]!);
        // Отключение размышлений локальной модели (EnableThinkingKwarg) к удалённой модели не относится.
        Assert.Null(model["options"]!["chat_template_kwargs"]);
    }

    [Theory]
    [InlineData("{file:~/.ssh/id_rsa}")]
    [InlineData("{env:GITHUB_TOKEN}")]
    public void ManagedConfig_Remote_UnsafeModelIdFromConfig_FallsBackToAlias(string evil)
    {
        using var home = new TempHome();
        var cfg = Remote();
        cfg.Remote.ModelId = evil;   // ручная правка config.json или старый ответ сервера
        var text = File.ReadAllText(OpenCodeConfigWriter.WriteManagedConfig(cfg));
        Assert.DoesNotContain("{file:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{env:GITHUB", text, StringComparison.Ordinal);
        Assert.Equal(RemoteServer.DefaultModel, cfg.MainEndpoint().Model);
    }

    [Fact]
    public void Environment_Remote_PassesRemoteKeyAndBypassesProxyForHost()
    {
        var env = OpenCodeConfigWriter.BuildEnvironment(Remote("http://gpu-pc.lan:8765"), interactive: false);
        Assert.Equal(RemoteKey, env[OpenCodeConfigWriter.ApiKeyEnvVar]);
        Assert.Contains("gpu-pc.lan", env["NO_PROXY"]!, StringComparison.OrdinalIgnoreCase);

        var local = OpenCodeConfigWriter.BuildEnvironment(TestConfig.Make(), interactive: false);
        Assert.Equal(TestConfig.Key, local[OpenCodeConfigWriter.ApiKeyEnvVar]);
    }

    [Fact]
    public void RegisterGlobal_Remote_DoesNotWriteNetworkKeyToUserFile()
    {
        using var home = new TempHome();
        OpenCodeConfigWriter.RegisterGlobal(Remote());
        var path = Path.Combine(home.GlobalOpenCodeDir, "opencode.json");
        Assert.DoesNotContain(RemoteKey, File.ReadAllText(path), StringComparison.Ordinal);
        var provider = TestConfig.ReadJson(path)["provider"]!["offload"]!;
        Assert.Equal("{env:OFFLOAD_API_KEY}", (string?)provider["options"]!["apiKey"]);
        Assert.Equal("http://192.168.1.10:8765/v1", (string?)provider["options"]!["baseURL"]);
    }

    [Fact]
    public void ClientBaseUrl_LanBindToAddress_UsesThatAddress()
    {
        var cfg = TestConfig.Make();
        cfg.Server.LanAccess = true;
        cfg.Server.LanBindAddress = "192.168.1.20";
        Assert.Equal("http://127.0.0.1:8799", LocalServer.ClientBaseUrl(cfg.Server)); // без сетевого ключа режим не действует
        cfg.Server.LanApiKeyProtected = Dpapi.Protect("olan-lan");
        Assert.Equal("http://192.168.1.20:8799", LocalServer.ClientBaseUrl(cfg.Server));
        cfg.Server.LanBindAddress = "0.0.0.0";
        Assert.Equal("http://127.0.0.1:8799", LocalServer.ClientBaseUrl(cfg.Server));
    }

    [Fact]
    public async Task Probe_RemoteUnreachable_RemoteSpecificMessage()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var probe = await LocalServer.ProbeAsync(Remote($"http://127.0.0.1:{port}"), null, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.False(probe.Reachable);
        Assert.Contains($"127.0.0.1:{port}", probe.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(RemoteKey, probe.Error!, StringComparison.Ordinal);
    }
}
