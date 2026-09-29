using Offload.Core;
using System.Text.Json;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Security;
using Offload.Llama;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>
/// Клиентский режим (ROADMAP §9.2): основная модель — на удалённом сервере. MCP обращается к нему напрямую (без трея),
/// берёт id модели из /v1/models и контекст из /props; вспомогательные роли остаются локальными.
/// </summary>
[Collection("AppPaths")]
public sealed class RemoteModeTests
{
    private const string LocalKey = "pc-local-only";

    /// <summary>Локальный сервер не запущен (свободный порт), удалённый — поддельный; ключ удалённого = TestEnv.ApiKey.</summary>
    private static TestEnv RemoteEnv(FakeLlamaServer? remote, int? remotePort = null, bool setupCompleted = true, string? key = TestEnv.ApiKey) =>
        new(TestEnv.FreePort(), setupCompleted, c =>
        {
            c.Server.ApiKey = LocalKey;
            c.Remote.Enabled = true;
            c.Remote.Url = $"http://127.0.0.1:{remote?.Port ?? remotePort ?? TestEnv.FreePort()}/v1";
            c.Remote.ApiKeyProtected = key is null ? null : Dpapi.Protect(key);
        });

    /// <summary>Трей в клиентском режиме не нужен: любое обращение к нему — ошибка теста.</summary>
    private static SessionState NoTray() => new()
    {
        TrayLauncherOverride = () => throw new InvalidOperationException("трей не должен запускаться в клиентском режиме"),
        IpcOverride = (_, _) => throw new InvalidOperationException("IPC не должен вызываться в клиентском режиме"),
    };

    [Fact]
    public async Task GetModel_Remote_UsesRemoteServerModelIdAndContext_WithoutTray()
    {
        using var remote = new FakeLlamaServer { ModelsAlias = "remote-coder", ContextSize = 12288, Responder = _ => "from remote" };
        using var env = RemoteEnv(remote);
        var ctx = env.Context(NoTray(), TestContext.Current.CancellationToken);

        var model = await ctx.GetModelAsync();
        var reply = await model.ChatAsync("sys", "user", 64, "test", ctx.Ct);

        Assert.True(model.Client.IsRemote);
        Assert.Equal($"http://127.0.0.1:{remote.Port}", model.Client.BaseUrl);
        Assert.Null(model.Model); // локальная запись модели к удалённому серверу не относится
        Assert.Equal(12288, model.ContextPerSlot);
        Assert.Equal($"remote-coder @ 127.0.0.1:{remote.Port}", model.DisplayName);
        Assert.Equal("from remote", reply.Text);
        using var body = JsonDocument.Parse(Assert.Single(remote.Requests));
        Assert.Equal("remote-coder", body.RootElement.GetProperty("model").GetString());
        // Сэмплинг и отключение размышлений локальной модели (EnableThinkingKwarg у TestEnv) удалённому серверу не навязываются.
        Assert.False(body.RootElement.TryGetProperty("chat_template_kwargs", out _));
        Assert.False(body.RootElement.TryGetProperty("top_k", out _));
    }

    [Fact]
    public async Task GetModel_Remote_WorksWithoutLocalSetup()
    {
        using var remote = new FakeLlamaServer { ModelsAlias = "offload" };
        using var env = RemoteEnv(remote, setupCompleted: false);
        var model = await env.Context(NoTray(), TestContext.Current.CancellationToken).GetModelAsync();
        Assert.Equal("offload", model.Client.Model);
    }

