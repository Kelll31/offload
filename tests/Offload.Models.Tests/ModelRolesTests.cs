using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models.Tests;

/// <summary>Роли моделей (ROADMAP §5.2): поле role каталога, назначение ролей, бюджет видеопамяти.</summary>
[Collection("AppPaths")]
public class ModelRolesTests
{
    private const string OneModel = """
        { "models": [ { "id": "a", "displayName": "A", "description": "d", "repo": "o/r", "quants": ["Q4_K_M"],
          "files": [ { "quant": "Q4_K_M", "path": "a-Q4_K_M.gguf", "size": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" } ],
          "approxSizeBytes": 10, "paramsB": 1, "activeParamsB": 1, "isMoe": false, "nativeContext": 4096, "defaultContext": 4096,
          "kv": { "layers": 1, "kvHeads": 1, "headDim": 64 }, "goodToolCalling": false,
          "sampling": { "temperature": 0.5 }, "license": "MIT", "minVramGb": 1 ROLE } ] }
        """;

    private static string Catalog(string extra) => OneModel.Replace("ROLE", extra, StringComparison.Ordinal);

    [Fact]
    public void Parse_RoleDefaultsToChat()
    {
        var m = Assert.Single(ModelCatalog.Parse(Catalog("")));
        Assert.Equal(ModelKind.Chat, m.Role);
        Assert.True(m.IsChat);
        Assert.Null(m.Pooling);
    }

    [Fact]
    public void Parse_EmbedWithPooling_Rerank()
    {
        var embed = Assert.Single(ModelCatalog.Parse(Catalog(""", "role": "embed", "pooling": "last" """)));
        Assert.Equal((ModelKind.Embed, "last", false), (embed.Role, embed.Pooling, embed.IsChat));
        var rerank = Assert.Single(ModelCatalog.Parse(Catalog(""", "role": "rerank" """)));
        Assert.Equal(ModelKind.Rerank, rerank.Role);
    }

