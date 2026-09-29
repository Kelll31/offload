using Offload.Core;

namespace Offload.Integrations.Tests;

public class InstalledSpecTests
{
    private static McpServerSpec Current(string exe) =>
        new(AppInfo.McpServerId, exe, [AppInfo.McpArg], new Dictionary<string, string>());

    private static McpServerSpec Resolve(string? installedExe, string? installedVersion, McpServerSpec current, string currentVersion = "1.0.4",
        Func<string, bool>? exists = null) =>
        McpServerSpec.ForInstalledOrCurrent(installedExe, installedVersion, current, currentVersion, exists ?? (_ => true));

    [Fact]
    public void Installed_SameOrNewerVersion_WinsOverRunningExe()
    {
        using var sb = new Sandbox();
        var running = Current(sb.P("Downloads", "Offload.exe"));
        var installed = sb.P("Programs", "Offload", "Offload.exe");

        Assert.Equal(installed, Resolve(installed, "1.0.4", running).Command);
        Assert.Equal(installed, Resolve(installed, "1.1.0", running).Command);
        Assert.Equal([AppInfo.McpArg], Resolve(installed, "1.0.4", running).Args);
        Assert.Equal(AppInfo.McpServerId, Resolve(installed, "1.0.4", running).Name);
    }

    [Fact]
    public void OlderInstalledCopy_IsIgnored()
    {
        using var sb = new Sandbox();
        var running = Current(sb.P("Downloads", "Offload.exe"));
        Assert.Equal(running.Command, Resolve(sb.P("Programs", "Offload.exe"), "1.0.3", running).Command);
        // Предварительная версия младше релиза.
        Assert.Equal(running.Command, Resolve(sb.P("Programs", "Offload.exe"), "1.0.4-beta", running).Command);
    }

    [Fact]
    public void NoInstalledCopy_UnknownVersion_MissingFile_UncOrRelative_FallBackToRunning()
    {
        using var sb = new Sandbox();
        var running = Current(sb.P("Portable", "Offload.exe"));
        var installed = sb.P("Programs", "Offload.exe");

        Assert.Equal(running.Command, Resolve(null, null, running).Command);
        Assert.Equal(running.Command, Resolve(installed, null, running).Command);
        Assert.Equal(running.Command, Resolve(installed, "1.0.4", running, exists: _ => false).Command);
        Assert.Equal(running.Command, Resolve(@"\\server\share\Offload.exe", "1.0.4", running).Command);
        Assert.Equal(running.Command, Resolve("Offload.exe", "1.0.4", running).Command);
        Assert.Equal(running.Command, Resolve("", "1.0.4", running).Command);
    }

    [Fact]
    public void PortableCopy_KeepsItsOwnPath_ByDefault()
    {
        // Без установленной копии публичная перегрузка возвращает текущий exe.
        Assert.Equal(McpServerSpec.ForCurrentExecutable().Command, McpServerSpec.ForInstalledOrCurrent(null, null).Command);
    }
}
