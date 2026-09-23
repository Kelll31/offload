using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core;
using Offload.Core.Usage;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

/// <summary>Сервер в процессе поверх пары каналов + клиент SDK (с roots) + поддельный llama-server.</summary>
internal sealed class McpHarness : IAsyncDisposable
{
    private readonly Pipe _c2s = new();
    private readonly Pipe _s2c = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ServiceProvider _sp;
    private readonly Task _serverTask;

    public McpClient Client { get; private set; } = null!;
    public SessionState State { get; } = new() { TrayLauncherOverride = () => false };
    public List<string> Progress { get; } = [];

    private McpHarness()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        McpEntry.AddOffloadServer(services, State).WithStreamServerTransport(_c2s.Reader.AsStream(), _s2c.Writer.AsStream());
        _sp = services.BuildServiceProvider();
        _serverTask = _sp.GetRequiredService<McpServer>().RunAsync(_cts.Token);
    }

    public static async Task<McpHarness> StartAsync(string workspace)
    {
        var h = new McpHarness();
        var options = new McpClientOptions
        {
            ProtocolVersion = "2025-11-25",
            ClientInfo = new Implementation { Name = "harness", Version = "1.0" },
            Capabilities = new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } },
        };
        options.Handlers.RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
        {
            Roots = [new Root { Uri = new Uri(workspace + "\\").AbsoluteUri, Name = "ws" }],
        });
        h.Client = await McpClient.CreateAsync(new StreamClientTransport(h._c2s.Writer.AsStream(), h._s2c.Reader.AsStream()), options);
        return h;
    }

    public async Task<(bool IsError, string Text)> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var progress = new Progress<ProgressNotificationValue>(v => { lock (Progress) Progress.Add($"{v.Progress}:{v.Message}"); });
        var res = await Client.CallToolAsync(tool, args, progress);
        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text));
        return (res.IsError == true, text);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisposeAsync(); } catch { }
        _cts.Cancel();
        _c2s.Writer.Complete();
        _s2c.Writer.Complete();
        try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await _sp.DisposeAsync();
    }
}

