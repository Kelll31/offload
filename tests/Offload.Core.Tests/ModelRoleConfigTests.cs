using System.Text.Json;
using Offload.Core.Config;
using Offload.Core.Util;

namespace Offload.Core.Tests;

/// <summary>Роли моделей в настройках: порты, совместимость, сериализация (ROADMAP §5.2).</summary>
public class ModelRoleConfigTests
{
    [Fact]
    public void AuxPort_DefaultOffsets_SavedPort_NeverMainPort()
    {
        var s = new ServerSettings { Port = 8765 };
        Assert.Equal(8765, s.AuxPort(ModelRole.Quality));
        Assert.Equal((8766, 8767, 8768), (s.AuxPort(ModelRole.Fast), s.AuxPort(ModelRole.Embed), s.AuxPort(ModelRole.Rerank)));

        s.AuxPorts["fast"] = 9001;
        Assert.Equal(9001, s.AuxPort(ModelRole.Fast));

        // Сохранённый порт совпал с основным (основной сам сменил порт) — берётся порт по умолчанию.
        s.AuxPorts["embed"] = 8765;
        Assert.Equal(8767, s.AuxPort(ModelRole.Embed));

        var top = new ServerSettings { Port = 65535 };
        Assert.Equal((65534, 65533), (top.AuxPort(ModelRole.Fast), top.AuxPort(ModelRole.Embed)));
    }

    [Fact]
    public void RoleBaseUrl_UsesHostAndRolePort()
    {
        var cfg = new AppConfig();
        cfg.Server.Port = 9000;
        Assert.Equal("http://127.0.0.1:9000", cfg.RoleBaseUrl(ModelRole.Quality));
        Assert.Equal("http://127.0.0.1:9003", cfg.RoleBaseUrl(ModelRole.Rerank));
    }

    [Theory]
    [InlineData("fast", ModelRole.Fast)]
    [InlineData(" EMBED ", ModelRole.Embed)]
    [InlineData("rerank", ModelRole.Rerank)]
    [InlineData("quality", ModelRole.Quality)]
    public void TryParse_KnownKeys(string key, ModelRole expected)
    {
        Assert.True(ModelRoleConfig.TryParse(key, out var role));
        Assert.Equal(expected, role);
        Assert.Equal(expected, ModelRoleConfig.TryParse(expected.Key(), out var back) ? back : (ModelRole)(-1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("agent")]
    public void TryParse_Unknown_FalseQuality(string? key)
    {
        Assert.False(ModelRoleConfig.TryParse(key, out var role));
        Assert.Equal(ModelRole.Quality, role);
    }

    [Fact]
    public void ActiveModel_EmptyId_FallsBackToFirstChatModel()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([
            new InstalledModel { Id = "emb", Kind = ModelKind.Embed },
            new InstalledModel { Id = "rr", Kind = ModelKind.Rerank },
            new InstalledModel { Id = "chat-a" },
            new InstalledModel { Id = "chat-b" },
        ]);

        cfg.Models.ActiveModelId = null;
        Assert.Equal("chat-a", cfg.ActiveModel()?.Id);
        cfg.Models.ActiveModelId = "";
        Assert.Equal("chat-a", cfg.ActiveModel()?.Id);
        cfg.Models.ActiveModelId = "missing";
        Assert.Equal("chat-a", cfg.ActiveModel()?.Id);
        cfg.Models.ActiveModelId = "chat-b";
        Assert.Equal("chat-b", cfg.ActiveModel()?.Id);
    }

    [Fact]
    public void ActiveModel_NeverEmbedOrRerank()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([
            new InstalledModel { Id = "emb", Kind = ModelKind.Embed },
            new InstalledModel { Id = "rr", Kind = ModelKind.Rerank },
        ]);
        Assert.Null(cfg.ActiveModel()); // только эмбеддинги и реранкер — активной модели нет

        // Идентификатор в конфиге указывает на модель эмбеддингов (ручная правка) — берётся чат-модель.
        cfg.Models.ActiveModelId = "emb";
        Assert.Null(cfg.ActiveModel());
        cfg.Models.Installed.Add(new InstalledModel { Id = "mine", IsCustom = true });
        Assert.Equal("mine", cfg.ActiveModel()?.Id);
        Assert.Equal("mine", cfg.RoleModel(ModelRole.Quality)?.Id);

        Assert.Null(new AppConfig().ActiveModel());
    }

