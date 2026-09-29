using Offload.Core.Config;

namespace Offload.Models.Tests;

/// <summary>Модели автодополнения (role = fim): записи каталога, назначение роли, бюджет памяти.</summary>
[Collection("AppPaths")]
public sealed class FimModelsTests
{
    private static readonly string[] FimIds = ["qwen2.5-coder-1.5b-fim-q8", "qwen2.5-coder-3b-fim-q8"];

    [Fact]
    public void Catalog_FimEntries_FromGgmlOrgWithVerifiedData()
    {
        foreach (var id in FimIds)
        {
            var m = ModelCatalog.Find(id);
            Assert.NotNull(m);
            Assert.Equal(ModelKind.Fim, m!.Role);
            Assert.False(m.IsChat, id);
            Assert.False(m.GoodToolCalling, id);
            Assert.StartsWith("ggml-org/Qwen2.5-Coder-", m.Repo, StringComparison.Ordinal);
            Assert.Equal("qwen2", m.Architecture);
            Assert.Equal(32768, m.NativeContext);
            Assert.Equal(ModelRoleConfig.FimContext, m.DefaultContext);
            Assert.Equal((2, 128), (m.Kv.KvHeads, m.Kv.HeadDim));
            Assert.Equal(m.BlockCount, m.Kv.Layers);
            Assert.Contains("fim", m.Tags ?? []);
            Assert.Equal("Q8_0", m.DefaultQuant);
            Assert.DoesNotContain(m, ModelCatalog.ChatModels);
        }
        // Данные HF (tree API на закреплённой ревизии) — сверены при добавлении.
        var small = ModelCatalog.Find(FimIds[0])!;
        Assert.Equal((1646573056L, "29871c94d15727a6e243f79a37113d4ae625a6215b5e800bf41a23af2da32832"), (small.DefaultFile!.Size, small.DefaultFile.Sha256));
        Assert.Equal("Apache-2.0", small.License);
        var big = ModelCatalog.Find(FimIds[1])!;
        Assert.Equal((3285476160L, "a522a906e299ed34db738b9626b2cd0da9e446c14674468a22fc2eae3dbd344d"), (big.DefaultFile!.Size, big.DefaultFile.Sha256));
        Assert.Equal("Qwen Research", big.License);
        // Рекомендация основной модели никогда не выбирает модель автодополнения.
        Assert.True(FitCalculator.Recommend(FitTests.Hw(2, 16)).IsChat);
    }

