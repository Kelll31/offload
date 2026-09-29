using Offload.Core.Config;

namespace Offload.Integrations.Tests;

public class AutoConnectExtraTests
{
    private static AppConfig Cfg(params string[] extra)
    {
        var c = new AppConfig { SetupCompleted = true };
        c.AutoConnectExtra.AddRange(extra);
        return c;
    }

    [Fact]
    public void Candidates_ClaudeFirst_ThenOptedIn_WithoutWslOrManual()
    {
        var cfg = Cfg("cursor", "claude-code-wsl:Ubuntu", "jetbrains-ai", "cursor", "claude-code", "windsurf");
        Assert.Equal(["claude-code", "claude-desktop", "cursor", "windsurf"], AutoConnectPolicy.Candidates(cfg));
    }

    [Fact]
    public void OptedInIde_FollowsSameRules()
    {
        var cfg = Cfg("cursor");
        Assert.True(AutoConnectPolicy.ShouldConnect(cfg, "cursor", IntegrationStatus.NotRegistered));
        Assert.False(AutoConnectPolicy.ShouldConnect(cfg, "cursor", IntegrationStatus.Foreign));
        Assert.False(AutoConnectPolicy.ShouldConnect(cfg, "vscode", IntegrationStatus.NotRegistered)); // не выбрана
        cfg.DeclinedIntegrations.Add("cursor");
        Assert.False(AutoConnectPolicy.ShouldConnect(cfg, "cursor", IntegrationStatus.NotRegistered));
    }

    [Fact]
    public async Task OptedInCursor_IsConnected_ButNotWithoutOptIn()
    {
        using var sb = new Sandbox();
        var mcp = sb.Write(sb.P(".cursor", "mcp.json"), Samples.CursorJson);
        var cfg = new AppConfig { SetupCompleted = true };
        var engine = Engine(sb, cfg);

        Assert.True((await engine.RunOnceAsync(TestContext.Current.CancellationToken)).IsEmpty);
        Assert.DoesNotContain("offload", File.ReadAllText(mcp), StringComparison.Ordinal);

        cfg.AutoConnectExtra.Add("cursor");
        var report = await engine.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("cursor", Assert.Single(report.Connected).Id);
        Assert.Contains("\"offload\"", File.ReadAllText(mcp), StringComparison.Ordinal);
        Assert.Contains("context7", File.ReadAllText(mcp), StringComparison.Ordinal);
    }

    private static AutoConnectEngine Engine(Sandbox sb, AppConfig cfg, Func<VerifyResult>? verify = null, Func<DateTime>? now = null) =>
        new(new AutoConnectHost
        {
            GetConfig = () => cfg,
            UpdateConfig = a => a(cfg),
            GetSpec = () => sb.Spec(),
            Verify = (_, _, _) => Task.FromResult(verify?.Invoke() ?? new VerifyResult(true, FailureKind.None, "", 25, TimeSpan.FromSeconds(1), 1)),
            UtcNow = now ?? (() => DateTime.UtcNow),
            InstallClaudeCodeExtras = () => { },
        });

    [Fact]
    public async Task Recheck_OnlyStaleTracked_ReportsBreakOnce_AndRecovery()
    {
        using var sb = new Sandbox();
        sb.Write(sb.P("AppData", "Roaming", "Claude", "claude_desktop_config.json"), Samples.DesktopConfig);
        var cfg = new AppConfig { SetupCompleted = true };
        var ok = true;
        var now = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        var engine = Engine(sb, cfg, () =>
        {
            calls++;
            return ok ? new VerifyResult(true, FailureKind.None, "", 25, TimeSpan.FromSeconds(1), 1)
                : new VerifyResult(false, FailureKind.ExeBlocked, "запуск запрещён", 0, TimeSpan.Zero, 4);
        }, () => now);
        var ct = TestContext.Current.CancellationToken;

        await engine.RunOnceAsync(ct); // подключение и первая проверка
        Assert.Equal(1, calls);

        Assert.True((await engine.RecheckAsync(ct: ct)).IsEmpty); // проверяли только что — не повторяем
        Assert.Equal(1, calls);

        now = now.AddHours(25);
        ok = false;
        var broken = await engine.RecheckAsync(ct: ct);
        Assert.Equal("claude-desktop", Assert.Single(broken.Broken).Id);
        Assert.True(broken.Notice().Warning);
        Assert.False(cfg.IntegrationStates["claude-desktop"].LastCheckOk);

        var again = await engine.RecheckAsync(force: true, ct: ct); // всё ещё сломано — повторно не сообщаем
        Assert.True(again.IsEmpty);

        ok = true;
        var fixedReport = await engine.RecheckAsync(force: true, ct: ct);
        Assert.Equal("claude-desktop", Assert.Single(fixedReport.Recovered).Id);
        Assert.False(fixedReport.Notice().Warning);
    }

