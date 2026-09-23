using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

/// <summary>Эвристический разбор объявлений (Symbols.Parse) и вспомогательные функции CodeIndex — без файлов и AppPaths.</summary>
public class SymbolsParserTests
{
    private static List<CodeSymbol> Parse(string path, params string[] source) =>
        Symbols.Parse(path, TextCodec.SplitLines(string.Join("\n", source) + "\n"));

    /// <summary>Номер строки (с 1), начинающейся (после отступа) с заданного текста.</summary>
    private static int LineOf(string[] source, string startsWith)
    {
        var i = Array.FindIndex(source, l => l.TrimStart().StartsWith(startsWith, StringComparison.Ordinal));
        Assert.True(i >= 0, $"в исходнике нет строки «{startsWith}»");
        return i + 1;
    }

    private static CodeSymbol Single(List<CodeSymbol> symbols, string name)
    {
        var found = symbols.Where(s => s.Name == name).ToList();
        Assert.True(found.Count == 1, $"символ «{name}» найден {found.Count} раз: {string.Join(", ", symbols.Select(s => $"{s.Kind} {s.Name}@{s.Line}"))}");
        return found[0];
    }

    private static void AssertSymbol(CodeSymbol s, string kind, int line, int endLine, string? container)
    {
        Assert.Equal(kind, s.Kind);
        Assert.Equal(line, s.Line);
        Assert.Equal(endLine, s.EndLine);
        Assert.Equal(container, s.Container);
    }

    // ───────────────────────── C# ─────────────────────────

    private static readonly string[] CSharpSource =
    [
        "namespace Shop.Core",
        "{",
        "    public sealed class OrderService",
        "    {",
        "        private readonly IRepo _repo;",
        "",
        "        public OrderService(IRepo repo)",
        "        {",
        "            _repo = repo;",
        "        }",
        "",
        "        public int Count { get; set; }",
        "",
        "        public string Describe() => \"order {\";",
        "",
        "        public void Submit(Order order)",
        "        {",
        "            Validate(order);",
        "            _repo.Save(order);",
        "        }",
        "",
        "        private static void Validate(Order order)",
        "        {",
        "            if (order is null) throw new ArgumentNullException(nameof(order));",
        "        }",
        "",
        "        public sealed class Nested",
        "        {",
        "            public void Run() { }",
        "        }",
        "    }",
        "",
        "    public interface IShape",
        "    {",
        "        double Area();",
        "    }",
        "",
        "    public record Order(int Id, string Name);",
        "}",
    ];

    [Fact]
    public void Parse_CSharp_NamespaceAndTypes()
    {
        var symbols = Parse("src/OrderService.cs", CSharpSource);

        AssertSymbol(Single(symbols, "Shop.Core"), "namespace", 1, CSharpSource.Length, null);
        // Пространство имён не входит в Container.
        AssertSymbol(symbols.Single(s => s.Name == "OrderService" && s.Kind == "class"), "class", LineOf(CSharpSource, "public sealed class OrderService"), LineOf(CSharpSource, "public interface IShape") - 2, null);
        AssertSymbol(Single(symbols, "IShape"), "interface", LineOf(CSharpSource, "public interface IShape"), LineOf(CSharpSource, "public interface IShape") + 3, null);
        AssertSymbol(Single(symbols, "Order"), "record", LineOf(CSharpSource, "public record Order"), LineOf(CSharpSource, "public record Order"), null);
        Assert.True(Single(symbols, "IShape").IsType);
    }

    [Fact]
    public void Parse_CSharp_MembersWithContainers()
    {
        var symbols = Parse("src/OrderService.cs", CSharpSource);

        var ctor = symbols.Single(s => s.Kind == "ctor");
        AssertSymbol(ctor, "ctor", LineOf(CSharpSource, "public OrderService(IRepo repo)"), LineOf(CSharpSource, "public OrderService(IRepo repo)") + 3, "OrderService");
        Assert.Equal("OrderService", ctor.Name);

        AssertSymbol(Single(symbols, "Count"), "property", LineOf(CSharpSource, "public int Count"), LineOf(CSharpSource, "public int Count"), "OrderService");
        // Выражение-тело: одна строка, «{» внутри строкового литерала не открывает тело.
        AssertSymbol(Single(symbols, "Describe"), "method", LineOf(CSharpSource, "public string Describe"), LineOf(CSharpSource, "public string Describe"), "OrderService");
        Assert.StartsWith("public string Describe() => \"order", Single(symbols, "Describe").Signature);

        var submit = Single(symbols, "Submit");
        AssertSymbol(submit, "method", LineOf(CSharpSource, "public void Submit"), LineOf(CSharpSource, "public void Submit") + 4, "OrderService");
        Assert.Equal("OrderService.Submit", submit.QualifiedName);
        Assert.Equal("public void Submit(Order order)", submit.Signature);

        AssertSymbol(Single(symbols, "Validate"), "method", LineOf(CSharpSource, "private static void Validate"), LineOf(CSharpSource, "private static void Validate") + 3, "OrderService");
    }

