using System.Globalization;
using Offload.Core.Util;

namespace Offload.Core.Tests;

[Collection("AppPaths")]
public class BackupTests
{
    /// <summary>Часть имени копии после метки времени (закодированный путь исходного файла).</summary>
    private static string SafeName(string backupPath)
    {
        var name = Path.GetFileName(backupPath);
        return name[(name.IndexOf('_') + 1)..];
    }

    private static string Stamp(DateTime t) => t.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

    [Fact]
    public void Backup_SameMoment_NeverOverwrites()
    {
        using var home = new TempHome();
        var file = Path.Combine(home.Path, "settings.json");
        var copies = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(file, "v" + i);
            copies.Add(FileUtil.Backup(file)!);
        }
        Assert.All(copies, Assert.NotNull);
        Assert.Equal(copies.Count, copies.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        for (var i = 0; i < copies.Count; i++) Assert.Equal("v" + i, File.ReadAllText(copies[i]));
        Assert.All(copies, c => Assert.StartsWith(AppPaths.BackupsDir, c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Backup_NameTaken_AddsCounter()
    {
        using var home = new TempHome();
        var file = Path.Combine(home.Path, "a.json");
        File.WriteAllText(file, "new");
        var first = FileUtil.Backup(file)!;
        var safe = SafeName(first);

        // Занимаем имена ближайших миллисекунд (и с первыми номерами счётчика) — копия не должна их перезаписать.
        var start = DateTime.Now;
        var taken = new List<string>();
        for (var ms = 0; ms < 3000; ms++)
        {
            var stamp = Stamp(start.AddMilliseconds(ms));
            foreach (var p in new[] { $"{stamp}_{safe}", $"{stamp}-1_{safe}" }.Select(n => Path.Combine(AppPaths.BackupsDir, n)))
            {
                if (File.Exists(p)) continue;
                File.WriteAllText(p, "old");
                taken.Add(p);
            }
        }
        var second = FileUtil.Backup(file)!;
        Assert.NotNull(second);
        Assert.DoesNotContain(second, taken);
        Assert.Equal("new", File.ReadAllText(second));
        // Часть занятых имён могла уйти по лимиту копий, но ни одно не перезаписано.
        Assert.All(taken.Where(File.Exists), p => Assert.Equal("old", File.ReadAllText(p)));
    }

    [Fact]
    public void Backup_KeepsOnlyNewestPerFile()
    {
        using var home = new TempHome();
        var file = Path.Combine(home.Path, "b.json");
        var other = Path.Combine(home.Path, "other.json");
        File.WriteAllText(other, "other");
        var otherCopy = FileUtil.Backup(other)!;

        var copies = new List<string>();
        for (var i = 0; i < FileUtil.BackupsPerFile + 5; i++)
        {
            File.WriteAllText(file, "v" + i);
            copies.Add(FileUtil.Backup(file)!);
        }
        var safe = SafeName(copies[0]);
        var left = Directory.GetFiles(AppPaths.BackupsDir).Where(f => SafeName(f) == safe).ToList();
        // Последние копии плюс самая первая («до Offload»), которая не удаляется никогда.
        Assert.Equal(FileUtil.BackupsPerFile + 1, left.Count);
        Assert.All(copies.TakeLast(FileUtil.BackupsPerFile), c => Assert.True(File.Exists(c), c));
        Assert.True(File.Exists(copies[0]), "самая старая копия файла должна сохраниться");
        Assert.All(copies.Skip(1).Take(4), c => Assert.False(File.Exists(c), c));
        // Копии других файлов лимит не затрагивает.
        Assert.True(File.Exists(otherCopy));
    }

    [Fact]
    public void Backup_DeletesExpiredCopies_KeepsForeignFiles()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(AppPaths.BackupsDir);
        var old = DateTime.Now - FileUtil.BackupMaxAge - TimeSpan.FromDays(1);
        var fresh = DateTime.Now - FileUtil.BackupMaxAge + TimeSpan.FromDays(1);
        var expiredNew = Path.Combine(AppPaths.BackupsDir, $"{Stamp(old)}_C__x_other.json");
        var expiredLegacy = Path.Combine(AppPaths.BackupsDir, $"{old.AddMinutes(-1):yyyyMMdd-HHmmss}_C__x_other.json");
        var expiredCounter = Path.Combine(AppPaths.BackupsDir, $"{Stamp(old)}-2_C__x_other.json");
        // Свежих копий столько, сколько хранится всегда, — старые копии этого файла лишние и истекли.
        var recent = Enumerable.Range(0, FileUtil.BackupsAlwaysKept)
            .Select(i => Path.Combine(AppPaths.BackupsDir, $"{Stamp(fresh.AddMinutes(i))}_C__x_other.json")).ToList();
        var foreign = Path.Combine(AppPaths.BackupsDir, "readme.txt");
        foreach (var p in new[] { expiredNew, expiredLegacy, expiredCounter, foreign }.Concat(recent)) File.WriteAllText(p, "x");

        var file = Path.Combine(home.Path, "c.json");
        File.WriteAllText(file, "c");
        var copy = FileUtil.Backup(file);

        Assert.NotNull(copy);
        Assert.True(File.Exists(copy));
        Assert.False(File.Exists(expiredNew));
        Assert.True(File.Exists(expiredLegacy), "самая старая копия файла не удаляется даже по сроку");
        Assert.False(File.Exists(expiredCounter));
        Assert.All(recent, p => Assert.True(File.Exists(p), p));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Backup_ExpiredButLastCopies_AreKept()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(AppPaths.BackupsDir);
        var old = DateTime.Now - FileUtil.BackupMaxAge - TimeSpan.FromDays(100);
        // Единственная (и очень старая) копия конфига, который давно не менялся, — её терять нельзя.
        var onlyCopy = Path.Combine(AppPaths.BackupsDir, $"{old:yyyyMMdd-HHmmss}_C__Users_u_.claude.json");
        File.WriteAllText(onlyCopy, "x");

        var file = Path.Combine(home.Path, "c.json");
        File.WriteAllText(file, "c");
        FileUtil.Backup(file);

        Assert.True(File.Exists(onlyCopy), "последние копии файла удаляться по сроку не должны");
    }

    [Fact]
    public void Backup_LongPathsWithSameTail_KeepSeparateQuota()
    {
        using var home = new TempHome();
        var tail = new string('t', 200);
        var a = Path.Combine(home.Path, "a", tail, "settings.json");
        var b = Path.Combine(home.Path, "b", tail, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        var copyOfA = FileUtil.Backup(a)!;
        for (var i = 0; i < FileUtil.BackupsPerFile + 3; i++) FileUtil.Backup(b);

        Assert.True(File.Exists(copyOfA), "копии другого файла с тем же хвостом пути не должны вытеснять его копию");
        Assert.NotEqual(SafeName(copyOfA), SafeName(FileUtil.Backup(b)!));
    }

    [Fact]
    public void Backup_MissingFile_ReturnsNull()
    {
        using var home = new TempHome();
        Assert.Null(FileUtil.Backup(Path.Combine(home.Path, "nope.json")));
    }
}