    [Fact]
    public async Task GetModel_RemoteUnreachable_ClearErrorWithoutTray()
    {
        using var env = RemoteEnv(null);
        var ctx = env.Context(NoTray(), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ToolException>(() => ctx.GetModelAsync());

        Assert.Contains("remote model server", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not reachable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetModel_RemoteKeyMissing_AsksToReenter()
    {
        using var remote = new FakeLlamaServer { ModelsAlias = "offload" };
        using var env = RemoteEnv(remote, key: null);

        var ex = await Assert.ThrowsAsync<ToolException>(() => env.Context(NoTray(), TestContext.Current.CancellationToken).GetModelAsync());

        Assert.Contains("API key of the remote model server", ex.Message, StringComparison.Ordinal);
        Assert.Empty(remote.Requests);
    }

    [Fact]
    public async Task Chat_RemoteRejectsKey_RemoteSpecificError()
    {
        using var remote = new FakeLlamaServer { ModelsAlias = "offload" };
        using var env = RemoteEnv(remote, key: "olan-wrong-key");
        var ctx = env.Context(NoTray(), TestContext.Current.CancellationToken);
        var model = await ctx.GetModelAsync();

        var ex = await Assert.ThrowsAsync<ToolException>(() => model.ChatAsync("sys", "user", 32, "test", ctx.Ct));

        Assert.Contains("remote model server rejected the API key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("olan-wrong-key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuxRole_InRemoteMode_StaysLocal()
    {
        using var remote = new FakeLlamaServer { ModelsAlias = "offload", Responder = _ => "from remote" };
        using var fast = new FakeLlamaServer { Responder = _ => "from local fast" };
        using var env = new TestEnv(TestEnv.FreePort(), configure: c =>
        {
            c.Remote.Enabled = true;
            c.Remote.Url = $"http://127.0.0.1:{remote.Port}";
            c.Remote.ApiKeyProtected = Dpapi.Protect(TestEnv.ApiKey);
            c.Models.Installed.Add(new InstalledModel { Id = "fast-mini", DisplayName = "Mini 4B", RecommendedContext = 4096 });
            c.Models.Roles.Fast = "fast-mini";
        });
        var state = new SessionState
        {
            IpcOverride = (req, _) => Task.FromResult<IpcResponse?>(new IpcResponse(true, null,
                new() { [IpcRoleArgs.Role] = "fast", [IpcRoleArgs.BaseUrl] = $"http://127.0.0.1:{fast.Port}" })),
        };
        var c0 = env.Context(state, TestContext.Current.CancellationToken);
        var ctx = new ToolContext { Tool = McpToolNames.CommitMessage, Cfg = c0.Cfg, State = c0.State, Progress = c0.Progress, Roots = c0.Roots, Ct = c0.Ct };

        var model = await ctx.GetModelAsync();
        var reply = await model.ChatAsync("sys", "user", 32, "test", ctx.Ct);

        Assert.Equal(ModelRole.Fast, model.Role);
        Assert.False(model.Client.IsRemote);
        Assert.Equal("from local fast", reply.Text);
        Assert.Empty(remote.Requests);
    }

    [Fact]
    public async Task Status_Remote_ReportsHostLatencyAndContext()
    {
        using var remote = new FakeLlamaServer { ModelsAlias = "offload", ContextSize = 32768 };
        using var env = RemoteEnv(remote);
        var ctx = env.Context(NoTray(), TestContext.Current.CancellationToken);

        var status = await StatusTool.RunAsync(ctx);

        var host = $"127.0.0.1:{remote.Port}";
        Assert.Contains($"local model: READY · offload @ {host}", status, StringComparison.Ordinal);
        Assert.Contains("context 32768 tok per request", status, StringComparison.Ordinal);
        Assert.Contains($"remote: {host} · reachable · latency ", status, StringComparison.Ordinal);
        Assert.DoesNotContain(TestEnv.ApiKey, status, StringComparison.Ordinal);
        var model = Assert.IsType<StatusOutput>(ctx.Structured).Model;
        Assert.Equal("ready", model.State);
        Assert.Equal(host, model.Remote);
        Assert.NotNull(model.LatencyMs);
    }

    [Fact]
    public async Task Status_RemoteDown_Unreachable()
    {
        using var env = RemoteEnv(null);
        var ctx = env.Context(NoTray(), TestContext.Current.CancellationToken);

        var status = await StatusTool.RunAsync(ctx);

        Assert.Contains("local model: UNREACHABLE", status, StringComparison.Ordinal);
        Assert.Contains("NOT reachable", status, StringComparison.Ordinal);
        var model = Assert.IsType<StatusOutput>(ctx.Structured).Model;
        Assert.Equal("offline", model.State);
        Assert.Null(model.LatencyMs);
    }

    [Fact]
    public void ServerInstructions_Remote_MentionsRemoteServer()
    {
        var cfg = new AppConfig();
        cfg.Remote.Enabled = true;
        cfg.Remote.Url = "http://192.168.1.10:8765";
        cfg.Remote.ContextSize = 65536;
        var line = ServerInstructions.StatusLine(cfg);
        Assert.Contains("remote server 192.168.1.10:8765", line, StringComparison.Ordinal);
        Assert.Contains("65536", line, StringComparison.Ordinal);
    }

    [Fact]
    public void MapError_Remote_TalksAboutNetworkAndKey()
    {
        var unauthorized = LocalModel.MapError(new LlamaApiException("Неверный ключ", 401) { Kind = LlamaErrorKind.Unauthorized }, remote: true);
        Assert.Contains("remote model server rejected the API key", unauthorized.Message, StringComparison.Ordinal);
        var lost = LocalModel.MapError(new LlamaApiException("нет связи") { Kind = LlamaErrorKind.ConnectionFailed }, remote: true);
        Assert.Contains("remote model server", lost.Message, StringComparison.Ordinal);
        // Для своего сервера — прежние тексты.
        Assert.Contains("Offload tray app", LocalModel.MapError(new LlamaApiException("x", 401) { Kind = LlamaErrorKind.Unauthorized }).Message, StringComparison.Ordinal);
    }
}
