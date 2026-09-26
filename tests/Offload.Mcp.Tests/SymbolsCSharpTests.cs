using System.Diagnostics;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Разбор C# через синтаксическое дерево Roslyn (Symbols.Parse для .cs) и кэш разбора — без файлов и AppPaths.</summary>
public sealed class SymbolsCSharpTests
{
    private static List<CodeSymbol> Parse(string path, params string[] source) =>
        Symbols.Parse(path, TextCodec.SplitLines(string.Join("\n", source) + "\n"));

    private static SourceFile File(string path, params string[] source) => new()
    {
        FullPath = path,
        Display = path,
        Lines = TextCodec.SplitLines(string.Join("\n", source) + "\n"),
        Lang = Symbols.LangOf(path),
    };

    private static CodeSymbol Single(List<CodeSymbol> symbols, string name)
    {
        var found = symbols.Where(s => s.Name == name).ToList();
        Assert.True(found.Count == 1, $"символ «{name}» найден {found.Count} раз: {string.Join(", ", symbols.Select(s => $"{s.Kind} {s.Name}@{s.Line}"))}");
        return found[0];
    }

    [Fact]
    public void Parse_Records_PositionalBodyAndStruct()
    {
        var symbols = Parse("Models.cs",
            "namespace Shop;",
            "public record Person(string Name, int Age);",
            "public readonly record struct Point(int X, int Y);",
            "public sealed record class Order",
            "{",
            "    public int Id { get; init; }",
            "    public decimal Total() => 0m;",
            "}");

        var person = Single(symbols, "Person");
        Assert.Equal(("record", 2, 2), (person.Kind, person.Line, person.EndLine));
        Assert.Equal("public record Person(string Name, int Age);", person.Signature);
        Assert.Equal("struct", Single(symbols, "Point").Kind);
        var order = Single(symbols, "Order");
        Assert.Equal(("record", 4, 8), (order.Kind, order.Line, order.EndLine));
        Assert.Equal("Order", Single(symbols, "Id").Container);
        Assert.Equal(("method", "Order", 0), (Single(symbols, "Total").Kind, Single(symbols, "Total").Container, Single(symbols, "Total").Params));
    }

    [Fact]
    public void Parse_GenericMethodWithMultiLineSignature_SpansHeaderAndBody()
    {
        string[] src =
        [
            "public static class Seq",
            "{",
            "    [System.Diagnostics.Contracts.Pure]",
            "    public static TResult Aggregate<TSource, TResult>(",
            "        IEnumerable<TSource> source,",
            "        Func<TResult, TSource, TResult> step)",
            "        where TResult : new()",
            "    {",
            "        return default!;",
            "    }",
            "}",
        ];
        var symbols = Parse("Seq.cs", src);

        var m = Single(symbols, "Aggregate");
        // Строка объявления — после атрибута; конец — закрывающая скобка тела.
        Assert.Equal(("method", 4, 10, "Seq", 2), (m.Kind, m.Line, m.EndLine, m.Container, m.Params));
        Assert.Equal("public static TResult Aggregate<TSource, TResult>(IEnumerable<TSource> source, Func<TResult, TSource, TResult> step) where TResult : new()", m.Signature);
    }

    [Fact]
    public void Parse_NestedTypes_QualifiedContainers()
    {
        var symbols = Parse("Outer.cs",
            "namespace A.B",
            "{",
            "    public class Outer",
            "    {",
            "        public struct Middle",
            "        {",
            "            private interface IInner",
            "            {",
            "                void Ping(int a, string b);",
            "            }",
            "        }",
            "    }",
            "}");

        Assert.Null(Single(symbols, "Outer").Container);
        Assert.Equal("Outer", Single(symbols, "Middle").Container);
        Assert.Equal("Outer.Middle", Single(symbols, "IInner").Container);
        var ping = Single(symbols, "Ping");
        Assert.Equal(("Outer.Middle.IInner.Ping", 9, 9, 2), (ping.QualifiedName, ping.Line, ping.EndLine, ping.Params));
    }

    [Fact]
    public void Parse_PartialClassAcrossTwoFiles_BothPartsAndMembersFound()
    {
        var a = File("src/Repo.cs",
            "namespace Data;",
            "public partial class Repo",
            "{",
            "    public void Save(int id) { }",
            "}");
        var b = File("src/Repo.Query.cs",
            "namespace Data;",
            "",
            "public partial class Repo",
            "{",
            "    public int Count() => 0;",
            "    private readonly List<int> _items = [];",
            "}");

        var parts = SymbolsTool.Definitions([a, b], "Repo");
        Assert.Equal(["src/Repo.cs", "src/Repo.Query.cs"], parts.Select(p => p.File.Display));
        Assert.All(parts, p => Assert.Equal("class", p.Symbol.Kind));
        Assert.Equal("src/Repo.cs", Assert.Single(SymbolsTool.Definitions([a, b], "Repo.Save")).File.Display);
        Assert.Equal("src/Repo.Query.cs", Assert.Single(SymbolsTool.Definitions([a, b], "Repo.Count")).File.Display);
        Assert.Equal("field", Assert.Single(SymbolsTool.Definitions([a, b], "Repo._items")).Symbol.Kind);
    }

