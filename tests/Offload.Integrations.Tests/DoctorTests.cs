using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Core.Usage;
using Offload.Integrations.Clients;

namespace Offload.Integrations.Tests;

/// <summary>Сервер MCP в памяти: два анонимных канала вместо stdin/stdout процесса.</summary>
internal sealed class FakeMcpServer : IDisposable
{
    private readonly AnonymousPipeServerStream _toServerWrite = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _toServerRead;
    private readonly AnonymousPipeServerStream _fromServerWrite = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _fromServerRead;
    private readonly StreamWriter _out;
    private readonly Task _loop;

    /// <summary>Всё, что сервер получил от клиента.</summary>
    public List<JsonObject> Received { get; } = [];

    public Stream ToServer => _toServerWrite;
    public Stream FromServer => _fromServerRead;

    /// <param name="handler">Сообщение клиента → строки ответа (null — закрыть соединение).</param>
    public FakeMcpServer(Func<JsonObject, IEnumerable<string>?> handler)
    {
        _toServerRead = new AnonymousPipeClientStream(PipeDirection.In, _toServerWrite.ClientSafePipeHandle);
        _fromServerRead = new AnonymousPipeClientStream(PipeDirection.In, _fromServerWrite.ClientSafePipeHandle);
        _out = new StreamWriter(_fromServerWrite, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _loop = Task.Run(() =>
        {
            using var reader = new StreamReader(_toServerRead, new UTF8Encoding(false));
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    var msg = (JsonObject)JsonNode.Parse(line)!;
                    lock (Received) Received.Add(msg);
                    var reply = handler(msg);
                    if (reply is null)
                    {
                        _out.Dispose();
                        return;
                    }
                    foreach (var l in reply) _out.WriteLine(l);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Клиент закрыл канал.
            }
        });
    }

