using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Usage;

namespace Offload.Core.Tests;

[Collection("AppPaths")]
public class ConfigExternalEditTests
{
    /// <summary>Правка файла «вручную»: изменить JSON и сдвинуть время записи (как сделал бы редактор).</summary>
    private static void EditOnDisk(Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        edit(node);
        WriteOnDisk(node.ToJsonString());
    }

    private static void WriteOnDisk(string text)
    {
        var before = File.GetLastWriteTimeUtc(AppPaths.ConfigFile);
        File.WriteAllText(AppPaths.ConfigFile, text);
        File.SetLastWriteTimeUtc(AppPaths.ConfigFile, before.AddSeconds(5));
    }

    [Fact]
    public void ManualEdit_IsNotOverwrittenByNextUpdate()
    {
        using var home = new TempHome();
        ConfigStore.Reload();
        ConfigStore.Update(c => c.Server.Port = 9999);
        var reloaded = 0;
        void OnChanged(AppConfig _) => reloaded++;
        ConfigStore.ExternallyChanged += OnChanged;
        try
        {
            EditOnDisk(o => o["mcp"]!["extraSystemPrompt"] = "Правка руками");

            ConfigStore.Update(c => c.Server.Parallel = 3);

            Assert.Equal(1, reloaded);
            var again = ConfigStore.Reload();
            Assert.Equal("Правка руками", again.Mcp.ExtraSystemPrompt);
            Assert.Equal(3, again.Server.Parallel);
            Assert.Equal(9999, again.Server.Port);
        }
        finally
        {
            ConfigStore.ExternallyChanged -= OnChanged;
        }
    }

    [Fact]
    public void ManualEdit_IsVisibleThroughCurrent()
    {
        using var home = new TempHome();
        ConfigStore.Reload();
        Assert.False(ConfigStore.Current.Ui.VerboseLog);
        EditOnDisk(o => o["ui"]!["verboseLog"] = true);
        Assert.True(ConfigStore.Current.Ui.VerboseLog, "ручная правка должна подхватываться без перезапуска");
    }

    [Fact]
    public void Touch_WithoutContentChange_DoesNotReload()
    {
        using var home = new TempHome();
        var cfg = ConfigStore.Reload();
        var reloaded = 0;
        void OnChanged(AppConfig _) => reloaded++;
        ConfigStore.ExternallyChanged += OnChanged;
        try
        {
            WriteOnDisk(File.ReadAllText(AppPaths.ConfigFile));
            Assert.Same(cfg, ConfigStore.Current);
            Assert.Equal(0, reloaded);
        }
        finally
        {
            ConfigStore.ExternallyChanged -= OnChanged;
        }
    }

    [Fact]
    public void InvalidManualEdit_KeepsLastGood_AndBacksUpBeforeOverwrite()
    {
        using var home = new TempHome();
        ConfigStore.Reload();
        ConfigStore.Update(c => c.Server.Port = 9999);
        var errors = new List<string>();
        void OnInvalid(string e) => errors.Add(e);
        ConfigStore.InvalidFile += OnInvalid;
        try
        {
            WriteOnDisk("{ \"server\": { \"port\": 1234, сломано");

            Assert.Equal(9999, ConfigStore.Current.Server.Port);
            Assert.Equal(9999, ConfigStore.Current.Server.Port);
            Assert.Single(errors); // один раз на версию сломанного файла
            Assert.Contains("сломано", File.ReadAllText(AppPaths.ConfigFile)); // файл пользователя не тронут

            ConfigStore.Update(c => c.Server.Parallel = 2);

            var backup = Assert.Single(Directory.GetFiles(AppPaths.BackupsDir));
            Assert.Contains("сломано", File.ReadAllText(backup));
            var again = ConfigStore.Reload();
            Assert.Equal((9999, 2), (again.Server.Port, again.Server.Parallel));
        }
        finally
        {
            ConfigStore.InvalidFile -= OnInvalid;
        }
    }

