using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>local_diagnostics по готовому логу: охватывающий символ и строка кода, фильтры severity/paths, кадры стека проекта.</summary>
[Collection("AppPaths")]
public sealed class DiagnosticsToolTests
{
    private const string Source =
        "public class A {\n" +
        "    public int Run() {\n" +
        "        return x;\n" +
        "    }\n" +
        "}\n";

    private static void WriteWorkspace(TestEnv env)
    {
        env.WriteFile("src/A.cs", Source);
        env.WriteFile("build.log", string.Join("\n",
            "  Determining projects to restore...",
            @"src/A.cs(3,9): error CS0103: The name 'x' does not exist in the current context [C:\p\A.csproj]",
            "src/A.cs(2,16): warning CS0168: The variable 'e' is declared but never used [C:\\p\\A.csproj]",
            "lib/B.cs(1,1): error CS1001: Identifier expected",
            "Unhandled exception. System.Exception: boom",
            $"   at A.Run() in {env.PathOf("src/A.cs")}:line 3",
            @"   at Program.Main() in C:\elsewhere\Program.cs:line 10",
            "Build FAILED.") + "\n");
    }

    private static Task<string> Run(TestEnv env, string? severity, string[]? paths = null) =>
        DiagnosticsTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), null, null, "build.log", paths, severity, 0, 0);

    [Fact]
    public async Task Run_FromLog_ShowsEnclosingSymbolAndSourceLine()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var r = await Run(env, null);

        Assert.Contains("2 errors, 1 warnings parsed", r);
        Assert.Contains("src/A.cs", r);
        Assert.Contains("CS0103", r);
        Assert.Contains("[in A.Run]", r);
        Assert.Contains("return x;", r);
    }

    [Fact]
    public async Task Run_SeverityError_HidesWarnings_AllShowsThem()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var errors = await Run(env, "error");
        Assert.DoesNotContain("CS0168", errors);
        Assert.Contains("CS0103", errors);

        var all = await Run(env, "all");
        Assert.Contains("CS0168", all);
        Assert.Contains("CS0103", all);
    }

    [Fact]
    public async Task Run_PathsFilter_KeepsOnlyMatchingFiles()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var unfiltered = await Run(env, "error");
        Assert.Contains("lib/B.cs", unfiltered);

        var filtered = await Run(env, "error", ["src"]);
        Assert.Contains("CS0103", filtered);
        Assert.DoesNotContain("lib/B.cs", filtered);
        Assert.DoesNotContain("CS1001", filtered);
    }

    [Fact]
    public async Task Run_StackFrames_ListOnlyProjectCode()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var r = await Run(env, "error");

        var at = r.IndexOf("stack frames in project code", StringComparison.Ordinal);
        Assert.True(at >= 0, "нет раздела с кадрами стека: " + r);
        var frames = r[at..];
        Assert.Contains("src/A.cs:3", frames);
        Assert.Contains("return x;", frames);
        Assert.DoesNotContain("Program.cs", frames);
    }

    [Fact]
    public async Task Run_InvalidSeverity_Throws()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        await Assert.ThrowsAsync<Offload.Mcp.Infrastructure.ToolException>(() => Run(env, "fatal"));
    }
}
