using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>local_refactor rename в C# по синтаксическому дереву Roslyn: перегрузки, строки/комментарии, cref, using-алиасы, partial, атрибуты.</summary>
[Collection("AppPaths")]
public sealed class RefactorRenameCSharpTests
{
    private static Task<string> Rename(TestEnv env, string name, string newName, bool dryRun = false, bool force = false) =>
        RefactorTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), "rename", name, newName, null, null, null, null, null,
            dryRun, force, renameFile: true);

    private const string Overloads =
        "public class Svc\n" +
        "{\n" +
        "    public void Run() { }\n" +
        "    public void Run(int times) { }\n" +
        "    public void Run(string a, string b) { }\n" +
        "    public void Start(Svc other)\n" +
        "    {\n" +
        "        Run();\n" +
        "        Run(3);\n" +
        "        this.Run(4);\n" +
        "        other?.Run(5);\n" +
        "        Run(\"a\", \"b\");\n" +
        "        System.Action a = Run;\n" +
        "    }\n" +
        "}\n";

    [Theory]
    [InlineData("Svc.Run(int)")]
    [InlineData("Svc.Run(int times)")]
    [InlineData("Svc.Run(1)")]
    public async Task Rename_Overload_ChangesOnlyTargetedParameterCount(string name)
    {
        using var env = new TestEnv();
        env.WriteFile("src/Svc.cs", Overloads);

        var r = await Rename(env, name, "Repeat");

        var text = File.ReadAllText(env.PathOf("src/Svc.cs"));
        Assert.Contains("    public void Run() { }", text);
        Assert.Contains("    public void Repeat(int times) { }", text);
        Assert.Contains("    public void Run(string a, string b) { }", text);
        Assert.Contains("        Run();", text);
        Assert.Contains("        Repeat(3);", text);
        Assert.Contains("        this.Repeat(4);", text);
        Assert.Contains("        other?.Repeat(5);", text);
        Assert.Contains("        Run(\"a\", \"b\");", text);
        // Группа методов без вызова — перегрузку по синтаксису не определить: не трогается и попадает в отчёт.
        Assert.Contains("        System.Action a = Run;", text);
        Assert.Contains("overload not determinable", r);
        Assert.Contains("src/Svc.cs:13", r);
    }

    [Fact]
    public async Task Rename_OverloadWithOptionalParameter_MatchesCallsByArgumentRange()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Svc.cs",
            "public class Svc\n" +
            "{\n" +
            "    public void Run(int times) { }\n" +
            "    public void Run(string a, string b = \"x\") { }\n" +
            "    public void Start()\n" +
            "    {\n" +
            "        Run(3);\n" +
            "        Run(\"a\", \"b\");\n" +
            "    }\n" +
            "}\n");

        await Rename(env, "Svc.Run(string, string)", "Pair");

        var text = File.ReadAllText(env.PathOf("src/Svc.cs"));
        Assert.Contains("public void Pair(string a, string b = \"x\") { }", text);
        Assert.Contains("        Pair(\"a\", \"b\");", text);
        // Вызов с одним аргументом подходит и к Run(int), и к Run(string, string = …) — неоднозначен, не трогается.
        Assert.Contains("        Run(3);", text);
        Assert.Contains("    public void Run(int times) { }", text);
    }

    [Fact]
    public async Task Rename_UnknownOverload_ListsExistingOnes()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Svc.cs", Overloads);

        var ex = await Assert.ThrowsAsync<ToolException>(() => Rename(env, "Svc.Run(int, int, int)", "X"));
        Assert.Contains("No overload", ex.Message);
        Assert.Contains("Svc.Run(1 params)", ex.Message);
        Assert.Equal(Overloads, File.ReadAllText(env.PathOf("src/Svc.cs")));
    }

    [Fact]
    public async Task Rename_EmptyParensWithoutZeroParameterOverload_RenamesWholeMethod()
    {
        using var env = new TestEnv();
        env.WriteFile("src/W.cs", "public class W\n{\n    public void Go(int x) { }\n    public void Call() => Go(1);\n}\n");

        await Rename(env, "W.Go()", "Move");

        var text = File.ReadAllText(env.PathOf("src/W.cs"));
        Assert.Contains("public void Move(int x) { }", text);
        Assert.Contains("public void Call() => Move(1);", text);
    }

    [Fact]
    public async Task Rename_Method_SkipsStringsCommentsVerbatimRaw_RenamesInterpolationAndCref()
    {
        using var env = new TestEnv();
        const string src =
            "public class Greeter\n" +
            "{\n" +
            "    public string Hello(string name) => $\"Hello {name}, Hello\"; // Hello\n" +
            "    /// <summary>Calls <see cref=\"Hello\"/> twice. Hello.</summary>\n" +
            "    public string Twice(string n) => Hello(n) + @\"\n" +
            "Hello(\" + \"\"\"\n" +
            "        Hello()\n" +
            "        \"\"\";\n" +
            "    public string Interp(string n) => $\"{Hello(n)}!\";\n" +
            "    /* Hello() */\n" +
            "}\n";
        env.WriteFile("src/Greeter.cs", src);

        await Rename(env, "Greeter.Hello", "Greet");

        var expected = src
            .Replace("public string Hello(", "public string Greet(", StringComparison.Ordinal)
            .Replace("cref=\"Hello\"", "cref=\"Greet\"", StringComparison.Ordinal)
            .Replace("=> Hello(n) + @", "=> Greet(n) + @", StringComparison.Ordinal)
            .Replace("{Hello(n)}", "{Greet(n)}", StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllText(env.PathOf("src/Greeter.cs")));
    }

    [Fact]
    public async Task Rename_StaticMember_ThroughUsingAliasAndUsingStatic()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Util.cs", "namespace App;\n\npublic static class Util\n{\n    public static int Twice(int x) => x * 2;\n}\n");
        env.WriteFile("src/Use.cs",
            "using U = App.Util;\n" +
            "using static App.Util;\n" +
            "\n" +
            "namespace App.Other;\n" +
            "\n" +
            "public class C\n" +
            "{\n" +
            "    public int A() => U.Twice(1) + Twice(2) + App.Util.Twice(3);\n" +
            "    public int B() => Calc.Twice(4);\n" +
            "}\n");

        var r = await Rename(env, "Util.Twice", "Double");

        Assert.Contains("public static int Double(int x)", File.ReadAllText(env.PathOf("src/Util.cs")));
        var use = File.ReadAllText(env.PathOf("src/Use.cs"));
        Assert.Contains("using U = App.Util;", use);
        Assert.Contains("public int A() => U.Double(1) + Double(2) + App.Util.Double(3);", use);
        // Calc — тип не из проекта: чужой API не трогается.
        Assert.Contains("public int B() => Calc.Twice(4);", use);
        Assert.Contains("Calc.Twice", r);
    }

    [Fact]
    public async Task Rename_PartialClassAcrossTwoFiles_RenamesBothPartsAndFiles()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Repo.cs", "namespace Data;\n\npublic partial class Repo\n{\n    public Repo() { }\n}\n");
        env.WriteFile("src/Repo.Query.cs", "namespace Data;\n\npublic partial class Repo\n{\n    public static Repo Create() => new Repo();\n}\n");
        env.WriteFile("src/Use.cs", "namespace Data;\n\npublic class Use\n{\n    private readonly Repo _repo = Repo.Create(); // Repo\n}\n");

        await Rename(env, "Repo", "Store");

        Assert.False(File.Exists(env.PathOf("src/Repo.cs")));
        Assert.False(File.Exists(env.PathOf("src/Repo.Query.cs")));
        Assert.Contains("public partial class Store\n{\n    public Store() { }", File.ReadAllText(env.PathOf("src/Store.cs")));
        Assert.Contains("public static Store Create() => new Store();", File.ReadAllText(env.PathOf("src/Store.Query.cs")));
        Assert.Contains("private readonly Store _repo = Store.Create(); // Repo", File.ReadAllText(env.PathOf("src/Use.cs")));
    }

    [Fact]
    public async Task Rename_AttributeClass_UpdatesShortUsages()
    {
        using var env = new TestEnv();
        env.WriteFile("src/CachedAttribute.cs", "public sealed class CachedAttribute : System.Attribute { }\n");
        env.WriteFile("src/Svc.cs", "public class Svc\n{\n    [Cached] public void A() { }\n    [CachedAttribute] public void B() { }\n    public string C() => \"Cached\";\n}\n");

        await Rename(env, "CachedAttribute", "MemoizedAttribute");

        Assert.True(File.Exists(env.PathOf("src/MemoizedAttribute.cs")));
        var svc = File.ReadAllText(env.PathOf("src/Svc.cs"));
        Assert.Contains("[Memoized] public void A()", svc);
        Assert.Contains("[MemoizedAttribute] public void B()", svc);
        Assert.Contains("\"Cached\"", svc);
    }

    [Theory]
    [InlineData("Svc.Run", "Svc.Run", null, false)]
    [InlineData("Svc.Run()", "Svc.Run", 0, true)]
    [InlineData("Svc.Run(2)", "Svc.Run", 2, false)]
    [InlineData("Run(int a, Dictionary<string, int> map)", "Run", 2, false)]
    [InlineData("Run(Func<int, (int, int)> f, int[,] grid, string s)", "Run", 3, false)]
    public void ParseOverloadSpec_CountsTopLevelParameters(string input, string name, int? count, bool empty) =>
        Assert.Equal((name, count, empty), RefactorTool.ParseOverloadSpec(input));

    [Fact]
    public void Find_ReportsDeclarationsReceiversAndArguments()
    {
        string[] lines =
        [
            "using R = Data.Repo;",
            "class Repo { public void Save(int a, params int[] rest) { } }",
            "class X { void M(Repo r) { R.Save(1); r.Save(1, 2, 3); Save(); var s = \"Save\"; } }",
        ];
        var occ = CSharpReferences.Find("x.cs", lines, "Save");

        Assert.Equal(4, occ.Count);
        var decl = occ[0];
        Assert.Equal(("method", "Repo", 2, 1, int.MaxValue), (decl.Declaration, decl.DeclaringType, decl.Params, decl.MinArgs, decl.MaxArgs));
        Assert.Equal(("Repo", 1), (occ[1].Receiver, occ[1].ArgCount));
        Assert.Equal(("r", 3), (occ[2].Receiver, occ[2].ArgCount));
        Assert.Equal(((string?)null, 0, "X"), (occ[3].Receiver, occ[3].ArgCount, occ[3].DeclaringType));
        Assert.Equal((3, lines[2].IndexOf("R.Save", StringComparison.Ordinal) + 2), (occ[1].Line, occ[1].Column));
    }
}
