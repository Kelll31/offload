using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Offload.Core;
using Offload.Core.Config;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Resources;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>
/// Возможности протокола MCP (ROADMAP 7.1, 7.3): outputSchema + structuredContent, ресурсы и resource_link,
/// прогресс с total, elicitation (подтверждение пользователем), completions, notifications/message.
/// </summary>
[Collection("AppPaths")]
public sealed class ProtocolFeaturesTests
{
    private static readonly string[] StructuredTools = [McpToolNames.Verify, McpToolNames.Diagnostics, McpToolNames.Impact, McpToolNames.Status];

    // ───────────────────────── outputSchema + structuredContent ─────────────────────────

    [Fact]
    public async Task ToolsList_StructuredToolsDeclareOutputSchema()
    {
        using var env = new TestEnv();
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var tools = (await h.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.ProtocolTool).ToDictionary(t => t.Name);
        foreach (var name in StructuredTools)
        {
            Assert.True(tools[name].OutputSchema is { } s && s.GetProperty("type").GetString() == "object", $"{name}: нет outputSchema");
            Assert.True(tools[name].OutputSchema!.Value.TryGetProperty("properties", out _), name);
        }
        var verify = tools[McpToolNames.Verify].OutputSchema!.Value.GetProperty("properties");
        foreach (var key in new[] { "command", "status", "exit_code", "log_uri", "errors", "summary" })
            Assert.True(verify.TryGetProperty(key, out _), "verify outputSchema без " + key);
        Assert.True(tools[McpToolNames.Status].OutputSchema!.Value.GetProperty("properties").TryGetProperty("model", out _));
        // Остальные инструменты — только текст.
        Assert.Null(tools[McpToolNames.AskFiles].OutputSchema);
    }

    [Fact]
    public async Task Status_ReturnsStructuredContentMatchingSchema()
    {
        using var env = new TestEnv();
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (schema, res) = await CallWithSchemaAsync(h, McpToolNames.Status, []);
        Assert.NotEqual(true, res.IsError);
        Assert.Contains("local model: OFFLINE", Text(res)); // текст прежний
        var sc = res.StructuredContent!.Value;
        AssertMatchesSchema(schema, sc, "status");
        Assert.Equal("offline", sc.GetProperty("model").GetProperty("state").GetString());
        Assert.Equal(AppInfo.Version, sc.GetProperty("version").GetString());
        Assert.Equal(env.Workspace, sc.GetProperty("workspace")[0].GetString());
    }