    [Fact]
    public void Parse_DeclarationLikeTextInStringsAndComments_IsIgnored()
    {
        string[] src =
        [
            "public class Real",
            "{",
            "    private const string Verbatim = @\"",
            "class Fake {",
            "    public void Ghost() {",
            "\";",
            "    private const string Raw = \"\"\"",
            "        public int Phantom { get; set; }",
            "        }}}",
            "        \"\"\";",
            "    // public void Commented() { }",
            "    /* class Hidden { } */",
            "    public void Work() { }",
            "}",
        ];
        var symbols = Parse("Real.cs", src);

        Assert.Equal(["Real", "Verbatim", "Raw", "Work"], symbols.Select(s => s.Name));
        Assert.Equal(14, Single(symbols, "Real").EndLine);
        Assert.Equal((3, 6), (Single(symbols, "Verbatim").Line, Single(symbols, "Verbatim").EndLine));
    }

    [Fact]
    public void Parse_MembersInConditionalBranches_AreFound()
    {
        var symbols = Parse("Cond.cs",
            "public class Cond",
            "{",
            "#if DEBUG",
            "    public void DebugOnly() { }",
            "#else",
            "    public void ReleaseOnly() { }",
            "#endif",
            "}");

        Assert.Equal(4, Single(symbols, "DebugOnly").Line);
        Assert.Equal(6, Single(symbols, "ReleaseOnly").Line);
        Assert.Equal(8, Single(symbols, "Cond").EndLine);
    }

    [Fact]
    public void Parse_EventsFieldsCtorAndTopLevelLocalFunction()
    {
        var symbols = Parse("Program.cs",
            "var x = Helper(2);",
            "static int Helper(int v) => v * 2;",
            "",
            "public class Bus",
            "{",
            "    public event EventHandler? Changed;",
            "    public event EventHandler Custom { add { } remove { } }",
            "    private int _a, _b;",
            "    public Bus(int a) { _a = a; }",
            "    public Bar(int b) { }",
            "}");

        Assert.Equal(("function", (string?)null, 1), (Single(symbols, "Helper").Kind, Single(symbols, "Helper").Container, Single(symbols, "Helper").Params));
        Assert.Equal("event", Single(symbols, "Changed").Kind);
        Assert.Equal("event", Single(symbols, "Custom").Kind);
        Assert.Equal(("field", "field"), (Single(symbols, "_a").Kind, Single(symbols, "_b").Kind));
        var ctor = symbols.Single(s => s.Kind == "ctor");
        Assert.Equal(("Bus", "Bus", 1), (ctor.Name, ctor.Container, ctor.Params));
        // «public Bar(int b)» внутри Bus — не конструктор (Roslyn принимает его за конструктор с ошибкой).
        Assert.DoesNotContain(symbols, s => s.Name == "Bar");
        Assert.DoesNotContain(symbols, s => s.Name == "x");
    }

    [Fact]
    public void Parse_Cache_ReusesResultAndSeesChangedText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"offload-cache-{Guid.NewGuid():N}.cs");
        string[] v1 = ["public class Cached", "{", "    public void One() { }", "}"];
        string[] v2 = ["public class Cached", "{", "    public void Two() { }", "}"];

        var first = Symbols.Parse(path, v1);
        Assert.True(Symbols.IsCSharpCached(path, v1), "после разбора результат должен быть в кэше");
        Assert.Equal(first, Symbols.Parse(path, v1));
        // Тот же путь, другой текст (например, замаскированная версия файла) — не из кэша.
        Assert.False(Symbols.IsCSharpCached(path, v2));
        Assert.Contains(Symbols.Parse(path, v2), s => s.Name == "Two");
        Assert.DoesNotContain(Symbols.Parse(path, v2), s => s.Name == "One");
        // Две версии одного файла (замаскированная и исходная) не вытесняют друг друга.
        Assert.True(Symbols.IsCSharpCached(path, v1) && Symbols.IsCSharpCached(path, v2), "обе версии текста должны остаться в кэше");
    }

    [Fact]
    public void Parse_RepositorySources_MeasuresRoslynTime()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !System.IO.File.Exists(Path.Combine(root, "Offload.slnx"))) root = Path.GetDirectoryName(root);
        Assert.SkipWhen(root is null, "корень репозитория (Offload.slnx) не найден рядом с тестами");
        var files = Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path: f, Lines: TextCodec.SplitLines(System.IO.File.ReadAllText(f))))
            .ToList();

        var sw = Stopwatch.StartNew();
        var symbols = files.Sum(f => Symbols.Parse(f.Path, f.Lines).Count);
        var cold = sw.Elapsed;
        sw.Restart();
        var again = files.Sum(f => Symbols.Parse(f.Path, f.Lines).Count);
        var warm = sw.Elapsed;

        var lines = files.Sum(f => f.Lines.Length);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Roslyn: {files.Count} файлов src/*.cs, {lines} строк, {symbols} символов; первый разбор {cold.TotalMilliseconds:F0} мс, из кэша {warm.TotalMilliseconds:F0} мс");
        Assert.Equal(symbols, again);
        Assert.True(symbols > files.Count, "в исходниках репозитория должны находиться объявления");
        Assert.True(cold < TimeSpan.FromSeconds(60), $"разбор src занял {cold}");
    }
}