    [Fact]
    public void Parse_CSharp_NestedClassAndInterfaceMembers()
    {
        var symbols = Parse("src/OrderService.cs", CSharpSource);

        AssertSymbol(Single(symbols, "Nested"), "class", LineOf(CSharpSource, "public sealed class Nested"), LineOf(CSharpSource, "public sealed class Nested") + 3, "OrderService");
        AssertSymbol(Single(symbols, "Run"), "method", LineOf(CSharpSource, "public void Run"), LineOf(CSharpSource, "public void Run"), "OrderService.Nested");
        Assert.Equal("OrderService.Nested.Run", Single(symbols, "Run").QualifiedName);

        // Объявление члена интерфейса без тела («double Area();») — тоже символ (однострочный); вызовы в телах — не объявления.
        var area = Single(symbols, "Area");
        Assert.Equal("method", area.Kind);
        Assert.Equal(area.Line, area.EndLine);
        Assert.DoesNotContain(symbols, s => s.Name is "_repo" or "ArgumentNullException" or "Save" or "if");
    }

    [Fact]
    public void Parse_CSharp_FileScopedNamespaceIsNotAContainer()
    {
        string[] src =
        [
            "namespace Shop;",
            "",
            "internal static class Util",
            "{",
            "    public static int Twice(int x) => x * 2;",
            "}",
        ];
        var symbols = Parse("Util.cs", src);

        AssertSymbol(Single(symbols, "Shop"), "namespace", 1, 1, null);
        AssertSymbol(Single(symbols, "Util"), "class", 3, 6, null);
        AssertSymbol(Single(symbols, "Twice"), "method", 5, 5, "Util");
    }

    [Fact]
    public void Parse_CSharp_CallWithoutAccessModifierIsNotACtor()
    {
        string[] src =
        [
            "public class Foo",
            "{",
            "    public Bar(int x)",
            "    {",
            "    }",
            "}",
        ];
        var symbols = Parse("Foo.cs", src);
        Assert.DoesNotContain(symbols, s => s.Kind == "ctor");
    }

    [Fact]
    public void Parse_CSharp_CallStatementsAreNotDeclarations()
    {
        string[] src =
        [
            "public class Foo",
            "{",
            "    public void Run()",
            "    {",
            "        service.Submit(order);",
            "        service.Submit(new Order(1));",
            "        var x = new Bar(Baz(2));",
            "    }",
            "}",
        ];
        var symbols = Parse("Foo.cs", src);
        Assert.Equal(["Foo", "Run"], symbols.Select(s => s.Name));
    }

    [Fact]
    public void Parse_UnknownExtensionOrEmpty_ReturnsNothing()
    {
        Assert.Empty(Parse("notes.txt", "class Foo {", "}"));
        Assert.Empty(Symbols.Parse("a.cs", []));
    }

    // ───────────────────────── TypeScript ─────────────────────────

    [Fact]
    public void Parse_TypeScript_ClassFunctionsInterface()
    {
        string[] src =
        [
            "export class Cart {",
            "  private items: string[] = [];",
            "  addItem(item: string): void {",
            "    this.items.push(item);",
            "  }",
            "  async total(): Promise<number> {",
            "    return 0;",
            "  }",
            "}",
            "export function makeCart(): Cart {",
            "  return new Cart();",
            "}",
            "export const sum = (a: number, b: number) => a + b;",
            "export interface Item {",
            "  name: string;",
            "}",
        ];
        var symbols = Parse("web/cart.ts", src);

        AssertSymbol(Single(symbols, "Cart"), "class", 1, 9, null);
        AssertSymbol(Single(symbols, "addItem"), "method", 3, 5, "Cart");
        AssertSymbol(Single(symbols, "total"), "method", 6, 8, "Cart");
        AssertSymbol(Single(symbols, "makeCart"), "function", 10, 12, null);
        AssertSymbol(Single(symbols, "sum"), "function", 13, 13, null);
        AssertSymbol(Single(symbols, "Item"), "interface", 14, 16, null);
        Assert.DoesNotContain(symbols, s => s.Name is "items" or "push" or "name");
    }

    // ───────────────────────── Python ─────────────────────────

