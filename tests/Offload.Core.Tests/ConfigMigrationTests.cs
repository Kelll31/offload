using System.Text.Json.Nodes;
using Offload.Core.Config;

namespace Offload.Core.Tests;

public class ConfigMigrationStepsTests
{
    private static readonly ConfigMigrations.Step[] FakeSteps =
    [
        // 1 → 2: server.threads переименовано в server.cpuThreads.
        new(1, o =>
        {
            if (o["server"] is JsonObject s && s["threads"] is { } t)
            {
                s.Remove("threads");
                s["cpuThreads"] = t.DeepClone();
            }
        }),
        // 2 → 3: ui.theme из числа в строку.
        new(2, o =>
        {
            if (o["ui"] is JsonObject u && u["theme"] is JsonValue v && v.TryGetValue<int>(out var n))
                u["theme"] = n == 1 ? "dark" : "light";
        }),
    ];

    [Fact]
    public void Steps_AreContiguousFromOneToCurrent()
    {
        var froms = ConfigMigrations.Steps.Select(s => s.From).ToList();
        Assert.Equal(Enumerable.Range(1, ConfigMigrations.CurrentVersion - 1), froms);
    }

    [Fact]
    public void MissingField_MeansVersionOne() =>
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{}", Offload.Core.Util.Json.Options)!.SchemaVersion);

    [Fact]
    public void Migrate_AppliesStepsInOrder_AndSetsVersion()
    {
        var text = """
                   {
                     // комментарий пользователя
                     "schemaVersion": 1,
                     "server": { "threads": 8, "port": 9000 },
                     "ui": { "theme": 1 },
                   }
                   """;

        var result = ConfigMigrations.Migrate(text, 1, 3, FakeSteps);

        var o = JsonNode.Parse(result!)!.AsObject();
        Assert.Equal(3, (int)o["schemaVersion"]!);
        Assert.Equal(8, (int)o["server"]!["cpuThreads"]!);
        Assert.Null(o["server"]!["threads"]);
        Assert.Equal(9000, (int)o["server"]!["port"]!);
        Assert.Equal("dark", (string)o["ui"]!["theme"]!);
    }

    [Fact]
    public void Migrate_StartsFromFileVersion()
    {
        var result = ConfigMigrations.Migrate("""{ "schemaVersion": 2, "server": { "threads": 8 }, "ui": { "theme": 0 } }""", 2, 3, FakeSteps);

        var o = JsonNode.Parse(result!)!.AsObject();
        Assert.Equal(3, (int)o["schemaVersion"]!);
        Assert.Equal(8, (int)o["server"]!["threads"]!); // шаг 1 → 2 не применяется к файлу версии 2
        Assert.Equal("light", (string)o["ui"]!["theme"]!);
    }

    [Fact]
    public void Migrate_StopsAtGap()
    {
        var result = ConfigMigrations.Migrate("""{ "schemaVersion": 1, "ui": { "theme": 1 } }""", 1, 3, [FakeSteps[1]]);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void Migrate_NotNeeded_ReturnsNull(int from) =>
        Assert.Null(ConfigMigrations.Migrate("""{ "schemaVersion": 3 }""", from, 3, FakeSteps));
}

[Collection("AppPaths")]
public class ConfigMigrationStoreTests
{
    [Fact]
    public void NewerSchema_BackedUpOnce_AndWrittenWithCurrentVersion()
    {
        using var home = new TempHome();
        var newer = ConfigMigrations.CurrentVersion + 5;
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigFile)!);
        File.WriteAllText(AppPaths.ConfigFile, $$"""
                                                 { "schemaVersion": {{newer}}, "server": { "port": 9123, "apiKey": "pc-x" }, "futureSection": { "a": 1 } }
                                                 """);

        var cfg = ConfigStore.Reload();
        Assert.Equal(9123, cfg.Server.Port);
        ConfigStore.Update(c => c.Server.Parallel = 2);

        var backup = Assert.Single(Directory.GetFiles(AppPaths.BackupsDir));
        Assert.Contains("futureSection", File.ReadAllText(backup));
        var saved = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        // Содержимое записано в схеме этой сборки — новая версия снова применит к нему свои миграции.
        Assert.Equal(ConfigMigrations.CurrentVersion, (int)saved["schemaVersion"]!);

        // «Перезапуск» и новая запись — вторая копия не нужна.
        var elsewhere = Path.Combine(home.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        AppPaths.OverrideDataDir(elsewhere);
        ConfigStore.Reload();
        AppPaths.OverrideDataDir(home.Path);
        ConfigStore.Reload();
        ConfigStore.Update(c => c.Server.Parallel = 3);
        Assert.Single(Directory.GetFiles(AppPaths.BackupsDir));
    }

    [Fact]
    public void OlderSchema_IsRaisedToCurrent()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigFile)!);
        File.WriteAllText(AppPaths.ConfigFile, """{ "schemaVersion": 0, "server": { "port": 9124, "apiKey": "pc-x" } }""");

        var cfg = ConfigStore.Reload();

        Assert.Equal(ConfigMigrations.CurrentVersion, cfg.SchemaVersion);
        Assert.Equal(9124, cfg.Server.Port);
        var saved = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        Assert.Equal(ConfigMigrations.CurrentVersion, (int)saved["schemaVersion"]!);
    }

    [Fact]
    public void UnreadableFile_IsNotOverwrittenWithDefaults()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigFile)!);
        const string original = """{ "schemaVersion": 1, "server": { "port": 9555, "apiKey": "pc-x" } }""";
        File.WriteAllText(AppPaths.ConfigFile, original);

        using (new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var cfg = ConfigStore.Reload();
            Assert.NotEqual(9555, cfg.Server.Port);
            var key = cfg.Server.ApiKey;
            ConfigStore.Update(c => c.Server.Parallel = 2);
            Assert.Equal(key, ConfigStore.Current.Server.ApiKey); // повторные неудачи не меняют настройки в памяти
        }

        Assert.Equal(original, File.ReadAllText(AppPaths.ConfigFile));
        Assert.Equal(9555, ConfigStore.Current.Server.Port); // файл снова доступен — перечитан
    }
}
