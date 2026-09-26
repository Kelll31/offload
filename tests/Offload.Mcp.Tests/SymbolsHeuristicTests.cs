using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Эвристический разбор не-C# языков: закрытые дыры (многострочные сигнатуры, длинные строки, шаблоны JS, docstring Python, Go, C++).</summary>
public sealed class SymbolsHeuristicTests
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
    public void TypeScript_MultiLineMethodSignature_IsJoined()
    {
        var symbols = Parse("web/api.ts",
            "export class Api {",
            "  async fetchItems(",
            "    page: number,",
            "    size: number,",
            "  ): Promise<Item[]> {",
            "    return [];",
            "  }",
            "}");

        var m = Single(symbols, "fetchItems");
        Assert.Equal(("method", 2, 7, "Api"), (m.Kind, m.Line, m.EndLine, m.Container));
        Assert.Equal("async fetchItems(page: number, size: number,): Promise<Item[]>", m.Signature);
        Assert.Equal(8, Single(symbols, "Api").EndLine);
    }

    [Fact]
    public void Cpp_MultiLineOutOfClassDefinition_ExposesContainer()
    {
        var f = File("src/calc.cpp",
            "#include \"calc.h\"",
            "namespace math {",
            "int Calculator::add(int a,",
            "                    int b) {",
            "  return a + b;",
            "}",
            "Calculator::~Calculator() {",
            "}",
            "}");

        var add = Single(f.Symbols.ToList(), "add");
        Assert.Equal(("Calculator", 3, 6), (add.Container, add.Line, add.EndLine));
        Assert.Equal("Calculator.add", add.QualifiedName);
        Assert.Equal("Calculator", Single(f.Symbols.ToList(), "~Calculator").Container);
        // Запросы «Calculator.add» и «Calculator::add» находят определение.
        Assert.Single(SymbolsTool.Definitions([f], "Calculator.add"));
        Assert.Single(SymbolsTool.Definitions([f], "Calculator::add"));
    }

    [Fact]
    public void LongDeclarationLine_Over400Chars_IsParsed()
    {
        var parameters = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"parameterNumber{i}: string"));
        var line = $"export function configure({parameters}): void {{";
        Assert.True(line.Length > 400);
        var symbols = Parse("web/config.ts", line, "  return;", "}");

        Assert.Equal((1, 3), (Single(symbols, "configure").Line, Single(symbols, "configure").EndLine));
    }

    [Fact]
    public void JavaScript_MultiLineTemplateLiteral_DoesNotBreakBracesOrAddSymbols()
    {
        var symbols = Parse("web/render.js",
            "export function render(items) {",
            "  const html = `",
            "    function ghost() {",
            "    <ul>${items.map(i => `<li>${i.name}}</li>`).join('')}</ul>",
            "    {{{",
            "  `;",
            "  return html;",
            "}",
            "export function after() {",
            "}");

        Assert.Equal(["render", "after"], symbols.Select(s => s.Name));
        Assert.Equal((1, 8), (Single(symbols, "render").Line, Single(symbols, "render").EndLine));
        Assert.Equal((9, 10), (Single(symbols, "after").Line, Single(symbols, "after").EndLine));
    }

    [Fact]
    public void Python_DefInsideDocstring_IsNotASymbol_AndBodyContinues()
    {
        var symbols = Parse("app/tools.py",
            "def tool(x):",
            "    \"\"\"Usage:",
            "def fake():",
            "class Fake:",
            "    \"\"\"",
            "    return x",
            "",
            "class Real:",
            "    '''Doc with def inner(): text'''",
            "    def method(",
            "        self,",
            "        value,",
            "    ):",
            "        return value");

        Assert.Equal(["tool", "Real", "method"], symbols.Select(s => s.Name));
        Assert.Equal((1, 6), (Single(symbols, "tool").Line, Single(symbols, "tool").EndLine));
        var method = Single(symbols, "method");
        Assert.Equal(("method", "Real", 10, 14), (method.Kind, method.Container, method.Line, method.EndLine));
        Assert.Equal("def method(self, value,):", method.Signature);
    }

    [Fact]
    public void Go_MethodsGetReceiverTypeAsContainer()
    {
        var f = File("store/repo.go",
            "package store",
            "",
            "type Repo struct{}",
            "",
            "func (r *Repo) Save(item string) error {",
            "\tq := `",
            "\t}}} func (x *Other) Fake() {",
            "\t`",
            "\treturn nil",
            "}",
            "",
            "func (Repo) Name() string { return \"repo\" }",
            "",
            "func (s *Stack[T]) Push(v T) {",
            "}",
            "",
            "func Open() *Repo {",
            "\treturn &Repo{}",
            "}");
        var symbols = f.Symbols.ToList();

        var save = Single(symbols, "Save");
        Assert.Equal(("method", "Repo", 5, 10), (save.Kind, save.Container, save.Line, save.EndLine));
        Assert.Equal("Repo", Single(symbols, "Name").Container);
        Assert.Equal("Stack", Single(symbols, "Push").Container);
        Assert.Null(Single(symbols, "Open").Container);
        Assert.DoesNotContain(symbols, s => s.Name == "Fake");
        Assert.Single(SymbolsTool.Definitions([f], "Repo.Save"));
    }

    [Fact]
    public void Rust_LifetimesDoNotSwallowBraces()
    {
        var symbols = Parse("src/lib.rs",
            "pub fn longest<'a>(x: &'a str, y: &'a str) -> &'a str {",
            "    if x.len() > y.len() { x } else { y }",
            "}",
            "",
            "pub fn raw() -> &'static str {",
            "    r#\"{ not a brace \"#",
            "}");

        Assert.Equal((1, 3), (Single(symbols, "longest").Line, Single(symbols, "longest").EndLine));
        Assert.Equal((5, 7), (Single(symbols, "raw").Line, Single(symbols, "raw").EndLine));
    }

    [Fact]
    public void StripForBraces_CSharpVerbatimAndRawStrings_AreBlanked()
    {
        var code = Symbols.StripForBraces(CodeLang.CSharp,
        [
            "var a = @\"{",
            "}\"\"{\";",
            "var b = $\"\"\"",
            "  { \"\" }",
            "  \"\"\";",
            "var c = 1; // {",
        ]);

        Assert.DoesNotContain(code, l => l.Contains('{') || l.Contains('}'));
        Assert.Equal("var c = 1; ", code[5]);
    }
}