    [Fact]
    public void Parse_Python_ClassMethodsFunctionByIndentation()
    {
        string[] src =
        [
            "class Greeter:",
            "    def __init__(self, name):",
            "        self.name = name",
            "",
            "    def greet(self):",
            "        # comment at column 8",
            "        return \"hi \" + self.name",
            "",
            "def main():",
            "    g = Greeter(\"x\")",
            "    print(g.greet())",
            "",
            "async def fetch():",
            "    pass",
        ];
        var symbols = Parse("app/greeter.py", src);

        AssertSymbol(Single(symbols, "Greeter"), "class", 1, 7, null);
        AssertSymbol(Single(symbols, "__init__"), "method", 2, 3, "Greeter");
        AssertSymbol(Single(symbols, "greet"), "method", 5, 7, "Greeter");
        AssertSymbol(Single(symbols, "main"), "function", 9, 11, null);
        AssertSymbol(Single(symbols, "fetch"), "function", 13, 14, null);
    }

    [Fact]
    public void Parse_Python_NestedFunctionIsFunctionNotMethod()
    {
        string[] src =
        [
            "def outer():",
            "    def inner():",
            "        return 1",
            "    return inner",
        ];
        var symbols = Parse("x.py", src);

        AssertSymbol(Single(symbols, "outer"), "function", 1, 4, null);
        AssertSymbol(Single(symbols, "inner"), "function", 2, 3, "outer");
    }

    // ───────────────────────── Go, Rust, Java ─────────────────────────

    [Fact]
    public void Parse_Go_FuncMethodAndStruct()
    {
        string[] src =
        [
            "package shop",
            "",
            "type Store struct {",
            "\titems []string",
            "}",
            "",
            "func (s *Store) Add(item string) {",
            "\ts.items = append(s.items, item)",
            "}",
            "",
            "func New() *Store {",
            "\treturn &Store{}",
            "}",
        ];
        var symbols = Parse("shop/store.go", src);

        AssertSymbol(Single(symbols, "Store"), "struct", 3, 5, null);
        AssertSymbol(Single(symbols, "Add"), "function", 7, 9, null);
        AssertSymbol(Single(symbols, "New"), "function", 11, 13, null);
        Assert.Equal(3, symbols.Count);
    }

    [Fact]
    public void Parse_Rust_FnStructImpl()
    {
        string[] src =
        [
            "pub struct Point {",
            "    x: i32,",
            "}",
            "",
            "impl Point {",
            "    pub fn origin(x: i32) -> Self {",
            "        Point { x }",
            "    }",
            "}",
            "",
            "fn main() {",
            "    let p = Point::origin(1);",
            "}",
        ];
        var symbols = Parse("src/main.rs", src);

        AssertSymbol(symbols.Single(s => s.Kind == "struct"), "struct", 1, 3, null);
        var impl = symbols.Single(s => s.Kind == "impl");
        AssertSymbol(impl, "impl", 5, 9, null);
        Assert.Equal("Point", impl.Name);
        AssertSymbol(Single(symbols, "origin"), "function", 6, 8, "Point");
        AssertSymbol(Single(symbols, "main"), "function", 11, 13, null);
    }

    [Fact]
    public void Parse_Rust_FnNamedNewIsRecognized()
    {
        // «fn new» — идиоматичный конструктор в Rust; после «fn» имя не может быть вызовом или ключевым словом.
        string[] src =
        [
            "impl Point {",
            "    pub fn new(x: i32) -> Self {",
            "        Point { x }",
            "    }",
            "}",
        ];
        var symbols = Parse("src/point.rs", src);
        AssertSymbol(Single(symbols, "new"), "function", 2, 4, "Point");
    }

    [Fact]
    public void Parse_Java_ClassAndMethod()
    {
        string[] src =
        [
            "public class Calculator {",
            "    @Override",
            "    public int plus(int a, int b) {",
            "        return a + b;",
            "    }",
            "}",
        ];
        var symbols = Parse("src/Calculator.java", src);

        AssertSymbol(Single(symbols, "Calculator"), "class", 1, 6, null);
        AssertSymbol(Single(symbols, "plus"), "method", 3, 5, "Calculator");
        Assert.Equal(2, symbols.Count);
    }

    // ───────────────────────── Enclosing ─────────────────────────

