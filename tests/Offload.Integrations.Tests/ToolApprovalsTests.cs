using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Integrations.Clients;

namespace Offload.Integrations.Tests;

public class ToolApprovalsTests
{
    private static readonly System.Text.Json.JsonDocumentOptions Lenient = new()
    {
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Песочница с Cline, Roo Code, Kiro и Gemini CLI, где Offload подключён, и установленным Claude Code.</summary>
    /// <remarks>Песочницу создаёт сам тест: подмена окружения живёт в AsyncLocal и не возвращается из async-метода.</remarks>
    private static async Task ArrangeAsync(Sandbox sb, bool approveOnRegister = false)
    {
        sb.Dir(".claude");
        sb.Write(sb.P(".cline", "data", "settings", "cline_mcp_settings.json"), "{\n  \"mcpServers\": {}\n}");
        sb.Dir("AppData", "Roaming", "Code", "User", "globalStorage", "rooveterinaryinc.roo-cline");
        sb.Dir(".kiro");
        sb.Write(sb.P(".gemini", "settings.json"), "{\n  // тема\n  \"theme\": \"Default\"\n}");
        var spec = sb.Spec() with { ApproveWriteTools = approveOnRegister };
        foreach (var id in new[] { "cline", "roo-code", "kiro", "gemini-cli" })
        {
            var r = await IntegrationRegistry.Find(id)!.RegisterAsync(spec);
            Assert.True(r.Ok, $"{id}: {r.Message}");
        }
    }

    private static JsonObject Entry(string id) =>
        JsonNode.Parse(File.ReadAllText(IntegrationRegistry.Find(id)!.ConfigPath!), documentOptions: Lenient)!["mcpServers"]!["offload"]!.AsObject();

    private static List<string> List(string id, string key) => Entry(id)[key]!.AsArray().Select(n => (string)n!).ToList();

    [Fact]
    public void SupportedIds_ClaudeFirst_ThenClientsWithApprovalKey()
    {
        Assert.Equal(["claude-code", "cline", "roo-code", "gemini-cli", "kiro"], ToolApprovals.SupportedIds);
    }

    [Fact]
    public async Task Default_OnlyReadTools_NoClaudeApprovals()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb);

        var states = ToolApprovals.Inspect();

