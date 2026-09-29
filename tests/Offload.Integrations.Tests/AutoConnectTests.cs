using System.Text;
using Offload.Core.Config;

namespace Offload.Integrations.Tests;

public class AutoConnectPolicyTests
{
    private static AppConfig Cfg(bool setup = true, bool auto = true, string[]? declined = null, string[]? tracked = null)
    {
        var c = new AppConfig { SetupCompleted = setup };
        c.Ui.AutoRepairIntegrations = auto;
        c.DeclinedIntegrations.AddRange(declined ?? []);
        c.Integrations.AddRange(tracked ?? []);
        return c;
    }

    [Fact]
    public void ShouldConnect_AllCombinations()
    {
        var statuses = Enum.GetValues<IntegrationStatus>();
        foreach (var id in new[] { "claude-code", "claude-desktop", "cursor", "claude-code-wsl:Ubuntu" })
        foreach (var setup in new[] { true, false })
        foreach (var auto in new[] { true, false })
        foreach (var declined in new[] { true, false })
        foreach (var tracked in new[] { true, false })
        foreach (var status in statuses)
        {
            var cfg = Cfg(setup, auto, declined ? [id] : null, tracked ? [id] : null);
            var expected = setup && auto && !declined && !tracked && status == IntegrationStatus.NotRegistered
                           && (id is "claude-code" or "claude-desktop");
            Assert.True(expected == AutoConnectPolicy.ShouldConnect(cfg, id, status),
                $"{id} setup={setup} auto={auto} declined={declined} tracked={tracked} status={status}");
        }
    }

    [Theory]
    [InlineData(IntegrationStatus.Foreign)]
    [InlineData(IntegrationStatus.Outdated)]
    [InlineData(IntegrationStatus.Error)]
    [InlineData(IntegrationStatus.Registered)]
    [InlineData(IntegrationStatus.ClientNotFound)]
    public void ForeignOutdatedError_AreNeverConnectedAutomatically(IntegrationStatus status) =>
        Assert.False(AutoConnectPolicy.ShouldConnect(Cfg(), "claude-code", status));

    [Fact]
    public void OnlyClaudeCodeAndDesktop_AreAutoConnected() =>
        Assert.Equal(["claude-code", "claude-desktop"], AutoConnectPolicy.Ids);
}

public class AutoConnectEngineTests
{
    private const string DesktopRel = "claude_desktop_config.json";

    private sealed class Rig : IDisposable
    {
        public Sandbox Sb { get; } = new();
        public AppConfig Cfg { get; } = new() { SetupCompleted = true };
        public DateTime Now { get; set; } = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        public List<string> Verified { get; } = [];
        public List<TimeSpan> Pauses { get; } = [];
        public int Extras { get; private set; }
        public VerifyResult VerifyAnswer { get; set; } = new(true, FailureKind.None, "", 25, TimeSpan.FromMilliseconds(120), 1);
        public Func<string, IIdeIntegration?>? FindOverride { get; set; }
        public bool RealExtras { get; set; }
        public AutoConnectEngine Engine { get; }

        public Rig()
        {
            Engine = new AutoConnectEngine(new AutoConnectHost
            {
                GetConfig = () => Cfg,
                UpdateConfig = a => a(Cfg),
                Find = id => FindOverride is null ? IntegrationRegistry.Find(id) : FindOverride(id),
                GetSpec = () => Sb.Spec(),
                Verify = (i, _, _) =>
                {
                    Verified.Add(i.Id);
                    return Task.FromResult(VerifyAnswer);
                },
                Pause = Pauses.Add,
                UtcNow = () => Now,
                InstallClaudeCodeExtras = () =>
                {
                    Extras++;
                    if (RealExtras) ClaudeCodeAutoExtras.Install();
                },
            });
        }

        public string MsixDesktop => Sb.P("AppData", "Local", "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", DesktopRel);
        public string ClassicDesktop => Sb.P("AppData", "Roaming", "Claude", DesktopRel);
        public string ClaudeJson => Sb.P(".claude.json");

        public string Read(string path) => File.ReadAllText(path);

        public byte[] Bytes(string path) => File.ReadAllBytes(path);

        public void Dispose() => Sb.Dispose();
    }

