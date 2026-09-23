using System.Text;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>local_symbols, local_search_code и local_find_context на маленьком C#-проекте во временной рабочей папке (без модели).</summary>
[Collection("AppPaths")]
public class NavigationToolsTests
{
    private const string OrderService =
        """
        namespace Shop;

        public sealed class OrderService(IRepo repo)
        {
            public void Submit(Order order)
            {
                Validate(order);
                repo.Save(order);
            }

            private static void Validate(Order order)
            {
                if (order.Id <= 0) throw new ArgumentException("bad id");
            }
        }

        """;

    private const string Repo =
        """
        namespace Shop
        {
            public sealed class Repo : IRepo
            {
                public void Save(Order order)
                {
                    Console.WriteLine(order.Id);
                }
            }
        }

        """;

    private const string IRepo =
        """
        namespace Shop
        {
            public interface IRepo
            {
                void Save(Order order);
            }
        }

        """;

    private const string Order =
        """
        public sealed record Order(int Id);

        """;

    private const string OrderServiceTests =
        """
        namespace Shop.Tests;

        public class OrderServiceTests
        {
            [Fact]
            public void Submit_SavesOrder()
            {
                var service = new OrderService(new Repo());
                var order = new Order(1);
                service.Submit(order);
            }
        }

        """;

    private static void WriteProject(TestEnv env)
    {
        env.WriteFile("src/OrderService.cs", OrderService);
        env.WriteFile("src/Repo.cs", Repo);
        env.WriteFile("src/IRepo.cs", IRepo);
        env.WriteFile("src/Order.cs", Order);
        env.WriteFile("tests/OrderServiceTests.cs", OrderServiceTests);
    }