    [Fact]
    public async Task Verify_StructuredContent_ResourceLink_AndRunResource()
    {
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["findstr *"]);
        env.WriteFile("a.txt", "line one\npassword = \"SuperSecretValue123\"\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);

        var (schema, res) = await CallWithSchemaAsync(h, McpToolNames.Verify, new() { ["command"] = "findstr /n \"o\" a.txt", ["analyze"] = false });
        Assert.NotEqual(true, res.IsError);
        Assert.Contains("PASSED", Text(res));
        var sc = res.StructuredContent!.Value;
        AssertMatchesSchema(schema, sc, "verify");
        Assert.Equal("passed", sc.GetProperty("status").GetString());
        Assert.Equal(0, sc.GetProperty("exit_code").GetInt32());
        var uri = sc.GetProperty("log_uri").GetString()!;
        Assert.StartsWith("offload://runs/", uri, StringComparison.Ordinal);
        var link = Assert.Single(res.Content.OfType<ResourceLinkBlock>());
        Assert.Equal(uri, link.Uri);

        // Полный лог — ресурсом; секреты замаскированы.
        var read = await h.Client.ReadResourceAsync(uri, cancellationToken: Ct);
        var log = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;
        Assert.Contains("1:line one", log);
        Assert.DoesNotContain("SuperSecretValue123", log);

        // Ошибка команды — структурированный итог failed.
        var (_, failed) = await CallWithSchemaAsync(h, McpToolNames.Verify, new() { ["command"] = "findstr /c:\"NOPE\" a.txt", ["analyze"] = false });
        Assert.NotEqual(true, failed.IsError);
        Assert.Equal("failed", failed.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal(1, failed.StructuredContent!.Value.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task Diagnostics_FromLog_StructuredItems()
    {
        using var env = new TestEnv();
        env.WriteFile("src/A.cs", "class A\n{\n    int x = y;\n}\n");
        env.WriteFile("build.log", "src/A.cs(3,13): error CS0103: The name 'y' does not exist in the current context\nsrc/A.cs(1,7): warning CS1591: Missing XML comment\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (schema, res) = await CallWithSchemaAsync(h, McpToolNames.Diagnostics, new() { ["log_path"] = "build.log", ["severity"] = "warning" });
        Assert.NotEqual(true, res.IsError);
        var sc = res.StructuredContent!.Value;
        AssertMatchesSchema(schema, sc, "diagnostics");
        Assert.Equal(1, sc.GetProperty("error_count").GetInt32());
        Assert.Equal(1, sc.GetProperty("warning_count").GetInt32());
        var first = sc.GetProperty("diagnostics").EnumerateArray().First(d => d.GetProperty("severity").GetString() == "error");
        Assert.Equal("src/A.cs", first.GetProperty("file").GetString());
        Assert.Equal(3, first.GetProperty("line").GetInt32());
        Assert.Equal("CS0103", first.GetProperty("code").GetString());
        Assert.Equal("int x = y;", first.GetProperty("source_line").GetString());
    }

    [Fact]
    public async Task Impact_BySymbol_StructuredContent()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Calc.cs", "namespace Demo;\npublic class Calc\n{\n    public int Add(int a, int b) => a + b;\n}\n");
        env.WriteFile("src/Use.cs", "namespace Demo;\npublic class Use\n{\n    public int Run() => new Calc().Add(1, 2);\n}\n");
        env.WriteFile("tests/CalcTests.cs", "namespace Demo.Tests;\npublic class CalcTests\n{\n    public void AddWorks() { new Calc().Add(1, 2); }\n}\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (schema, res) = await CallWithSchemaAsync(h, McpToolNames.Impact, new() { ["symbol"] = "Calc.Add" });
        Assert.NotEqual(true, res.IsError);
        var sc = res.StructuredContent!.Value;
        AssertMatchesSchema(schema, sc, "impact");
        Assert.Contains(sc.GetProperty("changed_symbols").EnumerateArray(), s => s.GetProperty("name").GetString()!.EndsWith("Add", StringComparison.Ordinal));
        Assert.Contains(sc.GetProperty("callers").EnumerateArray(), c => c.GetProperty("file").GetString() == "src/Use.cs");
        Assert.Contains(sc.GetProperty("related_tests").EnumerateArray(), t => t.GetProperty("file").GetString() == "tests/CalcTests.cs");
        Assert.Equal(0, sc.GetProperty("test_runs").GetArrayLength());
    }

    // ───────────────────────── ресурсы ─────────────────────────

    [Fact]
    public async Task Resources_ListTemplatesAndRead()
    {
        using var env = new TestEnv();
        env.WriteFile("src/App.cs", "class App { static void Main() { } }\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var resources = (await h.Client.ListResourcesAsync(cancellationToken: Ct)).Select(r => r.Uri).ToList();
        Assert.Contains(ResourceUris.ProjectMap, resources);
        Assert.Contains(ResourceUris.Memory, resources);
        var templates = (await h.Client.ListResourceTemplatesAsync(cancellationToken: Ct)).Select(t => t.UriTemplate).ToList();
        Assert.Contains(ResourceUris.RunTemplate, templates);
        Assert.Contains(ResourceUris.JobDiffTemplate, templates);

        var memory = await h.Client.ReadResourceAsync(ResourceUris.Memory, cancellationToken: Ct);
        Assert.Contains("No entries", Assert.IsType<TextResourceContents>(Assert.Single(memory.Contents)).Text);
        var map = await h.Client.ReadResourceAsync(ResourceUris.ProjectMap, cancellationToken: Ct);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<TextResourceContents>(Assert.Single(map.Contents)).Text));
    }

    [Theory]
    [InlineData("offload://runs/not-a-run")]
    [InlineData("offload://runs/20260101-000000-000-..")]
    [InlineData("offload://runs/20260101-000000-000-missing")]
    [InlineData("offload://jobs/..%2F..%2Fx/diff")]
    [InlineData("offload://jobs/20200101-000000-abcd/diff")]
    public async Task Resources_InvalidOrMissingIds_AreRefused(string uri)
    {
        using var env = new TestEnv();
        await using var h = await McpHarness.StartAsync(env.Workspace);
        await Assert.ThrowsAnyAsync<McpException>(() => h.Client.ReadResourceAsync(uri, cancellationToken: Ct).AsTask());
    }

    [Fact]
    public void RunIds_OnlyGeneratedShape()
    {
        Assert.True(ResourceUris.IsValidRunId("20260924-101500-123-dotnet-test"));
        Assert.True(ResourceUris.IsValidRunId(Path.GetFileNameWithoutExtension(ResourceUris.Run(@"C:\p\.offload\runs\20260924-101500-123-run.log")[15..])));
        foreach (var bad in new[] { "", "..", "20260924-101500-123-", "20260924-101500-123-../x", "20260924-101500-123-a\\b", "20260924-101500-123-A", "x-20260924-101500-123-a" })
            Assert.False(ResourceUris.IsValidRunId(bad), bad);
    }

    [Fact]
    public async Task JobDiff_LinkInLocalJob_AndResource_OnlyForThisWorkspace()
    {
        using var env = new TestEnv();
        var file = env.WriteFile("src/a.cs", "line1\n");
        var job = JobStore.Create(McpToolNames.EditFiles, env.Workspace, "task", null);
        JobStore.Snapshot(job, file, "src/a.cs");
        File.WriteAllText(file, "line1\nline2 password = \"SuperSecretValue123\"\n");
        JobStore.Finish(job, JobStatus.Applied);
        await using var h = await McpHarness.StartAsync(env.Workspace);

        var res = await h.Client.CallToolAsync(McpToolNames.Job, new Dictionary<string, object?> { ["action"] = "diff", ["job_id"] = job.Id, ["max_lines"] = 10 },
            cancellationToken: Ct);
        Assert.NotEqual(true, res.IsError);
        var link = Assert.Single(res.Content.OfType<ResourceLinkBlock>());
        Assert.Equal(ResourceUris.JobDiff(job.Id), link.Uri);
        var read = await h.Client.ReadResourceAsync(link.Uri, cancellationToken: Ct);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;
        Assert.Contains("+line2", text);
        Assert.DoesNotContain("SuperSecretValue123", text);

        // Задача другой рабочей области ресурсом не читается.
        var foreign = JobStore.Create(McpToolNames.EditFiles, Path.Combine(Path.GetTempPath(), "other-ws-" + Guid.NewGuid().ToString("N")), "t", null);
        JobStore.Finish(foreign, JobStatus.Applied);
        await Assert.ThrowsAnyAsync<McpException>(() => h.Client.ReadResourceAsync(ResourceUris.JobDiff(foreign.Id), cancellationToken: Ct).AsTask());
    }

    [Fact]
    public void Cap_WithResource_PointsToFullText()
    {
        var capped = ToolRunner.Cap(new string('x', 5000), 1000, "offload://runs/20260924-101500-123-x");
        Assert.True(capped.Length <= 1000);
        Assert.Contains("offload://runs/20260924-101500-123-x", capped);
    }

    // ───────────────────────── прогресс с total ─────────────────────────

    [Fact]
    public async Task AskFiles_MapReduce_ProgressHasTotal()
    {
        using var llama = new FakeLlamaServer { ContextSize = 2048 };
        using var env = new TestEnv(llama.Port);
        for (var i = 0; i < 4; i++)
            env.WriteFile($"m{i}.txt", string.Concat(Enumerable.Range(0, 120).Select(n => $"file {i} line {n} with some filler text\n")));
        llama.Responder = req => FakeLlamaServer.UserText(req).StartsWith("PARTIAL ANSWERS", StringComparison.Ordinal) ? "MERGED" : "part answer";
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var values = new List<ProgressNotificationValue>();
        var progress = new SyncProgress(v => { lock (values) values.Add(v); });
        var res = await h.Client.CallToolAsync(McpToolNames.AskFiles,
            new Dictionary<string, object?> { ["paths"] = new[] { "*.txt" }, ["question"] = "q", ["max_answer_tokens"] = 200 }, progress, cancellationToken: Ct);
        Assert.NotEqual(true, res.IsError);
        List<ProgressNotificationValue> seen;
        lock (values) seen = [.. values];
        var withTotal = seen.Where(v => v.Total is not null).ToList();
        Assert.NotEmpty(withTotal);
        Assert.All(withTotal, v => Assert.True(v.Progress <= v.Total, $"{v.Progress} > {v.Total}"));
        Assert.Contains(withTotal, v => v.Message?.StartsWith("part 1/", StringComparison.Ordinal) == true);
        // Клиент SDK обрабатывает уведомления параллельно, порядок получения не гарантирован — проверяем, что значения не повторяются.
        Assert.True(seen.Select(v => v.Progress).Distinct().Count() == seen.Count,
            "значения прогресса должны строго расти: " + string.Join(", ", seen.Select(v => $"{v.Progress}/{v.Total}:{v.Message}")));
    }

    // ───────────────────────── elicitation ─────────────────────────

    [Fact]
    public async Task Verify_NotAllowlisted_UserAllowsOnce()
    {
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["sort /r*"]);
        env.WriteFile("a.txt", "b\na\n");
        var asked = new List<string>();
        await using var h = await McpHarness.StartAsync(env.Workspace, Elicit(asked, accept: true));
        var (isError, text) = await h.CallAsync(McpToolNames.Verify, new() { ["command"] = "sort a.txt", ["analyze"] = false });
        Assert.False(isError, text);
        Assert.Contains("PASSED", text);
        Assert.Contains("sort a.txt", Assert.Single(asked));
        // Разрешение одноразовое: белый список не изменился.
        Assert.Equal(["sort /r*"], ConfigStore.Reload().Mcp.VerifyCommandAllowlist);
    }

    [Fact]
    public async Task Verify_NotAllowlisted_UserDeclines_OrClientWithoutElicitation()
    {
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["sort /r*"]);
        env.WriteFile("a.txt", "b\na\n");
        var asked = new List<string>();
        await using (var h = await McpHarness.StartAsync(env.Workspace, Elicit(asked, accept: false)))
        {
            var (isError, text) = await h.CallAsync(McpToolNames.Verify, new() { ["command"] = "sort a.txt" });
            Assert.True(isError);
            Assert.Contains("declined", text);
            Assert.Single(asked);
        }
        await using (var h = await McpHarness.StartAsync(env.Workspace))
        {
            var (isError, text) = await h.CallAsync(McpToolNames.Verify, new() { ["command"] = "sort a.txt" });
            Assert.True(isError);
            Assert.Contains("not in the Offload allowlist", text);
        }
    }

    [Fact]
    public async Task Verify_OneTimeAllowDisabledInConfig_NeverAsks()
    {
        using var env = new TestEnv(configure: c =>
        {
            c.Mcp.VerifyCommandAllowlist = ["sort /r*"];
            c.Mcp.AllowOneTimeVerify = false;
        });
        env.WriteFile("a.txt", "b\na\n");
        var asked = new List<string>();
        await using var h = await McpHarness.StartAsync(env.Workspace, Elicit(asked, accept: true));
        var (isError, text) = await h.CallAsync(McpToolNames.Verify, new() { ["command"] = "sort a.txt" });
        Assert.True(isError);
        Assert.Contains("not in the Offload allowlist", text);
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData("sort a.txt & calc")]
    [InlineData("sort a.txt > out.txt")]
    [InlineData("C:\\Windows\\System32\\sort.exe a.txt")]
    [InlineData("sort ..\\secret.txt")]
    [InlineData("dotnet build -p:PreBuildEvent=calc")]
    public async Task Verify_ForbiddenSyntax_NeverOfferedToUser(string command)
    {
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["findstr *"]);
        var asked = new List<string>();
        await using var h = await McpHarness.StartAsync(env.Workspace, Elicit(asked, accept: true));
        var (isError, _) = await h.CallAsync(McpToolNames.Verify, new() { ["command"] = command });
        Assert.True(isError);
        Assert.Empty(asked);
        Assert.Null(VerifyCommand.CandidateForOneTimeAllow(command, ["findstr *"]));
    }

