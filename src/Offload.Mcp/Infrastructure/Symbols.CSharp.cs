using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// C#: точный разбор объявлений через синтаксическое дерево Roslyn (по одному файлу, без компиляции и MSBuild).
/// Результат кэшируется по (путь, размер, время изменения) плюс отпечаток текста — замаскированная и исходная версии файла
/// не путаются, а строки без файла на диске (тесты, фрагменты) кэшируются по отпечатку.
/// </summary>
internal static partial class Symbols
{
    private const int MaxCSharpCacheEntries = 60_000;

    /// <summary>Сколько строк заголовка объявления склеивается в сигнатуру (многострочные параметры, where-ограничения).</summary>
    private const int MaxSignatureLines = 12;

    private sealed record CSharpCacheEntry(long Size, DateTime Mtime, int Length, int Fingerprint, CodeSymbol[] Symbols)
    {
        public bool Matches(long size, DateTime mtime, string text, int fingerprint) =>
            Size == size && Mtime == mtime && Length == text.Length && Fingerprint == fingerprint;
    }

    /// <summary>
    /// Ключ — полный путь; значение — до двух последних версий (замаскированный и исходный текст одного файла читаются
    /// разными инструментами и не должны вытеснять друг друга).
    /// </summary>
    private static readonly ConcurrentDictionary<string, CSharpCacheEntry[]> CSharpCache = new(StringComparer.OrdinalIgnoreCase);

    private static List<CodeSymbol> ParseCSharp(string path, string[] lines)
    {
        var text = string.Join('\n', lines);
        var (size, mtime) = FileStamp(path);
        var fingerprint = string.GetHashCode(text.AsSpan());
        var key = Path.GetFullPath(path);
        var cached = CSharpCache.TryGetValue(key, out var versions) ? versions : [];
        if (cached.FirstOrDefault(e => e.Matches(size, mtime, text, fingerprint)) is { } hit) return [.. hit.Symbols];

        var map = new LineMap(text);
        var result = new List<CodeSymbol>();
        var seen = new HashSet<(string, string, int)>();
        foreach (var tree in CSharpTrees(path, text, documentation: false))
            foreach (var s in new CSharpDeclarationWalker(map, lines).Collect(tree.GetRoot()))
                if (seen.Add((s.Name, s.Kind, s.Line))) result.Add(s);
        // Второе дерево (с другим набором #define) добавляет объявления из неактивных веток — возвращаем порядок по строкам.
        var ordered = result.Select((s, i) => (s, i)).OrderBy(x => x.s.Line).ThenBy(x => x.i).Select(x => x.s).ToList();

        if (CSharpCache.Count >= MaxCSharpCacheEntries) CSharpCache.Clear();
        // Файл изменился (другие размер/время) — старые версии не нужны; иначе храним новую и одну предыдущую.
        var keep = cached.Where(e => e.Size == size && e.Mtime == mtime).Take(1);
        CSharpCache[key] = [new CSharpCacheEntry(size, mtime, text.Length, fingerprint, [.. ordered]), .. keep];
        return ordered;
    }

    /// <summary>Есть ли в кэше разбор именно этого текста файла (для тестов).</summary>
    internal static bool IsCSharpCached(string path, string[] lines)
    {
        var text = string.Join('\n', lines);
        var (size, mtime) = FileStamp(path);
        var fingerprint = string.GetHashCode(text.AsSpan());
        return CSharpCache.TryGetValue(Path.GetFullPath(path), out var versions) && versions.Any(e => e.Matches(size, mtime, text, fingerprint));
    }

