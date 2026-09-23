using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Построчные правила CodeRules (секреты, опасные API) и инструмент local_code_scan по видам проверок.</summary>
[Collection("AppPaths")]
public sealed class CodeScanTests
{
    // Правдоподобные значения без слов-заглушек (example/test/fake/…), иначе фильтр плейсхолдеров их пропустит.
    private const string AwsKey = "AKIA" + "Q3EGRXZJ7N2LMP4K";
    private const string GithubToken = "gh" + "p_R8mK2qW9zL4vN7bX1cY6hJ3dF5gT0pAsQeUo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string N(string s) => s.ReplaceLineEndings("\n");

    // ───────────── CodeRules ─────────────

    [Fact]
    public void Secrets_RealLookingAwsKey_IsFoundAndMasked()
    {
        var hit = Assert.Single(CodeRules.Secrets($"const string Key = \"{AwsKey}\";"));

        Assert.Equal("aws-key", hit.Rule);
        Assert.Equal("high", hit.Severity);
        Assert.DoesNotContain(AwsKey, hit.Message);
        Assert.Contains("AKIA*", hit.Message);
    }

    [Theory]
    [InlineData("var key = \"AKIAIOSFODNN7EXAMPLE\";")]
    [InlineData("password = \"changeme123\"")]
    [InlineData("api_key: \"your-api-key-here\"")]
    [InlineData("token = \"gh" + "p_0123456789abcdefghijklmnopqrstuvwxyzTEST\"")]
    public void Secrets_Placeholders_AreIgnored(string line)
    {
        Assert.Empty(CodeRules.Secrets(line));
    }

    [Fact]
    public void Secrets_GithubToken_IsFound()
    {
        Assert.Contains(CodeRules.Secrets("token: " + GithubToken), h => h.Rule == "github-token");
    }

    [Theory]
    [InlineData(AwsKey)]
    [InlineData(GithubToken)]
    [InlineData("abcdefg")]
    [InlineData("secret")]
    public void Mask_NeverReturnsTheFullSecret(string value)
    {
        var masked = CodeRules.Mask(value);

        Assert.DoesNotContain(value, masked);
        Assert.Contains("*", masked);
    }

    [Fact]
    public void Mask_KeepsPrefixAndLength()
    {
        Assert.Equal("AKIA************ (20 chars)", CodeRules.Mask(AwsKey));
        Assert.Equal("****", CodeRules.Mask("abc"));
    }

    [Fact]
    public void RedactLine_HidesSecretValueButKeepsRestOfLine()
    {
        var line = $"var client = new S3(\"{AwsKey}\", region);";

        var redacted = CodeRules.RedactLine(line);

        Assert.DoesNotContain(AwsKey, redacted);
        Assert.StartsWith("var client = new S3(\"AKIA", redacted);
        Assert.EndsWith("\", region);", redacted);
        // Заглушки не трогаем.
        Assert.Equal("x = \"AKIAIOSFODNN7EXAMPLE\"", CodeRules.RedactLine("x = \"AKIAIOSFODNN7EXAMPLE\""));
    }

    [Fact]
    public void Unsafe_DetectsLanguageSpecificApis()
    {
        Assert.Contains(CodeRules.Unsafe(CodeLang.CSharp, "var f = new BinaryFormatter();"), h => h.Rule == "binaryformatter" && h.Severity == "high");
        Assert.Contains(CodeRules.Unsafe(CodeLang.CSharp, "Process.Start(fileName);"), h => h.Rule == "process-start");
        Assert.Contains(CodeRules.Unsafe(CodeLang.Python, "subprocess.run(cmd, shell=True)"), h => h.Rule == "shell-exec");
        Assert.Contains(CodeRules.Unsafe(CodeLang.TypeScript, "const r = eval(userInput);"), h => h.Rule == "eval");
    }

    [Fact]
    public void Unsafe_RulesAreScopedToTheirLanguage()
    {
        Assert.Empty(CodeRules.Unsafe(CodeLang.TypeScript, "var f = new BinaryFormatter();"));
        Assert.Empty(CodeRules.Unsafe(CodeLang.CSharp, "subprocess.run(cmd, shell=True)"));
        Assert.Empty(CodeRules.Unsafe(CodeLang.TypeScript, "obj.eval(x);"));
        Assert.Empty(CodeRules.Unsafe(CodeLang.Python, "subprocess.run(cmd, shell=False)"));
    }

