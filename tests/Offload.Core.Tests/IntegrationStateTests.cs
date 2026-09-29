using Offload.Core.Config;

namespace Offload.Core.Tests;

public class IntegrationStateTests
{
    private static readonly string[] ClaudeIds = ["claude-code", "claude-desktop"];

    [Fact]
    public void Declined_AddIsIdempotent_UndeclineRemoves()
    {
        var cfg = new AppConfig();
        cfg.Decline("claude-desktop");
        cfg.Decline("claude-desktop");
        Assert.Equal(["claude-desktop"], cfg.DeclinedIntegrations);
        cfg.Undecline("claude-desktop");
        cfg.Undecline("never-declined");
        Assert.Empty(cfg.DeclinedIntegrations);
    }

    [Fact]
    public void MarkConnected_ResetsCheck_RecordCheck_KeepsWayOfConnecting()
    {
        var cfg = new AppConfig();
        var t = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
        cfg.MarkConnected("claude-code", auto: true);
        cfg.RecordCheck("claude-code", ok: true, "проверено", t);
        var state = cfg.IntegrationStates["claude-code"];
        Assert.True(state.AutoConnected);
        Assert.True(state.LastCheckOk);
        Assert.Equal(t, state.LastCheckUtc);
        Assert.Equal("проверено", state.LastCheckText);

        cfg.MarkConnected("claude-code", auto: false); // явное подключение пользователем — старая проверка не в счёт
        Assert.False(cfg.IntegrationStates["claude-code"].AutoConnected);
        Assert.Null(cfg.IntegrationStates["claude-code"].LastCheckUtc);

        cfg.RecordCheck("cursor", ok: false, "нет файла", t); // проверка без записи о подключении тоже сохраняется
        Assert.False(cfg.IntegrationStates["cursor"].AutoConnected);
        cfg.ForgetState("cursor");
        Assert.False(cfg.IntegrationStates.ContainsKey("cursor"));
    }

    [Fact]
    public void ClearConnections_KeepsDeclined()
    {
        var cfg = new AppConfig();
        cfg.Integrations.AddRange(["claude-code", "cursor"]);
        cfg.MarkConnected("claude-code", auto: true);
        cfg.Decline("claude-desktop");

        cfg.ClearConnections();

        Assert.Empty(cfg.Integrations);
        Assert.Empty(cfg.IntegrationStates);
        Assert.Equal(["claude-desktop"], cfg.DeclinedIntegrations);
    }

    [Fact]
    public void ClaudeLink_NoneConnectedNeedsAttention()
    {
        var cfg = new AppConfig();
        Assert.Equal(ClaudeLink.None, cfg.ClaudeLink(ClaudeIds));

        cfg.Integrations.Add("cursor"); // не Claude
        Assert.Equal(ClaudeLink.None, cfg.ClaudeLink(ClaudeIds));

        cfg.Integrations.Add("claude-code");
        Assert.Equal(ClaudeLink.Connected, cfg.ClaudeLink(ClaudeIds)); // проверки ещё не было — ошибки нет

        cfg.RecordCheck("claude-code", true, "ok", DateTime.UtcNow);
        Assert.Equal(ClaudeLink.Connected, cfg.ClaudeLink(ClaudeIds));

        cfg.Integrations.Add("claude-desktop");
        cfg.RecordCheck("claude-desktop", false, "антивирус", DateTime.UtcNow);
        Assert.Equal(ClaudeLink.NeedsAttention, cfg.ClaudeLink(ClaudeIds));
    }

    [Collection("AppPaths")]
    public class Persistence
    {
        [Fact]
        public void DeclinedAndStates_SurviveSaveAndReload()
        {
            using var home = new TempHome();
            ConfigStore.Reload();
            ConfigStore.Update(c =>
            {
                c.Decline("claude-desktop");
                c.MarkConnected("claude-code", auto: true);
                c.RecordCheck("claude-code", true, "проверено (25 инструментов, 1,2 с)", new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc));
            });

            var again = ConfigStore.Reload();

            Assert.Equal(["claude-desktop"], again.DeclinedIntegrations);
            var state = again.IntegrationStates["claude-code"];
            Assert.True(state.AutoConnected);
            Assert.True(state.LastCheckOk);
            Assert.Equal("проверено (25 инструментов, 1,2 с)", state.LastCheckText);
        }

        [Fact]
        public void OldConfigWithoutNewFields_LoadsWithEmptyDefaults()
        {
            using var home = new TempHome();
            File.WriteAllText(AppPaths.ConfigFile, "{ \"schemaVersion\": 1, \"setupCompleted\": true, \"integrations\": [\"cursor\"] }");

            var cfg = ConfigStore.Reload();

            Assert.True(cfg.SetupCompleted);
            Assert.Equal(["cursor"], cfg.Integrations);
            Assert.Empty(cfg.DeclinedIntegrations);
            Assert.Empty(cfg.IntegrationStates);
        }

        [Fact]
        public void NullNewFields_AreNormalized()
        {
            using var home = new TempHome();
            File.WriteAllText(AppPaths.ConfigFile, "{ \"declinedIntegrations\": null, \"integrationStates\": null }");

            var cfg = ConfigStore.Reload();

            Assert.NotNull(cfg.DeclinedIntegrations);
            Assert.NotNull(cfg.IntegrationStates);
        }
    }
}