    private static Task<string> RunSymbols(TestEnv env, string action, string? name = null, string? path = null, string[]? paths = null, int depth = 0, int max = 0) =>
        SymbolsTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), action, name, path, paths, depth, max);

    private static Task<string> Search(TestEnv env, string query, string mode = "text", bool caseSensitive = false, int contextLines = 0, int max = 0,
        bool filesOnly = false, string[]? paths = null) =>
        SearchCodeTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), query, mode, paths, caseSensitive, contextLines, max, filesOnly);

    private static string[] Lines(string text) => text.Replace("\r", "").Split('\n');

    // ───────────────────────── local_symbols ─────────────────────────

    [Fact]
    public async Task Outline_ListsDeclarationsWithRangesAndSignatures()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "outline", path: "src/OrderService.cs");

        Assert.StartsWith("src/OrderService.cs (15 lines, CSharp)", r);
        Assert.Contains("\n1-1 namespace Shop\n", r);
        Assert.Contains("\n3-15 class OrderService\n", r);
        Assert.Contains("\n  5-9 method Submit: public void Submit(Order order)\n", r);
        Assert.Contains("\n  11-14 method Validate: private static void Validate(Order order)\n", r);
    }

    [Fact]
    public async Task Outline_NameCanBeUsedAsPath_MissingFileThrows()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "outline", name: "src/Repo.cs");
        Assert.Contains("class Repo", r);

        await Assert.ThrowsAsync<ToolException>(() => RunSymbols(env, "outline", path: "src/Missing.cs"));
        await Assert.ThrowsAsync<ToolException>(() => RunSymbols(env, "outline"));
    }

    [Fact]
    public async Task Find_ExactNameFirst_AndTruncates()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "find", name: "Order");
        var lines = Lines(r);
        Assert.Equal("src/Order.cs:1 record Order", lines[0]);
        Assert.Contains("src/OrderService.cs:3 class OrderService", r);
        Assert.Contains("tests/OrderServiceTests.cs:3 class OrderServiceTests", r);
        Assert.Contains("files indexed", r);

        var limited = await RunSymbols(env, "find", name: "Order", max: 1);
        Assert.Contains("… more (raise max_results)", limited);
        Assert.DoesNotContain("OrderService", limited);

        Assert.Contains("No symbols matching \"Zebra\"", await RunSymbols(env, "find", name: "Zebra"));
    }

    [Fact]
    public async Task Definition_QualifiedName()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "definition", name: "OrderService.Submit");
        Assert.Contains("src/OrderService.cs:5-9 method OrderService.Submit", r);
        Assert.Contains("    public void Submit(Order order)", r);
        Assert.DoesNotContain("Submit_SavesOrder", r);

        Assert.Contains("No definition of \"Nope\" found", await RunSymbols(env, "definition", name: "Nope"));
        await Assert.ThrowsAsync<ToolException>(() => RunSymbols(env, "definition"));
    }

    [Fact]
    public async Task References_ExcludeDefinitionAndReportEnclosingMethod()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "references", name: "Validate");
        Assert.Contains("defined at: src/OrderService.cs:11", r);
        Assert.Contains("  7: Validate(order);   [in OrderService.Submit]", r);
        Assert.DoesNotContain("11: private static void Validate", r);
        Assert.Contains("1 reference(s)", r);
    }

    [Fact]
    public async Task References_AcrossFiles_SkipDefinitionLine()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "references", name: "Save");
        // Объявлен в интерфейсе и реализован в Repo — обе строки считаются определениями, а не ссылками.
        Assert.Contains("src/Repo.cs:5", Lines(r).First(l => l.StartsWith("defined at:", StringComparison.Ordinal)));
        Assert.Contains("src/IRepo.cs:5", Lines(r).First(l => l.StartsWith("defined at:", StringComparison.Ordinal)));
        Assert.Contains("repo.Save(order);   [in OrderService.Submit]", r);
        Assert.DoesNotContain("public void Save(Order order)", r);
    }

    [Fact]
    public async Task Implementations_InterfaceToClass()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "implementations", name: "IRepo");
        Assert.Contains("src/Repo.cs:3 class Repo", r);
        Assert.Contains("public sealed class Repo : IRepo", r);
        Assert.DoesNotContain("OrderService", r);
        Assert.EndsWith("1 found", Lines(r.Split("\n\n")[0]).Last());
    }

    [Fact]
    public async Task Implementations_FileScopedNamespaceIsNotASubtype()
    {
        using var env = new TestEnv();
        WriteProject(env);
        // Объявление класса попадает в «шапку» из трёх строк, начиная с «namespace Shop;» — пространство имён не должно считаться подтипом.
        env.WriteFile("src/SqlRepo.cs", "namespace Shop;\n\npublic sealed class SqlRepo : IRepo\n{\n}\n");

        var r = await RunSymbols(env, "implementations", name: "IRepo");
        Assert.Contains("src/SqlRepo.cs:3 class SqlRepo", r);
        Assert.DoesNotContain("namespace Shop", r);
    }

    [Fact]
    public async Task Implementations_MethodName_ListsSameNamedMembersExceptDefinitions()
    {
        using var env = new TestEnv();
        WriteProject(env);

        // По квалифицированному имени члена интерфейса находятся одноимённые реализации в других типах.
        var r = await RunSymbols(env, "implementations", name: "IRepo.Save");
        Assert.Contains("src/Repo.cs:5 Repo.Save", r);
        Assert.DoesNotContain("src/IRepo.cs", r.Split("\n\n")[0]);
    }

    [Fact]
    public async Task Callers_ValidateCalledFromSubmit()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "callers", name: "Validate");
        var lines = Lines(r);
        Assert.Equal("OrderService.Validate  src/OrderService.cs:11", lines[0]);
        Assert.Equal("  ← OrderService.Submit  src/OrderService.cs:5", lines[1]);
        Assert.Contains("callers are textual references inside functions", r);
    }

    [Fact]
    public async Task Callers_Depth2_ReachesTestMethod()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "callers", name: "Validate", depth: 2);
        Assert.Contains("    ← OrderServiceTests.Submit_SavesOrder  tests/OrderServiceTests.cs:6", r);
    }

    [Fact]
    public async Task Callees_SubmitCallsValidateAndSave()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "callees", name: "Submit");
        var lines = Lines(r);
        Assert.Equal("OrderService.Submit  src/OrderService.cs:5", lines[0]);
        Assert.Contains("  → OrderService.Validate  src/OrderService.cs:11", r);
        Assert.Contains("  → Repo.Save  src/Repo.cs:5", r);
        Assert.DoesNotContain("ArgumentException", r);
        Assert.Contains("callees resolved by name", r);

        Assert.Contains("No function/method named \"Missing\" found.", await RunSymbols(env, "callees", name: "Missing"));
    }

    [Fact]
    public async Task Tests_ByTypeName()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "tests", name: "OrderService");
        Assert.Contains("tests/OrderServiceTests.cs (by name)", r);
        Assert.Contains("  6: OrderServiceTests.Submit_SavesOrder", r);
        Assert.Contains("1 test file(s)", r);

        var byPath = await RunSymbols(env, "tests", path: "src/Repo.cs");
        Assert.Contains("tests/OrderServiceTests.cs", byPath);

        await Assert.ThrowsAsync<ToolException>(() => RunSymbols(env, "tests"));
    }

    [Fact]
    public async Task Slice_ReturnsNumberedBody()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "slice", name: "Submit");
        Assert.Contains("src/OrderService.cs:5-9 method OrderService.Submit\n", r);
        Assert.Contains("5|     public void Submit(Order order)\n", r);
        Assert.Contains("7|         Validate(order);\n", r);
        Assert.Contains("9|     }\n", r);
        Assert.DoesNotContain("10|", r);
        Assert.DoesNotContain("private static void Validate", r);
    }

    [Fact]
    public async Task Api_ListsPublicSurfaceOutsideTests()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await RunSymbols(env, "api");
        Assert.Contains("class OrderService", r);
        Assert.Contains("public void Submit(Order order)", r);
        Assert.Contains("interface IRepo", r);
        Assert.Contains("record Order", r);
        Assert.DoesNotContain("Validate", r);
        Assert.DoesNotContain("tests/OrderServiceTests.cs", r);
    }

    [Fact]
    public async Task UnknownAction_Throws()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var ex = await Assert.ThrowsAsync<ToolException>(() => RunSymbols(env, "bogus", name: "x"));
        Assert.Contains("action must be one of", ex.Message);
    }

    [Fact]
    public async Task EmptyWorkspace_Throws()
    {
        using var env = new TestEnv();
        env.WriteFile("README.md", "# nothing here\n");

        await Assert.ThrowsAsync<ToolException>(() => RunSymbols(env, "find", name: "x"));
    }

    // ───────────────────────── local_search_code ─────────────────────────

    [Fact]
    public async Task SearchText_FindsLineWithNumber_CaseSensitivity()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await Search(env, "repo.save");
        Assert.Contains("src/OrderService.cs\n", r);
        Assert.Contains("  8:         repo.Save(order);\n", r);
        Assert.Contains("1 match shown in 1 file", r);

        Assert.StartsWith("No matches for text \"repo.save\"", await Search(env, "repo.save", caseSensitive: true));
    }

    [Fact]
    public async Task SearchText_ContextLines()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await Search(env, "repo.Save", contextLines: 1);
        Assert.Contains("   7-         Validate(order);\n", r);
        Assert.Contains("  8:         repo.Save(order);\n", r);
        Assert.Contains("   9-     }\n", r);
    }

    [Fact]
    public async Task SearchWord_WholeWordOnly()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await Search(env, "Repo", mode: "word", caseSensitive: true);
        Assert.Contains("src/Repo.cs", r);
        Assert.Contains("tests/OrderServiceTests.cs", r);
        Assert.DoesNotContain("src/IRepo.cs", r);
        Assert.DoesNotContain("src/OrderService.cs", r);
    }

    [Fact]
    public async Task SearchRegex_MatchesPattern_InvalidRegexThrows()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await Search(env, @"void\s+Sav\w*\(", mode: "regex");
        Assert.Contains("src/Repo.cs", r);
        Assert.Contains("src/IRepo.cs", r);
        Assert.DoesNotContain("src/OrderService.cs", r);

        var ex = await Assert.ThrowsAsync<ToolException>(() => Search(env, "(unclosed", mode: "regex"));
        Assert.Contains("Invalid regex", ex.Message);
        await Assert.ThrowsAsync<ToolException>(() => Search(env, "x", mode: "fuzzy"));
        await Assert.ThrowsAsync<ToolException>(() => Search(env, "  "));
    }

    [Fact]
    public async Task SearchFile_ByNameAndGlob()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await Search(env, "Repo.cs", mode: "file");
        var lines = Lines(r);
        Assert.Equal("src/Repo.cs  (10 lines)", lines[0]);
        Assert.Equal("src/IRepo.cs  (7 lines)", lines[1]);

        var glob = await Search(env, "*Tests.cs", mode: "file");
        Assert.Equal("tests/OrderServiceTests.cs  (12 lines)", glob);

        Assert.StartsWith("No files match \"Nope.cs\"", await Search(env, "Nope.cs", mode: "file"));
    }

    [Fact]
    public async Task SearchFilesOnly_ListsFilesWithCounts()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await Search(env, "Order", caseSensitive: true, filesOnly: true);
        Assert.Contains("src/OrderService.cs (", r);
        Assert.Contains("tests/OrderServiceTests.cs (", r);
        Assert.DoesNotContain(": ", r.Split("\n\n")[0]);
    }

    [Fact]
    public async Task Search_MaxResults_AddsTruncationNote()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await Search(env, "Order", max: 1);
        Assert.Contains("1 match shown in", r);
        Assert.Contains("(limit max_results=1 reached; narrow the query or paths)", r);
    }

    [Fact]
    public async Task Search_TestFilesAfterSources()
    {
        using var env = new TestEnv();
        WriteProject(env);

        var r = await Search(env, "Submit", caseSensitive: true);
        Assert.True(r.IndexOf("src/OrderService.cs", StringComparison.Ordinal) < r.IndexOf("tests/OrderServiceTests.cs", StringComparison.Ordinal),
            "исходники должны идти раньше тестов");
    }

    [Fact]
    public async Task Search_NeverReadsSecretFiles()
    {
        using var env = new TestEnv();
        WriteProject(env);
        env.WriteFile(".env", "API_TOKEN=SUPERSECRET_9f3a\n");
        env.WriteFile("secrets.json", "{ \"token\": \"SUPERSECRET_9f3a\" }\n");
        env.WriteFile("config/.env", "SUPERSECRET_9f3a\n");
        env.WriteFile("src/Config.cs", "public static class Config { public const string Marker = \"SUPERSECRET_9f3a\"; }\n");

        var r = await Search(env, "SUPERSECRET_9f3a");
        Assert.Contains("src/Config.cs", r);
        Assert.Contains("1 match shown in 1 file", r);
        foreach (var line in Lines(r))
        {
            Assert.False(line.StartsWith(".env", StringComparison.Ordinal) || line.StartsWith("config/.env", StringComparison.Ordinal)
                || line.StartsWith("secrets.json", StringComparison.Ordinal), $"секретный файл попал в результаты: {line}");
            Assert.DoesNotContain("API_TOKEN", line);
        }

        // И в режиме файлов, и при явном пути секреты не читаются.
        var files = await Search(env, "secrets", mode: "file");
        Assert.StartsWith("No files match", files);
        var explicitPath = await Record.ExceptionAsync(() => Search(env, "SUPERSECRET_9f3a", paths: [".env"]));
        if (explicitPath is null)
            Assert.StartsWith("No matches", await Search(env, "SUPERSECRET_9f3a", paths: [".env"]));
        else
            Assert.IsType<ToolException>(explicitPath);
    }

    // ───────────────────────── local_find_context ─────────────────────────

    [Fact]
    public void ExtractTerms_KeepsIdentifiersDropsStopWords()
    {
        var terms = FindContextTool.ExtractTerms("Add retry to OrderService.Submit when Repo save fails");

        Assert.Contains("OrderService", terms);
        Assert.Contains("Submit", terms);
        Assert.Contains("Repo", terms);
        Assert.Contains("retry", terms);
        // CamelCase раскладывается на части.
        Assert.Contains("Order", terms);
        Assert.Contains("Service", terms);
        Assert.DoesNotContain("Add", terms);
        Assert.DoesNotContain("when", terms);
        Assert.DoesNotContain("to", terms);
        Assert.Equal(terms.Count, terms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ExtractTerms_QuotedPhrasesAndLimit()
    {
        var terms = FindContextTool.ExtractTerms("Rename `parse_header` in the \"TokenCache\" module");
        Assert.Equal("parse_header", terms[0]);
        Assert.Contains("TokenCache", terms);
        Assert.Contains("parse", terms);
        Assert.Contains("header", terms);

        var many = FindContextTool.ExtractTerms(string.Join(" ", Enumerable.Range(0, 100).Select(i => "Word" + i)));
        Assert.Equal(30, many.Count);
    }

    [Fact]
    public async Task FindContextRank_PutsOrderServiceFirst()
    {
        using var env = new TestEnv();
        WriteProject(env);
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);

        var r = await FindContextTool.RunAsync(ctx, "Add retry to OrderService.Submit when Repo save fails", null, 5, 0, "rank", useModel: false);

        var lines = Lines(r);
        var header = Array.IndexOf(lines, "files by relevance:");
        Assert.True(header >= 0, r);
        Assert.StartsWith("  src/OrderService.cs  — matches", lines[header + 1]);
        Assert.StartsWith("terms: ", lines[0]);
        Assert.DoesNotContain("\n## ", r);
    }

    [Fact]
    public async Task FindContextPack_IncludesNumberedSubmitBody()
    {
        using var env = new TestEnv();
        WriteProject(env);
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);

        var r = await FindContextTool.RunAsync(ctx, "Add retry to OrderService.Submit when Repo save fails", null, 2, 800, "pack", useModel: false);

        Assert.Contains("## src/OrderService.cs (15 lines)", r);
        Assert.Contains("5|     public void Submit(Order order)\n", r);
        Assert.Contains("8|         repo.Save(order);\n", r);
        Assert.Contains("related tests: tests/OrderServiceTests.cs", r);
        Assert.True(ctx.Stats.FilesRead > 0, "статистика прочитанных файлов не заполнена");
    }

    [Fact]
    public async Task FindContextPack_RespectsBudgetRoughly()
    {
        using var env = new TestEnv();
        WriteProject(env);
        // Крупный файл с множеством подходящих методов: пакет обязан уложиться в бюджет, а не выгрузить всё.
        var big = new StringBuilder("namespace Shop;\n\npublic sealed class OrderArchive\n{\n");
        for (var i = 0; i < 300; i++)
            big.Append($"    public void SubmitOrder{i}(Order order)\n    {{\n        Repo.Save(order); // retry {i}\n        Repo.Save(order);\n    }}\n\n");
        big.Append("}\n");
        env.WriteFile("src/OrderArchive.cs", big.ToString());
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);
        const string task = "Add retry to OrderService.Submit when Repo save fails";

        var small = await FindContextTool.RunAsync(ctx, task, null, 8, 800, "pack", useModel: false);
        var large = await FindContextTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), task, null, 8, 15000, "pack", useModel: false);

        Assert.True(Tokens.Estimate(small) < 800 * 2, $"пакет на 800 токенов слишком велик: ≈{Tokens.Estimate(small)} токенов");
        Assert.True(small.Length < large.Length, "больший бюджет должен давать больше контекста");
        Assert.True(Tokens.Estimate(large) < 15000 * 2, $"пакет на 15000 токенов слишком велик: ≈{Tokens.Estimate(large)} токенов");
    }

    [Fact]
    public async Task FindContextPlan_WithoutModel_ReturnsPackWithoutPlan()
    {
        using var env = new TestEnv();
        WriteProject(env);
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);

        var r = await FindContextTool.RunAsync(ctx, "Add retry to OrderService.Submit when Repo save fails", null, 5, 800, "plan", useModel: false);

        Assert.Contains("## src/OrderService.cs", r);
        Assert.Contains("5|     public void Submit(Order order)", r);
        Assert.DoesNotContain("implementation plan", r);
    }

    [Fact]
    public async Task FindContext_InvalidInput_Throws()
    {
        using var env = new TestEnv();
        WriteProject(env);
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ToolException>(() => FindContextTool.RunAsync(ctx, "OrderService", null, 5, 800, "draw", useModel: false));
        await Assert.ThrowsAsync<ToolException>(() => FindContextTool.RunAsync(ctx, " ", null, 5, 800, "pack", useModel: false));
        await Assert.ThrowsAsync<ToolException>(() => FindContextTool.RunAsync(ctx, "do it to me", null, 5, 800, "pack", useModel: false));
    }

    [Fact]
    public async Task FindContext_NoMatches_SaysSo()
    {
        using var env = new TestEnv();
        WriteProject(env);
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);

        var r = await FindContextTool.RunAsync(ctx, "quaternion slerp interpolation", null, 5, 800, "pack", useModel: false);
        Assert.StartsWith("Nothing in the project matches the task terms (quaternion, slerp, interpolation)", r);
    }
}
