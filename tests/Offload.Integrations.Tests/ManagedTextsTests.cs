using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Integrations.Claude;
using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Tests;

/// <summary>Метка управляемых текстов с версией и хэшем, секции инструкций в AGENTS.md/GEMINI.md, устаревшие умолчания записей.</summary>
public class ManagedTextsTests
{
    private const string Template = "---\nname: x\nx-offload: managed\n---\nbody v1\n";

    // ---------------------------------------------------------------- метка

    [Fact]
    public void Stamp_WritesVersionAndHash_InspectCurrent()
    {
        var stamped = ManagedContent.Stamp(Template, "9.9.9");
        Assert.Matches(@"x-offload: managed v=9\.9\.9 h=[0-9a-f]{16}\n", stamped);
        Assert.Equal(ManagedState.Current, ManagedContent.Inspect(stamped, Template));
        Assert.Equal(ManagedState.Current, ManagedContent.Inspect(stamped.Replace("\n", "\r\n"), Template)); // концы строк не важны
        Assert.Equal(ManagedContent.Hash(Template), ManagedContent.ReadMarker(stamped)!.Value.Hash);
    }

    [Fact]
    public void Inspect_OutdatedModifiedForeignMissingLegacy()
    {
        var old = ManagedContent.Stamp(Template, "1.0.0");
        var newTemplate = Template.Replace("body v1", "body v2");
        Assert.Equal(ManagedState.Outdated, ManagedContent.Inspect(old, newTemplate));
        Assert.Equal(ManagedState.Modified, ManagedContent.Inspect(old.Replace("body v1", "my edits"), newTemplate));
        Assert.Equal(ManagedState.Foreign, ManagedContent.Inspect("no marker here", newTemplate));
        Assert.Equal(ManagedState.Missing, ManagedContent.Inspect(null, newTemplate));
        // Метка без хэша (прежние версии Offload): совпадает с текущим шаблоном — актуален; с известным выпущенным — устарел;
        // любой другой текст — изменён пользователем (молча не перезаписываем).
        Assert.Equal(ManagedState.Current, ManagedContent.Inspect(newTemplate, newTemplate));
        Assert.Equal(ManagedState.Modified, ManagedContent.Inspect(Template, newTemplate));
        Assert.Equal(ManagedState.Outdated, ManagedContent.Inspect(Template, newTemplate, [ManagedContent.Hash(Template)]));
        Assert.Equal(ManagedState.Modified,
            ManagedContent.Inspect(Template.Replace("body v1", "body v1 + my note"), newTemplate, [ManagedContent.Hash(Template)]));
        // Концы строк не влияют на сравнение с выпущенным шаблоном.
        Assert.Equal(ManagedState.Outdated, ManagedContent.Inspect(Template.ReplaceLineEndings("\r\n"), newTemplate, [ManagedContent.Hash(Template)]));
    }

    [Fact]
    public void ShippedHashes_AreKnownAndDifferFromCurrentTemplates()
    {
        foreach (var (hashes, current) in new[]
                 {
                     (ClaudeTexts.SkillShippedHashes, ClaudeTexts.Skill()),
                     (ClaudeTexts.StrongRuleShippedHashes, ClaudeTexts.StrongRule()),
                     (ClaudeTexts.RunnerAgentShippedHashes, ClaudeTexts.RunnerAgent()),
                 })
        {
            Assert.NotEmpty(hashes);
            Assert.All(hashes, h => Assert.Matches("^[0-9a-f]{16}$", h));
            // Текущий шаблон распознаётся как Current и без списка; список — только для текстов прежних версий.
            Assert.Equal(ManagedState.Current, ManagedContent.Inspect(current, current, hashes));
        }
    }

    // ---------------------------------------------------------------- секции AGENTS.md / GEMINI.md

    [Fact]
    public void Section_AppendReplaceRemove_UserTextByteForByte()
    {
        const string user = "# My rules\r\nAlways answer in English.\r\n";
        var template = GuidanceSection.Template();
        var added = GuidanceSection.Apply(user, template)!;
        Assert.StartsWith(user + "\r\n" + GuidanceFile.BeginPrefix, added, StringComparison.Ordinal);
        Assert.DoesNotMatch("[^\r]\n", added); // все концы строк — CRLF, как в файле
        Assert.Null(GuidanceSection.Apply(added, template)); // актуальна — не переписываем

        var removed = GuidanceSection.Remove(added);
        Assert.Equal(user, removed);

        // Секция в середине файла: текст после неё тоже сохраняется.
        var middle = added + "## After\r\ntext\r\n";
        Assert.Equal(user + "\r\n" + "## After\r\ntext\r\n", GuidanceSection.Remove(middle));
    }

