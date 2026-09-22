using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Offload.Llama.Tests;

public sealed class ChatStreamParserTests
{
    /// <summary>Поток в формате llama-server b11102 (stream_options.include_usage = true).</summary>
    internal const string CannedStream =
        ": ping\r\n" +
        "\r\n" +
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null}}],\"object\":\"chat.completion.chunk\"}\r\n\r\n" +
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"reasoning_content\":\"Думаю\"}}]}\r\n\r\n" +
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"reasoning_content\":\"...\"}}]}\r\n\r\n" +
        "event: message\r\n" +
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"content\":\"def add\"}}]}\r\n\r\n" +
        "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"content\":\"(a, b):\"}}]}\r\n\r\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\",\"index\":0,\"delta\":{}}],\"timings\":{\"cache_n\":10,\"prompt_n\":5,\"prompt_per_second\":1345.5,\"predicted_n\":8,\"predicted_per_second\":241.4}}\r\n\r\n" +
        "data: {\"choices\":[],\"usage\":{\"completion_tokens\":8,\"prompt_tokens\":15,\"total_tokens\":23},\"timings\":{\"cache_n\":10,\"prompt_n\":5,\"prompt_per_second\":1345.5,\"predicted_n\":8,\"predicted_per_second\":241.4}}\r\n\r\n" +
        "data: [DONE]\r\n\r\n";

    private static ChatStreamParser Feed(string stream, List<string>? deltas = null)
    {
        var p = new ChatStreamParser(d => deltas?.Add(d));
        using var reader = new StringReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (!p.ProcessLine(line)) break;
        }
        p.Complete();
        return p;
    }

    [Fact]
    public void CannedStream_ContentReasoningUsageTimings()
    {
        var deltas = new List<string>();
        var p = Feed(CannedStream, deltas);
        Assert.True(p.Done);
        Assert.Equal(["def add", "(a, b):"], deltas);
        var r = p.ToResult(TimeSpan.FromSeconds(1));
        Assert.Equal("def add(a, b):", r.Content);
        Assert.Equal("Думаю...", r.Reasoning);
        Assert.Equal("stop", r.FinishReason);
        Assert.Equal(15, r.PromptTokens);
        Assert.Equal(8, r.CompletionTokens);
        Assert.Equal(241.4, r.GenerationTokensPerSecond);
        Assert.Equal(1345.5, r.PromptTokensPerSecond);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void WithoutUsage_FallsBackToTimings()
    {
        var p = Feed(
            "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"length\",\"delta\":{}}],\"timings\":{\"cache_n\":3,\"prompt_n\":4,\"predicted_n\":7}}\n\n" +
            "data: [DONE]\n\n");
        var r = p.ToResult(TimeSpan.Zero);
        Assert.Equal(7, r.PromptTokens);
        Assert.Equal(7, r.CompletionTokens);
        Assert.True(r.Truncated);
        Assert.Null(r.GenerationTokensPerSecond);
    }

    [Fact]
    public void MultiLineDataEvent_IsJoined()
    {
        var p = Feed(
            "data: {\"choices\":[{\"delta\":\n" +
            "data: {\"content\":\"joined\"}}]}\n" +
            "\n" +
            "data: [DONE]\n");
        Assert.Equal("joined", p.Content);
        Assert.True(p.Done);
    }

    [Fact]
    public void NoSpaceAfterColon_AndGarbageIgnored()
    {
        var p = Feed("data:{\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\nid: 5\nretry: 100\ndata: not json\n\ndata:[DONE]\n");
        Assert.Equal("a", p.Content);
        Assert.True(p.Done);
    }

    [Fact]
    public void LinesAfterDone_Ignored()
    {
        var p = Feed("data: [DONE]\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"late\"}}]}\n\n");
        Assert.Equal("", p.Content);
    }

    [Fact]
    public void ErrorInsideStream_Throws()
    {
        var ex = Assert.Throws<LlamaApiException>(() =>
            Feed("data: {\"error\":{\"code\":400,\"message\":\"the request exceeds the available context size\",\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":9000,\"n_ctx\":8192}}\n\n"));
        Assert.Contains("не помещается в контекст", ex.Message);
        Assert.Contains("9000", ex.Message);
    }

    [Fact]
    public void ParseProps_ReadsCapsAndSlots()
    {
        var props = LlamaClient.ParseProps("""
        {"default_generation_settings":{"n_ctx":8192},"total_slots":2,"model_path":"C:\\m\\smol.gguf","model_alias":"offload",
         "build_info":"b11102-bfd73a876","is_sleeping":false,"chat_template_caps":{"supports_tool_calls":true}}
        """);
        Assert.Equal(8192, props.ContextPerSlot);
        Assert.Equal(2, props.TotalSlots);
        Assert.Equal(@"C:\m\smol.gguf", props.ModelPath);
        Assert.Equal("offload", props.ModelAlias);
        Assert.Equal("b11102-bfd73a876", props.BuildInfo);
        Assert.True(props.SupportsToolCalls);
        Assert.False(props.IsSleeping);
    }

    [Fact]
    public void BuildChatBody_StreamsWithUsage()
    {
        var body = LlamaClient.BuildChatBody(new ChatRequest([ChatMessage.User("привет")], MaxTokens: 10, Temperature: 0.2,
            ChatTemplateKwargs: new Dictionary<string, object> { ["enable_thinking"] = false }, ReasoningEffort: "low", PresencePenalty: 1.5));
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        Assert.True(r.GetProperty("stream").GetBoolean());
        Assert.True(r.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(LlamaServerArgs.DefaultAlias, r.GetProperty("model").GetString());
        Assert.Equal("привет", r.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(r.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("low", r.GetProperty("reasoning_effort").GetString());
        Assert.Equal(1.5, r.GetProperty("presence_penalty").GetDouble());
        Assert.Equal(10, r.GetProperty("max_tokens").GetInt32());
    }
}

public sealed class LlamaClientTests
{
    private static int UnusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Theory]
    [InlineData(200, HealthState.Ready)]
    [InlineData(503, HealthState.Loading)]
    [InlineData(500, HealthState.Down)]
    public async Task Health_MapsStatus(int status, HealthState expected)
    {
        await using var server = new FakeHttpServer((_, s, _) =>
            FakeHttpServer.WriteResponseAsync(s, status, status == 200 ? "{\"status\":\"ok\"}" : "{\"error\":{\"message\":\"Loading model\",\"type\":\"unavailable_error\",\"code\":503}}"));
        var client = new LlamaClient(server.BaseUrl, "key");
        Assert.Equal(expected, await client.GetHealthAsync());
        // /health — без ключа.
        Assert.False(server.Requests[0].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Health_Refused_IsDown()
    {
        var client = new LlamaClient($"http://127.0.0.1:{UnusedPort()}", "key");
        Assert.Equal(HealthState.Down, await client.GetHealthAsync());
    }

    [Fact]
    public async Task Chat_Streaming_SendsBearerAndParses()
    {
        await using var server = new FakeHttpServer(async (req, s, _) =>
        {
            await FakeHttpServer.WriteSseHeadersAsync(s);
            // Отправляем кусками, разрывая строки посередине.
            var text = ChatStreamParserTests.CannedStream;
            for (var i = 0; i < text.Length; i += 37)
            {
                await FakeHttpServer.WriteRawAsync(s, text.Substring(i, Math.Min(37, text.Length - i)));
                await Task.Delay(1);
            }
        });
        var client = new LlamaClient(server.BaseUrl, "pc-key");
        var deltas = new List<string>();
        var r = await client.ChatAsync(new ChatRequest([ChatMessage.User("hi")]), deltas.Add);
        Assert.Equal("def add(a, b):", r.Content);
        Assert.Equal("Думаю...", r.Reasoning);
        Assert.Equal(15, r.PromptTokens);
        Assert.Equal(8, r.CompletionTokens);
        Assert.Equal(2, deltas.Count);
        var req = Assert.Single(server.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1/chat/completions", req.Path);
        Assert.Equal("Bearer pc-key", req.Headers["Authorization"]);
        using var body = JsonDocument.Parse(req.Body);
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task Chat_401_RussianMessage()
    {
        await using var server = new FakeHttpServer((_, s, _) =>
            FakeHttpServer.WriteResponseAsync(s, 401, "{\"error\":{\"message\":\"Invalid API Key\",\"type\":\"authentication_error\",\"code\":401}}"));
        var client = new LlamaClient(server.BaseUrl, "wrong");
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() => client.ChatAsync(new ChatRequest([ChatMessage.User("hi")])));
        Assert.Equal(401, ex.StatusCode);
        Assert.StartsWith("Неверный ключ API", ex.Message);
    }

    [Fact]
    public async Task Chat_503_Loading()
    {
        await using var server = new FakeHttpServer((_, s, _) =>
            FakeHttpServer.WriteResponseAsync(s, 503, "{\"error\":{\"message\":\"Loading model\",\"type\":\"unavailable_error\",\"code\":503}}"));
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() => new LlamaClient(server.BaseUrl, "k").ChatAsync(new ChatRequest([ChatMessage.User("hi")])));
        Assert.Equal(503, ex.StatusCode);
        Assert.StartsWith("Модель загружается", ex.Message);
    }

    [Fact]
    public async Task Chat_Refused_ServerNotRunning()
    {
        var client = new LlamaClient($"http://127.0.0.1:{UnusedPort()}", "k");
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() => client.ChatAsync(new ChatRequest([ChatMessage.User("hi")])));
        Assert.Null(ex.StatusCode);
        Assert.StartsWith("Сервер не запущен", ex.Message);
    }

    [Fact]
    public async Task Chat_TruncatedStream_Throws()
    {
        await using var server = new FakeHttpServer(async (_, s, _) =>
        {
            await FakeHttpServer.WriteSseHeadersAsync(s);
            await FakeHttpServer.WriteRawAsync(s, "data: {\"choices\":[{\"delta\":{\"content\":\"half\"}}]}\n\n");
        });
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() => new LlamaClient(server.BaseUrl, "k").ChatAsync(new ChatRequest([ChatMessage.User("hi")])));
        Assert.Contains("оборвался", ex.Message);
    }

    [Fact]
    public async Task Chat_CancelMidStream_AbortsRequest()
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeHttpServer(async (_, s, ct) =>
        {
            await FakeHttpServer.WriteSseHeadersAsync(s);
            try
            {
                for (var i = 0; i < 400; i++)
                {
                    await FakeHttpServer.WriteRawAsync(s, "data: {\"choices\":[{\"delta\":{\"content\":\"t\"}}]}\n\n");
                    await Task.Delay(25, ct);
                }
            }
            catch (IOException)
            {
                disconnected.TrySetResult();
            }
        });
        using var cts = new CancellationTokenSource();
        var client = new LlamaClient(server.BaseUrl, "k");
        var count = 0;
        var task = client.ChatAsync(new ChatRequest([ChatMessage.User("hi")]), _ =>
        {
            if (++count == 3) cts.Cancel();
        }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // Соединение закрыто клиентом — сервер получает ошибку записи (llama-server прекращает генерацию).
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CountTokens_UsesTokenizeOrFallsBack()
    {
        await using var server = new FakeHttpServer((req, s, _) =>
            req.Path == "/tokenize"
                ? FakeHttpServer.WriteResponseAsync(s, 200, "{\"tokens\":[1,2,3,4]}")
                : FakeHttpServer.WriteResponseAsync(s, 404, "{}"));
        Assert.Equal(4, await new LlamaClient(server.BaseUrl, "k").CountTokensAsync("hello world"));
        Assert.Equal("{\"content\":\"hello world\"}", server.Requests[0].Body);

        var down = new LlamaClient($"http://127.0.0.1:{UnusedPort()}", "k");
        Assert.Equal(LlamaClient.EstimateTokens("hello world"), await down.CountTokensAsync("hello world"));
    }

    [Fact]
    public async Task Props_Unauthorized_ReturnsNull()
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 401, "{}"));
        Assert.Null(await new LlamaClient(server.BaseUrl, "k").GetPropsAsync());
    }
}

public sealed class DeviceListTests
{
    [Fact]
    public void Parse_CpuBuild_None()
    {
        Assert.Empty(DeviceList.Parse("Available devices:\n  (none)\n"));
    }

    [Fact]
    public void Parse_CudaAndVulkan()
    {
        var list = DeviceList.Parse(
            "\u001b[34m0.00.000.596\u001b[0m I srv  llama_server: initializing ...\r\n" +
            "Available devices:\r\n" +
            "  CUDA0: NVIDIA GeForce RTX 3090 (24575 MiB, 23000 MiB free)\r\n" +
            "  Vulkan0: NVIDIA GeForce RTX 3090 (24320 MiB, 23000 MiB free)\r\n" +
            "\r\n");
        Assert.Equal(["CUDA0: NVIDIA GeForce RTX 3090 (24575 MiB, 23000 MiB free)", "Vulkan0: NVIDIA GeForce RTX 3090 (24320 MiB, 23000 MiB free)"], list);
    }

    [Fact]
    public void Parse_NoHeader_Empty() => Assert.Empty(DeviceList.Parse("error: something\n"));
}