    [Fact]
    public async Task JobRevertForce_AsksUser_WhenLaterEditsWouldBeLost()
    {
        using var env = new TestEnv();
        var file = env.WriteFile("src/a.cs", "original\n");
        var job = JobStore.Create(McpToolNames.EditFiles, env.Workspace, "task", null);
        JobStore.Snapshot(job, file, "src/a.cs");
        File.WriteAllText(file, "by job\n");
        JobStore.Finish(job, JobStatus.Applied);
        File.WriteAllText(file, "edited later\n");

        var asked = new List<string>();
        await using (var h = await McpHarness.StartAsync(env.Workspace, Elicit(asked, accept: false)))
        {
            var (isError, text) = await h.CallAsync(McpToolNames.Job, new() { ["action"] = "revert", ["job_id"] = job.Id, ["force"] = true });
            Assert.True(isError);
            Assert.Contains("declined", text);
            Assert.Contains("src/a.cs", Assert.Single(asked));
            Assert.Equal("edited later\n", File.ReadAllText(file));
        }
        await using (var h = await McpHarness.StartAsync(env.Workspace, Elicit(asked, accept: true)))
        {
            var (isError, text) = await h.CallAsync(McpToolNames.Job, new() { ["action"] = "revert", ["job_id"] = job.Id, ["force"] = true });
            Assert.False(isError, text);
            Assert.Equal("original\n", File.ReadAllText(file));
        }
    }