    private static (long Size, DateTime Mtime) FileStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : (-1, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (-1, default);
        }
    }

    /// <summary>
    /// Синтаксические деревья файла: без символов препроцессора и, если в файле есть #if/#elif, ещё одно — со всеми символами
    /// из условий (объявления и ссылки из неактивных веток тоже нужны навигации и rename).
    /// </summary>
    internal static List<SyntaxTree> CSharpTrees(string path, string text, bool documentation)
    {
        var kind = path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase) ? SourceCodeKind.Script : SourceCodeKind.Regular;
        var docs = documentation ? DocumentationMode.Parse : DocumentationMode.None;
        var options = new CSharpParseOptions(LanguageVersion.Preview, docs, kind);
        var first = CSharpSyntaxTree.ParseText(text, options);
        var trees = new List<SyntaxTree> { first };
        var root = first.GetCompilationUnitRoot();
        if (!root.ContainsDirectives) return trees;
        var defines = root.DescendantTrivia()
            .Select(t => t.GetStructure() switch
            {
                IfDirectiveTriviaSyntax i => i.Condition,
                ElifDirectiveTriviaSyntax e => e.Condition,
                _ => null,
            })
            .Where(c => c is not null)
            .SelectMany(c => c!.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            .Select(n => n.Identifier.ValueText).Distinct(StringComparer.Ordinal).ToList();
        if (defines.Count > 0) trees.Add(CSharpSyntaxTree.ParseText(text, options.WithPreprocessorSymbols(defines)));
        return trees;
    }

    /// <summary>Номер строки (с 1) по смещению в тексте, склеенном из строк через '\n' (переводы строк Roslyn — U+2028 и т. п. — не учитываются).</summary>
    internal sealed class LineMap
    {
        private readonly int[] _starts;

        public LineMap(string text)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            _starts = [.. starts];
        }

        public int Line(int position)
        {
            var i = Array.BinarySearch(_starts, position);
            return (i >= 0 ? i : ~i - 1) + 1;
        }

        public int Column(int position) => position - _starts[Line(position) - 1];
    }

    /// <summary>Обход объявлений: типы, члены, пространства имён и локальные функции верхнего уровня (top-level statements).</summary>
    private sealed class CSharpDeclarationWalker(LineMap map, string[] lines)
    {
        private readonly List<CodeSymbol> _result = [];

        public List<CodeSymbol> Collect(SyntaxNode root)
        {
            if (root is CompilationUnitSyntax cu) Members(cu.Members, null, null);
            return _result;
        }

        private void Members(SyntaxList<MemberDeclarationSyntax> members, string? container, string? typeName)
        {
            foreach (var m in members) Member(m, container, typeName);
        }

        private void Member(MemberDeclarationSyntax m, string? container, string? typeName)
        {
            switch (m)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    var nsEnd = ns is NamespaceDeclarationSyntax block && !block.CloseBraceToken.IsMissing ? block.CloseBraceToken.SpanStart
                        : ns is FileScopedNamespaceDeclarationSyntax fs ? fs.SemicolonToken.SpanStart : ns.Span.End;
                    Add(ns.Name.ToString(), "namespace", ns, map.Line(nsEnd), null, HeaderEnd(ns), -1);
                    Members(ns.Members, container, typeName);
                    break;
                case BaseTypeDeclarationSyntax t:
                    var name = t.Identifier.ValueText;
                    if (name.Length == 0) break;
                    var kind = t switch
                    {
                        ClassDeclarationSyntax => "class",
                        StructDeclarationSyntax => "struct",
                        InterfaceDeclarationSyntax => "interface",
                        EnumDeclarationSyntax => "enum",
                        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "struct" : "record",
                        _ => "type",
                    };
                    Add(name, kind, t, EndLine(t), container, HeaderEnd(t), -1);
                    if (t is TypeDeclarationSyntax td) Members(td.Members, container is null ? name : container + "." + name, name);
                    break;
                case DelegateDeclarationSyntax d:
                    Add(d.Identifier.ValueText, "delegate", d, EndLine(d), container, HeaderEnd(d), d.ParameterList.Parameters.Count);
                    break;
                case MethodDeclarationSyntax md:
                    Add(md.Identifier.ValueText, "method", md, EndLine(md), container, HeaderEnd(md), md.ParameterList.Parameters.Count);
                    break;
                case ConstructorDeclarationSyntax c:
                    // «public Bar(int x)» внутри Foo Roslyn тоже считает конструктором (с ошибкой) — это не объявление.
                    if (c.Identifier.ValueText == typeName)
                        Add(c.Identifier.ValueText, "ctor", c, EndLine(c), container, HeaderEnd(c), c.ParameterList.Parameters.Count);
                    break;
                case PropertyDeclarationSyntax p:
                    Add(p.Identifier.ValueText, "property", p, EndLine(p), container, HeaderEnd(p), -1);
                    break;
                case EventDeclarationSyntax e:
                    Add(e.Identifier.ValueText, "event", e, EndLine(e), container, HeaderEnd(e), -1);
                    break;
                case BaseFieldDeclarationSyntax f:
                    var fieldKind = f is EventFieldDeclarationSyntax ? "event" : "field";
                    foreach (var v in f.Declaration.Variables)
                        Add(v.Identifier.ValueText, fieldKind, f, EndLine(f), container, v.Identifier.Span.End, -1, trimSemicolon: true);
                    break;
                case GlobalStatementSyntax { Statement: LocalFunctionStatementSyntax lf }:
                    Add(lf.Identifier.ValueText, "function", lf, EndLine(lf), container, HeaderEnd(lf), lf.ParameterList.Parameters.Count);
                    break;
            }
        }

        private void Add(string name, string kind, SyntaxNode node, int endLine, string? container, int headerEnd, int parameters, bool trimSemicolon = false)
        {
            if (name.Length == 0) return;
            var startPos = StartPosition(node);
            var line = map.Line(startPos);
            var headerLine = Math.Max(line, map.Line(Math.Max(startPos, headerEnd)));
            var signature = HeaderSignature(line, Math.Min(headerLine, line + MaxSignatureLines - 1));
            if (trimSemicolon && signature.EndsWith(';')) signature = signature[..^1].TrimEnd();
            _result.Add(new CodeSymbol(name, kind, line, Math.Max(line, endLine), container, signature, parameters));
        }

        /// <summary>Начало объявления без атрибутов на отдельных строках: первый модификатор или ключевое слово.</summary>
        private static int StartPosition(SyntaxNode node)
        {
            SyntaxList<AttributeListSyntax> attrs = node switch
            {
                MemberDeclarationSyntax m => m.AttributeLists,
                LocalFunctionStatementSyntax lf => lf.AttributeLists,
                _ => default,
            };
            var token = attrs.Count > 0 ? attrs[^1].GetLastToken().GetNextToken() : node.GetFirstToken();
            return token.RawKind == 0 || token.SpanStart > node.Span.End ? node.SpanStart : token.SpanStart;
        }

        private int EndLine(SyntaxNode node)
        {
            var last = node.GetLastToken(includeZeroWidth: false);
            return map.Line(last.RawKind == 0 ? node.Span.End : last.SpanStart);
        }

        /// <summary>Где кончается заголовок: «{» тела/аксессоров, «=>» выражения-тела или конец объявления.</summary>
        private static int HeaderEnd(SyntaxNode node)
        {
            SyntaxToken open = node switch
            {
                NamespaceDeclarationSyntax n => n.OpenBraceToken,
                FileScopedNamespaceDeclarationSyntax fs => fs.SemicolonToken,
                BaseTypeDeclarationSyntax t => t.OpenBraceToken,
                BaseMethodDeclarationSyntax m when m.Body is not null => m.Body.OpenBraceToken,
                BaseMethodDeclarationSyntax { ExpressionBody: { } eb } => eb.ArrowToken,
                LocalFunctionStatementSyntax { Body: { } b } => b.OpenBraceToken,
                LocalFunctionStatementSyntax { ExpressionBody: { } eb } => eb.ArrowToken,
                PropertyDeclarationSyntax { AccessorList: { } al } => al.OpenBraceToken,
                PropertyDeclarationSyntax { ExpressionBody: { } eb } => eb.ArrowToken,
                EventDeclarationSyntax { AccessorList: { } al } => al.OpenBraceToken,
                _ => default,
            };
            return open.RawKind != 0 && !open.IsMissing ? open.SpanStart : node.Span.End;
        }

        /// <summary>Сигнатура как в эвристическом разборе, но многострочный заголовок склеивается в одну строку.</summary>
        private string HeaderSignature(int line, int headerLine)
        {
            if (headerLine <= line || headerLine > lines.Length) return Signature(lines[line - 1]);
            var sb = new StringBuilder();
            for (var i = line; i <= headerLine; i++)
            {
                var part = lines[i - 1].Trim();
                if (part.Length == 0) continue;
                if (sb.Length > 0 && sb[^1] != '(' && sb[^1] != '<' && !part.StartsWith(')') && !part.StartsWith('>') && !part.StartsWith(',')) sb.Append(' ');
                sb.Append(part);
                if (sb.Length > 600) break;
            }
            return Signature(sb.ToString());
        }
    }
}
