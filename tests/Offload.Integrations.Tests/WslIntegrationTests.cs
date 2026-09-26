using System.Text;
using System.Text.Json.Nodes;
using Offload.Integrations.Clients;

namespace Offload.Integrations.Tests;

/// <summary>
/// Claude Code в WSL (claude-code-wsl:&lt;дистрибутив&gt;): список дистрибутивов, запись в ~/.claude.json пользователя Linux
/// через общий ресурс (в песочнице — папка), команда через interop (/mnt/c/…/Offload.exe) с аргументом дистрибутива.
/// </summary>
public class WslIntegrationTests
{
    private const string Home = "/home/dev";

    private static Sandbox WithUbuntu() => new(wslDistros: new Dictionary<string, string> { ["Ubuntu"] = Home, ["Debian"] = "/root" });

    private static IIdeIntegration Ubuntu() =>
        Assert.Single(IntegrationRegistry.WslIntegrations(), i => i.Id == "claude-code-wsl:Ubuntu");

    [Fact]
    public async Task Register_WritesInteropCommand_ThenUnregister_KeepsUserEntries()
    {
        using var sb = WithUbuntu();
        var config = sb.Write(sb.Wsl("Ubuntu", Home + "/.claude.json"),
            "{\n  \"numStartups\": 3,\n  \"mcpServers\": {\n    \"other\": { \"type\": \"stdio\", \"command\": \"node\", \"args\": [] }\n  }\n}\n");
        var spec = sb.Spec();
        var wsl = Ubuntu();
        Assert.True(wsl.IsClientInstalled());
        Assert.Equal(config, wsl.ConfigPath);
        Assert.Equal(IntegrationStatus.NotRegistered, wsl.GetStatus(spec));

        var r = await wsl.RegisterAsync(spec, TestContext.Current.CancellationToken);
        Assert.True(r.Ok, r.Message);
        var entry = JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["offload"]!;
        Assert.Equal("stdio", entry["type"]!.GetValue<string>());
        var command = entry["command"]!.GetValue<string>();
        Assert.StartsWith("/mnt/" + char.ToLowerInvariant(spec.Command[0]) + "/", command);
        Assert.EndsWith("/Программы Offload/Offload.exe", command);
        Assert.DoesNotContain('\\', command);
        Assert.Equal(["--mcp", "--wsl-distro", "Ubuntu"], entry["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(IntegrationStatus.Registered, wsl.GetStatus(spec));
        Assert.Equal(RepairNeed.None, IntegrationRegistry.Assess(wsl, spec));

        // Повторная регистрация ничего не меняет.
        var again = await wsl.RegisterAsync(spec, TestContext.Current.CancellationToken);
        Assert.True(again.Ok);
        Assert.Null(again.BackupPath);

        var u = await wsl.UnregisterAsync(TestContext.Current.CancellationToken);
        Assert.True(u.Ok, u.Message);
        var after = JsonNode.Parse(File.ReadAllText(config))!;
        Assert.Null(after["mcpServers"]!["offload"]);
        Assert.NotNull(after["mcpServers"]!["other"]);
        Assert.Equal(3, after["numStartups"]!.GetValue<int>());
    }

    [Fact]
    public async Task ForeignEntry_IsNotRemoved_AndWindowsConfigUntouched()
    {
        using var sb = WithUbuntu();
        sb.Dir(".claude");
        var windowsConfig = sb.Write(sb.P(".claude.json"), "{\n  \"mcpServers\": {}\n}\n");
        var config = sb.Write(sb.Wsl("Ubuntu", Home + "/.claude.json"),
            "{\"mcpServers\":{\"offload\":{\"type\":\"stdio\",\"command\":\"/usr/local/bin/my-offload\",\"args\":[]}}}");
        var wsl = Ubuntu();
        Assert.Equal(IntegrationStatus.Foreign, wsl.GetStatus(sb.Spec()));
        var u = await wsl.UnregisterAsync(TestContext.Current.CancellationToken);
        Assert.False(u.Ok);
        Assert.Contains("/usr/local/bin/my-offload", File.ReadAllText(config));

        // Регистрация в WSL не трогает конфиг Claude Code в Windows.
        var r = await wsl.RegisterAsync(sb.Spec(), TestContext.Current.CancellationToken);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("{\n  \"mcpServers\": {}\n}\n", File.ReadAllText(windowsConfig));
    }

    [Fact]
    public void DistroWithoutClaude_IsNotInstalled()
    {
        using var sb = WithUbuntu();
        var debian = Assert.Single(IntegrationRegistry.WslIntegrations(), i => i.Id == "claude-code-wsl:Debian");
        Assert.False(debian.IsClientInstalled());
        Assert.Equal(IntegrationStatus.ClientNotFound, debian.GetStatus(sb.Spec()));

        // Папка ~/.claude — признак установленного Claude Code.
        sb.Dir("wsl", "Debian", "root", ".claude");
        Assert.True(debian.IsClientInstalled());
        Assert.Equal(sb.Wsl("Debian", "/root/.claude.json"), debian.ConfigPath);
    }

    [Fact]
    public async Task NetworkExePath_IsRefused()
    {
        using var sb = WithUbuntu();
        sb.Write(sb.Wsl("Ubuntu", Home + "/.claude.json"), "{}");
        var spec = new McpServerSpec("offload", @"\\server\share\Offload.exe", ["--mcp"], new Dictionary<string, string>());
        var r = await Ubuntu().RegisterAsync(spec, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Equal("{}", File.ReadAllText(sb.Wsl("Ubuntu", Home + "/.claude.json")));
    }

    [Fact]
    public async Task UnregisterAll_AlsoRemovesWslEntries()
    {
        using var sb = WithUbuntu();
        var config = sb.Write(sb.Wsl("Ubuntu", Home + "/.claude.json"), "{}");
        Assert.True((await Ubuntu().RegisterAsync(sb.Spec(), TestContext.Current.CancellationToken)).Ok);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["offload"]);
        await IntegrationRegistry.UnregisterAllAsync(TestContext.Current.CancellationToken);
        Assert.Null(JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["offload"]);
    }

    [Fact]
    public void SandboxWithoutWsl_ListsNothing_AndNeverRunsWslExe()
    {
        using var sb = new Sandbox();
        Assert.Empty(IntegrationRegistry.WslIntegrations());
        Assert.DoesNotContain(IntegrationRegistry.All, i => i.Id.StartsWith(WslClaudeCodeIntegration.Kind, StringComparison.Ordinal));
    }

    [Fact]
    public void ParseWslList_Utf16AndUtf8()
    {
        // wsl.exe без WSL_UTF8 пишет UTF-16LE; прочитанный как UTF-8 вывод содержит нулевые символы.
        var utf16 = Encoding.UTF8.GetString(Encoding.Unicode.GetBytes("\uFEFFUbuntu-24.04\r\ndocker-desktop\r\nDebian\r\n"));
        Assert.Equal(["Ubuntu-24.04", "Debian"], ClientLocations.ParseWslList(utf16));
        Assert.Equal(["Ubuntu", "Arch"], ClientLocations.ParseWslList("Ubuntu\nArch\n\nbad name\n../evil\ndocker-desktop-data\nubuntu\n"));
        Assert.Empty(ClientLocations.ParseWslList(""));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Offload\Offload.exe", "/mnt/c/Program Files/Offload/Offload.exe")]
    [InlineData(@"D:\tools\Offload.exe", "/mnt/d/tools/Offload.exe")]
    [InlineData(@"\\server\share\Offload.exe", null)]
    [InlineData(@"Offload.exe", null)]
    [InlineData(@"C:Offload.exe", null)]
    public void InteropPath(string windows, string? expected) => Assert.Equal(expected, ClientLocations.WslInteropPath(windows));

    [Theory]
    [InlineData("/home/dev", true)]
    [InlineData("/root", true)]
    [InlineData("/home/../etc", false)]
    [InlineData("home/dev", false)]
    [InlineData("/home/dev\\x", false)]
    [InlineData("/home/de\nv", false)]
    [InlineData("C:/Users", false)]
    [InlineData("", false)]
    public void LinuxHome_IsValidated(string home, bool ok) => Assert.Equal(ok, ClientLocations.IsSafeLinuxHome(home));

    [Fact]
    public async Task StoppedDistro_IsNotStartedByRefreshOrUninstall_OnlyByUserAction()
    {
        using var sb = WithUbuntu();
        var config = sb.Write(sb.Wsl("Ubuntu", Home + "/.claude.json"), "{}");
        Assert.True((await Ubuntu().RegisterAsync(sb.Spec(), TestContext.Current.CancellationToken)).Ok);
        var registered = File.ReadAllText(config);
        var starts = ClientLocations.SandboxWslStarts;

        using (ClientLocations.SimulateStoppedWslDistros("Ubuntu"))
        {
            Assert.False(WslProbe.IsRunning("Ubuntu"));
            Assert.True(WslProbe.IsRunning("Debian"));
            Assert.Equal(["Ubuntu"], WslProbe.Stopped());

            // Обновление страницы: строка есть, но дистрибутив не запускается и его файлы не читаются.
            var wsl = Ubuntu();
            Assert.False(wsl.IsClientInstalled());
            Assert.Null(wsl.ConfigPath);
            Assert.Null(ClientLocations.WslHome("Ubuntu"));

            // Удаление программы: остановленный дистрибутив не трогается.
            await IntegrationRegistry.UnregisterAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(registered, File.ReadAllText(config));
            Assert.Equal(starts, ClientLocations.SandboxWslStarts);

            // Явное действие пользователя («Определить», «Подключить»/«Отключить» внутри AllowStart) — запускает.
            Assert.Equal(Home, WslProbe.Detect()["Ubuntu"]);
            Assert.True(ClientLocations.SandboxWslStarts > starts);
            using (WslProbe.AllowStart())
            {
                Assert.Equal(config, Ubuntu().ConfigPath);
                Assert.True((await Ubuntu().UnregisterAsync(TestContext.Current.CancellationToken)).Ok);
            }
            Assert.Null(ClientLocations.WslHome("Ubuntu"));
        }
        Assert.Null(JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["offload"]);
        Assert.Equal(Home, ClientLocations.WslHome("Ubuntu"));
    }

    [Fact]
    public void UnsafeHomeFromSandbox_IsIgnored()
    {
        using var sb = new Sandbox(wslDistros: new Dictionary<string, string> { ["Evil"] = "/home/../../x" });
        var evil = Assert.Single(IntegrationRegistry.WslIntegrations());
        Assert.False(evil.IsClientInstalled());
        Assert.Null(evil.ConfigPath);
    }
}
