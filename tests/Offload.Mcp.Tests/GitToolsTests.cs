using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Инструменты поверх git: local_git_history, local_impact, local_security_review и local_job list.</summary>
[Collection("AppPaths")]
public sealed class GitToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string N(string s) => s.ReplaceLineEndings("\n");

    private static readonly string OrderServiceV1 = N("""
        namespace Shop;

        public sealed class OrderService
        {
            public decimal Total(decimal price, int qty)
            {
                return price * qty;
            }
        }
        """);

    private static readonly string OrderServiceV2 = N("""
        namespace Shop;

        public sealed class OrderService
        {
            public decimal Total(decimal price, int qty)
            {
                if (qty <= 0) return 0;
                return price * qty;
            }
        }
        """);

    private static readonly string Checkout = N("""
        namespace Shop;

        public sealed class Checkout
        {
            public decimal Pay(OrderService service)
            {
                return service.Total(10m, 2);
            }
        }
        """);

    private static readonly string OrderServiceTests = N("""
        namespace Shop.Tests;

        public sealed class OrderServiceTests
        {
            public void Total_MultipliesPriceByQuantity()
            {
                var service = new OrderService();
                var total = service.Total(1m, 2);
            }
        }
        """);

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
        await GitAsync(env, "init", "-q");
        await GitAsync(env, "config", "core.autocrlf", "false");
        await GitAsync(env, "config", "user.name", "Test");
        await GitAsync(env, "config", "user.email", "test@example.com");
        await GitAsync(env, "config", "commit.gpgsign", "false");
    }

    /// <summary>Три коммита: chore (README) → feat (сервис, вызывающий, тест) → fix (тело Total меняется).</summary>
    private static async Task SeedHistoryAsync(TestEnv env)
    {
        await InitRepoAsync(env);
        env.WriteFile("README.md", "# Shop\n");
        await CommitAllAsync(env, "chore: initial layout");
        env.WriteFile("src/OrderService.cs", OrderServiceV1);
        env.WriteFile("src/Checkout.cs", Checkout);
        env.WriteFile("tests/OrderServiceTests.cs", OrderServiceTests);
        await CommitAllAsync(env, "feat: add orders");
        env.WriteFile("src/OrderService.cs", OrderServiceV2);
        await CommitAllAsync(env, "fix(api): null check");
    }

    private static Task<string> HistoryAsync(TestEnv env, string action, string? path = null, string? symbol = null, string? lines = null,
        string? query = null, string? range = null) =>
        GitHistoryTool.RunAsync(env.Context(ct: Ct), action, path, symbol, lines, query, range, null, useModel: false, 15, language: null);

    // ───────────── local_git_history ─────────────

    [Fact]
    public async Task History_File_ListsCommitsOfFile()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await HistoryAsync(env, "file", path: "src/OrderService.cs");

        Assert.StartsWith("history of src/OrderService.cs:", text);
        Assert.Contains("Test: fix(api): null check", text);
        Assert.Contains("Test: feat: add orders", text);
        Assert.DoesNotContain("initial layout", text);
        Assert.True(text.IndexOf("fix(api)", StringComparison.Ordinal) < text.IndexOf("feat:", StringComparison.Ordinal), "новые коммиты — первыми");
    }

    [Fact]
    public async Task History_Symbol_ListsCommitsThatChangedMethodBody()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await HistoryAsync(env, "symbol", path: "src/OrderService.cs", symbol: "Total");

        Assert.StartsWith("history of OrderService.Total (src/OrderService.cs:5-9):", text);
        Assert.Contains("fix(api): null check", text);
        Assert.Contains("feat: add orders", text);
        Assert.DoesNotContain("initial layout", text);
    }

    [Fact]
    public async Task History_UnknownSymbol_Throws()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var ex = await Assert.ThrowsAsync<ToolException>(() => HistoryAsync(env, "symbol", symbol: "NoSuchMethod"));
        Assert.Contains("NoSuchMethod", ex.Message);
    }

    [Fact]
    public async Task History_Blame_GroupsLinesByCommit()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await HistoryAsync(env, "blame", path: "src/OrderService.cs", lines: "1-10");

        Assert.StartsWith("blame of src/OrderService.cs:1-10 (10 lines) grouped by commit:", text);
        Assert.Contains("Test: fix(api): null check — lines 7 (1)", text);
        Assert.Contains("Test: feat: add orders — lines 1-6,8-10 (9)", text);
    }

    [Fact]
    public async Task History_BlameBySymbol_CoversMethodLines()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await HistoryAsync(env, "blame", path: "src/OrderService.cs", symbol: "Total");

        Assert.StartsWith("blame of src/OrderService.cs:5-9 (5 lines)", text);
        Assert.Contains("fix(api): null check — lines 7 (1)", text);
        Assert.Contains("feat: add orders — lines 5-6,8-9 (4)", text);
    }

    [Fact]
    public async Task History_BlameWithoutLines_UsesDefaultRangeOnShortFile()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        // Без lines берётся диапазон 1-400, а файл короче — git должен обрезать диапазон, а не падать.
        var text = await HistoryAsync(env, "blame", path: "src/OrderService.cs");

        Assert.Contains("grouped by commit", text);
        Assert.Contains("fix(api): null check", text);
    }

    [Fact]
    public async Task History_Related_FindsCommitByWordInMessage()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await HistoryAsync(env, "related", query: "orders");

        var byMessage = text[..text.IndexOf("commits that added/removed", StringComparison.Ordinal)];
        Assert.Contains("commits mentioning \"orders\" in the message:", byMessage);
        Assert.Contains("feat: add orders", byMessage);
        Assert.DoesNotContain("null check", byMessage);
    }

    [Fact]
    public async Task History_Related_FindsCommitThatAddedCode()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await HistoryAsync(env, "related", query: "qty <= 0");

        var byCode = text[text.IndexOf("commits that added/removed", StringComparison.Ordinal)..];
        Assert.Contains("fix(api): null check", byCode);
        Assert.Contains("src/OrderService.cs", byCode);
    }

    [Fact]
    public async Task History_Related_RejectsOptionLikeQuery()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        await Assert.ThrowsAsync<ToolException>(() => HistoryAsync(env, "related", query: "--all"));
    }

    [Fact]
    public async Task History_Changelog_GroupsByConventionalType()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await HistoryAsync(env, "changelog", range: "HEAD~2..HEAD");

        Assert.StartsWith("changes in HEAD~2..HEAD:", text);
        Assert.Contains("feat:\n  - add orders (", text);
        Assert.Contains("fix:\n  - api: null check (", text);
        Assert.DoesNotContain("initial layout", text);
        Assert.True(text.IndexOf("feat:", StringComparison.Ordinal) < text.IndexOf("fix:", StringComparison.Ordinal), "feat идёт перед fix");
    }

    [Fact]
    public async Task History_Changelog_RejectsUnsafeRange()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        await Assert.ThrowsAsync<ToolException>(() => HistoryAsync(env, "changelog", range: "--output=x"));
    }

    [Fact]
    public async Task History_NotGitRepository_Throws()
    {
        using var env = new TestEnv();
        env.WriteFile("a.cs", "class A { }\n");

        var ex = await Assert.ThrowsAsync<ToolException>(() => HistoryAsync(env, "file", path: "a.cs"));
        Assert.Contains("not a git repository", ex.Message);
    }

    // ───────────── local_impact ─────────────

    private static async Task SeedImpactAsync(TestEnv env)
    {
        await SeedHistoryAsync(env);
        env.WriteFile("src/OrderService.cs", OrderServiceV2.Replace("return price * qty;", "return price * qty * 1.0m;"));
        env.WriteFile("src/NewThing.cs", "namespace Shop;\n\npublic sealed class NewThing\n{\n}\n");
    }

    [Fact]
    public async Task ChangedRanges_All_IncludesModifiedLinesAndUntrackedFiles()
    {
        using var env = new TestEnv();
        await SeedImpactAsync(env);

        var ranges = await ImpactTool.ChangedRangesAsync(env.Context(ct: Ct), "all");

        Assert.Equal(new[] { "src/NewThing.cs", "src/OrderService.cs" }, ranges.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { 8 }, ranges["src/OrderService.cs"]);
        Assert.Equal(1, ranges["src/NewThing.cs"][0]);
    }

    [Fact]
    public async Task ChangedRanges_SecretFilesAreHidden()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);
        env.WriteFile(".env", "TOKEN=1\n");

        var ranges = await ImpactTool.ChangedRangesAsync(env.Context(ct: Ct), "all");

        Assert.DoesNotContain(".env", ranges.Keys);
    }

    [Fact]
    public async Task ChangedRanges_InvalidRef_Throws()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        await Assert.ThrowsAsync<ToolException>(() => ImpactTool.ChangedRangesAsync(env.Context(ct: Ct), "--output=x"));
    }

    [Fact]
    public async Task Impact_ListsChangedMethodCallerAndRelatedTest()
    {
        using var env = new TestEnv();
        await SeedImpactAsync(env);

        var text = await ImpactTool.RunAsync(env.Context(ct: Ct), "all", null, runTests: false, 40, 0);

        Assert.Contains("changed files (2)", text);
        Assert.Contains("OrderService.Total [method] src/OrderService.cs:5", text);
        Assert.Contains("OrderService.Total ← Checkout.Pay (src/Checkout.cs:7)", text);
        Assert.Contains("related tests (1 files):", text);
        Assert.Contains("tests/OrderServiceTests.cs [OrderServiceTests", text);
        Assert.Contains("run_tests=true", text);
    }

    [Fact]
    public async Task Impact_BySymbol_WorksWithoutDiff()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await ImpactTool.RunAsync(env.Context(ct: Ct), null, "OrderService.Total", runTests: false, 40, 0);

        Assert.Contains("OrderService.Total [method]", text);
        Assert.Contains("Checkout.Pay", text);
        Assert.Contains("tests/OrderServiceTests.cs", text);
    }

    [Fact]
    public async Task Impact_NoChanges_SaysSo()
    {
        using var env = new TestEnv();
        await SeedHistoryAsync(env);

        var text = await ImpactTool.RunAsync(env.Context(ct: Ct), "all", null, runTests: false, 40, 0);

        Assert.Equal("No changes found for target=all.", text);
    }

    private static void WriteTestProject(TestEnv env)
    {
        env.WriteFile("src/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        env.WriteFile("tests/Shop.Tests.csproj", N("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """));
        env.WriteFile("tests/OrderServiceTests.cs", OrderServiceTests);
    }

    [Fact]
    public async Task TestCommands_Mtp_UsesProjectAndFilterClass()
    {
        using var env = new TestEnv();
        WriteTestProject(env);
        env.WriteFile("global.json", "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }\n");
        var ctx = env.Context(ct: Ct);
        var (map, _) = await ProjectMap.BuildAsync(ctx, Ct);
        var tests = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tests/OrderServiceTests.cs"] = ["OrderServiceTests"],
        };

        var commands = ImpactTool.TestCommands(ctx, map, tests);

        Assert.Equal("dotnet test --project tests/Shop.Tests.csproj --filter-class \"*OrderServiceTests\"", Assert.Single(commands));
    }

    [Fact]
    public async Task TestCommands_VsTest_UsesFullyQualifiedNameFilter()
    {
        using var env = new TestEnv();
        WriteTestProject(env);
        var ctx = env.Context(ct: Ct);
        var (map, _) = await ProjectMap.BuildAsync(ctx, Ct);
        var tests = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tests/OrderServiceTests.cs"] = [],
        };

        var commands = ImpactTool.TestCommands(ctx, map, tests);

        Assert.Equal("dotnet test tests/Shop.Tests.csproj --filter FullyQualifiedName~OrderServiceTests", Assert.Single(commands));
    }

    // ───────────── local_security_review ─────────────

    [Fact]
    public void SecurityLines_ParsesHunkWithNewLineNumbers()
    {
        const string diff = "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -10,3 +10,4 @@ class X\n ctx\n-old\n+new1\n+new2\n ctx2\n@@ -40 +41,2 @@\n+tail1\n+tail2\n";

        var lines = SecurityReviewTool.Lines(diff).ToList();

        Assert.Equal(
            new (int, char, string)[] { (11, '-', "old"), (11, '+', "new1"), (12, '+', "new2"), (41, '+', "tail1"), (42, '+', "tail2") },
            lines.Select(l => (l.Line, l.Op, l.Text)).ToArray());
    }

    [Fact]
    public void SecurityLines_AddedLineStartingWithPlusPlus_IsNotTreatedAsFileHeader()
    {
        // Добавленная строка «++counter;» в diff выглядит как «+++counter;»: это не заголовок файла (он только до первого @@).
        const string diff = "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1,3 @@\n ctx\n+++counter;\n+eval(x);\n";

        var lines = SecurityReviewTool.Lines(diff).ToList();

        Assert.Equal(
            new (int, char, string)[] { (2, '+', "++counter;"), (3, '+', "eval(x);") },
            lines.Select(l => (l.Line, l.Op, l.Text)).ToArray());
    }

    [Fact]
    public async Task SecurityReview_ReportsShellExecutionAndMaskedSecret()
    {
        using var env = new TestEnv();
        await InitRepoAsync(env);
        const string before = """
            namespace Shop;

            public static class Runner
            {
                public static void Run(string input)
                {
                }
            }
            """;
        env.WriteFile("src/Runner.cs", N(before));
        await CommitAllAsync(env, "feat: runner");
        env.WriteFile("src/Runner.cs", N(before).Replace("    {\n    }\n", "    {\n        var p = Process.Start(\"cmd.exe\", \"/c \" + input);\n        var password = \"hunter2hunter2\";\n    }\n"));

        var text = await SecurityReviewTool.RunAsync(env.Context(ct: Ct), "all", useModel: false, 50);

        Assert.Contains("1 changed file(s) +2", text);
        Assert.Contains("src/Runner.cs:7 shell-exec", text);
        Assert.Contains("src/Runner.cs:7 process-start", text);
        Assert.Contains("src/Runner.cs:8 hardcoded-secret", text);
        Assert.DoesNotContain("hunter2hunter2", text);
        Assert.Contains("pass****", text);
    }

    [Fact]
    public async Task SecurityReview_ScansUntrackedFilesAndExcludesSecretFiles()
    {
        using var env = new TestEnv();
        await InitRepoAsync(env);
        env.WriteFile("README.md", "# x\n");
        await CommitAllAsync(env, "chore: init");
        env.WriteFile("tools/run.py", "import subprocess\nsubprocess.run(cmd, shell=True)\n");
        env.WriteFile(".env", "PASSWORD=hunter2hunter2\n");

        var text = await SecurityReviewTool.RunAsync(env.Context(ct: Ct), "all", useModel: false, 50);

        Assert.Contains("tools/run.py:2 shell-exec", text);
        Assert.DoesNotContain("hunter2hunter2", text);
    }

    [Fact]
    public async Task SecurityReview_NoChanges_NoFindings()
    {
        using var env = new TestEnv();
        await InitRepoAsync(env);
        env.WriteFile("README.md", "# x\n");
        await CommitAllAsync(env, "chore: init");

        var text = await SecurityReviewTool.RunAsync(env.Context(ct: Ct), null, useModel: false, 50);

        Assert.Contains("0 changed file(s)", text);
        Assert.Contains("rule-based checks: no findings", text);
    }

    // ───────────── local_job ─────────────

    [Fact]
    public async Task JobList_EmptyWorkspace_SaysNoJobs()
    {
        using var env = new TestEnv();

        var text = await JobTool.RunAsync(env.Context(ct: Ct), null, "list", 0, null, false, false, 0);

        Assert.Equal("No Offload jobs for this workspace yet.", text);
    }

    [Fact]
    public async Task Job_MissingId_Throws()
    {
        using var env = new TestEnv();

        var ex = await Assert.ThrowsAsync<ToolException>(() => JobTool.RunAsync(env.Context(ct: Ct), null, "status", 0, null, false, false, 0));
        Assert.Contains("job_id is required", ex.Message);
    }
}
