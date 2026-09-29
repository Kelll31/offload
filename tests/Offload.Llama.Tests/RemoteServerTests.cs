using System.Net;
using System.Net.Sockets;
using Offload.Core.Config;
using Offload.Core.Security;

namespace Offload.Llama.Tests;

/// <summary>
/// «Доступ из сети» (аргументы и ключи основного llama-server) и клиентский режим (адрес основной модели, проверки
/// удалённого сервера) — без сети, против поддельного HTTP-сервера на 127.0.0.1.
/// </summary>
public sealed class RemoteServerTests
{
    private const string Exe = @"C:\pc\llama.cpp\b11102-cuda12\llama-server.exe";
    private const string RemoteKey = "olan-remote-key";

    private static AppConfig Cfg(Action<AppConfig>? edit = null)
    {
        var cfg = new AppConfig();
        cfg.Server.ApiKey = "pc-local";
        cfg.Server.Port = 18080;
        edit?.Invoke(cfg);
        return cfg;
    }

    private static InstalledModel Model() => new()
    {
        Id = "m",
        DisplayName = "M",
        FilePath = @"C:\pc\models\m.gguf",
        RecommendedContext = 8192,
    };

    private static string? ValueOf(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static int UnusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static AppConfig RemoteCfg(string url) => Cfg(c =>
    {
        c.Remote.Enabled = true;
        c.Remote.Url = url;
        c.Remote.ApiKeyProtected = Dpapi.Protect(RemoteKey);
    });

    // ── Доступ из сети ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_Default_ListensOnLoopbackOnly()
    {
        var a = LlamaServerArgs.Build(Cfg(), Model(), Exe).Arguments;
        Assert.Equal("127.0.0.1", ValueOf(a, "--host"));
        Assert.Equal("pc-local", LlamaServerArgs.BuildEnvironment(Cfg())[LlamaServerArgs.ApiKeyEnvVar]);
    }

    [Fact]
    public void Build_LanRequestedWithoutKey_StaysOnLoopback()
    {
        var cfg = Cfg(c =>
        {
            c.Server.LanAccess = true;
            c.Server.LanBindAddress = "0.0.0.0";
        });
        Assert.Equal("127.0.0.1", ValueOf(LlamaServerArgs.Build(cfg, Model(), Exe).Arguments, "--host"));
        Assert.Equal("pc-local", LlamaServerArgs.BuildEnvironment(cfg)[LlamaServerArgs.ApiKeyEnvVar]);
    }

    [Fact]
    public void Build_LanWithKey_BindsNetworkAndRequiresBothKeys_KeysNotInArguments()
    {
        var cfg = Cfg(c =>
        {
            c.Server.LanAccess = true;
            c.Server.LanBindAddress = "192.168.1.20";
            c.Server.LanApiKeyProtected = Dpapi.Protect("olan-network");
        });
        var a = LlamaServerArgs.Build(cfg, Model(), Exe).Arguments;
        Assert.Equal("192.168.1.20", ValueOf(a, "--host"));
        Assert.DoesNotContain(a, x => x.Contains("olan-network", StringComparison.Ordinal) || x.Contains("pc-local", StringComparison.Ordinal));
        Assert.Equal("pc-local,olan-network", LlamaServerArgs.BuildEnvironment(cfg)[LlamaServerArgs.ApiKeyEnvVar]);
        // Вспомогательные серверы (127.0.0.1) сетевой ключ не получают.
        Assert.Equal("pc-local", LlamaServerArgs.BuildEnvironment(cfg, main: false)[LlamaServerArgs.ApiKeyEnvVar]);
        Assert.Equal("http://192.168.1.20:18080", LlamaClient.FromConfig(cfg).BaseUrl);
    }

    [Fact]
    public void AuxServer_StaysOnLoopback_InLanMode()
    {
        var cfg = Cfg(c =>
        {
            c.Server.LanAccess = true;
            c.Server.LanBindAddress = "0.0.0.0";
            c.Server.LanApiKeyProtected = Dpapi.Protect("olan-network");
        });
        var plan = AuxServerArgs.Build(cfg, ModelRole.Fast, Model(), Exe, 18081);
        Assert.Equal("127.0.0.1", ValueOf(plan.Arguments, "--host"));
        Assert.StartsWith("http://127.0.0.1:", cfg.RoleBaseUrl(ModelRole.Fast), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtraArgs_CannotOverrideHostOrKey()
    {
        var cfg = Cfg(c => c.Server.ExtraArgs = "--host 0.0.0.0 --api-key x --api-key-file k.txt");
        var a = LlamaServerArgs.Build(cfg, Model(), Exe).Arguments;
        Assert.Equal("127.0.0.1", ValueOf(a, "--host"));
        Assert.Single(a, x => x == "--host");
        Assert.DoesNotContain("--api-key", a);
    }

    // ── Клиентский режим: выбор сервера ──────────────────────────────────────

    [Fact]
    public void FromConfig_Local_UsesOwnServer()
    {
        var client = LlamaClient.FromConfig(Cfg());
        Assert.False(client.IsRemote);
        Assert.Equal("http://127.0.0.1:18080", client.BaseUrl);
        Assert.Equal("pc-local", client.ApiKey);
        Assert.Equal(LlamaServerArgs.DefaultAlias, client.Model);
    }

    [Fact]
    public void ForRole_Remote_MainGoesRemote_AuxStaysLocal()
    {
        var cfg = RemoteCfg("http://192.168.1.10:8765/v1");
        cfg.Remote.ModelId = "qwen-remote";

        var main = LlamaClient.ForRole(cfg, ModelRole.Quality);
        Assert.True(main.IsRemote);
        Assert.Equal("http://192.168.1.10:8765", main.BaseUrl);
        Assert.Equal(RemoteKey, main.ApiKey);
        Assert.Equal("qwen-remote", main.Model);
        Assert.True(main.UnifiedKv);

        var fast = LlamaClient.ForRole(cfg, ModelRole.Fast);
        Assert.False(fast.IsRemote);
        Assert.StartsWith("http://127.0.0.1:", fast.BaseUrl, StringComparison.Ordinal);
        Assert.Equal("pc-local", fast.ApiKey);

        var renamed = main.WithModel("other");
        Assert.Equal(("other", main.BaseUrl, main.ApiKey, true), (renamed.Model, renamed.BaseUrl, renamed.ApiKey, renamed.IsRemote));
    }

    [Theory]
    [InlineData("{% if enable_thinking %}<think>{% endif %}", ReasoningControl.EnableThinkingKwarg)]
    [InlineData("{{ reasoning_effort }}", ReasoningControl.ReasoningEffort)]
    [InlineData("{{ messages }}", ReasoningControl.None)]
    [InlineData(null, null)]
    public void Props_ReasoningHint_FromChatTemplate(string? template, ReasoningControl? expected)
    {
        var json = template is null
            ? """{"default_generation_settings":{"n_ctx":4096},"total_slots":1}"""
            : $$"""{"default_generation_settings":{"n_ctx":4096},"total_slots":1,"chat_template":{{System.Text.Json.JsonSerializer.Serialize(template)}}}""";
        Assert.Equal(expected, LlamaClient.ParseProps(json).ReasoningHint);
    }

    // ── Клиентский режим: проверки удалённого сервера ────────────────────────

    [Theory]
    [InlineData(new[] { "offload", "x" }, "wanted", "offload")]
    [InlineData(new[] { "a", "wanted" }, "wanted", "wanted")]
    [InlineData(new[] { "only" }, null, "only")]
    [InlineData(new string[0], "wanted", "wanted")]
    public void PickModel(string[] served, string? preferred, string expected) =>
        Assert.Equal(expected, RemoteProbe.PickModel(served, preferred));

    [Fact]
    public void ParseModelIds_SkipsJunk()
    {
        var ids = RemoteProbe.ParseModelIds("""{"data":[{"id":"a"},{"id":""},{"id":5},"x",{"id":"a"},{"id":"b"}]}""");
        Assert.Equal(["a", "b"], ids);
        Assert.Empty(RemoteProbe.ParseModelIds("[]"));
    }

    [Theory]
    [InlineData("{file:~/.ssh/id_ed25519}")]
    [InlineData("{env:GITHUB_TOKEN}")]
    [InlineData("a\nb")]
    [InlineData("x\"y")]
    [InlineData("$(whoami)")]
    public void ParseModelIds_DropsIdsUnsafeForOpenCodeConfig(string evil)
    {
        // Удалённый сервер недоверенный: id уходит в конфиг OpenCode, где {file:…}/{env:…} подставляются.
        var json = System.Text.Json.JsonSerializer.Serialize(new { data = new[] { new { id = evil }, new { id = "C:\\models\\qwen 7b.gguf" } } });
        Assert.Equal(["C:\\models\\qwen 7b.gguf"], RemoteProbe.ParseModelIds(json));
        Assert.Equal(RemoteServer.DefaultModel, RemoteProbe.PickModel([], evil));
    }

    [Fact]
    public async Task Health_SendsKeyInHeaderNotUrl_Ready()
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 200, """{"status":"ok"}"""));
        var client = LlamaClient.FromConfig(RemoteCfg(server.BaseUrl));

        var h = await RemoteProbe.HealthAsync(client, TestContext.Current.CancellationToken);

        Assert.Equal(HealthState.Ready, h.State);
        Assert.True(h.Latency > TimeSpan.Zero);
        var req = Assert.Single(server.Requests);
        Assert.Equal("/health", req.Path);
        Assert.Equal("Bearer " + RemoteKey, req.Headers["Authorization"]);
        Assert.DoesNotContain(RemoteKey, req.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_NoHealthEndpoint_FallsBackToModels()
    {
        await using var server = new FakeHttpServer((r, s, _) => r.Path == "/v1/models"
            ? FakeHttpServer.WriteResponseAsync(s, 200, """{"data":[{"id":"gpt-oss"}]}""")
            : FakeHttpServer.WriteResponseAsync(s, 404, "{}"));
        var client = LlamaClient.FromConfig(RemoteCfg(server.BaseUrl));
        Assert.Equal(HealthState.Ready, (await RemoteProbe.HealthAsync(client, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task Health_Loading_And_Down()
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 503, "{}"));
        Assert.Equal(HealthState.Loading, (await RemoteProbe.HealthAsync(LlamaClient.FromConfig(RemoteCfg(server.BaseUrl)), TestContext.Current.CancellationToken)).State);

        var down = await RemoteProbe.HealthAsync(LlamaClient.FromConfig(RemoteCfg($"http://127.0.0.1:{UnusedPort()}")), TestContext.Current.CancellationToken);
        Assert.Equal(HealthState.Down, down.State);
        Assert.False(string.IsNullOrWhiteSpace(down.Detail));
    }

    [Fact]
    public async Task Check_Success_ReportsModelContextAndLatency()
    {
        await using var server = new FakeHttpServer((r, s, _) =>
        {
            if (r.Headers.GetValueOrDefault("Authorization") != "Bearer " + RemoteKey && r.Path == "/props")
                return FakeHttpServer.WriteResponseAsync(s, 401, "{}");
            return r.Path switch
            {
                "/health" => FakeHttpServer.WriteResponseAsync(s, 200, """{"status":"ok"}"""),
                "/v1/models" => FakeHttpServer.WriteResponseAsync(s, 200, """{"object":"list","data":[{"id":"offload"}]}"""),
                "/props" => FakeHttpServer.WriteResponseAsync(s, 200, """{"default_generation_settings":{"n_ctx":131072},"total_slots":2,"model_alias":"offload"}"""),
                _ => FakeHttpServer.WriteResponseAsync(s, 404, "{}"),
            };
        });

        var r = await RemoteProbe.CheckAsync(server.BaseUrl, RemoteKey, null, TestContext.Current.CancellationToken);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("offload", r.ModelId);
        Assert.Equal(["offload"], r.Models);
        // Слоты удалённого сервера делят KV — контекст на запрос с запасом: n_ctx / слоты.
        Assert.Equal(65536, r.ContextPerRequest);
        Assert.Equal(2, r.Slots);
        Assert.True(r.KeyAccepted);
        Assert.All(server.Requests, q => Assert.Equal("Bearer " + RemoteKey, q.Headers["Authorization"]));
    }

    [Fact]
    public async Task Check_WrongKey_ReportsRejectedKey()
    {
        await using var server = new FakeHttpServer((r, s, _) => r.Path == "/health"
            ? FakeHttpServer.WriteResponseAsync(s, 200, """{"status":"ok"}""")
            : FakeHttpServer.WriteResponseAsync(s, 401, """{"error":{"code":401,"message":"Invalid API Key"}}"""));

        var r = await RemoteProbe.CheckAsync(server.BaseUrl, "wrong", null, TestContext.Current.CancellationToken);

        Assert.False(r.Ok);
        Assert.False(r.KeyAccepted);
        Assert.Contains("401", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_Unreachable_ReportsError()
    {
        var r = await RemoteProbe.CheckAsync($"http://127.0.0.1:{UnusedPort()}", RemoteKey, null, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Equal(HealthState.Down, r.State);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }
}
