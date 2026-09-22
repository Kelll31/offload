using System.Text;
using System.Text.Json.Nodes;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Tests;

public class IntegrationLifecycleTests
{
    /// <summary>Подготовка песочницы: «устанавливает» клиента и кладёт реалистичный исходный файл. Возвращает путь к файлу-образцу (или null).</summary>
    private static string? Arrange(Sandbox sb, string id)
    {
        switch (id)
        {
            case "claude-code":
                sb.Dir(".claude");
                return sb.Write(sb.P(".claude.json"), Samples.ClaudeJson);
            case "claude-desktop":
                return sb.Write(sb.P("AppData", "Local", "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"), Samples.DesktopConfig);
            case "cursor":
                return sb.Write(sb.P(".cursor", "mcp.json"), Samples.CursorJson);
            case "vscode":
                return sb.Write(sb.P("AppData", "Roaming", "Code", "User", "mcp.json"), Samples.VsCodeMcp);
            case "vscode-insiders":
                return sb.Write(sb.P("AppData", "Roaming", "Code - Insiders", "User", "mcp.json"), Samples.VsCodeMcp.Replace("\n", "\r\n"));
            case "copilot-cli":
                sb.Dir(".copilot");
                return null;
            case "windsurf":
                sb.Dir("AppData", "Roaming", "devin");
                return sb.Write(sb.P(".codeium", "windsurf", "mcp_config.json"), Samples.CursorJson);
            case "cline":
                return sb.Write(sb.P(".cline", "data", "settings", "cline_mcp_settings.json"), "{\n  \"mcpServers\": {}\n}");
            case "roo-code":
                sb.Dir("AppData", "Roaming", "Code", "User", "globalStorage", "rooveterinaryinc.roo-cline");
                return null;
            case "kilo-code":
                return sb.Write(sb.P(".config", "kilo", "kilo.jsonc"), Samples.KiloJsonc);
            case "codex":
                return sb.Write(sb.P(".codex", "config.toml"), TomlPatcherTests.CodexSample);
            case "gemini-cli":
                return sb.Write(sb.P(".gemini", "settings.json"), Samples.GeminiSettings);
            case "zed":
                return sb.Write(sb.P("AppData", "Roaming", "Zed", "settings.json"), Samples.ZedSettings);
            case "visual-studio":
                sb.Write(sb.P("ProgramFiles", "Microsoft Visual Studio", "18", "Community", "Common7", "IDE", "devenv.exe"), "");
                return null;
            case "junie":
                sb.Dir(".junie");
                return null;
            case "continue":
                sb.Write(sb.P(".continue", "config.yaml"), "name: Local\nversion: 1.0.0\nschema: v1\n");
                return null;
            case "kiro":
                sb.Dir(".kiro");
                return null;
            case "trae":
                sb.Dir("AppData", "Roaming", "Trae");
                return null;
            case "qoder":
                sb.Dir(".qoder");
                return null;
            default:
                throw new ArgumentException(id);
        }
    }

    private static IEnumerable<string> WritableIdList => IntegrationRegistry.All.Select(i => i.Id).Where(i => i != "jetbrains-ai");

    public static TheoryData<string> WritableIds => new(WritableIdList);