    [Fact]
    public async Task ClaudeDesktopInstalledLater_IsConnected_Verified_AndTracked()
    {
        using var rig = new Rig();
        rig.Sb.Write(rig.MsixDesktop, Samples.DesktopConfig);

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        var ide = Assert.Single(report.Connected);
        Assert.Equal("claude-desktop", ide.Id);
        Assert.True(ide.Verify.Ok);
        Assert.Empty(report.Failed);
        Assert.Contains("claude-desktop", rig.Cfg.Integrations);
        Assert.True(rig.Cfg.IntegrationStates["claude-desktop"].AutoConnected);
        Assert.True(rig.Cfg.IntegrationStates["claude-desktop"].LastCheckOk);
        Assert.Equal(["claude-desktop"], rig.Verified);
        Assert.Equal(IntegrationStatus.Registered, IntegrationRegistry.Find("claude-desktop")!.GetStatus(rig.Sb.Spec()));
        var text = rig.Read(rig.MsixDesktop);
        Assert.Contains("\"offload\"", text, StringComparison.Ordinal);
        Assert.Contains("server-filesystem", text, StringComparison.Ordinal); // чужие записи на месте
        Assert.Contains(AutoConnectEngine.PauseDuration, rig.Pauses);

        var (title, body, warning) = report.Notice();
        Assert.False(warning);
        Assert.Contains("Claude Desktop", body, StringComparison.Ordinal);
        Assert.NotEqual("", title);
    }

    [Fact]
    public async Task SecondRun_IsIdempotent_FileBytewiseSame_NoNewNotice()
    {
        using var rig = new Rig();
        rig.Sb.Write(rig.MsixDesktop, Samples.DesktopConfig);
        await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);
        var before = rig.Bytes(rig.MsixDesktop);