    [Fact]
    public async Task Recheck_IgnoresUntrackedAndDeclined()
    {
        using var sb = new Sandbox();
        sb.Write(sb.P("AppData", "Roaming", "Claude", "claude_desktop_config.json"), Samples.DesktopConfig);
        var cfg = new AppConfig { SetupCompleted = true };
        var calls = 0;
        var engine = Engine(sb, cfg, () =>
        {
            calls++;
            return new VerifyResult(true, FailureKind.None, "", 1, TimeSpan.Zero, 1);
        });

        await engine.RecheckAsync(force: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
    }
}

public class ClaudeOverviewTests
{
    [Fact]
    public async Task Rows_ReflectInstallStatusAndChecks()
    {
        using var sb = new Sandbox();
        var cfg = new AppConfig();
        var spec = sb.Spec();

        var none = ClaudeOverview.Build(cfg, spec);
        Assert.Equal(["claude-code", "claude-desktop"], none.Select(r => r.Id));
        Assert.All(none, r => Assert.Equal(ClaudeRowKind.NotInstalled, r.Kind));

        sb.Write(sb.P("AppData", "Roaming", "Claude", "claude_desktop_config.json"), Samples.DesktopConfig);
        Assert.Equal(ClaudeRowKind.NotConnected, ClaudeOverview.Build(cfg, spec)[1].Kind);

        await IntegrationRegistry.Find("claude-desktop")!.RegisterAsync(spec, TestContext.Current.CancellationToken);
        Assert.Equal(ClaudeRowKind.Connected, ClaudeOverview.Build(cfg, spec)[1].Kind);

        cfg.RecordCheck("claude-desktop", ok: false, "не запустился", DateTime.UtcNow);
        cfg.Decline("claude-code");
        var rows = ClaudeOverview.Build(cfg, spec);
        Assert.Equal(ClaudeRowKind.Attention, rows[1].Kind);
        Assert.Equal("не запустился", rows[1].State!.LastCheckText);
        Assert.True(rows[0].Declined);
    }
}

public class ConfigBackupsTests
{
    [Fact]
    public async Task Restore_BringsBackPreviousVersion_AndBacksUpCurrent()
    {
        using var sb = new Sandbox();
        var path = sb.Write(sb.P(".cursor", "mcp.json"), Samples.CursorJson);
        var cursor = IntegrationRegistry.Find("cursor")!;
        var original = File.ReadAllText(path);
        await cursor.RegisterAsync(sb.Spec(), TestContext.Current.CancellationToken); // перед правкой — копия исходного файла
        Assert.NotEqual(original, File.ReadAllText(path));

        var backups = ConfigBackups.For(cursor);
        var first = Assert.Single(backups, b => File.ReadAllText(b.BackupPath) == original);

        var r = ConfigBackups.Restore(first);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.NotNull(r.BackupPath); // версия с записью Offload тоже сохранена
        Assert.Contains("\"offload\"", File.ReadAllText(r.BackupPath!), StringComparison.Ordinal);

        var again = ConfigBackups.Restore(first);
        Assert.True(again.Ok);
        Assert.Null(again.BackupPath); // уже совпадает — файл не переписывается
    }

    [Fact]
    public void Restore_RefusesFilesOutsideBackupFolder()
    {
        using var sb = new Sandbox();
        var path = sb.Write(sb.P(".cursor", "mcp.json"), Samples.CursorJson);
        var fake = sb.Write(sb.P("evil.json"), "{\"mcpServers\":{}}");

        var r = ConfigBackups.Restore(new ConfigBackup(path, fake, DateTime.Now, 10));

        Assert.False(r.Ok);
        Assert.Equal(Samples.CursorJson, File.ReadAllText(path));
    }
}
