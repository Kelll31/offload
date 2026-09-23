namespace Offload.Core.Tests;

/// <summary>Согласованность программы и установщика: по AppId программа находит установленную копию.</summary>
public class InstallerTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Offload.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void InstallerAppId_MatchesInnoScript()
    {
        var iss = File.ReadAllText(RepoFile("installer", "Offload.iss"));
        Assert.Contains($"#define AppGuid \"{AppInfo.InstallerAppId}\"", iss);
        Assert.Contains("AppId={{{#AppGuid}", iss);
    }

    [Fact]
    public void UninstallRegistryKey_IsInnoSetupKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppInfo.InstallerAppId + "}_is1", AppInfo.UninstallRegistryKey);
    }

    [Fact]
    public void Installer_IsPerUserOnly()
    {
        var iss = File.ReadAllText(RepoFile("installer", "Offload.iss"));
        // Режим «для всех пользователей» давал бы вторую, независимую установку.
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", iss);
        Assert.Contains("PrivilegesRequired=lowest", iss);
    }
}
