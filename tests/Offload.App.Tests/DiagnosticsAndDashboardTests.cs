using System.IO.Compression;
using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Usage;

namespace Offload.App.Tests;

public sealed class DiagnosticsMaskingTests
{
    private const string ApiKey = "k3y-Secret-123456";

    [Fact]
    public void MaskConfig_HidesKeysTokensAndSecrets_KeepsOtherValues()
    {
        var json = $$"""
            {
              // комментарий допустим
              "server": { "host": "127.0.0.1", "port": 8765, "apiKey": "{{ApiKey}}" },
              "models": { "hfToken": "hf_abcdefghijklmnop", "maxTokens": 4096, "dir": "D:\\models" },
              "mcp": { "secretFilePatterns": ["*.pem", ".env"], "note": "ключ {{ApiKey}} внутри" },
              "integrations": ["claude-code"],
              "extra": { "env": { "GITHUB_TOKEN": "ghp_1234567890abcdef", "PATH_HINT": "sk-live-abcdefghijk" } },
              "ui": { "password": "", "theme": "dark" }
            }
            """;
        var masked = DiagnosticsBundle.MaskConfig(json, [ApiKey]);

        Assert.DoesNotContain(ApiKey, masked, StringComparison.Ordinal);
        Assert.DoesNotContain("hf_abcdefghijklmnop", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_1234567890abcdef", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-abcdefghijk", masked, StringComparison.Ordinal);
        Assert.Contains("\"maxTokens\": 4096", masked, StringComparison.Ordinal);
        Assert.Contains("\"host\": \"127.0.0.1\"", masked, StringComparison.Ordinal);
        Assert.Contains("*.pem", masked, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"dark\"", masked, StringComparison.Ordinal);
        Assert.Contains("\"apiKey\": \"***\"", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskConfig_InvalidJson_MaskedAsText()
    {
        var masked = DiagnosticsBundle.MaskConfig("{ \"apiKey\": \"abcdef123\", broken", []);
        Assert.DoesNotContain("abcdef123", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskText_HidesCommandLineKeysAndBearer()
    {
        var log = $"llama-server.exe --port 8765 --api-key {ApiKey} --ctx-size 8192\nAuthorization: Bearer abc.def-123\n--api-key=other-key-777";
        var masked = DiagnosticsBundle.MaskText(log, [ApiKey]);
        Assert.DoesNotContain(ApiKey, masked, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def-123", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("other-key-777", masked, StringComparison.Ordinal);
        Assert.Contains("--ctx-size 8192", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskConfig_ProxyUrlWithPassword_PasswordHidden()
    {
        var masked = DiagnosticsBundle.MaskConfig("""{"network":{"proxyUrl":"http://u:S3cretPass@p:3128","proxyMode":"custom"}}""", []);

        Assert.DoesNotContain("S3cretPass", masked, StringComparison.Ordinal);
        Assert.Contains("http://***@p:3128", masked, StringComparison.Ordinal);
        Assert.Contains("\"proxyMode\": \"custom\"", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskText_UrlUserInfo_HiddenEverywhere()
    {
        var log = "proxy socks5://admin:p%40ss@10.0.0.1:1080 failed; mirror https://token@mirror.example/x; plain https://github.com/a@b";

        var masked = DiagnosticsBundle.MaskText(log, []);

        Assert.DoesNotContain("admin", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("p%40ss", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("token@", masked, StringComparison.Ordinal);
        Assert.Contains("socks5://***@10.0.0.1:1080", masked, StringComparison.Ordinal);
        Assert.Contains("https://github.com/a@b", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsOf_ProxyCredentials_Included()
    {
        var cfg = new AppConfig();
        cfg.Network.ProxyUrl = "http://user:S3cret%21Pass@proxy.local:3128";

        var secrets = DiagnosticsBundle.SecretsOf(cfg);

        // Пароль, встреченный в журнале в раскодированном виде (без адреса), тоже маскируется.
        var masked = DiagnosticsBundle.MaskText("auth failed with S3cret!Pass", secrets);
        Assert.DoesNotContain("S3cret!Pass", masked, StringComparison.Ordinal);
        Assert.Contains("S3cret%21Pass", secrets);
    }

    [Theory]
    [InlineData("sk_" + "live_" + "4eC39HqLyjWDarjtT1zdp7dc")]
    [InlineData("rk_" + "live_" + "51H8xYzAbCdEfGhIjKlMnOp")]
    [InlineData("AI" + "za" + "SyD-1234567890abcdefghijklmnopqrstu")]
    [InlineData("np" + "m_" + "a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8")]
    [InlineData("ey" + "JhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [InlineData("gh" + "p_" + "R8mK2qW9zL4vN7bX1cY6hJ3dF5gT0pAsQeUo")]
    [InlineData("github" + "_pat_" + "11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMN")]
    [InlineData("hf" + "_" + "AbCdEfGhIjKlMnOpQrStUvWxYz12345678")]
    [InlineData("AS" + "IA" + "Z7Q4RLWX3B2NPYCD")]
    public void MaskText_SharedTokenPatterns_Hidden(string token)
    {
        // Те же шаблоны, что у маскирования в MCP (SecretPatterns): журналы и конфиг в пакете диагностики.
        var log = $"2026-09-23 12:00:00 INFO request failed for {token} (retry)";

        var masked = DiagnosticsBundle.MaskText(log, []);
        var config = DiagnosticsBundle.MaskConfig($$"""{ "extra": { "note": "{{token}}" }, "theme": "dark" }""", []);

        Assert.DoesNotContain(token, masked, StringComparison.Ordinal);
        Assert.Contains("INFO request failed for *** (retry)", masked, StringComparison.Ordinal);
        Assert.DoesNotContain(token, config, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"dark\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void HideProfile_ReplacesUserPathInAllForms()
    {
        var text = @"C:\Users\Ivan\AppData\x; C:/Users/Ivan/y; ""C:\\Users\\Ivan\\z""";
        var hidden = DiagnosticsBundle.HideProfile(text, @"C:\Users\Ivan");
        Assert.DoesNotContain("Ivan", hidden, StringComparison.Ordinal);
        // Другой пользователь с тем же началом имени не затрагивается.
        Assert.Equal(@"C:\Users\Ivanov\x", DiagnosticsBundle.HideProfile(@"C:\Users\Ivanov\x", @"C:\Users\Ivan"));
    }

    [Fact]
    public void Write_ZipContainsSummaryConfigAndLogsWithRotations_Masked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "offload-diag-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            File.WriteAllText(Path.Combine(logs, "app.log"), $"started --api-key {ApiKey}");
            File.WriteAllText(Path.Combine(logs, "app.log.1"), "old rotation");
            File.WriteAllText(Path.Combine(logs, "notes.txt"), "not a log");
            var zipPath = Path.Combine(dir, "out", "diag.zip");

            DiagnosticsBundle.Write(zipPath, "summary " + ApiKey, $"{{\"server\":{{\"apiKey\":\"{ApiKey}\"}}}}",
                DiagnosticsBundle.LogFiles(logs), [ApiKey]);

            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(["config.json", "logs/app.log", "logs/app.log.1", "summary.txt"], names);
            foreach (var e in zip.Entries)
            {
                using var r = new StreamReader(e.Open());
                Assert.DoesNotContain(ApiKey, r.ReadToEnd(), StringComparison.Ordinal);
            }
            Assert.False(File.Exists(zipPath + ".tmp"), "временный файл должен быть переименован");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildSummary_HasNoPromptContent_AndCountsErrors()
    {
        var cfg = new AppConfig();
        var usage = new List<UsageRecord>
        {
            new(DateTime.UtcNow, "local_ask_files", "claude-code", 100, 20, 1500, true),
            new(DateTime.UtcNow, "local_ask_files", "claude-code", 50, 0, 300, false),
        };
        var summary = DiagnosticsBundle.BuildSummary(cfg, null, null, [new DiagnosticsBundle.IntegrationLine("claude-code", "Claude Code", "Registered")], usage, DateTime.Now);
        Assert.Contains("local_ask_files: 2 calls, 1 failed", summary, StringComparison.Ordinal);
        Assert.Contains("claude-code (Claude Code): Registered", summary, StringComparison.Ordinal);
        Assert.Contains("[Hardware]", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void IssueUrl_UsesBugTemplateAndEscapes()
    {
        var url = DiagnosticsBundle.IssueUrl("1.2.3", "RTX 3090, 580.12", "Qwen 27B Q4_K_M, b1234 Cuda12");
        Assert.StartsWith("https://github.com/Kelll31/offload/issues/new?template=bug.yml&", url, StringComparison.Ordinal);
        Assert.Contains("version=1.2.3", url, StringComparison.Ordinal);
        Assert.Contains("gpu=RTX%203090%2C%20580.12", url, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", url, StringComparison.Ordinal);
    }
}

public sealed class DashboardTests
{
    [Fact]
    public void Csv_EscapesFieldsAndBlocksFormulas()
    {
        var records = new List<UsageRecord>
        {
            new(new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc), "local_verify", "=cmd|' /C calc'!A0", 1234, 56, 789, false, "Model, \"Q4\"", 42),
            new(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "local_ask_files", null, 1, 2, 3, true),
        };
        var lines = UsageCsv.Build(records).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("timestamp_utc,local_time,tool,client,model,", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("2026-09-01T10:00:00Z,", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",1,2,0,3,true", lines[1], StringComparison.Ordinal);
        Assert.Contains(",'=cmd|' /C calc'!A0,", lines[2], StringComparison.Ordinal);
        Assert.Contains(",\"Model, \"\"Q4\"\"\",1234,56,42,789,false", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorsByTool_CountsOnlyFailed()
    {
        var now = DateTime.UtcNow;
        var map = UsageStats.ErrorsByTool(
        [
            new(now, "a", null, 0, 0, 0, false),
            new(now, "a", null, 0, 0, 0, false),
            new(now, "a", null, 0, 0, 0, true),
            new(now, "b", null, 0, 0, 0, true),
        ]);
        Assert.Single(map);
        Assert.Equal(2, map["a"]);
    }

    [Fact]
    public void PeriodOptions_ContainDefault()
    {
        Assert.Equal([7, 14, 30, 90], Forms.Pages.StatusPage.PeriodOptions);
    }

    [Theory]
    [InlineData("claude-code", "Claude Code")]
    [InlineData(null, "IDE")]
    [InlineData("(unknown)", "IDE")]
    [InlineData("cursor-vscode", "Cursor")]
    [InlineData("zed", "zed")]
    public void DisplayClient_KnownNames(string? client, string expected)
    {
        Assert.Equal(expected, OnboardingCard.DisplayClient(client));
    }
}

public sealed class UiAndThemeTests
{
    [Theory]
    [InlineData("system", "default", true, "contrast")]
    [InlineData(null, "purple", true, "contrast")]
    [InlineData("system", "purple", false, "purple")]
    [InlineData("light", "default", true, "default")]
    [InlineData("dark", "teal", true, "teal")]
    public void EffectivePreset_HighContrastOnlyForSystemTheme(string? mode, string preset, bool highContrast, string expected)
    {
        Assert.Equal(expected, Theme.EffectivePreset(mode, preset, highContrast));
    }

    [Fact]
    public void ErrorReport_ContainsTitleDetailsAndException()
    {
        var ex = new InvalidOperationException("boom");
        var text = Ui.ErrorReport("Title", "Details", ex);
        Assert.Contains("Title", text, StringComparison.Ordinal);
        Assert.Contains("Details", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: boom", text, StringComparison.Ordinal);
        Assert.Contains("Offload ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PaintedControls_ExposeAccessibleText()
    {
        using var tile = new StatTile("Вызовов сегодня", Glyphs.Chat);
        tile.Set("12", "за 7 дней: 40");
        Assert.Equal("Вызовов сегодня: 12", tile.AccessibleName);
        Assert.Equal("за 7 дней: 40", tile.AccessibleDescription);
        Assert.Equal(AccessibleRole.StaticText, tile.AccessibleRole);

        using var list = new BarList();
        list.SetData([new BarList.Row("local_ask_files", 5, "5"), new BarList.Row("local_verify", 2, "2")]);
        Assert.Equal(2, list.AccessibilityObject.GetChildCount());
        Assert.Equal("local_ask_files: 5", list.AccessibilityObject.GetChild(0)?.Name);

        using var chart = new BarChart();
        chart.SetData([new BarChart.Bar("1", 10, "1 сентября: 10"), new BarChart.Bar("2", 0, "2 сентября: 0")]);
        Assert.Equal(2, chart.AccessibilityObject.GetChildCount());
        Assert.Equal("1 сентября: 10", chart.AccessibilityObject.GetChild(0)?.Name);
        Assert.Equal(AccessibleRole.Chart, chart.AccessibleRole);
    }
}
