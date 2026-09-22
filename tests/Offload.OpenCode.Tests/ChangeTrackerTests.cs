using Offload.Core.Processes;

namespace Offload.OpenCode.Tests;

public class ChangeTrackerTests
{
    [Fact]
    public void ParsePorcelainZ_HandlesRenamesSpacesCyrillic()
    {
        var output = " M src/a.cs\0R  new name.txt\0old name.txt\0?? папка/файл с пробелом.txt\0D  gone.txt\0AM x.cs\0\r\n";
        var e = ChangeTracker.ParsePorcelainZ(output);
        Assert.Equal(5, e.Count);
        Assert.Equal((" M", "src/a.cs", (string?)null), (e[0].Status, e[0].Path, e[0].OrigPath));
        Assert.Equal(("R ", "new name.txt", (string?)"old name.txt"), (e[1].Status, e[1].Path, e[1].OrigPath));
        Assert.Equal(("??", "папка/файл с пробелом.txt"), (e[2].Status, e[2].Path));
        Assert.Equal(("D ", "gone.txt"), (e[3].Status, e[3].Path));
        Assert.Equal(("AM", "x.cs"), (e[4].Status, e[4].Path));
        Assert.Empty(ChangeTracker.ParsePorcelainZ(""));
    }

    [Fact]
    public async Task Snapshot_DetectsAddModifyDelete_SkipsNodeModules()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "1");
            File.WriteAllText(Path.Combine(dir, "mod.txt"), "1");
            File.WriteAllText(Path.Combine(dir, "del.txt"), "1");
            Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
            var before = ChangeTracker.Snapshot(dir, ChangeTracker.MaxSnapshotFiles);
            Assert.Null(before.GitRoot);

            await Task.Delay(20);
            File.WriteAllText(Path.Combine(dir, "mod.txt"), "22");
            File.Delete(Path.Combine(dir, "del.txt"));
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "новый.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "node_modules", "junk.js"), "x");

            var after = ChangeTracker.Snapshot(dir, ChangeTracker.MaxSnapshotFiles);
            Assert.Equal(new[] { "D del.txt", "M mod.txt", "A sub/новый.txt" }, ChangeTracker.Diff(before, after));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Git_DetectsChangesIncludingRepeatedEdit()
    {
        var git = ProcessRunner.FindOnPath("git.exe");
        Assert.SkipWhen(git is null, "git не установлен");
        var dir = NewDir();
        try
        {
            await Git(git!, dir, "init", "-q");
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "b");
            File.WriteAllText(Path.Combine(dir, "x.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "same.txt"), "s");
            await Git(git!, dir, "add", "-A");
            File.WriteAllText(Path.Combine(dir, "x.txt"), "x-changed-before"); // уже изменён до запуска
            Directory.CreateDirectory(Path.Combine(dir, "sub"));

            var before = await ChangeTracker.CaptureAsync(Path.Combine(dir, "sub"), CancellationToken.None);
            Assert.NotNull(before.GitRoot);

            await Task.Delay(20);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a-modified");
            File.Delete(Path.Combine(dir, "b.txt"));
            File.WriteAllText(Path.Combine(dir, "x.txt"), "x-changed-again-by-agent");
            Directory.CreateDirectory(Path.Combine(dir, "папка"));
            File.WriteAllText(Path.Combine(dir, "папка", "новый файл.txt"), "n");
            File.WriteAllText(Path.Combine(dir, "sub", "c.txt"), "c");

            var after = await ChangeTracker.CaptureAfterAsync(before, CancellationToken.None);
            // Пути — относительно рабочей папки (sub).
            Assert.Equal(new[] { "M ../a.txt", "D ../b.txt", "M ../x.txt", "A ../папка/новый файл.txt", "A c.txt" },
                ChangeTracker.Diff(before, after));
        }
        finally
        {
            ForceDelete(dir);
        }
    }

    private static async Task Git(string git, string dir, params string[] args)
    {
        var r = await ProcessRunner.RunAsync(git, ["-C", dir, .. args], dir, timeout: TimeSpan.FromSeconds(30));
        Assert.True(r.Success, r.StdErr);
    }

    internal static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-oc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void ForceDelete(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch
        {
            // Временная папка.
        }
    }
}