    // ───────────── CodeScanTool ─────────────

    [Fact]
    public async Task Todo_CountsCommentMarkersOnly()
    {
        using var env = new TestEnv();
        env.WriteFile("src/A.cs", N("""
            // TODO: split this class
            public class A
            {
                public string M() => "TODO";
                /* FIXME: broken on empty input */
            }
            """));

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "todo", null, 0, false);

        Assert.StartsWith("2 markers:", text);
        Assert.Contains("src/A.cs:1", text);
        Assert.Contains("src/A.cs:5", text);
        Assert.DoesNotContain("src/A.cs:4", text);
    }

    [Fact]
    public async Task Todo_NoMarkers_SaysSo()
    {
        using var env = new TestEnv();
        env.WriteFile("src/A.cs", "public class A { }\n");

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "todo", null, 0, false);

        Assert.StartsWith("No TODO/FIXME/HACK markers found.", text);
    }

    [Fact]
    public async Task Secrets_OutputIsMasked()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Aws.cs", N($$"""
            public static class Aws
            {
                public const string Key = "{{AwsKey}}";
            }
            """));
        env.WriteFile("scripts/deploy.py", $"TOKEN = '{GithubToken}'\n");

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "secrets", null, 0, false);

        Assert.Contains("src/Aws.cs:3 aws-key", text);
        Assert.Contains("scripts/deploy.py:1 github-token", text);
        Assert.DoesNotContain(AwsKey, text);
        Assert.DoesNotContain(GithubToken, text);
        Assert.DoesNotContain(GithubToken[4..], text);
    }

    [Fact]
    public async Task Secrets_EnvAndSecretFilesAreNeverScanned()
    {
        using var env = new TestEnv();
        env.WriteFile(".env", $"AWS_KEY={AwsKey}\n");
        env.WriteFile(".env.local", $"AWS_KEY={AwsKey}\n");
        env.WriteFile("secrets.json", $"{{ \"aws\": \"{AwsKey}\" }}\n");
        env.WriteFile("src/Ok.cs", "public class Ok { }\n");

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "secrets", null, 0, true);

        Assert.StartsWith("No possible secrets found.", text);
        Assert.DoesNotContain(".env:1", text);
        Assert.DoesNotContain("secrets.json:1", text);
        Assert.DoesNotContain(AwsKey, text);
    }

    [Fact]
    public async Task UnsafeApi_ReportsRiskyCallsButNotComments()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Runner.cs", N("""
            public static class Runner
            {
                public static void Run(string file)
                {
                    // Process.Start(file) was here before
                    System.Diagnostics.Process.Start(file);
                    var f = new BinaryFormatter();
                }
            }
            """));
        env.WriteFile("tools/run.py", "import subprocess\nsubprocess.run(cmd, shell=True)\n");
        env.WriteFile("web/app.js", "const r = eval(userInput);\n");

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "unsafe_api", null, 0, false);

        Assert.Contains("src/Runner.cs:6 process-start", text);
        Assert.Contains("src/Runner.cs:7 binaryformatter", text);
        Assert.Contains("tools/run.py:2 shell-exec", text);
        Assert.Contains("web/app.js:1 eval", text);
        Assert.DoesNotContain("src/Runner.cs:5", text);
    }

    [Fact]
    public async Task DeadCode_ReportsUnusedPrivateMethodOnly()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Svc.cs", N("""
            namespace Demo;

            internal sealed class Svc
            {
                public int Run()
                {
                    return Helper();
                }

                private int Helper()
                {
                    return 1;
                }

                private int NeverCalledAnywhere()
                {
                    return 2;
                }
            }
            """));

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "dead_code", null, 0, false);

        Assert.Contains("method Svc.NeverCalledAnywhere", text);
        Assert.DoesNotContain("Helper", text);
    }

    [Fact]
    public async Task Duplicates_FindsSharedBlockInTwoFiles()
    {
        using var env = new TestEnv();
        const string block = """
                    var subtotal = order.Price * order.Quantity;
                    var discount = subtotal > 1000 ? subtotal * 0.05m : 0m;
                    var shipping = order.Express ? 25m : order.Weight * 1.5m;
                    var taxable = subtotal - discount + shipping;
                    var tax = Math.Round(taxable * order.TaxRate, 2);
                    var total = taxable + tax;
                    order.Total = total;
                    logger.LogInformation("Order total computed: {Total}", total);
            """;
        env.WriteFile("src/Billing.cs", N($$"""
            public sealed class Billing
            {
                public void Compute(Order order)
                {
            {{block}}
                }
            }
            """));
        env.WriteFile("src/Checkout.cs", N($$"""
            public sealed class Checkout
            {
                public void Finish(Order order, bool notify)
                {
                    if (notify) Notify(order);
            {{block}}
                }
            }
            """));
        env.WriteFile("src/Other.cs", "public sealed class Other { public int X => 1; }\n");

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "duplicates", null, 0, false);

        Assert.Contains("duplicated blocks", text);
        Assert.Contains("src/Billing.cs:5", text);
        Assert.Contains("src/Checkout.cs:6", text);
        Assert.DoesNotContain("src/Other.cs", text);
    }

    [Fact]
    public async Task Duplicates_NoRepeats_SaysSo()
    {
        using var env = new TestEnv();
        env.WriteFile("src/A.cs", "public sealed class A { public int X => 1; }\n");

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "duplicates", null, 0, false);

        Assert.StartsWith("No duplicated blocks", text);
    }

    [Fact]
    public async Task Complexity_BranchyFunctionRanksFirst()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Rules.cs", N("""
            public static class Rules
            {
                public static int Plain(int x)
                {
                    return x + 1;
                }

                public static int Branchy(int x, bool a, bool b)
                {
                    if (x > 10) return 1;
                    if (x > 20 && a) return 2;
                    if (x > 30 || b) return 3;
                    for (var i = 0; i < x; i++)
                    {
                        if (i == 5) return 4;
                        while (a && i > 2) { a = false; }
                    }
                    switch (x)
                    {
                        case 1: return 5;
                        case 2: return 6;
                    }
                    return 0;
                }
            }
            """));

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "complexity", null, 0, false);

        var branchy = text.IndexOf("Rules.Branchy", StringComparison.Ordinal);
        var plain = text.IndexOf("Rules.Plain", StringComparison.Ordinal);
        Assert.True(branchy >= 0 && plain >= 0, text);
        Assert.True(branchy < plain, "самая сложная функция должна быть первой:\n" + text);
        Assert.Contains("functions: 2", text);
    }

    [Fact]
    public async Task Generated_DetectsAutoGeneratedHeaderAndSuffix()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Api.cs", N("""
            //------------------------------------------------------------------------------
            // <auto-generated>
            //     This code was generated by a tool.
            // </auto-generated>
            //------------------------------------------------------------------------------
            public partial class Api { }
            """));
        env.WriteFile("src/Resources.g.cs", "public partial class Resources { }\n");
        env.WriteFile("src/Handwritten.cs", "public class Handwritten { }\n");

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "generated", null, 0, false);

        Assert.StartsWith("2 generated files", text);
        Assert.Contains("src/Api.cs", text);
        Assert.Contains("src/Resources.g.cs", text);
        Assert.DoesNotContain("Handwritten", text);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData(null)]
    public async Task RunAsync_InvalidCheck_Throws(string? check)
    {
        using var env = new TestEnv();

        var ex = await Assert.ThrowsAsync<ToolException>(() => CodeScanTool.RunAsync(env.Context(ct: Ct), check, null, 0, false));
        Assert.Contains("check must be one of", ex.Message);
    }

    [Fact]
    public async Task Hotspots_NotGitRepository_SuggestsComplexity()
    {
        using var env = new TestEnv();
        env.WriteFile("src/A.cs", "public class A { }\n");

        var text = await CodeScanTool.RunAsync(env.Context(ct: Ct), "hotspots", null, 0, false);

        Assert.StartsWith("hotspots need git history", text);
    }
}