    [Fact]
    public async Task JobRevertForce_WithoutElicitation_KeepsOldBehavior()
    {
        using var env = new TestEnv();
        var file = env.WriteFile("src/a.cs", "original\n");
        var job = JobStore.Create(McpToolNames.EditFiles, env.Workspace, "task", null);
        JobStore.Snapshot(job, file, "src/a.cs");
        File.WriteAllText(file, "by job\n");
        JobStore.Finish(job, JobStatus.Applied);
        File.WriteAllText(file, "edited later\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.Job, new() { ["action"] = "revert", ["job_id"] = job.Id, ["force"] = true });
        Assert.False(isError, text);
        Assert.Equal("original\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task JobMerge_HeldBackJob_AsksUser()
    {
        using var env = new TestEnv();
        var job = JobStore.Create(McpToolNames.AgentTask, env.Workspace, "big change", null);
        job.Status = JobStatus.PendingMerge;
        job.Notes.Add(JobConfirmation.NotMergedNote + " 12 files changed (max_files=5); the review found critical/high issues");
        JobStore.Save(job);

        var asked = new List<string>();
        await using (var h = await McpHarness.StartAsync(env.Workspace, Elicit(asked, accept: false)))
        {
            var (isError, text) = await h.CallAsync(McpToolNames.Job, new() { ["action"] = "merge", ["job_id"] = job.Id });
            Assert.True(isError);
            Assert.Contains("declined", text);
            Assert.Contains("max_files=5", Assert.Single(asked));
        }
        // Подтверждено — дальше обычное слияние (здесь у задачи нет песочницы, и JobTool так и отвечает).
        await using (var h = await McpHarness.StartAsync(env.Workspace, Elicit(asked, accept: true)))
        {
            var (isError, text) = await h.CallAsync(McpToolNames.Job, new() { ["action"] = "merge", ["job_id"] = job.Id });
            Assert.True(isError);
            Assert.Contains("no sandbox to merge", text);
        }
        await using (var h = await McpHarness.StartAsync(env.Workspace))
        {
            var (_, text) = await h.CallAsync(McpToolNames.Job, new() { ["action"] = "merge", ["job_id"] = job.Id });
            Assert.Contains("no sandbox to merge", text);
        }
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public void JobConfirmation_QuestionOnlyForRiskyCases()
    {
        var job = new JobInfo { Id = "20260924-101500-ab12", Tool = McpToolNames.AgentTask, Status = JobStatus.PendingMerge, Root = "C:\\p", Task = "t" };
        Assert.Null(JobConfirmation.Question(job, "merge", false));
        job.Notes.Add(JobConfirmation.NotMergedNote + " the check failed");
        Assert.Null(JobConfirmation.Question(job, "merge", false)); // проваленная проверка — не повод для вопроса (решает модель)
        job.Notes.Add(JobConfirmation.NotMergedNote + " 9 files changed (max_files=3)");
        Assert.Contains("max_files=3", JobConfirmation.Question(job, "merge", false));
        job.Status = JobStatus.Applied;
        Assert.Null(JobConfirmation.Question(job, "merge", false));
        Assert.Null(JobConfirmation.Question(job, "revert", false));
        Assert.Null(JobConfirmation.Question(null, "merge", false));
    }

    // ───────────────────────── completions и notifications/message ─────────────────────────

    [Fact]
    public async Task Completions_PromptKindsPathsAndRunIds()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Alpha.cs", "class Alpha { }\n");
        env.WriteFile("src/Beta.cs", "class Beta { }\n");
        env.WriteFile(".env", "API_KEY=abc\n");
        env.WriteFile(".offload/runs/20260924-101500-123-dotnet-test.log", "log\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);

        var kinds = await h.Client.CompleteAsync(new PromptReference { Name = OffloadPrompts.Solve }, "kind", "b", cancellationToken: Ct);
        Assert.Equal(["bug"], kinds.Completion.Values);
        var all = await h.Client.CompleteAsync(new PromptReference { Name = OffloadPrompts.Solve }, "kind", "", cancellationToken: Ct);
        Assert.Equal(OffloadCompletions.SolveKinds, all.Completion.Values);

        var paths = await h.Client.CompleteAsync(new PromptReference { Name = OffloadPrompts.Tests }, "path", "src/A", cancellationToken: Ct);
        Assert.Equal(["src/Alpha.cs"], paths.Completion.Values);
        var root = await h.Client.CompleteAsync(new PromptReference { Name = OffloadPrompts.Tests }, "path", "", cancellationToken: Ct);
        Assert.Contains("src/Beta.cs", root.Completion.Values);
        Assert.DoesNotContain(root.Completion.Values, v => v.Contains(".env", StringComparison.Ordinal) || v.Contains(".offload", StringComparison.Ordinal));
        var list = await h.Client.CompleteAsync(new PromptReference { Name = OffloadPrompts.Explain }, "paths", "src/Alpha.cs, src/B", cancellationToken: Ct);
        Assert.Equal(["src/Alpha.cs, src/Beta.cs"], list.Completion.Values);

        var runs = await h.Client.CompleteAsync(new ResourceTemplateReference { Uri = ResourceUris.RunTemplate }, "id", "2026", cancellationToken: Ct);
        Assert.Equal(["20260924-101500-123-dotnet-test"], runs.Completion.Values);
    }

    [Fact]
    public async Task ServerDown_SendsWarningMessage()
    {
        using var env = new TestEnv();
        env.WriteFile("a.cs", "x\n");
        var messages = new List<string>();
        await using var h = await McpHarness.StartAsync(env.Workspace, o =>
        {
            o.Handlers.NotificationHandlers =
            [
                new("notifications/message", (n, _) =>
                {
                    lock (messages) messages.Add(n.Params?.ToJsonString() ?? "");
                    return ValueTask.CompletedTask;
                }),
            ];
        });
        var (isError, _) = await h.CallAsync(McpToolNames.AskFiles, new() { ["paths"] = new[] { "a.cs" }, ["question"] = "q" });
        Assert.True(isError);
        for (var i = 0; i < 50 && messages.Count == 0; i++) await Task.Delay(50, Ct);
        lock (messages) Assert.Contains(messages, m => m.Contains("not running", StringComparison.Ordinal) && m.Contains("warning", StringComparison.Ordinal));
    }

    // ───────────────────────── помощники ─────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static async Task<(JsonElement Schema, CallToolResult Result)> CallWithSchemaAsync(McpHarness h, string tool, Dictionary<string, object?> args)
    {
        var schema = (await h.Client.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == tool).ProtocolTool.OutputSchema!.Value;
        var res = await h.Client.CallToolAsync(tool, args, cancellationToken: Ct);
        Assert.True(res.IsError == true || res.StructuredContent is not null, $"{tool}: нет structuredContent: {Text(res)}");
        return (schema, res);
    }

    /// <summary>Клиент с capability elicitation: отвечает accept+confirm (или decline) и запоминает вопросы.</summary>
    private static Action<McpClientOptions> Elicit(List<string> asked, bool accept) => o =>
    {
        o.Capabilities ??= new ClientCapabilities();
        o.Capabilities.Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() };
        o.Handlers.ElicitationHandler = (p, _) =>
        {
            lock (asked) asked.Add(p?.Message ?? "");
            return ValueTask.FromResult(accept
                ? new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) } }
                : new ElicitResult { Action = "decline" });
        };
    };

    /// <summary>Проверка формы по JSON Schema: обязательные поля есть, типы совпадают (вложенные объекты и массивы — рекурсивно).</summary>
    private static void AssertMatchesSchema(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToList() : [type.GetString()!];
            var actual = value.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                _ => "null",
            };
            Assert.True(types.Contains(actual) || actual == "integer" && types.Contains("number"), $"{path}: тип {actual}, ожидался {string.Join("|", types)}");
        }
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var props))
        {
            if (schema.TryGetProperty("required", out var req))
                foreach (var r in req.EnumerateArray())
                    Assert.True(value.TryGetProperty(r.GetString()!, out _), $"{path}: нет обязательного поля {r.GetString()}");
            foreach (var p in value.EnumerateObject())
            {
                Assert.True(props.TryGetProperty(p.Name, out var ps), $"{path}: поле {p.Name} не описано в схеме");
                AssertMatchesSchema(ps, p.Value, path + "." + p.Name);
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) AssertMatchesSchema(items, item, path + "[]");
    }

    /// <summary>IProgress без SynchronizationContext: значения приходят в порядке отправки.</summary>
    private sealed class SyncProgress(Action<ProgressNotificationValue> onValue) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => onValue(value);
    }
}