        var again = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(again.IsEmpty);
        Assert.Equal(before, rig.Bytes(rig.MsixDesktop));
        Assert.Single(rig.Verified);
    }

    [Fact]
    public async Task DeclinedIde_IsNotTouched()
    {
        using var rig = new Rig();
        rig.Sb.Write(rig.MsixDesktop, Samples.DesktopConfig);
        var before = rig.Bytes(rig.MsixDesktop);
        rig.Cfg.DeclinedIntegrations.Add("claude-desktop");

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(report.IsEmpty);
        Assert.Equal(before, rig.Bytes(rig.MsixDesktop));
        Assert.DoesNotContain("claude-desktop", rig.Cfg.Integrations);
        Assert.Empty(rig.Verified);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SetupNotCompleted_OrAutomationOff_DoesNothing(bool setup, bool auto)
    {
        using var rig = new Rig();
        rig.Sb.Write(rig.MsixDesktop, Samples.DesktopConfig);
        var before = rig.Bytes(rig.MsixDesktop);
        rig.Cfg.SetupCompleted = setup;
        rig.Cfg.Ui.AutoRepairIntegrations = auto;

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(report.IsEmpty);
        Assert.Equal(before, rig.Bytes(rig.MsixDesktop));
    }

    [Fact]
    public async Task NoClaudeInstalled_DoesNothing()
    {
        using var rig = new Rig();
        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(report.IsEmpty);
        Assert.Empty(rig.Cfg.Integrations);
        Assert.Empty(rig.Pauses);
    }

    [Fact]
    public async Task ForeignOffloadEntry_IsNotReplaced()
    {
        using var rig = new Rig();
        const string foreign = "{\"mcpServers\":{\"offload\":{\"command\":\"/opt/mine/wrapper\",\"args\":[\"x\"]}}}";
        rig.Sb.Write(rig.MsixDesktop, foreign);
        var before = rig.Bytes(rig.MsixDesktop);

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(report.IsEmpty);
        Assert.Equal(before, rig.Bytes(rig.MsixDesktop));
        Assert.DoesNotContain("claude-desktop", rig.Cfg.Integrations);
    }

    [Fact]
    public async Task BrokenJson_FileUntouched_NothingRegistered()
    {
        using var rig = new Rig();
        rig.Sb.Write(rig.MsixDesktop, "{ \"mcpServers\": { broken ");
        var before = rig.Bytes(rig.MsixDesktop);

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(report.IsEmpty);
        Assert.Equal(before, rig.Bytes(rig.MsixDesktop));
        Assert.Empty(rig.Cfg.Integrations);
    }

    [Fact]
    public async Task ClaudeCode_GetsGuidanceAndReadApprovals_LikeTheWizard()
    {
        using var rig = new Rig { RealExtras = true };
        rig.Sb.Dir(".claude");
        rig.Sb.Write(rig.ClaudeJson, Samples.ClaudeJson);

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("claude-code", Assert.Single(report.Connected).Id);
        Assert.Equal(1, rig.Extras);
        Assert.True(ClaudeCodeExtras.IsGuidanceInstalled());
        Assert.True(ClaudeCodeExtras.AreToolsPreapproved());
        Assert.False(ClaudeCodeExtras.AreWriteToolsPreapproved()); // запись — только по явному согласию
        Assert.False(ClaudeCodeExtras.IsStrongRuleInstalled());
        Assert.Contains("claude-code", rig.Cfg.Integrations);
    }

    [Fact]
    public async Task VerifyFailure_KeepsEntry_AndProducesWarningWithReasonAndHint()
    {
        using var rig = new Rig();
        rig.Sb.Write(rig.MsixDesktop, Samples.DesktopConfig);
        rig.VerifyAnswer = new VerifyResult(false, FailureKind.ExeBlocked, "запуск запрещён", 0, TimeSpan.Zero, 4);

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Contains("\"offload\"", rig.Read(rig.MsixDesktop), StringComparison.Ordinal); // запись не откатывается
        Assert.Contains("claude-desktop", rig.Cfg.Integrations);
        Assert.False(rig.Cfg.IntegrationStates["claude-desktop"].LastCheckOk);
        var (_, body, warning) = report.Notice();
        Assert.True(warning);
        Assert.Contains("антивирус", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyThrows_IsReportedAsFailure_NotCrash()
    {
        using var rig = new Rig();
        rig.Sb.Write(rig.MsixDesktop, Samples.DesktopConfig);
        var engine = new AutoConnectEngine(new AutoConnectHost
        {
            GetConfig = () => rig.Cfg,
            UpdateConfig = a => a(rig.Cfg),
            GetSpec = () => rig.Sb.Spec(),
            Verify = (_, _, _) => throw new InvalidOperationException("boom"),
        });

        var report = await engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(report.Connected).Verify.Ok);
    }

    [Fact]
    public async Task RegistrationFailure_IsRetriedNoMoreThanOncePerDay()
    {
        using var rig = new Rig();
        rig.Sb.Write(rig.MsixDesktop, Samples.DesktopConfig);
        var fake = new FakeIde("claude-desktop", installed: true, IntegrationStatus.NotRegistered, ok: false);
        rig.FindOverride = id => id == "claude-desktop" ? fake : null;

        var first = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("claude-desktop", Assert.Single(first.Failed).Id);
        Assert.Equal(1, fake.RegisterCalls);
        Assert.True(first.Notice().Warning);

        rig.Now = rig.Now.AddHours(1);
        Assert.True((await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken)).IsEmpty);
        Assert.Equal(1, fake.RegisterCalls); // не спамим

        rig.Now = rig.Now.AddHours(24);
        var third = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Single(third.Failed);
        Assert.Equal(2, fake.RegisterCalls);
        Assert.Empty(rig.Cfg.Integrations);
    }

    [Fact]
    public async Task StatusRecheckedRightBeforeWrite_UserConnectedMeanwhile()
    {
        using var rig = new Rig();
        // Первый GetStatus — NotRegistered, после паузы пользователь уже подключил сам — Registered.
        var fake = new FakeIde("claude-desktop", installed: true, IntegrationStatus.NotRegistered, ok: true)
        {
            StatusAfterPause = IntegrationStatus.Registered,
        };
        rig.FindOverride = id => id == "claude-desktop" ? fake : null;
        fake.OnPause = () => rig.Pauses.Count > 0;

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(report.IsEmpty);
        Assert.Equal(0, fake.RegisterCalls);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Engine.RunOnceAsync(cts.Token));
    }

    [Fact]
    public void Notice_CombinesAllIdesIntoOneMessage()
    {
        var ok = new VerifyResult(true, FailureKind.None, "", 25, TimeSpan.FromSeconds(1.2), 1);
        var report = new AutoConnectReport(
            [new AutoConnectedIde("claude-code", "Claude Code", ok, "Перезапустите Claude Code."),
             new AutoConnectedIde("claude-desktop", "Claude Desktop", ok, "Закройте Claude Desktop.")],
            []);

        var (_, text, warning) = report.Notice();

        Assert.False(warning);
        Assert.Contains("Claude Code, Claude Desktop", text, StringComparison.Ordinal);
        Assert.Contains("Перезапустите Claude Code.", text, StringComparison.Ordinal);
        Assert.Contains("Закройте Claude Desktop.", text, StringComparison.Ordinal);
    }

    // ---------------- золотые конфиги ----------------

    public static TheoryData<string, string> GoldenCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var target in new[] { "msix", "classic", "claude-json" })
        foreach (var variant in new[] { "plain", "bom", "crlf", "comments", "empty", "missing-container" })
            data.Add(target, variant);
        return data;
    }

    private static string Golden(string target, string variant)
    {
        var baseText = target == "claude-json" ? Samples.ClaudeJson : Samples.DesktopConfig;
        return variant switch
        {
            "plain" => baseText,
            "bom" => "﻿" + baseText,
            "crlf" => baseText.Replace("\n", "\r\n"),
            "comments" => "// настройки\n" + baseText.Replace("{\n", "{\n  /* комментарий */\n", StringComparison.Ordinal),
            "empty" => "",
            "missing-container" => "{\n  \"theme\": \"dark\"\n}\n",
            _ => throw new ArgumentException(variant),
        };
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task Golden_Register_Repeat_Unregister(string target, string variant)
    {
        using var rig = new Rig();
        var id = target == "claude-json" ? "claude-code" : "claude-desktop";
        var path = target switch { "msix" => rig.MsixDesktop, "classic" => rig.ClassicDesktop, _ => rig.ClaudeJson };
        if (target == "claude-json") rig.Sb.Dir(".claude");
        rig.Sb.Write(path, Golden(target, variant));
        var ct = TestContext.Current.CancellationToken;
        var integration = IntegrationRegistry.Find(id)!;
        var spec = rig.Sb.Spec();

        var first = await rig.Engine.RunOnceAsync(ct);
        Assert.Equal(id, Assert.Single(first.Connected).Id);
        Assert.Equal(IntegrationStatus.Registered, integration.GetStatus(spec));
        var afterFirst = rig.Bytes(path);
        Assert.False(afterFirst.Length >= 3 && afterFirst[0] == 0xEF && afterFirst[1] == 0xBB && afterFirst[2] == 0xBF && variant != "bom",
            "BOM не должен появляться");
        if (variant == "crlf") Assert.DoesNotContain("\n", rig.Read(path).Replace("\r\n", ""), StringComparison.Ordinal); // переводы строк не смешаны

        // Повторная регистрация — без изменений.
        var again = await integration.RegisterAsync(spec, ct);
        Assert.True(again.Ok, again.Message);
        Assert.Equal(afterFirst, rig.Bytes(path));

        // Отмена возвращает файл к состоянию без записи и не портит остальное.
        // Опознание «нашей» записи по имени Offload.exe при отключении опирается на пути Windows.
        if (!OperatingSystem.IsWindows()) return;
        var removed = await integration.UnregisterAsync(ct);
        Assert.True(removed.Ok, removed.Message);
        Assert.Equal(IntegrationStatus.NotRegistered, integration.GetStatus(spec));
        if (variant is "plain" or "bom" or "comments" or "crlf" && target != "claude-json")
            Assert.Contains("server-filesystem", rig.Read(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Golden_ClaudeJson_ExistingProjectLevelOffload_IsNotTopLevelEntry()
    {
        // В ~/.claude.json запись «offload» внутри проекта не считается подключением на уровне пользователя.
        using var rig = new Rig();
        rig.Sb.Dir(".claude");
        rig.Sb.Write(rig.ClaudeJson, Samples.ClaudeJson);

        var report = await rig.Engine.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("claude-code", Assert.Single(report.Connected).Id);
        var text = rig.Read(rig.ClaudeJson);
        Assert.Contains("\"-m\", \"pc\"", text, StringComparison.Ordinal); // запись проекта не тронута
        Assert.Contains("Привет", text, StringComparison.Ordinal);
    }

    private sealed class FakeIde(string id, bool installed, IntegrationStatus status, bool ok) : IIdeIntegration
    {
        public int RegisterCalls { get; private set; }
        public IntegrationStatus? StatusAfterPause { get; init; }
        public Func<bool>? OnPause { get; set; }
        public string Id => id;
        public string DisplayName => "Fake " + id;
        public string? ConfigPath => null;
        public string? PostRegisterHint => "Перезапустите " + id;
        public bool IsClientInstalled() => installed;

        public IntegrationStatus GetStatus(McpServerSpec spec) =>
            StatusAfterPause is { } after && OnPause?.Invoke() == true ? after : status;

        public Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default)
        {
            RegisterCalls++;
            return Task.FromResult(new IntegrationResult(ok, ok ? "готово" : "файл заблокирован"));
        }

        public Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default) => Task.FromResult(new IntegrationResult(true, ""));
    }
}