    [Fact]
    public void Reload_InvalidManualEdit_KeepsLastGood_AndLeavesFile()
    {
        using var home = new TempHome();
        ConfigStore.Reload();
        ConfigStore.Update(c => c.Server.Port = 9999);
        var key = ConfigStore.Current.Server.ApiKey;

        WriteOnDisk("{ \"server\": { \"port\": 1234, сломано");
        var cfg = ConfigStore.Reload();

        Assert.Equal(9999, cfg.Server.Port);
        Assert.Equal(key, cfg.Server.ApiKey); // иначе запущенный сервер и MCP разойдутся по ключу
        Assert.Contains("сломано", File.ReadAllText(AppPaths.ConfigFile));
        Assert.False(Directory.Exists(AppPaths.BackupsDir) && Directory.GetFiles(AppPaths.BackupsDir).Length > 0,
            "до сохранения настроек сломанный файл не копируется и не перезаписывается");
    }

    [Fact]
    public void ReadOnly_BrokenFileAtStart_NoWritesNoBackups()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigFile)!);
        File.WriteAllText(AppPaths.ConfigFile, "не json");
        ConfigStore.ReadOnly = true;
        try
        {
            for (var i = 0; i < 5; i++) ConfigStore.Reload();

            Assert.Equal("не json", File.ReadAllText(AppPaths.ConfigFile));
            Assert.False(Directory.Exists(AppPaths.BackupsDir) && Directory.GetFiles(AppPaths.BackupsDir).Length > 0,
                "MCP-процесс (только чтение) не должен плодить копии и вытеснять ими хорошие");
        }
        finally
        {
            ConfigStore.ReadOnly = false;
        }
    }

    [Fact]
    public void ManualEdit_ParallelAboveLimit_IsClamped()
    {
        using var home = new TempHome();
        ConfigStore.Reload();
        EditOnDisk(o => o["server"]!["parallel"] = 16);
        Assert.Equal(ServerSettings.MaxParallel, ConfigStore.Current.Server.Parallel);
    }

    [Fact]
    public void FixedManualEdit_IsAppliedAfterInvalidOne()
    {
        using var home = new TempHome();
        ConfigStore.Reload();
        WriteOnDisk("не json");
        Assert.Equal(8765, ConfigStore.Current.Server.Port);
        var node = new JsonObject { ["server"] = new JsonObject { ["port"] = 8800 } };
        var before = File.GetLastWriteTimeUtc(AppPaths.ConfigFile);
        File.WriteAllText(AppPaths.ConfigFile, node.ToJsonString());
        File.SetLastWriteTimeUtc(AppPaths.ConfigFile, before.AddSeconds(5));
        var cfg = ConfigStore.Current;
        Assert.Equal(8800, cfg.Server.Port);
        Assert.NotEmpty(cfg.Server.ApiKey); // значения по умолчанию заполнены
    }
}

public class LogLevelTests
{
    [Theory]
    [InlineData(false, null, LogLevel.Info)]
    [InlineData(true, null, LogLevel.Debug)]
    [InlineData(false, "debug", LogLevel.Debug)]
    [InlineData(true, "WARN", LogLevel.Warn)]
    [InlineData(true, " error ", LogLevel.Error)]
    [InlineData(true, "чепуха", LogLevel.Debug)]
    [InlineData(false, "", LogLevel.Info)]
    public void ResolveLevel_EnvOverridesSetting(bool verbose, string? env, LogLevel expected) =>
        Assert.Equal(expected, Log.ResolveLevel(verbose, env));

    [Fact]
    public void MutexName_SameForSamePath_DifferentForOthers()
    {
        var a = Log.MutexName(@"C:\Temp\logs\mcp.log");
        Assert.Equal(a, Log.MutexName(@"c:\temp\LOGS\mcp.log"));
        Assert.NotEqual(a, Log.MutexName(@"C:\Temp\logs\app.log"));
        Assert.StartsWith(@"Local\", a);
    }
}

[Collection("AppPaths")]
public class LogRotationTests
{
    [Fact]
    public void ConcurrentWriters_RotateOnce_AndLoseNothing()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "logs", "mcp.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int writers = 4, lines = 250;
        // ≈42 КБ всего при пороге 30 КБ: ровно одна ротация. Двойная (гонка без межпроцессной блокировки) потеряла бы строки.
        const long max = 30_000;

