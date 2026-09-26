using System.Net;
using System.Net.Sockets;
using Offload.Core.Config;

namespace Offload.Llama.Tests;

/// <summary>Устойчивая классификация ошибок llama-server (Kind/Detail) и контекст слота при общем KV-кэше.</summary>
public sealed class ErrorKindTests
{
    private static readonly ChatRequest Hi = new([ChatMessage.User("hi")]);

    private static bool HasCyrillic(string? s) => s is not null && s.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    private static int UnusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<LlamaApiException> ChatErrorAsync(int status, string body)
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, status, body));
        return await Assert.ThrowsAsync<LlamaApiException>(() => new LlamaClient(server.BaseUrl, "k").ChatAsync(Hi, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContextOverflow_TypedAsContextExceeded()
    {
        var ex = await ChatErrorAsync(400,
            "{\"error\":{\"code\":400,\"message\":\"the request exceeds the available context size, try increasing it\",\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":9000,\"n_ctx\":8192}}");
        Assert.Equal(LlamaErrorKind.ContextExceeded, ex.Kind);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(9000, ex.PromptTokens);
        Assert.Equal(8192, ex.ContextSize);
        Assert.Contains("не помещается в контекст", ex.Message); // текст для UI остаётся русским
        Assert.False(HasCyrillic(ex.Detail));
    }

    [Fact]
    public async Task Http400_WithWordContext_IsNotContextExceeded()
    {
        var ex = await ChatErrorAsync(400,
            "{\"error\":{\"code\":400,\"message\":\"Invalid context shift: n_keep exceeds the prompt\",\"type\":\"invalid_request_error\"}}");
        Assert.Equal(LlamaErrorKind.Rejected, ex.Kind);
        Assert.Equal("Invalid context shift: n_keep exceeds the prompt", ex.Detail);
    }

    [Fact]
    public async Task Http500_WithWordContext_IsServerError()
    {
        var ex = await ChatErrorAsync(500, "{\"error\":{\"code\":500,\"message\":\"failed to decode: context slot is busy\",\"type\":\"server_error\"}}");
        Assert.Equal(LlamaErrorKind.ServerError, ex.Kind);
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("failed to decode: context slot is busy", ex.Detail);
    }

    [Fact]
    public async Task Http500_PlainTextBody_KeepsBodyAsDetail()
    {
        var ex = await ChatErrorAsync(500, "Internal Server Error");
        Assert.Equal(LlamaErrorKind.ServerError, ex.Kind);
        Assert.Equal("Internal Server Error", ex.Detail);
    }

    [Fact]
    public async Task Http401And503_Typed()
    {
        Assert.Equal(LlamaErrorKind.Unauthorized, (await ChatErrorAsync(401, "{}")).Kind);
        Assert.Equal(LlamaErrorKind.Loading, (await ChatErrorAsync(503, "{}")).Kind);
    }

    [Fact]
    public void StreamErrorChunk_ContextOverflowByTokens()
    {
        var p = new ChatStreamParser();
        var ex = Assert.Throws<LlamaApiException>(() =>
            p.ProcessLine("data: {\"error\":{\"message\":\"prompt is too long\",\"n_prompt_tokens\":70000,\"n_ctx\":65536}}"));
        Assert.Equal(LlamaErrorKind.ContextExceeded, ex.Kind);

        // Слово «context» без признаков переполнения — не переполнение.
        var p2 = new ChatStreamParser();
        var other = Assert.Throws<LlamaApiException>(() =>
            p2.ProcessLine("data: {\"error\":{\"message\":\"context checkpoint restore failed\",\"type\":\"server_error\"}}"));
        Assert.Equal(LlamaErrorKind.ServerError, other.Kind);
    }

    [Fact]
    public async Task Refused_NotRunning_WithEnglishDetail()
    {
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() =>
            new LlamaClient($"http://127.0.0.1:{UnusedPort()}", "k").ChatAsync(Hi, ct: TestContext.Current.CancellationToken));
        Assert.Equal(LlamaErrorKind.NotRunning, ex.Kind);
        Assert.NotNull(ex.Detail);
        Assert.False(HasCyrillic(ex.Detail), ex.Detail);
    }

    [Fact]
    public async Task TruncatedStream_ConnectionLost()
    {
        await using var server = new FakeHttpServer(async (_, s, _) =>
        {
            await FakeHttpServer.WriteSseHeadersAsync(s);
            await FakeHttpServer.WriteRawAsync(s, "data: {\"choices\":[{\"delta\":{\"content\":\"half\"}}]}\n\n");
        });
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() => new LlamaClient(server.BaseUrl, "k").ChatAsync(Hi, ct: TestContext.Current.CancellationToken));
        Assert.Equal(LlamaErrorKind.ConnectionLost, ex.Kind);
        Assert.False(HasCyrillic(ex.Detail), ex.Detail);
    }

    [Fact]
    public void ParseProps_UnifiedKv_SplitsSharedBufferPerSlot()
    {
        const string json = """{"default_generation_settings":{"n_ctx":196608},"total_slots":3}""";
        var shared = LlamaClient.ParseProps(json, unifiedKv: true);
        Assert.Equal(65536, shared.ContextPerSlot);
        Assert.Equal(196608, shared.SharedContext);
        Assert.Equal(3, shared.TotalSlots);

        // Без -kvu llama-server сам сообщает долю слота.
        var own = LlamaClient.ParseProps(json);
        Assert.Equal(196608, own.ContextPerSlot);
        Assert.Null(own.SharedContext);

        // Один слот — делить нечего.
        var single = LlamaClient.ParseProps("""{"default_generation_settings":{"n_ctx":32768},"total_slots":1}""", unifiedKv: true);
        Assert.Equal(32768, single.ContextPerSlot);
        Assert.Null(single.SharedContext);
    }

    [Fact]
    public void FromConfig_UnifiedKvFollowsLaunchArgs()
    {
        var cfg = new AppConfig();
        cfg.Server.Parallel = 1;
        Assert.False(LlamaClient.FromConfig(cfg).UnifiedKv);
        cfg.Server.Parallel = 3;
        Assert.True(LlamaClient.FromConfig(cfg).UnifiedKv);
        cfg.Server.ExtraArgs = "--no-kv-unified";
        Assert.False(LlamaClient.FromConfig(cfg).UnifiedKv);
    }

    [Fact]
    public async Task GetProps_FromConfig_ReportsPerSlotContext()
    {
        await using var server = new FakeHttpServer((_, s, _) =>
            FakeHttpServer.WriteResponseAsync(s, 200, """{"default_generation_settings":{"n_ctx":196608},"total_slots":3}"""));
        var client = new LlamaClient(server.BaseUrl, "k") { UnifiedKv = true };
        var props = await client.GetPropsAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(props);
        Assert.Equal(65536, props.ContextPerSlot);
        Assert.Equal(196608, props.SharedContext);
    }
}