    public static string Result(JsonNode? id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString();

    public void Dispose()
    {
        try { _out.Dispose(); } catch (IOException) { }
        _toServerWrite.Dispose();
        _fromServerRead.Dispose();
        _toServerRead.Dispose();
        _fromServerWrite.Dispose();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }
}

public class McpHandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Handshake_PaginatesTools_AnswersServerRequests_CountsNonJson()
    {
        using var server = new FakeMcpServer(msg => (string?)msg["method"] switch
        {
            "initialize" =>
            [
                "log noise that breaks IDEs",
                """{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info","data":"hi"}}""",
                """{"jsonrpc":"2.0","id":"srv-1","method":"roots/list"}""",
                FakeMcpServer.Result(msg["id"], new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["serverInfo"] = new JsonObject { ["name"] = "offload", ["version"] = "1.2.3" },
                }),
            ],
            "tools/list" when msg["params"]?["cursor"] is null =>
            [
                FakeMcpServer.Result(msg["id"], new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject { ["name"] = "a" }, new JsonObject { ["name"] = "b" }),
                    ["nextCursor"] = "p2",
                }),
            ],
            "tools/list" => [FakeMcpServer.Result(msg["id"], new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "c" }) })],
            _ => [],
        });

        var r = await McpHandshake.RunAsync(server.ToServer, server.FromServer, Timeout, TestContext.Current.CancellationToken);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("offload", r.ServerName);
        Assert.Equal("1.2.3", r.ServerVersion);
        Assert.Equal("2025-06-18", r.ProtocolVersion);
        Assert.Equal(3, r.ToolCount);
        Assert.Equal(1, r.NonJsonLines);
        lock (server.Received)
        {
            var init = server.Received[0];
            Assert.Equal("initialize", (string?)init["method"]);
            Assert.Equal(McpHandshake.ClientName, (string?)init["params"]!["clientInfo"]!["name"]);
            Assert.Contains(server.Received, m => m["id"]?.ToJsonString() == "\"srv-1\"" && m["result"]?["roots"] is JsonArray);
            Assert.Contains(server.Received, m => (string?)m["method"] == "notifications/initialized");
        }
    }

    [Fact]
    public async Task Handshake_InitializeError_Fails()
    {
        using var server = new FakeMcpServer(msg =>
        [
            new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = msg["id"]?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Unsupported protocol version" },
            }.ToJsonString(),
        ]);
        var r = await McpHandshake.RunAsync(server.ToServer, server.FromServer, Timeout, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Contains("Unsupported protocol version", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_ServerClosesPipe_Fails()
    {
        using var server = new FakeMcpServer(_ => null);
        var r = await McpHandshake.RunAsync(server.ToServer, server.FromServer, Timeout, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Contains("закрыл", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_SilentServer_TimesOut()
    {
        using var server = new FakeMcpServer(_ => []);
        var r = await McpHandshake.RunAsync(server.ToServer, server.FromServer, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Contains("не ответил", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunProcess_MissingExe_ReportsStartFailure()
    {
        using var sb = new Sandbox();
        var r = await McpHandshake.RunProcessAsync(sb.P("nope", "Offload.exe"), [AppInfo.McpArg], Timeout, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Contains("не удалось запустить", r.Error, StringComparison.Ordinal);
    }

    /// <summary>Живая проверка с настоящим Offload.exe: OFFLOAD_LIVE=1 и OFFLOAD_TEST_OFFLOAD_EXE=путь к exe (режим --mcp безопасен).</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task RunProcess_RealOffloadExe_Live()
    {
        var exe = Environment.GetEnvironmentVariable("OFFLOAD_TEST_OFFLOAD_EXE");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OFFLOAD_LIVE") == "1" && File.Exists(exe), "нужны OFFLOAD_LIVE=1 и OFFLOAD_TEST_OFFLOAD_EXE");
        using var sb = new Sandbox();
        // Дочерний сервер пишет журнал и чистит задачи в своей папке данных — пусть это будет песочница.
        var savedHome = Environment.GetEnvironmentVariable("OFFLOAD_HOME");
        Environment.SetEnvironmentVariable("OFFLOAD_HOME", sb.Dir("offload-home"));
        HandshakeResult r;
        try
        {
            r = await McpHandshake.RunProcessAsync(exe!, [AppInfo.McpArg], TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OFFLOAD_HOME", savedHome);
        }
        Assert.True(r.Ok, r.Error);
        Assert.Equal(AppInfo.McpServerId, r.ServerName);
        Assert.Equal(McpToolNames.All.Count, r.ToolCount);
        Assert.Equal(0, r.NonJsonLines);
    }

    [Fact]
    public async Task RunProcess_NotAnMcpServer_Fails()
    {
        // hostname.exe печатает имя компьютера (не JSON) и завершается — как сломанный сервер.
        var exe = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        Assert.SkipUnless(File.Exists(exe), "нет hostname.exe");
        var r = await McpHandshake.RunProcessAsync(exe, [], Timeout, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        // В зависимости от того, успел ли процесс завершиться до записи initialize: «закрыл соединение» или «обмен прерван».
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.True(r.NonJsonLines <= 1);
    }
}

public class IntegrationDoctorTests
{
    private static JsonIntegration Client(Sandbox sb, bool installed = true) =>
        new("vscode", "Test IDE", null)
        {
            Detect = () => installed,
            Targets = () => [sb.P("ide", "mcp.json")],
            Container = ["servers"],
            Entry = spec => new JsonObject { ["command"] = spec.Command, ["args"] = JsonIntegration.Strings(spec.Args) },
        };

    private static string WriteEntry(Sandbox sb, string command) =>
        sb.Write(sb.P("ide", "mcp.json"),
            "{\"servers\":{\"offload\":{\"command\":" + JsonValue.Create(command).ToJsonString() + ",\"args\":[\"--mcp\"]}}}");

    [Fact]
    public async Task NotInstalled_OnlyInfo()
    {
        using var sb = new Sandbox();
        var report = await IntegrationDoctor.CheckAsync(Client(sb, installed: false), ct: TestContext.Current.CancellationToken);
        Assert.Null(report.Handshake);
        Assert.Equal(DoctorLevel.Info, Assert.Single(report.Checks).Level);
    }

    [Fact]
    public async Task MissingFile_AbsentEntry_ForeignEntry()
    {
        using var sb = new Sandbox();
        var client = Client(sb);
        var ct = TestContext.Current.CancellationToken;

        var missing = await IntegrationDoctor.CheckAsync(client, ct: ct);
        Assert.Equal(DoctorLevel.Error, missing.Worst);
        Assert.Contains(missing.Checks, c => c.Level == DoctorLevel.Error && c.Text.Contains("не найден", StringComparison.Ordinal));

        sb.Write(sb.P("ide", "mcp.json"), "{\"servers\":{}}");
        var absent = await IntegrationDoctor.CheckAsync(client, ct: ct);
        Assert.Contains(absent.Checks, c => c.Level == DoctorLevel.Error && c.Text.Contains("записи", StringComparison.Ordinal));

        WriteEntry(sb, @"C:\Tools\other-server.exe");
        var foreign = await IntegrationDoctor.CheckAsync(client, ct: ct);
        Assert.Null(foreign.Handshake); // чужую программу не запускаем
        Assert.Contains(foreign.Checks, c => c.Level == DoctorLevel.Warning && c.Text.Contains("other-server.exe", StringComparison.Ordinal));

        // Подсказки о подводных камнях VS Code и «последнее обращение».
        Assert.Contains(foreign.Checks, c => c.Level == DoctorLevel.Info && c.Text.Contains("VS Code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OurEntry_MissingExe_IsError()
    {
        using var sb = new Sandbox();
        WriteEntry(sb, sb.Spec().Command);
        var report = await IntegrationDoctor.CheckAsync(Client(sb), ct: TestContext.Current.CancellationToken);
        Assert.Null(report.Handshake);
        Assert.Contains(report.Checks, c => c.Level == DoctorLevel.Ok && c.Text.Contains("запись Offload найдена", StringComparison.Ordinal));
        Assert.Contains(report.Checks, c => c.Level == DoctorLevel.Error && c.Text.Contains(sb.Spec().Command, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OurEntry_BrokenExe_HandshakeFails()
    {
        var hostname = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        Assert.SkipUnless(File.Exists(hostname), "нет hostname.exe");
        using var sb = new Sandbox();
        var exe = sb.P("bin", "Offload.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.Copy(hostname, exe);
        WriteEntry(sb, exe);
        var lastCalls = new Dictionary<string, DateTime> { ["vscode"] = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc) };

        // Запускается только известная копия Offload (здесь — «установленная»).
        var report = await IntegrationDoctor.CheckAsync(Client(sb), lastCalls, [exe], TestContext.Current.CancellationToken);

        Assert.NotNull(report.Handshake);
        Assert.False(report.Handshake.Ok);
        Assert.Equal(DoctorLevel.Error, report.Worst);
        Assert.Contains(report.Checks, c => c.Level == DoctorLevel.Error && c.Text.Contains("MCP", StringComparison.Ordinal));
        Assert.Equal(lastCalls["vscode"], report.LastCallUtc);
    }

    private static void WriteEntry(Sandbox sb, string command, params string[] args) =>
        sb.Write(sb.P("ide", "mcp.json"),
            "{\"servers\":{\"offload\":{\"command\":" + JsonValue.Create(command).ToJsonString() + ",\"args\":" +
            JsonIntegration.Strings(args).ToJsonString() + "}}}");

    private static string FakeOffload(Sandbox sb, params string[] parts)
    {
        var hostname = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        Assert.SkipUnless(File.Exists(hostname), "нет hostname.exe");
        var exe = sb.P(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.Copy(hostname, exe);
        return exe;
    }

    [Fact]
    public async Task OffloadNamedExe_NotCurrentOrInstalled_IsNotLaunched()
    {
        using var sb = new Sandbox();
        // Файл с именем Offload.exe где-то на диске — ещё не Offload: имя файла не основание для запуска.
        var exe = FakeOffload(sb, "Downloads", "Offload.exe");
        WriteEntry(sb, exe, AppInfo.McpArg);

        var report = await IntegrationDoctor.CheckAsync(Client(sb), ct: TestContext.Current.CancellationToken);

        Assert.Null(report.Handshake);
        Assert.Contains(report.Checks, c => c.Level == DoctorLevel.Warning && c.Text.Contains("не выполнялась", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicFolderExe_IsNotLaunched()
    {
        using var sb = new Sandbox();
        WriteEntry(sb, @"C:\Users\Public\Offload.exe", AppInfo.McpArg);
        var report = await IntegrationDoctor.CheckAsync(Client(sb), ct: TestContext.Current.CancellationToken);
        Assert.Null(report.Handshake);
        Assert.False(IntegrationDoctor.MayLaunch(new EntryInfo(@"C:\Users\Public\Offload.exe", [AppInfo.McpArg]), sb.Spec(), [sb.Spec("x").Command]));
    }

    [Fact]
    public async Task UncPath_RejectedBeforeFileSystemAccess()
    {
        using var sb = new Sandbox();
        const string unc = @"\\evil\s\Offload.exe";
        WriteEntry(sb, unc, AppInfo.McpArg);

        var report = await IntegrationDoctor.CheckAsync(Client(sb), null, [unc], TestContext.Current.CancellationToken);

        Assert.Null(report.Handshake);
        Assert.Contains(report.Checks, c => c.Level == DoctorLevel.Warning && c.Text.Contains("сетевой путь", StringComparison.Ordinal));
        // Существование файла не проверялось (иначе было бы «не найдена»).
        Assert.DoesNotContain(report.Checks, c => c.Text.Contains("не найдена", StringComparison.Ordinal));
        Assert.False(IntegrationDoctor.MayLaunch(new EntryInfo(unc, [AppInfo.McpArg]), new McpServerSpec("offload", unc, [AppInfo.McpArg], new Dictionary<string, string>()), [unc]));
        Assert.False(IntegrationDoctor.MayLaunch(new EntryInfo("//evil/s/Offload.exe", [AppInfo.McpArg]), sb.Spec(), null));
        Assert.True(Editing.CommandPath.IsUnc("\"\\\\evil\\s\\Offload.exe\""));
        Assert.False(Editing.CommandPath.IsMissingLocalFile(unc)); // сетевой путь «не проверяем», а не «отсутствует»

        var direct = await McpHandshake.RunProcessAsync(unc, [AppInfo.McpArg], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(direct.Ok);
        Assert.Contains("сетевой путь", direct.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedExe_WithOtherArgs_IsNotLaunched()
    {
        using var sb = new Sandbox();
        var exe = FakeOffload(sb, "Program Files", "Offload", "Offload.exe");
        WriteEntry(sb, exe, "--uninstall-cleanup");

        var report = await IntegrationDoctor.CheckAsync(Client(sb), null, [exe], TestContext.Current.CancellationToken);

        Assert.Null(report.Handshake);
        Assert.Contains(report.Checks, c => c.Level == DoctorLevel.Warning && c.Text.Contains("--uninstall-cleanup", StringComparison.Ordinal));
        Assert.False(IntegrationDoctor.MayLaunch(new EntryInfo(exe, [AppInfo.McpArg, "--x"]), sb.Spec(), [exe]));
        Assert.False(IntegrationDoctor.MayLaunch(new EntryInfo(exe, []), sb.Spec(), [exe]));
        Assert.True(IntegrationDoctor.MayLaunch(new EntryInfo(exe.ToUpperInvariant().Replace('\\', '/'), [AppInfo.McpArg]), sb.Spec(), [exe]));
        Assert.True(IntegrationDoctor.MayLaunch(new EntryInfo(sb.Spec().Command, [AppInfo.McpArg]), sb.Spec(), null));
    }

    [Fact]
    public void Describe_OkHandshake()
    {
        var checks = IntegrationDoctor.Describe(new HandshakeResult(true, "offload", "1.0.0", "2025-06-18", 25, TimeSpan.FromMilliseconds(420), null, 0)).ToList();
        var ok = Assert.Single(checks);
        Assert.Equal(DoctorLevel.Ok, ok.Level);
        Assert.Contains("25", ok.Text, StringComparison.Ordinal);

        var noisy = IntegrationDoctor.Describe(new HandshakeResult(true, "offload", "1.0.0", null, 0, TimeSpan.Zero, null, 2)).ToList();
        Assert.Equal(3, noisy.Count);
        Assert.Equal(2, noisy.Count(c => c.Level == DoctorLevel.Error));
    }

    [Theory]
    [InlineData("claude-code", "claude-code")]
    [InlineData("claude-ai", "claude-desktop")]
    [InlineData("cursor-vscode", "cursor")]
    [InlineData("Visual Studio Code", "vscode")]
    [InlineData("Visual Studio Code - Insiders", "vscode-insiders")]
    [InlineData("Microsoft Visual Studio", "visual-studio")]
    [InlineData("Roo Code", "roo-code")]
    [InlineData("Cline", "cline")]
    [InlineData("Kilo Code", "kilo-code")]
    [InlineData("codex-mcp-client", "codex")]
    [InlineData("gemini-cli-mcp-client", "gemini-cli")]
    [InlineData("Zed", "zed")]
    [InlineData("windsurf-client", "windsurf")]
    [InlineData("github-copilot-cli", "copilot-cli")]
    [InlineData("continue-client", "continue")]
    [InlineData("offload-doctor", null)]
    [InlineData("offload-smoke", null)]
    [InlineData("(unknown)", null)]
    [InlineData("some-new-agent", null)]
    [InlineData(null, null)]
    public void ClientNames_Map(string? name, string? expected) => Assert.Equal(expected, ClientNames.ToIntegrationId(name));

    [Fact]
    public void LastCalls_MaxPerIntegration()
    {
        var t1 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(5);
        var map = IntegrationDoctor.LastCallsByIntegration(
        [
            new UsageRecord(t1, "local_status", "claude-code", 0, 0, 1, true),
            new UsageRecord(t2, "local_status", "claude-code", 0, 0, 1, true),
            new UsageRecord(t2, "local_status", "offload-smoke", 0, 0, 1, true),
            new UsageRecord(t1, "local_status", "cursor-vscode", 0, 0, 1, false),
        ]);
        Assert.Equal(2, map.Count);
        Assert.Equal(t2, map["claude-code"]);
        Assert.Equal(t1, map["cursor"]);
    }
}

