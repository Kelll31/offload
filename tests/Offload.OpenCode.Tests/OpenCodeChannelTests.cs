using System.Runtime.InteropServices;
using Offload.Core;
using Offload.Core.Net;

namespace Offload.OpenCode.Tests;

[Collection("AppPaths")]
public class OpenCodeChannelTests
{
    [Theory]
    [InlineData(null, "stable")]
    [InlineData("", "stable")]
    [InlineData("stable", "stable")]
    [InlineData(" Latest ", "latest")]
    [InlineData("beta", "stable")]
    public void Normalize_UnknownIsStable(string? value, string expected) =>
        Assert.Equal(expected, OpenCodeChannels.Normalize(value));

    [Theory]
    // Канал не выбран явно (конфиг прежних версий): установлена версия новее проверенной → «latest», иначе «stable».
    [InlineData(null, "99.0.0", "latest")]
    [InlineData(null, "v99.1.0-beta", "latest")]
    [InlineData(null, OpenCodeReleases.PinnedVersion, "stable")]
    [InlineData(null, "1.0.0", "stable")]
    [InlineData(null, null, "stable")]
    [InlineData(null, "garbage", "stable")]
    [InlineData("", "99.0.0", "latest")]
    // Явный выбор пользователя важнее.
    [InlineData("stable", "99.0.0", "stable")]
    [InlineData("latest", "1.0.0", "latest")]
    public void Effective_NoExplicitChannel_NewerInstalledIsLatest(string? channel, string? installed, string expected)
    {
        var settings = new Offload.Core.Config.OpenCodeSettings { Channel = channel, InstalledVersion = installed };
        Assert.Equal(expected, OpenCodeChannels.Effective(settings));
    }

    [Fact]
    public void NewConfig_HasNoExplicitChannel() => Assert.Null(new Offload.Core.Config.AppConfig().OpenCode.Channel);

    [Theory]
    [InlineData("99.0.0", OpenCodeReleases.PinnedVersion, true)]
    [InlineData("v1.18.33", "1.18.32", true)]
    [InlineData("1.18.32", "1.18.32", false)]
    [InlineData("1.18.31", "1.18.32", false)]
    [InlineData(null, "1.18.32", false)]
    [InlineData("1.18.33", null, false)]
    public void IsDowngrade(string? installed, string? target, bool expected) =>
        Assert.Equal(expected, OpenCodeChannels.IsDowngrade(installed, target));

    [Fact]
    public void Persist_OldConfigWithNewerInstall_WritesLatest_ExplicitKept()
    {
        using var home = new TempHome();
        // Конфиг прежней версии: ключа channel нет, установлена версия новее проверенной.
        File.WriteAllText(AppPaths.ConfigFile, """{ "openCode": { "installedVersion": "99.0.0" } }""");
        Offload.Core.Config.ConfigStore.Reload();
        Assert.Null(Offload.Core.Config.ConfigStore.Current.OpenCode.Channel);

        Assert.Equal(OpenCodeChannels.Latest, OpenCodeChannels.Persist());
        Assert.Equal(OpenCodeChannels.Latest, Offload.Core.Config.ConfigStore.Reload().OpenCode.Channel);

        // Явно выбранный канал не меняется, даже если версия новее.
        Offload.Core.Config.ConfigStore.Update(c => c.OpenCode.Channel = OpenCodeChannels.Stable);
        Assert.Equal(OpenCodeChannels.Stable, OpenCodeChannels.Persist());
        Assert.Equal(OpenCodeChannels.Stable, Offload.Core.Config.ConfigStore.Reload().OpenCode.Channel);
    }

    [Fact]
    public void Persist_OldConfigWithPinnedInstall_WritesStable()
    {
        using var home = new TempHome();
        File.WriteAllText(AppPaths.ConfigFile, """{ "openCode": { "installedVersion": "1.0.0" } }""");
        Offload.Core.Config.ConfigStore.Reload();
        Assert.Equal(OpenCodeChannels.Stable, OpenCodeChannels.Persist());
        Assert.Equal(OpenCodeChannels.Stable, Offload.Core.Config.ConfigStore.Reload().OpenCode.Channel);
    }

