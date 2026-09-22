using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Offload.Core.Config;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Регрессионные тесты для проблем, найденных при самопроверке (обход проверок чтения, откат чужих задач).</summary>
[Collection("AppPaths")]
public class SecurityRegressionTests
{
    private static GatherOptions Opts => new(512 * 1024, 4 * 1024 * 1024, new McpSettings().SecretFilePatterns);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferSize);

    private static void Junction(string link, string target)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit(10_000);
        Assert.True(Directory.Exists(link), "junction was not created");
    }

    [Fact]
    public async Task ExplicitFileThroughJunctionToSecretFolder_IsSkipped()
    {
        using var env = new TestEnv();
        // Имитация ~/.ssh вне проекта: папка с «чувствительным» именем.
        var outside = Path.Combine(Path.GetTempPath(), "pc-out-" + Guid.NewGuid().ToString("N")[..8], ".ssh");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "config"), "Host secret\n");
        var link = Path.Combine(env.Workspace, "innocent");
        try
        {
            Junction(link, outside);
            var r = await FileGatherer.GatherAsync(["innocent/config"], [env.Workspace], Opts, CancellationToken.None);
            Assert.Empty(r.Files);
            Assert.Contains(r.Skipped, s => s.Reason == "secret");
            // Обход папки через ссылку наружу тоже запрещён.
            var dir = await FileGatherer.GatherAsync(["innocent", "innocent/**/*"], [env.Workspace], Opts, CancellationToken.None);
            Assert.Empty(dir.Files);
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(outside)!, true); } catch { }
        }
    }

    [Fact]
    public async Task ShortName83OfSecretFile_IsSkipped()
    {
        using var env = new TestEnv();
        var secret = env.WriteFile("secrets.production.json", "{\"password\":\"x\"}\n");
        var sb = new StringBuilder(1024);
        GetShortPathName(secret, sb, 1024);
        var shortName = Path.GetFileName(sb.ToString());
        if (string.IsNullOrEmpty(shortName) || shortName.Equals("secrets.production.json", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("8.3 short names are disabled on this volume");
        var r = await FileGatherer.GatherAsync([shortName], [env.Workspace], Opts, CancellationToken.None);
        Assert.Empty(r.Files);
        Assert.Contains(r.Skipped, s => s.Reason == "secret");
    }

    [Fact]
    public async Task OutsideWorkspace_DirsGlobsAndProfileDotFolders_Refused()
    {
        using var env = new TestEnv();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var r = await FileGatherer.GatherAsync([Path.Combine(profile, "*.txt"), Environment.GetFolderPath(Environment.SpecialFolder.Windows)],
            [env.Workspace], Opts, CancellationToken.None);
        Assert.Empty(r.Files);
        Assert.All(r.Skipped, s => Assert.Contains("outside the workspace", s.Reason));

        Assert.NotNull(PathGuard.CheckOutsideRoots(Path.Combine(profile, ".config", "gh", "hosts.yml"), [env.Workspace]));
        Assert.NotNull(PathGuard.CheckOutsideRoots(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "x", "token.json"), [env.Workspace]));
        Assert.Null(PathGuard.CheckOutsideRoots(Path.Combine(Path.GetTempPath(), "build.log"), [env.Workspace]));
        Assert.Null(PathGuard.CheckOutsideRoots(Path.Combine(env.Workspace, ".config", "x"), [env.Workspace]));
        // Явный файл вне проекта (не в профиле) по-прежнему читается.
        var external = Path.Combine(Path.GetTempPath(), "pc-ext-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(external, "hello\n");
        try
        {
            var ok = await FileGatherer.GatherAsync([external], [env.Workspace], Opts, CancellationToken.None);
            Assert.Single(ok.Files);
        }
        finally { File.Delete(external); }
    }

    [Fact]
    public void Revert_RefusesJobFromAnotherWorkspace_AndForceRecoversInterrupted()
    {
        using var env = new TestEnv();
        var other = Path.Combine(Path.GetTempPath(), "pc-other-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(other);
        try
        {
            var file = Path.Combine(other, "a.txt");
            File.WriteAllText(file, "orig\n");
            var job = JobStore.Create("local_edit_files", other, "t", null);
            JobStore.Snapshot(job, file, "a.txt");
            File.WriteAllText(file, "changed\n");
            // Задача «зависла» в running (процесс был убит).
            var ctx = env.Context();
            var ex = Assert.Throws<ToolException>(() => JobTool.Run(ctx, job.Id, "revert", 300, null, force: true));
            Assert.Contains("another workspace", ex.Message);

            Assert.False(JobStore.Revert(JobStore.Load(job.Id), force: false).Ok);
            Assert.True(JobStore.Revert(JobStore.Load(job.Id), force: true).Ok);
            Assert.Equal("orig\n", File.ReadAllText(file));
        }
        finally
        {
            try { Directory.Delete(other, true); } catch { }
        }
    }
}
