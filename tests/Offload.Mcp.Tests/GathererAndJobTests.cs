using System.Diagnostics;
using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

[Collection("AppPaths")]
public class FileGathererTests
{
    private static GatherOptions Opts(int maxFile = 512 * 1024, long maxTotal = 4 * 1024 * 1024) =>
        new(maxFile, maxTotal, new Offload.Core.Config.McpSettings().SecretFilePatterns);

    [Fact]
    public async Task Globs_Dirs_SkipsBinarySecretsAndIgnoredDirs()
    {
        using var env = new TestEnv();
        env.WriteFile("src/a.cs", "class A {}\n");
        env.WriteFile("src/sub/b.cs", "class B {}\n");
        env.WriteFile("src/readme.md", "# doc\n");
        env.WriteFile("src/.env", "KEY=secret\n");
        env.WriteFile("node_modules/lib/x.cs", "class X {}\n");
        File.WriteAllBytes(env.PathOf("src/blob.dat.txt"), [65, 0, 66, 67]);
        File.WriteAllBytes(env.PathOf("src/logo.png"), [1, 2, 3]);

        var r = await FileGatherer.GatherAsync(["src/**/*.cs"], [env.Workspace], Opts(), CancellationToken.None);
        Assert.Equal(["src/a.cs", "src/sub/b.cs"], r.Files.Select(f => f.Display).OrderBy(x => x).ToArray());

        var dir = await FileGatherer.GatherAsync(["."], [env.Workspace], Opts(), CancellationToken.None);
        var names = dir.Files.Select(f => f.Display).ToList();
        Assert.Contains("src/readme.md", names);
        Assert.DoesNotContain(names, n => n.Contains("node_modules"));
        Assert.DoesNotContain(names, n => n.EndsWith(".env"));
        Assert.Contains(dir.Skipped, s => s.Display == "src/.env" && s.Reason == "secret");
        Assert.Contains(dir.Skipped, s => s.Display == "src/blob.dat.txt" && s.Reason == "binary");
        Assert.Contains(dir.Skipped, s => s.Display == "src/logo.png" && s.Reason == "binary");

        var explicitSecret = await FileGatherer.GatherAsync(["src/.env", "missing.cs"], [env.Workspace], Opts(), CancellationToken.None);
        Assert.Empty(explicitSecret.Files);
        Assert.Contains("secret", explicitSecret.CoverageLine());
        Assert.Contains("not found", explicitSecret.CoverageLine());
    }

    [Fact]
    public async Task Cp1251AndSizeCaps()
    {
        using var env = new TestEnv();
        File.WriteAllBytes(env.PathOf("legacy.pas"), Offload.Mcp.Infrastructure.TextCodec.Cp1251.GetBytes("// Привет, мир\r\nbegin end.\r\n"));
        env.WriteFile("big.txt", string.Concat(Enumerable.Repeat("0123456789\n", 1000)));
        env.WriteFile("c.txt", "small\n");

        var r = await FileGatherer.GatherAsync(["legacy.pas", "big.txt", "c.txt"], [env.Workspace], Opts(maxFile: 4096, maxTotal: 4096 + 30), CancellationToken.None);
        var legacy = r.Files.Single(f => f.Display == "legacy.pas");
        Assert.Contains("Привет, мир", legacy.Text);
        Assert.Equal(TextEncodingKind.Windows1251, legacy.Format.Kind);
        var big = r.Files.Single(f => f.Display == "big.txt");
        Assert.True(big.Truncated);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(big.Text));
        Assert.Contains(r.Skipped, s => s.Display == "c.txt" && s.Reason == "total size limit");
        Assert.Contains("big.txt (truncated)", r.CoverageLine());
        Assert.StartsWith("1| // Привет", legacy.Numbered());
    }

    [Fact]
    public async Task GitIgnoreRespected()
    {
        if (Git.Executable is null) Assert.Skip("git is not installed");
        using var env = new TestEnv();
        var init = Process.Start(new ProcessStartInfo(Git.Executable!, "init -q") { WorkingDirectory = env.Workspace, UseShellExecute = false, CreateNoWindow = true })!;
        await init.WaitForExitAsync();
        env.WriteFile(".gitignore", "generated/\n*.log\n");
        env.WriteFile("src/a.cs", "class A {}\n");
        env.WriteFile("generated/g.cs", "class G {}\n");
        env.WriteFile("debug.log", "x\n");
        var r = await FileGatherer.GatherAsync(["**/*"], [env.Workspace], Opts(), CancellationToken.None);
        var names = r.Files.Select(f => f.Display).ToList();
        Assert.Contains("src/a.cs", names);
        Assert.Contains(".gitignore", names);
        Assert.DoesNotContain("generated/g.cs", names);
        Assert.DoesNotContain("debug.log", names);
        // Явно указанный игнорируемый файл читается.
        var explicitFile = await FileGatherer.GatherAsync(["debug.log"], [env.Workspace], Opts(), CancellationToken.None);
        Assert.Single(explicitFile.Files);
    }

    [Fact]
    public async Task EnumerationCapStopsBlowup()
    {
        using var env = new TestEnv();
        for (var i = 0; i < 60; i++) env.WriteFile($"d{i % 6}/f{i}.txt", "x\n");
        var r = await FileGatherer.GatherAsync(["**/*.txt"], [env.Workspace],
            new GatherOptions(1024, 1 << 20, [], MaxFiles: 10, MaxEntriesVisited: 30), CancellationToken.None);
        Assert.True(r.Files.Count <= 10);
        Assert.NotNull(r.LimitNote);
    }
}

