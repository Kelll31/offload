using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>Вхождение имени в коде: файл, строка, охватывающий символ и текст строки.</summary>
internal sealed record CodeRef(SourceFile File, int Line, CodeSymbol? Enclosing, string Text);

/// <summary>
/// local_symbols: навигация по коду без чтения файлов в контекст — outline, поиск символов, определение, ссылки, реализации,
/// вызывающие/вызываемые (граф вызовов с глубиной), связанные тесты, публичный API и срез тела символа. Эвристики без компилятора.
/// </summary>
internal static partial class SymbolsTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? action, string? name, string? path, string[]? paths, int depth, int maxResults)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();
        maxResults = Math.Clamp(maxResults <= 0 ? 50 : maxResults, 1, 400);
        depth = Math.Clamp(depth <= 0 ? 1 : depth, 1, 4);

        if (act == "outline")
        {
            var file = CodeIndex.LoadOne(ctx, ToolHelpers.RequireText(path ?? name, "path", 1024));
            return Outline(file!);
        }

        ctx.Progress.Report("Indexing source files…");
        var scope = paths is { Length: > 0 } ? paths : path is { Length: > 0 } && act is "api" or "find" ? [path] : null;
        var index = await CodeIndex.LoadAsync(ctx, scope, codeOnly: true, ctx.Ct).ConfigureAwait(false);
        if (index.Files.Count == 0) throw new ToolException("No source files found in scope. " + index.CoverageNote());
        var files = index.Files;

        string result = act switch
        {
            "find" => Find(files, ToolHelpers.RequireText(name, "name", 200), maxResults),
            "definition" => Definition(files, ToolHelpers.RequireText(name, "name", 200)),
            "references" => References(files, ToolHelpers.RequireText(name, "name", 200), maxResults),
            "implementations" => Implementations(files, ToolHelpers.RequireText(name, "name", 200), maxResults),
            "callers" => CallGraph(files, ToolHelpers.RequireText(name, "name", 200), depth, callers: true, maxResults),
            "callees" => CallGraph(files, ToolHelpers.RequireText(name, "name", 200), depth, callers: false, maxResults),
            "tests" => Tests(files, name, path, maxResults),
            "api" => Api(files, maxResults),
            "slice" => Slice(files, ToolHelpers.RequireText(name, "name", 200), maxResults),
            _ => throw new ToolException("action must be one of: outline, find, definition, references, implementations, callers, callees, tests, api, slice."),
        };
        return result.TrimEnd() + "\n\n" + index.CoverageNote();
    }

    // ───────────────────────── общие запросы (используются и другими инструментами) ─────────────────────────

    /// <summary>«Class.Method» → (Method, Class); просто «Method» → (Method, null).</summary>
    internal static (string Name, string? Container) SplitName(string name)
    {
        var n = name.Trim().TrimEnd('(', ')');
        var dot = n.LastIndexOfAny(['.', ':']);
        if (dot <= 0) return (n, null);
        return (n[(dot + 1)..], n[..dot].Replace("::", ".").TrimEnd(':', '.'));
    }

    internal static List<(SourceFile File, CodeSymbol Symbol)> Definitions(IEnumerable<SourceFile> files, string qualified)
    {
        var (n, container) = SplitName(qualified);
        var list = new List<(SourceFile, CodeSymbol)>();
        foreach (var f in files)
            foreach (var s in f.Symbols)
                if (s.Name == n && (container is null || s.Container is not null && (s.Container == container || s.Container.EndsWith("." + container, StringComparison.Ordinal))))
                    list.Add((f, s));
        return list;
    }

    internal static List<CodeRef> FindReferences(IEnumerable<SourceFile> files, string name, int max, bool includeDefinitions = false)
    {
        var (n, _) = SplitName(name);
        var regex = CodeIndex.WordRegex(n);
        var refs = new List<CodeRef>();
        foreach (var f in files)
        {
            for (var i = 0; i < f.Lines.Length; i++)
            {
                var line = f.Lines[i];
                if (line.Length > 2000 || !line.Contains(n, StringComparison.Ordinal)) continue;
                var m = regex.Match(line);
                if (!m.Success || CodeIndex.IsCommentLine(line) || CodeIndex.InStringOrComment(line, m.Index)) continue;
                var isDef = f.Symbols.Any(s => s.Line == i + 1 && s.Name == n);
                if (isDef && !includeDefinitions) continue;
                refs.Add(new CodeRef(f, i + 1, Symbols.Enclosing(f.Symbols, i + 1), line.Trim()));
                if (refs.Count >= max) return refs;
            }
        }
        return refs;
    }

    internal static bool IsCallable(CodeSymbol s) => s.Kind is "method" or "function" or "ctor" or "property" or "delegate";

    /// <summary>
    /// Ссылка — вызов именно этого символа? Отсекаем очевидно чужие: «Other.Name(» с квалификатором-типом в начале цепочки
    /// (Other ≠ контейнер символа) и вызовы private-членов из других файлов.
    /// </summary>
    internal static bool MayReferTo(CodeRef r, SourceFile defFile, CodeSymbol target)
    {
        if (target.Signature.Contains("private ", StringComparison.Ordinal) && r.File != defFile) return false;
        var owner = target.Container?.Split('.')[^1];
        if (owner is null) return true;
        foreach (Match m in Regex.Matches(r.Text, @"(?<![\w.$])([A-Z]\w*)\s*\.\s*" + Regex.Escape(target.Name) + @"\b"))
        {
            var q = m.Groups[1].Value;
            if (q != owner && q != "this" && q != "base") return false;
        }
        return true;
    }

    // ───────────────────────── действия ─────────────────────────

    internal static string Outline(SourceFile f)
    {
        var sb = new StringBuilder($"{f.Display} ({f.Lines.Length} lines, {f.Lang})\n");
        if (f.Symbols.Count == 0) return sb.Append("(no declarations recognized)").ToString();
        foreach (var s in f.Symbols)
        {
            var indent = s.Container is null ? 0 : s.Container.Count(c => c == '.') + 1;
            sb.Append(new string(' ', 2 * indent)).Append($"{s.Line}-{s.EndLine} {s.Kind} {s.Name}");
            if (!s.IsType && s.Kind != "namespace") sb.Append(": ").Append(s.Signature);
            sb.Append('\n');
        }
        if (f.Truncated) sb.Append("(file truncated: only the first part was analyzed)\n");
        return sb.ToString();
    }

    private static string Find(List<SourceFile> files, string query, int max)
    {
        var q = query.Trim();
        var hits = files.SelectMany(f => f.Symbols.Select(s => (f, s)))
            .Where(x => x.s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.s.QualifiedName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.s.Name.Equals(q, StringComparison.Ordinal) ? 0 : x.s.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 1
                : x.s.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenBy(x => x.s.IsType ? 0 : 1).ThenBy(x => x.f.IsTest).ThenBy(x => x.s.Name.Length)
            .Take(max + 1).ToList();
        if (hits.Count == 0) return $"No symbols matching \"{q}\".";
        var sb = new StringBuilder();
        foreach (var (f, s) in hits.Take(max)) sb.Append($"{f.Display}:{s.Line} {s.Kind} {s.QualifiedName}\n");
        if (hits.Count > max) sb.Append($"… more (raise max_results)\n");
        return sb.ToString();
    }

    private static string Definition(List<SourceFile> files, string name)
    {
        var defs = Definitions(files, name);
        if (defs.Count == 0) return $"No definition of \"{name}\" found (the heuristic parser may miss unusual declarations; try action=find or local_search_code).";
        var sb = new StringBuilder();
        foreach (var (f, s) in defs.Take(20))
            sb.Append($"{f.Display}:{s.Line}-{s.EndLine} {s.Kind} {s.QualifiedName}\n    {s.Signature}\n");
        if (defs.Count > 20) sb.Append($"… {defs.Count - 20} more\n");
        return sb.ToString();
    }

    private static string References(List<SourceFile> files, string name, int max)
    {
        var refs = FindReferences(files, name, max + 1);
        var defs = Definitions(files, name);
        var sb = new StringBuilder();
        if (defs.Count > 0) sb.Append("defined at: ").Append(string.Join(", ", defs.Take(5).Select(d => $"{d.File.Display}:{d.Symbol.Line}"))).Append('\n');
        if (refs.Count == 0) return sb.Append($"No references to \"{name}\" outside its definition.").ToString();
        foreach (var g in refs.Take(max).GroupBy(r => r.File))
        {
            sb.Append(g.Key.Display).Append('\n');
            foreach (var r in g)
                sb.Append($"  {r.Line}: {Short(r.Text, 160)}").Append(r.Enclosing is { } e ? $"   [in {e.QualifiedName}]" : "").Append('\n');
        }
        sb.Append($"{Math.Min(refs.Count, max)} reference(s)").Append(refs.Count > max ? " (limit reached)" : "").Append(" · textual match: may include same-named members");
        return sb.ToString();
    }

    private static string Implementations(List<SourceFile> files, string name, int max)
    {
        var (n, _) = SplitName(name);
        var defs = Definitions(files, name);
        var sb = new StringBuilder();
        var isType = defs.Count == 0 || defs.Any(d => d.Symbol.IsType);
        var found = 0;
        if (isType)
        {
            var word = CodeIndex.WordRegex(n);
            foreach (var f in files)
            {
                foreach (var s in f.Symbols.Where(s => s.IsType && s.Kind is not ("namespace" or "module") && s.Name != n))
                {
                    var head = string.Join(' ', f.Lines.Skip(s.Line - 1).Take(3));
                    var cut = head.IndexOf('{');
                    if (cut > 0) head = head[..cut];
                    if (!InheritsFrom(f.Lang, head, s.Name, word)) continue;
                    sb.Append($"{f.Display}:{s.Line} {s.Kind} {s.QualifiedName}  ← {Short(head.Trim(), 140)}\n");
                    if (++found >= max) break;
                }
                if (found >= max) break;
            }
        }
        else
        {
            foreach (var f in files)
            {
                foreach (var s in f.Symbols.Where(s => s.Name == n && IsCallable(s) && !defs.Any(d => d.File == f && d.Symbol.Line == s.Line)))
                {
                    sb.Append($"{f.Display}:{s.Line} {s.QualifiedName}: {Short(s.Signature, 140)}\n");
                    if (++found >= max) break;
                }
            }
        }
        return found == 0 ? $"No implementations/subtypes of \"{name}\" found." : sb.Append($"{found} found").ToString();
    }

    /// <summary>Объявление типа наследует/реализует base: «: base», «extends/implements base», «class X(base)», «impl base for X».</summary>
    private static bool InheritsFrom(CodeLang lang, string head, string typeName, Regex baseWord)
    {
        switch (lang)
        {
            case CodeLang.CSharp:
            case CodeLang.Kotlin:
            case CodeLang.Swift:
            case CodeLang.Cpp:
                var colon = head.IndexOf(':', head.IndexOf(typeName, StringComparison.Ordinal) is var at and >= 0 ? at : 0);
                return colon >= 0 && baseWord.IsMatch(head[colon..]);
            case CodeLang.Java:
            case CodeLang.TypeScript:
            case CodeLang.Php:
                var ext = Regex.Match(head, @"\b(extends|implements)\b");
                return ext.Success && baseWord.IsMatch(head[ext.Index..]);
            case CodeLang.Python:
                var paren = head.IndexOf('(');
                return paren >= 0 && baseWord.IsMatch(head[paren..]);
            case CodeLang.Rust:
                // «impl Trait for Type»: реализует трейт, если имя стоит до « for ».
                var forAt = head.IndexOf(" for ", StringComparison.Ordinal);
                return head.TrimStart().StartsWith("impl", StringComparison.Ordinal) && forAt > 0 && baseWord.IsMatch(head[..forAt]);
            case CodeLang.Pascal:
                var cls = Regex.Match(head, @"class\s*\(", RegexOptions.IgnoreCase);
                return cls.Success && baseWord.IsMatch(head[cls.Index..]);
            default:
                return false;
        }
    }

    // ───────────────────────── граф вызовов ─────────────────────────

    [GeneratedRegex(@"(?<![\w$])(\w+)\s*(?:<[\w\s,.<>\[\]?]*>)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CallSite();

    private static string CallGraph(List<SourceFile> files, string name, int depth, bool callers, int max)
    {
        var roots = Definitions(files, name).Where(d => IsCallable(d.Symbol)).ToList();
        if (roots.Count == 0 && callers) roots = Definitions(files, name);
        if (roots.Count == 0) return $"No function/method named \"{name}\" found.";
        var callable = callers ? null : files.SelectMany(f => f.Symbols.Where(IsCallable).Select(s => (f, s))).ToLookup(x => x.s.Name, StringComparer.Ordinal);
        var sb = new StringBuilder();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var lines = 0;

        void Walk(SourceFile f, CodeSymbol s, int level)
        {
            if (lines >= max) return;
            var key = f.Display + ":" + s.Line;
            // Уже показанный узел не повторяем (кроме прямых связей корня) — граф остаётся компактным.
            if (!visited.Add(key))
            {
                if (level > 1) return;
                sb.Append(new string(' ', 2 * level)).Append(callers ? "← " : "→ ").Append($"{s.QualifiedName}  {f.Display}:{s.Line} (seen)\n");
                lines++;
                return;
            }
            sb.Append(new string(' ', 2 * level)).Append(level == 0 ? "" : callers ? "← " : "→ ")
              .Append($"{s.QualifiedName}  {f.Display}:{s.Line}").Append('\n');
            lines++;
            if (level >= depth) return;
            if (callers)
            {
                var refs = FindReferences(files, s.Name, 200)
                    .Where(r => r.Enclosing is { } e && IsCallable(e) && !(r.File == f && e.Line == s.Line) && MayReferTo(r, f, s));
                foreach (var g in refs.GroupBy(r => (r.File, r.Enclosing!.Line)).Take(25))
                    Walk(g.Key.File, g.First().Enclosing!, level + 1);
            }
            else
            {
                // Вызовы в теле: имя + получатель («Type.M(», «obj.M(», «M(»); конструкторы «new T(» пропускаем.
                var calls = new List<(string Name, string? Receiver)>();
                for (var i = s.Line; i <= s.EndLine && i <= f.Lines.Length; i++)
                {
                    var text = f.Lines[i - 1];
                    if (CodeIndex.IsCommentLine(text)) continue;
                    foreach (Match m in CallSite().Matches(text))
                    {
                        var callee = m.Groups[1].Value;
                        if (callee == s.Name && i == s.Line || Symbols.Keywords.Contains(callee) || CodeIndex.InStringOrComment(text, m.Index)) continue;
                        var (receiver, isNew) = ReceiverOf(text, m.Index);
                        if (isNew) continue;
                        if (!calls.Contains((callee, receiver))) calls.Add((callee, receiver));
                    }
                }
                var owner = s.Container?.Split('.')[^1];
                var shown = new HashSet<string>(StringComparer.Ordinal);
                var unresolved = new List<string>();
                foreach (var (callee, receiver) in calls)
                {
                    if (shown.Count >= 30) break;
                    var targets = callable![callee].ToList();
                    if (targets.Count == 0) continue;
                    var pick = ResolveCallee(targets, f, owner, receiver);
                    if (pick is null)
                    {
                        if (!unresolved.Contains(callee)) unresolved.Add(callee);
                        continue;
                    }
                    var (tf, ts) = pick.Value;
                    if (!shown.Add(tf.Display + ":" + ts.Line)) continue;
                    Walk(tf, ts, level + 1);
                }
                if (level == 0 && unresolved.Count > 0 && lines < max)
                {
                    sb.Append(new string(' ', 2 * (level + 1))).Append($"(ambiguous or external, not resolved: {string.Join(", ", unresolved.Take(12))})\n");
                    lines++;
                }
            }
        }

        foreach (var (f, s) in roots.Take(3)) Walk(f, s, 0);
        if (lines >= max) sb.Append($"… truncated at max_results={max}\n");
        sb.Append(callers ? "callers are textual references inside functions; " : "callees resolved by name and receiver; ").Append("verify before relying on it");
        return sb.ToString();
    }

    /// <summary>Получатель вызова по тексту слева от имени: «a.b.Name(» → «b», «Name(» → null; признак «new Name(».</summary>
    internal static (string? Receiver, bool IsNew) ReceiverOf(string text, int nameIndex)
    {
        var i = nameIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i >= 0 && text[i] == '.')
        {
            i--;
            while (i >= 0 && (text[i] == '?' || text[i] == '!' || char.IsWhiteSpace(text[i]))) i--;
            if (i >= 0 && text[i] == ')') return ("()", false);
            var end = i;
            while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i--;
            return end > i ? (text[(i + 1)..(end + 1)], false) : ("?", false);
        }
        var before = text[..(i + 1)].TrimEnd();
        return (null, before.EndsWith("new", StringComparison.Ordinal) && (before.Length == 3 || !char.IsLetterOrDigit(before[^4])));
    }

    /// <summary>
    /// Выбор цели вызова среди одноимённых методов: «Type.M(» — только у Type; «obj.M(» — у типа, чьё имя похоже на получатель
    /// (repo → Repo, Progress → ProgressReporter); «M(» — тот же тип, тот же файл или единственный кандидат. Иначе null (не угадываем).
    /// </summary>
    internal static (SourceFile File, CodeSymbol Symbol)? ResolveCallee(List<(SourceFile f, CodeSymbol s)> targets, SourceFile caller, string? callerOwner,
        string? receiver)
    {
        static string Norm(string x) => x.TrimStart('_', '@', '$').ToLowerInvariant();
        static string? OwnerOf(CodeSymbol s) => s.Container?.Split('.')[^1];
        (SourceFile, CodeSymbol)? Best(IEnumerable<(SourceFile f, CodeSymbol s)> list)
        {
            var l = list.ToList();
            if (l.Count == 0) return null;
            var withBody = l.FirstOrDefault(t => t.s.EndLine > t.s.Line);
            return withBody.f is not null ? withBody : l[0];
        }

        // Код вне тестов не вызывает тестовые классы.
        if (!caller.IsTest)
        {
            targets = targets.Where(t => !t.f.IsTest).ToList();
            if (targets.Count == 0) return null;
        }
        if (receiver is null or "this" or "base" or "self" or "super")
        {
            if (Best(targets.Where(t => callerOwner is not null && OwnerOf(t.s) == callerOwner)) is { } sameType) return sameType;
            if (Best(targets.Where(t => t.f == caller)) is { } sameFile) return sameFile;
            var distinctOwners = targets.Select(t => OwnerOf(t.s)).Distinct().Count();
            return distinctOwners == 1 ? Best(targets) : null;
        }
        if (receiver is "()" or "?") return null;
        if (char.IsUpper(receiver[0]) && targets.Any(t => OwnerOf(t.s) == receiver)) return Best(targets.Where(t => OwnerOf(t.s) == receiver));
        var r = Norm(receiver);
        if (r.Length < 3) return null;
        var similar = targets.Where(t => OwnerOf(t.s) is { } o && (Norm(o).Contains(r, StringComparison.Ordinal) || r.Contains(Norm(o), StringComparison.Ordinal)
                                                                 || Norm(o).TrimStart('i') == r)).ToList();
        if (similar.Select(t => OwnerOf(t.s)).Distinct().Count() == 1) return Best(similar);
        // Интерфейс + реализация (IRepo.Save / Repo.Save): берём единственную реализацию с телом.
        var implemented = similar.Where(t => t.s.EndLine > t.s.Line).ToList();
        return implemented.Select(t => OwnerOf(t.s)).Distinct().Count() == 1 ? Best(implemented) : null;
    }

    // ───────────────────────── тесты, API, срез ─────────────────────────

    private static string Tests(List<SourceFile> files, string? name, string? path, int max)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(name)) terms.Add(SplitName(name).Name);
        if (!string.IsNullOrWhiteSpace(path))
        {
            var baseName = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            terms.Add(baseName);
            // Типы из файла тоже ищем в тестах.
            var f = files.FirstOrDefault(x => x.Display.Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (f is not null) terms.AddRange(f.Symbols.Where(s => s.IsType && s.Kind != "namespace").Select(s => s.Name).Take(8));
        }
        terms = terms.Where(t => t.Length >= 3).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0) throw new ToolException("action=tests needs name (symbol) or path (source file).");
        var tests = files.Where(f => f.IsTest).ToList();
        var sb = new StringBuilder();
        var found = 0;
        foreach (var t in tests)
        {
            var byName = terms.Any(term => Path.GetFileNameWithoutExtension(t.Display).StartsWith(term, StringComparison.OrdinalIgnoreCase));
            var refs = FindReferences([t], terms[0], 50);
            foreach (var term in terms.Skip(1)) refs.AddRange(FindReferences([t], term, 50));
            if (!byName && refs.Count == 0) continue;
            var methods = refs.Select(r => r.Enclosing).Where(e => e is not null && IsCallable(e)).Select(e => e!).DistinctBy(e => e.Line).Take(12).ToList();
            sb.Append(t.Display).Append(byName ? " (by name)" : "").Append('\n');
            foreach (var mth in methods) sb.Append($"  {mth.Line}: {mth.QualifiedName}\n");
            if (++found >= max) break;
        }
        return found == 0 ? $"No tests reference {string.Join(", ", terms)}." : sb.Append($"{found} test file(s)").ToString();
    }

    private static string Api(List<SourceFile> files, int max)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var f in files.Where(f => !f.IsTest))
        {
            var pub = f.Symbols.Where(s => s.Kind != "namespace" && IsPublic(f, s)).ToList();
            if (pub.Count == 0) continue;
            sb.Append(f.Display).Append('\n');
            foreach (var s in pub)
            {
                var indent = s.Container is null ? 1 : s.Container.Count(c => c == '.') + 2;
                sb.Append(new string(' ', 2 * indent)).Append(s.IsType ? $"{s.Kind} {s.Name}" : s.Signature).Append('\n');
                if (++count >= max * 4) break;
            }
            if (count >= max * 4)
            {
                sb.Append("… truncated; narrow paths\n");
                break;
            }
        }
        return count == 0 ? "No public API found in scope." : sb.ToString();
    }

    private static bool IsPublic(SourceFile f, CodeSymbol s)
    {
        var sig = s.Signature;
        return f.Lang switch
        {
            CodeLang.CSharp or CodeLang.Java => Regex.IsMatch(sig, @"\b(public|protected)\b") || s.Container is not null && f.Symbols.Any(t => t.IsType && t.Kind == "interface" && s.Container.EndsWith(t.Name, StringComparison.Ordinal)),
            CodeLang.TypeScript => sig.StartsWith("export", StringComparison.Ordinal) || s.Container is not null && !Regex.IsMatch(sig, @"^\s*(private|#)"),
            CodeLang.Rust => sig.StartsWith("pub", StringComparison.Ordinal),
            CodeLang.Go => char.IsUpper(s.Name[0]),
            CodeLang.Python => !s.Name.StartsWith('_') || s.Name is "__init__" or "__call__",
            CodeLang.Kotlin or CodeLang.Swift => !Regex.IsMatch(sig, @"\b(private|internal|fileprivate)\b"),
            _ => true,
        };
    }

    private static string Slice(List<SourceFile> files, string name, int maxLines)
    {
        var defs = Definitions(files, name);
        if (defs.Count == 0) return $"No definition of \"{name}\" found.";
        var limit = Math.Clamp(maxLines < 20 ? 150 : maxLines * 3, 20, 600);
        var sb = new StringBuilder();
        foreach (var (f, s) in defs.Take(3))
        {
            sb.Append($"{f.Display}:{s.Line}-{s.EndLine} {s.Kind} {s.QualifiedName}\n");
            var end = Math.Min(s.EndLine, s.Line + limit - 1);
            sb.Append(CodeIndex.Numbered(f, s.Line, end));
            if (end < s.EndLine) sb.Append($"… {s.EndLine - end} more lines (raise max_results)\n");
        }
        if (defs.Count > 3) sb.Append($"(+{defs.Count - 3} more definitions with this name)\n");
        return sb.ToString();
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
