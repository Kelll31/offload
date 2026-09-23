using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

public sealed class VerifyTests
{
    [Fact]
    public void Scan_CollectsDistinctErrorsWarningsAndSummary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "log.txt");
            File.WriteAllLines(path, [
                "Build started",
                "src/A.cs(12,5): error CS1002: ; expected [C:\\p\\A.csproj]",
                "src/A.cs(12,5): error CS1002: ; expected",
                "src/B.cs(3,1): warning CS0168: unused",
                "src/B.cs(3,1): warning CS0168: unused",
                "    0 Error(s)",
                "Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6"
            ]);

            var r = VerifyTool.Scan(path, CancellationToken.None);

            Assert.Equal(7L, r.Lines);
            Assert.Contains(r.Errors, e => e.Contains("CS1002", StringComparison.Ordinal));
            Assert.Equal(2, r.WarningCount);
            Assert.Single(r.Warnings);
            Assert.Contains(r.Summary, l => l.StartsWith("Failed!", StringComparison.Ordinal));
            Assert.DoesNotContain(r.Errors, e => e.Contains("0 Error(s)", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Scan_CleanLog_HasNoErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "log.txt");
            File.WriteAllLines(path, [
                "Build succeeded.",
                "    0 Warning(s)",
                "    0 Error(s)",
                "Passed!  - Failed:     0, Passed:    12"
            ]);

            var r = VerifyTool.Scan(path, CancellationToken.None);

            Assert.Empty(r.Errors);
            Assert.Equal(0, r.WarningCount);
            Assert.NotEmpty(r.Summary);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Slug_MakesSafeFileNamePart()
    {
        Assert.Equal("dotnet-test-filter-footests", VerifyTool.Slug("dotnet test --filter FooTests"));
        Assert.Equal("run", VerifyTool.Slug("--"));
        var longCmd = new string('a', 200);
        Assert.True(VerifyTool.Slug(longCmd).Length <= 40, "длина slug должна быть не более 40");
    }

    [Fact]
    public void EnsureSelfIgnored_CreatesGitignoreOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
        try
        {
            VerifyTool.EnsureSelfIgnored(dir);
            var gi = Path.Combine(dir, ".gitignore");
            Assert.True(File.Exists(gi), "файл .gitignore должен быть создан");
            Assert.Contains("*", File.ReadAllLines(gi));

            File.WriteAllText(gi, "custom");
            VerifyTool.EnsureSelfIgnored(dir);
            Assert.Equal("custom", File.ReadAllText(gi));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ContextHints_RejectsAbsoluteAndParentPaths()
    {
        var ok = AgentTaskTool.ContextHints(["src/a.cs", "tests\\b.cs"]);
        Assert.Equal(["src/a.cs", "tests/b.cs"], ok);

        Assert.Throws<ToolException>(() => AgentTaskTool.ContextHints(["C:\\x.cs"]));
        Assert.Throws<ToolException>(() => AgentTaskTool.ContextHints(["../x.cs"]));
        Assert.Throws<ToolException>(() => AgentTaskTool.ContextHints(["src/../../x"]));

        Assert.Empty(AgentTaskTool.ContextHints(null));
    }

    [Fact]
    public void ParseRaw_ReadsModesAndPaths()
    {
        var result = GitSandbox.ParseRaw(":100644 100644 aaa bbb M\0src/a.cs\0:000000 120000 000 ccc A\0link\0").ToList();
        Assert.Equal(2, result.Count);
        Assert.Equal("100644", result[0].NewMode);
        Assert.Equal("src/a.cs", result[0].Path);
        Assert.Equal("120000", result[1].NewMode);
        Assert.Equal("link", result[1].Path);
    }
}
