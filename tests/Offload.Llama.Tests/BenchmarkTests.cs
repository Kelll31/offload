using System.Text.Json;
using Offload.Core.Config;

namespace Offload.Llama.Tests;

/// <summary>Встроенный замер скорости (LlamaBenchmark) на поддельном сервере.</summary>
[Collection("AppPaths")]
public sealed class BenchmarkTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private static string Stream(double pps, double gps, int prompt, int completion) =>
        "data: {\"choices\":[{\"delta\":{\"content\":\"The function\"}}]}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"length\",\"delta\":{}}],\"timings\":{\"prompt_n\":" + prompt +
        ",\"prompt_per_second\":" + pps.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"predicted_n\":" + completion + ",\"predicted_per_second\":" + gps.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"completion_tokens\":" + completion + ",\"prompt_tokens\":" + prompt + ",\"total_tokens\":" + (prompt + completion) + "}}\n\n" +
        "data: [DONE]\n\n";

    private static FakeHttpServer Server(Func<string, string> reply) => new(async (req, s, _) =>
    {
        await FakeHttpServer.WriteSseHeadersAsync(s);
        await FakeHttpServer.WriteRawAsync(s, reply(req.Body));
    });

    [Fact]
    public async Task Run_WarmsUpThenMeasures()
    {
        var bodies = new List<string>();
        await using var server = Server(body =>
        {
            lock (bodies) bodies.Add(body);
            return Stream(1520.4, 48.25, 1510, LlamaBenchmark.GenerationTokens);
        });
        var r = await LlamaBenchmark.RunAsync(new LlamaClient(server.BaseUrl, "k"), TestContext.Current.CancellationToken);

        Assert.Equal(1520.4, r.PromptTokensPerSecond);
        Assert.Equal(48.25, r.GenerationTokensPerSecond);
        Assert.Equal(1510, r.PromptTokens);
        Assert.Equal(LlamaBenchmark.GenerationTokens, r.CompletionTokens);

        Assert.Equal(2, bodies.Count);
        using var warm = JsonDocument.Parse(bodies[0]);
        Assert.Equal(1, warm.RootElement.GetProperty("max_tokens").GetInt32());
        using var main = JsonDocument.Parse(bodies[1]);
        Assert.Equal(LlamaBenchmark.GenerationTokens, main.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Contains("BinarySearch", bodies[1]);
        Assert.Equal("Bearer k", server.Requests[1].Headers["Authorization"]);
    }

    [Fact]
    public void Prompt_UniquePerRun_SoPromptCacheIsNotReused()
    {
        var a = LlamaBenchmark.BuildPrompt("aaa");
        var b = LlamaBenchmark.BuildPrompt("bbb");
        Assert.NotEqual(a, b);
        Assert.StartsWith("Benchmark run aaa.", a);
        Assert.Equal(a.Length, b.Length);
        Assert.InRange(LlamaClient.EstimateTokens(a), 800, 4000);
    }

    [Fact]
    public async Task Run_NoTimings_Throws()
    {
        await using var server = Server(_ =>
            "data: {\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{\"content\":\"x\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"completion_tokens\":1,\"prompt_tokens\":5,\"total_tokens\":6}}\n\ndata: [DONE]\n\n");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LlamaBenchmark.RunAsync(new LlamaClient(server.BaseUrl, "k"), TestContext.Current.CancellationToken));
        Assert.Contains("timings", ex.Message);
    }

    [Fact]
    public async Task Run_ServerError_PropagatesApiException()
    {
        await using var server = new FakeHttpServer((_, s, _) =>
            FakeHttpServer.WriteResponseAsync(s, 401, "{\"error\":{\"code\":401,\"message\":\"Invalid API Key\",\"type\":\"authentication_error\"}}"));
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() =>
            LlamaBenchmark.RunAsync(new LlamaClient(server.BaseUrl, "wrong"), TestContext.Current.CancellationToken));
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task MeasureAndSave_StoresLastResultPerModel()
    {
        ConfigStore.Update(c => c.Llama.InstalledTag = "b7000");
        var gps = 40.0;
        await using var server = Server(_ => Stream(900, gps, 1500, 160));
        var plan = new ServerLaunchPlan(@"C:\llama\llama-server.exe", [], 65536, 2, 4, LlamaServerArgs.DefaultAlias);
        var client = new LlamaClient(server.BaseUrl, "k");

        var first = await LlamaBenchmark.MeasureAndSaveAsync(client, "qwen3.6-35b-a3b-q4", plan, TestContext.Current.CancellationToken);
        Assert.Equal(40.0, first.GenerationTokensPerSecond);
        Assert.Equal(65536, first.ContextSize);
        Assert.Equal(2, first.Parallel);
        Assert.Equal(4, first.CpuMoeLayers);
        Assert.Equal("b7000", first.LlamaTag);

        gps = 55.55;
        await LlamaBenchmark.MeasureAndSaveAsync(client, "qwen3.6-35b-a3b-q4", null, TestContext.Current.CancellationToken);
        await LlamaBenchmark.MeasureAndSaveAsync(client, "qwen3.5-9b-q4", plan, TestContext.Current.CancellationToken);

        var cfg = ConfigStore.Reload();
        Assert.Equal(2, cfg.Server.LastBenchmark.Count);
        var last = LlamaBenchmark.Last(cfg, "qwen3.6-35b-a3b-q4");
        Assert.NotNull(last);
        Assert.Equal(55.6, last.GenerationTokensPerSecond); // округление до 0,1
        Assert.Equal(0, last.ContextSize);
        Assert.Equal(-1, last.CpuMoeLayers);
        Assert.Null(LlamaBenchmark.Last(cfg, "missing"));
        Assert.Null(LlamaBenchmark.Last(cfg, null));
    }
}
