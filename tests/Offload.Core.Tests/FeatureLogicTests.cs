using System.Text.Json.Nodes;
using Offload.Core.Config;
using Offload.Core.Notifications;
using Offload.Core.Usage;
using Offload.Core.Util;

namespace Offload.Core.Tests;

public class FuzzyMatchTests
{
    private static readonly string[] Commands = ["Журнал", "Открыть журнал в Блокноте", "Интеграции", "Запустить сервер", "Остановить сервер", "Настройки"];

    [Fact]
    public void EmptyQuery_MatchesEverything_InOriginalOrder() =>
        Assert.Equal(Commands, FuzzyMatch.Rank("", Commands, c => [c]));

    [Fact]
    public void Prefix_And_ShortText_WinOverLongerContains()
    {
        var r = FuzzyMatch.Rank("журн", Commands, c => [c]);
        Assert.Equal(["Журнал", "Открыть журнал в Блокноте"], r);
    }

    [Fact]
    public void Subsequence_Works_AcrossWords_AndIgnoresCaseAndYo()
    {
        Assert.NotNull(FuzzyMatch.Score("зпс", "Запустить сервер"));
        Assert.NotNull(FuzzyMatch.Score("ИНТЕГР", "Интеграции"));
        Assert.NotNull(FuzzyMatch.Score("ещё", "Показать еще"));
        Assert.Null(FuzzyMatch.Score("xyz", "Запустить сервер"));
    }

    [Fact]
    public void MultipleWords_MustAllMatch()
    {
        Assert.Equal(["Остановить сервер"], FuzzyMatch.Rank("сервер ост", Commands, c => [c]));
        Assert.Empty(FuzzyMatch.Rank("сервер журнал", Commands, c => [c]));
    }

    [Fact]
    public void Keywords_AreSearchedToo()
    {
        var items = new[] { ("Интеграции", "ide claude cursor"), ("Журнал", "log") };
        Assert.Equal("Интеграции", Assert.Single(FuzzyMatch.Rank("cursor", items, i => [i.Item1, i.Item2])).Item1);
    }
}

public class NotificationHistoryTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Add_NewestFirst_Unread_MarkRead()
    {
        var h = new NotificationHistory();
        var changes = 0;
        h.Changed += () => changes++;
        h.Add("A", "a", NotificationLevel.Info, "status", shown: true, T0);
        h.Add("B", "b", NotificationLevel.Warning, null, shown: false, T0.AddMinutes(1));

        Assert.Equal(["B", "A"], h.Items.Select(i => i.Title));
        Assert.Equal(2, h.UnreadCount);
        Assert.False(h.Items[0].Shown);
        h.MarkAllRead();
        Assert.Equal(0, h.UnreadCount);
        h.Add("C", "c", NotificationLevel.Error, null, true, T0.AddMinutes(2));
        Assert.Equal(1, h.UnreadCount);
        Assert.True(h.IsUnread(h.Items[0]));
        Assert.False(h.IsUnread(h.Items[1]));
        Assert.Equal(4, changes);
    }

    [Fact]
    public void SameNotificationWithinMinute_IsNotDuplicated_AndCapacityIsKept()
    {
        var h = new NotificationHistory(capacity: 3);
        h.Add("A", "a", NotificationLevel.Info, null, true, T0);
        h.Add("A", "a", NotificationLevel.Info, null, true, T0.AddSeconds(30));
        Assert.Single(h.Items);
        h.Add("A", "a", NotificationLevel.Info, null, true, T0.AddMinutes(2));
        Assert.Equal(2, h.Items.Count);
        for (var i = 0; i < 5; i++) h.Add("N" + i, "", NotificationLevel.Info, null, true, T0.AddMinutes(10 + i));
        Assert.Equal(3, h.Items.Count);
        Assert.Equal("N4", h.Items[0].Title);
    }

    [Fact]
    public void Clear_EmptiesAndResetsUnread()
    {
        var h = new NotificationHistory();
        h.Add("A", "a", NotificationLevel.Info, null, true, T0);
        h.Clear();
        Assert.Empty(h.Items);
        Assert.Equal(0, h.UnreadCount);
    }
}

public class QuietHoursTests
{
    private static DateTime At(int h, int m) => new(2026, 9, 29, h, m, 0, DateTimeKind.Local);

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(2, 30, true)]
    [InlineData(7, 59, true)]
    [InlineData(8, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(22, 0, true)]
    [InlineData(21, 59, false)]
    public void OverMidnight(int h, int m, bool quiet) => Assert.Equal(quiet, QuietHours.IsQuiet(At(h, m), "22:00", "08:00"));

    [Theory]
    [InlineData(13, 0, true)]
    [InlineData(14, 0, false)]
    [InlineData(12, 59, false)]
    public void SameDay(int h, int m, bool quiet) => Assert.Equal(quiet, QuietHours.IsQuiet(At(h, m), "13:00", "14:00"));

    [Theory]
    [InlineData(null, "08:00")]
    [InlineData("25:00", "08:00")]
    [InlineData("abc", "08:00")]
    [InlineData("08:00", "08:00")]
    public void InvalidOrEmptyRange_IsNeverQuiet(string? from, string? to) => Assert.False(QuietHours.IsQuiet(At(3, 0), from, to));

    [Fact]
    public void Parse_AcceptsShortHour()
    {
        Assert.True(QuietHours.TryParse("7:05", out var t));
        Assert.Equal(new TimeSpan(7, 5, 0), t);
    }
}

