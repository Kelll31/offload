using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Integrations.Claude;
using Offload.Integrations.Clients;

namespace Offload.Integrations.Tests;

public class ClaudeExtrasTests
{
    private const string Settings =
        "{\n  \"permissions\": {\n    \"allow\": [\n      \"Bash(echo \\\"exit $?\\\")\",\n      \"Read(//tmp/**)\"\n    ],\n    \"deny\": [\"Read(.env)\"]\n  },\n  \"modelSettings\": {\n    \"claude-opus-5\": {\"effortLevel\": \"xhigh\"}\n  }\n}\n";

    private static Sandbox WithClaude()
    {
        var sb = new Sandbox();
        sb.Dir(".claude");
        return sb;
    }

    [Fact]
    public void NotInstalled_NothingWritten()
    {
        using var sb = new Sandbox();
        Assert.False(ClaudeCodeExtras.InstallGuidance().Ok);
        Assert.False(ClaudeCodeExtras.PreapproveTools(false).Ok);
        Assert.True(ClaudeCodeExtras.RevokeToolApprovals().Ok);
        Assert.False(Directory.Exists(sb.P(".claude")));
    }

    [Fact]
    public void Guidance_InstallIdempotentRemove()
    {
        using var sb = WithClaude();
        Assert.False(ClaudeCodeExtras.IsGuidanceInstalled());
        var r = ClaudeCodeExtras.InstallGuidance();
        Assert.True(r.Ok, r.Message);
        Assert.True(ClaudeCodeExtras.IsGuidanceInstalled());
        var path = sb.P(".claude", "skills", "offload", "SKILL.md");
        var text = File.ReadAllText(path);
        Assert.StartsWith("---\nname: offload\n", text);
        Assert.Contains("x-offload: managed", text);
        foreach (var t in McpToolNames.All) Assert.Contains(t, text);
        var allowed = text.Split('\n').Single(l => l.StartsWith("allowed-tools:"));
        foreach (var t in McpToolNames.ReadOnly) Assert.Contains(McpToolNames.ClaudeCodeName(t), allowed);
        foreach (var t in McpToolNames.Writing) Assert.DoesNotContain(t, allowed);
        Assert.True(ClaudeTexts.SkillDescription.Length + ClaudeTexts.SkillWhenToUse.Length <= 1536);
        Assert.DoesNotContain(": ", ClaudeTexts.SkillDescription); // простой YAML-скаляр

        var mtime = File.GetLastWriteTimeUtc(path);
        Assert.Contains("уже", ClaudeCodeExtras.InstallGuidance().Message);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));

        Assert.True(ClaudeCodeExtras.RemoveGuidance().Ok);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(sb.P(".claude", "skills", "offload")));
        Assert.True(ClaudeCodeExtras.RemoveGuidance().Ok);
    }

    [Fact]
    public void ForeignSkill_IsNeverOverwrittenOrDeleted()
    {
        using var sb = WithClaude();
        var path = sb.Write(sb.P(".claude", "skills", "offload", "SKILL.md"), "---\nname: offload\ndescription: mine\n---\nhello");
        Assert.False(ClaudeCodeExtras.IsGuidanceInstalled());
        Assert.False(ClaudeCodeExtras.InstallGuidance().Ok);
        Assert.False(ClaudeCodeExtras.RemoveGuidance().Ok);
        Assert.Equal("---\nname: offload\ndescription: mine\n---\nhello", File.ReadAllText(path));
    }

    [Fact]
    public void StrongRule_And_RunnerAgent()
    {
        using var sb = WithClaude();
        sb.Write(sb.P(".claude", "rules", "other.md"), "keep");
        Assert.True(ClaudeCodeExtras.InstallStrongRule().Ok);
        Assert.True(ClaudeCodeExtras.IsStrongRuleInstalled());
        var rule = File.ReadAllText(sb.P(".claude", "rules", "offload.md"));
        Assert.StartsWith("<!-- x-offload: managed", rule);
        Assert.Contains("mcp__offload__local_ask_files", rule);

        Assert.True(ClaudeCodeExtras.InstallRunnerAgent().Ok);
        Assert.True(ClaudeCodeExtras.IsRunnerAgentInstalled());
        var agent = File.ReadAllText(sb.P(".claude", "agents", "offload-runner.md"));
        Assert.StartsWith("---\nname: offload-runner\n", agent);
        Assert.Contains("\ntools: mcp__offload, Read, Grep, Glob\n", agent);
        Assert.Contains("\nmodel: haiku\n", agent);

        Assert.True(ClaudeCodeExtras.RemoveStrongRule().Ok);
        Assert.True(ClaudeCodeExtras.RemoveRunnerAgent().Ok);
        Assert.False(ClaudeCodeExtras.IsStrongRuleInstalled());
        Assert.False(ClaudeCodeExtras.IsRunnerAgentInstalled());
        Assert.Equal("keep", File.ReadAllText(sb.P(".claude", "rules", "other.md")));
    }

    [Fact]
    public void Permissions_MergeKeepEverythingElse()
    {
        using var sb = WithClaude();
        var path = sb.Write(sb.P(".claude", "settings.json"), Settings);
        Assert.False(ClaudeCodeExtras.AreToolsPreapproved());

        var r = ClaudeCodeExtras.PreapproveTools(false);
        Assert.True(r.Ok, r.Message);
        Assert.NotNull(r.BackupPath);
        Assert.True(ClaudeCodeExtras.AreToolsPreapproved());
        Assert.False(ClaudeCodeExtras.AreWriteToolsPreapproved());
        var text = File.ReadAllText(path);
        Assert.Contains("\"Bash(echo \\\"exit $?\\\")\",\n      \"Read(//tmp/**)\",\n      \"mcp__offload__local_status\"", text);
        Assert.Contains("\"deny\": [\"Read(.env)\"]", text);
        Assert.Contains("\"claude-opus-5\": {\"effortLevel\": \"xhigh\"}", text);

        // Повтор — без изменений и без дубликатов.
        Assert.True(ClaudeCodeExtras.PreapproveTools(false).Ok);
        Assert.Equal(text, File.ReadAllText(path));

        Assert.True(ClaudeCodeExtras.PreapproveTools(true).Ok);
        Assert.True(ClaudeCodeExtras.AreWriteToolsPreapproved());
        var allow = JsonNode.Parse(File.ReadAllText(path))!["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Equal(allow.Count, allow.Distinct().Count());
        Assert.Equal(2 + McpToolNames.All.Count, allow.Count);
        Assert.DoesNotContain(allow, a => a is "mcp__offload" or "mcp__offload__*");

        // Снять галочку «запись» — остаются только инструменты чтения.
        Assert.True(ClaudeCodeExtras.PreapproveTools(false).Ok);
        Assert.False(ClaudeCodeExtras.AreWriteToolsPreapproved());
        Assert.True(ClaudeCodeExtras.AreToolsPreapproved());

        Assert.True(ClaudeCodeExtras.RevokeToolApprovals().Ok);
        Assert.False(ClaudeCodeExtras.AreToolsPreapproved());
        Assert.Equal(Settings, File.ReadAllText(path));
    }

    [Fact]
    public void Permissions_UserWildcard_IsLeftAlone()
    {
        using var sb = WithClaude();
        var path = sb.Write(sb.P(".claude", "settings.json"), "{\"permissions\":{\"allow\":[\"mcp__offload\"]}}");
        Assert.True(ClaudeCodeExtras.PreapproveTools(false).Ok);
        var r = ClaudeCodeExtras.RevokeToolApprovals();
        Assert.True(r.Ok);
        Assert.Contains("оставлено", r.Message);
        Assert.Equal("{\"permissions\":{\"allow\":[\"mcp__offload\"]}}", File.ReadAllText(path));
    }

    [Fact]
    public void Permissions_NewFile_And_Malformed()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.PreapproveTools(false).Ok);
        Assert.True(ClaudeCodeExtras.AreToolsPreapproved());

        var path = sb.Write(sb.P(".claude", "settings.json"), "{\"permissions\": {\"allow\": \"oops\"}}");
        Assert.False(ClaudeCodeExtras.PreapproveTools(false).Ok);
        Assert.Equal("{\"permissions\": {\"allow\": \"oops\"}}", File.ReadAllText(path));
        sb.Write(path, "{ broken");
        Assert.False(ClaudeCodeExtras.PreapproveTools(true).Ok);
        Assert.False(ClaudeCodeExtras.RevokeToolApprovals().Ok);
        Assert.False(ClaudeCodeExtras.AreToolsPreapproved());
        Assert.Equal("{ broken", File.ReadAllText(path));
    }

    [Fact]
    public void ConfigDirVariable_IsHonored()
    {
        var dir = Path.Combine(Path.GetTempPath(), TestSetup.SandboxPrefix + "cfg-" + Guid.NewGuid().ToString("N"));
        using var sb = new Sandbox(new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = dir });
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(ClaudeCodeExtras.InstallGuidance().Ok);
            Assert.True(File.Exists(Path.Combine(dir, "skills", "offload", "SKILL.md")));
            Assert.Equal(Path.Combine(dir, "settings.json"), ClientLocations.ClaudeSettings);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