    [Fact]
    public void Section_EmptyFile_AndMalformedMarkers()
    {
        var template = GuidanceSection.Template();
        var fresh = GuidanceSection.Apply("", template)!;
        Assert.StartsWith(GuidanceFile.BeginPrefix, fresh, StringComparison.Ordinal);
        Assert.EndsWith(GuidanceFile.EndMarker + "\n", fresh, StringComparison.Ordinal);
        foreach (var tool in new[] { McpToolNames.FindContext, McpToolNames.AskFiles, McpToolNames.Solve })
            Assert.Contains(tool, fresh, StringComparison.Ordinal);
        Assert.Equal("", GuidanceSection.Remove(fresh));

        Assert.Throws<GuidanceSectionException>(() => GuidanceSection.Apply("x\n" + GuidanceFile.BeginPrefix + " -->\nno end\n", template));
        Assert.Throws<GuidanceSectionException>(() => GuidanceSection.Apply(fresh + fresh, template));
        Assert.Throws<GuidanceSectionException>(() => GuidanceSection.Remove("text " + GuidanceFile.BeginPrefix + " -->\n" + GuidanceFile.EndMarker));
    }

    [Fact]
    public void Codex_InstallRefreshRemove_KeepsUserText()
    {
        using var sb = new Sandbox();
        var codex = ClientGuidance.Find("codex")!;
        Assert.False(codex.Install().Ok); // Codex не установлен — файл не создаём
        Assert.False(File.Exists(codex.FilePath));

        sb.Dir(".codex");
        const string user = "# Personal\nUse tabs.\n";
        var path = sb.Write(sb.P(".codex", "AGENTS.md"), user);
        Assert.Equal(ManagedState.Missing, codex.State());

        var r = codex.Install();
        Assert.True(r.Ok, r.Message);
        Assert.NotNull(r.BackupPath);
        Assert.Equal(ManagedState.Current, codex.State());
        Assert.StartsWith(user, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("уже", codex.Install().Message, StringComparison.Ordinal);

        // «Прежняя версия» секции: другой текст с корректным хэшем — обновляется автоматически.
        var text = File.ReadAllText(path);
        var span = GuidanceSection.Find(text)!.Value;
        var oldSection = ManagedContent.Stamp(GuidanceSection.Template().Replace("Keep architecture", "Keep design"), "0.9.0");
        File.WriteAllText(path, text[..span.Start] + oldSection);
        Assert.Equal(ManagedState.Outdated, codex.State());
        Assert.Single(ClientGuidance.RefreshAll(), x => x.Ok);
        Assert.Equal(ManagedState.Current, codex.State());

        // Правка пользователя внутри секции — больше не трогаем.
        File.WriteAllText(path, File.ReadAllText(path).Replace("Keep architecture", "Keep my architecture"));
        Assert.Equal(ManagedState.Modified, codex.State());
        Assert.Empty(ClientGuidance.RefreshAll());
        Assert.Contains("Keep my architecture", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.True(codex.Remove().Ok);
        Assert.Equal(user, File.ReadAllText(path));
        Assert.True(codex.Remove().Ok); // повторно — нечего снимать
    }

    [Fact]
    public void Gemini_SectionOnlyFile_RemovedEntirely()
    {
        using var sb = new Sandbox();
        sb.Dir(".gemini");
        var gemini = ClientGuidance.Find("gemini-cli")!;
        Assert.True(gemini.Install().Ok);
        var path = sb.P(".gemini", "GEMINI.md");
        Assert.True(File.Exists(path));
        Assert.True(gemini.IsInstalled());

        var results = ClientGuidance.RemoveAll();
        Assert.All(results, x => Assert.True(x.Ok, x.Message));
        Assert.False(File.Exists(path)); // в файле не было ничего, кроме секции
    }

    [Fact]
    public void Guidance_MalformedFile_NotTouched()
    {
        using var sb = new Sandbox();
        sb.Dir(".codex");
        var content = "mine\n" + GuidanceFile.BeginPrefix + " x-offload: managed -->\nno end marker\n";
        var path = sb.Write(sb.P(".codex", "AGENTS.md"), content);
        var codex = ClientGuidance.Find("codex")!;
        Assert.Equal(ManagedState.Modified, codex.State());
        Assert.False(codex.Install().Ok);
        Assert.False(codex.Remove().Ok);
        Assert.Equal(content, File.ReadAllText(path));
    }

    // ---------------------------------------------------------------- устаревшие умолчания записи

    private static JsonIntegration TestClient(Sandbox sb, Dictionary<string, string[]>? previous = null) =>
        new("test-client", "Test", null)
        {
            Detect = () => true,
            Targets = () => [sb.P("client", "mcp.json")],
            Container = ["mcpServers"],
            Entry = spec => new JsonObject
            {
                ["command"] = spec.Command,
                ["args"] = JsonIntegration.Strings(spec.Args),
                ["timeout"] = 3600,
                ["autoApprove"] = JsonIntegration.Strings(McpToolNames.ReadOnly),
            },
            RefreshableKeys = ["timeout", "autoApprove"],
            PreviousDefaults = previous ?? new Dictionary<string, string[]>(),
        };

    private static string Entry(Sandbox sb, string command, string timeout, IEnumerable<string> approve) =>
        sb.Write(sb.P("client", "mcp.json"),
            "{\n  // comment\n  \"mcpServers\": {\n    \"offload\": {\n      \"command\": " + JsonValue.Create(command).ToJsonString() +
            ",\n      \"args\": [\"--mcp\"],\n      \"timeout\": " + timeout + ",\n      \"autoApprove\": " +
            JsonIntegration.Strings(approve).ToJsonString() + ",\n      \"disabled\": true\n    }\n  }\n}\n");

    [Fact]
    public async Task StaleAutoApprove_IsOutdated_RefreshAddsNewTools_KeepsUserOnes()
    {
        using var sb = new Sandbox();
        var spec = sb.Spec();
        var client = TestClient(sb);
        // Список прежней версии (без последнего инструмента чтения) + добавленный пользователем инструмент записи.
        var old = McpToolNames.ReadOnly.Take(McpToolNames.ReadOnly.Count - 1).Append(McpToolNames.EditFiles).ToList();
        var path = Entry(sb, spec.Command, "900", old);
        Assert.Equal(IntegrationStatus.Outdated, client.GetStatus(spec));

        var r = await client.RegisterAsync(spec, TestContext.Current.CancellationToken);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(IntegrationStatus.Registered, client.GetStatus(spec));
        var text = File.ReadAllText(path);
        Assert.Contains("// comment", text, StringComparison.Ordinal);
        var entry = JsonNode.Parse(text, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!["mcpServers"]!["offload"]!;
        var approve = entry["autoApprove"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains(McpToolNames.EditFiles, approve);
        Assert.All(McpToolNames.ReadOnly, t => Assert.Contains(t, approve));
        Assert.Equal(900, (int)entry["timeout"]!); // своё значение пользователя
        Assert.True((bool)entry["disabled"]!);
    }

    [Fact]
    public async Task PreviousDefaultTimeout_IsReplaced_CustomIsKept()
    {
        using var sb = new Sandbox();
        var spec = sb.Spec();
        var client = TestClient(sb, new Dictionary<string, string[]> { ["timeout"] = ["600"] });
        var path = Entry(sb, spec.Command, "600", McpToolNames.ReadOnly);
        Assert.Equal(IntegrationStatus.Outdated, client.GetStatus(spec));
        await client.RegisterAsync(spec, TestContext.Current.CancellationToken);
        var entry = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!["mcpServers"]!["offload"]!;
        Assert.Equal(3600, (int)entry["timeout"]!);
        Assert.Equal(IntegrationStatus.Registered, client.GetStatus(spec));

        Entry(sb, spec.Command, "1234", McpToolNames.ReadOnly);
        Assert.Equal(IntegrationStatus.Registered, client.GetStatus(spec));
    }

    [Fact]
    public void AutoApproveWithForeignNames_IsUserList_NotStale()
    {
        using var sb = new Sandbox();
        var spec = sb.Spec();
        var client = TestClient(sb);
        Entry(sb, spec.Command, "3600", [McpToolNames.AskFiles, "some_other_tool"]);
        Assert.Equal(IntegrationStatus.Registered, client.GetStatus(spec));
    }

    [Fact]
    public void Codex_PreviousDefault_IsRewritten()
    {
        using var sb = new Sandbox();
        var spec = sb.Spec();
        var toml = "[mcp_servers.offload]\ncommand = " + TomlDocument.FormatString(spec.Command) +
                   "\nargs = [\"--mcp\"]\nstartup_timeout_sec = 10\ntool_timeout_sec = 999\nenabled = true\n";
        var previous = new Dictionary<string, string[]> { ["startup_timeout_sec"] = ["10"], ["tool_timeout_sec"] = ["60"] };

        Assert.Null(CodexIntegration.BuildRegistered(toml, spec)); // без прежних умолчаний — всё актуально
        var updated = CodexIntegration.BuildRegistered(toml, spec, previous)!;
        Assert.Contains($"startup_timeout_sec = {CodexIntegration.StartupTimeoutSec}", updated, StringComparison.Ordinal);
        Assert.Contains("tool_timeout_sec = 999", updated, StringComparison.Ordinal);
        Assert.Null(CodexIntegration.BuildRegistered(updated, spec, previous));
    }
}
