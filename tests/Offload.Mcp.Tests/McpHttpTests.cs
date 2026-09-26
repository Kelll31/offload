using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Offload.Core;
using Offload.Mcp.Http;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

/// <summary>
/// Streamable HTTP (--mcp-http): только loopback, Bearer-токен (401), проверка Host/Origin против DNS rebinding (403),
/// initialize и вызовы по HTTP — сырыми запросами и клиентом SDK в обеих ревизиях протокола.
/// </summary>
[Collection("AppPaths")]
public class McpHttpTests
{
    private const string Token = "oft-test-token-0123456789abcdef";

    private const string InitializeBody =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"http-test","version":"1.0"}}}""";

    private static async Task<McpHttpHost> StartAsync(TestEnv env, bool allowExec = false, Func<string?>? tokenSource = null)
    {
        var host = new McpHttpHost(new McpHttpOptions(IPAddress.Loopback, 0, Token, [env.Workspace]) { AllowExec = allowExec, TokenSource = tokenSource });
        host.Start();
        await Task.CompletedTask;
        return host;
    }

    private static async Task<McpClient> SdkClientAsync(McpHttpHost host, string protocol = "2025-11-25", string? clientRoot = null,
        Action<McpClientOptions>? configure = null)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = host.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token },
        });
        var options = new McpClientOptions { ProtocolVersion = protocol, ClientInfo = new Implementation { Name = "http-sdk", Version = "1.0" } };
        if (clientRoot is not null)
        {
            options.Capabilities = new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } };
            options.Handlers.RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
            {
                Roots = [new Root { Uri = new Uri(clientRoot + "\\").AbsoluteUri, Name = "client" }],
            });
        }
        configure?.Invoke(options);
        return await McpClient.CreateAsync(transport, options, cancellationToken: Ct);
    }

    private static string TextOf(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static HttpRequestMessage Post(McpHttpHost host, string json, string? token = Token, string? session = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, host.Endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session is not null) req.Headers.Add("Mcp-Session-Id", session);
        return req;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unauthorized_Returns401()
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env);
        using var http = new HttpClient();

        using var noToken = await http.SendAsync(Post(host, InitializeBody, token: null), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Contains("Bearer", noToken.Headers.WwwAuthenticate.ToString());

        using var wrong = await http.SendAsync(Post(host, InitializeBody, token: Token + "x"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var basic = Post(host, InitializeBody, token: null);
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("a:" + Token)));
        using var basicResp = await http.SendAsync(basic, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, basicResp.StatusCode);
    }

    [Fact]
    public async Task WrongHostOrOrigin_Returns403_EvenWithToken()
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env);
        using var http = new HttpClient();

        // DNS rebinding: браузер обращается к 127.0.0.1, но с Host чужого домена.
        var rebound = Post(host, InitializeBody);
        rebound.Headers.Host = $"attacker.example:{host.Port}";
        using var r1 = await http.SendAsync(rebound, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);

        var wrongPort = Post(host, InitializeBody);
        wrongPort.Headers.Host = $"127.0.0.1:{host.Port + 1}";
        using var r2 = await http.SendAsync(wrongPort, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r2.StatusCode);

        var origin = Post(host, InitializeBody);
        origin.Headers.Add("Origin", "http://attacker.example");
        using var r3 = await http.SendAsync(origin, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r3.StatusCode);

        var nullOrigin = Post(host, InitializeBody);
        nullOrigin.Headers.Add("Origin", "null");
        using var r4 = await http.SendAsync(nullOrigin, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r4.StatusCode);

        // Локальный Origin с тем же портом допустим.
        var local = Post(host, InitializeBody);
        local.Headers.Host = $"localhost:{host.Port}";
        local.Headers.Add("Origin", $"http://localhost:{host.Port}");
        using var r5 = await http.SendAsync(local, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, r5.StatusCode);
    }

    [Fact]
    public async Task Initialize_OverRawHttp_CreatesSession_ThenToolsListAndDelete()
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env);
        using var http = new HttpClient();

        using var init = await http.SendAsync(Post(host, InitializeBody), Ct);
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Equal("text/event-stream", init.Content.Headers.ContentType?.MediaType);
        var session = Assert.Single(init.Headers.GetValues("Mcp-Session-Id"));
        Assert.Matches("^[0-9a-f]{32}$", session);
        var initText = await init.Content.ReadAsStringAsync(Ct);
        Assert.Contains("data:", initText);
        Assert.Contains($"\"name\":\"{AppInfo.McpServerId}\"", initText);
        Assert.Contains("\"protocolVersion\":\"2025-11-25\"", initText);

        using var notified = await http.SendAsync(Post(host, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", session: session), Ct);
        Assert.Equal(HttpStatusCode.Accepted, notified.StatusCode);

        var list = Post(host, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""", session: session);
        list.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        using var tools = await http.SendAsync(list, Ct);
        Assert.Equal(HttpStatusCode.OK, tools.StatusCode);
        Assert.Contains(McpToolNames.AskFiles, await tools.Content.ReadAsStringAsync(Ct));

        // Без Mcp-Session-Id (ревизия с initialize) — 400; с неизвестной сессией — 404.
        using var noSession = await http.SendAsync(Post(host, """{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}"""), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noSession.StatusCode);
        using var unknown = await http.SendAsync(Post(host, """{"jsonrpc":"2.0","id":4,"method":"tools/list","params":{}}""", session: "deadbeef"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var delete = new HttpRequestMessage(HttpMethod.Delete, host.Endpoint);
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        delete.Headers.Add("Mcp-Session-Id", session);
        using var deleted = await http.SendAsync(delete, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var after = await http.SendAsync(Post(host, """{"jsonrpc":"2.0","id":5,"method":"tools/list","params":{}}""", session: session), Ct);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task BadRequests_AreRejected()
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env);
        using var http = new HttpClient();

        var wrongPath = new HttpRequestMessage(HttpMethod.Post, new Uri(host.Endpoint, "/other")) { Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json") };
        wrongPath.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var r1 = await http.SendAsync(wrongPath, Ct);
        Assert.Equal(HttpStatusCode.NotFound, r1.StatusCode);

        var text = Post(host, InitializeBody);
        text.Content = new StringContent(InitializeBody, Encoding.UTF8, "text/plain");
        using var r2 = await http.SendAsync(text, Ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r2.StatusCode);

        using var r3 = await http.SendAsync(Post(host, "{not json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r3.StatusCode);

        using var r4 = await http.SendAsync(Post(host, "[" + InitializeBody + "]"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r4.StatusCode);

        var put = new HttpRequestMessage(HttpMethod.Put, host.Endpoint) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var r5 = await http.SendAsync(put, Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r5.StatusCode);

        // Слишком большое тело отклоняется до чтения.
        using var r6 = await RawAsync(host.Port,
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nAuthorization: Bearer {Token}\r\nContent-Type: application/json\r\nContent-Length: {HttpWire.MaxBodyBytes + 1}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 413", r6.StatusLine);

        // Повторный Host — попытка обойти проверку.
        using var r7 = await RawAsync(host.Port,
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nHost: attacker.example\r\nAuthorization: Bearer {Token}\r\nContent-Length: 0\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", r7.StatusLine);
    }

    [Fact]
    public async Task ChunkedRequestBody_IsAccepted()
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env);
        var body = Encoding.UTF8.GetBytes(InitializeBody);
        var half = body.Length / 2;
        var chunked = $"{half:x}\r\n{Encoding.UTF8.GetString(body, 0, half)}\r\n{body.Length - half:x}\r\n{Encoding.UTF8.GetString(body, half, body.Length - half)}\r\n0\r\n\r\n";
        using var r = await RawAsync(host.Port,
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nAuthorization: Bearer {Token}\r\nContent-Type: application/json\r\n" +
            $"Accept: application/json, text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n{chunked}");
        Assert.StartsWith("HTTP/1.1 200", r.StatusLine);
        Assert.Contains(AppInfo.McpServerId, r.Rest);
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task SdkClient_OverHttp_ListsAndCallsTools(string protocol)
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env);
        await using var client = await SdkClientAsync(host, protocol);
        Assert.Equal(protocol, client.NegotiatedProtocolVersion);
        Assert.Equal(AppInfo.McpServerId, client.ServerInfo.Name);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Contains(tools, t => t.Name == McpToolNames.Status);
        var status = await client.CallToolAsync(McpToolNames.Status, new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.NotEmpty(status.Content.OfType<TextContentBlock>());
    }

    [Fact]
    public void NonLoopbackBind_IsRejected()
    {
        string[] roots = [Path.GetTempPath()];
        Assert.Throws<ArgumentException>(() => new McpHttpHost(new McpHttpOptions(IPAddress.Any, 0, Token, roots)));
        Assert.Throws<ArgumentException>(() => new McpHttpHost(new McpHttpOptions(IPAddress.Parse("192.168.1.10"), 0, Token, roots)));
        Assert.Throws<ArgumentException>(() => new McpHttpHost(new McpHttpOptions(IPAddress.Loopback, 0, "short", roots)));
        // Без закреплённых корней HTTP-сервер не создаётся: корни клиента не принимаются.
        Assert.Throws<ArgumentException>(() => new McpHttpHost(new McpHttpOptions(IPAddress.Loopback, 0, Token, [])));
    }

    [Theory]
    [InlineData(new string[0], true, null)]
    [InlineData(new[] { "--mcp-http", "--port", "4100" }, true, 4100)]
    [InlineData(new[] { "--mcp-http", "--port", "0" }, false, null)]
    [InlineData(new[] { "--mcp-http", "--port", "70000" }, false, null)]
    [InlineData(new[] { "--mcp-http", "--port", "-5" }, false, null)]
    [InlineData(new[] { "--mcp-http", "--port" }, false, null)]
    public void PortArgument_Parsed(string[] args, bool ok, int? port)
    {
        Assert.Equal(ok, McpEntry.TryParsePort(args, out var p));
        if (ok) Assert.Equal(port, p);
    }

    [Fact]
    public async Task HttpCredentials_FromConfig_OrCreatedByTray()
    {
        using var env = new TestEnv(configure: c =>
        {
            c.Mcp.HttpToken = "";
            c.Mcp.HttpPort = 0;
        });
        var asked = 0;
        var ok = new Offload.Core.Ipc.IpcResponse(true);
        // Трея нет — токена нет: процесс не создаёт его сам и не пишет конфиг.
        var none = await McpEntry.EnsureHttpCredentialsAsync(() => { asked++; return Task.FromResult<Offload.Core.Ipc.IpcResponse?>(null); });
        Assert.Contains("not running", none.Error);
        Assert.Equal(1, asked);
        Assert.Equal("", Offload.Core.Config.ConfigStore.Reload().Mcp.HttpToken);

        // Трей старой версии не знает команду (отказ) — отдельный совет: обновить или перезапустить Offload.
        var old = await McpEntry.EnsureHttpCredentialsAsync(() => Task.FromResult<Offload.Core.Ipc.IpcResponse?>(
            new Offload.Core.Ipc.IpcResponse(false, "Неизвестная команда: ensure-mcp-http-token")));
        Assert.Contains("older version", old.Error);
        Assert.DoesNotContain("not running", old.Error);

        // Трей ответил, но токен так и не появился — ошибка.
        Assert.NotNull((await McpEntry.EnsureHttpCredentialsAsync(() => Task.FromResult<Offload.Core.Ipc.IpcResponse?>(ok))).Error);

        // Токен без порта (трей до случайных портов) — тоже ошибка с советом обновить.
        var cfg = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!;
        cfg["mcp"]!["httpToken"] = Token;
        File.WriteAllText(AppPaths.ConfigFile, cfg.ToJsonString());
        Assert.Contains("Update or restart", (await McpEntry.EnsureHttpCredentialsAsync(() => Task.FromResult<Offload.Core.Ipc.IpcResponse?>(ok))).Error);

        // Трей создал токен и порт (имитация: записали в config.json) — берём их, трей больше не спрашиваем.
        cfg["mcp"]!["httpPort"] = 31234;
        File.WriteAllText(AppPaths.ConfigFile, cfg.ToJsonString());
        var creds = await McpEntry.EnsureHttpCredentialsAsync(() => throw new InvalidOperationException("трей не нужен"));
        Assert.Null(creds.Error);
        Assert.Equal((Token, 31234), (creds.Token, creds.Port));
    }

    [Fact]
    public async Task ExtraHosts_FromConfig_AllowPortForwarding()
    {
        using var env = new TestEnv();
        Assert.Equal(["localhost:1234", "host.docker.internal:31234"], McpHttpHost.ValidExtraHosts(
            ["LocalHost:1234", " host.docker.internal:31234 ", "http://evil:1", "evil", "*:1", "a b:1", "x:70000", "", null, "localhost:1234"]));
        await using var host = new McpHttpHost(new McpHttpOptions(IPAddress.Loopback, 0, Token, [env.Workspace]) { ExtraHosts = ["localhost:1234"] });
        host.Start();
        using var http = new HttpClient();

        // ssh -L 1234:127.0.0.1:<порт> — клиент присылает Host: localhost:1234.
        var forwarded = Post(host, InitializeBody);
        forwarded.Headers.Host = "localhost:1234";
        forwarded.Headers.Add("Origin", "http://localhost:1234");
        using var r1 = await http.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        // Не из списка — 403; из списка, но без токена — 401.
        var other = Post(host, InitializeBody);
        other.Headers.Host = "localhost:1235";
        using var r2 = await http.SendAsync(other, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, r2.StatusCode);
        var noToken = Post(host, InitializeBody, token: null);
        noToken.Headers.Host = "localhost:1234";
        using var r3 = await http.SendAsync(noToken, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, r3.StatusCode);
    }

    [Fact]
    public void EnsureHttpToken_AlsoAssignsRandomPort()
    {
        var m = new Offload.Core.Config.McpSettings();
        Assert.True(m.EnsureHttpCredentials());
        Assert.Matches("^oft-[0-9a-f]{64}$", m.HttpToken);
        Assert.InRange(m.HttpPort, Offload.Core.Config.McpSettings.HttpPortMin, Offload.Core.Config.McpSettings.HttpPortMax);
        Assert.NotEqual(Offload.Core.Config.McpSettings.LegacyHttpPort, m.HttpPort);
        // Повторно ничего не меняется.
        var (token, port) = (m.HttpToken, m.HttpPort);
        Assert.False(m.EnsureHttpCredentials());
        Assert.Equal((token, port), (m.HttpToken, m.HttpPort));

        // Порт никогда не прежний фиксированный 39217 и разный у разных пользователей; занятые кандидаты пропускаются.
        var ports = Enumerable.Range(0, 300).Select(_ => Offload.Core.Config.McpSettings.NewHttpPort()).ToList();
        Assert.DoesNotContain(Offload.Core.Config.McpSettings.LegacyHttpPort, ports);
        Assert.True(ports.Distinct().Count() > 250, "порт должен быть случайным");
        var busy = new HashSet<int>();
        var chosen = Offload.Core.Config.McpSettings.NewHttpPort(p => { if (busy.Count < 3) { busy.Add(p); return false; } return true; });
        Assert.DoesNotContain(chosen, busy);

        // Ротация: новый токен, порт тот же.
        m.RotateHttpToken();
        Assert.NotEqual(token, m.HttpToken);
        Assert.Equal(port, m.HttpPort);
    }

    [Fact]
    public async Task McpHttp_ClientRootsIgnored_UsesPinnedRoot()
    {
        using var env = new TestEnv();
        env.WriteFile("pinned-marker.txt", "x");
        var other = Path.Combine(Path.GetTempPath(), "pc-client-root-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(other);
        try
        {
            File.WriteAllText(Path.Combine(other, "client-marker.txt"), "x");
            await using var host = await StartAsync(env);
            foreach (var protocol in new[] { "2025-11-25", "2026-07-28" })
            {
                // Клиент объявляет корень в другой папке — сервер его не спрашивает и работает в закреплённой.
                await using var client = await SdkClientAsync(host, protocol, clientRoot: other);
                var res = await client.CallToolAsync(McpToolNames.SearchCode,
                    new Dictionary<string, object?> { ["query"] = "marker", ["mode"] = "file" }, cancellationToken: Ct);
                var text = TextOf(res);
                Assert.Contains("pinned-marker.txt", text);
                Assert.DoesNotContain("client-marker.txt", text);
            }
        }
        finally
        {
            try { Directory.Delete(other, true); } catch { }
        }
    }

    [Fact]
    public async Task McpHttp_ExecTools_RefusedWithoutAllowExec()
    {
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["npm test*"]);
        env.WriteFile("package.json", """{"scripts":{"test":"calc"}}""");
        await using var host = await StartAsync(env);
        await using var client = await SdkClientAsync(host);

        // Инструменты, выполняющие код или пишущие файлы, не показываются…
        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        Assert.DoesNotContain(McpToolNames.Verify, tools);
        Assert.DoesNotContain(McpToolNames.WriteFile, tools);
        Assert.DoesNotContain(McpToolNames.AgentTask, tools);
        Assert.Contains(McpToolNames.SearchCode, tools);
        Assert.Contains(McpToolNames.Diagnostics, tools); // с log_path — только чтение

        // …и отклоняются, даже если клиент вызовет их по имени.
        foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
                 {
                     (McpToolNames.Verify, new() { ["command"] = "npm test" }),
                     (McpToolNames.WriteFile, new() { ["path"] = "a.txt", ["spec"] = "x" }),
                     (McpToolNames.ApplyPatch, new() { ["patch"] = "" }),
                     (McpToolNames.Diagnostics, new() { ["command"] = "npm test" }),
                     (McpToolNames.Diagnostics, new()),
                     (McpToolNames.Impact, new() { ["run_tests"] = true }),
                     (McpToolNames.Job, new() { ["action"] = "merge", ["job_id"] = "x" }),
                     (McpToolNames.Dependencies, new() { ["action"] = "outdated" }),
                 })
        {
            var res = await client.CallToolAsync(tool, args, cancellationToken: Ct);
            Assert.True(res.IsError, tool);
            Assert.Contains("--allow-exec", TextOf(res));
        }
        Assert.False(File.Exists(env.PathOf("a.txt")));
        Assert.False(Directory.Exists(env.PathOf(".offload/runs")));

        // Чтение работает как обычно.
        var ok = await client.CallToolAsync(McpToolNames.SearchCode, new Dictionary<string, object?> { ["query"] = "scripts" }, cancellationToken: Ct);
        Assert.NotEqual(true, ok.IsError);
    }

    [Fact]
    public async Task McpHttp_OneTimeAllow_NeverOffered_EvenWithAllowExec()
    {
        // По HTTP на elicitation может отвечать агент-клиент: «разрешить один раз» не предлагается вовсе.
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["sort /r*"]);
        env.WriteFile("a.txt", "b\na\n");
        await using var host = await StartAsync(env, allowExec: true);
        var asked = 0;
        await using var client = await SdkClientAsync(host, configure: o =>
        {
            o.Capabilities ??= new ClientCapabilities();
            o.Capabilities.Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() };
            o.Handlers.ElicitationHandler = (_, _) =>
            {
                Interlocked.Increment(ref asked);
                return ValueTask.FromResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, System.Text.Json.JsonElement> { ["confirm"] = System.Text.Json.JsonSerializer.SerializeToElement(true) },
                });
            };
        });
        var res = await client.CallToolAsync(McpToolNames.Verify, new Dictionary<string, object?> { ["command"] = "sort a.txt", ["analyze"] = false }, cancellationToken: Ct);
        Assert.True(res.IsError);
        Assert.Contains("not in the Offload allowlist", TextOf(res));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task McpHttp_AllowExec_ListsExecTools()
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env, allowExec: true);
        await using var client = await SdkClientAsync(host);
        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        Assert.Contains(McpToolNames.Verify, tools);
        Assert.Contains(McpToolNames.WriteFile, tools);
    }

    [Theory]
    [InlineData("local_verify", "{}", true)]
    [InlineData("local_refactor", "{}", true)]
    [InlineData("local_diagnostics", """{"log_path":"build.log"}""", false)]
    [InlineData("local_diagnostics", """{"log_path":"build.log","command":"dotnet build"}""", true)]
    [InlineData("local_diagnostics", """{"kind":"build"}""", true)]
    [InlineData("local_impact", """{"run_tests":false}""", false)]
    [InlineData("local_impact", """{"run_tests":"TRUE"}""", true)]
    [InlineData("local_job", """{"action":"diff"}""", false)]
    [InlineData("local_job", """{"action":" Merge "}""", true)]
    [InlineData("local_job", """{"action":"retry"}""", true)]
    [InlineData("local_job", """{"action":"revert"}""", true)]
    [InlineData("local_dependency_check", """{"action":"list"}""", false)]
    [InlineData("local_dependency_check", """{"action":"vulnerable"}""", true)]
    [InlineData("local_search_code", """{"query":"x"}""", false)]
    [InlineData("local_memory", """{"action":"store","text":"x"}""", false)]
    public void HttpExecPolicy_RefusesCodeExecution(string tool, string argsJson, bool refused)
    {
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(argsJson)!;
        Assert.Equal(refused, HttpExecPolicy.Refusal(tool, args) is not null);
    }

    [Fact]
    public void RootArguments_ParsedAndValidated()
    {
        using var env = new TestEnv();
        Assert.Equal([env.Workspace, "b"], McpEntry.ParseRootArgs(["--mcp-http", "--root", env.Workspace, "--port", "1", "--root", "b"]));
        Assert.Equal([""], McpEntry.ParseRootArgs(["--mcp-http", "--root", "--allow-exec"]));
        Assert.Empty(McpEntry.ParseRootArgs(["--mcp-http"]));

        Assert.Equal([env.Workspace], Workspace.PinRoots([env.Workspace, env.Workspace + "\\"]));
        Assert.Throws<ArgumentException>(() => Workspace.PinRoots([""]));
        Assert.Throws<ArgumentException>(() => Workspace.PinRoots([Path.Combine(env.Workspace, "missing")]));
        Assert.Throws<ArgumentException>(() => Workspace.PinRoots([Path.GetPathRoot(env.Workspace)!]));
        Assert.Throws<ArgumentException>(() => Workspace.PinRoots([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]));
        Assert.Throws<ArgumentException>(() => Workspace.PinRoots([AppPaths.DataDir]));
        Assert.Throws<ArgumentException>(() => Workspace.PinRoots([@"\\server\share\proj"]));
    }

    [Fact]
    public async Task RequestTarget_WithBareLineFeed_IsRejected()
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env);
        using var r = await RawAsync(host.Port,
            $"POST /mcp\nX HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nAuthorization: Bearer {Token}\r\nContent-Length: 0\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", r.StatusLine);
        using var r2 = await RawAsync(host.Port,
            $"GET /mcp\u007f HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nAuthorization: Bearer {Token}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", r2.StatusLine);
    }

    [Fact]
    public async Task Unauthorized_BodyIsNotRead()
    {
        using var env = new TestEnv();
        await using var host = await StartAsync(env);
        // Без токена: 401 сразу по заголовкам, хотя тело объявлено на 4 МиБ и не отправлено (иначе ждали бы его до таймаута).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var r = await RawAsync(host.Port,
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nContent-Type: application/json\r\nContent-Length: {HttpWire.MaxBodyBytes}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 401", r.StatusLine);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"ответ за {sw.Elapsed}");

        // Медленные заголовки: соединение закрывается по таймауту заголовков (5 с), а не держится.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port, Ct);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\n"), Ct);
        var buf = new byte[64];
        var closed = await stream.ReadAsync(buf, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task TokenRotation_NewTokenAcceptedOldRejected()
    {
        using var env = new TestEnv();
        const string rotated = "oft-rotated-token-abcdef0123456789";
        var current = Token;
        await using var host = await StartAsync(env, tokenSource: () => current);
        using var http = new HttpClient();

        using var before = await http.SendAsync(Post(host, InitializeBody), HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        current = rotated;
        await Task.Delay(TimeSpan.FromSeconds(2.2), Ct);
        using var old = await http.SendAsync(Post(host, InitializeBody), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var fresh = await http.SendAsync(Post(host, InitializeBody, token: rotated), HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        // Пустой токен в конфиге не открывает доступ и не отменяет действующий.
        current = "";
        await Task.Delay(TimeSpan.FromSeconds(2.2), Ct);
        using var stillFresh = await http.SendAsync(Post(host, InitializeBody, token: rotated), HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, stillFresh.StatusCode);
    }

    [Fact]
    public void NewHttpToken_IsLongAndRandom()
    {
        var a = Offload.Core.Config.McpSettings.NewHttpToken();
        var b = Offload.Core.Config.McpSettings.NewHttpToken();
        Assert.NotEqual(a, b);
        Assert.Matches("^oft-[0-9a-f]{64}$", a);
    }

    private sealed record RawResponse(string StatusLine, string Rest) : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static async Task<RawResponse> RawAsync(int port, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);
        using var ms = new MemoryStream();
        try { await stream.CopyToAsync(ms, Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct); }
        catch (IOException) { }
        var text = Encoding.UTF8.GetString(ms.ToArray());
        var nl = text.IndexOf("\r\n", StringComparison.Ordinal);
        return nl < 0 ? new RawResponse(text, "") : new RawResponse(text[..nl], text[(nl + 2)..]);
    }
}
