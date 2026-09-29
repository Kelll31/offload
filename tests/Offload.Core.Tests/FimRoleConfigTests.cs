using System.Text.Json;
using Offload.Core.Config;
using Offload.Core.Util;

namespace Offload.Core.Tests;

/// <summary>Роль автодополнения (fim): порт, контекст, совместимость, настройки и их сохранение.</summary>
[Collection("AppPaths")]
public sealed class FimRoleConfigTests
{
    [Fact]
    public void AuxPort_Fim_DefaultsTo8012_SavedPort_NeverMainPort()
    {
        var s = new ServerSettings { Port = 8765 };
        Assert.Equal(ModelRoleConfig.DefaultFimPort, s.AuxPort(ModelRole.Fim));
        Assert.Equal(8012, ModelRoleConfig.DefaultFimPort);

        // Порт 8012 был занят — сервер выбрал другой и сохранил его.
        s.AuxPorts["fim"] = 8013;
        Assert.Equal(8013, s.AuxPort(ModelRole.Fim));

        // Основной сервер сам на 8012 — автодополнению достаётся соседний порт.
        var clash = new ServerSettings { Port = 8012 };
        Assert.Equal(8013, clash.AuxPort(ModelRole.Fim));
        clash.AuxPorts["fim"] = 8012;
        Assert.Equal(8013, clash.AuxPort(ModelRole.Fim));

        // Порты остальных ролей не изменились.
        Assert.Equal((8766, 8767, 8768), (s.AuxPort(ModelRole.Fast), s.AuxPort(ModelRole.Embed), s.AuxPort(ModelRole.Rerank)));
    }

    [Fact]
    public void Roles_FimKeyKindAndLists()
    {
        Assert.Equal("fim", ModelRole.Fim.Key());
        Assert.True(ModelRoleConfig.TryParse("FIM", out var role));
        Assert.Equal(ModelRole.Fim, role);
        Assert.Equal(ModelKind.Fim, ModelRoleConfig.KindFor(ModelRole.Fim));
        Assert.Equal(ModelRole.Fim, ModelRoleConfig.RoleFor(ModelKind.Fim));
        Assert.Equal(ModelRole.Embed, ModelRoleConfig.RoleFor(ModelKind.Embed));
        Assert.Null(ModelRoleConfig.RoleFor(ModelKind.Chat));

        // Роль fim не входит в роли MCP (запуск по запросу), но назначается и учитывается в общем списке.
        Assert.DoesNotContain(ModelRole.Fim, ModelRoleConfig.Auxiliary);
        Assert.Contains(ModelRole.Fim, ModelRoleConfig.Assignable);
        Assert.Contains(ModelRole.Fim, ModelRoleConfig.All);
    }

    [Fact]
    public void RoleModel_FimRequiresFimOrCustomModel_NeverActive()
    {
        var cfg = new AppConfig();
        cfg.Models.Installed.AddRange([
            new InstalledModel { Id = "chat" },
            new InstalledModel { Id = "coder", Kind = ModelKind.Fim },
            new InstalledModel { Id = "mine", IsCustom = true },
        ]);
        cfg.Assign(ModelRole.Fim, "chat");
        Assert.Null(cfg.RoleModel(ModelRole.Fim)); // чат-модель из каталога не подходит

        cfg.Assign(ModelRole.Fim, "coder");
        Assert.Equal("coder", cfg.RoleModel(ModelRole.Fim)?.Id);
        Assert.Equal("coder", cfg.Models.Roles.Fim);
        cfg.Assign(ModelRole.Fim, "mine");
        Assert.Equal("mine", cfg.RoleModel(ModelRole.Fim)?.Id);

        // Модель автодополнения не становится активной, даже если на неё указывает идентификатор.
        cfg.Models.ActiveModelId = "coder";
        Assert.Equal("chat", cfg.ActiveModel()?.Id);
        Assert.False(ModelRoleConfig.IsCompatible(new InstalledModel { Kind = ModelKind.Fim }, ModelRole.Fast));
    }

    [Fact]
    public void AuxContext_Fim_SmallFixedContext_CappedByNative()
    {
        Assert.Equal(ModelRoleConfig.FimContext, ModelRoleConfig.AuxContext(ModelRole.Fim, new InstalledModel { NativeContext = 32768, RecommendedContext = 32768 }));
        Assert.Equal(4096, ModelRoleConfig.AuxContext(ModelRole.Fim, new InstalledModel { NativeContext = 4096 }));
    }

    [Fact]
    public void Config_AutocompleteSection_RoundTrip_OldConfigDefaults()
    {
        var cfg = new AppConfig();
        cfg.Autocomplete.Enabled = true;
        cfg.Autocomplete.CpuOnly = true;
        cfg.Autocomplete.ConfigureContinue = false;
        cfg.Models.Roles.Fim = "coder";
        cfg.Models.Installed.Add(new InstalledModel { Id = "coder", Kind = ModelKind.Fim });
        var json = JsonSerializer.Serialize(cfg, Json.Options);
        Assert.Contains("\"kind\": \"fim\"", json);
        Assert.Contains("\"autocomplete\"", json);

        var back = JsonSerializer.Deserialize<AppConfig>(json, Json.Options)!;
        Assert.Equal((true, true, false), (back.Autocomplete.Enabled, back.Autocomplete.CpuOnly, back.Autocomplete.ConfigureContinue));
        Assert.Equal("coder", back.Models.Roles.Fim);
        Assert.Equal(ModelKind.Fim, back.Models.Installed[0].Kind);

        var old = JsonSerializer.Deserialize<AppConfig>("""{"models":{"installed":[{"id":"x"}]}}""", Json.Options)!;
        Assert.False(old.Autocomplete.Enabled);
        Assert.True(old.Autocomplete.ConfigureContinue);
        Assert.Null(old.RoleModel(ModelRole.Fim));
    }

    [Fact]
    public void ConfigStore_NullAutocompleteSection_Normalized_AndSaved()
    {
        using var home = new TempHome();
        File.WriteAllText(AppPaths.ConfigFile, """{"schemaVersion": 1, "autocomplete": null}""");
        var cfg = ConfigStore.Reload();
        Assert.NotNull(cfg.Autocomplete);
        ConfigStore.Update(c =>
        {
            c.Autocomplete.Enabled = true;
            c.Models.Roles.Fim = "coder";
        });
        var again = ConfigStore.Reload();
        Assert.True(again.Autocomplete.Enabled);
        Assert.Equal("coder", again.Models.Roles.Fim);
    }

    [Fact]
    public void ConfigStore_FimKey_OwnAndStable_NeverMainKey()
    {
        using var home = new TempHome();
        // Старый конфиг без ключа автодополнения (или с ключом основного сервера) — создаётся свой ключ.
        File.WriteAllText(AppPaths.ConfigFile, """{"schemaVersion": 1, "server": {"apiKey": "pc-main"}, "autocomplete": {"apiKey": "pc-main"}}""");
        var cfg = ConfigStore.Reload();
        Assert.StartsWith("fim-", cfg.Autocomplete.ApiKey, StringComparison.Ordinal);
        Assert.NotEqual(cfg.Server.ApiKey, cfg.Autocomplete.ApiKey);
        Assert.Equal("pc-main", cfg.Server.ApiKey);
        // Ключ сохраняется и не меняется при следующих загрузках (он записан в файлы IDE).
        var key = cfg.Autocomplete.ApiKey;
        ConfigStore.Update(c => c.Autocomplete.Enabled = true);
        Assert.Equal(key, ConfigStore.Reload().Autocomplete.ApiKey);
    }
}
