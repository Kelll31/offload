using System.Text;
using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

/// <summary>Язык исходного файла (по расширению) — определяет правила разбора символов.</summary>
internal enum CodeLang { Unknown, CSharp, TypeScript, Python, Go, Java, Kotlin, Rust, Cpp, Pascal, Php, Ruby, Swift }

/// <summary>
/// Объявление в исходнике. Строки — с 1; EndLine — последняя строка тела (или строка объявления). Params — число объявленных
/// параметров метода/конструктора/делегата/локальной функции (C#, разбор Roslyn); −1 — неизвестно или неприменимо.
/// </summary>
internal sealed record CodeSymbol(string Name, string Kind, int Line, int EndLine, string? Container, string Signature, int Params = -1)
{
    public bool IsType => Kind is "class" or "struct" or "interface" or "enum" or "record" or "trait" or "type" or "impl" or "object" or "namespace" or "module";

    public string QualifiedName => Container is null ? Name : Container + "." + Name;
}

/// <summary>
/// Разбор объявлений без компилятора и language server. C# — синтаксическое дерево Roslyn (Symbols.CSharp.cs, точные границы,
/// многострочные сигнатуры, вложенность). Остальные языки — эвристика: регулярные выражения по строкам (с склейкой
/// многострочных сигнатур) + подсчёт скобок вне строк/комментариев для границ тел (Python — по отступам).
/// Цель — outline и навигация за миллисекунды, а не точная семантика.
/// </summary>
internal static partial class Symbols
{
    public static CodeLang LangOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".csx" => CodeLang.CSharp,
        ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".mts" or ".cts" or ".vue" or ".svelte" => CodeLang.TypeScript,
        ".py" or ".pyw" => CodeLang.Python,
        ".go" => CodeLang.Go,
        ".java" => CodeLang.Java,
        ".kt" or ".kts" => CodeLang.Kotlin,
        ".rs" => CodeLang.Rust,
        ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx" or ".ino" => CodeLang.Cpp,
        ".pas" or ".dpr" or ".dpk" or ".inc" or ".pp" or ".lpr" => CodeLang.Pascal,
        ".php" => CodeLang.Php,
        ".rb" => CodeLang.Ruby,
        ".swift" => CodeLang.Swift,
        _ => CodeLang.Unknown,
    };

    public static bool IsCode(string path) => LangOf(path) != CodeLang.Unknown;

    /// <summary>
    /// Слова, которые не бывают именами методов/функций (отсекают вызовы и управляющие конструкции). Применяются только к правилам
    /// без явного ключевого слова объявления (методы C#/Java/TS, функции C/C++): «fn new» в Rust или «add()» в TS — нормальные имена.
    /// </summary>
    internal static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "catch", "try", "finally", "using", "return", "new", "lock",
        "fixed", "nameof", "typeof", "sizeof", "default", "throw", "await", "yield", "when", "where", "select", "from", "in", "is", "as",
        "function", "async", "var", "let", "const", "checked", "unchecked", "base", "this", "super", "delete", "void", "typeof", "import",
        "export", "match", "loop", "unsafe", "goto", "break", "continue", "not", "and", "or", "with", "elif", "except", "assert", "print",
        "go", "defer", "range", "func", "fn", "operator", "stackalloc", "static",
    };

    public static List<CodeSymbol> Parse(string path, string[] lines)
    {
        var lang = LangOf(path);
        if (lang == CodeLang.Unknown || lines.Length == 0) return [];
        switch (lang)
        {
            case CodeLang.CSharp:
                try { return ParseCSharp(path, lines); }
                catch (Exception ex) when (ex is InsufficientExecutionStackException or InvalidOperationException or ArgumentException)
                {
                    // Патологическая вложенность и т. п. — остаётся эвристика.
                    Offload.Core.Logging.Log.Warn("symbols", $"Roslyn не разобрал {path}: {ex.Message}; эвристический разбор");
                    return ParseBraced(lang, lines);
                }
            case CodeLang.Python:
                return ParsePython(lines);
            default:
                return ParseBraced(lang, lines);
        }
    }

    // ───────────────────────── языки со скобками ─────────────────────────

    /// <summary>ContainerGroup — группа с именем типа-владельца (получатель метода Go).</summary>
    private sealed record Rule(Regex Regex, string Kind, int NameGroup = 1, int ContainerGroup = 0);

    /// <summary>Строки длиннее — не объявления (минифицированный код, данные); до этого предела правила работают с тайм-аутом.</summary>
    private const int MaxDeclarationLine = 4000;

    /// <summary>Многострочная сигнатура склеивается не более чем из стольких строк.</summary>
    private const int MaxJoinedLines = 10;

    private static Regex R(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, O | extra, TimeSpan.FromMilliseconds(250));

    private static Rule[] RulesFor(CodeLang lang) => lang switch
    {
        CodeLang.CSharp => CSharpRules,
        CodeLang.Java => JavaRules,
        CodeLang.Kotlin => KotlinRules,
        CodeLang.TypeScript => TsRules,
        CodeLang.Go => GoRules,
        CodeLang.Rust => RustRules,
        CodeLang.Cpp => CppRules,
        CodeLang.Pascal => PascalRules,
        CodeLang.Php => PhpRules,
        CodeLang.Ruby => RubyRules,
        CodeLang.Swift => SwiftRules,
        _ => [],
    };

    private const RegexOptions O = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string CsMods = @"(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|ref|unsafe|file|new|virtual|override|async|extern|required|volatile|const)\s+)*";

    private static readonly Rule[] CSharpRules =
    [
        new(R(@"^\s*namespace\s+([\w.]+)"), "namespace"),
        new(R(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"(?:record\s+(?:struct|class)|class|struct|interface|enum|record)\s+(\w+)"), "type"),
        new(R(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"delegate\s+[\w<>\[\],.?\s]+?\s(\w+)\s*[<(]"), "delegate"),
        // Конструктор: модификатор доступа + Имя( (имя совпадает с типом — проверяется при разборе).
        new(R(@"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal)(?:\s+(?:static|unsafe|extern))*\s+(\w+)\s*\("), "ctor"),
        new(R(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"(?!return\b|await\b|throw\b|else\b|new\b|yield\b|goto\b|using\b|case\b)(?:\([^()]*\)|[\w<>\[\],.?]+(?:\s*<[^;=]*?>)?)\??\s+(?:[\w.<>]+\.)?(\w+)\s*(?:<[^>()]*>)?\s*\("), "method"),
        new(R(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"(?!return\b|await\b|throw\b|else\b|new\b|using\b)[\w<>\[\],.?]+\??\s+(\w+)\s*(?:\{\s*(?:get|set|init)\b|=>)"), "property"),
        new(R(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"event\s+[\w<>\[\],.?]+\s+(\w+)"), "event"),
    ];

    private static readonly Rule[] JavaRules =
    [
        new(R(@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*(?:(?:public|private|protected|static|final|abstract|sealed|non-sealed|strictfp)\s+)*(?:class|interface|enum|record|@interface)\s+(\w+)"), "type"),
        new(R(@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*(?:(?:public|private|protected|static|final|abstract|synchronized|native|default|strictfp)\s+)*(?:<[^>]+>\s+)?(?!return\b|new\b|throw\b|else\b)[\w<>\[\],.?]+\s+(\w+)\s*\((?![^)]*\)\s*;\s*$)"), "method"),
    ];

    private static readonly Rule[] KotlinRules =
    [
        new(R(@"^\s*(?:(?:public|private|protected|internal|open|abstract|sealed|data|enum|inline|value|annotation|inner)\s+)*(?:class|interface|object)\s+(\w+)"), "type"),
        new(R(@"^\s*(?:(?:public|private|protected|internal|open|override|abstract|suspend|inline|operator|infix|tailrec|external)\s+)*fun\s+(?:<[^>]+>\s*)?(?:[\w.]+\.)?(\w+)\s*\("), "function"),
    ];

    private static readonly Rule[] TsRules =
    [
        new(R(@"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?class\s+(\w+)"), "class"),
        new(R(@"^\s*(?:export\s+)?(?:declare\s+)?interface\s+(\w+)"), "interface"),
        new(R(@"^\s*(?:export\s+)?(?:declare\s+)?(?:const\s+)?enum\s+(\w+)"), "enum"),
        new(R(@"^\s*(?:export\s+)?(?:declare\s+)?type\s+(\w+)\s*(?:<[^=]*>)?\s*="), "type"),
        new(R(@"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:async\s+)?function\s*\*?\s*(\w+)"), "function"),
        new(R(@"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|\w+\s*=>)"), "function"),
        new(R(@"^\s*(?:(?:public|private|protected|static|readonly|async|override|abstract|get|set)\s+)*(?!if\b|for\b|while\b|switch\b|catch\b|function\b|return\b|new\b)(\w+)\s*(?:<[^>]*>)?\s*\([^;]*\)\s*(?::\s*[^{;]+)?\{\s*$"), "method"),
    ];

    private static readonly Rule[] GoRules =
    [
        new(R(@"^type\s+(\w+)\s+(?:struct|interface)\b"), "type"),
        new(R(@"^type\s+(\w+)\s+"), "type"),
        // Метод: получатель «(r *Repo)» / «(Repo)» / «(r Repo[T])» — контейнер, чтобы находился «Repo.Save».
        new(R(@"^func\s+\(\s*(?:\w+\s+)?\*?\s*(\w+)(?:\[[^\]]*\])?\s*\)\s*(\w+)\s*[(\[]"), "method", NameGroup: 2, ContainerGroup: 1),
        new(R(@"^func\s+(?:\([^)]*\)\s*)?(\w+)\s*[(\[]"), "function"),
    ];

    private static readonly Rule[] RustRules =
    [
        new(R(@"^\s*(?:pub(?:\([^)]*\))?\s+)?mod\s+(\w+)"), "module"),
        new(R(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:struct|enum|union|trait)\s+(\w+)"), "type"),
        new(R(@"^\s*impl(?:<[^>]*>)?\s+(?:[\w:<>, ]+\s+for\s+)?([\w:]+)"), "impl"),
        new(R(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:const\s+)?(?:async\s+)?(?:unsafe\s+)?(?:extern\s+""[^""]*""\s+)?fn\s+(\w+)"), "function"),
    ];

    private static readonly Rule[] CppRules =
    [
        new(R(@"^\s*namespace\s+([\w:]+)\s*\{?"), "namespace"),
        new(R(@"^\s*(?:template\s*<[^>]*>\s*)?(?:class|struct|union|enum(?:\s+class)?)\s+(?:\w+\s+)?(\w+)\s*(?:final\s*)?(?::[^;{]*)?\{?\s*$"), "type"),
        new(R(@"^\s*(?:template\s*<[^>]*>\s*)?(?:(?:static|inline|virtual|explicit|constexpr|extern|friend|unsigned|signed|const)\s+)*(?!return\b|else\b|new\b|delete\b)[\w:<>*&,\s]+?[\s*&]+([\w:~]+)\s*\([^;]*\)\s*(?:const\s*)?(?:noexcept\s*)?(?:override\s*)?(?:final\s*)?\{?\s*$"), "function"),
        // Конструктор/деструктор вне класса без типа результата: «Foo::Foo(int a) : x(a) {», «Foo::~Foo() {».
        new(R(@"^\s*((?:\w+::)+~?\w+)\s*\([^;]*\)\s*(?:noexcept\s*)?(?::[^;{]*)?\{?\s*$"), "function"),
    ];

    private static readonly Rule[] PascalRules =
    [
        new(R(@"^\s*(\w+)\s*=\s*(?:packed\s+)?(?:class|record|interface|object)\b(?!\s*of\b)(?!\s*;)", RegexOptions.IgnoreCase), "type"),
        new(R(@"^\s*(?:class\s+)?(?:procedure|function|constructor|destructor)\s+([\w.]+)", RegexOptions.IgnoreCase), "method"),
    ];

    private static readonly Rule[] PhpRules =
    [
        new(R(@"^\s*(?:abstract\s+|final\s+)?(?:class|interface|trait|enum)\s+(\w+)"), "type"),
        new(R(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+&?(\w+)"), "function"),
    ];

    private static readonly Rule[] RubyRules =
    [
        new(R(@"^\s*(?:class|module)\s+([\w:]+)"), "type"),
        new(R(@"^\s*def\s+(?:self\.)?(\w+[?!=]?)"), "method"),
    ];

    private static readonly Rule[] SwiftRules =
    [
        new(R(@"^\s*(?:(?:public|private|fileprivate|internal|open|final)\s+)*(?:class|struct|enum|protocol|extension|actor)\s+(\w+)"), "type"),
        new(R(@"^\s*(?:(?:public|private|fileprivate|internal|open|final|static|override|mutating|class)\s+)*func\s+(\w+)"), "function"),
    ];

    private static List<CodeSymbol> ParseBraced(CodeLang lang, string[] lines)
    {
        var rules = RulesFor(lang);
        var code = StripForBraces(lang, lines);
        var found = new List<(string Name, string Kind, int Line, string? Container, string Signature)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > MaxDeclarationLine || code[i].Trim().Length == 0) continue;
            // Многострочная сигнатура («foo(a,\n b) {») проверяется склеенной, если сама строка не подошла.
            var joined = JoinSignature(lines, code, i);
            var hit = MatchRules(lang, rules, line) ?? (joined is null ? null : MatchRules(lang, rules, joined));
            if (hit is not { } h) continue;
            found.Add((h.Name, h.Kind, i + 1, h.Container, Signature(joined ?? line)));
        }

        var result = new List<CodeSymbol>(found.Count);
        for (var k = 0; k < found.Count; k++)
        {
            var (name, kind, line, container, signature) = found[k];
            var next = k + 1 < found.Count ? found[k + 1].Line : lines.Length + 1;
            var end = lang is CodeLang.Pascal or CodeLang.Ruby ? EndByNext(lines, line, next) : BraceEnd(code, line - 1, next - 1);
            result.Add(new CodeSymbol(name, kind, line, end, container, signature));
        }
        var withContainers = AssignContainers(result);
        // Конструктор C#: имя совпадает с типом-контейнером; иначе это вызов вида «public Foo(...)» — редкость, отбрасываем.
        if (lang == CodeLang.CSharp)
            withContainers = withContainers.Where(s => s.Kind != "ctor" || s.Container?.Split('.')[^1] == s.Name).ToList();
        return withContainers;
    }

    /// <summary>Первое подходящее правило: имя, вид и явный контейнер (получатель Go, «Foo::» в C++).</summary>
    private static (string Name, string Kind, string? Container)? MatchRules(CodeLang lang, Rule[] rules, string text)
    {
        foreach (var rule in rules)
        {
            Match m;
            try { m = rule.Regex.Match(text); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!m.Success) continue;
            var name = m.Groups[rule.NameGroup].Value;
            var container = rule.ContainerGroup > 0 && m.Groups[rule.ContainerGroup].Success ? m.Groups[rule.ContainerGroup].Value : null;
            if (name.Contains("::", StringComparison.Ordinal))
            {
                // C++ «Foo::bar» (определение вне класса) → контейнер Foo, имя bar; Rust «impl a::B» → B.
                var parts = name.Split("::", StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                name = parts[^1];
                if (lang == CodeLang.Cpp && rule.Kind == "function" && parts.Length > 1) container = string.Join(".", parts[..^1]);
            }
            var needsFilter = rule.Kind is "method" or "ctor" or "property" || lang == CodeLang.Cpp;
            if (name.Length == 0 || needsFilter && Keywords.Contains(name)) continue;
            return (name, rule.Kind == "type" ? TypeKind(text) : rule.Kind, container);
        }
        return null;
    }

    /// <summary>
    /// Многострочная сигнатура: если на строке остаётся незакрытая «(» (вне строк и комментариев), склеивает следующие строки
    /// до её закрытия (не больше <see cref="MaxJoinedLines"/> строк и 2000 символов). Иначе — null.
    /// </summary>
    internal static string? JoinSignature(string[] lines, string[] code, int start)
    {
        var depth = ParenDepth(code[start], 0);
        if (depth <= 0) return null;
        var sb = new StringBuilder(lines[start].TrimEnd());
        for (var j = start + 1; j < lines.Length && j < start + MaxJoinedLines; j++)
        {
            var part = lines[j].Trim();
            if (sb.Length + part.Length > 2000 || lines[j].Length > MaxDeclarationLine) return null;
            if (part.Length > 0)
            {
                if (sb[^1] != '(' && !part.StartsWith(')') && !part.StartsWith(',')) sb.Append(' ');
                sb.Append(part);
            }
            depth = ParenDepth(code[j], depth);
            if (depth > 0) continue;
            // Скобка тела на следующей строке (стиль Allman) — тоже часть заголовка.
            if (!code[j].Contains('{', StringComparison.Ordinal) && j + 1 < lines.Length && lines[j + 1].Trim() == "{") sb.Append(" {");
            return sb.ToString();
        }
        return null;
    }

    private static int ParenDepth(string code, int depth)
    {
        foreach (var c in code)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
        }
        return depth;
    }

    private static string TypeKind(string line)
    {
        foreach (var k in new[] { "interface", "struct", "enum", "record", "trait", "union", "protocol", "extension", "actor", "object", "module", "class" })
            if (Regex.IsMatch(line, @"\b" + k + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return k is "union" ? "struct" : k is "protocol" ? "interface" : k;
        return "type";
    }

    /// <summary>Конец тела: от строки объявления до закрытия первой открытой «{». Без «{» до следующего объявления — одна строка.</summary>
    private static int BraceEnd(string[] code, int start, int nextDecl)
    {
        var depth = 0;
        var opened = false;
        var limit = Math.Min(code.Length, start + 20_000);
        for (var i = start; i < limit; i++)
        {
            var line = code[i];
            if (!opened && i > start && i >= nextDecl) return start + 1;
            if (!opened && i > start + 3 && line.Contains(';')) return start + 1;
            foreach (var c in line)
            {
                if (c == '{') { depth++; opened = true; }
                else if (c == '}' && opened)
                {
                    depth--;
                    if (depth == 0) return i + 1;
                }
            }
            if (!opened && (line.TrimEnd().EndsWith(';') || line.Contains("=>") && line.TrimEnd().EndsWith(';'))) return i + 1;
        }
        return opened ? limit : start + 1;
    }

    private static int EndByNext(string[] lines, int line, int next)
    {
        // Pascal/Ruby: до следующего объявления того же или внешнего уровня (приближение), не дальше 2000 строк.
        var end = Math.Min(next - 1, line + 2000);
        while (end > line && lines[end - 1].Trim().Length == 0) end--;
        return Math.Max(line, end);
    }

    /// <summary>Контейнер — ближайший охватывающий тип/пространство имён (по диапазонам строк).</summary>
    private static List<CodeSymbol> AssignContainers(List<CodeSymbol> symbols)
    {
        var result = new List<CodeSymbol>(symbols.Count);
        var stack = new List<CodeSymbol>();
        foreach (var s in symbols)
        {
            while (stack.Count > 0 && stack[^1].EndLine < s.Line) stack.RemoveAt(stack.Count - 1);
            var container = stack.Count > 0 ? string.Join(".", stack.Where(x => x.Kind != "namespace").Select(x => x.Name)) : null;
            if (string.IsNullOrEmpty(container)) container = null;
            // Явный контейнер (получатель метода Go, «Foo::bar» в C++) важнее вложенности по строкам.
            var withC = s with { Container = s.Container ?? container };
            result.Add(withC);
            if ((s.IsType || s.Kind == "namespace") && s.EndLine > s.Line) stack.Add(withC);
        }
        return result;
    }

    // ───────────────────────── Python ─────────────────────────

    [GeneratedRegex(@"^(\s*)(?:async\s+)?def\s+(\w+)|^(\s*)class\s+(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex PyDecl();

    private static List<CodeSymbol> ParsePython(string[] lines)
    {
        // Объявления и отступы — по тексту без строк и комментариев: «def» внутри docstring (""" … """) — не символ,
        // а строки docstring с меньшим отступом не обрывают тело функции.
        var code = StripForBraces(CodeLang.Python, lines);
        var result = new List<CodeSymbol>();
        var stack = new List<(int Indent, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = PyDecl().Match(code[i]);
            if (!m.Success) continue;
            var isClass = m.Groups[4].Success;
            var indent = (isClass ? m.Groups[3].Value : m.Groups[1].Value).Replace("\t", "    ", StringComparison.Ordinal).Length;
            var name = isClass ? m.Groups[4].Value : m.Groups[2].Value;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var end = i + 1;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var t = code[j];
                if (t.Trim().Length == 0) continue;
                var ind = t.Length - t.TrimStart().Length;
                if (ind <= indent && !t.TrimStart().StartsWith(')')) break;
                end = j + 1;
            }
            var container = stack.Count > 0 ? string.Join(".", stack.Select(s => s.Name)) : null;
            var kind = isClass ? "class" : container is not null && stack[^1].Indent < indent && IsClassName(result, stack[^1].Name) ? "method" : "function";
            result.Add(new CodeSymbol(name, kind, i + 1, end, container, Signature(JoinSignature(lines, code, i) ?? lines[i])));
            stack.Add((indent, name));
        }
        return result;
    }

    private static bool IsClassName(List<CodeSymbol> soFar, string name) => soFar.Any(s => s.Name == name && s.Kind == "class");

    // ───────────────────────── общее ─────────────────────────

    /// <summary>
    /// Строки без строковых литералов и комментариев (для подсчёта скобок и поиска объявлений). Литерал заменяется одним пробелом
    /// в месте открытия; многострочные литералы — шаблоны JS/TS (`…${…}…`, с вложенностью), тройные кавычки Python/Java/Kotlin/Swift,
    /// verbatim (@"…") и raw ("""…""") строки C#, raw-строки Go (`…`) и Rust (r#"…"#) — вырезаются целиком, и их строки становятся
    /// пустыми. Приближение: символы препроцессора, heredoc и регулярные выражения-литералы не учитываются.
    /// </summary>
    internal static string[] StripForBraces(CodeLang lang, string[] lines)
    {
        var result = new string[lines.Length];
        var hashComments = lang is CodeLang.Python or CodeLang.Ruby;
        var inBlock = false;
        string? multiEnd = null;     // терминатор открытой многострочной строки
        var multiEscapes = false;    // в ней действует «\»
        var multiDoubled = false;    // verbatim C#: «""» внутри — кавычка
        var inTemplateText = false;  // JS/TS: внутри текста шаблонной строки
        var holes = new Stack<int>(); // JS/TS: глубина «{» в открытых ${…} (вложенные шаблоны)
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var sb = new StringBuilder(line.Length);
            var j = 0;
            while (j < line.Length)
            {
                var c = line[j];
                var next = j + 1 < line.Length ? line[j + 1] : '\0';
                if (inBlock)
                {
                    if (c == '*' && next == '/') { inBlock = false; j += 2; }
                    else if (lang == CodeLang.Pascal && c == '}') { inBlock = false; j++; }
                    else j++;
                    continue;
                }
                if (multiEnd is not null)
                {
                    if (multiEscapes && c == '\\') j += 2;
                    else if (multiDoubled && c == '"' && next == '"') j += 2;
                    else if (string.CompareOrdinal(line, j, multiEnd, 0, multiEnd.Length) == 0) { j += multiEnd.Length; multiEnd = null; }
                    else j++;
                    continue;
                }
                if (inTemplateText)
                {
                    if (c == '\\') j += 2;
                    else if (c == '`') { inTemplateText = false; j++; }
                    else if (c == '$' && next == '{') { holes.Push(0); inTemplateText = false; j += 2; }
                    else j++;
                    continue;
                }
                var emit = holes.Count == 0;
                if (c == '/' && next == '/' && !hashComments) break;
                if (hashComments && c == '#') break;
                if (c == '/' && next == '*') { inBlock = true; j += 2; continue; }
                if (lang == CodeLang.Pascal && c == '{') { inBlock = true; j++; continue; }
                if (holes.Count > 0 && c is '{' or '}')
                {
                    // Код внутри ${…}: «}» на нулевой глубине возвращает в текст шаблона.
                    var depth = holes.Pop();
                    if (c == '{') holes.Push(depth + 1);
                    else if (depth > 0) holes.Push(depth - 1);
                    else inTemplateText = true;
                    j++;
                    continue;
                }
                var open = MultiLineOpening(lang, line, j);
                if (open.Length > 0)
                {
                    (multiEnd, multiEscapes, multiDoubled) = (open.End, open.Escapes, open.Doubled);
                    j += open.Length;
                    if (emit) sb.Append(' ');
                    continue;
                }
                if (c == '`' && lang == CodeLang.TypeScript)
                {
                    inTemplateText = true;
                    j++;
                    if (emit) sb.Append(' ');
                    continue;
                }
                if (c is '"' or '\'' or '`' && !(c == '\'' && lang == CodeLang.Rust && IsRustLifetime(line, j)))
                {
                    // Символ 'x' и однострочные строки: пропускаем до закрывающей кавычки той же строки.
                    var q = c;
                    j++;
                    while (j < line.Length && line[j] != q)
                    {
                        if (line[j] == '\\' && lang != CodeLang.Pascal) j++;
                        j++;
                    }
                    j++;
                    if (emit) sb.Append(' ');
                    continue;
                }
                if (emit) sb.Append(c);
                j++;
            }
            result[i] = sb.ToString();
        }
        return result;
    }

    private readonly record struct MultiOpen(int Length, string End, bool Escapes, bool Doubled);

    /// <summary>Начало многострочного литерала в позиции j (длина префикса и терминатор) или Length = 0.</summary>
    private static MultiOpen MultiLineOpening(CodeLang lang, string line, int j)
    {
        var c = line[j];
        switch (lang)
        {
            case CodeLang.CSharp:
            {
                // raw: $…"""…""" (3+ кавычки, терминатор — столько же); verbatim: @"…", $@"…", @$"…" («""» внутри).
                var k = j;
                while (k < line.Length && line[k] == '$') k++;
                var quotes = 0;
                while (k + quotes < line.Length && line[k + quotes] == '"') quotes++;
                if (quotes >= 3 && (k == j || c == '$')) return new MultiOpen(k - j + quotes, new string('"', quotes), false, false);
                if (c == '@' && At(line, j + 1, "\"")) return new MultiOpen(2, "\"", false, true);
                if (c == '@' && At(line, j + 1, "$\"") || c == '$' && At(line, j + 1, "@\"")) return new MultiOpen(3, "\"", false, true);
                return default;
            }
            case CodeLang.Python:
                if (At(line, j, "\"\"\"")) return new MultiOpen(3, "\"\"\"", true, false);
                if (At(line, j, "'''")) return new MultiOpen(3, "'''", true, false);
                return default;
            case CodeLang.Java or CodeLang.Kotlin or CodeLang.Swift:
                return At(line, j, "\"\"\"") ? new MultiOpen(3, "\"\"\"", lang != CodeLang.Kotlin, false) : default;
            case CodeLang.Go:
                return c == '`' ? new MultiOpen(1, "`", false, false) : default;
            case CodeLang.Rust:
            {
                // r"…", r#"…"#, br##"…"## — без экранирования; обычные "…" в Rust тоже могут занимать несколько строк.
                if (c == '"') return new MultiOpen(1, "\"", true, false);
                var k = j;
                if (c == 'b' && k + 1 < line.Length && line[k + 1] == 'r') k++;
                if (line[k] != 'r' || j > 0 && (char.IsLetterOrDigit(line[j - 1]) || line[j - 1] == '_')) return default;
                var hashes = 0;
                while (k + 1 + hashes < line.Length && line[k + 1 + hashes] == '#') hashes++;
                return At(line, k + 1 + hashes, "\"") ? new MultiOpen(k - j + 2 + hashes, "\"" + new string('#', hashes), false, false) : default;
            }
            default:
                return default;
        }
    }

    private static bool At(string line, int index, string text) =>
        index >= 0 && index + text.Length <= line.Length && string.CompareOrdinal(line, index, text, 0, text.Length) == 0;

    /// <summary>Rust: «'a» / «'static» — время жизни, а не символьный литерал ('a', '\n').</summary>
    private static bool IsRustLifetime(string line, int j) =>
        j + 1 < line.Length && (char.IsLetter(line[j + 1]) || line[j + 1] == '_') && !(j + 2 < line.Length && line[j + 2] == '\'');
    private static string Signature(string line)
    {
        var s = line.Trim();
        // Первая «{» вне строкового литерала: «=> "order {";» не обрезаем посреди строки.
        var brace = -1;
        for (var i = s.IndexOf('{'); i >= 0; i = s.IndexOf('{', i + 1))
            if (!CodeIndex.InStringOrComment(s, i)) { brace = i; break; }
        if (brace > 0) s = s[..brace].TrimEnd();
        if (s.EndsWith("=>", StringComparison.Ordinal)) s = s[..^2].TrimEnd();
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    /// <summary>Самый внутренний символ, содержащий строку (1-based), с приоритетом методов/функций.</summary>
    public static CodeSymbol? Enclosing(IReadOnlyList<CodeSymbol> symbols, int line)
    {
        CodeSymbol? best = null;
        foreach (var s in symbols)
        {
            if (s.Line > line || s.EndLine < line) continue;
            if (best is null || s.Line >= best.Line) best = s;
        }
        return best;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    public static partial Regex Identifier();
}
