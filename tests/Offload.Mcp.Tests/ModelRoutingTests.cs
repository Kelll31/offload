using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Llama;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Маршрутизация по ролям моделей (ROADMAP §5.2) и структурированный вывод (§9.2).</summary>
[Collection("AppPaths")]
public class ModelRoutingTests
{
    private static AppConfig WithFast(string? fast = "fast-mini")
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([new InstalledModel { Id = "big" }, new InstalledModel { Id = "fast-mini" }]);
        cfg.Models.ActiveModelId = "big";
        cfg.Models.Roles.Fast = fast;
        return cfg;
    }

    [Theory]
    [InlineData(McpToolNames.CommitMessage)]
    [InlineData(McpToolNames.SummarizeLog)]
    [InlineData(McpToolNames.FindContext)]
    public void ShortInteractiveTools_GoToFast_WhenConfigured(string tool)
    {
        Assert.Equal(GpuPriority.Interactive, GpuQueue.PriorityFor(tool));
        Assert.Equal(ModelRole.Fast, ModelRouting.Resolve(WithFast(), tool));
        Assert.Equal(ModelRole.Quality, ModelRouting.Resolve(WithFast(null), tool));
    }

    [Theory]
    [InlineData(McpToolNames.AskFiles)]
    [InlineData(McpToolNames.ReviewDiff)]
    [InlineData(McpToolNames.WriteFile)]
    [InlineData(McpToolNames.EditFiles)]
    [InlineData(McpToolNames.AgentTask)]
    [InlineData(McpToolNames.Solve)]
    public void QualityTools_NeverFast(string tool)
    {
        Assert.Equal(ModelRole.Quality, ModelRouting.Resolve(WithFast(), tool));
    }

    [Fact]
    public void FastTool_WithAgentOrNormalPriority_Quality()
    {
        Assert.Equal(ModelRole.Quality, ModelRouting.Resolve(WithFast(), McpToolNames.CommitMessage, GpuPriority.Agent));
        Assert.Equal(ModelRole.Quality, ModelRouting.PreferredRole(McpToolNames.FindContext, GpuPriority.Normal));
    }

    [Fact]
    public void FastRole_SameAsActiveOrNotInstalled_Quality()
    {
        Assert.Equal(ModelRole.Quality, ModelRouting.Resolve(WithFast("big"), McpToolNames.CommitMessage));
        Assert.Equal(ModelRole.Quality, ModelRouting.Resolve(WithFast("gone"), McpToolNames.CommitMessage));
        var embedAsFast = WithFast("fast-mini");
        embedAsFast.Models.Installed[1].Kind = ModelKind.Embed;
        Assert.Equal(ModelRole.Quality, ModelRouting.Resolve(embedAsFast, McpToolNames.CommitMessage));
    }

    [Fact]
    public void GpuRequest_AuxRoleHasOwnSingleSlotPool()
    {
        var main = GpuQueue.RequestFor("t", null, GpuPriority.Interactive, 4, ModelRole.Quality);
        var fast = GpuQueue.RequestFor("t", null, GpuPriority.Interactive, 4, ModelRole.Fast);
        Assert.Equal((4, (string?)null), (main.Parallel, main.Pool));
        Assert.Equal((1, "fast"), (fast.Parallel, fast.Pool));
        Assert.NotEqual(GpuQueue.MutexName(null, 0), GpuQueue.MutexName("fast", 0));
        Assert.StartsWith(GpuQueue.BaseName, GpuQueue.MutexName("fast", 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuxPool_DoesNotWaitForMainSlot()
    {
        using var home = new TempHome();
        var progress = new ProgressReporter(null, null);
        var ct = TestContext.Current.CancellationToken;
        await using var mainSlot = await GpuQueue.AcquireAsync(new GpuRequest("a", null, GpuPriority.Normal, 1), progress, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        await using var fastSlot = await GpuQueue.AcquireAsync(GpuQueue.RequestFor("b", null, GpuPriority.Interactive, 1, ModelRole.Fast), progress, cts.Token);
        Assert.Equal(0, fastSlot.Index);
        Assert.True(fastSlot.Waited < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void BuildRequest_WithFormat_PassesResponseFormat()
    {
        var format = ResponseFormat.JsonSchema("plan", """{"type":"object"}""");
        var r = LocalModel.BuildRequest(null, [ChatMessage.User("x")], 100, format);
        Assert.Same(format, r.ResponseFormat);
        Assert.Null(LocalModel.BuildRequest(null, [ChatMessage.User("x")], 100).ResponseFormat);
    }

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("  [1,2]  ", true)]
    [InlineData("```json\n{\"a\":1}\n```", true)]
    [InlineData("Here: {\"a\":1}", false)]
    [InlineData("{\"a\":", false)]
    [InlineData("", false)]
    public void TryParseJson_PlainOrFenced(string text, bool ok)
    {
        Assert.Equal(ok, LocalModel.TryParseJson(text) is not null);
    }

    [Fact]
    public void CheckRoleResponse_OldTray_Throws_NewTray_FollowsLoopbackPort()
    {
        var client = new LlamaClient("http://127.0.0.1:9001", "k") { Model = "offload-fast" };
        // Трей до ролей: запустил основной сервер и не знает про роль.
        Assert.Throws<ToolException>(() => ServerEnsurer.CheckRoleResponse(new IpcResponse(true, "ok", new() { ["state"] = "Running" }), ModelRole.Fast, client));
        Assert.Throws<ToolException>(() => ServerEnsurer.CheckRoleResponse(new IpcResponse(true, "ok", new() { [IpcRoleArgs.Role] = "quality" }), ModelRole.Fast, client));

        // Без адреса от трея — отказ: адрес роли из конфига (порт основного + 1…3) мог занять чужой процесс.
        Assert.Throws<ToolException>(() => ServerEnsurer.CheckRoleResponse(new IpcResponse(true, null, new() { [IpcRoleArgs.Role] = "fast" }), ModelRole.Fast, client));

        var moved = ServerEnsurer.CheckRoleResponse(
            new IpcResponse(true, null, new() { [IpcRoleArgs.Role] = "fast", [IpcRoleArgs.BaseUrl] = "http://127.0.0.1:9050" }), ModelRole.Fast, client);
        Assert.Equal(("http://127.0.0.1:9050", "offload-fast", "k"), (moved.BaseUrl, moved.Model, moved.ApiKey));

        // Не петлевой адрес, другая схема или путь — отказ: ключ API не уходит на чужой хост.
        foreach (var bad in new[] { "http://example.com:80", "https://127.0.0.1:9050", "http://127.0.0.1:9050/x", "http://u:p@127.0.0.1:9050", "not a url" })
        {
            Assert.Throws<ToolException>(() => ServerEnsurer.CheckRoleResponse(
                new IpcResponse(true, null, new() { [IpcRoleArgs.Role] = "fast", [IpcRoleArgs.BaseUrl] = bad }), ModelRole.Fast, client));
        }
    }

    [Fact]
    public async Task AuxRole_NeverProbesConfigPortBeforeTray_UsesTrayUrl()
    {
        // На порту роли из конфига «сидит» чужой сервер: MCP не должен отправить ему ни одного запроса.
        using var main = new FakeLlamaServer();
        using var squatter = new FakeLlamaServer { Responder = _ => "stolen" };
        using var fast = new FakeLlamaServer { Responder = _ => "feat: fast", ModelsAlias = "offload-fast" };
        using var env = new TestEnv(main.Port, configure: c =>
        {
            c.Models.Installed.Add(new InstalledModel { Id = "fast-mini", DisplayName = "Mini 4B", RecommendedContext = 4096 });
            c.Models.Roles.Fast = "fast-mini";
            c.Server.AuxPorts["fast"] = squatter.Port;
        });
        var asked = 0;
        var state = new SessionState
        {
            IpcOverride = (req, _) =>
            {
                asked++;
                Assert.Equal(IpcCommands.StartServer, req.Command);
                return Task.FromResult<IpcResponse?>(new IpcResponse(true, null,
                    new() { [IpcRoleArgs.Role] = "fast", [IpcRoleArgs.BaseUrl] = $"http://127.0.0.1:{fast.Port}" }));
            },
        };
        var ctx = WithTool(env.Context(state, TestContext.Current.CancellationToken), McpToolNames.CommitMessage);

        var model = await ctx.GetModelAsync();
        var reply = await model.ChatAsync("sys", "user", 64, "test", ctx.Ct);

        Assert.Equal(1, asked);
        Assert.Equal(ModelRole.Fast, model.Role);
        Assert.Equal("feat: fast", reply.Text);
        Assert.Empty(squatter.Requests);
        Assert.Empty(main.Requests);
    }

    [Fact]
    public async Task AuxRole_WrongAliasOnTrayUrl_FallsBackToMain()
    {
        using var main = new FakeLlamaServer { Responder = _ => "from main" };
        // По адресу, который вернул трей, отвечает чужой сервер (другой псевдоним в /v1/models).
        using var other = new FakeLlamaServer { Responder = _ => "stolen", ModelsAlias = "someone-else" };
        using var env = new TestEnv(main.Port, configure: c =>
        {
            c.Models.Installed.Add(new InstalledModel { Id = "fast-mini", DisplayName = "Mini 4B" });
            c.Models.Roles.Fast = "fast-mini";
        });
        var state = new SessionState
        {
            IpcOverride = (_, _) => Task.FromResult<IpcResponse?>(new IpcResponse(true, null,
                new() { [IpcRoleArgs.Role] = "fast", [IpcRoleArgs.BaseUrl] = $"http://127.0.0.1:{other.Port}" })),
        };
        var ctx = WithTool(env.Context(state, TestContext.Current.CancellationToken), McpToolNames.CommitMessage);

        var model = await ctx.GetModelAsync();
        var reply = await model.ChatAsync("sys", "user", 64, "test", ctx.Ct);

        Assert.Equal(ModelRole.Quality, model.Role);
        Assert.Equal("from main", reply.Text);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task MainServer_WrongAlias_IsRefused()
    {
        using var impostor = new FakeLlamaServer { ModelsAlias = "not-offload" };
        using var env = new TestEnv(impostor.Port);
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ToolException>(() => ctx.GetModelAsync());
        Assert.Contains("Another program answers", ex.Message);
        Assert.Empty(impostor.Requests);

        using var real = new FakeLlamaServer { ModelsAlias = "offload" };
        using var env2 = new TestEnv(real.Port);
        Assert.NotNull(await env2.Context(ct: TestContext.Current.CancellationToken).GetModelAsync());
    }

    [Fact]
    public async Task GetModel_FastConfiguredAndRunning_UsesFastServerAndAlias()
    {
        using var main = new FakeLlamaServer();
        using var fast = new FakeLlamaServer { Responder = _ => "feat: fast" };
        using var env = new TestEnv(main.Port, configure: c =>
        {
            c.Models.Installed.Add(new InstalledModel { Id = "fast-mini", DisplayName = "Mini 4B", RecommendedContext = 4096 });
            c.Models.Roles.Fast = "fast-mini";
            c.Server.AuxPorts["fast"] = fast.Port;
        });
        // Адрес сервера роли сообщает трей (IPC start-server с ролью).
        var state = new SessionState
        {
            IpcOverride = (_, _) => Task.FromResult<IpcResponse?>(new IpcResponse(true, null,
                new() { [IpcRoleArgs.Role] = "fast", [IpcRoleArgs.BaseUrl] = $"http://127.0.0.1:{fast.Port}" })),
        };
        var ctx = WithTool(env.Context(state, TestContext.Current.CancellationToken), McpToolNames.CommitMessage);

        var model = await ctx.GetModelAsync();
        var reply = await model.ChatAsync("sys", "user", 64, "test", ctx.Ct);

        Assert.Equal(ModelRole.Fast, model.Role);
        Assert.Equal("fast-mini", model.Model?.Id);
        Assert.Equal("feat: fast", reply.Text);
        Assert.Empty(main.Requests);
        using var body = JsonDocument.Parse(Assert.Single(fast.Requests));
        Assert.Equal("offload-fast", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task GetModel_FastServerUnavailable_FallsBackToQuality()
    {
        using var main = new FakeLlamaServer { Responder = _ => "from main" };
        using var env = new TestEnv(main.Port, configure: c =>
        {
            c.Models.Installed.Add(new InstalledModel { Id = "fast-mini", DisplayName = "Mini 4B" });
            c.Models.Roles.Fast = "fast-mini";
            c.Server.AuxPorts["fast"] = TestEnv.FreePort();
        });
        // Трей не запущен и не запускается — сервер роли поднять некому.
        var state = new SessionState { TrayLauncherOverride = () => false };
        var ctx = WithTool(env.Context(state, TestContext.Current.CancellationToken), McpToolNames.SummarizeLog);

        var model = await ctx.GetModelAsync();
        var reply = await model.ChatAsync("sys", "user", 64, "test", ctx.Ct);

        Assert.Equal(ModelRole.Quality, model.Role);
        Assert.Equal("test-coder", model.Model?.Id);
        Assert.Equal("from main", reply.Text);
    }

    [Fact]
    public async Task ChatJson_SendsSchema_ParsesAnswer()
    {
        using var server = new FakeLlamaServer { Responder = _ => "{\"files\":[\"a.cs\"]}" };
        using var env = new TestEnv(server.Port);
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);
        var model = await ctx.GetModelAsync();

        var r = await model.ChatJsonAsync("sys", "user", ResponseFormat.JsonSchema("files", """{"type":"object","required":["files"]}"""), 64, "test", ctx.Ct);

        Assert.Equal("a.cs", r.Json!.Value.GetProperty("files")[0].GetString());
        using var body = JsonDocument.Parse(Assert.Single(server.Requests));
        Assert.Equal("files", body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Status_ReportsRoleServers_TextAndStructured()
    {
        using var main = new FakeLlamaServer();
        using var embed = new FakeLlamaServer();
        var rerankPort = TestEnv.FreePort(); // сервер реранка не запущен
        using var env = new TestEnv(main.Port, configure: c =>
        {
            c.Models.Installed.Add(new InstalledModel { Id = "emb", DisplayName = "Emb 0.6B", Quant = "Q8_0", Kind = ModelKind.Embed });
            c.Models.Installed.Add(new InstalledModel { Id = "rr", DisplayName = "Reranker", Kind = ModelKind.Rerank });
            c.Models.Roles.Embed = "emb";
            c.Models.Roles.Rerank = "rr";
            c.Models.Roles.Fast = "test-coder"; // совпадает с активной — короткие задачи идут на основной сервер
            c.Server.AuxPorts["embed"] = embed.Port;
            c.Server.AuxPorts["rerank"] = rerankPort;
        });
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);

        var status = await StatusTool.RunAsync(ctx);

        Assert.Contains($"roles: fast — not configured · embed — Emb 0.6B (Q8_0) (running, :{embed.Port}) · " +
                        $"rerank — Reranker (idle, starts on the first call, :{rerankPort})", status);
        var roles = Assert.IsType<StatusOutput>(ctx.Structured).Roles!;
        Assert.Equal(["fast", "embed", "rerank"], roles.Select(r => r.Role));
        Assert.Equal(["not_configured", "running", "idle"], roles.Select(r => r.State));
        Assert.Equal([null, embed.Port, rerankPort], roles.Select(r => r.Port));
        Assert.Null(roles[0].Model);

        // structuredContent сериализуется с полем roles (схема — optional, старые клиенты его не требуют).
        var json = JsonSerializer.SerializeToElement(ctx.Structured, ctx.Structured!.GetType(), ToolRunner.StructuredJson);
        Assert.Equal("embed", json.GetProperty("roles")[1].GetProperty("role").GetString());
    }

    [Fact]
    public void RolesLine_NoneConfigured_ShortHint()
    {
        var none = ModelRoleConfig.Auxiliary.Select(r => new StatusRole { Role = r.Key(), State = "not_configured" }).ToList();
        var line = StatusTool.RolesLine(none);
        Assert.StartsWith("roles: fast/embed/rerank not configured", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    private static ToolContext WithTool(ToolContext c, string tool) => new()
    {
        Tool = tool,
        Cfg = c.Cfg,
        State = c.State,
        Progress = c.Progress,
        Roots = c.Roots,
        Ct = c.Ct,
    };
}
