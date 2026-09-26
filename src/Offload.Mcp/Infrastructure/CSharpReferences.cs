using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Вхождение идентификатора в C#-файле по синтаксическому дереву Roslyn (строки и комментарии сюда не попадают; ссылки из
/// &lt;see cref="…"/&gt; — попадают). Line — с 1, Column — с 0 в строке, Length — длина имени (без «@» verbatim-идентификатора).
/// </summary>
/// <param name="Declaration">Вид объявления, если это имя объявляемого символа (method, ctor, type, property, field, …), иначе null.</param>
/// <param name="DeclaringType">Ближайший охватывающий тип (для объявления типа — внешний тип).</param>
/// <param name="Params">Число объявленных параметров метода (−1 — не метод).</param>
/// <param name="MinArgs">Минимум аргументов вызова (без необязательных и params).</param>
/// <param name="MaxArgs">Максимум аргументов (int.MaxValue при params).</param>
/// <param name="IsExtension">Метод расширения (первый параметр с this).</param>
/// <param name="Receiver">Квалификатор «X.Name» (самое правое имя, с раскрытием using-алиасов), «()» для выражений, null — без квалификатора.</param>
/// <param name="ArgCount">Число аргументов, если имя вызывается («Name(…)», «x.Name(…)»), иначе −1.</param>
/// <param name="InAttribute">Имя стоит в [Атрибуте] — для типа FooAttribute это «[Foo]».</param>
internal sealed record CSharpOccurrence(int Line, int Column, int Length, string? Declaration, string? DeclaringType, int Params, int MinArgs,
    int MaxArgs, bool IsExtension, string? Receiver, int ArgCount, bool InAttribute);

internal static class CSharpReferences
{
    /// <summary>Все вхождения идентификатора name (ValueText, т. е. и @name) в порядке следования.</summary>
    public static List<CSharpOccurrence> Find(string path, string[] lines, string name)
    {
        var text = string.Join('\n', lines);
        if (!text.Contains(name, StringComparison.Ordinal)) return [];
        var map = new Symbols.LineMap(text);
        var seen = new HashSet<int>();
        var result = new List<(int Position, CSharpOccurrence Occurrence)>();
        foreach (var tree in Symbols.CSharpTrees(path, text, documentation: true))
        {
            var root = tree.GetCompilationUnitRoot();
            var aliases = Aliases(root);
            foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
            {
                if (!token.IsKind(SyntaxKind.IdentifierToken) || token.ValueText != name || token.Parent is null) continue;
                // В структурированных «комментариях» (XML-doc, директивы) — только cref.
                if (token.IsPartOfStructuredTrivia() && !token.Parent.AncestorsAndSelf().Any(a => a is CrefSyntax)) continue;
                var start = token.Span.Start + (token.Text.StartsWith('@') ? 1 : 0);
                if (!seen.Add(start)) continue;
                result.Add((start, Describe(token, map.Line(start), map.Column(start), name.Length, aliases)));
            }
        }
        return [.. result.OrderBy(r => r.Position).Select(r => r.Occurrence)];
    }

    /// <summary>using-алиасы файла: «using F = App.Foo;» → F → Foo.</summary>
    private static Dictionary<string, string> Aliases(CompilationUnitSyntax root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var u in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>())
            if (u.Alias is { } alias && Rightmost(u.NamespaceOrType) is { Length: > 0 } target)
                map[alias.Name.Identifier.ValueText] = target;
        return map;
    }

    private static CSharpOccurrence Describe(SyntaxToken token, int line, int column, int length, Dictionary<string, string> aliases)
    {
        var parent = token.Parent!;
        var declaration = parent switch
        {
            MethodDeclarationSyntax m when m.Identifier == token => "method",
            ConstructorDeclarationSyntax c when c.Identifier == token => "ctor",
            DestructorDeclarationSyntax d when d.Identifier == token => "dtor",
            BaseTypeDeclarationSyntax t when t.Identifier == token => "type",
            DelegateDeclarationSyntax d when d.Identifier == token => "delegate",
            PropertyDeclarationSyntax p when p.Identifier == token => "property",
            EventDeclarationSyntax e when e.Identifier == token => "event",
            VariableDeclaratorSyntax v when v.Identifier == token => v.Parent?.Parent is BaseFieldDeclarationSyntax ? "field" : "local",
            ParameterSyntax p when p.Identifier == token => "parameter",
            LocalFunctionStatementSyntax l when l.Identifier == token => "localfunction",
            TypeParameterSyntax t when t.Identifier == token => "typeparameter",
            EnumMemberDeclarationSyntax e when e.Identifier == token => "enummember",
            _ => null,
        };
        var declaringType = parent.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

        int parameters = -1, min = -1, max = -1;
        var isExtension = false;
        if (parent is MethodDeclarationSyntax method && declaration == "method")
        {
            var ps = method.ParameterList.Parameters;
            parameters = ps.Count;
            min = ps.Count(p => p.Default is null && !p.Modifiers.Any(SyntaxKind.ParamsKeyword));
            max = ps.Any(p => p.Modifiers.Any(SyntaxKind.ParamsKeyword)) ? int.MaxValue : ps.Count;
            isExtension = ps.Count > 0 && ps[0].Modifiers.Any(SyntaxKind.ThisKeyword);
        }

        string? receiver = null;
        var argCount = -1;
        var inAttribute = false;
        if (parent is SimpleNameSyntax name)
        {
            ExpressionSyntax called = name;
            switch (name.Parent)
            {
                case MemberAccessExpressionSyntax ma when ma.Name == name:
                    receiver = Rightmost(ma.Expression);
                    called = ma;
                    break;
                case MemberBindingExpressionSyntax mb:
                    // «x?.Name(…)»: квалификатор — выражение условного доступа.
                    var conditional = mb.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(c => c.WhenNotNull.Span.Contains(mb.Span));
                    receiver = conditional is null ? "()" : Rightmost(conditional.Expression);
                    called = mb;
                    break;
                case QualifiedNameSyntax q when q.Right == name:
                    receiver = Rightmost(q.Left);
                    break;
            }
            if (called.Parent is InvocationExpressionSyntax inv && inv.Expression == called) argCount = inv.ArgumentList.Arguments.Count;
            SyntaxNode attributeName = name.Parent is QualifiedNameSyntax qn && qn.Right == name ? qn : name;
            inAttribute = attributeName.Parent is AttributeSyntax;
        }
        if (receiver is not null && aliases.TryGetValue(receiver, out var aliased)) receiver = aliased;
        return new CSharpOccurrence(line, column, length, declaration, declaringType, parameters, min, max, isExtension, receiver, argCount, inAttribute);
    }

    /// <summary>Самое правое имя выражения/типа: «a.B.C» → C, «this» → this, «string» → String, сложное выражение → «()».</summary>
    private static string? Rightmost(SyntaxNode? node) => node switch
    {
        null => null,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberAccessExpressionSyntax m => Rightmost(m.Name),
        QualifiedNameSyntax q => Rightmost(q.Right),
        AliasQualifiedNameSyntax a => Rightmost(a.Name),
        ThisExpressionSyntax => "this",
        BaseExpressionSyntax => "base",
        // Встроенные типы (string.Join, int.Parse) — всегда чужие: с заглавной, как их CLR-имена.
        PredefinedTypeSyntax p => char.ToUpperInvariant(p.Keyword.ValueText[0]) + p.Keyword.ValueText[1..],
        _ => "()",
    };
}