    [Theory]
    [InlineData(""", "role": "vision" """)]
    [InlineData(""", "role": 7 """)]
    [InlineData(""", "role": "chat", "pooling": "mean" """)]
    [InlineData(""", "role": "embed", "pooling": "none" """)]
    public void Parse_InvalidRoleOrPooling_Rejected(string extra)
    {
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Catalog(extra)));
    }

    [Fact]
    public void BuiltInCatalog_ChatModelsForRecommendation()
    {
        Assert.All(ModelCatalog.ChatModels, m => Assert.True(m.IsChat, m.Id));
        Assert.True(FitCalculator.Recommend(FitTests.Hw(24, 64)).IsChat);
    }

    private static InstalledModel Installed(string id, ModelKind kind = ModelKind.Chat, bool custom = false) => new()
    {
        Id = id,
        DisplayName = id,
        FilePath = @"C:\models\" + id + ".gguf",
        Kind = kind,
        IsCustom = custom,
    };

    [Fact]
    public void SetActive_EmbedModel_Rejected_FastRoleClearedWhenActivated()
    {
        using var home = new TempHome();
        ConfigStore.Update(c =>
        {
            c.Models.Installed.AddRange([Installed("chat-a"), Installed("chat-b"), Installed("emb", ModelKind.Embed)]);
            c.Models.ActiveModelId = "chat-a";
            c.Models.Roles.Fast = "chat-b";
        });

        var ex = Assert.Throws<ModelException>(() => ModelManager.SetActive("emb"));
        Assert.Contains("emb", ex.Message);
        Assert.Equal("chat-a", ConfigStore.Reload().Models.ActiveModelId);

        ModelManager.SetActive("chat-b");
        var cfg = ConfigStore.Reload();
        Assert.Equal("chat-b", cfg.Models.ActiveModelId);
        Assert.Null(cfg.Models.Roles.Fast);
    }

    [Fact]
    public void AssignRole_ChecksCompatibility()
    {
        using var home = new TempHome();
        ConfigStore.Update(c =>
        {
            c.Models.Installed.AddRange([Installed("chat-a"), Installed("chat-b"), Installed("emb", ModelKind.Embed), Installed("mine", custom: true)]);
            c.Models.ActiveModelId = "chat-a";
        });

        ModelManager.AssignRole(ModelRole.Fast, "chat-b");
        ModelManager.AssignRole(ModelRole.Embed, "emb");
        ModelManager.AssignRole(ModelRole.Rerank, "mine"); // свой GGUF — назначение на совести пользователя
        Assert.Throws<ModelException>(() => ModelManager.AssignRole(ModelRole.Fast, "emb"));
        Assert.Throws<ModelException>(() => ModelManager.AssignRole(ModelRole.Embed, "chat-b"));
        Assert.Throws<ModelException>(() => ModelManager.AssignRole(ModelRole.Fast, "chat-a"));
        Assert.Throws<ModelException>(() => ModelManager.AssignRole(ModelRole.Fast, "missing"));

        var cfg = ConfigStore.Reload();
        Assert.Equal(("chat-b", "emb", "mine"), (cfg.Models.Roles.Fast, cfg.Models.Roles.Embed, cfg.Models.Roles.Rerank));
        Assert.Equal("chat-b", cfg.RoleModel(ModelRole.Fast)?.Id);
        Assert.Equal("chat-a", cfg.RoleModel(ModelRole.Quality)?.Id);

        ModelManager.AssignRole(ModelRole.Rerank, null);
        Assert.Null(ConfigStore.Reload().Models.Roles.Rerank);
    }

    [Fact]
    public void Remove_ClearsRoleAssignment_EmbedNeverBecomesActive()
    {
        using var home = new TempHome();
        ConfigStore.Update(c =>
        {
            c.Models.Installed.AddRange([Installed("emb", ModelKind.Embed), Installed("chat-a"), Installed("chat-b")]);
            c.Models.ActiveModelId = "chat-a";
            c.Models.Roles.Embed = "emb";
            c.Models.Roles.Fast = "chat-b";
        });

        ModelManager.Remove("chat-b", deleteFiles: false);
        ModelManager.Remove("chat-a", deleteFiles: false);

        var cfg = ConfigStore.Reload();
        Assert.Null(cfg.Models.Roles.Fast);
        Assert.Equal("emb", cfg.Models.Roles.Embed);
        Assert.Null(cfg.Models.ActiveModelId); // осталась только модель эмбеддингов — активной нет

        ModelManager.Remove("emb", deleteFiles: false);
        Assert.Null(ConfigStore.Reload().Models.Roles.Embed);
    }

    [Fact]
    public void FixActive_SkipsEmbedModels()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([Installed("emb", ModelKind.Embed), Installed("chat")]);
        cfg.Models.ActiveModelId = "emb";
        cfg.Models.Roles.Rerank = "chat"; // не реранкер

        ModelManager.FixActive(cfg);

        Assert.Equal("chat", cfg.Models.ActiveModelId);
        Assert.Null(cfg.Models.Roles.Rerank);
    }

    private static AppConfig TwoRoles(string quality, string fast)
    {
        var cfg = new AppConfig();
        foreach (var id in new[] { quality, fast })
        {
            var c = ModelCatalog.Find(id)!;
            cfg.Models.Installed.Add(new InstalledModel
            {
                Id = id, DisplayName = id, FilePath = id + ".gguf", SizeBytes = c.ApproxSizeBytes,
                NativeContext = c.NativeContext, RecommendedContext = c.DefaultContext, IsMoe = c.IsMoe,
            });
        }
        cfg.Models.ActiveModelId = quality;
        cfg.Models.Roles.Fast = fast;
        return cfg;
    }

    [Fact]
    public void RoleBudget_SumsMainAndAuxiliary()
    {
        var cfg = TwoRoles("qwen3.8-27b-q4", "qwen3.5-4b-q4");

        var big = RoleBudget.Evaluate(cfg, FitTests.Hw(48, 64), ModelCatalog.All);
        Assert.True(big.HasAuxiliary);
        Assert.Equal([ModelRole.Quality, ModelRole.Fast], big.Items.Select(i => i.Role));
        Assert.Equal(big.Items.Sum(i => i.VramBytes), big.TotalVramBytes);
        Assert.True(big.Fits);

        // 20 ГБ: основная 27B в режиме «Авто» занимает почти всю карту — вместе с быстрой моделью не помещается.
        var tight = RoleBudget.Evaluate(cfg, FitTests.Hw(20, 64), ModelCatalog.All);
        Assert.False(tight.Fits);
        Assert.True(tight.TotalVramBytes > tight.VramBudgetBytes);
    }

    [Fact]
    public void RoleBudget_TwoCardSplit_BudgetIsSumOfCards()
    {
        // Две карты по 20 ГБ: основная модель делится между ними — роли помещаются, хотя на одну карту не поместились бы.
        var cfg = TwoRoles("qwen3.8-27b-q4", "qwen3.5-4b-q4");
        const long GiB = 1L << 30;
        var split = new GpuSplit([new GpuSplitDevice("CUDA0", "RTX", 20 * GiB, 19 * GiB), new GpuSplitDevice("CUDA1", "RTX", 20 * GiB, 19 * GiB)], 0);
        var hw = FitTests.Hw(20, 64);
        Assert.False(RoleBudget.Evaluate(cfg, hw, ModelCatalog.All).Fits);

        var r = RoleBudget.Evaluate(cfg, hw, ModelCatalog.All, split: split);
        Assert.Equal(split.BudgetBytes, r.VramBudgetBytes);
        Assert.True(r.Fits);
    }

    [Fact]
    public void RoleBudget_RemoteMode_MainModelNotCounted()
    {
        var cfg = TwoRoles("qwen3.8-27b-q4", "qwen3.5-4b-q4");
        cfg.Remote.Enabled = true;
        cfg.Remote.Url = "http://192.168.1.10:8765";
        var r = RoleBudget.Evaluate(cfg, FitTests.Hw(20, 64), ModelCatalog.All);
        Assert.DoesNotContain(r.Items, i => i.Role == ModelRole.Quality);
        Assert.True(r.Fits);
    }

    [Fact]
    public void Fit_Reranker_EncoderWithoutKvCache_OneSlotDefaultContext()
    {
        var rerank = ModelCatalog.Find("bge-reranker-v2-m3-q4")!;
        Assert.False(FitCalculator.HasKvCache(rerank)); // bert — энкодер

        // Автоподбор и 4 слота не влияют: один слот, контекст модели (8K), память = веса + буфер вычислений.
        var fit = FitCalculator.Evaluate(rerank, 0, FitTests.Hw(8, 32), 0, "f16", parallel: 4);
        Assert.Equal(FitLevel.FullGpu, fit.Level);
        Assert.Equal(8192, fit.ContextSize);
        Assert.Equal(0, fit.KvCacheBytes);
        var expected = rerank.ApproxSizeBytes + FitCalculator.ComputeBufferBytes(rerank, rerank.ApproxSizeBytes, 8192);
        Assert.Equal(expected, fit.EstimatedVramBytes);
        Assert.Equal(438_376_864L + 548_782_735L + 8_388_608L, expected); // веса + (512 + 20 × 0,568) МиБ + 8K × 1 КиБ

        // Без видеокарты — то же на процессоре.
        var cpu = FitCalculator.Evaluate(rerank, 0, FitTests.Hw(0, 16));
        Assert.Equal((FitLevel.CpuOnly, 8192, expected), (cpu.Level, cpu.ContextSize, cpu.EstimatedRamBytes));
    }

    [Fact]
    public void Fit_DecoderEmbedding_KvForSingleSlotOnly()
    {
        var emb = ModelCatalog.Find("qwen3-embed-0.6b-q8")!;
        Assert.True(FitCalculator.HasKvCache(emb)); // qwen3 — каузальный декодер: llama.cpp выделяет KV-кэш

        var one = FitCalculator.Evaluate(emb, 0, FitTests.Hw(8, 32), 0, "f16", parallel: 1);
        var four = FitCalculator.Evaluate(emb, 0, FitTests.Hw(8, 32), 0, "f16", parallel: 4);
        Assert.Equal(one, four); // слотов у сервера эмбеддингов всегда один — KV не растёт
        Assert.Equal(8192, one.ContextSize);
        Assert.Equal(28L * 8 * 128 * 2 * 2 * 8192, one.KvCacheBytes); // 0,875 ГиБ
        Assert.Equal(emb.ApproxSizeBytes + one.KvCacheBytes + FitCalculator.ComputeBufferBytes(emb, emb.ApproxSizeBytes, 8192),
            one.EstimatedVramBytes);

        // Явный контекст соблюдается.
        Assert.Equal(2048, FitCalculator.Evaluate(emb, 0, FitTests.Hw(8, 32), 2048, "f16").ContextSize);
    }

    [Fact]
    public void RoleBudget_EmbedRerank_UseRoleSizing()
    {
        var cfg = TwoRoles("qwen3.8-27b-q4", "qwen3.5-4b-q4");
        cfg.Models.Roles.Fast = null;
        cfg.Server.CacheType = "q4_0"; // у серверов эмбеддингов/реранка кэш всегда f16
        foreach (var (id, kind) in new[] { ("qwen3-embed-0.6b-q8", ModelKind.Embed), ("bge-reranker-v2-m3-q4", ModelKind.Rerank) })
        {
            var c = ModelCatalog.Find(id)!;
            cfg.Models.Installed.Add(new InstalledModel
            {
                Id = id, DisplayName = id, FilePath = id + ".gguf", SizeBytes = c.ApproxSizeBytes, Kind = kind,
                NativeContext = c.NativeContext, RecommendedContext = c.DefaultContext, Architecture = c.Architecture,
            });
        }
        cfg.Models.Roles.Embed = "qwen3-embed-0.6b-q8";
        cfg.Models.Roles.Rerank = "bge-reranker-v2-m3-q4";

        var r = RoleBudget.Evaluate(cfg, FitTests.Hw(24, 64), ModelCatalog.All);
        Assert.Equal([ModelRole.Quality, ModelRole.Embed, ModelRole.Rerank], r.Items.Select(i => i.Role));
        var embed = r.Items.Single(i => i.Role == ModelRole.Embed).Fit;
        Assert.Equal(FitCalculator.KvCacheBytes(ModelCatalog.Find("qwen3-embed-0.6b-q8")!.Kv, 8192, "f16"), embed.KvCacheBytes);
        var rerank = r.Items.Single(i => i.Role == ModelRole.Rerank).Fit;
        Assert.Equal((0L, 8192), (rerank.KvCacheBytes, rerank.ContextSize));

        // Пользовательская модель-энкодер, назначенная реранку, тоже оценивается без KV-кэша.
        cfg.Models.Installed.Add(new InstalledModel { Id = "my-bert", FilePath = "my-bert.gguf", SizeBytes = 400_000_000, IsCustom = true, Architecture = "bert" });
        cfg.Models.Roles.Rerank = "my-bert";
        var header = new GgufInfo("bert", "My", 8192, 24, 16, 16, 1024, 64, false, 0, null, ValueLength: 64);
        var custom = RoleBudget.Evaluate(cfg, FitTests.Hw(24, 64), ModelCatalog.All, _ => header);
        Assert.Equal(0, custom.Items.Single(i => i.Role == ModelRole.Rerank).Fit.KvCacheBytes);
    }

    [Fact]
    public void RoleBudget_NoAuxiliary_CustomWithoutHeaderUnknown()
    {
        var cfg = TwoRoles("qwen3.8-27b-q4", "qwen3.5-4b-q4");
        cfg.Models.Roles.Fast = null;
        Assert.False(RoleBudget.Evaluate(cfg, FitTests.Hw(24, 64), ModelCatalog.All).HasAuxiliary);

        cfg.Models.Installed.Add(new InstalledModel { Id = "mine", FilePath = "mine.gguf", SizeBytes = 1000, IsCustom = true });
        cfg.Models.Roles.Embed = "mine";
        var r = RoleBudget.Evaluate(cfg, FitTests.Hw(24, 64), ModelCatalog.All);
        Assert.Equal([ModelRole.Embed], r.Unknown);
    }
}