    [Theory]
    [InlineData(Architecture.X64, true, "opencode-windows-x64.zip")]
    [InlineData(Architecture.X64, false, "opencode-windows-x64-baseline.zip")]
    [InlineData(Architecture.Arm64, true, "opencode-windows-arm64.zip")]
    public void Pinned_EveryAssetHasSizeAndSha256(Architecture arch, bool avx2, string expected)
    {
        var release = OpenCodeReleases.Pinned();
        Assert.Equal(OpenCodeReleases.PinnedVersion, release.Version);
        Assert.Equal("v" + OpenCodeReleases.PinnedVersion, release.Tag);
        Assert.False(release.FromApi);

        var asset = OpenCodeReleases.SelectAsset(release, arch, avx2);
        Assert.NotNull(asset);
        Assert.Equal(expected, asset.Name);
        Assert.True(asset.Size > 0);
        Assert.Matches("^[0-9a-f]{64}$", asset.Sha256!);
        Assert.Equal($"https://github.com/anomalyco/opencode/releases/download/v{OpenCodeReleases.PinnedVersion}/{expected}", asset.Url);
        DownloadPolicy.RequireChecksum(asset.Sha256, asset.Name, "OpenCode"); // не бросает: хэш зашит
    }

    [Fact]
    public void Pinned_ShaMatchesGitHubDigest()
    {
        // Тот же релиз в формате GitHub API (digest) должен давать ту же контрольную сумму, что зашита в таблице.
        var api = OpenCodeReleases.ParseRelease("""
            { "tag_name": "v1.18.32", "assets": [ { "name": "opencode-windows-x64.zip", "size": 62101772, "state": "uploaded",
              "browser_download_url": "https://github.com/anomalyco/opencode/releases/download/v1.18.32/opencode-windows-x64.zip",
              "digest": "sha256:1483C72D5ADCED825590A0ECF8CC18B3E87E535960A125DBF539D33BCE135D0F" } ] }
            """);
        var pinned = OpenCodeReleases.PinnedAssets[OpenCodeReleases.AssetX64];
        Assert.Equal(api.Assets[0].Sha256, pinned.Sha256);
        Assert.Equal(api.Assets[0].Size, pinned.Size);
    }

    [Fact]
    public void KeepPrevious_SavesCopyAndVersion()
    {
        using var home = new TempHome();
        Assert.Null(OpenCodeInstaller.GetPrevious());

        Directory.CreateDirectory(OpenCodeInstaller.BinDir);
        File.WriteAllText(OpenCodeInstaller.ManagedExePath, "v1");
        OpenCodeInstaller.KeepPrevious(OpenCodeInstaller.ManagedExePath, "1.18.30");

        var previous = OpenCodeInstaller.GetPrevious();
        Assert.NotNull(previous);
        Assert.Equal("1.18.30", previous.Version);
        Assert.Equal("v1", File.ReadAllText(previous.ExecutablePath));
        Assert.Equal("v1", File.ReadAllText(OpenCodeInstaller.ManagedExePath)); // текущий остаётся на месте
        Assert.StartsWith(home.Path, previous.ExecutablePath, StringComparison.OrdinalIgnoreCase);

        // Следующее обновление заменяет сохранённую версию.
        File.WriteAllText(OpenCodeInstaller.ManagedExePath, "v2");
        OpenCodeInstaller.KeepPrevious(OpenCodeInstaller.ManagedExePath, "1.18.31");
        Assert.Equal("1.18.31", OpenCodeInstaller.GetPrevious()!.Version);
        Assert.Equal("v2", File.ReadAllText(OpenCodeInstaller.PreviousExePath));
    }

    [Fact]
    public async Task Rollback_WithoutPrevious_Throws()
    {
        using var home = new TempHome();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => OpenCodeInstaller.RollbackAsync(null, TestContext.Current.CancellationToken));
        Assert.Contains("не сохранена", ex.Message, StringComparison.Ordinal);
    }
}