[Collection("Language")]
public class TrendTextTests
{
    [Fact]
    public void Compare_RoundsAndSigns()
    {
        L.Initialize("ru");
        Assert.Equal(new Trend("+50 %", 1), TrendText.Compare(150, 100));
        Assert.Equal(new Trend("−25 %", -1), TrendText.Compare(75, 100));
        Assert.Equal(0, TrendText.Compare(103, 100)!.Value.Direction);
        Assert.Equal(1, TrendText.Compare(5, 0)!.Value.Direction);
        Assert.Null(TrendText.Compare(0, 0));
        Assert.StartsWith("×", TrendText.Compare(5000, 10)!.Value.Text, StringComparison.Ordinal);
        Assert.Null(TrendText.Compare(double.NaN, 1));
    }

    [Fact]
    public void PreviousAverage_ExcludesToday()
    {
        Assert.Equal(2, TrendText.PreviousAverage([1, 2, 3, 100]));
        Assert.Equal(0, TrendText.PreviousAverage([100]));
        Assert.Equal(15, TrendText.PreviousAverage([1000, 10, 20, 0], days: 2));
    }
}

public class SettingsTransferTests
{
    private static AppConfig Sample()
    {
        var cfg = new AppConfig();
        cfg.Ui.Theme = "dark";
        cfg.Ui.ThemePreset = "aurora";
        cfg.Ui.QuietHoursEnabled = true;
        cfg.Ui.Window = new WindowPlacement();
        cfg.Server.ApiKey = "sk-secret-server";
        cfg.Server.ContextSize = 32768;
        cfg.Server.ExtraArgs = "--host 0.0.0.0";
        cfg.Server.LanAccess = true;
        cfg.Server.LanApiKeyProtected = "protected-lan";
        cfg.Mcp.HttpToken = "http-secret";
        cfg.Mcp.CloudInputPricePerMTok = 5;
        cfg.Mcp.VerifyCommandAllowlist = ["rm -rf /"];
        cfg.Mcp.RestrictWritesToWorkspace = false;
        cfg.Network.HfTokenProtected = "hf-secret";
        cfg.Network.ProxyUrl = "http://user:pass@proxy";
        cfg.Network.HfMirror = "https://evil.example";
        cfg.Remote.ApiKeyProtected = "remote-secret";
        cfg.Integrations.Add("claude-code");
        cfg.Autocomplete.ApiKey = "fim-secret";
        cfg.OpenCode.AllowShellCommands = true;
        return cfg;
    }

    [Fact]
    public void Export_ContainsOnlyAllowedKeys_NoSecrets()
    {
        var text = SettingsTransfer.Export(Sample(), new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc));

