using System.Text.Json.Nodes;
using Offload.Integrations.Clients;

namespace Offload.Integrations.Tests;

public class IntegrationVerifierTests
{
    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private static JsonIntegration Client(Sandbox sb, bool installed = true) =>
        new("vscode", "Test IDE", null)
        {
            Detect = () => installed,
            Targets = () => [sb.P("ide", "mcp.json")],
            Container = ["servers"],
            Entry = spec => new JsonObject { ["command"] = spec.Command, ["args"] = JsonIntegration.Strings(spec.Args) },
        };

    /// <summary>Спецификация с настоящим файлом (заглушкой exe) и запись в конфиге IDE, указывающая на него.</summary>
    private static McpServerSpec ArrangeOurEntry(Sandbox sb, bool createExe = true, string? args = null)
    {
        var spec = sb.Spec();
        if (createExe) sb.Write(spec.Command, "MZ");
        sb.Write(sb.P("ide", "mcp.json"),
            "{\"servers\":{\"offload\":{\"command\":" + JsonValue.Create(spec.Command).ToJsonString() + ",\"args\":" + (args ?? "[\"--mcp\"]") + "}}}");
        return spec;
    }

    private static HandshakeResult Good(int tools = 25) =>
        new(true, "offload", "1.0.4", "2025-06-18", tools, TimeSpan.FromMilliseconds(120), null, 0);

    private static IntegrationVerifier.Handshaker Script(List<string> launched, params HandshakeResult[] results)
    {
        var i = 0;
        return (command, _, _) =>
        {
            launched.Add(command);
            return Task.FromResult(results[Math.Min(i++, results.Length - 1)]);
        };
    }

    private static Task<VerifyResult> Verify(JsonIntegration client, McpServerSpec spec, IntegrationVerifier.Handshaker hs, List<TimeSpan>? delays = null)
    {
        Task Delay(TimeSpan t, CancellationToken _)
        {
            delays?.Add(t);
            return Task.CompletedTask;
        }
        return IntegrationVerifier.VerifyAsync(client, spec, null, hs, Delay, NoDelays, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Success_LaunchesEntryCommand_AndReportsToolCount()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);
        var launched = new List<string>();

        var r = await Verify(Client(sb), spec, Script(launched, Good(25)));

        Assert.True(r.Ok, r.Message);
        Assert.Equal(FailureKind.None, r.Kind);
        Assert.Equal(25, r.ToolCount);
        Assert.Equal(1, r.Attempts);
        Assert.Equal([spec.Command], launched);
        Assert.Equal("", r.Hint);
    }

