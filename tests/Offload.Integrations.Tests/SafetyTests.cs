using Offload.Integrations.Clients;

namespace Offload.Integrations.Tests;

/// <summary>Защита реального профиля: все пути — в песочнице, CLI не запускаются, переменные окружения процесса игнорируются.</summary>
public class SafetyTests
{
    private static string RealHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void WithoutExplicitSandbox_CatchAllIsActive()
    {
        Assert.True(IntegrationEnvironment.IsSandboxed);
        Assert.StartsWith(TestSetup.CatchAllRoot, IntegrationEnvironment.UserProfile);
        Assert.False(IntegrationEnvironment.CliAllowed);
        Assert.Null(ClaudeCli.Find());
    }

    [Fact]
    public void AllConfigPaths_AreInsideSandbox_EvenWithHostileProcessVariables()
    {
        // Реальные переменные окружения процесса (например, CODEX_HOME пользователя) в песочнице игнорируются.
        var hostile = Path.Combine(RealHome, ".hostile-should-not-be-used");
        var saved = IntegrationEnvironment.RedirectVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        foreach (var v in IntegrationEnvironment.RedirectVariables) Environment.SetEnvironmentVariable(v, hostile);
        try
        {
            Check();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    private static void Check()
    {
        using var sb = new Sandbox();
        foreach (var i in IntegrationRegistry.All)
        {
            if (i.ConfigPath is { } p) Assert.StartsWith(sb.Root, p, StringComparison.OrdinalIgnoreCase);
        }
        Assert.StartsWith(sb.Root, ClientLocations.ClaudeHome, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(sb.Root, Claude.ClaudeExtrasImpl.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.Null(IntegrationEnvironment.GetVariable("PATH"));
        Assert.Null(IntegrationEnvironment.GetVariable("CODEX_HOME"));
        Assert.Null(ClaudeCli.Find());
        Assert.Null(QoderIntegration.FindCli());
        var env = IntegrationEnvironment.ChildEnvironment()!;
        Assert.Equal(sb.Root, env["USERPROFILE"]);
        Assert.Equal(sb.Root, env["HOME"]);
        Assert.Null(env["CODEX_HOME"]);
    }

    [Fact]
    public void EnvironmentVariableSandbox_DerivesAllRoots()
    {
        var sb = IntegrationEnvironment.FromVariables(n => n == IntegrationEnvironment.UserProfileVar ? @"C:\t\home" : null)!;
        Assert.Equal(@"C:\t\home", sb.UserProfile);
        Assert.Equal(@"C:\t\home\AppData\Roaming", sb.AppData);
        Assert.Equal(@"C:\t\home\AppData\Local", sb.LocalAppData);
        Assert.False(sb.AllowCli);
        Assert.Null(IntegrationEnvironment.FromVariables(_ => null));
    }

    [Fact]
    public void RealProfile_NotTouchedSoFar()
    {
        var problems = RealProfileGuard.Violations();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}

/// <summary>
/// Живая проверка с настоящим claude.exe (из расширения VS Code). Только по OFFLOAD_LIVE_CLI=1.
/// USERPROFILE/HOME/APPDATA/LOCALAPPDATA/CLAUDE_CONFIG_DIR дочернего процесса — внутри песочницы.
/// </summary>
public class LiveClaudeCliTests
{
    [Fact]
    public async Task RealClaudeCli_RegisterStatusUnregister_InSandbox()
    {
        if (Environment.GetEnvironmentVariable("OFFLOAD_LIVE_CLI") != "1")
            Assert.Skip("Живая проверка: задайте OFFLOAD_LIVE_CLI=1");
        var claude = ClaudeCli.FindInExtensions(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (claude is null) Assert.Skip("claude.exe в расширениях VS Code не найден");

        using var sb = new Sandbox(allowCli: true, claudeCli: claude);
        var cfg = IntegrationEnvironment.GetVariable("CLAUDE_CONFIG_DIR")!;
        Assert.StartsWith(sb.Root, cfg);
        Directory.CreateDirectory(cfg);
        var cc = IntegrationRegistry.Find("claude-code")!;
        Assert.Equal(Path.Combine(cfg, ".claude.json"), cc.ConfigPath);
        var spec = new McpServerSpec("offload", @"C:\fake PcTestSandbox\Иван\Offload.exe", ["--mcp"], new Dictionary<string, string>());

        var r = await cc.RegisterAsync(spec);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("claude CLI", r.Message);
        Assert.Equal(IntegrationStatus.Registered, cc.GetStatus(spec));
        Assert.Contains("\"type\": \"stdio\"", File.ReadAllText(cc.ConfigPath!));

        var moved = spec with { Command = @"C:\fake PcTestSandbox\moved\Offload.exe" };
        Assert.Equal(IntegrationStatus.Outdated, cc.GetStatus(moved));
        Assert.True((await cc.RegisterAsync(moved)).Ok);
        Assert.Equal(IntegrationStatus.Registered, cc.GetStatus(moved));

        var u = await cc.UnregisterAsync();
        Assert.True(u.Ok, u.Message);
        Assert.Contains("claude CLI", u.Message);
        Assert.Equal(IntegrationStatus.NotRegistered, cc.GetStatus(moved));
        Assert.Empty(RealProfileGuard.Violations());
    }
}