        var threads = Enumerable.Range(0, writers).Select(w => new Thread(() =>
        {
            for (var i = 0; i < lines; i++)
                Log.AppendLine(path, $"writer-{w}-line-{i:D4}-".PadRight(40, 'x'), max);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.True(File.Exists(path + ".1"), "журнал должен был повернуться");
        var all = File.ReadAllLines(path + ".1").Concat(File.ReadAllLines(path)).ToList();
        Assert.All(all, l => Assert.Matches(@"^writer-\d-line-\d{4}-x+$", l));
        Assert.Equal(writers * lines, all.Distinct().Count());
        Assert.True(new FileInfo(path).Length < max, "после ротации текущий файл меньше порога");
    }

    [Fact]
    public void AppendLine_FileHeldByAnotherWriter_StillAppends()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "app.log");
        using (var other = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            other.Write(Encoding.UTF8.GetBytes("чужая строка\n"));
            other.Flush();
            Log.AppendLine(path, "наша строка", 1_000_000);
        }
        Assert.Equal(["чужая строка", "наша строка"], File.ReadAllLines(path));
    }
}

[Collection("AppPaths")]
public class UsageRotationTests
{
    private static UsageRecord Rec(string tool, string? client = "claude-code", DateTime? at = null) =>
        new(at ?? DateTime.UtcNow, tool, client, 100, 20, 500, true, EstimatedSavedTokens: 50);

    private static void AgeCurrentFile(int months)
    {
        var t = DateTime.Now.AddMonths(-months);
        File.SetLastWriteTime(AppPaths.UsageFile, t);
    }

    private static string ArchiveName(int monthsAgo) =>
        "usage-" + DateTime.Now.AddMonths(-monthsAgo).ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl";

    [Fact]
    public void Summarize_UnknownClient_UsesInvariantKey()
    {
        var s = UsageLog.Summarize([Rec("local_ask", null), Rec("local_ask", " "), Rec("local_ask", "cursor")]);
        Assert.Equal(2, s.CallsByClient[UsageLog.UnknownClient]);
        Assert.Equal(1, s.CallsByClient["cursor"]);
    }

    [Fact]
    public void NewMonth_ArchivesCurrentFile_AndReadAllSeesEverything()
    {
        using var home = new TempHome();
        UsageLog.AppendLocal(Rec("a"));
        UsageLog.AppendLocal(Rec("b"));
        AgeCurrentFile(1);

        UsageLog.AppendLocal(Rec("c"));

        var archive = Path.Combine(home.Path, ArchiveName(1));
        Assert.True(File.Exists(archive), "прошлый месяц должен уйти в архив");
        Assert.Equal(2, File.ReadAllLines(archive).Length);
        Assert.Single(File.ReadAllLines(AppPaths.UsageFile));
        Assert.Equal(["a", "b", "c"], UsageLog.ReadAll().Select(r => r.Tool));
    }

    [Fact]
    public void LegacyFile_IsReadAndArchivedWhole()
    {
        using var home = new TempHome();
        // usage.jsonl старой версии: записи за несколько месяцев в одном файле.
        var old = new[] { Rec("x", at: DateTime.UtcNow.AddMonths(-3)), Rec("y", at: DateTime.UtcNow.AddMonths(-2)) };
        File.WriteAllLines(AppPaths.UsageFile, old.Select(r => System.Text.Json.JsonSerializer.Serialize(r, Util.Json.Compact)));
        AgeCurrentFile(2);
        Assert.Equal(2, UsageLog.ReadAll().Count);

        UsageLog.AppendLocal(Rec("z"));

        Assert.True(File.Exists(Path.Combine(home.Path, ArchiveName(2))));
        Assert.Equal(["x", "y", "z"], UsageLog.ReadAll().Select(r => r.Tool));
        Assert.Equal(["z"], UsageLog.ReadAll(DateTime.UtcNow.AddDays(-1)).Select(r => r.Tool));
    }