    [Fact]
    public void Enclosing_PicksInnermostSymbol()
    {
        var symbols = Parse("src/OrderService.cs", CSharpSource);
        var submitLine = LineOf(CSharpSource, "public void Submit");

        Assert.Equal("Submit", Symbols.Enclosing(symbols, submitLine + 2)?.Name);
        Assert.Equal("Submit", Symbols.Enclosing(symbols, submitLine)?.Name);
        Assert.Equal("Run", Symbols.Enclosing(symbols, LineOf(CSharpSource, "public void Run"))?.Name);
        // Поле — внутри класса, но вне методов.
        Assert.Equal("OrderService", Symbols.Enclosing(symbols, LineOf(CSharpSource, "private readonly IRepo"))?.Name);
        // Между типами — только пространство имён.
        Assert.Equal("Shop.Core", Symbols.Enclosing(symbols, LineOf(CSharpSource, "public interface IShape") - 1)?.Name);
    }

    [Fact]
    public void Enclosing_OutsideAllSymbols_ReturnsNull()
    {
        List<CodeSymbol> symbols =
        [
            new("A", "class", 3, 10, null, "class A"),
            new("M", "method", 5, 7, "A", "void M()"),
        ];
        Assert.Null(Symbols.Enclosing(symbols, 1));
        Assert.Null(Symbols.Enclosing(symbols, 11));
        Assert.Equal("M", Symbols.Enclosing(symbols, 7)?.Name);
        Assert.Equal("A", Symbols.Enclosing(symbols, 8)?.Name);
        Assert.Null(Symbols.Enclosing([], 1));
    }

    // ───────────────────────── CodeIndex ─────────────────────────

    [Theory]
    [InlineData("tests/Foo.cs", true)]
    [InlineData(@"tests\Foo.cs", true)]
    [InlineData("src/test/Helper.java", true)]
    [InlineData("web/__tests__/cart.ts", true)]
    [InlineData("tests/Offload.Mcp.Tests/Foo.cs", true)]
    [InlineData("Offload.Core.Tests/Foo.cs", true)]
    [InlineData("src/FooTests.cs", true)]
    [InlineData("src/FooTest.java", true)]
    [InlineData("src/CartSpec.ts", true)]
    [InlineData("web/foo.test.ts", true)]
    [InlineData("web/foo.spec.js", true)]
    [InlineData("test_x.py", true)]
    [InlineData("pkg/x_test.go", true)]
    [InlineData("src/Tester.cs", false)]
    [InlineData("src/Contest.cs", false)]
    [InlineData("src/latest/Foo.cs", false)]
    [InlineData("src/attestation/Foo.cs", false)]
    [InlineData("src/OrderService.cs", false)]
    [InlineData("testdata.json", false)]
    public void IsTestPath_Classifies(string path, bool expected) =>
        Assert.Equal(expected, CodeIndex.IsTestPath(path));

    [Theory]
    [InlineData("var s = \"Foo\";", "Foo", 0, true)]
    [InlineData("var c = 'F' + Foo;", "Foo", 0, false)]
    [InlineData("Foo(); // Foo later", "Foo", 0, false)]
    [InlineData("Foo(); // Foo later", "Foo", 1, true)]
    [InlineData("var url = \"http://x\"; Foo();", "Foo", 0, false)]
    [InlineData("var s = \"a\\\"Foo\"; Bar();", "Foo", 0, true)]
    [InlineData("var s = \"a\\\"b\"; Foo();", "Foo", 0, false)]
    [InlineData("var t = `x ${Foo}`;", "Foo", 0, true)]
    public void InStringOrComment_DetectsLiteralsAndLineComments(string line, string word, int occurrence, bool expected)
    {
        var index = -1;
        for (var k = 0; k <= occurrence; k++) index = line.IndexOf(word, index + 1, StringComparison.Ordinal);
        Assert.True(index >= 0, "вхождение не найдено в строке теста");
        Assert.Equal(expected, CodeIndex.InStringOrComment(line, index));
    }

    [Theory]
    [InlineData("repo.Save(x);", true)]
    [InlineData("Save", true)]
    [InlineData("SaveAll(x);", false)]
    [InlineData("AutoSave(x);", false)]
    [InlineData("_Save()", false)]
    [InlineData("$Save()", false)]
    [InlineData("Save_1", false)]
    [InlineData("save(x);", false)]
    [InlineData("(Save)", true)]
    public void WordRegex_MatchesWholeWordOnly(string text, bool expected) =>
        Assert.Equal(expected, CodeIndex.WordRegex("Save").IsMatch(text));

    [Fact]
    public void WordRegex_IgnoreCaseAndEscaping()
    {
        Assert.Matches(CodeIndex.WordRegex("Save", ignoreCase: true), "x.save()");
        Assert.DoesNotMatch(CodeIndex.WordRegex("Save", ignoreCase: true), "x.saved()");
        Assert.Matches(CodeIndex.WordRegex("a.b"), "x = a.b;");
        Assert.DoesNotMatch(CodeIndex.WordRegex("a.b"), "x = axb;");
    }
}
