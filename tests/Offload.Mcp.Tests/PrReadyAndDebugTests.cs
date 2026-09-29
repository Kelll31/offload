using System.Text.Json;
using ModelContextProtocol.Protocol;
using Offload.Core;
using Offload.Mcp.Http;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>local_pr_ready (готовность ветки к PR) и local_debug (воспроизведение → диагностика → история → исправление).</summary>
[Collection("AppPaths")]
public sealed class PrReadyAndDebugTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ───────────── помощники ─────────────

    private static async Task<string> GitAsync(TestEnv env, params string[] args)
    {
        var r = await Git.RunAsync(env.Workspace, args, Ct);
        Assert.True(r.Success, $"git {string.Join(' ', args)}: {r.StdErr}");
        return r.StdOut.Trim();
    }

    private static async Task CommitAllAsync(TestEnv env, string message)
    {
        await GitAsync(env, "add", "-A");
        await GitAsync(env, "commit", "-q", "-m", message);
    }

    private static async Task InitRepoAsync(TestEnv env)
    {
        await GitAsync(env, "init", "-q", "-b", "main");
        await GitAsync(env, "config", "core.autocrlf", "false");
        await GitAsync(env, "config", "user.name", "Test");
        await GitAsync(env, "config", "user.email", "test@example.com");
        await GitAsync(env, "config", "commit.gpgsign", "false");
    }

    private const string Calc = "namespace Shop;\n\npublic static class Calc\n{\n    public static int Add(int a, int b) => a + b;\n}\n";

    /// <summary>main: README + Calc; ветка feature — изменения из <paramref name="onFeature"/>.</summary>
    private static async Task SeedBranchAsync(TestEnv env, Action onFeature)
    {
        await InitRepoAsync(env);
        env.WriteFile("README.md", "# Shop\n");
        env.WriteFile("notes.txt", "base\n");
        env.WriteFile("src/Calc.cs", Calc);
        await CommitAllAsync(env, "chore: initial");
        await GitAsync(env, "checkout", "-q", "-b", "feature");
        onFeature();
        await CommitAllAsync(env, "feat: feature work");
    }

    private static PrReadyOutput Structured(ToolContext ctx) => Assert.IsType<PrReadyOutput>(ctx.Structured);

    // ───────────── local_pr_ready ─────────────

    [Fact]
    public async Task PrReady_CleanBranch_Ready()
    {
        using var env = new TestEnv();
        await SeedBranchAsync(env, () => env.WriteFile("src/Calc.cs", Calc.Replace("a + b;", "a + b;\n    public static int Sub(int a, int b) => a - b;")));
        var ctx = env.Context(ct: Ct);

        var text = await PrReadyTool.RunAsync(ctx, null, runTests: false, useModel: false, prText: false, 5);

        Assert.StartsWith("PR readiness: READY · feature → main", text);
        var o = Structured(ctx);
        Assert.Equal(PrReadyTool.Ready, o.Verdict);
        Assert.Equal("main", o.Base);
        Assert.Equal(1, o.Ahead);
        Assert.Equal(0, o.Behind);
        Assert.Equal(1, o.FilesChanged);
        Assert.Equal("clean", o.ConflictCheck);
        Assert.Empty(o.Blockers);
        Assert.Empty(o.Checks);
        Assert.Contains("no project code was run", text);
    }

    [Fact]
    public async Task PrReady_ConflictSecretMarkerTodoDebug_NotReadyWithBlockersFirst()
    {
        using var env = new TestEnv();
        await SeedBranchAsync(env, () =>
        {
            env.WriteFile("notes.txt", "feature side\n");
            env.WriteFile("src/Calc.cs", Calc.Replace("a + b;",
                "a + b;\n    // TODO: handle overflow\n    public static void Dump() { Console.WriteLine(\"x\"); var password = \"hunter2hunter2\"; }"));
            env.WriteFile("docs/merge.txt", "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> other\n");
        });
        // База ушла вперёд и меняет ту же строку — конфликт; ветка отстаёт на один коммит.
        await GitAsync(env, "checkout", "-q", "main");
        env.WriteFile("notes.txt", "main side\n");
        await CommitAllAsync(env, "docs: notes");
        await GitAsync(env, "checkout", "-q", "feature");
        env.WriteFile("scratch.txt", "uncommitted\n");
        var ctx = env.Context(ct: Ct);

        var text = await PrReadyTool.RunAsync(ctx, "main", runTests: false, useModel: false, prText: false, 5);

        Assert.StartsWith("PR readiness: NOT READY", text);
        Assert.True(text.IndexOf("blockers", StringComparison.Ordinal) < text.IndexOf("details:", StringComparison.Ordinal), "блокеры — до подробностей");
        var o = Structured(ctx);
        Assert.Equal(PrReadyTool.NotReady, o.Verdict);
        Assert.Equal("conflicts", o.ConflictCheck);
        Assert.Contains("notes.txt", o.Conflicts);
        Assert.Contains(o.Blockers, b => b.Contains("merge conflicts with main", StringComparison.Ordinal));
        Assert.Contains(o.Blockers, b => b.Contains("possible secret added at src/Calc.cs", StringComparison.Ordinal));
        Assert.Contains(o.Blockers, b => b.Contains("merge conflict marker left in docs/merge.txt:1", StringComparison.Ordinal));
        Assert.Contains(o.Warnings, w => w.Contains("TODO/FIXME/HACK", StringComparison.Ordinal));
        Assert.Contains(o.Warnings, w => w.Contains("debug leftover", StringComparison.Ordinal));
        Assert.Contains(o.Warnings, w => w.Contains("uncommitted", StringComparison.Ordinal));
        Assert.Contains(o.Warnings, w => w.Contains("1 commit(s) behind main", StringComparison.Ordinal));
        Assert.Equal(1, o.Behind);
        Assert.DoesNotContain("hunter2hunter2", text);
        // Рабочее дерево не тронуто: ветка та же, незакоммиченный файл на месте, конфликт не «применён».
        Assert.Equal("feature", await GitAsync(env, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("feature side\n", File.ReadAllText(env.PathOf("notes.txt")));
        Assert.True(File.Exists(env.PathOf("scratch.txt")));
    }

    [Fact]
    public async Task PrReady_NoCommitsAhead_NotReady()
    {
        using var env = new TestEnv();
        await InitRepoAsync(env);
        env.WriteFile("README.md", "# x\n");
        await CommitAllAsync(env, "init");
        await GitAsync(env, "checkout", "-q", "-b", "feature");
        var ctx = env.Context(ct: Ct);

        await PrReadyTool.RunAsync(ctx, null, runTests: true, useModel: true, prText: true, 5);

        var o = Structured(ctx);
        Assert.Equal(PrReadyTool.NotReady, o.Verdict);
        Assert.Contains(o.Blockers, b => b.Contains("no commits ahead of main", StringComparison.Ordinal));
        Assert.Empty(o.Checks);
    }

    [Fact]
    public async Task PrReady_WithModel_ReviewWarningsAndPrDraftWithoutAttribution()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        llama.Responder = req =>
        {
            var system = FakeLlamaServer.SystemText(req);
            if (system.Contains("pull request", StringComparison.Ordinal))
                return "Title: Add subtraction to Calc\n\n## Summary\nAdds Sub.\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n🤖 Generated with Claude Code\n\n## Risks\n- none";
            return "[high] src/Calc.cs:6 - Sub may overflow - use checked arithmetic";
        };
        await SeedBranchAsync(env, () => env.WriteFile("src/Calc.cs", Calc.Replace("a + b;", "a + b;\n    public static int Sub(int a, int b) => a - b;")));
        var ctx = env.Context(ct: Ct);

        var text = await PrReadyTool.RunAsync(ctx, "main", runTests: false, useModel: true, prText: true, 5);

        var o = Structured(ctx);
        Assert.Equal(PrReadyTool.ReadyWithWarnings, o.Verdict);
        Assert.Contains(o.Warnings, w => w.StartsWith("review (local model", StringComparison.Ordinal) && w.Contains("overflow", StringComparison.Ordinal));
        Assert.Equal("Add subtraction to Calc", o.PrTitle);
        Assert.Contains("## Summary", o.PrBody);
        Assert.DoesNotContain("Co-Authored-By", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Generated with", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Co-Authored-By", o.PrBody!, StringComparison.OrdinalIgnoreCase);
        // Ревью — по диапазону ветки (base...HEAD).
        Assert.Contains(llama.Requests, r => r.Contains("Sub(int a, int b)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrReady_BaseResolution_AutoDetectsMainAndRejectsRanges()
    {
        using var env = new TestEnv();
        await SeedBranchAsync(env, () => env.WriteFile("a.txt", "a\n"));
        var ctx = env.Context(ct: Ct);

        Assert.Equal("main", await PrReadyTool.ResolveBaseAsync(ctx, env.Workspace, null));
        await Assert.ThrowsAsync<ToolException>(() => PrReadyTool.ResolveBaseAsync(ctx, env.Workspace, "main..HEAD"));
        await Assert.ThrowsAsync<ToolException>(() => PrReadyTool.ResolveBaseAsync(ctx, env.Workspace, "--output=x"));
        var ex = await Assert.ThrowsAsync<ToolException>(() => PrReadyTool.ResolveBaseAsync(ctx, env.Workspace, "no-such-branch"));
        Assert.Contains("no-such-branch", ex.Message);
    }

    [Fact]
    public void PrReady_StripAttribution_RemovesAiLinesOnly()
    {
        var text = "Title: Fix parser\n\n## Summary\nFixes the parser generated by ANTLR.\nCo-authored-by: Claude <x@y>\n🤖 Generated with [Claude Code](https://claude.ai)\nWritten by an AI assistant\n## Testing\n- unit tests";
        var clean = PrReadyTool.StripAttribution(text);
        Assert.Contains("Fixes the parser generated by ANTLR.", clean);
        Assert.Contains("## Testing", clean);
        Assert.DoesNotContain("Co-authored-by", clean);
        Assert.DoesNotContain("Claude", clean);
        Assert.DoesNotContain("AI assistant", clean);
    }

    [Theory]
    [InlineData("src/A.cs", "        Console.WriteLine(x);", "console output (C#)")]
    [InlineData("src/Program.cs", "        Console.WriteLine(x);", null)]
    [InlineData("src/A.cs", "        Debugger.Break();", "Debugger.Break/Launch")]
    [InlineData("web/a.ts", "  console.log(x);", "console.log")]
    [InlineData("web/a.ts", "  console.error(x);", null)]
    [InlineData("web/a.ts", "  debugger;", "debugger statement")]
    [InlineData("app/svc.py", "    breakpoint()", "breakpoint/pdb")]
    [InlineData("app/svc.py", "    print(x)", "print()")]
    [InlineData("scripts/tool.py", "    print(x)", null)]
    [InlineData("src/lib.rs", "    dbg!(x);", "dbg!")]
    [InlineData("README.md", "console.log(x)", null)]
    public void PrReady_DebugLeftover_IsLanguageAwareAndConservative(string path, string line, string? expected)
    {
        Assert.Equal(expected, PrReadyTool.DebugLeftover(Symbols.LangOf(path), path, line));
    }

    [Fact]
    public void HttpPolicy_PrReadyAndDebugAlwaysBlocked()
    {
        static Dictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        // pr_ready и без тестов запускает git status / merge-tree (драйверы и фильтры из .git/config) — по HTTP только с --allow-exec.
        Assert.NotNull(HttpExecPolicy.Refusal(McpToolNames.PrReady, Args("{}")));
        Assert.NotNull(HttpExecPolicy.Refusal(McpToolNames.PrReady, Args("{\"run_tests\":true}")));
        Assert.NotNull(HttpExecPolicy.Refusal(McpToolNames.PrReady, Args("{\"run_tests\":false}")));
        Assert.NotNull(HttpExecPolicy.Refusal(McpToolNames.PrReady, Args("{\"run_tests\":\"false\"}")));
        Assert.Contains(McpToolNames.PrReady, HttpExecPolicy.AlwaysBlocked);
        Assert.NotNull(HttpExecPolicy.Refusal(McpToolNames.Debug, Args("{\"fix\":false}")));
        Assert.Contains(McpToolNames.Debug, BackgroundJobSpec.HostableTools);
    }

    // ───────────── local_debug ─────────────

    [Fact]
    public void Debug_FailingTests_ParsedFromDotnetPytestJest()
    {
        var parsed = DebugTool.FailingTests(
            "  Failed Shop.Tests.OrderServiceTests.Total_Multiplies [12 ms]\n" +
            "failed Shop.Tests.CalcTests.Add (5ms)\n" +
            "FAILED tests/test_calc.py::test_add - assert 3 == 4\n" +
            " FAIL src/calc.test.ts\n" +
            "Failed!  - Failed:     1, Passed:     5\n");
        Assert.Contains(("OrderServiceTests", (string?)null), parsed);
        Assert.Contains(("CalcTests", (string?)null), parsed);
        Assert.Contains(((string?)null, "tests/test_calc.py"), parsed);
        Assert.Contains(((string?)null, "src/calc.test.ts"), parsed);
        Assert.Equal(4, parsed.Count);
    }

    [Fact]
    public async Task Debug_CommandPasses_CouldNotReproduce_Stops()
    {
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["findstr *"]);
        env.WriteFile("a.txt", "all good\n");
        var ctx = env.Context(ct: Ct);

        var text = await DebugTool.RunAsync(ctx, "NullReferenceException somewhere", "findstr /c:\"good\" a.txt", fix: true, "apply", null, 5, false);

        Assert.Contains("could not reproduce", text);
        Assert.Contains("Stopped", text);
        var o = Assert.IsType<DebugOutput>(ctx.Structured);
        Assert.False(o.Reproduced);
        Assert.Equal("given", o.CommandSource);
        Assert.Equal("not_attempted", o.FixStatus);
        Assert.Null(o.JobId);
        Assert.Equal("reproduction", Assert.Single(o.Verification).Scope);
    }

    [Fact]
    public async Task Debug_CommandNotAllowlisted_Refused()
    {
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["findstr *"]);
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            DebugTool.RunAsync(env.Context(ct: Ct), "boom", "powershell -c whoami", fix: false, "apply", null, 5, false));
        Assert.Contains("allowlist", ex.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<ToolException>(() => DebugTool.RunAsync(env.Context(ct: Ct), "boom", null, fix: true, "commit", null, 5, false));
        await Assert.ThrowsAsync<ToolException>(() => DebugTool.RunAsync(env.Context(ct: Ct), "boom", null, fix: true, "apply", ["../outside"], 5, false));
    }

    private const string DivideV1 = "namespace Shop;\n\npublic static class Calc\n{\n    public static int Divide(int a, int b)\n    {\n        return b == 0 ? 0 : a / b;\n    }\n}\n";
    private const string DivideV2 = "namespace Shop;\n\npublic static class Calc\n{\n    public static int Divide(int a, int b)\n    {\n        return a / b;\n    }\n}\n";

    /// <summary>Два коммита: исходный Divide с проверкой → коммит, убравший проверку (его и должна назвать модель).</summary>
    private static async Task<string> SeedDivideAsync(TestEnv env)
    {
        await InitRepoAsync(env);
        env.WriteFile("Calc.cs", DivideV1);
        await CommitAllAsync(env, "feat: calc");
        env.WriteFile("Calc.cs", DivideV2);
        await CommitAllAsync(env, "refactor: simplify divide");
        return await GitAsync(env, "rev-parse", "--short=7", "HEAD");
    }

    private static string Problem(TestEnv env) =>
        "System.DivideByZeroException: Attempted to divide by zero.\n" +
        $"   at Shop.Calc.Divide(Int32 a, Int32 b) in {env.PathOf("Calc.cs")}:line 7\n";

    [Fact]
    public async Task Debug_DiagnosisOnly_ReproducesImplicatesAndNamesSuspectCommit()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port, configure: c => c.Mcp.VerifyCommandAllowlist = ["findstr *"]);
        var sha = await SeedDivideAsync(env);
        string? material = null;
        llama.Responder = req =>
        {
            material = FakeLlamaServer.UserText(req);
            return $"ROOT CAUSE: Calc.Divide no longer guards b == 0 (Calc.cs:7).\nSUSPECT COMMIT: {sha}\nFIX IDEA: restore the zero check\nOPEN QUESTIONS: should it throw instead?";
        };
        var ctx = env.Context(ct: Ct);

        var text = await DebugTool.RunAsync(ctx, Problem(env), "findstr /c:\"b == 0\" Calc.cs", fix: false, "apply", null, 5, false);

        Assert.Contains("Reproduced? YES", text);
        Assert.Contains("Root cause (local model hypothesis, verify): Calc.Divide no longer guards", text);
        Assert.Contains($"Suspect commit: likely introduced by {sha}", text);
        Assert.Contains("refactor: simplify divide", text);
        Assert.Contains("Fix: not attempted (fix=false", text);
        var o = Assert.IsType<DebugOutput>(ctx.Structured);
        Assert.True(o.Reproduced);
        Assert.Contains(o.Implicated, i => i.StartsWith("Calc.cs:7 in ", StringComparison.Ordinal) && i.Contains("Divide", StringComparison.Ordinal));
        Assert.Contains("refactor: simplify divide", o.SuspectCommit);
        Assert.Equal("not_attempted", o.FixStatus);
        Assert.Contains("should it throw instead?", o.OpenQuestions);
        // Модель получила срез кода с отмеченной строкой и историю.
        Assert.NotNull(material);
        Assert.Contains(">    7|         return a / b;", material);
        Assert.Contains("refactor: simplify divide", material);
        Assert.Equal(DivideV2, File.ReadAllText(env.PathOf("Calc.cs"))); // ничего не изменено
    }

    [Fact]
    public async Task Debug_FixWithoutOpenCode_ReportsFailureAfterDiagnosis_NothingChanged()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port, configure: c =>
        {
            c.Mcp.VerifyCommandAllowlist = ["findstr *"];
            c.OpenCode.Enabled = false;
        });
        await SeedDivideAsync(env);
        llama.Responder = _ => "ROOT CAUSE: missing zero check at Calc.cs:7\nSUSPECT COMMIT: none\nFIX IDEA: add it\nOPEN QUESTIONS: none";
        var ctx = env.Context(ct: Ct);

        var text = await DebugTool.RunAsync(ctx, Problem(env), "findstr /c:\"b == 0\" Calc.cs", fix: true, "apply", ["Calc.cs"], 5, false);

        Assert.Contains("Reproduced? YES", text);
        Assert.Contains("Suspect commit: none identified", text);
        Assert.Contains("Fix: failed", text);
        Assert.Contains("OpenCode", text);
        var o = Assert.IsType<DebugOutput>(ctx.Structured);
        Assert.Equal("failed", o.FixStatus);
        Assert.Null(o.JobId);
        Assert.Equal(DivideV2, File.ReadAllText(env.PathOf("Calc.cs")));
    }

    [Fact]
    public async Task Debug_StackFrameIntoSecretFile_IsNotRead()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        env.WriteFile(".env", "API_KEY=topsecretvalue123\n");
        env.WriteFile("Calc.cs", DivideV2);
        var requests = new List<string>();
        llama.Responder = req =>
        {
            lock (requests) requests.Add(FakeLlamaServer.UserText(req));
            return "ROOT CAUSE: unknown\nSUSPECT COMMIT: none\nFIX IDEA: none\nOPEN QUESTIONS: none";
        };
        var ctx = env.Context(ct: Ct);
        // Текст проблемы (из недоверенного источника) указывает кадрами стека на файл-секрет — он не читается и не уходит модели.
        var problem = $"   at Shop.Cfg.Load() in {env.PathOf(".env")}:line 1\n   at Shop.Calc.Divide(Int32 a, Int32 b) in {env.PathOf("Calc.cs")}:line 7\n";

        var text = await DebugTool.RunAsync(ctx, problem, null, fix: false, "apply", null, 5, false);

        var o = Assert.IsType<DebugOutput>(ctx.Structured);
        Assert.DoesNotContain(o.Implicated, i => i.Contains(".env", StringComparison.Ordinal));
        Assert.Contains(o.Implicated, i => i.StartsWith("Calc.cs:7", StringComparison.Ordinal));
        Assert.DoesNotContain("topsecretvalue123", text);
        lock (requests) Assert.DoesNotContain(requests, r => r.Contains("topsecretvalue123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Debug_ViaMcp_StructuredContentAndAnnotations()
    {
        using var env = new TestEnv(configure: c => c.Mcp.VerifyCommandAllowlist = ["findstr *"]);
        env.WriteFile("a.txt", "all good\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);

        var tools = (await h.Client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.ProtocolTool).ToDictionary(t => t.Name);
        Assert.False(tools[McpToolNames.Debug].Annotations!.ReadOnlyHint);
        Assert.False(tools[McpToolNames.Debug].Annotations!.DestructiveHint);
        Assert.False(tools[McpToolNames.Debug].Annotations!.IdempotentHint);
        Assert.Equal(tools[McpToolNames.Verify].Annotations!.ReadOnlyHint, tools[McpToolNames.PrReady].Annotations!.ReadOnlyHint);
        Assert.False(tools[McpToolNames.PrReady].Annotations!.DestructiveHint);
        Assert.NotNull(tools[McpToolNames.PrReady].OutputSchema);
        Assert.NotNull(tools[McpToolNames.Debug].OutputSchema);
        Assert.Contains("\"base\"", tools[McpToolNames.PrReady].InputSchema.GetRawText());

        var res = await h.Client.CallToolAsync(McpToolNames.Debug,
            new Dictionary<string, object?> { ["problem"] = "it breaks", ["command"] = "findstr /c:\"good\" a.txt" }, cancellationToken: Ct);
        Assert.NotEqual(true, res.IsError);
        var sc = res.StructuredContent!.Value;
        Assert.False(sc.GetProperty("reproduced").GetBoolean());
        Assert.Equal("not_attempted", sc.GetProperty("fix_status").GetString());
        Assert.Contains("could not reproduce", string.Join("\n", res.Content.OfType<TextContentBlock>().Select(t => t.Text)));
    }
}