    [Fact]
    public void ArchiveNameConflict_GetsSuffix_AndClearRemovesAll()
    {
        using var home = new TempHome();
        File.WriteAllText(Path.Combine(home.Path, ArchiveName(1)), System.Text.Json.JsonSerializer.Serialize(Rec("old"), Util.Json.Compact) + "\n");
        UsageLog.AppendLocal(Rec("a"));
        AgeCurrentFile(1);
        UsageLog.AppendLocal(Rec("b"));

        Assert.Equal(2, UsageLog.ArchiveFiles().Count);
        Assert.EndsWith("-2.jsonl", UsageLog.ArchiveFiles()[1]);
        Assert.Equal(["old", "a", "b"], UsageLog.ReadAll().Select(r => r.Tool));

        UsageLog.Clear();
        Assert.Empty(UsageLog.ReadAll());
        Assert.Empty(UsageLog.ArchiveFiles());
    }

    [Fact]
    public void Reader_ReadsOnlyAppendedRecords()
    {
        using var home = new TempHome();
        var reader = new UsageReader();
        Assert.True(reader.Refresh());
        Assert.Empty(reader.Records);

        UsageLog.AppendLocal(Rec("first"));
        UsageLog.AppendLocal(Rec("second"));
        Assert.True(reader.Refresh());
        Assert.Equal(2, reader.Records.Count);
        var version = reader.Version;
        Assert.False(reader.Refresh(), "без новых записей — без изменений");
        Assert.Equal(version, reader.Version);

        // Портим уже прочитанную середину файла (не начало): инкрементальное чтение её не перечитывает.
        var bytes = File.ReadAllBytes(AppPaths.UsageFile);
        var pos = Encoding.UTF8.GetString(bytes).IndexOf("claude-code", StringComparison.Ordinal);
        Assert.True(pos > 64);
        Encoding.ASCII.GetBytes("CLAUDE-CODE").CopyTo(bytes, pos);
        using (var fs = new FileStream(AppPaths.UsageFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(bytes);

        UsageLog.AppendLocal(Rec("third"));
        Assert.True(reader.Refresh());
        Assert.Equal(["first", "second", "third"], reader.Records.Select(r => r.Tool));
        Assert.Equal("claude-code", reader.Records[0].Client);
        Assert.Equal("CLAUDE-CODE", UsageLog.ReadAll()[0].Client); // полное чтение видит правку — значит, reader её не перечитывал

        // Недописанная строка не учитывается, пока не появится перевод строки.
        var line = System.Text.Json.JsonSerializer.Serialize(Rec("fourth"), Util.Json.Compact);
        File.AppendAllText(AppPaths.UsageFile, line[..10]);
        Assert.False(reader.Refresh());
        File.AppendAllText(AppPaths.UsageFile, line[10..] + "\n");
        Assert.True(reader.Refresh());
        Assert.Equal("fourth", reader.Records[^1].Tool);
    }

    [Fact]
    public void Reader_FollowsRotationAndClear()
    {
        using var home = new TempHome();
        var reader = new UsageReader();
        UsageLog.AppendLocal(Rec("a"));
        UsageLog.AppendLocal(Rec("b"));
        reader.Refresh();
        AgeCurrentFile(1);
        UsageLog.AppendLocal(Rec("c"));

        Assert.True(reader.Refresh());
        Assert.Equal(["a", "b", "c"], reader.Records.Select(r => r.Tool));

        UsageLog.Clear();
        Assert.True(reader.Refresh());
        Assert.Empty(reader.Records);
        UsageLog.AppendLocal(Rec("d"));
        reader.Refresh();
        Assert.Equal(["d"], reader.Records.Select(r => r.Tool));
    }
}