        Assert.DoesNotContain(states, s => s.Id == "claude-code"); // разрешений Claude Code ещё нет
        Assert.Equal(["cline", "roo-code", "gemini-cli", "kiro"], states.Select(s => s.Id));
        Assert.All(states, s => Assert.Equal(WriteApproval.None, s.State));
        Assert.False((bool)Entry("gemini-cli")["trust"]!);
    }

    [Fact]
    public async Task Allow_ThenRevoke_AllClients()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb);

        var on = ToolApprovals.SetWriteTools(allow: true);

        Assert.Equal(["claude-code", "cline", "roo-code", "gemini-cli", "kiro"], on.Select(r => r.Id));
        Assert.All(on, r => Assert.True(r.Result.Ok, r.Result.Message));
        Assert.All(ToolApprovals.Inspect(), s => Assert.Equal(WriteApproval.Full, s.State));
        Assert.Equal(McpToolNames.All.Count, List("cline", "autoApprove").Count);
        Assert.Subset(List("roo-code", "alwaysAllow").ToHashSet(), McpToolNames.Writing.ToHashSet());
        Assert.True((bool)Entry("gemini-cli")["trust"]!);
        Assert.Contains("// тема", File.ReadAllText(IntegrationRegistry.Find("gemini-cli")!.ConfigPath!));
        Assert.True(ClaudeCodeExtras.AreWriteToolsPreapproved());

        Assert.Empty(ToolApprovals.SetWriteTools(allow: true)); // повтор ничего не меняет

        var off = ToolApprovals.SetWriteTools(allow: false);

        Assert.Equal(5, off.Count);
        Assert.All(ToolApprovals.Inspect(), s => Assert.Equal(WriteApproval.None, s.State));
        Assert.Equal(McpToolNames.ReadOnly, List("kiro", "autoApprove"));
        Assert.False((bool)Entry("gemini-cli")["trust"]!);
        Assert.True(ClaudeCodeExtras.AreToolsPreapproved()); // инструменты чтения в Claude Code остаются
        Assert.False(ClaudeCodeExtras.AreWriteToolsPreapproved());
    }

    [Fact]
    public async Task RegisterWithApproval_WritesAllTools_AndKeepsUserAdditions()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb);
        var path = IntegrationRegistry.Find("cline")!.ConfigPath!;
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        root["mcpServers"]!["offload"]!["autoApprove"] = JsonIntegration.Strings([McpToolNames.Status, "custom"]);
        File.WriteAllText(path, root.ToJsonString());

        var r = await IntegrationRegistry.Find("cline")!.RegisterAsync(sb.Spec() with { ApproveWriteTools = true });
        Assert.True(r.Ok, r.Message);

        var list = List("cline", "autoApprove");
        Assert.Contains("custom", list);
        Assert.Subset(list.ToHashSet(), McpToolNames.Writing.ToHashSet());
    }

    [Fact]
    public async Task RegisterWithApproval_FreshEntries()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb, approveOnRegister: true);

        Assert.All(ToolApprovals.Inspect(), s => Assert.Equal(WriteApproval.Full, s.State));
        Assert.True((bool)Entry("gemini-cli")["trust"]!);
    }

    [Fact]
    public async Task RegisterWithoutApproval_DoesNotRevokeEarlierConsent()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb, approveOnRegister: true);

        var r = await IntegrationRegistry.Find("roo-code")!.RegisterAsync(sb.Spec());
        Assert.True(r.Ok, r.Message);

        Assert.Subset(List("roo-code", "alwaysAllow").ToHashSet(), McpToolNames.Writing.ToHashSet());
    }

    [Fact]
    public async Task NewWriteTool_IsPartial_UntilConsentAgain()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb, approveOnRegister: true);
        var path = IntegrationRegistry.Find("kiro")!.ConfigPath!;
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var missing = McpToolNames.Writing[^1];
        root["mcpServers"]!["offload"]!["autoApprove"] = JsonIntegration.Strings(McpToolNames.All.Where(t => t != missing));
        File.WriteAllText(path, root.ToJsonString());

        var kiro = Assert.Single(ToolApprovals.Inspect(), s => s.Id == "kiro");

        Assert.Equal(WriteApproval.Partial, kiro.State);
        Assert.Equal([missing], kiro.Missing);
    }

    [Fact]
    public async Task ForeignEntry_IsNotTouched()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb);
        var path = IntegrationRegistry.Find("cline")!.ConfigPath!;
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        root["mcpServers"]!["offload"]!["command"] = @"C:\tools\my-wrapper.exe";
        File.WriteAllText(path, root.ToJsonString());
        var before = File.ReadAllText(path);

        var results = ToolApprovals.SetWriteTools(allow: true);

        Assert.DoesNotContain(results, r => r.Id == "cline");
        Assert.Equal(before, File.ReadAllText(path));
        Assert.DoesNotContain(ToolApprovals.Inspect(), s => s.Id == "cline");
    }

    [Theory]
    [InlineData(null, true, "[\"local_status\"")]
    [InlineData("[\"local_status\",\"x\"]", false, null)]
    [InlineData("\"oops\"", true, null)]
    public void WithWriteTools_Lists(string? current, bool allow, string? startsWith)
    {
        var node = current is null ? null : JsonNode.Parse(current);
        var result = JsonIntegration.WithWriteTools(node, allow, trustFlag: false);
        if (startsWith is null) Assert.Null(result);
        else Assert.StartsWith(startsWith, result!.ToJsonString());
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("false", true, true)]
    [InlineData("true", true, null)]
    [InlineData("true", false, false)]
    [InlineData("false", false, null)]
    public void WithWriteTools_Trust(string? current, bool allow, bool? expected)
    {
        var node = current is null ? null : JsonNode.Parse(current);
        var result = JsonIntegration.WithWriteTools(node, allow, trustFlag: true);
        Assert.Equal(expected, result is null ? null : (bool)result);
    }

    [Fact]
    public async Task StaleReadListRefresh_DoesNotGrantNewWriteTool()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb, approveOnRegister: true);
        var path = IntegrationRegistry.Find("cline")!.ConfigPath!;
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var newRead = McpToolNames.ReadOnly[^1];
        var newWrite = McpToolNames.Writing[^1];
        // «Прежняя версия»: нет появившихся позже инструмента чтения и инструмента записи.
        root["mcpServers"]!["offload"]!["autoApprove"] = JsonIntegration.Strings(McpToolNames.All.Where(t => t != newRead && t != newWrite));
        File.WriteAllText(path, root.ToJsonString());

        var r = await IntegrationRegistry.Find("cline")!.RegisterAsync(sb.Spec());
        Assert.True(r.Ok, r.Message);

        var list = List("cline", "autoApprove");
        Assert.Contains(newRead, list); // устаревшее умолчание обновлено
        Assert.DoesNotContain(newWrite, list); // но новый инструмент записи — только по согласию
    }

    [Fact]
    public async Task Gemini_IsWholeServer()
    {
        using var sb = new Sandbox();
        await ArrangeAsync(sb, approveOnRegister: true);

        var gemini = Assert.Single(ToolApprovals.Inspect(), s => s.Id == "gemini-cli");

        Assert.True(gemini.WholeServer);
        Assert.DoesNotContain(ToolApprovals.Inspect(), s => s.Id != "gemini-cli" && s.WholeServer);
    }
}
