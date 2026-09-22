using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Core.Config;

namespace Offload.OpenCode.Tests;

internal static class TestConfig
{
    public const string Key = "pc-secret-test-key-0123456789";

    public static AppConfig Make(bool allowShell = false, int contextSize = 0, bool withModel = true)
    {
        var cfg = new AppConfig();
        cfg.Server.ApiKey = Key;
        cfg.Server.Port = 8799;
        cfg.Server.ContextSize = contextSize;
        cfg.OpenCode.AllowShellCommands = allowShell;
        if (withModel)
        {
            cfg.Models.Installed.Add(new InstalledModel
            {
                Id = "qwen3-coder",
                DisplayName = "Qwen3-Coder 30B",
                RecommendedContext = 65536,
                Reasoning = ReasoningControl.EnableThinkingKwarg,
                Sampling = new SamplingSettings { Temperature = 0.7, TopP = 0.8, TopK = 20, MinP = 0, RepeatPenalty = 1.05 },
            });
            cfg.Models.ActiveModelId = "qwen3-coder";
        }
        return cfg;
    }

    public static JsonObject ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject();
}

[Collection("AppPaths")]
public class ConfigWriterTests
{
    [Fact]
    public void ManagedConfig_HasExpectedShape()
    {
        using var home = new TempHome();
        var path = OpenCodeConfigWriter.WriteManagedConfig(TestConfig.Make());
        Assert.Equal(AppPaths.OpenCodeConfigFile, path);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain(TestConfig.Key, text); // ключ — только через переменную окружения
        var root = TestConfig.ReadJson(path);

        Assert.Equal("https://opencode.ai/config.json", (string?)root["$schema"]);
        Assert.False((bool)root["autoupdate"]!);
        Assert.Equal("disabled", (string?)root["share"]);
        Assert.False((bool)root["snapshot"]!);
        Assert.Equal(new[] { "offload" }, root["enabled_providers"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("offload/local-coder", (string?)root["model"]);
        Assert.Equal("offload", (string?)root["default_agent"]);

        var provider = root["provider"]!["offload"]!;
        Assert.Equal("@ai-sdk/openai-compatible", (string?)provider["npm"]);
        Assert.Equal("http://127.0.0.1:8799/v1", (string?)provider["options"]!["baseURL"]);
        Assert.Equal("{env:OFFLOAD_API_KEY}", (string?)provider["options"]!["apiKey"]);
        Assert.Equal(900000, (int)provider["options"]!["timeout"]!);
        Assert.Equal(900000, (int)provider["options"]!["headerTimeout"]!);
        Assert.Equal(300000, (int)provider["options"]!["chunkTimeout"]!);

        var model = provider["models"]!["local-coder"]!;
        Assert.Equal("offload", (string?)model["id"]);
        Assert.Equal("Qwen3-Coder 30B (Offload)", (string?)model["name"]);
        Assert.True((bool)model["temperature"]!);
        Assert.True((bool)model["tool_call"]!);
        Assert.Equal(65536, (int)model["limit"]!["context"]!);
        Assert.Equal(8192, (int)model["limit"]!["output"]!);
        Assert.Equal(20, (int)model["options"]!["top_k"]!);
        Assert.Equal(1.05, (double)model["options"]!["repeat_penalty"]!);
        Assert.False((bool)model["options"]!["chat_template_kwargs"]!["enable_thinking"]!);

        var agents = root["agent"]!.AsObject();
        Assert.True((bool)agents["title"]!["disable"]!);
        Assert.Equal("primary", (string?)agents["offload"]!["mode"]);
        Assert.Equal("primary", (string?)agents["offload-readonly"]!["mode"]);
        Assert.Equal(40, (int)agents["offload"]!["steps"]!);
        Assert.Equal(0.7, (double)agents["offload"]!["temperature"]!);
        Assert.Contains("read before you edit", (string?)agents["offload"]!["prompt"]);
        Assert.Contains("cannot modify files", (string?)agents["offload-readonly"]!["prompt"]);
    }

    [Fact]
    public void ManagedConfig_ContextPriority()
    {
        using var home = new TempHome();
        Assert.Equal(16384, Context(OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make(contextSize: 16384))));
        Assert.Equal(4096, Output(OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make(contextSize: 16384))));
        Assert.Equal(65536, Context(OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make())));
        Assert.Equal(32768, Context(OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make(withModel: false))));
        // Фактический контекст работающего сервера (/props) главнее настроек.
        Assert.Equal(12288, Context(OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make(), 12288)));
        Assert.Equal(3072, Output(OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make(), 12288)));

        static JsonNode Limit(JsonObject root) => root["provider"]!["offload"]!["models"]!["local-coder"]!["limit"]!;
        static int Context(JsonObject root) => (int)Limit(root)["context"]!;
        static int Output(JsonObject root) => (int)Limit(root)["output"]!;
    }

    [Fact]
    public void Permissions_ShellDenied()
    {
        using var home = new TempHome();
        var root = OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make(allowShell: false));
        var edit = root["agent"]!["offload"]!["permission"]!.AsObject();
        Assert.Equal("*", edit.First().Key); // «*: allow» первым — перекрывает умолчания с «ask»
        Assert.Equal("allow", (string?)edit["*"]);
        Assert.Equal("deny", (string?)edit["bash"]);
        Assert.Equal("allow", (string?)edit["edit"]!["*"]);
        Assert.Equal("deny", (string?)edit["external_directory"]);
        foreach (var tool in new[] { "webfetch", "websearch", "task", "todowrite", "skill", "question", "doom_loop" })
            Assert.Equal("deny", (string?)edit[tool]);
        Assert.DoesNotContain("PowerShell", (string?)root["agent"]!["offload"]!["prompt"]);

        // Секреты: шаблон без «*» — и в корне, и во вложенных папках.
        var read = edit["read"]!.AsObject();
        Assert.Equal("allow", (string?)read["*"]);
        Assert.Equal("deny", (string?)read[".env"]);
        Assert.Equal("deny", (string?)read["*/.env"]);
        Assert.Equal("deny", (string?)read["*.pem"]);
        Assert.False(read.ContainsKey("*/*.pem"));

        var ro = root["agent"]!["offload-readonly"]!["permission"]!.AsObject();
        Assert.Equal("deny", (string?)ro["edit"]);
        Assert.Equal("deny", (string?)ro["bash"]);
        Assert.Equal(25, (int)root["agent"]!["offload-readonly"]!["steps"]!);
    }

    [Fact]
    public void Permissions_ShellAllowed_DangerousDenied()
    {
        using var home = new TempHome();
        var root = OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make(allowShell: true));
        var bash = root["agent"]!["offload"]!["permission"]!["bash"]!.AsObject();
        Assert.Equal("*", bash.First().Key);
        Assert.Equal("allow", (string?)bash["*"]);
        foreach (var p in new[] { "git push*", "git commit*", "git reset --hard*", "rm -rf*", "*Remove-Item*-Recurse*", "*del /s*", "format *" })
            Assert.Equal("deny", (string?)bash[p]);
        Assert.Contains("PowerShell", (string?)root["agent"]!["offload"]!["prompt"]);
        // Агент только для чтения оболочку не получает никогда.
        Assert.Equal("deny", (string?)root["agent"]!["offload-readonly"]!["permission"]!["bash"]);
    }

    [Fact]
    public void ManagedConfig_MatchesSchemaTypes()
    {
        using var home = new TempHome();
        foreach (var shell in new[] { false, true })
        {
            var root = OpenCodeConfigWriter.BuildManagedConfig(TestConfig.Make(allowShell: shell));
            var known = new HashSet<string>
            {
                "$schema", "autoupdate", "share", "snapshot", "enabled_providers", "model", "small_model",
                "default_agent", "provider", "agent", "tool_output", "experimental",
            };
            Assert.All(root.Select(p => p.Key), k => Assert.Contains(k, known));

            // Эти разрешения по схеме — только строки (PermissionActionConfig).
            foreach (var agent in new[] { "offload", "offload-readonly" })
            {
                var perm = root["agent"]![agent]!["permission"]!.AsObject();
                foreach (var key in new[] { "todowrite", "question", "webfetch", "websearch", "doom_loop" })
                    Assert.Equal(JsonValueKind.String, perm[key]!.GetValueKind());
                foreach (var (_, value) in perm)
                {
                    if (value is JsonObject rules)
                        Assert.All(rules, r => Assert.Contains((string?)r.Value, new[] { "allow", "deny", "ask" }));
                    else
                        Assert.Contains((string?)value, new[] { "allow", "deny", "ask" });
                }
            }
            var limit = root["provider"]!["offload"]!["models"]!["local-coder"]!["limit"]!.AsObject();
            Assert.True(limit.ContainsKey("context") && limit.ContainsKey("output"));
            Assert.True((int)root["tool_output"]!["max_bytes"]! > 0);
        }
    }

    [Fact]
    public void WriteManagedConfig_SkipsIdenticalRewrite()
    {
        using var home = new TempHome();
        var cfg = TestConfig.Make();
        var path = OpenCodeConfigWriter.WriteManagedConfig(cfg);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        OpenCodeConfigWriter.WriteManagedConfig(cfg);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        cfg.Server.Port = 9001;
        OpenCodeConfigWriter.WriteManagedConfig(cfg);
        Assert.Contains("http://127.0.0.1:9001/v1", File.ReadAllText(path));
    }

    [Fact]
    public void BaseUrl_WildcardHostBecomesLoopback()
    {
        var cfg = TestConfig.Make();
        cfg.Server.Host = "0.0.0.0";
        Assert.Equal("http://127.0.0.1:8799", LocalServer.ClientBaseUrl(cfg.Server));
        cfg.Server.Host = "::1";
        Assert.Equal("http://[::1]:8799", LocalServer.ClientBaseUrl(cfg.Server));
    }

    [Fact]
    public void ExtraPrompt_IsAppendedAndSanitized()
    {
        using var home = new TempHome();
        var cfg = TestConfig.Make();
        cfg.Mcp.ExtraSystemPrompt = "Пиши на Delphi. Не трогай {env:HOME} и {file:./x.md}.";
        var prompt = (string?)OpenCodeConfigWriter.BuildManagedConfig(cfg)["agent"]!["offload"]!["prompt"];
        Assert.Contains("Пиши на Delphi", prompt);
        Assert.DoesNotContain("{env:", prompt);
        Assert.DoesNotContain("{file:", prompt);
    }

    [Fact]
    public void ShellOverride_IsValidPartialConfig()
    {
        var cfg = TestConfig.Make(allowShell: false);
        var allow = JsonNode.Parse(OpenCodeConfigWriter.ShellOverrideContent(cfg, allowShell: true))!;
        Assert.Equal("https://opencode.ai/config.json", (string?)allow["$schema"]);
        Assert.Equal("allow", (string?)allow["agent"]!["offload"]!["permission"]!["bash"]!["*"]);
        Assert.Contains("PowerShell", (string?)allow["agent"]!["offload"]!["prompt"]);
        var deny = JsonNode.Parse(OpenCodeConfigWriter.ShellOverrideContent(cfg, allowShell: false))!;
        Assert.Equal("deny", (string?)deny["agent"]!["offload"]!["permission"]!["bash"]);
    }

    [Fact]
    public void Environment_IsIsolated()
    {
        using var home = new TempHome();
        var env = OpenCodeConfigWriter.Environment(TestConfig.Make());
        Assert.Equal(AppPaths.OpenCodeConfigFile, env["OPENCODE_CONFIG"]);
        foreach (var flag in new[]
                 {
                     "OPENCODE_DISABLE_AUTOUPDATE", "OPENCODE_DISABLE_MODELS_FETCH", "OPENCODE_DISABLE_SHARE",
                     "OPENCODE_DISABLE_LSP_DOWNLOAD", "OPENCODE_DISABLE_DEFAULT_PLUGINS", "OPENCODE_DISABLE_EXTERNAL_SKILLS",
                     "OPENCODE_DISABLE_CLAUDE_CODE", "OPENCODE_PURE",
                 })
            Assert.Equal("1", env[flag]);
        Assert.Equal(TestConfig.Key, env["OFFLOAD_API_KEY"]);
        Assert.Equal(Path.Combine(AppPaths.OpenCodeDir, "config"), env["XDG_CONFIG_HOME"]);
        Assert.Equal(Path.Combine(AppPaths.OpenCodeDir, "data"), env["XDG_DATA_HOME"]);
        Assert.Equal(Path.Combine(AppPaths.OpenCodeDir, "state"), env["XDG_STATE_HOME"]);
        Assert.Equal(Path.Combine(AppPaths.OpenCodeDir, "cache"), env["XDG_CACHE_HOME"]);
        Assert.StartsWith(home.Path, env["XDG_CONFIG_HOME"]);
        Assert.True(env.ContainsKey("OPENCODE_CONFIG_DIR"));
        Assert.Null(env["OPENCODE_CONFIG_DIR"]); // унаследованная переменная удаляется
        Assert.Null(env["OPENCODE_PERMISSION"]);
        Assert.Contains("127.0.0.1", env["NO_PROXY"]);

        var tui = OpenCodeConfigWriter.BuildEnvironment(TestConfig.Make(), interactive: true);
        Assert.Equal(Path.Combine(AppPaths.OpenCodeDir, "data-tui"), tui["XDG_DATA_HOME"]);
        Assert.Equal(env["XDG_CACHE_HOME"], tui["XDG_CACHE_HOME"]);
    }

    [Fact]
    public void RegisterGlobal_CreatesFileWithLiteralKey()
    {
        using var home = new TempHome();
        OpenCodeConfigWriter.RegisterGlobal(TestConfig.Make());
        var path = Path.Combine(home.GlobalOpenCodeDir, "opencode.json");
        var root = TestConfig.ReadJson(path);
        Assert.Equal("https://opencode.ai/config.json", (string?)root["$schema"]);
        var provider = root["provider"]!["offload"]!;
        // У пользователя нет переменной OFFLOAD_API_KEY — ключ записан как есть.
        Assert.Equal(TestConfig.Key, (string?)provider["options"]!["apiKey"]);
        Assert.Equal(65536, (int)provider["models"]!["local-coder"]!["limit"]!["context"]!);
        Assert.Null(root["agent"]); // агенты Offload — только в управляемом конфиге
        Assert.Null(root["model"]); // модель по умолчанию пользователя не меняем
    }

    [Fact]
    public void RegisterGlobal_MergesAndPreservesUserData()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(home.GlobalOpenCodeDir);
        var path = Path.Combine(home.GlobalOpenCodeDir, "opencode.json");
        File.WriteAllText(path, """
            {
              "$schema": "https://opencode.ai/config.json",
              "theme": "tokyonight",
              "model": "anthropic/claude-sonnet-4",
              "enabled_providers": ["anthropic"],
              "provider": { "anthropic": { "options": { "apiKey": "sk-user" } } },
              "mcp": { "files": { "type": "local", "command": ["npx", "x"] } },
              "number": 1.50
            }
            """);

        OpenCodeConfigWriter.RegisterGlobal(TestConfig.Make());
        var root = TestConfig.ReadJson(path);
        Assert.Equal("tokyonight", (string?)root["theme"]);
        Assert.Equal("anthropic/claude-sonnet-4", (string?)root["model"]);
        Assert.Equal("sk-user", (string?)root["provider"]!["anthropic"]!["options"]!["apiKey"]);
        Assert.Equal("npx", (string?)root["mcp"]!["files"]!["command"]![0]);
        Assert.Equal(new[] { "anthropic", "offload" }, root["enabled_providers"]!.AsArray().Select(n => (string?)n));
        Assert.NotNull(root["provider"]!["offload"]);
        Assert.Contains("1.50", File.ReadAllText(path)); // числа сохраняются как были
        Assert.NotEmpty(Directory.GetFiles(AppPaths.BackupsDir)); // резервная копия

        // Повторная регистрация не дублирует провайдера в enabled_providers.
        OpenCodeConfigWriter.RegisterGlobal(TestConfig.Make());
        Assert.Equal(2, TestConfig.ReadJson(path)["enabled_providers"]!.AsArray().Count);
    }

    [Fact]
    public void RegisterGlobal_PrefersJsoncAndRewritesComments()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(home.GlobalOpenCodeDir);
        var jsonc = Path.Combine(home.GlobalOpenCodeDir, "opencode.jsonc");
        const string original = """
            {
              // мой конфиг
              "theme": "opencode", /* блок */
              "provider": {
                "ollama": { "npm": "@ai-sdk/openai-compatible", },
              },
            }
            """;
        File.WriteAllText(jsonc, original);

        OpenCodeConfigWriter.RegisterGlobal(TestConfig.Make());
        Assert.False(File.Exists(Path.Combine(home.GlobalOpenCodeDir, "opencode.json")));
        var text = File.ReadAllText(jsonc);
        var root = JsonNode.Parse(text)!.AsObject(); // теперь строгий JSON
        Assert.Equal("opencode", (string?)root["theme"]);
        Assert.NotNull(root["provider"]!["ollama"]);
        Assert.NotNull(root["provider"]!["offload"]);
        Assert.Equal("$schema", root.First().Key);
        var backup = Assert.Single(Directory.GetFiles(AppPaths.BackupsDir));
        Assert.Equal(original, File.ReadAllText(backup)); // комментарии сохранены в копии
    }

    [Fact]
    public void RegisterGlobal_CorruptFile_ThrowsAndKeepsFile()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(home.GlobalOpenCodeDir);
        var path = Path.Combine(home.GlobalOpenCodeDir, "opencode.json");
        File.WriteAllText(path, "{ \"theme\": ");
        var ex = Assert.Throws<InvalidOperationException>(() => OpenCodeConfigWriter.RegisterGlobal(TestConfig.Make()));
        Assert.Contains("Не удалось разобрать", ex.Message);
        Assert.Equal("{ \"theme\": ", File.ReadAllText(path));
    }

    [Fact]
    public void UnregisterGlobal_RemovesOnlyOurEntries()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(home.GlobalOpenCodeDir);
        var path = Path.Combine(home.GlobalOpenCodeDir, "opencode.json");
        File.WriteAllText(path, """
            {
              "theme": "x",
              "model": "anthropic/claude",
              "enabled_providers": ["anthropic"],
              "provider": { "anthropic": { "options": { "apiKey": "sk" } } }
            }
            """);
        OpenCodeConfigWriter.RegisterGlobal(TestConfig.Make());
        // Пользователь сам выбрал нашу модель по умолчанию.
        var root = TestConfig.ReadJson(path);
        root["small_model"] = "offload/local-coder";
        File.WriteAllText(path, root.ToJsonString());

        OpenCodeConfigWriter.UnregisterGlobal();
        root = TestConfig.ReadJson(path);
        Assert.Null(root["provider"]!["offload"]);
        Assert.NotNull(root["provider"]!["anthropic"]);
        Assert.Equal("anthropic/claude", (string?)root["model"]);
        Assert.Null(root["small_model"]);
        Assert.Equal(new[] { "anthropic" }, root["enabled_providers"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("x", (string?)root["theme"]);

        // Нечего удалять — файл не переписывается.
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        OpenCodeConfigWriter.UnregisterGlobal();
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void UnregisterGlobal_DropsEmptyProviderAndIgnoresMissingDir()
    {
        using var home = new TempHome();
        OpenCodeConfigWriter.UnregisterGlobal(); // папки нет — не ошибка
        OpenCodeConfigWriter.RegisterGlobal(TestConfig.Make());
        OpenCodeConfigWriter.UnregisterGlobal();
        var root = TestConfig.ReadJson(Path.Combine(home.GlobalOpenCodeDir, "opencode.json"));
        Assert.Null(root["provider"]);
        Assert.Equal("https://opencode.ai/config.json", (string?)root["$schema"]);
    }

    [Fact]
    public void GlobalDir_CanBeOverriddenByEnv()
    {
        using var home = new TempHome();
        OpenCodeConfigWriter.GlobalConfigDirOverride = null;
        var dir = Path.Combine(home.Path, "env-global");
        Environment.SetEnvironmentVariable(OpenCodeConfigWriter.GlobalDirEnvVar, dir);
        try
        {
            Assert.Equal(dir, OpenCodeConfigWriter.GlobalConfigDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenCodeConfigWriter.GlobalDirEnvVar, null);
        }
    }
}