[Collection("AppPaths")]
public class JobStoreTests
{
    [Fact]
    public void SnapshotDiffRevertAndForce()
    {
        using var env = new TestEnv();
        var existing = env.WriteFile("src/a.cs", "line1\nline2\nline3\n");
        var created = env.PathOf("src/new.cs");
        var job = JobStore.Create("local_edit_files", env.Workspace, "task", null);
        JobStore.Snapshot(job, existing, "src/a.cs");
        JobStore.Snapshot(job, created, "src/new.cs");

        File.WriteAllText(existing, "line1\nLINE2\nline3\nline4\n");
        File.WriteAllText(created, "new file\n");
        JobStore.Finish(job, JobStatus.Applied);
        var loaded = JobStore.Load(job.Id);
        Assert.Equal((2, 1), (loaded.Files[0].Added, loaded.Files[0].Removed));
        Assert.Equal((1, 0), (loaded.Files[1].Added, loaded.Files[1].Removed));

        var diff = JobStore.Diff(loaded, 300, 10_000, null);
        Assert.Contains("--- a/src/a.cs", diff);
        Assert.Contains("-line2", diff);
        Assert.Contains("+LINE2", diff);
        Assert.Contains("--- /dev/null", diff);
        Assert.Contains("+new file", diff);
        var filtered = JobStore.Diff(loaded, 300, 10_000, ["src/new.cs"]);
        Assert.DoesNotContain("a/src/a.cs", filtered);
        Assert.Contains("truncated", JobStore.Diff(loaded, 3, 10_000, null));

        // Изменение после завершения задачи → откат только с force.
        File.AppendAllText(existing, "user edit\n");
        var refused = JobStore.Revert(loaded, force: false);
        Assert.False(refused.Ok);
        Assert.Contains("src/a.cs", refused.Message);
        var forced = JobStore.Revert(JobStore.Load(job.Id), force: true);
        Assert.True(forced.Ok, forced.Message);
        Assert.Equal("line1\nline2\nline3\n", File.ReadAllText(existing));
        Assert.False(File.Exists(created));
        Assert.Contains("already reverted", JobStore.Revert(JobStore.Load(job.Id), false).Message);
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("20260922-183005-ab12\\..\\..")]
    [InlineData("")]
    public void InvalidIdsRejected(string id) => Assert.Throws<ToolException>(() => JobStore.Load(id));

    [Fact]
    public void CleanupRemovesOldJobs()
    {
        using var env = new TestEnv();
        var old = Path.Combine(JobStore.JobsDir, "20200101-000000-abcd");
        Directory.CreateDirectory(old);
        var fresh = JobStore.Create("t", env.Workspace, "x", null);
        Assert.Equal(1, JobStore.CleanupOld(7));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(JobStore.DirOf(fresh.Id)));
    }
}