[Collection("AppPaths")]
public class EndToEndTests
{
    [Fact]
    public async Task ToolsList_AnnotationsMetaAndInstructions()
    {
        using var env = new TestEnv();
        await using var h = await McpHarness.StartAsync(env.Workspace);
        Assert.Equal("offload", h.Client.ServerInfo.Name);
        var instructions = h.Client.ServerInstructions ?? "";
        Assert.InRange(instructions.Length, 500, 2048);
        Assert.Contains("TestCoder 7B Q4_K_M", instructions);

        var tools = (await h.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToDictionary(t => t.Name);
        Assert.Equal(McpToolNames.All.OrderBy(x => x), tools.Keys.OrderBy(x => x));
        foreach (var t in tools.Values)
        {
            var a = t.Annotations!;
            Assert.NotNull(a.ReadOnlyHint);
            Assert.NotNull(a.DestructiveHint);
            Assert.NotNull(a.IdempotentHint);
            Assert.Equal(t.Name == McpToolNames.Dependencies, a.OpenWorldHint);
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.InRange(t.Description!.Length, 150, 1000);
            Assert.True(t.Meta?["anthropic/searchHint"] is not null, t.Name);
            Assert.Equal(McpToolNames.ReadOnly.Contains(t.Name), a.ReadOnlyHint);
            // Схема: служебные параметры (контекст, токен отмены) не видны модели.
            var schema = t.InputSchema.GetRawText();
            Assert.DoesNotContain("cancellationToken", schema);
            Assert.DoesNotContain("\"context\"", schema);
        }
        Assert.True(tools[McpToolNames.AskFiles].Meta!["anthropic/alwaysLoad"]!.GetValue<bool>());
        Assert.True(tools[McpToolNames.Status].Meta!["anthropic/alwaysLoad"]!.GetValue<bool>());
        Assert.Null(tools[McpToolNames.WriteFile].Meta!["anthropic/alwaysLoad"]);
        Assert.True(tools[McpToolNames.EditFiles].Annotations!.DestructiveHint);
        Assert.False(tools[McpToolNames.WriteFile].Annotations!.DestructiveHint);
        Assert.Contains("\"paths\"", tools[McpToolNames.AskFiles].InputSchema.GetRawText());
        Assert.True(tools[McpToolNames.AgentTask].Annotations!.DestructiveHint);
        Assert.Contains("\"background\"", tools[McpToolNames.AgentTask].InputSchema.GetRawText());

        // Подсказки (слэш-команды Claude Code) ссылаются на инструменты по полным именам.
        var prompts = await h.Client.ListPromptsAsync();
        Assert.Equal(OffloadPrompts.All.OrderBy(x => x), prompts.Select(p => p.Name).OrderBy(x => x));
        var delegatePrompt = await h.Client.GetPromptAsync(OffloadPrompts.Delegate,
            new Dictionary<string, object?> { ["task"] = "Add a Sum method", ["verify_command"] = "dotnet test" });
        var text = string.Join("\n", delegatePrompt.Messages.Select(m => (m.Content as ModelContextProtocol.Protocol.TextContentBlock)?.Text));
        Assert.Contains("Add a Sum method", text);
        Assert.Contains(McpToolNames.ClaudeCodeName(McpToolNames.AgentTask), text);
        Assert.Contains("\"dotnet test\"", text);
    }

    [Fact]
    public async Task AskFiles_ReadsFilesServerSide_AndReportsCoverage()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        env.WriteFile("src/Calc.cs", "public class Calc\n{\n    public int Add(int a, int b) => a + b;\n}\n");
        env.WriteFile("src/.env", "API_KEY=topsecret\n");
        llama.Responder = req =>
        {
            var user = FakeLlamaServer.UserText(req);
            return user.Contains("3|     public int Add") ? "<think>internal musing</think>Add is defined at src/Calc.cs:3." : "not found";
        };
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.AskFiles, new()
        {
            ["paths"] = new[] { "src" },
            ["question"] = "Where is Add defined?",
        });
        Assert.False(isError, text);
        Assert.StartsWith("Add is defined at src/Calc.cs:3.", text);
        Assert.DoesNotContain("musing", text);
        Assert.Contains("coverage: 1/2 files", text);
        Assert.Contains("src/.env (secret)", text);
        Assert.Contains("offload · TestCoder 7B Q4_K_M · read 1 file", text);
        Assert.Contains("cloud tokens avoided", text);

        var body = Assert.Single(llama.Requests);
        Assert.DoesNotContain("topsecret", body);
        Assert.Contains("\"enable_thinking\":false", body);
        Assert.Contains("\"stream\":true", body);

