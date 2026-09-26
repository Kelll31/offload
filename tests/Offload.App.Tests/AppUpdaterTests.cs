using System.Security.Cryptography;
using System.Text;
using Offload.App.Services;
using Offload.Core.Net;
using Offload.Core.Update;

namespace Offload.App.Tests;

public sealed class AppUpdaterDecisionTests
{
    private static AppRelease Release(string version, params string[] assets) =>
        new($"v{version}", version, DateTime.UtcNow, null, assets.Select(a => new AppReleaseAsset(a, "http://127.0.0.1/" + a, 1)).ToList());

    [Theory]
    [InlineData(true, true, false, true, "None")]
    [InlineData(true, false, false, true, "None")]
    [InlineData(false, true, false, true, "Installer")]
    [InlineData(false, true, true, true, "Manual")]
    [InlineData(false, false, false, true, "Portable")]
    [InlineData(false, false, false, false, "None")]
    public void DecideMode_ByInstallKind(bool dev, bool installedHere, bool perMachine, bool singleFile, string expected)
    {
        Assert.Equal(Enum.Parse<AppUpdateMode>(expected), AppUpdater.DecideMode(dev, installedHere, perMachine, singleFile));
    }

    [Fact]
    public void Evaluate_OnlyNewerRelease()
    {
        var r = Release("1.5.0", "Offload.exe", "Offload-Setup-1.5.0.exe", "SHA256SUMS.txt");
        Assert.Null(AppUpdater.Evaluate(null, "1.0.0", AppUpdateMode.Portable));
        Assert.Null(AppUpdater.Evaluate(r, "1.5.0", AppUpdateMode.Portable));
        Assert.Null(AppUpdater.Evaluate(r, "1.6.0", AppUpdateMode.Installer));
        Assert.Null(AppUpdater.Evaluate(r, "1.0.0", AppUpdateMode.None));

        var portable = AppUpdater.Evaluate(r, "1.4.9", AppUpdateMode.Portable);
        Assert.NotNull(portable);
        Assert.Equal("Offload.exe", portable.AssetName);
        Assert.True(portable.CanInstall);

        var installer = AppUpdater.Evaluate(r, "1.4.9", AppUpdateMode.Installer);
        Assert.Equal("Offload-Setup-1.5.0.exe", installer?.AssetName);
    }

