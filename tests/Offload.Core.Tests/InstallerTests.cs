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
        // AppId={{{#AppGuid} → «{GUID» (без «}»), Inno Setup дописывает «_is1» — так ключ выглядит у всех установок с 1.0.0.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppInfo.InstallerAppId + "_is1", AppInfo.UninstallRegistryKey);
        var iss = File.ReadAllText(RepoFile("installer", "Offload.iss"));
        Assert.Contains(@"UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + '{' + '{#AppGuid}_is1';", iss);
    }

    /// <summary>
    /// Версия Inno Setup одна для CI и локальной сборки: #define в скрипте (он же отказывается собираться другой версией),
    /// текст #error, переменная workflow и подсказка build.ps1.
    /// </summary>
    [Fact]
    public void InnoSetupVersion_IsPinnedEverywhere()
    {
        var iss = File.ReadAllText(RepoFile("installer", "Offload.iss"));
        var m = System.Text.RegularExpressions.Regex.Match(iss, "#define InnoSetupVersion \"([0-9.]+)\"");
        Assert.True(m.Success, "в Offload.iss нет #define InnoSetupVersion");
        var v = m.Groups[1].Value;
        Assert.Contains("#if DecodeVer(Ver) != InnoSetupVersion", iss);
        Assert.Contains($"#error Нужен Inno Setup {v}: winget install JRSoftware.InnoSetup.7 --version {v}", iss);
        Assert.Contains($"INNO_SETUP_VERSION: \"{v}\"", File.ReadAllText(RepoFile(".github", "workflows", "build.yml")));
        Assert.Contains($"--version {v}", File.ReadAllText(RepoFile("scripts", "build.ps1")));
    }

    [Fact]
    public void Installer_IsPerUserOnly()
    {
        var iss = File.ReadAllText(RepoFile("installer", "Offload.iss"));
        // Режим «для всех пользователей» давал бы вторую, независимую установку.
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", iss);
        Assert.Contains("PrivilegesRequired=lowest", iss);
    }

    /// <summary>Версия по умолчанию в .iss — та же, что в Directory.Build.props (выпуск передаёт её явно через /DAppVersion).</summary>
    [Fact]
    public void AppVersionDefault_MatchesBuildProps()
    {
        var iss = File.ReadAllText(RepoFile("installer", "Offload.iss"));
        var props = File.ReadAllText(RepoFile("Directory.Build.props"));
        var fromProps = System.Text.RegularExpressions.Regex.Match(props, "<Version>([0-9.]+)</Version>");
        var fromIss = System.Text.RegularExpressions.Regex.Match(iss, "#ifndef AppVersion\\s+#define AppVersion \"([0-9.]+)\"");
        Assert.True(fromProps.Success, "в Directory.Build.props нет <Version>");
        Assert.True(fromIss.Success, "в Offload.iss нет значения AppVersion по умолчанию");
        Assert.Equal(fromProps.Groups[1].Value, fromIss.Groups[1].Value);
    }

    /// <summary>
    /// Подключение к IDE — только в программе (один путь кода: ConfigFile.Edit, бэкап, проверка запуском). Установщик конфиги IDE
    /// не пишет, а фиксированную папку установки и тихий запуск трея сохраняет: без них путь в IDE устаревал бы после обновления.
    /// </summary>
    [Fact]
    public void Installer_DoesNotWriteIdeConfigs_ButKeepsStablePathAndSilentStart()
    {
        var iss = File.ReadAllText(RepoFile("installer", "Offload.iss"));
        var code = string.Join('\n', iss.Split('\n').Where(l => !l.TrimStart().StartsWith(';')));
        foreach (var forbidden in new[] { ".claude", "claude_desktop_config", "mcp.json", "mcpServers" })
            Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\{#AppName}", iss);
        Assert.Contains("UsePreviousAppDir=yes", iss);
        Assert.Contains("Parameters: \"--background\"; Flags: nowait runasoriginaluser; Check: WizardSilent", iss);
        Assert.Contains("StopProcessesIn(ExpandConstant('{app}'))", iss);
    }
}
