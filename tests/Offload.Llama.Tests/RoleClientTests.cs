using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Offload.Core.Config;

namespace Offload.Llama.Tests;

/// <summary>Эмбеддинги, реранк и структурированный вывод (ROADMAP §5.2, §6.3, §9.2) — против поддельного сервера.</summary>
public sealed class RoleClientTests
{
    private static int UnusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Embed_SendsInputsAndModel_ParsesVectorsByIndex()
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 200,
            """
            {"model":"offload-embed","object":"list","usage":{"prompt_tokens":7,"total_tokens":7},
             "data":[{"object":"embedding","index":1,"embedding":[0.5,-0.25]},{"object":"embedding","index":0,"embedding":[1,0]}]}
            """));
        var client = new LlamaClient(server.BaseUrl, "pc-key") { Model = "offload-embed" };

        var r = await client.EmbedAsync(["первый", "второй"], TestContext.Current.CancellationToken);

        Assert.Equal(2, r.Vectors.Count);
        Assert.Equal([1f, 0f], r.Vectors[0]);
        Assert.Equal([0.5f, -0.25f], r.Vectors[1]);
        Assert.Equal(7, r.PromptTokens);
        var req = Assert.Single(server.Requests);
        Assert.Equal(("POST", "/v1/embeddings"), (req.Method, req.Path));
        Assert.Equal("Bearer pc-key", req.Headers["Authorization"]);
        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("offload-embed", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(["первый", "второй"], body.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Embed_EmptyInput_NoRequest()
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 500, "{}"));
        var r = await new LlamaClient(server.BaseUrl, "k").EmbedAsync([], TestContext.Current.CancellationToken);
        Assert.Empty(r.Vectors);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void ParseEmbeddings_MissingVector_Throws()
    {
        var ex = Assert.Throws<LlamaApiException>(() =>
            LlamaClient.ParseEmbeddings("""{"data":[{"index":0,"embedding":[1]}]}""", 2, TimeSpan.Zero));
        Assert.Equal(LlamaErrorKind.Other, ex.Kind);
        Assert.Contains("expected 2", ex.Detail);
    }

    [Fact]
    public async Task Embed_ServerWithoutEmbeddings_RejectedWithDetail()
    {
        // llama-server без --embeddings отвечает 501 с текстом ошибки.
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 501,
            """{"error":{"code":501,"message":"This server does not support embeddings. Start it with `--embeddings`","type":"not_supported_error"}}"""));
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() =>
            new LlamaClient(server.BaseUrl, "k").EmbedAsync(["x"], TestContext.Current.CancellationToken));
        Assert.Equal(LlamaErrorKind.ServerError, ex.Kind);
        Assert.Contains("--embeddings", ex.Detail);
    }

    [Fact]
    public async Task Embed_401_Unauthorized()
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 401, "{}"));
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() =>
            new LlamaClient(server.BaseUrl, "k").EmbedAsync(["x"], TestContext.Current.CancellationToken));
        Assert.Equal(LlamaErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task Rerank_Refused_NotRunning()
    {
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() =>
            new LlamaClient($"http://127.0.0.1:{UnusedPort()}", "k").RerankAsync("q", ["a"], ct: TestContext.Current.CancellationToken));
        Assert.Equal(LlamaErrorKind.NotRunning, ex.Kind);
    }

    [Fact]
    public async Task Rerank_SendsQueryDocumentsTopN_SortsByRelevance()
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 200,
            """
            {"model":"offload-rerank","object":"list","usage":{"prompt_tokens":30,"total_tokens":30},
             "results":[{"index":0,"relevance_score":-2.5},{"index":2,"relevance_score":4.25},{"index":1,"relevance_score":0.5}]}
            """));
        var client = new LlamaClient(server.BaseUrl, "pc-key") { Model = "offload-rerank" };

        var r = await client.RerankAsync("где парсер?", ["a.cs", "b.cs", "parser.cs"], topN: 3, ct: TestContext.Current.CancellationToken);

        Assert.Equal([2, 1, 0], r.Scores.Select(x => x.Index));
        Assert.Equal(4.25, r.Scores[0].Score);
        Assert.Equal(30, r.PromptTokens);
        var req = Assert.Single(server.Requests);
        Assert.Equal("/v1/rerank", req.Path);
        using var body = JsonDocument.Parse(req.Body);
        var root = body.RootElement;
        Assert.Equal("offload-rerank", root.GetProperty("model").GetString());
        Assert.Equal("где парсер?", root.GetProperty("query").GetString());
        Assert.Equal(3, root.GetProperty("documents").GetArrayLength());
        Assert.Equal(3, root.GetProperty("top_n").GetInt32());
    }

    [Fact]
    public void ParseRerank_IndexOutOfRange_Throws()
    {
        Assert.Throws<LlamaApiException>(() =>
            LlamaClient.ParseRerank("""{"results":[{"index":5,"relevance_score":1}]}""", 2, TimeSpan.Zero));
        Assert.Throws<LlamaApiException>(() => LlamaClient.ParseRerank("not json", 2, TimeSpan.Zero));
    }

    [Fact]
    public void BuildChatBody_ResponseFormat_JsonSchema()
    {
        var format = ResponseFormat.JsonSchema("files", """{"type":"object","properties":{"files":{"type":"array","items":{"type":"string"}}},"required":["files"]}""");
        var body = LlamaClient.BuildChatBody(new ChatRequest([ChatMessage.User("x")], ResponseFormat: format), "offload-fast");

        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        Assert.Equal("offload-fast", r.GetProperty("model").GetString());
        var rf = r.GetProperty("response_format");
        Assert.Equal("json_schema", rf.GetProperty("type").GetString());
        var js = rf.GetProperty("json_schema");
        Assert.Equal("files", js.GetProperty("name").GetString());
        Assert.True(js.GetProperty("strict").GetBoolean());
        Assert.Equal("array", js.GetProperty("schema").GetProperty("properties").GetProperty("files").GetProperty("type").GetString());
    }

    [Fact]
    public void BuildChatBody_WithoutFormat_NoResponseFormat_DefaultAlias()
    {
        using var doc = JsonDocument.Parse(LlamaClient.BuildChatBody(new ChatRequest([ChatMessage.User("x")])));
        Assert.False(doc.RootElement.TryGetProperty("response_format", out _));
        Assert.Equal(LlamaServerArgs.DefaultAlias, doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ResponseFormat_NotObject_Rejected()
    {
        Assert.Throws<ArgumentException>(() => ResponseFormat.JsonSchema("x", "[1]"));
        Assert.ThrowsAny<JsonException>(() => ResponseFormat.JsonSchema("x", "{oops"));
    }

    [Fact]
    public async Task Chat_SendsResponseFormatAndClientModel()
    {
        await using var server = new FakeHttpServer((_, s, _) => FakeHttpServer.WriteResponseAsync(s, 200,
            """{"choices":[{"message":{"content":"{\"files\":[\"a.cs\"]}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":5}}"""));
        var client = new LlamaClient(server.BaseUrl, "k") { Model = "offload-fast" };
        var format = ResponseFormat.JsonSchema("files", """{"type":"object"}""");

        var r = await client.ChatAsync(new ChatRequest([ChatMessage.User("x")], ResponseFormat: format), ct: TestContext.Current.CancellationToken);

        Assert.Equal("{\"files\":[\"a.cs\"]}", r.Content);
        using var body = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        Assert.Equal("offload-fast", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public void ForRole_UsesRolePortAliasAndSharedKey()
    {
        var cfg = new AppConfig();
        cfg.Server.Port = 9000;
        cfg.Server.ApiKey = "pc-k";

        var main = LlamaClient.ForRole(cfg, ModelRole.Quality);
        var fast = LlamaClient.ForRole(cfg, ModelRole.Fast);
        cfg.Server.AuxPorts["embed"] = 9100;
        var embed = LlamaClient.ForRole(cfg, ModelRole.Embed);

        Assert.Equal(("http://127.0.0.1:9000", LlamaServerArgs.DefaultAlias), (main.BaseUrl, main.Model));
        Assert.Equal(("http://127.0.0.1:9001", "offload-fast", "pc-k"), (fast.BaseUrl, fast.Model, fast.ApiKey));
        Assert.Equal(("http://127.0.0.1:9100", "offload-embed"), (embed.BaseUrl, embed.Model));
    }
}

/// <summary>Аргументы вспомогательных серверов ролей.</summary>
public sealed class AuxServerArgsTests
{
    private static (AppConfig Cfg, InstalledModel Model) Setup(ModelKind kind, string? pooling = null)
    {
        var cfg = new AppConfig();
        cfg.Server.ApiKey = "secret-key";
        cfg.Server.ExtraArgs = "--n-cpu-moe 10 --chat-template-kwargs '{}'";
        cfg.Server.CacheType = "q8_0";
        cfg.Server.Parallel = 4;
        var model = new InstalledModel
        {
            Id = "m", FilePath = @"C:\models\m.gguf", Kind = kind, Pooling = pooling,
            NativeContext = 32768, RecommendedContext = 16384,
            Sampling = new SamplingSettings { Temperature = 0.3, TopP = 0.9, TopK = 40 },
        };
        return (cfg, model);
    }

    private static string? ValueOf(IReadOnlyList<string> a, string flag)
    {
        var i = a.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
    }

    [Fact]
    public void Fast_OneSlot_ChatFlags_NoExtraArgs_NoKey()
    {
        var (cfg, model) = Setup(ModelKind.Chat);
        var plan = AuxServerArgs.Build(cfg, ModelRole.Fast, model, @"C:\llama\llama-server.exe", 8766);
        var a = plan.Arguments;

        Assert.Equal("8766", ValueOf(a, "--port"));
        Assert.Equal("offload-fast", ValueOf(a, "--alias"));
        Assert.Equal("offload-fast", plan.ModelAlias);
        Assert.Equal("1", ValueOf(a, "-np"));
        Assert.Equal("16384", ValueOf(a, "-c"));
        Assert.Equal((16384, 1), (plan.ContextSize, plan.Parallel));
        Assert.Contains("--jinja", a);
        Assert.Equal("0.3", ValueOf(a, "--temp"));
        Assert.Equal("q8_0", ValueOf(a, "-ctk"));
        Assert.DoesNotContain("--n-cpu-moe", a);
        Assert.DoesNotContain("-kvu", a);
        Assert.DoesNotContain("--embeddings", a);
        Assert.DoesNotContain(a, x => x.Contains("secret-key", StringComparison.Ordinal));
    }

    [Fact]
    public void Embed_EmbeddingsPoolingAndBatchEqualsContext()
    {
        var (cfg, model) = Setup(ModelKind.Embed, "last");
        var a = AuxServerArgs.Build(cfg, ModelRole.Embed, model, @"C:\llama\llama-server.exe", 8767).Arguments;

        Assert.Contains("--embeddings", a);
        Assert.Equal("last", ValueOf(a, "--pooling"));
        Assert.Equal("8192", ValueOf(a, "-c"));
        Assert.Equal("8192", ValueOf(a, "-b"));
        Assert.Equal("8192", ValueOf(a, "-ub"));
        Assert.Equal("offload-embed", ValueOf(a, "--alias"));
        Assert.DoesNotContain("--jinja", a);
        Assert.DoesNotContain("--temp", a);
    }

    [Fact]
    public void Embed_UnknownPooling_LeftToGguf()
    {
        var (cfg, model) = Setup(ModelKind.Embed, "weird");
        Assert.DoesNotContain("--pooling", AuxServerArgs.Build(cfg, ModelRole.Embed, model, @"C:\l\llama-server.exe", 1).Arguments);
        Assert.Equal("mean", AuxServerArgs.NormalizePooling(" MEAN "));
        Assert.Null(AuxServerArgs.NormalizePooling("none"));
    }

    [Fact]
    public void Rerank_RerankingFlag()
    {
        var (cfg, model) = Setup(ModelKind.Rerank);
        var a = AuxServerArgs.Build(cfg, ModelRole.Rerank, model, @"C:\llama\llama-server.exe", 8768).Arguments;

        Assert.Contains("--reranking", a);
        Assert.DoesNotContain("--embeddings", a);
        Assert.Equal("offload-rerank", ValueOf(a, "--alias"));
        Assert.Equal("8192", ValueOf(a, "-ub"));
    }

    [Fact]
    public void Quality_NotAnAuxRole()
    {
        var (cfg, model) = Setup(ModelKind.Chat);
        Assert.Throws<ArgumentException>(() => AuxServerArgs.Build(cfg, ModelRole.Quality, model, @"C:\l\llama-server.exe", 1));
        Assert.Equal(LlamaServerArgs.DefaultAlias, AuxServerArgs.AliasFor(ModelRole.Quality));
    }
}
