using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Tests;

/// <summary>
/// Автовосстановление: само исправляет только путь к отсутствующему Offload.exe в нашей записи; удалённые, чужие,
/// устаревшие и указывающие на другую копию записи не трогает, а сообщает о них.
/// </summary>
public class IntegrationRepairTests
{
    private static string CursorFile(Sandbox sb)
    {
        sb.Dir(".cursor");
        return sb.P(".cursor", "mcp.json");
    }

    // Кириллица в пути — без \u-экранирования, как пишет JsoncEditor.
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JsonTree.Encoder };

    private static string Entry(string command, params string[] args) =>
        "{\n  \"mcpServers\": {\n    \"other\": { \"command\": \"x.exe\" },\n    \"offload\": { \"type\": \"stdio\", \"command\": " +
        JsonValue.Create(command).ToJsonString(Unescaped) + ", \"args\": " + JsonIntegration.Strings(args).ToJsonString(Unescaped) +
        ", \"disabled\": true, \"env\": { \"MY_VAR\": \"1\" } }\n  }\n}\n";

    [Fact]
    public async Task MissingEntry_NotRestored_ReportedForAttention()
    {
        using var sb = new Sandbox();
        var path = CursorFile(sb);
        var ct = TestContext.Current.CancellationToken;
        // Пользователь убрал запись сам (или IDE сбросила конфиг) — возвращать её молча нельзя.
        const string content = "{\"mcpServers\":{\"other\":{\"command\":\"x.exe\"}}}";
        sb.Write(path, content);

        var r = await IntegrationRegistry.RepairAsync(["cursor", "vscode", "no-such-ide"], sb.Spec(), _ => true, ct);

        Assert.Empty(r.Repaired);
        Assert.Empty(r.Failed);
        Assert.Equal(new[] { new RepairIssue("cursor", RepairNeed.Missing) }, r.NeedsAttention);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(IntegrationStatus.NotRegistered, IntegrationRegistry.Find("cursor")!.GetStatus(sb.Spec()));

        // Явное «Восстановить» (обычная регистрация) возвращает запись.
        Assert.True((await IntegrationRegistry.Find("cursor")!.RegisterAsync(sb.Spec(), ct)).Ok);
        Assert.Empty((await IntegrationRegistry.RepairAsync(["cursor"], sb.Spec(), ct: ct)).NeedsAttention);
    }

    [Fact]
    public async Task ClaudeCode_RemovedByUser_NotReAdded()
    {
        using var sb = new Sandbox();
        sb.Dir(".claude");
        // Как после «claude mcp remove offload»: записи нет, остальной файл состояния Claude Code на месте.
        var file = sb.Write(ClientLocations.ClaudeGlobalConfig, "{\"numStartups\":3,\"mcpServers\":{}}");
        Assert.StartsWith(sb.Root, file, StringComparison.OrdinalIgnoreCase);
        var before = File.ReadAllText(file);

        var r = await IntegrationRegistry.RepairAsync(["claude-code"], sb.Spec(), _ => true, TestContext.Current.CancellationToken);

        Assert.Empty(r.Repaired);
        Assert.Equal(new[] { new RepairIssue("claude-code", RepairNeed.Missing) }, r.NeedsAttention);
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public async Task MovedExe_OnlyPathUpdated_UserKeysKept_RespectsLimiter()
    {
        using var sb = new Sandbox();
        var path = CursorFile(sb);
        var ct = TestContext.Current.CancellationToken;
        var old = sb.P("Old place", "Offload.exe"); // файла нет — программу переместили
        sb.Write(path, Entry(old, AppInfo.McpArg));
        var cursor = IntegrationRegistry.Find("cursor")!;
        Assert.Equal(RepairNeed.PathMoved, IntegrationRegistry.Assess(cursor, sb.Spec()));

        var limited = await IntegrationRegistry.RepairAsync(["cursor"], sb.Spec(), _ => false, ct);
        Assert.Equal(new[] { "cursor" }, limited.Skipped);
        Assert.Empty(limited.NeedsAttention);
        Assert.Equal(Entry(old, AppInfo.McpArg), File.ReadAllText(path));

        var r = await IntegrationRegistry.RepairAsync(["cursor"], sb.Spec(), _ => true, ct);
        Assert.Equal(new[] { "cursor" }, r.Repaired);
        Assert.Empty(r.NeedsAttention);
        // Изменился только путь: форма записи, ключи пользователя и соседняя запись — как были.
        Assert.Equal(Entry(sb.Spec().Command, AppInfo.McpArg), File.ReadAllText(path));
        Assert.Equal(IntegrationStatus.Registered, cursor.GetStatus(sb.Spec()));
    }

    [Fact]
    public async Task DevBuildOrOtherCopy_NotTouched()
    {
        using var sb = new Sandbox();
        var path = CursorFile(sb);
        var ct = TestContext.Current.CancellationToken;
        // Существующий Offload.exe другой копии (dev-сборка): «наш» по имени, но на установленную копию не переписывается.
        var dev = sb.Write(sb.P("src", "Offload.App", "bin", "Debug", "Offload.exe"), "MZ");
        var content = Entry(dev, AppInfo.McpArg);
        sb.Write(path, content);

        var r = await IntegrationRegistry.RepairAsync(["cursor"], sb.Spec(), _ => true, ct);
        Assert.Empty(r.Repaired);
        Assert.Equal(new[] { new RepairIssue("cursor", RepairNeed.OtherCopy) }, r.NeedsAttention);
        Assert.Empty(await IntegrationRegistry.RefreshOutdatedAsync(sb.Spec(), ct));
        Assert.Equal(content, File.ReadAllText(path));

        // Exe отсутствует, но у записи свои аргументы пользователя — тоже не трогаем.
        var custom = Entry(sb.P("Old", "Offload.exe"), AppInfo.McpArg, "--profile", "work");
        sb.Write(path, custom);
        r = await IntegrationRegistry.RepairAsync(["cursor"], sb.Spec(), _ => true, ct);
        Assert.Empty(r.Repaired);
        Assert.Equal(RepairNeed.OtherCopy, Assert.Single(r.NeedsAttention).Need);
        Assert.Equal(custom, File.ReadAllText(path));
    }

    [Fact]
    public async Task ForeignEntryNamedOffload_Untouched()
    {
        using var sb = new Sandbox();
        var path = CursorFile(sb);
        const string content = "{\"mcpServers\":{\"offload\":{\"command\":\"node\",\"args\":[\"proxy.js\"]}}}";
        sb.Write(path, content);
        var cursor = IntegrationRegistry.Find("cursor")!;
        Assert.Equal(IntegrationStatus.Foreign, cursor.GetStatus(sb.Spec()));

        var r = await IntegrationRegistry.RepairAsync(["cursor"], sb.Spec(), _ => true, TestContext.Current.CancellationToken);
        Assert.Empty(r.Repaired);
        Assert.Equal(new[] { new RepairIssue("cursor", RepairNeed.Foreign) }, r.NeedsAttention);
        Assert.Empty(await IntegrationRegistry.RefreshOutdatedAsync(sb.Spec(), TestContext.Current.CancellationToken));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task TrimmedAutoApprove_StaleDefaults_NotAutoFixed()
    {
        using var sb = new Sandbox();
        sb.Dir(".kiro");
        var kiro = IntegrationRegistry.Find("kiro")!;
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await kiro.RegisterAsync(sb.Spec(), ct)).Ok);
        var path = kiro.ConfigPath!;
        // Пользователь сократил список автоодобрения.
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["mcpServers"]!["offload"]!["autoApprove"] = JsonIntegration.Strings([McpToolNames.ReadOnly[0]]);
        File.WriteAllText(path, node.ToJsonString());
        var trimmed = File.ReadAllText(path);
        Assert.Equal(RepairNeed.StaleSettings, IntegrationRegistry.Assess(kiro, sb.Spec()));

        var r = await IntegrationRegistry.RepairAsync(["kiro"], sb.Spec(), _ => true, ct);
        Assert.Empty(r.Repaired);
        Assert.Equal(new[] { new RepairIssue("kiro", RepairNeed.StaleSettings) }, r.NeedsAttention);
        Assert.Empty(await IntegrationRegistry.RefreshOutdatedAsync(sb.Spec(), ct));
        Assert.Equal(trimmed, File.ReadAllText(path));
    }

    [Fact]
    public async Task MovedExe_Codex_OnlyCommandChanges()
    {
        using var sb = new Sandbox();
        sb.Dir(".codex");
        var path = sb.P(".codex", "config.toml");
        var old = sb.P("Old", "Offload.exe").Replace("\\", "\\\\", StringComparison.Ordinal);
        var text = "# my config\n[mcp_servers.offload]\ncommand = \"" + old + "\"\nargs = [\"--mcp\"]\ntool_timeout_sec = 99 # mine\n";
        sb.Write(path, text);

        var r = await IntegrationRegistry.RepairAsync(["codex"], sb.Spec(), _ => true, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "codex" }, r.Repaired);
        var after = File.ReadAllText(path);
        Assert.Contains("tool_timeout_sec = 99 # mine", after, StringComparison.Ordinal);
        Assert.Contains("# my config", after, StringComparison.Ordinal);
        Assert.DoesNotContain("startup_timeout_sec", after, StringComparison.Ordinal); // умолчания не дописываются
        Assert.Equal(IntegrationStatus.Registered, IntegrationRegistry.Find("codex")!.GetStatus(sb.Spec()));
    }
}