    [Fact]
    public async Task SucceedsOnSecondAttempt_AfterSlowFirstStart()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);
        var launched = new List<string>();
        var delays = new List<TimeSpan>();
        var timeout = new HandshakeResult(false, null, null, null, 0, TimeSpan.FromSeconds(30), "нет ответа", 0, FailureKind.Timeout);

        var r = await Verify(Client(sb), spec, Script(launched, timeout, Good()), delays);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(2, launched.Count);
        Assert.Single(delays);
    }

    [Fact]
    public async Task ProcessKeepsFailing_RetriesThreeTimes_ThenReportsStartFailed()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);
        var launched = new List<string>();
        var died = new HandshakeResult(false, null, null, null, 0, TimeSpan.Zero, "процесс завершился с кодом 1", 0, FailureKind.StartFailed);

        var r = await Verify(Client(sb), spec, Script(launched, died));

        Assert.False(r.Ok);
        Assert.Equal(FailureKind.StartFailed, r.Kind);
        Assert.Equal(4, launched.Count); // первая попытка + 3 повтора
        Assert.Equal(4, r.Attempts);
        Assert.Contains("кодом 1", r.Message, StringComparison.Ordinal);
        Assert.NotEqual("", r.Hint);
    }

    [Fact]
    public async Task Timeout_IsReportedAsTimeout()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);
        var timeout = new HandshakeResult(false, null, null, null, 0, TimeSpan.FromSeconds(30), "нет ответа", 0, FailureKind.Timeout);

        var r = await Verify(Client(sb), spec, Script([], timeout));

        Assert.Equal(FailureKind.Timeout, r.Kind);
    }

    [Fact]
    public async Task StdoutNoise_IsNotRetried_AndFails()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);
        var launched = new List<string>();
        var noisy = new HandshakeResult(true, "offload", "1.0.4", null, 25, TimeSpan.Zero, null, 2);

        var r = await Verify(Client(sb), spec, Script(launched, noisy));

        Assert.False(r.Ok);
        Assert.Equal(FailureKind.ProtocolNoise, r.Kind);
        Assert.Single(launched);
    }

    [Fact]
    public async Task ZeroTools_IsFailure()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);

        var r = await Verify(Client(sb), spec, Script([], Good(tools: 0)));

        Assert.False(r.Ok);
        Assert.Equal(FailureKind.StartFailed, r.Kind);
    }

    [Fact]
    public async Task MissingExe_IsReported_WithoutLaunching()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb, createExe: false);
        var launched = new List<string>();

        var r = await Verify(Client(sb), spec, Script(launched, Good()));

        Assert.Equal(FailureKind.ExeMissing, r.Kind);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task BlockedLaunch_IsRetried_ThenReportedAsBlocked()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);
        var launched = new List<string>();
        var blocked = new HandshakeResult(false, null, null, null, 0, TimeSpan.Zero, "отказано в доступе", 0, FailureKind.ExeBlocked);

        var r = await Verify(Client(sb), spec, Script(launched, blocked));

        Assert.Equal(FailureKind.ExeBlocked, r.Kind);
        Assert.Equal(4, launched.Count);
    }

    [Fact]
    public async Task ThrowingHandshake_IsStartFailed_NotException()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);

        var r = await Verify(Client(sb), spec, (_, _, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(FailureKind.StartFailed, r.Kind);
        Assert.Contains("boom", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var sb = new Sandbox();
        var spec = ArrangeOurEntry(sb);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IntegrationVerifier.VerifyAsync(Client(sb), spec, null, Script([], Good()), (_, _) => Task.CompletedTask, NoDelays, cts.Token));
    }

    [Fact]
    public async Task ConfigProblems_AreClassified_AndNothingIsLaunched()
    {
        using var sb = new Sandbox();
        var spec = sb.Spec();
        var launched = new List<string>();
        var client = Client(sb);

        // Файла нет.
        var missing = await Verify(client, spec, Script(launched, Good()));
        Assert.Equal(FailureKind.ConfigMissing, missing.Kind);

        // Записи нет.
        sb.Write(sb.P("ide", "mcp.json"), "{\"servers\":{}}");
        Assert.Equal(FailureKind.ConfigMissing, (await Verify(client, spec, Script(launched, Good()))).Kind);

        // Чужая запись — не запускаем и не выдаём за нашу.
        sb.Write(sb.P("ide", "mcp.json"), "{\"servers\":{\"offload\":{\"command\":\"/opt/other/server\",\"args\":[]}}}");
        var foreign = await Verify(client, spec, Script(launched, Good()));
        Assert.False(foreign.Ok);
        Assert.Contains("другой программ", foreign.Message, StringComparison.Ordinal);

        // Битый JSON — файл не разобран.
        sb.Write(sb.P("ide", "mcp.json"), "{ this is not json");
        Assert.Equal(FailureKind.ConfigInvalid, (await Verify(client, spec, Script(launched, Good()))).Kind);

        Assert.Empty(launched);
    }

    [Fact]
    public async Task ClientNotInstalled_And_ManualClient()
    {
        using var sb = new Sandbox();
        var spec = sb.Spec();
        Assert.Equal(FailureKind.ClientNotFound, (await Verify(Client(sb, installed: false), spec, Script([], Good()))).Kind);
        var manual = await IntegrationVerifier.VerifyAsync(IntegrationRegistry.Find("jetbrains-ai")!, spec, ct: TestContext.Current.CancellationToken);
        Assert.False(manual.Ok);
    }

    [Fact]
    public async Task WslClaudeCode_IsNotVerifiable_NotReportedAsMissingExe()
    {
        using var sb = new Sandbox(wslDistros: new Dictionary<string, string> { ["Ubuntu"] = "/home/dev" });
        sb.Write(sb.Wsl("Ubuntu", "/home/dev/.claude.json"), "{}");
        var wsl = Assert.Single(IntegrationRegistry.WslIntegrations(), i => i.Id == "claude-code-wsl:Ubuntu");
        await wsl.RegisterAsync(sb.Spec(), TestContext.Current.CancellationToken);

        var r = await IntegrationVerifier.VerifyAsync(wsl, sb.Spec(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FailureKind.NotVerifiable, r.Kind);
    }

    [Fact]
    public async Task OtherCopyOrExtraArgs_IsNotLaunched()
    {
        // Распознавание «нашей» записи по имени Offload.exe опирается на обратные слеши Windows.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "нужны пути Windows");
        using var sb = new Sandbox();
        var spec = sb.Spec();
        var other = sb.Spec("Другая копия");
        sb.Write(other.Command, "MZ");
        sb.Write(sb.P("ide", "mcp.json"),
            "{\"servers\":{\"offload\":{\"command\":" + JsonValue.Create(other.Command).ToJsonString() + ",\"args\":[\"--mcp\"]}}}");
        var launched = new List<string>();

        var r = await Verify(Client(sb), spec, Script(launched, Good()));

        Assert.Equal(FailureKind.NotVerifiable, r.Kind);
        Assert.Empty(launched);

        // Известная установленная копия запускать можно.
        var allowed = await IntegrationVerifier.VerifyAsync(Client(sb), spec, [other.Command], Script(launched, Good()),
            (_, _) => Task.CompletedTask, NoDelays, TestContext.Current.CancellationToken);
        Assert.True(allowed.Ok, allowed.Message);
        Assert.Equal([other.Command], launched);
    }

    [Fact]
    public async Task UncPath_IsNeverLaunched()
    {
        using var sb = new Sandbox();
        var spec = sb.Spec();
        sb.Write(sb.P("ide", "mcp.json"), "{\"servers\":{\"offload\":{\"command\":\"\\\\\\\\server\\\\share\\\\Offload.exe\",\"args\":[\"--mcp\"]}}}");
        var launched = new List<string>();

        var r = await Verify(Client(sb), spec, Script(launched, Good()));

        Assert.False(r.Ok);
        Assert.Empty(launched);
    }

    [Theory]
    [InlineData(true, 5, 0, FailureKind.None, FailureKind.None)]
    [InlineData(true, 5, 1, FailureKind.None, FailureKind.ProtocolNoise)]
    [InlineData(true, 0, 0, FailureKind.None, FailureKind.StartFailed)]
    [InlineData(false, 0, 0, FailureKind.None, FailureKind.StartFailed)]
    [InlineData(false, 0, 0, FailureKind.Timeout, FailureKind.Timeout)]
    [InlineData(false, 0, 3, FailureKind.Timeout, FailureKind.ProtocolNoise)]
    [InlineData(false, 0, 3, FailureKind.ExeBlocked, FailureKind.ExeBlocked)]
    public void Classify_Table(bool ok, int tools, int noise, FailureKind kind, FailureKind expected)
    {
        var h = new HandshakeResult(ok, null, null, null, tools, TimeSpan.Zero, ok ? null : "err", noise, kind);
        Assert.Equal(expected, IntegrationVerifier.Classify(h));
    }

    [Fact]
    public void FailureText_HasReasonForEveryFailure()
    {
        foreach (var kind in Enum.GetValues<FailureKind>().Where(k => k != FailureKind.None))
            Assert.NotEqual("", FailureText.Reason(kind));
    }
}
