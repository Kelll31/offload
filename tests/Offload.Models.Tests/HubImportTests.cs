using System.Security.Cryptography;
using System.Text.Json;
using Offload.Core.Config;

namespace Offload.Models.Tests;

/// <summary>Поиск на Hugging Face, группировка квантов, чтение заголовка по частям, запись и выбор кванта — без сети.</summary>
public class HubParsingTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void ParseSearch_RealResponse()
    {
        var list = HfClient.ParseSearch(Fixture("hf_search_gguf.json"));
        Assert.Equal(4, list.Count);
        var qwen = list[0];
        Assert.Equal("Qwen/Qwen2.5-Coder-1.5B-Instruct-GGUF", qwen.Repo);
        Assert.Equal("f86cb2c1fa58255f8052cc32aeede1b7482d4361", qwen.Sha);
        Assert.Equal((93870L, 118L), (qwen.Downloads, qwen.Likes));
        Assert.Equal("apache-2.0", qwen.License);
        Assert.Equal(HfGate.None, qwen.Gated);
        Assert.Equal("text-generation", qwen.PipelineTag);
        Assert.Equal(new DateTimeOffset(2024, 11, 12, 2, 56, 34, TimeSpan.Zero), qwen.LastModified);

        var gemma = list.Single(r => r.Repo == "google/gemma-3-4b-it-qat-q4_0-gguf");
        Assert.Equal(HfGate.Manual, gemma.Gated);
        Assert.True(gemma.IsGated);
        Assert.Equal("gemma", gemma.License);
    }

    [Fact]
    public void ParseRepo_InfoResponse_AndOddShapes()
    {
        using var doc = JsonDocument.Parse(Fixture("hf_info_bartowski_qwen25_coder_15b.json"));
        var info = HfClient.ParseRepo(doc.RootElement)!;
        Assert.Equal("bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF", info.Repo);
        Assert.Equal("1af47f78b1f9b0c242fabe43f7a365d5a67f3207", info.Sha);
        Assert.Equal("apache-2.0", info.License);

        var odd = HfClient.ParseSearch("""
            [{"id":"a/b","gated":"auto","sha":"main","cardData":{"license":"other","license_name":"my-license"}},
             {"id":"../evil"}, {"modelId":"c/d","tags":["gguf","license:mit"]}, 42]
            """);
        Assert.Equal(2, odd.Count);
        Assert.Equal((HfGate.Auto, (string?)null, "my-license"), (odd[0].Gated, odd[0].Sha, odd[0].License));
        Assert.Equal(("c/d", "mit"), (odd[1].Repo, odd[1].License));
    }

    [Fact]
    public void SearchUrl_HasGgufFilterSortAndFields()
    {
        var url = HfClient.SearchUrl("qwen coder & co", HfSort.Trending, 500);
        Assert.StartsWith(HfClient.Endpoint + "/api/models?", url);
        Assert.Contains("search=qwen%20coder%20%26%20co", url);
        Assert.Contains("filter=gguf", url);
        Assert.Contains("sort=trendingScore", url);
        Assert.Contains("limit=100", url);
        Assert.Contains("expand%5B%5D=sha", url);
        Assert.Contains("expand%5B%5D=cardData", url);
        Assert.Contains("sort=lastModified", HfClient.SearchUrl("", HfSort.Updated, 5));
        Assert.DoesNotContain("search=", HfClient.SearchUrl("", HfSort.Downloads, 5));
    }

    [Fact]
    public void GroupQuants_SingleFiles_SkipsNonGguf()
    {
        var files = HfClient.ParseTree(Fixture("tree_bartowski_qwen25_coder_15b.json"));
        var quants = HfClient.GroupQuants(files);
        Assert.Equal(24, quants.Count); // 27 записей без .gitattributes, README.md и .imatrix
        Assert.All(quants, q => Assert.Single(q.Files));
        Assert.All(quants, q => Assert.Matches("^[0-9a-f]{64}$", q.Files[0].Sha256!));
        Assert.Equal(quants.OrderBy(q => q.TotalSize).Select(q => q.Quant), quants.Select(q => q.Quant));
        Assert.Contains(quants, q => q.Quant == "Q4_0_4_4");
        Assert.Contains(quants, q => q.Quant == "f16");
    }

    [Fact]
    public void GroupQuants_SplitModelIsOneOptionWithAllParts()
    {
        var files = HfClient.ParseTree(Fixture("tree_qwen3_coder_next.json"));
        var q8 = HfClient.GroupQuants(files).Single(q => q.Quant == "Q8_0");
        Assert.True(q8.IsSplit);
        Assert.Equal(
            ["Q8_0/Qwen3-Coder-Next-Q8_0-00001-of-00003.gguf", "Q8_0/Qwen3-Coder-Next-Q8_0-00002-of-00003.gguf", "Q8_0/Qwen3-Coder-Next-Q8_0-00003-of-00003.gguf"],
            q8.Files.Select(f => f.Path));
        Assert.Equal(5936288L + 49772613984 + 35033705312, q8.TotalSize);
    }

    [Fact]
    public void GroupQuants_SkipsProjectorsIncompleteShardsAndFilesWithoutSha()
    {
        var sha = new string('a', 64);
        var files = new List<HfFile>
        {
            new("mmproj-model-f16.gguf", 10, sha),
            new("Model-Q4_K_M.gguf", 100, sha),
            new("Model-Q5_K_M.gguf", 120, null), // без SHA-256 — не предлагается
            new("Model-Q8_0-00001-of-00002.gguf", 100, sha), // второй части нет
            new("Model-Q6_K-00002-of-00002.gguf", 60, sha),
            new("Model-Q6_K-00001-of-00002.gguf", 80, sha),
            new("README.md", 1, null),
            new("../Evil-Q2_K.gguf", 5, sha),
        };
        var quants = HfClient.GroupQuants(files);
        Assert.Equal(["Q4_K_M", "Q6_K"], quants.Select(q => q.Quant));
        Assert.Equal(["Model-Q6_K-00001-of-00002.gguf", "Model-Q6_K-00002-of-00002.gguf"], quants[1].Files.Select(f => f.Path));
    }

    [Theory]
    [InlineData("1.5B", 1.5, 1.5)]
    [InlineData("30B-A3B", 30, 3)]
    [InlineData("235B-A22B", 235, 22)]
    [InlineData("8x7B", 56, 0)]
    [InlineData("500M", 0.5, 0.5)]
    [InlineData(null, 0, 0)]
    [InlineData("large", 0, 0)]
    public void ParseSizeLabel_Works(string? label, double total, double active) =>
        Assert.Equal((total, active), HubImport.ParseSizeLabel(label));

    [Theory]
    [InlineData("Q4_K_M", 4)]
    [InlineData("UD-Q3_K_XL", 3)]
    [InlineData("IQ2_XXS", 2)]
    [InlineData("Q8_0", 8)]
    [InlineData("BF16", 16)]
    [InlineData("f16", 16)]
    [InlineData("MXFP4_MOE", 4)]
    [InlineData("TQ1_0", 1)]
    [InlineData("model", 4)]
    public void QuantBits_FromTag(string quant, int bits) => Assert.Equal(bits, HubImport.QuantBits(quant));

    private static HubQuantFit QF(string quant, long size, FitLevel level, int ctx = 32768) =>
        new(new HfQuantOption(quant, [new HfFile($"m-{quant}.gguf", size, null)]),
            new FitResult(level, size, 0, size, 0, ctx, 0, -1, "test"));

    [Fact]
    public void Recommend_LargestAtBestLevelWithSaneBits()
    {
        Assert.Equal("Q6_K", HubImport.Recommend(
        [
            QF("Q8_0", 900, FitLevel.PartialGpu), QF("Q6_K", 700, FitLevel.FullGpu), QF("Q4_K_M", 500, FitLevel.FullGpu), QF("Q2_K", 300, FitLevel.FullGpu),
        ]));
        // F16 не лучше Q8_0 при вдвое большем размере; 2 бита — только если иначе никак.
        Assert.Equal("Q8_0", HubImport.Recommend([QF("F16", 1600, FitLevel.FullGpu), QF("Q8_0", 900, FitLevel.FullGpu)]));
        Assert.Equal("Q2_K", HubImport.Recommend([QF("Q4_K_M", 500, FitLevel.TooLarge), QF("Q2_K", 300, FitLevel.CpuOnly)]));
        // MoE с выгрузкой лучше, чем частично на GPU, даже если файл меньше.
        Assert.Equal("Q4_K_M", HubImport.Recommend([QF("Q8_0", 900, FitLevel.PartialGpu), QF("Q4_K_M", 500, FitLevel.MoeOffload)]));
        // Контекст меньше 16K — только если с нормальным контекстом не помещается ничего.
        Assert.Equal("Q4_K_M", HubImport.Recommend([QF("Q6_K", 700, FitLevel.FullGpu, 8192), QF("Q4_K_M", 500, FitLevel.PartialGpu)]));
        Assert.Null(HubImport.Recommend([QF("Q4_K_M", 500, FitLevel.TooLarge)]));
        Assert.Null(HubImport.Recommend([new HubQuantFit(new HfQuantOption("Q4_K_M", []), null)]));
    }

    // ── Заголовок GGUF по частям ────────────────────────────────────────────────

    internal static GgufWriter DenseHeader() => new GgufWriter()
        .Str("general.architecture", "llama")
        .Str("general.name", "Test Coder")
        .Str("general.size_label", "7B")
        .U32("general.file_type", 15)
        .U32("llama.block_count", 32)
        .U32("llama.context_length", 131072)
        .U32("llama.embedding_length", 4096)
        .U32("llama.attention.head_count", 32)
        .U32("llama.attention.head_count_kv", 8)
        .StrArray("tokenizer.ggml.tokens", Enumerable.Range(0, 40_000).Select(i => "token-" + i).ToArray())
        .BigU8Array("tokenizer.ggml.token_type", 300_000)
        .Str("tokenizer.chat_template", "{% for m in messages %}{{ m.content }}{% endfor %}{% if tools %}<tool_call>{% endif %}<think>");

    private static Func<long, int, CancellationToken, Task<byte[]>> Fetcher(byte[] file, List<(long From, int Count)> calls) =>
        (from, count, _) =>
        {
            calls.Add((from, count));
            var n = (int)Math.Max(0, Math.Min(count, file.Length - from));
            return Task.FromResult(file.AsSpan((int)from, n).ToArray());
        };

    [Fact]
    public async Task RangeReader_ReadsHeaderInGrowingChunks_WithoutWeights()
    {
        var header = DenseHeader().Build();
        var file = header.Concat(new byte[3_000_000]).ToArray(); // «веса»
        var calls = new List<(long From, int Count)>();
        var (info, read) = await GgufRangeReader.ReadAsync(Fetcher(file, calls), file.Length, initialBytes: 64 * 1024,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(GgufReader.Read(new MemoryStream(file)), info);
        Assert.Equal(("llama", 32, 8, 131072, 15, "7B"), (info.Architecture, info.BlockCount, info.HeadCountKv, info.ContextLength, info.FileType, info.SizeLabel));
        Assert.True(calls.Count > 1, "заголовок больше первой порции — нужны дочитывания");
        Assert.Equal(read, calls.Sum(c => c.Count));
        Assert.True(read < file.Length, $"прочитано {read} из {file.Length} — веса не должны скачиваться");
        Assert.True(read <= header.Length + 2 * 1024 * 1024);
        // Порции идут подряд, без повторного чтения.
        for (var i = 1; i < calls.Count; i++) Assert.Equal(calls[i - 1].From + calls[i - 1].Count, calls[i].From);
    }

    [Fact]
    public async Task RangeReader_BoundsHeaderSize()
    {
        var file = DenseHeader().Build().Concat(new byte[2_000_000]).ToArray();
        var calls = new List<(long From, int Count)>();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => GgufRangeReader.ReadAsync(Fetcher(file, calls), file.Length,
            initialBytes: 16 * 1024, maxBytes: 128 * 1024, ct: TestContext.Current.CancellationToken));
        Assert.Contains("больше", ex.Message);
        Assert.True(calls.Sum(c => c.Count) <= 128 * 1024);
    }

    [Fact]
    public async Task RangeReader_TruncatedOrNotGguf_IsInvalidData()
    {
        var ct = TestContext.Current.CancellationToken;
        var header = DenseHeader().Build();
        var cut = header[..(header.Length / 2)];
        await Assert.ThrowsAsync<InvalidDataException>(() => GgufRangeReader.ReadAsync(Fetcher(cut, []), cut.Length, ct: ct));
        var junk = new byte[4096];
        await Assert.ThrowsAsync<InvalidDataException>(() => GgufRangeReader.ReadAsync(Fetcher(junk, []), junk.Length, ct: ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => GgufRangeReader.ReadAsync(Fetcher(junk, []), 0, ct: ct));
    }

    // ── Запись каталога ─────────────────────────────────────────────────────────

    internal static HubModelDetails Details(GgufInfo header, string pipeline = "text-generation", HfGate gated = HfGate.None)
    {
        string S(char c) => new(c, 64);
        var quants = new List<HfQuantOption>
        {
            new("Q4_K_M", [new HfFile("Coder-Q4_K_M.gguf", 4_000_000_000, S('a'))]),
            new("Q6_K", [new HfFile("Coder-Q6_K.gguf", 5_500_000_000, S('b'))]),
            new("Q8_0", [new HfFile("Q8_0/Coder-Q8_0-00001-of-00002.gguf", 4_000_000_000, S('c')), new HfFile("Q8_0/Coder-Q8_0-00002-of-00002.gguf", 3_100_000_000, S('d'))]),
        };
        var info = new HfRepoInfo("someone/Test-Coder-7B-GGUF", "0123456789abcdef0123456789abcdef01234567", 1000, 10,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "apache-2.0", gated, pipeline);
        return new HubModelDetails(info, info.Sha!, quants, header, "Q4_K_M");
    }

    [Fact]
    public void BuildEntry_IsInstallableCatalogRecord()
    {
        var header = GgufReader.Read(new MemoryStream(DenseHeader().Build()));
        var e = HubImport.BuildEntry(Details(header));

        Assert.Equal("hf-someone--test-coder-7b-gguf", e.Id);
        Assert.Equal("Test-Coder-7B (someone)", e.DisplayName);
        Assert.True(e.FromHub);
        Assert.False(e.Gated);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", e.Revision);
        Assert.Equal(["Q4_K_M", "Q8_0", "Q6_K"], e.Quants); // по умолчанию Q4_K_M, остальные — от большего
        Assert.Equal(4_000_000_000, e.ApproxSizeBytes);
        var q8 = e.FindFile("Q8_0")!;
        Assert.Equal(("Q8_0/Coder-Q8_0-00001-of-00002.gguf", 4_000_000_000L, 7_100_000_000L), (q8.Path, q8.Size, q8.TotalSize));
        Assert.Equal(new string('d', 64), Assert.Single(q8.Parts!).Sha256);
        Assert.Equal(("apache-2.0", "llama", 32), (e.License, e.Architecture, e.BlockCount));
        Assert.Equal((131072, 32768), (e.NativeContext, e.DefaultContext));
        Assert.Equal(new KvSpec(32, 8, 128), e.Kv);
        Assert.Equal((7.0, 7.0, false), (e.ParamsB, e.ActiveParamsB, e.IsMoe));
        Assert.True(e.GoodToolCalling);
        Assert.True(e.IsReasoning);
        Assert.Equal(ModelKind.Chat, e.Role);
        Assert.Contains("Лицензия: apache-2.0", e.Description);
        Assert.Contains("License: apache-2.0", e.DescriptionEn);
        Assert.Contains("не проверена", e.Description);
        Assert.Null(HubCatalog.Problem(e));

        var q6 = HubImport.WithDefaultQuant(e, "q6_k");
        Assert.Equal(["Q6_K", "Q4_K_M", "Q8_0"], q6.Quants);
        Assert.Equal(5_500_000_000, q6.ApproxSizeBytes);
        Assert.Throws<ModelException>(() => HubImport.WithDefaultQuant(e, "Q2_K"));

        var embed = HubImport.BuildEntry(Details(header, "feature-extraction", HfGate.Manual));
        Assert.Equal((ModelKind.Embed, 8192, true, false), (embed.Role, embed.DefaultContext, embed.Gated, embed.GoodToolCalling));
        Assert.Contains("gated", embed.Tags!);
    }

    [Fact]
    public void EvaluateAndRecommend_OnSmallGpu()
    {
        var header = GgufReader.Read(new MemoryStream(DenseHeader().Build()));
        var e = HubImport.BuildEntry(Details(header));
        var fits = HubImport.Evaluate(e, FitTests.Hw(8, 32), new ServerSettings());
        Assert.Equal(3, fits.Count);
        Assert.All(fits, f => Assert.NotNull(f.Fit));
        Assert.Equal(7_100_000_000, fits[0].Size); // разбитый Q8_0 оценивается по сумме частей
        var rec = HubImport.Recommend(fits);
        Assert.NotNull(rec);
        var chosen = fits.Single(f => f.Quant == rec);
        // Наибольший квант, который целиком помещается в видеопамять: всё крупнее — уже нет.
        Assert.Equal(FitLevel.FullGpu, chosen.Fit!.Level);
        Assert.All(fits.Where(f => f.Size > chosen.Size), f => Assert.NotEqual(FitLevel.FullGpu, f.Fit!.Level));
        Assert.NotEqual("Q8_0", rec);

        // Большая карта — берём квант крупнее.
        Assert.Equal("Q8_0", HubImport.Recommend(HubImport.Evaluate(e, FitTests.Hw(24, 64), new ServerSettings())));
        Assert.All(HubImport.Evaluate(e, null, new ServerSettings()), f => Assert.Null(f.Fit));
    }
}