    [Fact]
    public void Registry_OrderAndIds()
    {
        var ids = IntegrationRegistry.All.Select(i => i.Id).ToList();
        Assert.Equal("claude-code", ids[0]);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var expected in new[] { "claude-desktop", "cursor", "vscode", "vscode-insiders", "copilot-cli", "windsurf", "cline", "roo-code", "kilo-code", "zed", "gemini-cli", "codex", "junie", "continue", "visual-studio", "kiro", "trae", "qoder", "jetbrains-ai" })
            Assert.Contains(expected, ids);
        Assert.Same(IntegrationRegistry.All[1], IntegrationRegistry.Find(ids[1]));
        Assert.Null(IntegrationRegistry.Find("nope"));
    }

    [Fact]
    public void EmptySandbox_NothingInstalled()
    {
        using var sb = new Sandbox();
        foreach (var i in IntegrationRegistry.All)
        {
            Assert.False(i.IsClientInstalled(), i.Id);
            Assert.Equal(IntegrationStatus.ClientNotFound, i.GetStatus(sb.Spec()));
        }
    }

    [Theory]
    [MemberData(nameof(WritableIds))]
    public async Task FullLifecycle(string id)
    {
        using var sb = new Sandbox();
        var sample = Arrange(sb, id);
        var original = sample is null ? null : File.ReadAllText(sample);
        var integration = IntegrationRegistry.Find(id)!;
        var spec = sb.Spec();

        Assert.True(integration.IsClientInstalled());
        Assert.StartsWith(sb.Root, integration.ConfigPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IntegrationStatus.NotRegistered, integration.GetStatus(spec));

        var r = await integration.RegisterAsync(spec);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(IntegrationStatus.Registered, integration.GetStatus(spec));
        var path = integration.ConfigPath!;
        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB, "BOM записан");
        Assert.Contains("Программы Offload", Encoding.UTF8.GetString(bytes));

        // Повторная регистрация ничего не меняет.
        var again = await integration.RegisterAsync(spec);
        Assert.True(again.Ok, again.Message);
        Assert.Contains("уже", again.Message);
        Assert.Equal(bytes, File.ReadAllBytes(path));

        // Программу «переместили» → запись устарела → обновление при старте.
        var moved = sb.Spec("Новая папка");
        Assert.Equal(IntegrationStatus.Outdated, integration.GetStatus(moved));
        var refreshed = await IntegrationRegistry.RefreshOutdatedAsync(moved);
        Assert.Equal([id], refreshed);
        Assert.Equal(IntegrationStatus.Registered, integration.GetStatus(moved));

        var un = await integration.UnregisterAsync();
        Assert.True(un.Ok, un.Message);
        Assert.Equal(IntegrationStatus.NotRegistered, integration.GetStatus(moved));
        var un2 = await integration.UnregisterAsync();
        Assert.True(un2.Ok, un2.Message);

        // Для файлов с уже существующим контейнером серверов правка обратима байт в байт.
        if (original is not null && id is "vscode" or "vscode-insiders" or "zed" or "codex" or "cursor" or "windsurf" or "kilo-code" or "gemini-cli" or "claude-desktop" or "cline")
            Assert.Equal(original, File.ReadAllText(sample!));
    }

    [Fact]
    public async Task Windsurf_WritesAllExistingLocations()
    {
        using var sb = new Sandbox();
        Arrange(sb, "windsurf");
        var w = IntegrationRegistry.Find("windsurf")!;
        Assert.True((await w.RegisterAsync(sb.Spec())).Ok);
        Assert.True(File.Exists(sb.P("AppData", "Roaming", "devin", "mcp_config.json")));
        Assert.Contains("offload", File.ReadAllText(sb.P(".codeium", "windsurf", "mcp_config.json")));
        // Удалили из одного файла вручную → частичная регистрация = «требует обновления».
        File.WriteAllText(sb.P("AppData", "Roaming", "devin", "mcp_config.json"), "{}");
        Assert.Equal(IntegrationStatus.Outdated, w.GetStatus(sb.Spec()));
    }

    [Fact]
    public async Task ClaudeCode_BigStateFile_ProjectsAndForeignKeysPreserved()
    {
        using var sb = new Sandbox();
        var path = Arrange(sb, "claude-code")!;
        var cc = IntegrationRegistry.Find("claude-code")!;
        var r = await cc.RegisterAsync(sb.Spec());
        Assert.True(r.Ok, r.Message);
        Assert.NotNull(r.BackupPath);
        Assert.StartsWith(Path.GetTempPath(), r.BackupPath!, StringComparison.OrdinalIgnoreCase);
        var text = File.ReadAllText(path);
        // Исходный текст — префикс результата до места вставки: всё прежнее содержимое сохранено дословно.
        var before = Samples.ClaudeJson.TrimEnd().TrimEnd('}').TrimEnd();
        Assert.StartsWith(before, text);
        var node = JsonNode.Parse(text)!;
        Assert.Equal("stdio", (string?)node["mcpServers"]!["offload"]!["type"]);
        // Проектная (local scope) запись с тем же именем не тронута.
        Assert.Equal("python", (string?)node["projects"]!["E:\\github\\auc"]!["mcpServers"]!["offload"]!["command"]);

        Assert.True((await cc.UnregisterAsync()).Ok);
        node = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Empty(node["mcpServers"]!.AsObject());
        Assert.Equal("python", (string?)node["projects"]!["E:\\github\\auc"]!["mcpServers"]!["offload"]!["command"]);
    }

    [Fact]
    public void ClaudeCode_ConfigDirVariable_RelocatesGlobalFile()
    {
        using var sb = new Sandbox(new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = Path.GetTempPath() + TestSetup.SandboxPrefix + "cfgdir" });
        var cc = IntegrationRegistry.Find("claude-code")!;
        Assert.Equal(Path.Combine(Path.GetTempPath() + TestSetup.SandboxPrefix + "cfgdir", ".claude.json"), cc.ConfigPath);
    }

    [Theory]
    [InlineData("vscode", "{ \"servers\": { \"a\": 1, } ")]
    [InlineData("zed", "{ \"context_servers\": { /* не закрыт ")]
    [InlineData("claude-desktop", "{\"mcpServers\": [\"x\"]}")]
    [InlineData("codex", "[mcp_servers.offload\ncommand = 'x'\n")]
    [InlineData("cursor", "{\"mcpServers\": {\"offload\": {\"command\": \"C:\\\\x\\\\Offload.exe\"}}} trailing")]
    public async Task Malformed_ReportsError_AndLeavesFileUntouched(string id, string content)
    {
        using var sb = new Sandbox();
        Arrange(sb, id);
        var integration = IntegrationRegistry.Find(id)!;
        var path = sb.Write(integration.ConfigPath!, content);
        var bytes = File.ReadAllBytes(path);

        Assert.Equal(IntegrationStatus.Error, integration.GetStatus(sb.Spec()));
        var r = await integration.RegisterAsync(sb.Spec());
        Assert.False(r.Ok);
        Assert.Contains("вручную", r.Message);
        var u = await integration.UnregisterAsync();
        Assert.False(u.Ok);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        // Не попадает в «отключить все» и не ломает его.
        await IntegrationRegistry.UnregisterAllAsync();
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Utf16File_IsError_AndUntouched()
    {
        using var sb = new Sandbox();
        Arrange(sb, "cursor");
        var cursor = IntegrationRegistry.Find("cursor")!;
        File.WriteAllText(cursor.ConfigPath!, "{\"mcpServers\":{}}", Encoding.Unicode);
        var bytes = File.ReadAllBytes(cursor.ConfigPath!);
        Assert.Equal(IntegrationStatus.Error, cursor.GetStatus(sb.Spec()));
        Assert.False((await cursor.RegisterAsync(sb.Spec())).Ok);
        Assert.Equal(bytes, File.ReadAllBytes(cursor.ConfigPath!));
    }

    [Fact]
    public async Task BomFile_IsRewrittenWithoutBom()
    {
        using var sb = new Sandbox();
        Arrange(sb, "cursor");
        var cursor = IntegrationRegistry.Find("cursor")!;
        File.WriteAllText(cursor.ConfigPath!, "{\n  \"mcpServers\": {}\n}\n", new UTF8Encoding(true));
        Assert.True((await cursor.RegisterAsync(sb.Spec())).Ok);
        var bytes = File.ReadAllBytes(cursor.ConfigPath!);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("codex")]
    [InlineData("continue")]
    [InlineData("claude-code")]
    public async Task ForeignEntry_IsNotRemoved_ButExplicitRegisterReplacesIt(string id)
    {
        using var sb = new Sandbox();
        Arrange(sb, id);
        var integration = IntegrationRegistry.Find(id)!;
        var path = integration.ConfigPath!;
        var content = id switch
        {
            "codex" => "[mcp_servers.offload]\ncommand = \"python\"\nargs = [\"server.py\"]\n",
            "continue" => "name: mine\nversion: 1\nschema: v1\nmcpServers:\n  - name: offload\n    command: python\n    args: [\"server.py\"]\n",
            _ => "{\n  \"mcpServers\": {\n    \"offload\": { \"command\": \"python\", \"args\": [\"server.py\"] },\n    \"url-one\": { \"url\": \"http://localhost:8000/sse\" }\n  }\n}\n",
        };
        sb.Write(path, content);

        Assert.Equal(IntegrationStatus.NotRegistered, integration.GetStatus(sb.Spec()));
        var u = await integration.UnregisterAsync();
        Assert.False(u.Ok);
        Assert.Contains("другую программу", u.Message);
        Assert.Equal(content, File.ReadAllText(path));
        await IntegrationRegistry.UnregisterAllAsync();
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Empty(await IntegrationRegistry.RefreshOutdatedAsync(sb.Spec()));
        Assert.Equal(content, File.ReadAllText(path));

        var r = await integration.RegisterAsync(sb.Spec());
        Assert.True(r.Ok, r.Message);
        Assert.Contains("заменена", r.Message);
        Assert.NotNull(r.BackupPath);
        Assert.Equal(content, File.ReadAllText(r.BackupPath!));
        Assert.Equal(IntegrationStatus.Registered, integration.GetStatus(sb.Spec()));
    }

    [Fact]
    public async Task UserTweaksInOurEntry_SurviveRefresh()
    {
        using var sb = new Sandbox();
        Arrange(sb, "cline");
        var cline = IntegrationRegistry.Find("cline")!;
        await cline.RegisterAsync(sb.Spec());
        var path = cline.ConfigPath!;
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var entry = node["mcpServers"]!["offload"]!;
        Assert.Equal(3600, (int)entry["timeout"]!);
        Assert.Equal("stdio", (string?)entry["type"]);
        Assert.Contains("local_ask_files", entry["autoApprove"]!.ToJsonString());
        Assert.DoesNotContain("local_edit_files", entry["autoApprove"]!.ToJsonString());
        entry["disabled"] = true;
        File.WriteAllText(path, node.ToJsonString());

        await IntegrationRegistry.RefreshOutdatedAsync(sb.Spec("moved"));
        entry = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["offload"]!;
        Assert.True((bool)entry["disabled"]!);
        Assert.Contains("moved", (string?)entry["command"]);
    }

    [Fact]
    public async Task EntryShapes_PerClient()
    {
        using var sb = new Sandbox();
        foreach (var id in WritableIdList) Arrange(sb, id);
        var spec = sb.Spec();
        foreach (var i in IntegrationRegistry.All.Where(i => i.Id != "jetbrains-ai")) Assert.True((await i.RegisterAsync(spec)).Ok, i.Id);

        JsonNode E(string id, params string[] container)
        {
            var n = JsonNode.Parse(File.ReadAllText(IntegrationRegistry.Find(id)!.ConfigPath!), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
            foreach (var c in container) n = n[c]!;
            return n["offload"]!;
        }

        Assert.Equal("stdio", (string?)E("vscode", "servers")["type"]);
        Assert.Equal("stdio", (string?)E("visual-studio", "servers")["type"]);
        Assert.DoesNotContain("mcpServers", File.ReadAllText(IntegrationRegistry.Find("visual-studio")!.ConfigPath!));
        Assert.Equal("local", (string?)E("copilot-cli", "mcpServers")["type"]);
        Assert.Equal("[\"*\"]", E("copilot-cli", "mcpServers")["tools"]!.ToJsonString());
        Assert.Equal(1800, (int)E("zed", "context_servers")["timeout"]!);
        Assert.Equal(1_800_000, (int)E("gemini-cli", "mcpServers")["timeout"]!);
        Assert.False((bool)E("gemini-cli", "mcpServers")["trust"]!);
        Assert.Equal(3600, (int)E("roo-code", "mcpServers")["timeout"]!);
        Assert.NotNull(E("roo-code", "mcpServers")["alwaysAllow"]);
        Assert.NotNull(E("kiro", "mcpServers")["autoApprove"]);
        var kilo = E("kilo-code", "mcp");
        Assert.Equal("local", (string?)kilo["type"]);
        Assert.Equal(2, kilo["command"]!.AsArray().Count);
        Assert.True((bool)kilo["enabled"]!);
        Assert.NotNull(E("junie", "mcpServers")["env"]);
        Assert.Equal("{}", E("claude-desktop", "mcpServers")["env"]!.ToJsonString());
        Assert.Contains("Claude_pzs8sxrjxfjjc", IntegrationRegistry.Find("claude-desktop")!.ConfigPath);

        var yaml = File.ReadAllText(IntegrationRegistry.Find("continue")!.ConfigPath!);
        Assert.Contains("command: '" + spec.Command + "'", yaml);
        Assert.StartsWith("# x-offload: managed", yaml);
        Assert.Contains("schema: v1", yaml);
    }

    [Fact]
    public void Continue_YamlQuoting()
    {
        var spec = new McpServerSpec("offload", @"C:\Users\O'Brien Иван\#dir\Offload.exe", ["--mcp"], new Dictionary<string, string>());
        var yaml = Clients.ContinueIntegration.BuildYaml(spec);
        Assert.Contains(@"command: 'C:\Users\O''Brien Иван\#dir\Offload.exe'", yaml);
        var info = Clients.ContinueIntegration.ParseYaml(yaml)!;
        Assert.Equal(spec.Command, info.Command);
        Assert.Equal(["--mcp"], info.Args);
    }

    [Fact]
    public void Cline_LegacyPath_WhenNoV4()
    {
        using var sb = new Sandbox();
        sb.Dir("AppData", "Roaming", "Code", "User", "globalStorage", "saoudrizwan.claude-dev");
        var cline = IntegrationRegistry.Find("cline")!;
        Assert.True(cline.IsClientInstalled());
        Assert.Contains("globalStorage", cline.ConfigPath);
        sb.Dir(".vscode", "extensions", "saoudrizwan.claude-dev-4.1.19");
        Assert.Contains(".cline", cline.ConfigPath);
    }

    [Fact]
    public async Task UnregisterAll_AlsoCleansStaleLegacyLocation()
    {
        using var sb = new Sandbox();
        sb.Dir(".cline");
        var legacy = sb.Write(sb.P("AppData", "Roaming", "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
            "{\n  \"mcpServers\": {\n    \"offload\": {\"command\": \"C:\\\\old\\\\Offload.exe\"},\n    \"keep\": {\"command\": \"node\"}\n  }\n}\n");
        Assert.Equal(IntegrationStatus.NotRegistered, IntegrationRegistry.Find("cline")!.GetStatus(sb.Spec()));
        await IntegrationRegistry.UnregisterAllAsync();
        var text = File.ReadAllText(legacy);
        Assert.DoesNotContain("offload", text);
        Assert.Contains("\"keep\"", text);
    }

    [Fact]
    public void Cline_EnvironmentOverrides()
    {
        var custom = Path.Combine(Path.GetTempPath(), TestSetup.SandboxPrefix + "cline", "x.json");
        using var sb = new Sandbox(new Dictionary<string, string> { ["CLINE_MCP_SETTINGS_PATH"] = custom });
        Assert.Equal(custom, IntegrationRegistry.Find("cline")!.ConfigPath);
    }

    [Fact]
    public void VisualStudio_BuildToolsOnly_IsNotDetected()
    {
        using var sb = new Sandbox();
        sb.Dir("ProgramFilesX86", "Microsoft Visual Studio", "2022", "BuildTools", "Common7", "Tools");
        sb.Dir("ProgramFilesX86", "Microsoft Visual Studio", "Installer");
        Assert.False(IntegrationRegistry.Find("visual-studio")!.IsClientInstalled());
    }

    [Fact]
    public async Task JetBrainsAi_IsInstructionsOnly()
    {
        using var sb = new Sandbox();
        var jb = IntegrationRegistry.Find("jetbrains-ai")!;
        Assert.Equal(IntegrationStatus.ClientNotFound, jb.GetStatus(sb.Spec()));
        sb.Dir("AppData", "Roaming", "JetBrains", "Rider2026.2");
        Assert.Equal(IntegrationStatus.NotRegistered, jb.GetStatus(sb.Spec()));
        var r = await jb.RegisterAsync(sb.Spec());
        Assert.False(r.Ok);
        Assert.Contains("Import from Claude", r.Message);
        Assert.Contains("\"mcpServers\"", r.Message);
        Assert.Null(jb.ConfigPath);
    }

    [Fact]
    public void CommandPath_Normalization()
    {
        Assert.True(CommandPath.Same(@"C:\Program Files\Offload\Offload.exe", "c:/program files/offload/OFFLOAD.EXE"));
        Assert.True(CommandPath.Same(@"""C:\a\..\b\Offload.exe""", @"C:\b\Offload.exe"));
        Assert.False(CommandPath.Same(@"C:\a\Offload.exe", @"C:\b\Offload.exe"));
        Assert.False(CommandPath.Same("", ""));
        Assert.True(CommandPath.IsOffload(@"D:\Tools\Offload.Mcp.exe"));
        Assert.False(CommandPath.IsOffload("python"));
        Assert.False(CommandPath.IsOffload(null));

        // Короткое имя 8.3 разворачивается (если файловая система их создаёт).
        using var sb = new Sandbox();
        var exe = sb.Write(sb.P("Long Folder Name", "Offload.exe"), "");
        var shortPath = new StringBuilder(260);
        if (GetShortPathName(exe, shortPath, 260) > 0 && shortPath.ToString() != exe)
            Assert.True(CommandPath.Same(shortPath.ToString(), exe));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

    [Fact]
    public void ForCurrentExecutable_Shape()
    {
        var spec = McpServerSpec.ForCurrentExecutable();
        Assert.Equal("offload", spec.Name);
        Assert.Equal(["--mcp"], spec.Args);
        Assert.Empty(spec.Env);
        Assert.True(Path.IsPathFullyQualified(spec.Command));
    }
}

internal static class Samples
{
    public const string ClaudeJson =
        "{\n" +
        "  \"numStartups\": 412,\n" +
        "  \"installMethod\": \"native\",\n" +
        "  \"cachedGrowthBookFeatures\": {\n    \"tengu_a\": false,\n    \"tengu_b\": {\"x\": [1, 2, 3]}\n  },\n" +
        "  \"projects\": {\n" +
        "    \"E:\\\\github\\\\auc\": {\n" +
        "      \"allowedTools\": [],\n" +
        "      \"mcpServers\": {\n        \"offload\": {\"type\": \"stdio\", \"command\": \"python\", \"args\": [\"-m\", \"pc\"]}\n      },\n" +
        "      \"history\": [{\"display\": \"Привет, \\\"мир\\\" \\u2014 тест\", \"pastedContents\": {}}],\n" +
        "      \"lastCost\": 0.5731\n" +
        "    },\n" +
        "    \"C:/Users/Иван/proj\": {\"hasTrustDialogAccepted\": true}\n" +
        "  },\n" +
        "  \"oauthAccount\": {\"emailAddress\": \"u@example.com\"},\n" +
        "  \"userID\": \"ef14d641\"\n" +
        "}";

    public const string DesktopConfig =
        "{\n  \"coworkUserFilesPath\": \"C:\\\\Users\\\\u\\\\Documents\",\n  \"preferences\": {\n    \"menuBarEnabled\": false\n  },\n  \"mcpServers\": {\n    \"fs\": {\n      \"command\": \"npx\",\n      \"args\": [\"-y\", \"@modelcontextprotocol/server-filesystem\"]\n    }\n  }\n}\n";

    public const string CursorJson =
        "{\n  \"mcpServers\": {\n    \"context7\": {\n      \"url\": \"https://mcp.context7.com/mcp\"\n    }\n  }\n}\n";

    public const string VsCodeMcp =
        "{\n\t// Серверы MCP пользователя\n\t\"servers\": {\n\t\t\"github\": {\n\t\t\t\"type\": \"http\",\n\t\t\t\"url\": \"https://api.githubcopilot.com/mcp/\" // удалённый\n\t\t}\n\t},\n\t/* inputs for secrets */\n\t\"inputs\": []\n}\n";

    public const string ZedSettings =
        "// Zed settings\n//\n// For information on how to configure Zed, see the Zed\n// documentation: https://zed.dev/docs/configuring-zed\n{\n  \"theme\": \"One Dark\",\n  \"context_servers\": {\n    \"other\": {\n      \"command\": \"node\",\n      \"args\": [\"server.js\",],\n    },\n  },\n  \"vim_mode\": false,\n}\n";

    public const string GeminiSettings =
        "{\n  // тема\n  \"theme\": \"GitHub\",\n  \"mcpServers\": {\n    \"other\": {\"command\": \"x\"}\n  }\n}\n";

    public const string KiloJsonc =
        "{\n  \"$schema\": \"https://kilo.ai/config.json\",\n  // модели\n  \"model\": \"local/qwen\",\n  \"mcp\": {\n    \"fs\": {\"type\": \"local\", \"command\": [\"npx\", \"fs\"]}\n  }\n}\n";
}