        foreach (var secret in new[] { "sk-secret-server", "protected-lan", "http-secret", "hf-secret", "user:pass", "remote-secret", "fim-secret", "evil.example", "rm -rf", "0.0.0.0", "claude-code" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        var root = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(1, (int)root[SettingsTransfer.Marker]!);
        Assert.Equal("aurora", (string?)root["ui"]!["themePreset"]);
        Assert.Equal(32768, (int)root["server"]!["contextSize"]!);
        Assert.Null(root["ui"]!["window"]);
        Assert.Null(root["remote"]);
        Assert.Null(root["models"]);
        foreach (var (section, node) in root)
        {
            if (node is not JsonObject obj) continue;
            Assert.True(SettingsTransfer.Allowed.TryGetValue(section, out var allowed), section);
            foreach (var (key, _) in obj) Assert.Contains(key, allowed!);
        }
    }

    [Fact]
    public void RoundTrip_AppliesAllowedKeys_KeepsLocalSecrets()
    {
        var exported = SettingsTransfer.Export(Sample(), DateTime.UtcNow);
        var local = new AppConfig();
        local.Server.ApiKey = "local-key";
        local.Integrations.Add("cursor");

        var import = SettingsTransfer.Prepare(exported, local);

        Assert.Contains("ui.theme", import.Changes);
        Assert.Contains("server.contextSize", import.Changes);
        Assert.Equal("dark", import.Result.Ui.Theme);
        Assert.Equal(32768, import.Result.Server.ContextSize);
        Assert.Equal(5, import.Result.Mcp.CloudInputPricePerMTok);
        Assert.Equal("local-key", import.Result.Server.ApiKey);
        Assert.Equal(["cursor"], import.Result.Integrations);
        Assert.NotEqual("dark", local.Ui.Theme); // исходная конфигурация не тронута
    }

    [Fact]
    public void Import_IgnoresSecurityAndSecretKeys_EvenIfPresentInFile()
    {
        var hostile = """
        {
          "offloadSettings": 1,
          "server": { "apiKey": "stolen", "extraArgs": "--host 0.0.0.0", "lanAccess": true, "parallel": 4 },
          "mcp": { "verifyCommandAllowlist": ["powershell"], "restrictWritesToWorkspace": false, "redactSecrets": false, "httpToken": "x" },
          "openCode": { "allowShellCommands": true },
          "network": { "hfMirror": "https://evil.example", "proxyMode": "custom", "proxyUrl": "http://evil" },
          "integrations": ["cursor"],
          "remote": { "enabled": true, "url": "http://evil" }
        }
        """;
        var local = new AppConfig();
        local.Server.ApiKey = "mine";

        var import = SettingsTransfer.Prepare(hostile, local);

        Assert.Equal(["server.parallel"], import.Changes);
        Assert.Equal(4, import.Result.Server.Parallel);
        Assert.Equal("mine", import.Result.Server.ApiKey);
        Assert.Equal("", import.Result.Server.ExtraArgs);
        Assert.False(import.Result.Server.LanAccess);
        Assert.True(import.Result.Mcp.RestrictWritesToWorkspace);
        Assert.True(import.Result.Mcp.RedactSecrets);
        Assert.False(import.Result.OpenCode.AllowShellCommands);
        Assert.Null(import.Result.Network.HfMirror);
        Assert.False(import.Result.Remote.Enabled);
        Assert.Empty(import.Result.Integrations);
        Assert.Contains("server.apiKey", import.Skipped);
        Assert.Contains("mcp.verifyCommandAllowlist", import.Skipped);
        Assert.Contains("remote", import.Skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ \"ui\": { \"theme\": \"dark\" } }")]
    [InlineData("{ not json")]
    [InlineData("{ \"offloadSettings\": 99 }")]
    [InlineData("{ \"offloadSettings\": 1, \"server\": { \"parallel\": \"много\" } }")]
    public void Prepare_RejectsForeignOrBrokenFiles(string text) =>
        Assert.Throws<SettingsTransferException>(() => SettingsTransfer.Prepare(text, new AppConfig()));

    [Fact]
    public void Apply_CopiesOnlyAllowedKeys_KeepsEverythingElseOfTarget()
    {
        var import = SettingsTransfer.Prepare(SettingsTransfer.Export(Sample(), DateTime.UtcNow), new AppConfig());
        var target = new AppConfig();
        target.Server.ApiKey = "target-key";
        target.Server.Port = 9999;
        target.Mcp.HttpToken = "target-token";
        target.Ui.LastTab = "models";

        SettingsTransfer.Apply(import.Result, target);

        Assert.Equal("dark", target.Ui.Theme);
        Assert.Equal("aurora", target.Ui.ThemePreset);
        Assert.Equal(32768, target.Server.ContextSize);
        Assert.Equal("target-key", target.Server.ApiKey);
        Assert.Equal(9999, target.Server.Port);
        Assert.Equal("target-token", target.Mcp.HttpToken);
        Assert.Equal("models", target.Ui.LastTab);
        Assert.True(target.Mcp.RestrictWritesToWorkspace);
    }

    [Fact]
    public void Prepare_SameValues_NoChanges()
    {
        var cfg = Sample();
        var import = SettingsTransfer.Prepare(SettingsTransfer.Export(cfg, DateTime.UtcNow), cfg);
        Assert.Empty(import.Changes);
    }
}

[Collection("AppPaths")]
public class BackupCatalogTests
{
    [Fact]
    public void BackupsOf_ListsOnlyThisFile_NewestFirst()
    {
        using var home = new TempHome();
        var file = Path.Join(home.Path, "ide", "mcp.json");
        var other = Path.Join(home.Path, "ide", "mcp.json.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "v1");
        File.WriteAllText(other, "x");
        var b1 = FileUtil.Backup(file);
        Thread.Sleep(20);
        File.WriteAllText(file, "v2");
        var b2 = FileUtil.Backup(file);
        FileUtil.Backup(other);

        var list = FileUtil.BackupsOf(file);

        Assert.Equal([b2, b1], list.Select(b => b.Path));
        Assert.Equal("v2", File.ReadAllText(list[0].Path));
        Assert.Equal(2, list[0].Size);
        Assert.Empty(FileUtil.BackupsOf(Path.Join(home.Path, "nothing.json")));
    }
}