    [Fact]
    public void Evaluate_MissingAsset_FallsBackToManual()
    {
        var r = Release("2.0.0", "Offload.exe");
        var info = AppUpdater.Evaluate(r, "1.0.0", AppUpdateMode.Installer);
        Assert.Equal(AppUpdateMode.Manual, info?.Mode);
        Assert.False(info!.CanInstall);
        Assert.Contains("v2.0.0", info.PageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void PageUrl_OnlyRepositoryAddresses()
    {
        var good = new AppUpdateInfo(Release("2.0.0") with { HtmlUrl = "https://github.com/Kelll31/offload/releases/tag/v2.0.0" }, AppUpdateMode.Manual);
        Assert.Equal("https://github.com/Kelll31/offload/releases/tag/v2.0.0", good.PageUrl);
        var bad = new AppUpdateInfo(Release("2.0.0") with { HtmlUrl = "file:///C:/Windows/System32/calc.exe" }, AppUpdateMode.Manual);
        Assert.Equal("https://github.com/Kelll31/offload/releases/tag/v2.0.0", bad.PageUrl);
    }

    [Fact]
    public void CleanupLeftovers_RemovesNewPartAndOld()
    {
        Assert.SkipWhen(DevMode.Active, "режим разработчика: очистка выключена");
        var dir = Path.Combine(Path.GetTempPath(), "offload-left-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "Offload.exe");
            foreach (var f in new[] { exe, exe + ".new", exe + ".new.part", exe + ".old" }) File.WriteAllText(f, "x");
            AppUpdater.CleanupLeftovers(exe);
            Assert.True(File.Exists(exe), "сам exe должен остаться");
            Assert.False(File.Exists(exe + ".new") || File.Exists(exe + ".new.part") || File.Exists(exe + ".old"), "остатки обновления должны быть удалены");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void VerifySignature_OnlyWhenCurrentExeSigned()
    {
        // Текущий exe не подписан — проверка пропускается, даже если обновление не подписано.
        AppUpdater.VerifySignature("new.exe", "cur.exe", _ => false, _ => null);

        // Подписан текущий — обновление без подписи отклоняется.
        Assert.Throws<DownloadException>(() =>
            AppUpdater.VerifySignature("new.exe", "cur.exe", p => p == "cur.exe", _ => "CN=Offload"));

        // Подписано другим издателем — отклоняется.
        Assert.Throws<DownloadException>(() =>
            AppUpdater.VerifySignature("new.exe", "cur.exe", _ => true, p => p == "cur.exe" ? "CN=Offload" : "CN=Evil"));

        // Тот же издатель — принимается.
        AppUpdater.VerifySignature("new.exe", "cur.exe", _ => true, _ => "CN=Offload");
    }

    [Fact]
    public void BuildSetupCommand_WaitsForSilentSetupAndRelaunches()
    {
        var cmd = AppUpdater.BuildSetupCommand(@"C:\Users\u\AppData\Local\Offload\downloads\offload-update\Offload-Setup-1.5.0.exe",
            @"C:\Users\u\AppData\Local\Programs\Offload\Offload.exe", @"C:\Windows\System32");
        Assert.NotNull(cmd);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", cmd.Value.File);
        Assert.Contains("start \"\" /wait \"C:\\Users\\u\\AppData\\Local\\Offload\\downloads\\offload-update\\Offload-Setup-1.5.0.exe\" /SILENT /SUPPRESSMSGBOXES /NORESTART", cmd.Value.Args, StringComparison.Ordinal);
        Assert.EndsWith("start \"\" \"C:\\Users\\u\\AppData\\Local\\Programs\\Offload\\Offload.exe\"\"", cmd.Value.Args, StringComparison.Ordinal);

        // % раскрывается cmd даже в кавычках — такой путь через cmd не передаём.
        Assert.Null(AppUpdater.BuildSetupCommand(@"C:\Users\100%\setup.exe", @"C:\x\Offload.exe", @"C:\Windows\System32"));
        Assert.Null(AppUpdater.BuildSetupCommand(@"C:\Users\a!b\setup.exe", @"C:\x\Offload.exe", @"C:\Windows\System32"));
        Assert.StartsWith("/d /v:off /s /c ", cmd.Value.Args, StringComparison.Ordinal);
    }

    [Fact]
    public void SwapExe_ReplacesFileAndKeepsOld()
    {
        var dir = Path.Combine(Path.GetTempPath(), "offload-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "Offload.exe");
            File.WriteAllText(exe, "old");
            File.WriteAllText(exe + ".new", "new");
            AppUpdater.SwapExe(exe + ".new", exe);
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.Equal("old", File.ReadAllText(exe + ".old"));
            Assert.False(File.Exists(exe + ".new"), "новый файл должен встать на место exe");

            // Нового файла нет — прежний exe возвращается на место.
            Assert.ThrowsAny<IOException>(() => AppUpdater.SwapExe(exe + ".missing", exe));
            Assert.Equal("new", File.ReadAllText(exe));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

[Collection("AppPaths")]
public sealed class AppUpdaterDownloadTests
{
    private static AppUpdateInfo PortableUpdate(string baseUrl, bool withSums) =>
        new(new AppRelease("v9.0.0", "9.0.0", DateTime.UtcNow, null,
            withSums
                ? [new AppReleaseAsset("Offload.exe", baseUrl + "/Kelll31/offload/releases/download/vtest/Offload.exe", 11), new AppReleaseAsset("SHA256SUMS.txt", baseUrl + "/Kelll31/offload/releases/download/vtest/SHA256SUMS.txt", 0)]
                : [new AppReleaseAsset("Offload.exe", baseUrl + "/Kelll31/offload/releases/download/vtest/Offload.exe", 11)]),
            AppUpdateMode.Portable);

    [Fact]
    public async Task Download_Portable_VerifiesSha256FromSums()
    {
        Assert.SkipWhen(DownloadPolicy.AllowUnverified, "задан OFFLOAD_ALLOW_UNVERIFIED");
        using var home = new TempHome();
        await using var http = new FakeHttp();
        var ct = TestContext.Current.CancellationToken;
        var payload = Encoding.ASCII.GetBytes("new-offload");
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        http.Map("/Kelll31/offload/releases/download/vtest/Offload.exe", payload);
        http.Map("/Kelll31/offload/releases/download/vtest/SHA256SUMS.txt", $"{sha}  Offload.exe\n");

        // «Текущий exe» — неподписанный файл: подпись обновления не проверяется.
        var current = Path.Combine(home.Path, "Offload.exe");
        File.WriteAllText(current, "old");

        var file = await AppUpdater.DownloadAsync(PortableUpdate(http.BaseUrl, withSums: true), current, null, ct);
        Assert.Equal(current + ".new", file);
        Assert.Equal(payload, File.ReadAllBytes(file));

        // Проверенный файл планируется к замене; непроверенный — нет.
        try
        {
            AppUpdater.SchedulePortableSwap(file, current);
            Assert.True(AppUpdater.HasPendingLaunch, "проверенное обновление должно быть запланировано");
            var other = Path.Combine(home.Path, "other.exe");
            File.WriteAllText(other, "unverified");
            AppUpdater.SchedulePortableSwap(other, current);
            Assert.False(AppUpdater.HasPendingLaunch, "файл, не прошедший DownloadAsync, не запускается");
        }
        finally
        {
            AppUpdater.CancelPending();
        }
    }

    [Theory]
    [InlineData("1.0/../../x")]
    [InlineData("1.0&calc")]
    [InlineData("1.0\"x")]
    [InlineData("1.0%PATH%")]
    public async Task Download_InvalidVersion_Refused(string version)
    {
        using var home = new TempHome();
        var ct = TestContext.Current.CancellationToken;
        var release = new AppRelease("v" + version, version, DateTime.UtcNow, null,
            [new AppReleaseAsset(AppReleases.SetupAssetName(version), "http://127.0.0.1:9/setup.exe", 1)]);
        Assert.Null(AppUpdater.Evaluate(release, "1.0.0", AppUpdateMode.Installer));

        var info = new AppUpdateInfo(release, AppUpdateMode.Installer);
        var current = Path.Combine(home.Path, "Offload.exe");
        await Assert.ThrowsAsync<DownloadException>(() => AppUpdater.DownloadAsync(info, current, null, ct));
        Assert.Throws<DownloadException>(() => AppUpdater.EnsureDownloadPath(info, AppUpdater.DownloadPath(info, current), current));
    }

    [Fact]
    public void EnsureDownloadPath_OnlyExpectedNameInsideFolder()
    {
        using var home = new TempHome();
        var current = Path.Combine(home.Path, "Offload.exe");
        var installer = new AppUpdateInfo(new AppRelease("v9.0.0", "9.0.0", DateTime.UtcNow, null, []), AppUpdateMode.Installer);
        AppUpdater.EnsureDownloadPath(installer, AppUpdater.DownloadPath(installer, current), current);
        Assert.Throws<DownloadException>(() => AppUpdater.EnsureDownloadPath(installer, Path.Combine(home.Path, installer.AssetName), current));
        Assert.Throws<DownloadException>(() => AppUpdater.EnsureDownloadPath(installer, Path.Combine(AppUpdater.InstallerDownloadDir, "other.exe"), current));

        var portable = installer with { Mode = AppUpdateMode.Portable };
        AppUpdater.EnsureDownloadPath(portable, AppUpdater.DownloadPath(portable, current), current);
        Assert.Throws<DownloadException>(() => AppUpdater.EnsureDownloadPath(portable, Path.Combine(home.Path, "sub", "Offload.exe.new"), current));
    }

    [Fact]
    public async Task Download_WrongHashOrNoSums_Refused()
    {
        Assert.SkipWhen(DownloadPolicy.AllowUnverified, "задан OFFLOAD_ALLOW_UNVERIFIED");
        using var home = new TempHome();
        await using var http = new FakeHttp();
        var ct = TestContext.Current.CancellationToken;
        http.Map("/Kelll31/offload/releases/download/vtest/Offload.exe", Encoding.ASCII.GetBytes("tampered!!!"));
        http.Map("/Kelll31/offload/releases/download/vtest/SHA256SUMS.txt", $"{new string('0', 64)}  Offload.exe\n");
        var current = Path.Combine(home.Path, "Offload.exe");
        File.WriteAllText(current, "old");

        await Assert.ThrowsAsync<DownloadException>(() => AppUpdater.DownloadAsync(PortableUpdate(http.BaseUrl, withSums: true), current, null, ct));
        Assert.False(File.Exists(current + ".new"), "файл с неверной суммой не должен остаться");

        await Assert.ThrowsAsync<DownloadException>(() => AppUpdater.DownloadAsync(PortableUpdate(http.BaseUrl, withSums: false), current, null, ct));
        Assert.Equal("old", File.ReadAllText(current));
    }
}

/// <summary>Подпись и повторная проверка файла обновления перед заменой exe и запуском установщика.</summary>
public sealed class AppUpdaterTrustTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "offload-trust-" + Guid.NewGuid().ToString("N"));

    public AppUpdaterTrustTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly Func<string, bool> Unsigned = _ => false;
    private static readonly Func<string, string?> NoPublisher = _ => null;
    private const string SystemDir = @"C:\Windows\System32";

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void VerifySignature_CurrentSignatureInvalidButHasPublisher_StillRequired()
    {
        // Подпись текущего exe сейчас не проходит проверку (истёк сертификат без метки времени), но издатель у неё есть:
        // неподписанное обновление отклоняется.
        Func<string, string?> publisher = p => p == "cur.exe" ? "CN=X" : null;
        Assert.Throws<DownloadException>(() => AppUpdater.VerifySignature("new.exe", "cur.exe", _ => false, publisher));

        // Действительная подпись того же издателя — принимается, издатель закрепляется.
        Assert.Equal("CN=X", AppUpdater.VerifySignature("new.exe", "cur.exe", p => p == "new.exe", _ => "CN=X"));

        // Действительная подпись без издателя у текущего exe — сравнивать не с чем, обновление не ставится.
        Assert.Throws<DownloadException>(() => AppUpdater.RequiredPublisher("cur.exe", _ => true, NoPublisher));

        // Неподписанный текущий exe — подпись не требуется.
        Assert.Null(AppUpdater.VerifySignature("new.exe", "cur.exe", Unsigned, NoPublisher));
    }

    [Fact]
    public void OpenVerified_LocksFileAndRechecksHash()
    {
        var file = Write("Offload-Setup-9.0.0.exe", "setup");
        var update = new AppUpdater.VerifiedUpdate(file, Sha("setup"), null);
        using (var locked = AppUpdater.OpenVerified(update, Unsigned, NoPublisher))
        {
            Assert.Equal(0, locked.Position);
            // Пока дескриптор открыт, файл нельзя подменить, удалить или переименовать.
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(file, "evil"));
            Assert.ThrowsAny<IOException>(() => File.Move(file, file + ".moved"));
            Assert.ThrowsAny<IOException>(() => File.Delete(file));
        }
        Assert.Equal("setup", File.ReadAllText(file));

        // Содержимое изменилось после проверки — отказ, дескриптор освобождён.
        File.WriteAllText(file, "evil!");
        Assert.Throws<DownloadException>(() => AppUpdater.OpenVerified(update, Unsigned, NoPublisher));
        File.Delete(file);
    }

    [Fact]
    public void OpenVerified_RequiredSignatureRechecked()
    {
        var file = Write("Offload-Setup-9.0.0.exe", "setup");
        var update = new AppUpdater.VerifiedUpdate(file, Sha("setup"), "CN=Offload");
        Assert.Throws<DownloadException>(() => AppUpdater.OpenVerified(update, Unsigned, NoPublisher));
        Assert.Throws<DownloadException>(() => AppUpdater.OpenVerified(update, _ => true, _ => "CN=Evil"));
        using (AppUpdater.OpenVerified(update, _ => true, _ => "CN=Offload")) { }
    }

    [Fact]
    public void RunPending_Portable_SwapsAndStartsWhileLocked()
    {
        var exe = Write("Offload.exe", "old");
        var newExe = Write("Offload.exe.new", "new");
        var started = new List<string>();
        var reports = new List<Exception>();
        var pending = new AppUpdater.PendingLaunch(new AppUpdater.VerifiedUpdate(newExe, Sha("new"), null), Installer: false, exe);

        AppUpdater.RunPending(pending, Unsigned, NoPublisher, SystemDir, psi =>
        {
            started.Add(psi.FileName);
            // В момент запуска новая версия заблокирована от записи.
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(exe, "evil"));
        }, (_, ex) => reports.Add(ex));

        Assert.Equal([exe], started);
        Assert.Empty(reports);
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
    }

    [Fact]
    public void RunPending_Portable_TamperedAfterDownload_KeepsOldVersion()
    {
        var exe = Write("Offload.exe", "old");
        var newExe = Write("Offload.exe.new", "new");
        var pending = new AppUpdater.PendingLaunch(new AppUpdater.VerifiedUpdate(newExe, Sha("new"), null), Installer: false, exe);
        File.WriteAllText(newExe, "evil");
        var started = new List<string>();
        var reports = new List<Exception>();

        AppUpdater.RunPending(pending, Unsigned, NoPublisher, SystemDir, psi => started.Add(psi.FileName), (_, ex) => reports.Add(ex));

        Assert.Equal("old", File.ReadAllText(exe));
        Assert.Equal([exe], started);
        Assert.IsType<DownloadException>(Assert.Single(reports));
    }

    [Fact]
    public void RunPending_Installer_TamperedAfterDownload_NotLaunched()
    {
        var exe = Write("Offload.exe", "old");
        var setup = Write("Offload-Setup-9.0.0.exe", "setup");
        var pending = new AppUpdater.PendingLaunch(new AppUpdater.VerifiedUpdate(setup, Sha("setup"), null), Installer: true, exe);
        File.WriteAllText(setup, "evil");
        var started = new List<string>();
        var reports = new List<Exception>();

        AppUpdater.RunPending(pending, Unsigned, NoPublisher, SystemDir, psi => started.Add(psi.FileName), (_, ex) => reports.Add(ex));

        // Установщик не запускается, снова открывается прежняя версия.
        Assert.Equal([exe], started);
        Assert.IsType<DownloadException>(Assert.Single(reports));
    }

    [Fact]
    public void RunPending_Installer_StartsCmdWhileLocked()
    {
        var exe = Write("Offload.exe", "old");
        var setup = Write("Offload-Setup-9.0.0.exe", "setup");
        var pending = new AppUpdater.PendingLaunch(new AppUpdater.VerifiedUpdate(setup, Sha("setup"), null), Installer: true, exe);
        var started = new List<string>();
        var reports = new List<Exception>();

        AppUpdater.RunPending(pending, Unsigned, NoPublisher, SystemDir, psi =>
        {
            started.Add(psi.FileName);
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(setup, "evil"));
        }, (_, ex) => reports.Add(ex));

        Assert.Equal([Path.Combine(SystemDir, "cmd.exe")], started);
        Assert.Empty(reports);
    }

    [Fact]
    public void OpenVerified_Inheritable_ChildProcessKeepsLock()
    {
        // cmd.exe наследует проверенный дескриптор и держит блокировку после выхода Offload — до конца установки.
        var file = Write("Offload-Setup-9.0.0.exe", "setup");
        var update = new AppUpdater.VerifiedUpdate(file, Sha("setup"), null);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        System.Diagnostics.Process? child;
        using (AppUpdater.OpenVerified(update, Unsigned, NoPublisher, inheritable: true))
        {
            child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cmd, $"/d /s /c \"\"{ping}\" -n 30 127.0.0.1 >nul\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        Assert.NotNull(child);
        using (child)
        {
            try
            {
                Assert.ThrowsAny<IOException>(() => File.WriteAllText(file, "evil"));
            }
            finally
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }
        Assert.Equal("setup", File.ReadAllText(file));
    }
}
