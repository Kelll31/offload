using System.Reflection;
using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Integrations.Claude;
using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

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
        var allowed = text.Split('\n').Single(l => l.StartsWith("allowed-tools:", StringComparison.Ordinal));
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

    [Fact]
    public void RemoveAll_AfterInstallingEverything_LeavesNothingManaged()
    {
        using var sb = WithClaude();
        var settings = sb.Write(sb.P(".claude", "settings.json"), Settings);
        var mine = sb.Write(sb.P(".claude", "rules", "mine.md"), "keep");

        // Все публичные установщики (новое дополнение Install* попадёт в проверку само).
        var installers = typeof(ClaudeCodeExtras).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Install", StringComparison.Ordinal) && m.GetParameters().Length == 0
                        && m.ReturnType == typeof(IntegrationResult))
            .ToList();
        Assert.True(installers.Count >= ClaudeCodeExtras.ManagedFiles.Count, "не все дополнения-файлы имеют установщик");
        foreach (var m in installers)
        {
            var r = (IntegrationResult)m.Invoke(null, null)!;
            Assert.True(r.Ok, $"{m.Name}: {r.Message}");
        }
        Assert.True(ClaudeCodeExtras.PreapproveTools(includeWriteTools: true).Ok);
        Assert.All(ClaudeCodeExtras.ManagedFiles, f => Assert.True(f.IsInstalled(), f.Path()));
        var marked = Directory.EnumerateFiles(sb.P(".claude"), "*", SearchOption.AllDirectories)
            .Count(f => File.ReadAllText(f).Contains(ClaudeTexts.Marker, StringComparison.Ordinal));
        Assert.Equal(ClaudeCodeExtras.ManagedFiles.Count, marked); // каждый файл с меткой учтён в ManagedFiles

        var results = ClaudeCodeExtras.RemoveAll();
        Assert.Equal(ClaudeCodeExtras.ManagedFiles.Count + 1, results.Count);
        Assert.All(results, r => Assert.True(r.Ok, r.Message));

        var left = Directory.EnumerateFiles(sb.P(".claude"), "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(ClaudeTexts.Marker, StringComparison.Ordinal))
            .ToList();
        Assert.True(left.Count == 0, "остались файлы Offload: " + string.Join(", ", left));
        Assert.Equal(Settings, File.ReadAllText(settings)); // разрешения сняты, остальное — байт в байт
        Assert.Equal("keep", File.ReadAllText(mine));
        Assert.False(ClaudeCodeExtras.IsGuidanceInstalled() || ClaudeCodeExtras.IsStrongRuleInstalled()
                     || ClaudeCodeExtras.IsRunnerAgentInstalled() || ClaudeCodeExtras.AreToolsPreapproved());

        // Повтор — без ошибок.
        Assert.All(ClaudeCodeExtras.RemoveAll(), r => Assert.True(r.Ok, r.Message));
    }

    [Fact]
    public void RemoveAll_KeepsForeignFiles()
    {
        using var sb = WithClaude();
        var rule = sb.Write(sb.P(".claude", "rules", "offload.md"), "мои правила");
        Assert.True(ClaudeCodeExtras.InstallRunnerAgent().Ok);
        var results = ClaudeCodeExtras.RemoveAll();
        Assert.Contains(results, r => !r.Ok && r.Message.Contains(rule, StringComparison.Ordinal));
        Assert.Equal("мои правила", File.ReadAllText(rule));
        Assert.False(ClaudeCodeExtras.IsRunnerAgentInstalled());
    }

    [Fact]
    public void RemoveManaged_BacksUpBeforeDelete()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.InstallStrongRule().Ok);
        var path = sb.P(".claude", "rules", "offload.md");
        var content = File.ReadAllText(path);

        var r = ClaudeCodeExtras.RemoveStrongRule();
        Assert.True(r.Ok, r.Message);
        Assert.False(File.Exists(path));
        Assert.NotNull(r.BackupPath);
        Assert.Equal(content, File.ReadAllText(r.BackupPath!));
    }

    [Fact]
    public void ConfigFileDelete_FileChangedAfterSnapshot_Refuses()
    {
        using var sb = new Sandbox();
        var path = sb.Write(sb.P("rules", "offload.md"), "x-offload: managed\nold");
        var snap = ConfigFile.Read(path);
        File.WriteAllText(path, "x-offload: managed\nnew");
        Assert.Throws<ConfigBusyException>(() => ConfigFile.Delete(snap));
        Assert.Equal("x-offload: managed\nnew", File.ReadAllText(path));

        File.Delete(path);
        Assert.Null(ConfigFile.Delete(snap)); // уже удалён — нечего делать
    }

    [Fact]
    public void ManagedFiles_Stamped_RefreshedWhenOutdated_ModifiedKept()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.InstallStrongRule().Ok);
        var rule = sb.P(".claude", "rules", "offload.md");
        Assert.Matches(@"x-offload: managed v=\S+ h=[0-9a-f]{16}", File.ReadAllText(rule));
        Assert.Empty(ClaudeCodeExtras.RefreshManaged());

        // Файл прежней версии Offload (с хэшем) — обновляется.
        File.WriteAllText(rule, ManagedContent.Stamp(ClaudeTexts.StrongRule().Replace("Keep architecture", "Keep design", StringComparison.Ordinal), "0.1.0"));
        var r = Assert.Single(ClaudeCodeExtras.RefreshManaged());
        Assert.True(r.Ok, r.Message);
        Assert.Equal(ManagedContent.Stamp(ClaudeTexts.StrongRule()), File.ReadAllText(rule));

        // Совсем старый файл (метка без хэша), не совпадающий ни с одним выпущенным шаблоном, — это правка пользователя:
        // не перезаписывается и помечается как изменённый.
        var legacyEdited = ClaudeTexts.StrongRule().Replace("Keep architecture", "Keep design", StringComparison.Ordinal);
        File.WriteAllText(rule, legacyEdited);
        Assert.Empty(ClaudeCodeExtras.RefreshManaged());
        Assert.Equal(legacyEdited, File.ReadAllText(rule));
        Assert.Single(ClaudeCodeExtras.ModifiedFiles());

        // Старый файл без хэша, совпадающий с текущим шаблоном, — актуален, запись не нужна.
        File.WriteAllText(rule, ClaudeTexts.StrongRule());
        Assert.Empty(ClaudeCodeExtras.RefreshManaged());
        Assert.Empty(ClaudeCodeExtras.ModifiedFiles());
        File.WriteAllText(rule, ManagedContent.Stamp(ClaudeTexts.StrongRule()));

        // Правка пользователя — файл больше не обновляется и помечается как изменённый.
        var edited = File.ReadAllText(rule).Replace("Keep architecture", "Keep my architecture", StringComparison.Ordinal);
        File.WriteAllText(rule, edited);
        Assert.Empty(ClaudeCodeExtras.RefreshManaged());
        Assert.Equal(edited, File.ReadAllText(rule));
        Assert.Single(ClaudeCodeExtras.ModifiedFiles());
        Assert.True(ClaudeCodeExtras.IsStrongRuleInstalled());
    }

    [Fact]
    public void Approvals_Refresh_AddsOnlyNewTools_NotUserRemoved()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.PreapproveTools(false).Ok);
        var settings = sb.P(".claude", "settings.json");
        var newest = McpToolNames.ClaudeCodeName(McpToolNames.ReadOnly[^1]);
        var userRemoved = McpToolNames.ClaudeCodeName(McpToolNames.ReadOnly[0]);

        // «Прежняя версия» не знала о последнем инструменте, а пользователь сам убрал первый.
        var node = JsonNode.Parse(File.ReadAllText(settings))!;
        var allow = node["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).Where(n => n != newest && n != userRemoved).ToList();
        node["permissions"]!["allow"] = JsonIntegration.Strings(allow);
        File.WriteAllText(settings, node.ToJsonString());
        ApprovalsRecord.Save(McpToolNames.ReadOnly.Select(McpToolNames.ClaudeCodeName).Where(n => n != newest));

        var r = Assert.Single(ClaudeCodeExtras.RefreshManaged());
        Assert.True(r.Ok, r.Message);
        var after = JsonNode.Parse(File.ReadAllText(settings))!["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains(newest, after);
        Assert.DoesNotContain(userRemoved, after);
        Assert.Empty(ClaudeCodeExtras.RefreshManaged());

        // Разрешения выключены — ничего не добавляем.
        Assert.True(ClaudeCodeExtras.RevokeToolApprovals().Ok);
        Assert.Empty(ClaudeCodeExtras.RefreshManaged());
    }

    private static List<string> Allow(Sandbox sb) =>
        JsonNode.Parse(File.ReadAllText(sb.P(".claude", "settings.json")))!["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();

    private static void SetAllow(Sandbox sb, IEnumerable<string> names)
    {
        var path = sb.P(".claude", "settings.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["permissions"]!["allow"] = JsonIntegration.Strings(names);
        File.WriteAllText(path, node.ToJsonString());
    }

    [Fact]
    public void Approvals_FirstRunWithoutRecord_AddsNothing_OnlyRecords()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.PreapproveTools(false).Ok);
        var missing = McpToolNames.ClaudeCodeName(McpToolNames.ReadOnly[^1]);
        // Как после Offload 1.0.0: записи нет, в списке — не все инструменты чтения (новый или убранный пользователем — не отличить).
        SetAllow(sb, Allow(sb).Where(n => n != missing));
        File.Delete(ApprovalsRecord.FilePath);

        Assert.Empty(ClaudeCodeExtras.RefreshManaged());
        Assert.DoesNotContain(missing, Allow(sb));
        Assert.True(File.Exists(ApprovalsRecord.FilePath), "текущее состояние записано");

        // И при следующих запусках недостающий инструмент не возвращается.
        Assert.Empty(ClaudeCodeExtras.RefreshManaged());
        Assert.DoesNotContain(missing, Allow(sb));
    }

    [Fact]
    public void Approvals_Refresh_NeverAddsNewWriteTools_ToggleIsConsent()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.PreapproveTools(true).Ok);
        Assert.Empty(ClaudeExtrasImpl.MissingTools()!);
        var newWrite = McpToolNames.Writing[^1];
        var newWriteName = McpToolNames.ClaudeCodeName(newWrite);
        // «Прежняя версия» разрешила все инструменты записи, кроме появившегося позже.
        SetAllow(sb, Allow(sb).Where(n => n != newWriteName));
        ApprovalsRecord.Save(McpToolNames.All.Select(McpToolNames.ClaudeCodeName).Where(n => n != newWriteName));

        Assert.Empty(ClaudeCodeExtras.RefreshManaged());
        Assert.DoesNotContain(newWriteName, Allow(sb));
        Assert.Equal(new[] { newWrite }, ClaudeExtrasImpl.MissingTools()!);
        Assert.False(ClaudeCodeExtras.AreWriteToolsPreapproved());

        // Явное согласие (переключатель на странице) добавляет его.
        Assert.True(ClaudeCodeExtras.PreapproveTools(true).Ok);
        Assert.Contains(newWriteName, Allow(sb));
        Assert.Empty(ClaudeExtrasImpl.MissingTools()!);
    }

    [Fact]
    public void Approvals_NotRefreshed_WhenExcluded()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.PreapproveTools(false).Ok);
        var newest = McpToolNames.ClaudeCodeName(McpToolNames.ReadOnly[^1]);
        SetAllow(sb, Allow(sb).Where(n => n != newest));
        ApprovalsRecord.Save(McpToolNames.ReadOnly.Select(McpToolNames.ClaudeCodeName).Where(n => n != newest));
        var before = File.ReadAllText(sb.P(".claude", "settings.json"));

        // Автовосстановление выключено — наблюдатель вызывает обновление без разрешений: settings.json не меняется.
        Assert.Empty(ClaudeCodeExtras.RefreshManaged(includeApprovals: false));
        Assert.Equal(before, File.ReadAllText(sb.P(".claude", "settings.json")));
    }

    [Fact]
    public void RevokeWriteTools_KeepsReadToolRemovedByUser()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.PreapproveTools(true).Ok);
        var removed = McpToolNames.ClaudeCodeName(McpToolNames.ReadOnly[0]);
        SetAllow(sb, Allow(sb).Where(n => n != removed));

        Assert.True(ClaudeExtrasImpl.RevokeWriteTools().Ok);

        var allow = Allow(sb);
        Assert.DoesNotContain(removed, allow); // отзыв записи не возвращает убранное пользователем
        Assert.DoesNotContain(allow, n => McpToolNames.Writing.Select(McpToolNames.ClaudeCodeName).Contains(n));
        Assert.Contains(McpToolNames.ClaudeCodeName(McpToolNames.ReadOnly[1]), allow);
    }

    [Fact]
    public void AllowReadTools_KeepsWriteApprovals()
    {
        using var sb = WithClaude();
        Assert.True(ClaudeCodeExtras.PreapproveTools(true).Ok);
        SetAllow(sb, Allow(sb).Where(n => n != McpToolNames.ClaudeCodeName(McpToolNames.ReadOnly[0])));

        Assert.True(ClaudeCodeExtras.AllowReadTools().Ok);

        Assert.True(ClaudeCodeExtras.AreToolsPreapproved());
        Assert.True(ClaudeCodeExtras.AreWriteToolsPreapproved()); // включение чтения не снимает запись
    }
}
