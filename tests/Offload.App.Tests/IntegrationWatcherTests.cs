using Offload.App.Forms.Pages;
using Offload.App.Services;
using Offload.Core.Config;
using Offload.Integrations;

namespace Offload.App.Tests;

public class IntegrationWatcherTests
{
    private static AppConfig Config(bool autoRepair, params string[] ids)
    {
        var cfg = new AppConfig();
        cfg.Ui.AutoRepairIntegrations = autoRepair;
        cfg.Integrations.AddRange(ids);
        return cfg;
    }

    [Fact]
    public void Enabled_OnlyForInstalledCopyWithSettingAndIntegrations()
    {
        Assert.True(new AppConfig().Ui.AutoRepairIntegrations, "автовосстановление включено по умолчанию");
        Assert.True(AutoRepairPolicy.Enabled(Config(true, "cursor"), devMode: false, foreignCopy: false));
        Assert.False(AutoRepairPolicy.Enabled(Config(false, "cursor"), devMode: false, foreignCopy: false));
        Assert.False(AutoRepairPolicy.Enabled(Config(true), devMode: false, foreignCopy: false));
        // Dev-сборка и копия не из папки установки никогда не переписывают пути в IDE.
        Assert.False(AutoRepairPolicy.Enabled(Config(true, "cursor"), devMode: true, foreignCopy: false));
        Assert.False(AutoRepairPolicy.Enabled(Config(true, "cursor"), devMode: false, foreignCopy: true));
    }

    [Fact]
    public void TryAcquire_LimitsRepairsPerWindow_PerIde()
    {
        var policy = new AutoRepairPolicy(maxPerWindow: 2, window: TimeSpan.FromHours(1));
        var t0 = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(policy.TryAcquire("cursor", t0));
        Assert.True(policy.TryAcquire("cursor", t0.AddMinutes(1)));
        Assert.False(policy.TryAcquire("cursor", t0.AddMinutes(2)));
        Assert.True(policy.TryAcquire("vscode", t0.AddMinutes(2))); // у каждой IDE свой счётчик

        Assert.True(policy.ShouldWarn("cursor"));
        Assert.False(policy.ShouldWarn("cursor")); // предупреждаем один раз

        Assert.True(policy.TryAcquire("cursor", t0.AddMinutes(61))); // окно сдвинулось
        Assert.True(policy.ShouldWarn("cursor")); // после удачи можно предупредить снова
    }

    [Fact]
    public void AttentionNotices_OncePerIdePerSession()
    {
        var notices = new AttentionNotices();
        var first = notices.Take([new RepairIssue("cursor", RepairNeed.Missing), new RepairIssue("zed", RepairNeed.Foreign)]);
        Assert.Equal(2, first.Count);
        // Та же IDE снова (и даже с другой причиной) — в этом сеансе больше не напоминаем.
        Assert.Empty(notices.Take([new RepairIssue("cursor", RepairNeed.Missing), new RepairIssue("zed", RepairNeed.StaleSettings)]));
        var next = Assert.Single(notices.Take([new RepairIssue("cursor", RepairNeed.Missing), new RepairIssue("kiro", RepairNeed.OtherCopy)]));
        Assert.Equal("kiro", next.Id);
    }

    [Fact]
    public void AttentionText_NamesIdeAndReason()
    {
        var text = IntegrationWatcher.AttentionText(
            [new RepairIssue("cursor", RepairNeed.Missing), new RepairIssue("zed", RepairNeed.Foreign), new RepairIssue("no-such", RepairNeed.StaleSettings)]);
        Assert.Contains("Cursor", text, StringComparison.Ordinal);
        Assert.Contains("Zed", text, StringComparison.Ordinal);
        Assert.Contains("no-such", text, StringComparison.Ordinal);
        Assert.Contains("offload", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorReport_Formatting()
    {
        var text = IntegrationsPage.FormatReports(
        [
            new DoctorReport("cursor", "Cursor",
            [
                new DoctorCheck(DoctorLevel.Ok, "ok line"),
                new DoctorCheck(DoctorLevel.Error, "bad line"),
                new DoctorCheck(DoctorLevel.Info, "hint"),
            ], null, null),
            new DoctorReport("zed", "Zed", [new DoctorCheck(DoctorLevel.Warning, "warn")], null, null),
        ]);
        var lines = text.Split(Environment.NewLine);
        Assert.Equal(["Cursor", "  ✓ ok line", "  ✗ bad line", "  · hint", "", "Zed", "  ! warn"], lines);
    }
}