/// <summary>Сценарии с поддельным хабом и папкой данных (последовательно: меняют HfClient.Endpoint и AppPaths).</summary>
[Collection("AppPaths")]
public class HubImportTests
{
    private const string Repo = "test/Split-Coder-GGUF";
    private const string Rev = "89abcdef0123456789abcdef0123456789abcdef";

    private static string InfoJson(string repo, string? sha, object? gated = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["id"] = repo,
        ["sha"] = sha,
        ["gated"] = gated ?? false,
        ["cardData"] = new { license = "mit" },
        ["downloads"] = 5,
        ["likes"] = 1,
        ["lastModified"] = "2026-09-20T10:00:00.000Z",
        ["pipeline_tag"] = "text-generation",
    });

    /// <summary>Хаб с разбитым Q4_K_M (две части), целым Q8_0, проектором и README.</summary>
    private static (LocalHub Hub, byte[] Part1, byte[] Part2, byte[] Q8) Hub()
    {
        var hub = new LocalHub();
        var part1 = HubParsingTests.DenseHeader().Build().Concat(new byte[500_000]).ToArray();
        var part2 = Enumerable.Range(0, 400_000).Select(i => (byte)i).ToArray();
        var q8 = HubParsingTests.DenseHeader().Build().Concat(new byte[5_000_000]).ToArray();
        hub.AddFile(Repo, "Coder-Q4_K_M-00001-of-00002.gguf", part1, HubParsingTests.Sha(part1));
        hub.AddFile(Repo, "Coder-Q4_K_M-00002-of-00002.gguf", part2, HubParsingTests.Sha(part2));
        hub.AddFile(Repo, "Coder-Q8_0.gguf", q8, HubParsingTests.Sha(q8));
        hub.AddFile(Repo, "mmproj-Coder-f16.gguf", [1, 2, 3], HubParsingTests.Sha([1, 2, 3]));
        hub.AddTreeEntry(Repo, new { type = "file", oid = "1", size = 10, path = "README.md" });
        hub.Infos[Repo] = InfoJson(Repo, Rev);
        return (hub, part1, part2, q8);
    }

    private static async Task WithHub(LocalHub hub, Func<Task> body)
    {
        var saved = HfClient.Endpoint;
        HfClient.Endpoint = hub.BaseUrl;
        try
        {
            await body();
        }
        finally
        {
            HfClient.Endpoint = saved;
            HubCatalog.ResetCache();
        }
    }

    [Fact]
    public async Task SearchAsync_SendsGgufQuery_AndParses()
    {
        using var home = new TempHome();
        using var hub = new LocalHub { SearchJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hf_search_gguf.json")) };
        await WithHub(hub, async () =>
        {
            var found = await HfClient.SearchAsync("qwen coder", HfSort.Downloads, 20, TestContext.Current.CancellationToken);
            Assert.Equal(4, found.Count);
            var request = Assert.Single(hub.Log, l => l.StartsWith("GET /api/models?", StringComparison.Ordinal));
            Assert.Contains("search=qwen%20coder", request);
            Assert.Contains("filter=gguf", request);
            Assert.Contains("sort=downloads", request);
            Assert.Contains("limit=20", request);
        });
    }

    [Fact]
    public async Task LoadDetails_PinsRevision_ReadsHeaderByRange_ThenInstallsLikeCatalogModel()
    {
        using var home = new TempHome();
        var (hub, part1, part2, q8) = Hub();
        using var _ = hub;
        await WithHub(hub, async () =>
        {
            var ct = TestContext.Current.CancellationToken;
            var details = await HubImport.LoadDetailsAsync(Repo, ct);

            Assert.Equal(Rev, details.Revision);
            Assert.Contains(hub.Log, l => l.Contains($"/api/models/{Repo}/tree/{Rev}?recursive=true", StringComparison.Ordinal));
            Assert.DoesNotContain(hub.Log, l => l.Contains("/tree/main", StringComparison.Ordinal) || l.Contains("/resolve/main/", StringComparison.Ordinal));
            Assert.Equal(["Q4_K_M", "Q8_0"], details.Quants.Select(q => q.Quant)); // проектор и README пропущены
            Assert.Equal(2, details.Quants[0].Files.Count);
            Assert.Equal("Q4_K_M", details.HeaderQuant);
            Assert.Contains(hub.Log, l => l.Contains($"/resolve/{Rev}/Coder-Q4_K_M-00001-of-00002.gguf", StringComparison.Ordinal));
            Assert.Equal(("llama", 131072), (details.Header.Architecture, details.Header.ContextLength));
            Assert.True(hub.ServedBytes < part1.Length + q8.Length, "заголовок читается частями, а не файлом целиком");

            var entry = HubImport.WithDefaultQuant(HubImport.BuildEntry(details), "Q4_K_M");
            Assert.Equal(part1.Length + part2.Length, entry.ApproxSizeBytes);
            HubCatalog.Save(entry);

            // Запись переживает «перезапуск» и видна там же, где модели каталога (кроме рекомендаций).
            HubCatalog.ResetCache();
            var found = ModelCatalog.Find(entry.Id)!;
            Assert.True(found.FromHub);
            Assert.DoesNotContain(ModelCatalog.All, m => m.Id == entry.Id);
            Assert.DoesNotContain(ModelCatalog.ChatModels, m => m.Id == entry.Id);
            Assert.Contains(ModelCatalog.Available, m => m.Id == entry.Id);
            Assert.Equal(ModelCatalog.All.Count + 1, ModelCatalog.Available.Count);

            // Загрузка — обычным путём: части по закреплённой ревизии, проверка SHA-256, регистрация.
            var resolveBefore = hub.Log.Count(l => l.Contains("/tree/", StringComparison.Ordinal));
            var installed = await ModelManager.DownloadAsync(found, "Q4_K_M", null, ct);
            Assert.Equal(resolveBefore, hub.Log.Count(l => l.Contains("/tree/", StringComparison.Ordinal))); // части уже известны
            Assert.Contains(hub.Log, l => l.Contains($"/resolve/{Rev}/Coder-Q4_K_M-00002-of-00002.gguf", StringComparison.Ordinal));
            Assert.Equal(entry.Id, installed.Id);
            Assert.EndsWith("Coder-Q4_K_M-00001-of-00002.gguf", installed.FilePath);
            Assert.Equal(part2, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(installed.FilePath)!, "Coder-Q4_K_M-00002-of-00002.gguf")));
            Assert.Equal((false, "Q4_K_M", "llama"), (installed.IsCustom, installed.Quant, installed.Architecture));
            Assert.Equal(part1.Length + part2.Length, installed.SizeBytes);
            Assert.Equal(entry.LocalizedDisplayName, ModelCatalog.NameOf(installed));
            Assert.Null(ModelCatalog.HasIqTensors(installed)); // для непроверенной модели неизвестно
            Assert.NotNull(ServerFit.ModelFor(installed, ModelCatalog.Available));
        });
    }

    [Fact]
    public async Task LoadDetails_WithoutRevision_Refuses()
    {
        using var home = new TempHome();
        var (hub, _, _, _) = Hub();
        using var h = hub;
        hub.Infos[Repo] = InfoJson(Repo, null);
        await WithHub(hub, async () =>
        {
            var ex = await Assert.ThrowsAsync<ModelException>(() => HubImport.LoadDetailsAsync(Repo, TestContext.Current.CancellationToken));
            Assert.Contains("ревизию", ex.Message);
            Assert.DoesNotContain(hub.Log, l => l.Contains("/tree/", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task GatedRepo_WithoutToken_ExplainsWhatToDo()
    {
        using var home = new TempHome();
        var (hub, _, _, _) = Hub();
        using var h = hub;
        // С токеном из переменной окружения HF_TOKEN поддельный хаб пустил бы — сценарий не про это.
        Assert.SkipUnless(Offload.Core.Net.NetworkOptions.HfToken is null, "Задан HF_TOKEN");
        hub.Infos[Repo] = InfoJson(Repo, Rev, "manual");
        hub.Gated.Add(Repo);
        await WithHub(hub, async () =>
        {
            var ct = TestContext.Current.CancellationToken;
            var info = await HfClient.GetRepoInfoAsync(Repo, ct);
            Assert.Equal(HfGate.Manual, info.Gated);
            var ex = await Assert.ThrowsAsync<ModelException>(() => HubImport.LoadDetailsAsync(Repo, ct));
            Assert.Contains(Repo, ex.Message);
            Assert.Contains("токен", ex.Message);

            // Неизвестный репозиторий: хаб отвечает 401 — понятная ошибка, а не падение.
            var missing = await Assert.ThrowsAsync<ModelException>(() => HfClient.GetRepoInfoAsync("test/nothing-here", ct));
            Assert.Contains("test/nothing-here", missing.Message);
        });
    }

    [Fact]
    public void HubCatalog_RoundTrip_SkipsBrokenEntries_AndRemoves()
    {
        using var home = new TempHome();
        HubCatalog.ResetCache();
        try
        {
            var header = GgufReader.Read(new MemoryStream(HubParsingTests.DenseHeader().Build()));
            var entry = HubImport.BuildEntry(HubParsingTests.Details(header));
            Assert.Empty(HubCatalog.Models);
            HubCatalog.Save(entry);
            HubCatalog.Save(entry); // повторное сохранение заменяет запись
            Assert.True(File.Exists(HubCatalog.FilePath));

            HubCatalog.ResetCache();
            var loaded = Assert.Single(HubCatalog.Models);
            Assert.Equal(entry.Id, loaded.Id);
            Assert.Equal(entry.Quants, loaded.Quants);
            Assert.Equal(entry.Kv, loaded.Kv);
            Assert.Equal((entry.Revision, entry.License, entry.Role, entry.Reasoning), (loaded.Revision, loaded.License, loaded.Role, loaded.Reasoning));
            Assert.Equal(entry.Files!.Select(f => (f.Quant, f.Path, f.Size, f.Sha256, f.TotalSize)), loaded.Files!.Select(f => (f.Quant, f.Path, f.Size, f.Sha256, f.TotalSize)));
            Assert.Equal(entry.FindFile("Q8_0")!.Parts, loaded.FindFile("Q8_0")!.Parts);
            Assert.True(loaded.FromHub);

            // Запись без SHA-256 или с «main» вместо ревизии не принимается ни при сохранении, ни при чтении.
            var noSha = entry with { Id = "hf-broken", Files = [.. entry.Files!.Select(f => f with { Sha256 = null })] };
            Assert.Throws<ModelException>(() => HubCatalog.Save(noSha));
            Assert.Throws<ModelException>(() => HubCatalog.Save(entry with { Id = "hf-main", Revision = "main" }));
            var text = File.ReadAllText(HubCatalog.FilePath);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            doc["models"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(noSha, Offload.Core.Util.Json.Options)));
            doc["models"]!.AsArray().Add(42);
            File.WriteAllText(HubCatalog.FilePath, doc.ToJsonString());
            HubCatalog.ResetCache();
            Assert.Equal([entry.Id], HubCatalog.Models.Select(m => m.Id));

            File.WriteAllText(HubCatalog.FilePath, "{ не json");
            HubCatalog.ResetCache();
            Assert.Empty(HubCatalog.Models);

            HubCatalog.Save(entry);
            Assert.True(HubCatalog.Remove(entry.Id.ToUpperInvariant()));
            Assert.False(HubCatalog.Remove(entry.Id));
            HubCatalog.ResetCache();
            Assert.Empty(HubCatalog.Models);
            Assert.Null(ModelCatalog.Find(entry.Id));
        }
        finally
        {
            HubCatalog.ResetCache();
        }
    }
}
