using Offload.App.Services;
using Offload.Core.Config;
using Offload.Models;

namespace Offload.App.Tests;

/// <summary>Строки списка моделей: мастер настройки показывает только чат-модели (эмбеддинги и реранкеры — только роли).</summary>
[Collection("AppPaths")]
public sealed class ModelRowsTests
{
    private static AppConfig Config()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([
            new InstalledModel { Id = "qwen3-embed-0.6b-q8", DisplayName = "Emb", Kind = ModelKind.Embed },
            new InstalledModel { Id = "my-reranker", DisplayName = "Mine", Kind = ModelKind.Rerank, IsCustom = true },
            new InstalledModel { Id = "my-chat", DisplayName = "Chat", IsCustom = true },
        ]);
        return cfg;
    }

    [Fact]
    public void Build_ChatOnly_SkipsEmbedAndRerank()
    {
        using var home = new TempHome();
        var (rows, error) = ModelRows.Build(Config(), hw: null, chatOnly: true);

        Assert.Null(error);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.IsChat, r.Id));
        Assert.DoesNotContain(rows, r => r.Id is "qwen3-embed-0.6b-q8" or "bge-reranker-v2-m3-q4" or "my-reranker");
        Assert.Contains(rows, r => r.Id == "my-chat");
        // Активной становится чат-модель, а не первая установленная (эмбеддинги).
        Assert.Equal("my-chat", Assert.Single(rows, r => r.IsActive).Id);
    }

    [Fact]
    public void Build_Full_KeepsAllKindsForModelsPage()
    {
        using var home = new TempHome();
        var (rows, _) = ModelRows.Build(Config(), hw: null);

        var embed = Assert.Single(rows, r => r.Id == "qwen3-embed-0.6b-q8");
        Assert.Equal((ModelKind.Embed, false, true), (embed.Kind, embed.IsChat, embed.IsInstalled));
        Assert.Equal(ModelKind.Rerank, Assert.Single(rows, r => r.Id == "bge-reranker-v2-m3-q4").Kind);
        Assert.Equal(ModelKind.Rerank, Assert.Single(rows, r => r.Id == "my-reranker").Kind);
        Assert.Equal(ModelCatalog.All.Count + 2, rows.Count);
    }
}
