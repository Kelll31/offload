using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

/// <summary>Разбор вывода компиляторов/линтеров и кадров стека (DiagnosticParser) — без файловой системы.</summary>
public sealed class DiagnosticParserTests
{
    private static List<Diagnostic> Parse(params string[] lines) => DiagnosticParser.Parse(lines);

    [Fact]
    public void Parse_MsBuildError_ExtractsFileLineColCodeAndStripsProjectSuffix()
    {
        var d = Assert.Single(Parse(@"C:\p\src\A.cs(12,5): error CS1002: ; expected [C:\p\A.csproj]"));
        Assert.Equal(@"C:\p\src\A.cs", d.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(5, d.Col);
        Assert.Equal("error", d.Severity);
        Assert.Equal("CS1002", d.Code);
        Assert.Equal("; expected", d.Message);
    }

    [Fact]
    public void Parse_DuplicateLines_AreDeduplicated()
    {
        const string line = @"C:\p\src\A.cs(12,5): error CS1002: ; expected [C:\p\A.csproj]";
        var list = Parse(line, line, "  " + line);
        Assert.Single(list);
    }

    [Fact]
    public void Parse_MsBuildWarning_IsWarning()
    {
        var d = Assert.Single(Parse(@"src\B.cs(3,13): warning CS0168: The variable 'e' is declared but never used [C:\p\A.csproj]"));
        Assert.Equal("warning", d.Severity);
        Assert.Equal("CS0168", d.Code);
        Assert.Equal(3, d.Line);
        Assert.Equal(13, d.Col);
        Assert.Equal("The variable 'e' is declared but never used", d.Message);
    }

    [Fact]
    public void Parse_TscClassic_IsRecognized()
    {
        var d = Assert.Single(Parse("src/a.ts(3,7): error TS2322: Type 'string' is not assignable to type 'number'."));
        Assert.Equal("src/a.ts", d.File);
        Assert.Equal((3, 7), (d.Line, d.Col));
        Assert.Equal("error", d.Severity);
        Assert.Equal("TS2322", d.Code);
        Assert.StartsWith("Type 'string'", d.Message);
    }

    [Fact]
    public void Parse_TscPretty_IsRecognized()
    {
        var d = Assert.Single(Parse("src/a.ts:3:7 - error TS2322: Type 'string' is not assignable to type 'number'."));
        Assert.Equal("src/a.ts", d.File);
        Assert.Equal((3, 7), (d.Line, d.Col));
        Assert.Equal("error", d.Severity);
        Assert.Equal("TS2322", d.Code);
        Assert.StartsWith("Type 'string'", d.Message);
    }

    [Fact]
    public void Parse_Gcc_IsRecognized()
    {
        var d = Assert.Single(Parse("src/x.c:10:4: error: 'y' undeclared"));
        Assert.Equal("src/x.c", d.File);
        Assert.Equal((10, 4), (d.Line, d.Col));
        Assert.Equal("error", d.Severity);
        Assert.Null(d.Code);
        Assert.Equal("'y' undeclared", d.Message);
    }

    [Fact]
    public void Parse_GoCompilerLineWithoutSeverity_IsError()
    {
        // У `go build` нет слова severity и кода: для .go со столбцом это ошибка компиляции.
        var d = Assert.Single(Parse("./main.go:5:2: undefined: foo"));
        Assert.Equal("./main.go", d.File);
        Assert.Equal(5, d.Line);
        Assert.Equal(2, d.Col);
        Assert.Equal("error", d.Severity);
        Assert.Equal("undefined: foo", d.Message);
        // Для других расширений строка без severity/кода по-прежнему не диагностика.
        Assert.Empty(Parse("src/app.txt:5:2: just text"));
    }

    [Fact]
    public void Parse_Mypy_IsRecognizedWithoutColumn()
    {
        var d = Assert.Single(Parse("app.py:12: error: Incompatible types [assignment]"));
        Assert.Equal("app.py", d.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(0, d.Col);
        Assert.Equal("error", d.Severity);
        Assert.Equal("Incompatible types [assignment]", d.Message);
    }

    [Fact]
    public void Parse_RustTwoLineDiagnostic_CombinesHeadAndLocation()
    {
        var d = Assert.Single(Parse("error[E0425]: cannot find value `x` in this scope", "  --> src/main.rs:3:5", "   |"));
        Assert.Equal("src/main.rs", d.File);
        Assert.Equal((3, 5), (d.Line, d.Col));
        Assert.Equal("error", d.Severity);
        Assert.Equal("E0425", d.Code);
        Assert.Equal("cannot find value `x` in this scope", d.Message);
    }

    [Fact]
    public void Parse_EslintStylish_UsesPrecedingFileHeader()
    {
        var d = Assert.Single(Parse(@"C:\p\src\app.js", "  12:5  error  'x' is defined but never used  no-unused-vars", "", "✖ 1 problem (1 error, 0 warnings)"));
        Assert.Equal(@"C:\p\src\app.js", d.File);
        Assert.Equal((12, 5), (d.Line, d.Col));
        Assert.Equal("error", d.Severity);
        Assert.Equal("no-unused-vars", d.Code);
        Assert.Equal("'x' is defined but never used", d.Message);
    }

    [Fact]
    public void Parse_BuildSummaryLines_ProduceNothing()
    {
        Assert.Empty(Parse("Build succeeded.", "    0 Warning(s)", "    0 Error(s)", "", "Time Elapsed 00:00:01.23",
            "  Determining projects to restore...", "  All projects are up-to-date for restore."));
    }

    [Fact]
    public void ParseFrames_DotNet()
    {
        var f = Assert.Single(DiagnosticParser.ParseFrames([@"   at Foo.Bar.Baz() in C:\p\src\Bar.cs:line 42"]));
        Assert.Equal(@"C:\p\src\Bar.cs", f.File);
        Assert.Equal(42, f.Line);
        Assert.Equal("Foo.Bar.Baz", f.Function);
        Assert.Equal(1, f.LogLine);
    }

    [Fact]
    public void ParseFrames_Python()
    {
        var f = Assert.Single(DiagnosticParser.ParseFrames(["Traceback (most recent call last):", "  File \"app/x.py\", line 7, in run"]));
        Assert.Equal("app/x.py", f.File);
        Assert.Equal(7, f.Line);
        Assert.Equal("run", f.Function);
        Assert.Equal(2, f.LogLine);
    }

    [Fact]
    public void ParseFrames_Node()
    {
        var f = Assert.Single(DiagnosticParser.ParseFrames(["    at run (src/x.js:10:5)"]));
        Assert.Equal("src/x.js", f.File);
        Assert.Equal(10, f.Line);
        Assert.Equal("run", f.Function);
    }

    [Fact]
    public void ParseFrames_Java()
    {
        var f = Assert.Single(DiagnosticParser.ParseFrames(["\tat com.a.B.c(B.java:12)"]));
        Assert.Equal("B.java", f.File);
        Assert.Equal(12, f.Line);
        Assert.Equal("com.a.B.c", f.Function);
    }

    [Fact]
    public void ParseFrames_Go()
    {
        var f = Assert.Single(DiagnosticParser.ParseFrames(["goroutine 1 [running]:", "main.main()", "\t/home/u/p/main.go:22 +0x1d"]));
        Assert.Equal("/home/u/p/main.go", f.File);
        Assert.Equal(22, f.Line);
        Assert.Null(f.Function);
        Assert.Equal(3, f.LogLine);
    }
}