        var usage = UsageLog.ReadAll();
        var rec = Assert.Single(usage);
        Assert.Equal(McpToolNames.AskFiles, rec.Tool);
        Assert.Equal("harness", rec.Client);
        Assert.True(rec.Ok);
        Assert.True(h.State.ModelCalls == 1);
    }

    [Fact]
    public async Task AskFiles_MapReduceWhenMaterialExceedsContext()
    {
        using var llama = new FakeLlamaServer { ContextSize = 2048 };
        using var env = new TestEnv(llama.Port);
        for (var i = 0; i < 4; i++)
            env.WriteFile($"m{i}.txt", string.Concat(Enumerable.Range(0, 120).Select(n => $"file {i} line {n} with some filler text\n")));
        llama.Responder = req =>
        {
            var user = FakeLlamaServer.UserText(req);
            if (user.StartsWith("PARTIAL ANSWERS")) return "MERGED ANSWER";
            return user.Contains("file 2 line 7 ") ? "found in m2.txt:8" : "NOTHING RELEVANT";
        };
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.AskFiles, new()
        {
            ["paths"] = new[] { "*.txt" },
            ["question"] = "Where is line 7 of file 2?",
            ["max_answer_tokens"] = 200,
        });
        Assert.False(isError, text);
        Assert.Contains("found in m2.txt:8", text);
        Assert.Contains("parts and merged", text);
        Assert.True(llama.Requests.Count > 2);
        Assert.NotEmpty(h.Progress);
    }

    [Fact]
    public async Task WriteFile_CreatesFile_JobDiffAndRevert()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        env.WriteFile("src/Calc.cs", "namespace Demo;\r\npublic class Calc { public int Add(int a, int b) => a + b; }\r\n");
        llama.Responder = _ => "Here you go:\n```csharp\nnamespace Demo.Tests;\npublic class CalcTests\n{\n    // test\n}\n```";
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.WriteFile, new()
        {
            ["path"] = "tests/CalcTests.cs",
            ["task"] = "Write xUnit tests for Calc.Add",
            ["context_paths"] = new[] { "src/Calc.cs" },
        });
        Assert.False(isError, text);
        var file = env.PathOf("tests/CalcTests.cs");
        Assert.True(File.Exists(file));
        // Переводы строк — как у справочного файла того же типа (CRLF), ограда ``` снята.
        Assert.Equal("namespace Demo.Tests;\r\npublic class CalcTests\r\n{\r\n    // test\r\n}\r\n", File.ReadAllText(file));
        Assert.Contains("wrote tests/CalcTests.cs (5 lines", text);
        Assert.DoesNotContain("public class CalcTests", text); // код не возвращается в контекст IDE
        var jobId = System.Text.RegularExpressions.Regex.Match(text, @"job_id=(\S+)").Groups[1].Value;

        var (_, status) = await h.CallAsync(McpToolNames.Job, new() { ["job_id"] = jobId, ["action"] = "status" });
        Assert.Contains("status applied", status);
        var (_, diff) = await h.CallAsync(McpToolNames.Job, new() { ["job_id"] = jobId, ["action"] = "diff" });
        Assert.Contains("+public class CalcTests", diff);

        // Повторная запись без overwrite — отказ.
        var (err2, text2) = await h.CallAsync(McpToolNames.WriteFile, new() { ["path"] = "tests/CalcTests.cs", ["task"] = "again" });
        Assert.True(err2);
        Assert.Contains("already exists", text2);

        var (revErr, rev) = await h.CallAsync(McpToolNames.Job, new() { ["job_id"] = jobId, ["action"] = "revert" });
        Assert.False(revErr, rev);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task WriteFile_VerifyFailure_FixLoop()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port, configure: c => c.Mcp.VerifyCommandAllowlist = ["findstr *"]);
        var calls = 0;
        llama.Responder = req =>
        {
            calls++;
            return FakeLlamaServer.UserText(req).Contains("FAILED") ? "GOOD MARKER\n" : "bad content\n";
        };
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.WriteFile, new()
        {
            ["path"] = "out.txt",
            ["task"] = "Write a file containing GOOD MARKER",
            ["verify_command"] = "findstr /c:\"GOOD MARKER\" out.txt",
            ["fix_attempts"] = 1,
        });
        Assert.False(isError, text);
        Assert.Equal(2, calls);
        Assert.Contains("exit 0", text);
        Assert.Contains("attempt 2", text);
        Assert.Equal("GOOD MARKER\n", File.ReadAllText(env.PathOf("out.txt")));
    }

    [Fact]
    public async Task EditFiles_RewriteMode_PreservesBomAndCrlf_DryRunAndGuards()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        var target = env.PathOf("src/Svc.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("class Svc\r\n{\r\n    void Run() { }\r\n}\r\n")]);
        var other = env.WriteFile("src/Other.cs", "class Other { }\n");
        llama.Responder = req =>
        {
            var user = FakeLlamaServer.UserText(req);
            if (user.Contains("FILE TO EDIT: src/Other.cs")) return "NO_CHANGES";
            return "```cs\nclass Svc\n{\n    void Run() { Log(\"run\"); }\n}\n```";
        };
        await using var h = await McpHarness.StartAsync(env.Workspace);

        var (dryErr, dry) = await h.CallAsync(McpToolNames.EditFiles, new()
        {
            ["task"] = "Add Log(\"run\") to Run",
            ["files"] = new[] { "src/*.cs" },
            ["dry_run"] = true,
        });
        Assert.False(dryErr, dry);
        Assert.Contains("status: dry_run", dry);
        Assert.Equal("class Svc\r\n{\r\n    void Run() { }\r\n}\r\n", File.ReadAllText(target)); // оригинал восстановлен
        var dryId = System.Text.RegularExpressions.Regex.Match(dry, @"job_id: (\S+)").Groups[1].Value;
        var (_, dryDiff) = await h.CallAsync(McpToolNames.Job, new() { ["job_id"] = dryId, ["action"] = "diff" });
        Assert.Contains("+    void Run() { Log(\"run\"); }", dryDiff);

        var (isError, text) = await h.CallAsync(McpToolNames.EditFiles, new()
        {
            ["task"] = "Add Log(\"run\") to Run",
            ["files"] = new[] { "src/*.cs" },
        });
        Assert.False(isError, text);
        Assert.Contains("status: applied · mode: rewrite", text);
        Assert.Contains("src/Svc.cs  +1 −1", text);
        Assert.Contains("src/Other.cs  no changes needed (model)", text);
        var bytes = File.ReadAllBytes(target);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Equal("class Svc\r\n{\r\n    void Run() { Log(\"run\"); }\r\n}\r\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal("class Other { }\n", File.ReadAllText(other));

        // Новые файлы и файлы вне проекта через local_edit_files недоступны.
        var (e1, t1) = await h.CallAsync(McpToolNames.EditFiles, new() { ["task"] = "x", ["files"] = new[] { "src/Missing.cs" } });
        Assert.True(e1);
        Assert.Contains("existing files", t1);
        var (e2, t2) = await h.CallAsync(McpToolNames.EditFiles, new() { ["task"] = "x", ["files"] = new[] { "../escape.cs" } });
        Assert.True(e2);
        Assert.Contains("escapes the project root", t2);
        var (e3, t3) = await h.CallAsync(McpToolNames.EditFiles, new() { ["task"] = "x", ["files"] = new[] { "src/Svc.cs" }, ["verify_command"] = "dotnet build & calc" });
        Assert.True(e3);
        Assert.Contains("shell operators", t3);
    }

    [Fact]
    public async Task SummarizeLog_PrefiltersUtf16Log()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        var sb = new StringBuilder();
        for (var i = 1; i <= 3000; i++) sb.Append(i == 1500 ? "Foo.cs(10,3): error CS1002: ; expected\r\n" : $"noise {i}\r\n");
        File.WriteAllText(env.PathOf("build.log"), sb.ToString(), Encoding.Unicode);
        string? seen = null;
        llama.Responder = req =>
        {
            seen = FakeLlamaServer.UserText(req);
            return "Failing: build\nRoot error: Foo.cs:10 CS1002 (L1500)\nLikely cause: missing semicolon\nNext step: add it";
        };
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.SummarizeLog, new() { ["path"] = "build.log" });
        Assert.False(isError, text);
        Assert.Contains("Root error: Foo.cs:10", text);
        Assert.Contains("3000 lines", text);
        Assert.NotNull(seen);
        Assert.Contains("L1500: Foo.cs(10,3): error CS1002", seen);
        Assert.DoesNotContain("noise 2000\n", seen);
    }

    [Fact]
    public async Task Status_AndErrorsWhenNotSetUp()
    {
        using var env = new TestEnv(setupCompleted: false);
        env.WriteFile("a.cs", "x\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (sErr, status) = await h.CallAsync(McpToolNames.Status, new());
        Assert.False(sErr, status);
        Assert.Contains("NOT SET UP", status);
        Assert.Contains(env.Workspace, status);

        var (isError, text) = await h.CallAsync(McpToolNames.AskFiles, new() { ["paths"] = new[] { "a.cs" }, ["question"] = "q" });
        Assert.True(isError);
        Assert.Contains("not set up yet", text);

        var (e2, t2) = await h.CallAsync(McpToolNames.AskFiles, new() { ["paths"] = new[] { "C:\\Windows\\System32\\config\\SAM" }, ["question"] = "q" });
        Assert.True(e2);
        var (e3, t3) = await h.CallAsync(McpToolNames.Job, new() { ["job_id"] = "../../x", ["action"] = "revert" });
        Assert.True(e3);
        Assert.Contains("Invalid job_id", t3);
    }

    [Fact]
    public async Task ServerDown_TrayNotLaunchable_ClearError()
    {
        using var env = new TestEnv();
        env.WriteFile("a.cs", "x\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.AskFiles, new() { ["paths"] = new[] { "a.cs" }, ["question"] = "q" });
        Assert.True(isError);
        Assert.Contains("tray app could not be started", text);
    }

    [Fact]
    public async Task ReviewAndCommit_OnTempGitRepo()
    {
        if (Git.Executable is null) Assert.Skip("git is not installed");
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        async Task GitAsync(params string[] args)
        {
            var r = await ChildProcess.RunAsync(Git.Executable!, ["-c", "user.name=t", "-c", "user.email=t@t", .. args],
                new ChildProcess.Options { WorkingDirectory = env.Workspace }, CancellationToken.None);
            Assert.True(r.Success, r.StdErr);
        }
        await GitAsync("init", "-q");
        env.WriteFile("a.cs", "class A\n{\n}\n");
        await GitAsync("add", ".");
        await GitAsync("commit", "-q", "-m", "init");

        await using var h0 = await McpHarness.StartAsync(env.Workspace);
        var (e0, nothing) = await h0.CallAsync(McpToolNames.CommitMessage, new());
        Assert.False(e0);
        Assert.Contains("Nothing is staged", nothing);

        env.WriteFile("a.cs", "class A\n{\n    int x = 1 / 0;\n}\n");
        env.WriteFile(".env", "TOKEN=abc\n");
        await GitAsync("add", "a.cs");
        await GitAsync("add", "-f", ".env");
        llama.Responder = req =>
        {
            var sys = FakeLlamaServer.SystemText(req);
            return sys.Contains("commit message")
                ? "\"feat(a): add division field to class A that will fail at runtime because of division by zero\""
                : "[high] a.cs:3 - division by zero - remove it";
        };
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (e1, review) = await h.CallAsync(McpToolNames.ReviewDiff, new() { ["target"] = "staged" });
        Assert.False(e1, review);
        Assert.Contains("[high] a.cs:3", review);
        Assert.Contains("excluded (secret files): .env", review);
        Assert.All(llama.Requests, r => Assert.DoesNotContain("TOKEN=abc", r));
        Assert.Contains("     3|+     int x = 1 / 0;", llama.Requests[0].Replace("\\u002B", "+"));

        var (e2, msg) = await h.CallAsync(McpToolNames.CommitMessage, new());
        Assert.False(e2, msg);
        var subject = msg.Split('\n')[0];
        Assert.StartsWith("feat(a): add division", subject);
        Assert.True(subject.Length <= 72);

        var (e3, bad) = await h.CallAsync(McpToolNames.ReviewDiff, new() { ["target"] = "--output=C:\\x" });
        Assert.True(e3);
        Assert.Contains("not a valid git ref", bad);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_AskFilesAgainstRealServer()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OFFLOAD_LIVE") == "1", "set OFFLOAD_LIVE=1 (and OFFLOAD_HOME) to run against a real llama-server");
        ConfigStore_ReadOnly();
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        await using var h = await McpHarness.StartAsync(repo);
        var (isError, text) = await h.CallAsync(McpToolNames.AskFiles, new()
        {
            ["paths"] = new[] { "src/Offload.Core/McpToolNames.cs" },
            ["question"] = "List the tool name constants.",
        });
        Assert.False(isError, text);
        Assert.Contains("local_ask_files", text);
    }

    private static void ConfigStore_ReadOnly() => Offload.Core.Config.ConfigStore.ReadOnly = true;
}
