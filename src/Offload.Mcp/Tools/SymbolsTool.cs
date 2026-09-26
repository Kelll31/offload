using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Index;
using Offload.Mcp.Infrastructure;
using static Offload.Mcp.Infrastructure.TextUtil;

namespace Offload.Mcp.Tools;

/// <summary>Вхождение имени в коде: файл, строка, охватывающий символ и текст строки.</summary>
internal sealed record CodeRef(SourceFile File, int Line, CodeSymbol? Enclosing, string Text);

/// <summary>
/// local_symbols: навигация по коду без чтения файлов в контекст — outline, поиск символов, определение, ссылки, реализации,
/// вызывающие/вызываемые (граф вызовов с глубиной), связанные тесты, публичный API и срез тела символа. Эвристики без компилятора;
/// данные — из постоянного индекса (<see cref="IndexStore"/>): символы, обратный индекс идентификаторов и таблица вызовов.
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
            if (file is not null) ctx.Stats.AddScanned([file]);
            return Outline(file!);
        }

        ctx.Progress.Report("Indexing source files…");
        var scope = paths is { Length: > 0 } ? paths : path is { Length: > 0 } && act is "api" or "find" ? [path] : null;
        using var index = await CodeIndex.OpenAsync(ctx, scope, codeOnly: true, ctx.Ct).ConfigureAwait(false);
        if (index.Files.Count == 0) throw new ToolException("No source files found in scope. " + index.CoverageNote());
        ctx.Stats.AddScanned(index.TotalChars, index.Files.Count);

        string result = act switch
        {
            "find" => Find(index, ToolHelpers.RequireText(name, "name", 200), maxResults),
            "definition" => Definition(index, ToolHelpers.RequireText(name, "name", 200)),
            "references" => References(index, ToolHelpers.RequireText(name, "name", 200), maxResults),
            "implementations" => Implementations(index, ToolHelpers.RequireText(name, "name", 200), maxResults),
            "callers" => CallGraph(index, ToolHelpers.RequireText(name, "name", 200), depth, callers: true, maxResults),
            "callees" => CallGraph(index, ToolHelpers.RequireText(name, "name", 200), depth, callers: false, maxResults),
            "tests" => Tests(index, name, path, maxResults),
            "api" => Api(index, maxResults),
            "slice" => Slice(index, ToolHelpers.RequireText(name, "name", 200), maxResults),
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

    /// <summary>Определения по индексу: имя (и контейнер, если указан «Тип.Член») — запрос к таблице symbols.</summary>
    internal static List<IndexedSymbol> Definitions(IndexView index, string qualified)
    {
        var (n, container) = SplitName(qualified);
        return index.SymbolsNamed(n)
            .Where(x => container is null || x.Symbol.Container is { } c && (c == container || c.EndsWith("." + container, StringComparison.Ordinal)))
            .ToList();
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
    internal static bool MayReferTo(CodeRef r, SourceFile defFile, CodeSymbol target) => MayReferTo(r, defFile.FullPath, target);

    internal static bool MayReferTo(CodeRef r, string defPath, CodeSymbol target)
    {
        if (target.Signature.Contains("private ", StringComparison.Ordinal) && !string.Equals(r.File.FullPath, defPath, StringComparison.OrdinalIgnoreCase)) return false;
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

    private static string Find(IndexView index, string query, int max)
    {
        var q = query.Trim();
        var hits = index.SymbolsLike(q)
            .Where(x => x.Symbol.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Symbol.QualifiedName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Symbol.Name.Equals(q, StringComparison.Ordinal) ? 0 : x.Symbol.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 1
                : x.Symbol.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenBy(x => x.Symbol.IsType ? 0 : 1).ThenBy(x => x.File.IsTest).ThenBy(x => x.Symbol.Name.Length)
            .Take(max + 1).ToList();
        if (hits.Count == 0) return $"No symbols matching \"{q}\".";
        var sb = new StringBuilder();
        foreach (var (_, f, s) in hits.Take(max)) sb.Append($"{f.Display}:{s.Line} {s.Kind} {s.QualifiedName}\n");
        if (hits.Count > max) sb.Append($"… more (raise max_results)\n");
        return sb.ToString();
    }

    private static string Definition(IndexView index, string name)
    {
        var defs = Definitions(index, name);
        if (defs.Count == 0) return $"No definition of \"{name}\" found (the heuristic parser may miss unusual declarations; try action=find or local_search_code).";
        var sb = new StringBuilder();
        foreach (var (_, f, s) in defs.Take(20))
            sb.Append($"{f.Display}:{s.Line}-{s.EndLine} {s.Kind} {s.QualifiedName}\n    {s.Signature}\n");
        if (defs.Count > 20) sb.Append($"… {defs.Count - 20} more\n");
        return sb.ToString();
    }

    private static string References(IndexView index, string name, int max)
    {
        var refs = index.References(name, max + 1);
        var defs = Definitions(index, name);
        var sb = new StringBuilder();
        if (defs.Count > 0) sb.Append("defined at: ").Append(string.Join(", ", defs.Take(5).Select(d => $"{d.File.Display}:{d.Symbol.Line}"))).Append('\n');
        if (refs.Count == 0) return sb.Append($"No references to \"{name}\" outside its definition.").ToString();
        foreach (var g in refs.Take(max).GroupBy(r => r.File.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(g.First().File.Display).Append('\n');
            foreach (var r in g)
                sb.Append($"  {r.Line}: {Short(r.Text, 160)}").Append(r.Enclosing is { } e ? $"   [in {e.QualifiedName}]" : "").Append('\n');
        }
        sb.Append($"{Math.Min(refs.Count, max)} reference(s)").Append(refs.Count > max ? " (limit reached)" : "").Append(" · textual match: may include same-named members");
        return sb.ToString();
    }

    private static string Implementations(IndexView index, string name, int max)
    {
        var (n, _) = SplitName(name);
        var defs = Definitions(index, name);
        var sb = new StringBuilder();
        var isType = defs.Count == 0 || defs.Any(d => d.Symbol.IsType);
        var found = 0;
        if (isType)
        {
            var word = CodeIndex.WordRegex(n);
            // Наследник упоминает базовый тип в своём объявлении — достаточно файлов, где встречается его имя.
            var candidates = IndexTokens.IsIndexable(n) ? index.Postings(n).Select(p => p.File).Distinct().ToList() : [.. index.Files];
            foreach (var f in candidates)
            {
                var types = index.SymbolsOf(f).Where(x => x.Symbol.IsType && x.Symbol.Kind is not ("namespace" or "module") && x.Symbol.Name != n).ToList();
                if (types.Count == 0) continue;
                var text = index.Text(f);
                if (text is null) continue;
                foreach (var (_, _, s) in types)
                {
                    var head = string.Join(' ', text.Lines.Skip(s.Line - 1).Take(3));
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
            var defIds = defs.Select(d => d.Id).ToHashSet();
            foreach (var (_, f, s) in index.SymbolsNamed(n).Where(x => IsCallable(x.Symbol) && !defIds.Contains(x.Id)))
            {
                sb.Append($"{f.Display}:{s.Line} {s.QualifiedName}: {Short(s.Signature, 140)}\n");
                if (++found >= max) break;
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

    private static string CallGraph(IndexView index, string name, int depth, bool callers, int max)
    {
        var roots = Definitions(index, name).Where(d => IsCallable(d.Symbol)).ToList();
        if (roots.Count == 0 && callers) roots = Definitions(index, name);
        if (roots.Count == 0) return $"No function/method named \"{name}\" found.";
        var callableByName = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        List<IndexedSymbol> CallableNamed(string n)
        {
            if (!callableByName.TryGetValue(n, out var list))
                callableByName[n] = list = index.SymbolsNamed(n).Where(x => IsCallable(x.Symbol)).ToList();
            return list;
        }
        var sb = new StringBuilder();
        var visited = new HashSet<long>();
        var lines = 0;

        void Walk(IndexedSymbol node, int level)
        {
            if (lines >= max) return;
            var (_, f, s) = node;
            // Уже показанный узел не повторяем (кроме прямых связей корня) — граф остаётся компактным, циклы не зацикливают обход.
            if (!visited.Add(node.Id))
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
                foreach (var caller in CallersOf(index, node).Take(25))
                    Walk(caller, level + 1);
            }
            else
            {
                var owner = s.Container?.Split('.')[^1];
                var shown = new HashSet<long>();
                var unresolved = new List<string>();
                // Вызовы в теле: имя + получатель («Type.M(», «obj.M(», «M(»); конструкторы «new T(» пропускаем.
                foreach (var call in index.CallsFrom(node).Where(c => c.Chain != "new").DistinctBy(c => (c.Callee, c.Chain)))
                {
                    if (shown.Count >= 30) break;
                    var targets = CallableNamed(call.Callee);
                    if (targets.Count == 0) continue;
                    var pick = ResolveCallee(targets, f, owner, IndexTokens.ImmediateReceiver(call.Chain));
                    if (pick is null)
                    {
                        if (!unresolved.Contains(call.Callee)) unresolved.Add(call.Callee);
                        continue;
                    }
                    if (!shown.Add(pick.Id)) continue;
                    Walk(pick, level + 1);
                }
                if (level == 0 && unresolved.Count > 0 && lines < max)
                {
                    sb.Append(new string(' ', 2 * (level + 1))).Append($"(ambiguous or external, not resolved: {string.Join(", ", unresolved.Take(12))})\n");
                    lines++;
                }
            }
        }

        foreach (var r in roots.Take(3)) Walk(r, 0);
        if (lines >= max) sb.Append($"… truncated at max_results={max}\n");
        sb.Append(callers ? "callers are call sites (and, for non-methods, textual references) inside functions; " : "callees resolved by name and receiver; ")
          .Append("verify before relying on it");
        return sb.ToString();
    }

    /// <summary>
    /// Вызывающие символа — запрос к графу: для методов/функций/конструкторов/делегатов — места вызова из таблицы calls
    /// (с отсевом «Other.Name(» с чужим типом-квалификатором и вызовов private-членов из других файлов), для остальных
    /// (свойства, события, типы) — текстовые ссылки из обратного индекса внутри функций. Порядок — по файлам и строкам.
    /// </summary>
    internal static List<IndexedSymbol> CallersOf(IndexView index, IndexedSymbol target)
    {
        var s = target.Symbol;
        var result = new List<IndexedSymbol>();
        var seen = new HashSet<long>();
        if (s.Kind is "method" or "function" or "ctor" or "delegate")
        {
            var owner = s.Container?.Split('.')[^1];
            var isPrivate = s.Signature.Contains("private ", StringComparison.Ordinal);
            List<IndexedSymbol>? sameNamed = null;
            foreach (var c in index.CallSitesOf(s.Name))
            {
                if (c.Caller.Id == target.Id || isPrivate && c.Caller.File.Id != target.File.Id) continue;
                if (!IsCallable(c.Caller.Symbol) || !ChainMayReferTo(c.Chain, owner, s.Kind == "ctor")) continue;
                // «M(» / «this.M(» внутри типа, где есть свой M, — вызов своего метода, а не цели из другого типа.
                if (s.Kind != "ctor" && c.Chain is null or "this" or "base" or "self")
                {
                    sameNamed ??= index.SymbolsNamed(s.Name).Where(x => IsCallable(x.Symbol)).ToList();
                    var pick = ResolveCallee(sameNamed, c.Caller.File, c.Caller.Symbol.Container?.Split('.')[^1], IndexTokens.ImmediateReceiver(c.Chain));
                    if (pick is not null && (pick.Symbol.Container != s.Container || s.Container is null && pick.File.Id != target.File.Id)) continue;
                }
                if (seen.Add(c.Caller.Id)) result.Add(c.Caller);
            }
            return result;
        }
        foreach (var r in index.References(s.Name, 200))
        {
            if (r.Enclosing is not { } e || !IsCallable(e) || !MayReferTo(r, target.File.FullPath, s)) continue;
            var file = index.FileByDisplay(r.File.Display);
            if (file is null) continue;
            var caller = index.SymbolsOf(file).FirstOrDefault(x => x.Symbol.Line == e.Line && x.Symbol.Name == e.Name);
            if (caller is null || caller.Id == target.Id) continue;
            if (seen.Add(caller.Id)) result.Add(caller);
        }
        return result;
    }

    /// <summary>
    /// Вызов с цепочкой получателя может относиться к члену типа owner: «Other.Name(» (один сегмент с заглавной, не this/base,
    /// не owner) — нет; «new Name(» — только для конструктора; конструктор вызывается только через «new» или без получателя.
    /// </summary>
    internal static bool ChainMayReferTo(string? chain, string? owner, bool isCtor)
    {
        if (chain == "new") return isCtor;
        if (isCtor) return chain is null;
        if (owner is null || chain is null || chain.Contains('.')) return true;
        return !(chain.Length > 0 && char.IsUpper(chain[0]) && chain != owner && chain is not ("this" or "base"));
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
    internal static IndexedSymbol? ResolveCallee(List<IndexedSymbol> targets, IndexedFile caller, string? callerOwner, string? receiver)
    {
        static string Norm(string x) => x.TrimStart('_', '@', '$').ToLowerInvariant();
        static string? OwnerOf(CodeSymbol s) => s.Container?.Split('.')[^1];
        static IndexedSymbol? Best(IEnumerable<IndexedSymbol> list)
        {
            var l = list.ToList();
            if (l.Count == 0) return null;
            return l.FirstOrDefault(t => t.Symbol.EndLine > t.Symbol.Line) ?? l[0];
        }

        // Код вне тестов не вызывает тестовые классы.
        if (!caller.IsTest)
        {
            targets = targets.Where(t => !t.File.IsTest).ToList();
            if (targets.Count == 0) return null;
        }
        if (receiver is null or "this" or "base" or "self" or "super")
        {
            if (Best(targets.Where(t => callerOwner is not null && OwnerOf(t.Symbol) == callerOwner)) is { } sameType) return sameType;
            if (Best(targets.Where(t => t.File.Id == caller.Id)) is { } sameFile) return sameFile;
            var distinctOwners = targets.Select(t => OwnerOf(t.Symbol)).Distinct().Count();
            return distinctOwners == 1 ? Best(targets) : null;
        }
        if (receiver is "()" or "?") return null;
        if (char.IsUpper(receiver[0]) && targets.Any(t => OwnerOf(t.Symbol) == receiver)) return Best(targets.Where(t => OwnerOf(t.Symbol) == receiver));
        var r = Norm(receiver);
        if (r.Length < 3) return null;
        var similar = targets.Where(t => OwnerOf(t.Symbol) is { } o && (Norm(o).Contains(r, StringComparison.Ordinal) || r.Contains(Norm(o), StringComparison.Ordinal)
                                                                         || Norm(o).TrimStart('i') == r)).ToList();
        if (similar.Select(t => OwnerOf(t.Symbol)).Distinct().Count() == 1) return Best(similar);
        // Интерфейс + реализация (IRepo.Save / Repo.Save): берём единственную реализацию с телом.
        var implemented = similar.Where(t => t.Symbol.EndLine > t.Symbol.Line).ToList();
        return implemented.Select(t => OwnerOf(t.Symbol)).Distinct().Count() == 1 ? Best(implemented) : null;
    }

    // ───────────────────────── тесты, API, срез ─────────────────────────

    private static string Tests(IndexView index, string? name, string? path, int max)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(name)) terms.Add(SplitName(name).Name);
        if (!string.IsNullOrWhiteSpace(path))
        {
            var baseName = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            terms.Add(baseName);
            // Типы из файла тоже ищем в тестах.
            if (index.FileByDisplay(path) is { } f)
                terms.AddRange(index.SymbolsOf(f).Where(x => x.Symbol.IsType && x.Symbol.Kind != "namespace").Select(x => x.Symbol.Name).Take(8));
        }
        terms = terms.Where(t => t.Length >= 3).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0) throw new ToolException("action=tests needs name (symbol) or path (source file).");
        // Тестовые файлы, где встречается хоть один термин, — из обратного индекса; текст читается только у них.
        var mentioning = new HashSet<long>();
        foreach (var term in terms.Where(IndexTokens.IsIndexable))
            foreach (var p in index.Postings(term))
                if (p.File.IsTest) mentioning.Add(p.File.Id);
        var sb = new StringBuilder();
        var found = 0;
        foreach (var t in index.Files.Where(f => f.IsTest))
        {
            var byName = terms.Any(term => Path.GetFileNameWithoutExtension(t.Display).StartsWith(term, StringComparison.OrdinalIgnoreCase));
            var mayMention = mentioning.Contains(t.Id) || terms.Any(term => !IndexTokens.IsIndexable(term));
            if (!byName && !mayMention) continue;
            var refs = new List<CodeRef>();
            if (mayMention)
                foreach (var term in terms)
                    refs.AddRange(index.References(term, 50, where: f => f.Id == t.Id));
            if (!byName && refs.Count == 0) continue;
            var methods = refs.Select(r => r.Enclosing).Where(e => e is not null && IsCallable(e)).Select(e => e!).DistinctBy(e => e.Line).Take(12).ToList();
            sb.Append(t.Display).Append(byName ? " (by name)" : "").Append('\n');
            foreach (var mth in methods) sb.Append($"  {mth.Line}: {mth.QualifiedName}\n");
            if (++found >= max) break;
        }
        return found == 0 ? $"No tests reference {string.Join(", ", terms)}." : sb.Append($"{found} test file(s)").ToString();
    }

    private static string Api(IndexView index, int max)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var f in index.Files.Where(f => !f.IsTest))
        {
            var symbols = index.CodeSymbolsOf(f);
            var pub = symbols.Where(s => s.Kind != "namespace" && IsPublic(f.Lang, symbols, s)).ToList();
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

    private static bool IsPublic(CodeLang lang, IReadOnlyList<CodeSymbol> fileSymbols, CodeSymbol s)
    {
        var sig = s.Signature;
        return lang switch
        {
            CodeLang.CSharp or CodeLang.Java => Regex.IsMatch(sig, @"\b(public|protected)\b") || s.Container is not null && fileSymbols.Any(t => t.IsType && t.Kind == "interface" && s.Container.EndsWith(t.Name, StringComparison.Ordinal)),
            CodeLang.TypeScript => sig.StartsWith("export", StringComparison.Ordinal) || s.Container is not null && !Regex.IsMatch(sig, @"^\s*(private|#)"),
            CodeLang.Rust => sig.StartsWith("pub", StringComparison.Ordinal),
            CodeLang.Go => char.IsUpper(s.Name[0]),
            CodeLang.Python => !s.Name.StartsWith('_') || s.Name is "__init__" or "__call__",
            CodeLang.Kotlin or CodeLang.Swift => !Regex.IsMatch(sig, @"\b(private|internal|fileprivate)\b"),
            _ => true,
        };
    }

    private static string Slice(IndexView index, string name, int maxLines)
    {
        var defs = Definitions(index, name);
        if (defs.Count == 0) return $"No definition of \"{name}\" found.";
        var limit = Math.Clamp(maxLines < 20 ? 150 : maxLines * 3, 20, 600);
        var sb = new StringBuilder();
        foreach (var (_, f, s) in defs.Take(3))
        {
            var text = index.Text(f);
            if (text is null) continue;
            sb.Append($"{f.Display}:{s.Line}-{s.EndLine} {s.Kind} {s.QualifiedName}\n");
            var end = Math.Min(s.EndLine, s.Line + limit - 1);
            sb.Append(CodeIndex.Numbered(text, s.Line, end));
            if (end < s.EndLine) sb.Append($"… {s.EndLine - end} more lines (raise max_results)\n");
        }
        if (defs.Count > 3) sb.Append($"(+{defs.Count - 3} more definitions with this name)\n");
        return sb.ToString();
    }
}
