using System.Text;
using Offload.Core.Config;
using Offload.Core.Security;

namespace Offload.Llama.Tests;

/// <summary>
/// Защита сервера и клиента: адреса прослушивания (Host из config.json, серверы ролей), отдельный ключ автодополнения,
/// проверка ключа удалённого сервера, потолки размера ответов недоверенного сервера.
/// </summary>
public sealed class ServerHardeningTests
{
    private const string Exe = @"C:\pc\llama.cpp\b11102-cuda12\llama-server.exe";

    private static AppConfig Cfg(Action<AppConfig>? edit = null)
    {
        var cfg = new AppConfig();
        cfg.Server.ApiKey = "pc-local";
        cfg.Server.Port = 18080;
        cfg.Autocomplete.ApiKey = "fim-own";
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

    // ── Адрес прослушивания ──────────────────────────────────────────────────

    [Fact]
    public void HostEditedToAnyAddress_WithoutLanKey_MainAndFimStayOnLoopback()
    {
        // Ручная правка config.json не открывает сервер в сеть без режима «Доступ из сети» с сетевым ключом.
        var cfg = Cfg(c => c.Server.Host = "0.0.0.0");
        var main = LlamaServerArgs.Build(cfg, Model(), Exe);
        Assert.Equal("127.0.0.1", ValueOf(main.Arguments, "--host"));
        Assert.Equal("127.0.0.1", main.ListenHost);
        Assert.Equal("127.0.0.1", ValueOf(AuxServerArgs.Build(cfg, ModelRole.Fim, Model(), Exe, 8012).Arguments, "--host"));
        Assert.Equal("http://127.0.0.1:8012", AuxServerArgs.ClientBaseUrl(cfg, ModelRole.Fim));
        Assert.StartsWith("http://127.0.0.1:", cfg.RoleBaseUrl(ModelRole.Fast), StringComparison.Ordinal);
    }

    [Fact]
    public void LanActive_AuxServersIgnoreNonLoopbackHost()
    {
        var cfg = Cfg(c =>
        {
            c.Server.Host = "192.168.1.20";
            c.Server.LanAccess = true;
            c.Server.LanBindAddress = "192.168.1.20";
            c.Server.LanApiKeyProtected = Dpapi.Protect("olan-network");
        });
        Assert.Equal("192.168.1.20", ValueOf(LlamaServerArgs.Build(cfg, Model(), Exe).Arguments, "--host"));
        Assert.Equal("127.0.0.1", ValueOf(AuxServerArgs.Build(cfg, ModelRole.Fim, Model(), Exe, 8012).Arguments, "--host"));
    }

    [Fact]
    public void LoopbackHost_KeepsLoopbackVariants()
    {
        Assert.Equal("::1", new ServerSettings { Host = "::1" }.LoopbackHost());
        Assert.Equal("127.0.0.2", new ServerSettings { Host = "127.0.0.2" }.LoopbackHost());
        Assert.Equal("localhost", new ServerSettings { Host = "localhost" }.LoopbackHost());
        Assert.Equal("127.0.0.1", new ServerSettings { Host = "10.0.0.5" }.LoopbackHost());
        Assert.Equal("127.0.0.1", new ServerSettings { Host = "" }.LoopbackHost());
    }

    // ── Ключ сервера автодополнения ───────────────────────────────────────────

    [Fact]
    public void FimServer_GetsOwnKey_NotMainOrLanKey()
    {
        var cfg = Cfg(c =>
        {
            c.Server.LanAccess = true;
            c.Server.LanBindAddress = "0.0.0.0";
            c.Server.LanApiKeyProtected = Dpapi.Protect("olan-network");
        });
        Assert.Equal("fim-own", LlamaServerArgs.BuildEnvironment(cfg, main: false, role: ModelRole.Fim)[LlamaServerArgs.ApiKeyEnvVar]);
        Assert.Equal("pc-local", LlamaServerArgs.BuildEnvironment(cfg, main: false, role: ModelRole.Embed)[LlamaServerArgs.ApiKeyEnvVar]);
    }

    [Fact]
    public void FimServer_WithoutOwnKey_RefusesToStart()
    {
        Assert.Throws<InvalidOperationException>(() => LlamaServerArgs.AuxApiKey(Cfg(c => c.Autocomplete.ApiKey = null), ModelRole.Fim));
        // Ключ, совпадающий с основным, — тоже не свой.
        Assert.Throws<InvalidOperationException>(() => LlamaServerArgs.AuxApiKey(Cfg(c => c.Autocomplete.ApiKey = "pc-local"), ModelRole.Fim));
    }

    // ── Ключ удалённого сервера ──────────────────────────────────────────────

    [Theory]
    [InlineData(200, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, null)]
    public async Task KeyAccepted_ByPropsStatus(int status, bool? expected)
    {
        await using var server = new FakeHttpServer((r, s, _) => r.Path == "/props"
            ? FakeHttpServer.WriteResponseAsync(s, status, "{}")
            : FakeHttpServer.WriteResponseAsync(s, 200, """{"status":"ok"}"""));
        var client = new LlamaClient(server.BaseUrl, "olan-key") { IsRemote = true };
        Assert.Equal(expected, await RemoteProbe.KeyAcceptedAsync(client, TestContext.Current.CancellationToken));
        Assert.Equal("Bearer olan-key", Assert.Single(server.Requests).Headers["Authorization"]);
    }

    // ── Потолки размера ответов ──────────────────────────────────────────────

    [Fact]
    public async Task Props_OversizedBody_Discarded()
    {
        var huge = "{\"n_ctx\":4096,\"build_info\":\"" + new string('x', BoundedRead.MaxJsonChars) + "\"}";
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 200, huge));
        Assert.Null(await new LlamaClient(server.BaseUrl, "k").GetPropsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Props_RemoteStrings_Sanitized()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            n_ctx = 4096,
            model_alias = "{env:GITHUB_TOKEN}",
            model_path = "C:\\m\u001b[31m.gguf",
            build_info = "b1\r\nFAKE LOG LINE",
        });
        var p = LlamaClient.ParseProps(json);
        Assert.Null(p.ModelAlias);
        Assert.Equal("C:\\m[31m.gguf", p.ModelPath);
        Assert.Equal("b1FAKE LOG LINE", p.BuildInfo);
        Assert.Equal("qwen3-coder", LlamaClient.ParseProps("""{"model_alias":"qwen3-coder"}""").ModelAlias);
    }

    [Fact]
    public async Task Chat_EndlessSseLine_FailsInsteadOfBufferingForever()
    {
        await using var server = new FakeHttpServer(async (_, s, ct) =>
        {
            await FakeHttpServer.WriteSseHeadersAsync(s);
            var chunk = Encoding.ASCII.GetBytes(new string('a', 64 * 1024));
            await s.WriteAsync(Encoding.ASCII.GetBytes("data: "), ct);
            // Строка без перевода строки длиннее потолка: клиент должен оборвать чтение сам.
            for (var i = 0; i < BoundedRead.MaxSseLineChars / chunk.Length + 2; i++)
                await s.WriteAsync(chunk, ct);
        });
        var client = new LlamaClient(server.BaseUrl, "k") { IsRemote = true };
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() => client.ChatAsync(new ChatRequest([ChatMessage.User("hi")]), ct: TestContext.Current.CancellationToken));
        Assert.Equal(LlamaErrorKind.Other, ex.Kind);
    }

    [Fact]
    public async Task BoundedLineReader_SplitsLikeReadLine()
    {
        var reader = new BoundedLineReader(new StringReader("a\r\nb\rc\n\nlast"), 10);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line) lines.Add(line);
        Assert.Equal(["a", "b", "c", "", "last"], lines);

        var tooLong = new BoundedLineReader(new StringReader(new string('x', 11) + "\n"), 10);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await tooLong.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ListenerOutdated_WhenLanDisabledButProcessStillOnNetwork()
    {
        var on = Cfg(c =>
        {
            c.Server.LanAccess = true;
            c.Server.LanBindAddress = "0.0.0.0";
            c.Server.LanApiKeyProtected = Dpapi.Protect("olan-network");
        }).Server;
        Assert.False(LanServer.ListenerOutdated("0.0.0.0", on));
        Assert.True(LanServer.ListenerOutdated("192.168.1.20", on)); // адрес сменили, процесс — ещё нет
        var off = Cfg().Server;
        Assert.True(LanServer.ListenerOutdated("0.0.0.0", off));   // режим выключен, процесс всё ещё в сети
        Assert.False(LanServer.ListenerOutdated("127.0.0.1", off));
        Assert.False(LanServer.ListenerOutdated(null, off));
    }

    // ── Адрес «Доступа из сети» пропал ───────────────────────────────────────

    [Fact]
    public void StaleBindAddress_ReportedOnlyForMissingSpecificAddress()
    {
        var s = Cfg(c =>
        {
            c.Server.LanAccess = true;
            c.Server.LanBindAddress = "192.168.1.20";
            c.Server.LanApiKeyProtected = Dpapi.Protect("olan-network");
        }).Server;
        var error = LanServer.StaleBindAddressError(s, ["10.0.0.7"]);
        Assert.NotNull(error);
        Assert.Contains("192.168.1.20", error, StringComparison.Ordinal);
        Assert.Null(LanServer.StaleBindAddressError(s, ["10.0.0.7", "192.168.1.20"]));
        s.LanBindAddress = LanServer.AnyAddress;
        Assert.Null(LanServer.StaleBindAddressError(s, []));
        s.LanAccess = false;
        s.LanBindAddress = "192.168.1.20";
        Assert.Null(LanServer.StaleBindAddressError(s, []));
    }
}
