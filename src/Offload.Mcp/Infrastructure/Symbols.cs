using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

/// <summary>Язык исходного файла (по расширению) — определяет правила разбора символов.</summary>
internal enum CodeLang { Unknown, CSharp, TypeScript, Python, Go, Java, Kotlin, Rust, Cpp, Pascal, Php, Ruby, Swift }

/// <summary>Объявление в исходнике. Строки — с 1; EndLine — последняя строка тела (или строка объявления).</summary>
internal sealed record CodeSymbol(string Name, string Kind, int Line, int EndLine, string? Container, string Signature)
{
    public bool IsType => Kind is "class" or "struct" or "interface" or "enum" or "record" or "trait" or "type" or "impl" or "object" or "namespace" or "module";

    public string QualifiedName => Container is null ? Name : Container + "." + Name;
}

/// <summary>
/// Эвристический (без компилятора и language server) разбор объявлений: регулярные выражения по строкам + подсчёт скобок
/// для границ тел (Python — по отступам). Цель — outline и навигация за миллисекунды, а не точная семантика.
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
        var raw = lang == CodeLang.Python ? ParsePython(lines) : ParseBraced(lang, lines);
        return raw;
    }

    // ───────────────────────── языки со скобками ─────────────────────────

    private sealed record Rule(Regex Regex, string Kind, int NameGroup = 1);

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
        new(new Regex(@"^\s*namespace\s+([\w.]+)", O), "namespace"),
        new(new Regex(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"(?:record\s+(?:struct|class)|class|struct|interface|enum|record)\s+(\w+)", O), "type"),
        new(new Regex(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"delegate\s+[\w<>\[\],.?\s]+?\s(\w+)\s*[<(]", O), "delegate"),
        // Конструктор: модификатор доступа + Имя( (имя совпадает с типом — проверяется при разборе).
        new(new Regex(@"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal)(?:\s+(?:static|unsafe|extern))*\s+(\w+)\s*\(", O), "ctor"),
        new(new Regex(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"(?!return\b|await\b|throw\b|else\b|new\b|yield\b|goto\b|using\b|case\b)(?:\([^()]*\)|[\w<>\[\],.?]+(?:\s*<[^;=]*?>)?)\??\s+(?:[\w.<>]+\.)?(\w+)\s*(?:<[^>()]*>)?\s*\(", O), "method"),
        new(new Regex(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"(?!return\b|await\b|throw\b|else\b|new\b|using\b)[\w<>\[\],.?]+\??\s+(\w+)\s*(?:\{\s*(?:get|set|init)\b|=>)", O), "property"),
        new(new Regex(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"event\s+[\w<>\[\],.?]+\s+(\w+)", O), "event"),
    ];

    private static readonly Rule[] JavaRules =
    [
        new(new Regex(@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*(?:(?:public|private|protected|static|final|abstract|sealed|non-sealed|strictfp)\s+)*(?:class|interface|enum|record|@interface)\s+(\w+)", O), "type"),
        new(new Regex(@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*(?:(?:public|private|protected|static|final|abstract|synchronized|native|default|strictfp)\s+)*(?:<[^>]+>\s+)?(?!return\b|new\b|throw\b|else\b)[\w<>\[\],.?]+\s+(\w+)\s*\((?![^)]*\)\s*;\s*$)", O), "method"),
    ];

    private static readonly Rule[] KotlinRules =
    [
        new(new Regex(@"^\s*(?:(?:public|private|protected|internal|open|abstract|sealed|data|enum|inline|value|annotation|inner)\s+)*(?:class|interface|object)\s+(\w+)", O), "type"),
        new(new Regex(@"^\s*(?:(?:public|private|protected|internal|open|override|abstract|suspend|inline|operator|infix|tailrec|external)\s+)*fun\s+(?:<[^>]+>\s*)?(?:[\w.]+\.)?(\w+)\s*\(", O), "function"),
    ];

    private static readonly Rule[] TsRules =
    [
        new(new Regex(@"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?class\s+(\w+)", O), "class"),
        new(new Regex(@"^\s*(?:export\s+)?(?:declare\s+)?interface\s+(\w+)", O), "interface"),
        new(new Regex(@"^\s*(?:export\s+)?(?:declare\s+)?(?:const\s+)?enum\s+(\w+)", O), "enum"),
        new(new Regex(@"^\s*(?:export\s+)?(?:declare\s+)?type\s+(\w+)\s*(?:<[^=]*>)?\s*=", O), "type"),
        new(new Regex(@"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:async\s+)?function\s*\*?\s*(\w+)", O), "function"),
        new(new Regex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|\w+\s*=>)", O), "function"),
        new(new Regex(@"^\s*(?:(?:public|private|protected|static|readonly|async|override|abstract|get|set)\s+)*(?!if\b|for\b|while\b|switch\b|catch\b|function\b|return\b|new\b)(\w+)\s*(?:<[^>]*>)?\s*\([^;]*\)\s*(?::\s*[^{;]+)?\{\s*$", O), "method"),
    ];

    private static readonly Rule[] GoRules =
    [
        new(new Regex(@"^type\s+(\w+)\s+(?:struct|interface)\b", O), "type"),
        new(new Regex(@"^type\s+(\w+)\s+", O), "type"),
        new(new Regex(@"^func\s+(?:\([^)]*\)\s*)?(\w+)\s*[(\[]", O), "function"),
    ];

    private static readonly Rule[] RustRules =
    [
        new(new Regex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?mod\s+(\w+)", O), "module"),
        new(new Regex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:struct|enum|union|trait)\s+(\w+)", O), "type"),
        new(new Regex(@"^\s*impl(?:<[^>]*>)?\s+(?:[\w:<>, ]+\s+for\s+)?([\w:]+)", O), "impl"),
        new(new Regex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:const\s+)?(?:async\s+)?(?:unsafe\s+)?(?:extern\s+""[^""]*""\s+)?fn\s+(\w+)", O), "function"),
    ];

    private static readonly Rule[] CppRules =
    [
        new(new Regex(@"^\s*namespace\s+([\w:]+)\s*\{?", O), "namespace"),
        new(new Regex(@"^\s*(?:template\s*<[^>]*>\s*)?(?:class|struct|union|enum(?:\s+class)?)\s+(?:\w+\s+)?(\w+)\s*(?:final\s*)?(?::[^;{]*)?\{?\s*$", O), "type"),
        new(new Regex(@"^\s*(?:template\s*<[^>]*>\s*)?(?:(?:static|inline|virtual|explicit|constexpr|extern|friend|unsigned|signed|const)\s+)*(?!return\b|else\b|new\b|delete\b)[\w:<>*&,\s]+?[\s*&]+([\w:~]+)\s*\([^;]*\)\s*(?:const\s*)?(?:noexcept\s*)?(?:override\s*)?(?:final\s*)?\{?\s*$", O), "function"),
    ];

    private static readonly Rule[] PascalRules =
    [
        new(new Regex(@"^\s*(\w+)\s*=\s*(?:packed\s+)?(?:class|record|interface|object)\b(?!\s*of\b)(?!\s*;)", O | RegexOptions.IgnoreCase), "type"),
        new(new Regex(@"^\s*(?:class\s+)?(?:procedure|function|constructor|destructor)\s+([\w.]+)", O | RegexOptions.IgnoreCase), "method"),
    ];

    private static readonly Rule[] PhpRules =
    [
        new(new Regex(@"^\s*(?:abstract\s+|final\s+)?(?:class|interface|trait|enum)\s+(\w+)", O), "type"),
        new(new Regex(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+&?(\w+)", O), "function"),
    ];

    private static readonly Rule[] RubyRules =
    [
        new(new Regex(@"^\s*(?:class|module)\s+([\w:]+)", O), "type"),
        new(new Regex(@"^\s*def\s+(?:self\.)?(\w+[?!=]?)", O), "method"),
    ];

    private static readonly Rule[] SwiftRules =
    [
        new(new Regex(@"^\s*(?:(?:public|private|fileprivate|internal|open|final)\s+)*(?:class|struct|enum|protocol|extension|actor)\s+(\w+)", O), "type"),
        new(new Regex(@"^\s*(?:(?:public|private|fileprivate|internal|open|final|static|override|mutating|class)\s+)*func\s+(\w+)", O), "function"),
    ];

    private static List<CodeSymbol> ParseBraced(CodeLang lang, string[] lines)
    {
        var rules = RulesFor(lang);
        var code = StripForBraces(lang, lines);
        var found = new List<(string Name, string Kind, int Line)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 400 || code[i].Trim().Length == 0) continue;
            foreach (var rule in rules)
            {
                var m = rule.Regex.Match(line);
                if (!m.Success) continue;
                var name = m.Groups[rule.NameGroup].Value;
                var needsFilter = rule.Kind is "method" or "ctor" or "property" || lang == CodeLang.Cpp;
                if (name.Length == 0 || needsFilter && Keywords.Contains(name)) continue;
                var kind = rule.Kind == "type" ? TypeKind(line) : rule.Kind;
                found.Add((name, kind, i + 1));
                break;
            }
        }

        var result = new List<CodeSymbol>(found.Count);
        for (var k = 0; k < found.Count; k++)
        {
            var (name, kind, line) = found[k];
            var next = k + 1 < found.Count ? found[k + 1].Line : lines.Length + 1;
            var end = lang is CodeLang.Pascal or CodeLang.Ruby ? EndByNext(lines, line, next) : BraceEnd(code, line - 1, next - 1);
            result.Add(new CodeSymbol(name, kind, line, end, null, Signature(lines[line - 1])));
        }
        var withContainers = AssignContainers(result);
        // Конструктор C#: имя совпадает с типом-контейнером; иначе это вызов вида «public Foo(...)» — редкость, отбрасываем.
        if (lang == CodeLang.CSharp)
            withContainers = withContainers.Where(s => s.Kind != "ctor" || s.Container?.Split('.')[^1] == s.Name).ToList();
        return withContainers;
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
            var withC = s with { Container = container };
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
        var result = new List<CodeSymbol>();
        var stack = new List<(int Indent, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = PyDecl().Match(lines[i]);
            if (!m.Success) continue;
            var isClass = m.Groups[4].Success;
            var indent = (isClass ? m.Groups[3].Value : m.Groups[1].Value).Replace("\t", "    ").Length;
            var name = isClass ? m.Groups[4].Value : m.Groups[2].Value;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var end = i + 1;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var t = lines[j];
                if (t.Trim().Length == 0 || t.TrimStart().StartsWith('#')) continue;
                var ind = (t.Length - t.TrimStart().Length);
                if (ind <= indent && !t.TrimStart().StartsWith(')')) break;
                end = j + 1;
            }
            var container = stack.Count > 0 ? string.Join(".", stack.Select(s => s.Name)) : null;
            var kind = isClass ? "class" : container is not null && stack[^1].Indent < indent && IsClassName(result, stack[^1].Name) ? "method" : "function";
            result.Add(new CodeSymbol(name, kind, i + 1, end, container, Signature(lines[i])));
            stack.Add((indent, name));
        }
        return result;
    }

    private static bool IsClassName(List<CodeSymbol> soFar, string name) => soFar.Any(s => s.Name == name && s.Kind == "class");

    // ───────────────────────── общее ─────────────────────────

    /// <summary>Строки без строковых литералов и комментариев (для подсчёта скобок). Приближение: без интерполяций и raw-строк.</summary>
    internal static string[] StripForBraces(CodeLang lang, string[] lines)
    {
        var result = new string[lines.Length];
        var inBlock = false;
        var hashComments = lang is CodeLang.Python or CodeLang.Ruby;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var sb = new System.Text.StringBuilder(line.Length);
            var j = 0;
            while (j < line.Length)
            {
                var c = line[j];
                if (inBlock)
                {
                    if (c == '*' && j + 1 < line.Length && line[j + 1] == '/') { inBlock = false; j += 2; }
                    else if (lang == CodeLang.Pascal && c == '}') { inBlock = false; j++; }
                    else j++;
                    continue;
                }
                if (c == '/' && j + 1 < line.Length && line[j + 1] == '/') break;
                if (hashComments && c == '#') break;
                if (c == '/' && j + 1 < line.Length && line[j + 1] == '*') { inBlock = true; j += 2; continue; }
                if (lang == CodeLang.Pascal && c == '{') { inBlock = true; j++; continue; }
                if (c is '"' or '\'' or '`')
                {
                    // Символ 'x' и строки: пропускаем до закрывающей кавычки той же строки.
                    var q = c;
                    j++;
                    while (j < line.Length && line[j] != q)
                    {
                        if (line[j] == '\\' && lang != CodeLang.Pascal) j++;
                        j++;
                    }
                    j++;
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c);
                j++;
            }
            result[i] = sb.ToString();
        }
        return result;
    }

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
