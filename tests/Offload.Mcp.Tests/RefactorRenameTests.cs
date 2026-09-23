using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>local_refactor action=rename: текстовое переименование вне строк/комментариев, файл типа, конфликты, откат.</summary>
[Collection("AppPaths")]
public sealed class RefactorRenameTests
{
    private const string FooSource =
        "namespace App;\n" +
        "\n" +
        "public class Foo\n" +
        "{\n" +
        "    public string Name => \"Foo\"; // Foo in a comment\n" +
        "    public static Foo Create() => new Foo();\n" +
        "}\n";

    private const string UseSource =
        "namespace App;\n" +
        "\n" +
        "public class Use\n" +
        "{\n" +
        "    private readonly Foo _foo = Foo.Create();\n" +
        "    public FooBar Other = new FooBar();\n" +
        "    public string Label = \"Foo\"; // uses Foo\n" +
        "}\n" +
        "\n" +
        "public class FooBar { }\n";

    private static void WriteWorkspace(TestEnv env)
    {
        env.WriteFile("src/Foo.cs", FooSource);
        env.WriteFile("src/Use.cs", UseSource);
    }

    private static Task<string> Rename(TestEnv env, string name, string newName, bool dryRun = false, bool force = false, bool renameFile = true) =>
        RefactorTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), "rename", name, newName, null, null, null, null, null,
            dryRun, force, renameFile);

    private static string JobId(string output)
    {
        var m = Regex.Match(output, @"job_id: (\S+)");
        Assert.True(m.Success, "в ответе нет job_id: " + output);
        return m.Groups[1].Value;
    }

    [Fact]
    public async Task Rename_DryRun_ListsFilesAndWritesNothing()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var r = await Rename(env, "Foo", "Bar", dryRun: true);

        Assert.Contains("dry run", r);
        Assert.Contains("src/Foo.cs", r);
        Assert.Contains("src/Use.cs", r);
        Assert.Contains("src/Bar.cs", r);
        Assert.Equal(FooSource, File.ReadAllText(env.PathOf("src/Foo.cs")));
        Assert.Equal(UseSource, File.ReadAllText(env.PathOf("src/Use.cs")));
        Assert.False(File.Exists(env.PathOf("src/Bar.cs")), "dry_run не должен переименовывать файл");
    }

    [Fact]
    public async Task Rename_Class_UpdatesReferencesAndFileName_SkipsStringsCommentsAndLongerIdentifiers()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var r = await Rename(env, "Foo", "Bar");

        Assert.Contains("job_id:", r);
        Assert.False(File.Exists(env.PathOf("src/Foo.cs")), "Foo.cs должен быть переименован");
        var bar = File.ReadAllText(env.PathOf("src/Bar.cs"));
        Assert.Contains("public class Bar", bar);
        Assert.Contains("public static Bar Create() => new Bar();", bar);
        Assert.Contains("public string Name => \"Foo\"; // Foo in a comment", bar);

        var use = File.ReadAllText(env.PathOf("src/Use.cs"));
        Assert.Contains("private readonly Bar _foo = Bar.Create();", use);
        Assert.Contains("public FooBar Other = new FooBar();", use);
        Assert.Contains("public class FooBar { }", use);
        Assert.Contains("public string Label = \"Foo\"; // uses Foo", use);
    }

    [Fact]
    public async Task Rename_SameSimpleNameDeclaredInAnotherClass_RequiresForce()
    {
        using var env = new TestEnv();
        const string svc =
            "public class Svc\n" +
            "{\n" +
            "    public void Run() { }\n" +
            "}\n" +
            "\n" +
            "public class Other\n" +
            "{\n" +
            "    public void Run() { }\n" +
            "}\n";
        env.WriteFile("src/Svc.cs", svc);

        var ex = await Assert.ThrowsAsync<ToolException>(() => Rename(env, "Svc.Run", "Execute"));
        Assert.Contains("Other.Run", ex.Message);
        Assert.Equal(svc, File.ReadAllText(env.PathOf("src/Svc.cs")));

        await Rename(env, "Svc.Run", "Execute", force: true);
        Assert.DoesNotContain("Run", File.ReadAllText(env.PathOf("src/Svc.cs")));
    }

    [Fact]
    public async Task Rename_Method_LeavesForeignQualifiedCallsAlone()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Worker.cs",
            "public class Worker\n" +
            "{\n" +
            "    public void Run() { }\n" +
            "    public void Start()\n" +
            "    {\n" +
            "        Run();\n" +
            "        this.Run();\n" +
            "        System.Threading.Tasks.Task.Run(() => { });\n" +
            "    }\n" +
            "}\n");

        var r = await Rename(env, "Worker.Run", "Execute");

        var text = File.ReadAllText(env.PathOf("src/Worker.cs"));
        Assert.Contains("public void Execute() { }", text);
        Assert.Contains("        Execute();", text);
        Assert.Contains("this.Execute();", text);
        Assert.Contains("Task.Run(() => { });", text);
        Assert.Contains("not renamed (qualified by types outside the project)", r);
    }

    [Fact]
    public async Task Rename_UnqualifiedNameWithSeveralDeclarations_RequiresForce()
    {
        using var env = new TestEnv();
        const string src =
            "public class Foo\n" +
            "{\n" +
            "}\n" +
            "\n" +
            "public class Other\n" +
            "{\n" +
            "    public void Foo() { }\n" +
            "}\n";
        env.WriteFile("src/Foo.cs", src);

        var ex = await Assert.ThrowsAsync<ToolException>(() => Rename(env, "Foo", "Bar"));
        Assert.Contains("Other.Foo", ex.Message);
        Assert.Equal(src, File.ReadAllText(env.PathOf("src/Foo.cs")));
    }

    [Fact]
    public async Task Rename_ToExistingName_RequiresForce()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var ex = await Assert.ThrowsAsync<ToolException>(() => Rename(env, "Foo", "FooBar"));
        Assert.Contains("already exists", ex.Message);
        Assert.Equal(FooSource, File.ReadAllText(env.PathOf("src/Foo.cs")));

        var r = await Rename(env, "Foo", "FooBar", dryRun: true, force: true);
        Assert.Contains("dry run", r);
    }

    [Theory]
    [InlineData("1Bad")]
    [InlineData("Bar-Baz")]
    [InlineData("Bar Baz")]
    public async Task Rename_InvalidIdentifier_Throws(string newName)
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var ex = await Assert.ThrowsAsync<ToolException>(() => Rename(env, "Foo", newName));
        Assert.Contains("not a valid identifier", ex.Message);
    }

    [Fact]
    public async Task Rename_JobRevert_RestoresContentAndFileName()
    {
        using var env = new TestEnv();
        WriteWorkspace(env);

        var r = await Rename(env, "Foo", "Bar");
        Assert.True(File.Exists(env.PathOf("src/Bar.cs")));

        var undo = JobStore.Revert(JobStore.Load(JobId(r)), force: false);

        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(FooSource, File.ReadAllText(env.PathOf("src/Foo.cs")));
        Assert.Equal(UseSource, File.ReadAllText(env.PathOf("src/Use.cs")));
        Assert.False(File.Exists(env.PathOf("src/Bar.cs")), "файл после переименования должен исчезнуть при откате");
    }
}