    [Fact]
    public void RoleModel_RequiresInstalledCompatibleModel()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([
            new InstalledModel { Id = "chat" },
            new InstalledModel { Id = "small" },
            new InstalledModel { Id = "emb", Kind = ModelKind.Embed },
            new InstalledModel { Id = "mine", IsCustom = true },
        ]);
        cfg.Models.ActiveModelId = "chat";
        cfg.Models.Roles = new ModelRoles { Fast = "emb", Embed = "EMB", Rerank = "mine" };

        Assert.Equal("chat", cfg.RoleModel(ModelRole.Quality)?.Id);
        Assert.Null(cfg.RoleModel(ModelRole.Fast)); // модель эмбеддингов не может быть быстрой
        Assert.Equal("emb", cfg.RoleModel(ModelRole.Embed)?.Id);
        Assert.Equal("mine", cfg.RoleModel(ModelRole.Rerank)?.Id);

        cfg.Assign(ModelRole.Fast, "small");
        Assert.Equal("small", cfg.RoleModel(ModelRole.Fast)?.Id);
        cfg.Assign(ModelRole.Fast, "missing");
        Assert.Null(cfg.RoleModel(ModelRole.Fast));
        cfg.Assign(ModelRole.Fast, " ");
        Assert.Null(cfg.AssignedId(ModelRole.Fast));

        // Настройки без раздела roles (конфиг прежних версий).
        cfg.Models.Roles = null!;
        Assert.Null(cfg.RoleModel(ModelRole.Embed));
        cfg.Assign(ModelRole.Embed, "emb");
        Assert.Equal("emb", cfg.RoleModel(ModelRole.Embed)?.Id);
    }

    [Fact]
    public void Compatibility_ByKind_CustomOnlyForAuxiliaryKinds()
    {
        var custom = new InstalledModel { IsCustom = true };
        Assert.True(ModelRoleConfig.IsCompatible(custom, ModelRole.Fast));
        Assert.True(ModelRoleConfig.IsCompatible(custom, ModelRole.Embed));
        var rerank = new InstalledModel { Kind = ModelKind.Rerank };
        Assert.False(ModelRoleConfig.IsCompatible(rerank, ModelRole.Quality));
        Assert.False(ModelRoleConfig.IsCompatible(rerank, ModelRole.Embed));
        Assert.True(ModelRoleConfig.IsCompatible(rerank, ModelRole.Rerank));
    }

    [Fact]
    public void AuxContext_EmbedCapped_FastRecommended()
    {
        var m = new InstalledModel { NativeContext = 40960, RecommendedContext = 32768 };
        Assert.Equal(32768, ModelRoleConfig.AuxContext(ModelRole.Fast, m));
        Assert.Equal(ModelRoleConfig.EmbedContext, ModelRoleConfig.AuxContext(ModelRole.Embed, m));
        Assert.Equal(512, ModelRoleConfig.AuxContext(ModelRole.Rerank, new InstalledModel { NativeContext = 512 }));
    }

    [Fact]
    public void Config_RolesAndKind_RoundTrip_OldConfigDefaults()
    {
        var cfg = new AppConfig();
        cfg.Models.Roles.Embed = "emb";
        cfg.Models.Installed.Add(new InstalledModel { Id = "emb", Kind = ModelKind.Embed, Pooling = "last" });
        cfg.Server.AuxPorts["fast"] = 9100;
        var json = JsonSerializer.Serialize(cfg, Json.Options);
        Assert.Contains("\"kind\": \"embed\"", json);

        var back = JsonSerializer.Deserialize<AppConfig>(json, Json.Options)!;
        Assert.Equal("emb", back.Models.Roles.Embed);
        Assert.Equal((ModelKind.Embed, "last"), (back.Models.Installed[0].Kind, back.Models.Installed[0].Pooling));
        Assert.Equal(9100, back.Server.AuxPorts["fast"]);

        var old = JsonSerializer.Deserialize<AppConfig>("""{"models":{"installed":[{"id":"x"}],"activeModelId":"x"}}""", Json.Options)!;
        Assert.NotNull(old.Models.Roles);
        Assert.Equal(ModelKind.Chat, old.Models.Installed[0].Kind);
        Assert.Null(old.RoleModel(ModelRole.Fast));
    }
}