    [Fact]
    public void Parse_FimRole_Accepted_PoolingRejected()
    {
        const string one = """
            { "models": [ { "id": "a", "displayName": "A", "description": "d", "repo": "o/r", "quants": ["Q8_0"], "role": "fim" EXTRA,
              "files": [ { "quant": "Q8_0", "path": "a.gguf", "size": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" } ],
              "approxSizeBytes": 10, "paramsB": 1, "activeParamsB": 1, "isMoe": false, "nativeContext": 4096, "defaultContext": 4096,
              "kv": { "layers": 1, "kvHeads": 1, "headDim": 64 }, "goodToolCalling": false,
              "sampling": { "temperature": 0.1 }, "license": "MIT", "minVramGb": 1 } ] }
            """;
        Assert.Equal(ModelKind.Fim, Assert.Single(ModelCatalog.Parse(one.Replace("EXTRA", "", StringComparison.Ordinal))).Role);
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(one.Replace("EXTRA", ", \"pooling\": \"mean\"", StringComparison.Ordinal)));
    }

    private static InstalledModel Installed(string id, ModelKind kind = ModelKind.Chat) => new()
    {
        Id = id, DisplayName = id, FilePath = @"C:\models\" + id + ".gguf", Kind = kind,
    };

    [Fact]
    public void AssignRole_Fim_CompatibleOnly_NeverActive_ClearedOnRemove()
    {
        using var home = new TempHome();
        ConfigStore.Update(c =>
        {
            c.Models.Installed.AddRange([Installed("chat-a"), Installed("coder", ModelKind.Fim)]);
            c.Models.ActiveModelId = "chat-a";
        });

        Assert.Throws<ModelException>(() => ModelManager.AssignRole(ModelRole.Fim, "chat-a"));
        Assert.Throws<ModelException>(() => ModelManager.SetActive("coder"));
        ModelManager.AssignRole(ModelRole.Fim, "coder");
        Assert.Equal("coder", ConfigStore.Reload().RoleModel(ModelRole.Fim)?.Id);

        ModelManager.Remove("coder", deleteFiles: false);
        var cfg = ConfigStore.Reload();
        Assert.Null(cfg.Models.Roles.Fim);
        Assert.Equal("chat-a", cfg.Models.ActiveModelId);
    }

    [Fact]
    public void FixActive_FimModelNeverActive_UnknownFimAssignmentCleared()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([Installed("coder", ModelKind.Fim), Installed("chat")]);
        cfg.Models.ActiveModelId = "coder";
        cfg.Models.Roles.Fim = "missing";
        ModelManager.FixActive(cfg);
        Assert.Equal("chat", cfg.Models.ActiveModelId);
        Assert.Null(cfg.Models.Roles.Fim);
    }

    private static AppConfig WithFim(bool enabled, bool cpuOnly)
    {
        var cfg = new AppConfig();
        foreach (var (id, kind) in new[] { ("qwen3.8-27b-q4", ModelKind.Chat), (FimIds[0], ModelKind.Fim) })
        {
            var c = ModelCatalog.Find(id)!;
            cfg.Models.Installed.Add(new InstalledModel
            {
                Id = id, DisplayName = id, FilePath = id + ".gguf", SizeBytes = c.ApproxSizeBytes, Kind = kind,
                NativeContext = c.NativeContext, RecommendedContext = c.DefaultContext, Architecture = c.Architecture,
            });
        }
        cfg.Models.ActiveModelId = "qwen3.8-27b-q4";
        cfg.Models.Roles.Fim = FimIds[0];
        cfg.Autocomplete.Enabled = enabled;
        cfg.Autocomplete.CpuOnly = cpuOnly;
        return cfg;
    }

    [Fact]
    public void RoleBudget_Fim_CountedWhenEnabledOrRequested()
    {
        var hw = FitTests.Hw(48, 64);
        Assert.DoesNotContain(RoleBudget.Evaluate(WithFim(false, false), hw, ModelCatalog.All).Items, i => i.Role == ModelRole.Fim);

        var preview = RoleBudget.Evaluate(WithFim(false, false), hw, ModelCatalog.All, includeFim: true);
        var fim = preview.Items.Single(i => i.Role == ModelRole.Fim);
        Assert.Equal(FitLevel.FullGpu, fim.Fit.Level);
        Assert.Equal(ModelRoleConfig.FimContext, fim.Fit.ContextSize);
        // KV-кэш f16 при контексте 8K: у декодера он есть (в отличие от энкодеров-реранкеров).
        Assert.Equal(FitCalculator.KvCacheBytes(ModelCatalog.Find(FimIds[0])!.Kv, ModelRoleConfig.FimContext, "f16"), fim.Fit.KvCacheBytes);
        Assert.True(fim.VramBytes > ModelCatalog.Find(FimIds[0])!.ApproxSizeBytes);

        var enabled = RoleBudget.Evaluate(WithFim(true, false), hw, ModelCatalog.All);
        Assert.Contains(enabled.Items, i => i.Role == ModelRole.Fim);
        Assert.True(enabled.HasAuxiliary);
    }

    [Fact]
    public void RoleBudget_FimCpuOnly_NoVideoMemory()
    {
        var r = RoleBudget.Evaluate(WithFim(true, true), FitTests.Hw(24, 64), ModelCatalog.All);
        var fim = r.Items.Single(i => i.Role == ModelRole.Fim);
        Assert.Equal(FitLevel.CpuOnly, fim.Fit.Level);
        Assert.Equal(0, fim.VramBytes);
        Assert.True(fim.RamBytes > 0);
    }
}
