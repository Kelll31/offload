using Offload.Core.Config;

namespace Offload.Models.Tests;

public class HfTreeTests
{
    private static IReadOnlyList<HfFile> Fixture() =>
        HfClient.ParseTree(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tree_qwen3_coder_next.json")));

    [Fact]
    public void ParseTree_TakesLfsSizeAndSha()
    {
        var files = Fixture();
        Assert.Equal(16, files.Count); // 20 записей минус 4 папки
        Assert.DoesNotContain(files, f => f.Path is "Q8_0" or "BF16");
        var q4 = files.Single(f => f.Path == "Qwen3-Coder-Next-UD-Q4_K_XL.gguf");
        Assert.Equal(49608478720, q4.Size);
        Assert.Equal("4bb93f0a0221ef4ff963ca9094df629c8dfdfabc3b4fdd85c1a2e4c0624fce36", q4.Sha256);
        Assert.Null(files.Single(f => f.Path == ".gitattributes").Sha256);
    }

    [Fact]
    public void SelectQuantFiles_ShardsInSubfolderInOrder()
    {
        var shards = HfClient.SelectQuantFiles(Fixture(), "Q8_0");
        Assert.Equal(
            ["Q8_0/Qwen3-Coder-Next-Q8_0-00001-of-00003.gguf", "Q8_0/Qwen3-Coder-Next-Q8_0-00002-of-00003.gguf", "Q8_0/Qwen3-Coder-Next-Q8_0-00003-of-00003.gguf"],
            shards.Select(f => f.Path));
        Assert.Equal(5936288L + 49772613984 + 35033705312, shards.Sum(f => f.Size));
    }

    [Fact]
    public void SelectQuantFiles_ExactTagWins()
    {
        var files = Fixture();
        Assert.Equal("Qwen3-Coder-Next-Q4_K_M.gguf", HfClient.SelectQuantFiles(files, "Q4_K_M").Single().Path);
        Assert.Equal("Qwen3-Coder-Next-UD-Q4_K_M.gguf", HfClient.SelectQuantFiles(files, "ud-q4_k_m").Single().Path);
        // Без учёта регистра, и Q5_K_M не путается с UD-Q5_K_M.
        var q5 = HfClient.SelectQuantFiles(files, "q5_k_m");
        Assert.Equal(3, q5.Count);
        Assert.All(q5, f => Assert.StartsWith("Q5_K_M/", f.Path));
        Assert.Equal("Qwen3-Coder-Next-MXFP4_MOE.gguf", HfClient.SelectQuantFiles(files, "MXFP4_MOE").Single().Path);
        Assert.Empty(HfClient.SelectQuantFiles(files, "Q2_K"));
    }

    [Fact]
    public void SelectQuantFiles_SkipsIncompleteShardsAndProjectors()
    {
        var files = Fixture().Where(f => f.Path != "Q8_0/Qwen3-Coder-Next-Q8_0-00002-of-00003.gguf").ToList();
        Assert.Empty(HfClient.SelectQuantFiles(files, "Q8_0"));

        var withExtras = new List<HfFile>
        {
            new("mmproj-Q8_0.gguf", 1, null),
            new("mtp-Model-Q8_0.gguf", 1, null),
            new("Model-Q8_0.gguf", 5, null),
        };
        Assert.Equal("Model-Q8_0.gguf", HfClient.SelectQuantFiles(withExtras, "Q8_0").Single().Path);
    }

    [Theory]
    [InlineData("Qwen3-Coder-Next-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
    [InlineData("Q8_0/Qwen3-Coder-Next-Q8_0-00002-of-00003.gguf", "Q8_0")]
    [InlineData("gpt-oss-20b-MXFP4.gguf", "MXFP4")]
    [InlineData("Kwaipilot_KAT-Coder-V2.5-Dev-IQ4_XS.gguf", "IQ4_XS")]
    [InlineData("qwen2.5-coder-1.5b-q8_0.gguf", "q8_0")]
    [InlineData("Qwen3.5-2B-BF16.gguf", "BF16")]
    [InlineData("mistral-7b.Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("model.gguf", null)]
    public void QuantTag_FromFileName(string path, string? tag) => Assert.Equal(tag, HfClient.QuantTag(path));

    [Fact]
    public void NextLink_ParsesRelNext()
    {
        var next = HfClient.NextLink(["<https://huggingface.co/api/models/a/b/tree/main?recursive=true&cursor=abc>; rel=\"next\""], "https://huggingface.co/api/models/a/b/tree/main?recursive=true");
        Assert.Equal("https://huggingface.co/api/models/a/b/tree/main?recursive=true&cursor=abc", next);
        Assert.Null(HfClient.NextLink(["<https://x/y>; rel=\"prev\""], "https://x/"));
        Assert.Equal("https://huggingface.co/p2", HfClient.NextLink(["</p2>; rel=next"], "https://huggingface.co/api/x"));
    }

    [Fact]
    public void DownloadUrl_WithRevision()
    {
        Assert.Equal(
            $"{HfClient.Endpoint}/unsloth/Qwen3.5-2B-GGUF/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Qwen3.5-2B-Q8_0.gguf",
            HfClient.DownloadUrl("unsloth/Qwen3.5-2B-GGUF", "Qwen3.5-2B-Q8_0.gguf", "f6d5376be1edb4d416d56da11e5397a961aca8ae"));
        Assert.EndsWith("/resolve/main/Q8_0/a%20b.gguf", HfClient.DownloadUrl("o/r", "Q8_0/a b.gguf"));
    }
}

/// <summary>Тесты, меняющие HfClient.Endpoint, выполняются последовательно.</summary>
[Collection("AppPaths")]
public class HfClientTests
{
    private static CatalogModel Model(string repo, IReadOnlyList<CatalogFile>? files = null, string quant = "Q4_K_M") =>
        new("test-model", "Тест", "Описание", repo, [quant], files?.FirstOrDefault()?.Size ?? 1, 1, 1, false, 4096, 4096,
            new KvSpec(1, 1, 64), false, new SamplingSettings(), "MIT", 1, Files: files ?? [], Revision: null);

    [Fact]
    public async Task ResolveAsync_CatalogFile_NeedsNoNetwork()
    {
        var saved = HfClient.Endpoint;
        HfClient.Endpoint = "http://127.0.0.1:9"; // недоступный адрес: любой сетевой запрос упадёт
        try
        {
            var m = ModelCatalog.Find("qwen3.6-35b-a3b-q4")!;
            var r = await HfClient.ResolveAsync(m, ct: TestContext.Current.CancellationToken);
            Assert.Equal("UD-Q4_K_XL", r.Quant);
            Assert.Equal(m.ApproxSizeBytes, r.TotalSize);
            Assert.Equal(m.DefaultFile!.Sha256, r.Files.Single().Sha256);

            var q8 = await HfClient.ResolveAsync(m, "q8_0", TestContext.Current.CancellationToken);
            Assert.Equal("Qwen3.6-35B-A3B-Q8_0.gguf", q8.Files.Single().Path);
        }
        finally
        {
            HfClient.Endpoint = saved;
        }
    }

    [Fact]
    public async Task ListFilesAsync_FollowsPagination_AndResolvesShards()
    {
        using var hub = new LocalHub { PageSize = 5 };
        foreach (var e in System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(
                     File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tree_qwen3_coder_next.json")))!)
            hub.AddTreeEntry("unsloth/Qwen3-Coder-Next-GGUF", e);
        var saved = HfClient.Endpoint;
        HfClient.Endpoint = hub.BaseUrl;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var files = await HfClient.ListFilesAsync("unsloth/Qwen3-Coder-Next-GGUF", ct);
            Assert.Equal(16, files.Count);
            Assert.Equal(4, hub.Log.Count(l => l.Contains("/tree/"))); // 20 записей по 5

            var model = Model("unsloth/Qwen3-Coder-Next-GGUF");
            var resolved = await HfClient.ResolveAsync(model, "Q8_0", ct);
            Assert.Equal("Q8_0", resolved.Quant);
            Assert.Equal(3, resolved.Files.Count);
            Assert.All(resolved.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256!));

            var ex = await Assert.ThrowsAsync<ModelException>(() => HfClient.ResolveAsync(model, "Q1_Z", ct));
            Assert.Contains("не найден файл GGUF с квантизацией Q1_Z", ex.Message);
            Assert.Contains("UD-Q4_K_XL", ex.Message);

            var missing = await Assert.ThrowsAsync<ModelException>(() => HfClient.ListFilesAsync("nobody/nothing", ct));
            Assert.Contains("не найден", missing.Message);
            await Assert.ThrowsAsync<ModelException>(() => HfClient.ListFilesAsync("not a repo", ct));
        }
        finally
        {
            HfClient.Endpoint = saved;
        }
    }

    [Fact]
    public async Task ResolveAsync_CatalogShards_UseTreeForExtraParts()
    {
        using var hub = new LocalHub();
        hub.AddFile("o/split", "Q8_0/M-Q8_0-00001-of-00002.gguf", [1, 2, 3], new string('a', 64));
        hub.AddFile("o/split", "Q8_0/M-Q8_0-00002-of-00002.gguf", [4, 5], new string('b', 64));
        var saved = HfClient.Endpoint;
        HfClient.Endpoint = hub.BaseUrl;
        try
        {
            var model = Model("o/split", [new CatalogFile("Q8_0", "Q8_0/M-Q8_0-00001-of-00002.gguf", 3, new string('a', 64), ["Q8_0/M-Q8_0-00002-of-00002.gguf"])], "Q8_0");
            var r = await HfClient.ResolveAsync(model, null, TestContext.Current.CancellationToken);
            Assert.Equal([3L, 2L], r.Files.Select(f => f.Size));
            Assert.Equal(new string('b', 64), r.Files[1].Sha256);
        }
        finally
        {
            HfClient.Endpoint = saved;
        }
    }
}
