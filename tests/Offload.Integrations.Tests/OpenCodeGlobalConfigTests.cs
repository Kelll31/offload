using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Tests;

public class OpenCodeGlobalConfigTests
{
    private const string Schema = "https://opencode.ai/config.json";

    private static JsonObject Provider(string key = "k-1") => new()
    {
        ["npm"] = "@ai-sdk/openai-compatible",
        ["options"] = new JsonObject { ["baseURL"] = "http://127.0.0.1:8765/v1", ["apiKey"] = key },
        ["models"] = new JsonObject { ["local-coder"] = new JsonObject { ["id"] = "offload" } },
    };

    private static JsonNode ParseJsonc(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;

    private const string UserJsonc = """
        {
          // мои настройки OpenCode
          "theme": "tokyonight", /* тема */
          "model": "anthropic/claude-sonnet-4",
          "provider": {
            // локальная ollama
            "ollama": { "npm": "@ai-sdk/openai-compatible", },
          },
          "mcp": { "files": { "type": "local", "command": ["npx", "x"] } },
        }

        """;

    [Fact]
    public void Paths_AreInsideSandbox()
    {
        using var sb = new Sandbox();
        Assert.Equal(sb.P(".config", "opencode"), OpenCodeGlobalConfig.ConfigDir);
        Assert.Equal(sb.P(".config", "opencode", "opencode.json"), OpenCodeGlobalConfig.TargetFile);
        sb.Write(sb.P(".config", "opencode", "opencode.jsonc"), "{}");
        Assert.Equal(sb.P(".config", "opencode", "opencode.jsonc"), OpenCodeGlobalConfig.TargetFile);
    }

    [Fact]
    public void Register_NewFile_HasSchemaAndProvider()
    {
        using var sb = new Sandbox();
        var r = OpenCodeGlobalConfig.RegisterProvider("offload", Provider(), Schema);
        Assert.True(r.Ok, r.Message);
        var root = JsonNode.Parse(File.ReadAllText(sb.P(".config", "opencode", "opencode.json")))!;
        Assert.Equal(Schema, (string?)root["$schema"]);
        Assert.Equal("k-1", (string?)root["provider"]!["offload"]!["options"]!["apiKey"]);
        Assert.Null(root["model"]);
    }

    [Fact]
    public void Register_KeepsCommentsAndForeignKeys_ThenUnregisterRestoresText()
    {
        using var sb = new Sandbox();
        var path = sb.Write(sb.P(".config", "opencode", "opencode.jsonc"), UserJsonc);

        var r = OpenCodeGlobalConfig.RegisterProvider("offload", Provider(), Schema);
        Assert.True(r.Ok, r.Message);
        Assert.NotNull(r.BackupPath);
        Assert.Equal(UserJsonc, File.ReadAllText(r.BackupPath!));
        var text = File.ReadAllText(path);
        foreach (var line in UserJsonc.Split('\n').Where(l => l.Contains("//") || l.Contains("/*") || l.Contains("\"mcp\"")))
            Assert.Contains(line, text);
        var root = ParseJsonc(text);
        Assert.Equal("anthropic/claude-sonnet-4", (string?)root["model"]);
        Assert.NotNull(root["provider"]!["ollama"]);
        Assert.NotNull(root["provider"]!["offload"]);
        Assert.Null(root["$schema"]);

        // Повтор — файл не переписывается.
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.True(OpenCodeGlobalConfig.RegisterProvider("offload", Provider(), Schema).Ok);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

        // Новый ключ — меняется только наш провайдер.
        Assert.True(OpenCodeGlobalConfig.RegisterProvider("offload", Provider("k-2"), Schema).Ok);
        Assert.Equal("k-2", (string?)ParseJsonc(File.ReadAllText(path))["provider"]!["offload"]!["options"]!["apiKey"]);

        var u = OpenCodeGlobalConfig.UnregisterProvider("offload");
        Assert.True(u.Ok, u.Message);
        Assert.Equal(UserJsonc, File.ReadAllText(path));
    }

    [Fact]
    public void Unregister_RemovesOnlyOurs()
    {
        using var sb = new Sandbox();
        var path = sb.Write(sb.P(".config", "opencode", "opencode.json"), """
            {
              "model": "offload/local-coder",
              "small_model": "anthropic/claude-haiku",
              "enabled_providers": ["anthropic", "offload"],
              "provider": {
                "anthropic": { "options": { "apiKey": "sk-user" } },
                "offload": { "npm": "x" }
              },
              "theme": "x"
            }
            """);
        // Устаревший файл с нашим провайдером тоже чистится; единственный провайдер — «provider» удаляется целиком.
        var legacy = sb.Write(sb.P(".config", "opencode", "config.json"), "{ \"provider\": { \"offload\": {} }, \"theme\": \"y\" }");

        var r = OpenCodeGlobalConfig.UnregisterProvider("offload");
        Assert.True(r.Ok, r.Message);
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Null(root["model"]);
        Assert.Equal("anthropic/claude-haiku", (string?)root["small_model"]);
        Assert.Equal(["anthropic"], root["enabled_providers"]!.AsArray().Select(n => (string?)n));
        Assert.Null(root["provider"]!["offload"]);
        Assert.Equal("sk-user", (string?)root["provider"]!["anthropic"]!["options"]!["apiKey"]);
        Assert.Equal("x", (string?)root["theme"]);
        var legacyRoot = JsonNode.Parse(File.ReadAllText(legacy))!;
        Assert.Null(legacyRoot["provider"]);
        Assert.Equal("y", (string?)legacyRoot["theme"]);

        // Нечего удалять — файлы не переписываются.
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.True(OpenCodeGlobalConfig.UnregisterProvider("offload").Ok);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Register_AppendsToExistingEnabledProvidersOnce()
    {
        using var sb = new Sandbox();
        var path = sb.Write(sb.P(".config", "opencode", "opencode.json"), "{\n  \"enabled_providers\": [\"anthropic\"]\n}\n");
        Assert.True(OpenCodeGlobalConfig.RegisterProvider("offload", Provider(), Schema).Ok);
        Assert.True(OpenCodeGlobalConfig.RegisterProvider("offload", Provider(), Schema).Ok);
        Assert.Equal(["anthropic", "offload"], JsonNode.Parse(File.ReadAllText(path))!["enabled_providers"]!.AsArray().Select(n => (string?)n));
    }

    [Theory]
    [InlineData("{ \"theme\": ")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"provider\": [\"offload\"] }")]
    public void Register_BadFile_FailsAndKeepsFile(string content)
    {
        using var sb = new Sandbox();
        var path = sb.Write(sb.P(".config", "opencode", "opencode.json"), content);
        var r = OpenCodeGlobalConfig.RegisterProvider("offload", Provider(), Schema);
        Assert.False(r.Ok);
        Assert.Contains(path, r.Message);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Unregister_CorruptFile_IsSkipped()
    {
        using var sb = new Sandbox();
        var path = sb.Write(sb.P(".config", "opencode", "opencode.json"), "{ \"provider\": { \"offload\": ");
        var r = OpenCodeGlobalConfig.UnregisterProvider("offload");
        Assert.True(r.Ok, r.Message);
        Assert.Contains(path, r.Message);
        Assert.Equal("{ \"provider\": { \"offload\": ", File.ReadAllText(path));
    }

    [Fact]
    public void Unregister_NoDirectory_IsNoop()
    {
        using var sb = new Sandbox();
        Assert.True(OpenCodeGlobalConfig.UnregisterProvider("offload").Ok);
        Assert.False(Directory.Exists(sb.P(".config", "opencode")));
    }

    [Fact]
    public void XdgConfigHome_FromSandboxVariables()
    {
        var xdg = Path.Combine(Path.GetTempPath(), TestSetup.SandboxPrefix + "xdg-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sb = new Sandbox(new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = xdg });
            Assert.True(OpenCodeGlobalConfig.RegisterProvider("offload", Provider(), Schema).Ok);
            Assert.True(File.Exists(Path.Combine(xdg, "opencode", "opencode.json")));
            Assert.False(Directory.Exists(sb.P(".config", "opencode")));
        }
        finally
        {
            try { Directory.Delete(xdg, true); } catch { }
        }
    }

    [Fact]
    public void Editing_UsesConfigFileRules()
    {
        // UTF-16 — ConfigReadException, файл не трогаем (общее правило ConfigFile).
        using var sb = new Sandbox();
        var path = sb.P(".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}", System.Text.Encoding.Unicode);
        var before = File.ReadAllBytes(path);
        Assert.False(OpenCodeGlobalConfig.RegisterProvider("offload", Provider(), Schema).Ok);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Throws<ConfigReadException>(() => ConfigFile.Read(path));
    }
}
