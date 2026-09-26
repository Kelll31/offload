using System.Text;
using Offload.Core;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_refactor: rename — детерминированно (без модели): в C# — по синтаксическому дереву Roslyn (строки/комментарии не трогаются,
/// cref и [Атрибут] — да, using-алиасы раскрываются, перегрузка выбирается по числу параметров «Type.M(int, string)»), в остальных
/// языках — вхождения как слова вне строк/комментариев; переименование файла типа, проверка конфликтов, снимок, проверка командой
/// и автооткат. extract_method / extract_class /
/// move_symbol — точное задание локальному агенту в git-песочнице (local_agent_task) с проверкой и слиянием.
/// </summary>
internal static class RefactorTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? action, string? name, string? newName, string[]? paths, string? targetFile,
        string? lines, string? instructions, string? verifyCommand, bool dryRun, bool force, bool renameFile)
    {
        var act = (action ?? "rename").Trim().ToLowerInvariant();
        var symbol = ToolHelpers.RequireText(name, "name", 300);
        return act switch
        {
            "rename" => await RenameAsync(ctx, symbol, ToolHelpers.RequireText(newName, "new_name", 200), paths, verifyCommand, dryRun, force, renameFile)
                .ConfigureAwait(false),
            "extract_method" or "extract_class" or "move_symbol" or "inline" or "change_signature" =>
                await AgentRefactorAsync(ctx, act, symbol, newName, targetFile, lines, instructions, verifyCommand, paths).ConfigureAwait(false),
            _ => throw new ToolException("action must be rename, extract_method, extract_class, move_symbol, inline or change_signature."),
        };
    }

    private static async Task<string> RenameAsync(ToolContext ctx, string name, string newName, string[]? paths, string? verifyCommand, bool dryRun,
        bool force, bool renameFile)
    {
        var (baseName, wantParams, emptyParens) = ParseOverloadSpec(name);
        var (oldSimple, container) = SymbolsTool.SplitName(baseName);
        if (!Symbols.Identifier().IsMatch(newName)) throw new ToolException($"new_name '{newName}' is not a valid identifier.");
        if (newName == oldSimple) throw new ToolException("new_name equals the current name.");
        var verify = string.IsNullOrWhiteSpace(verifyCommand) ? null : VerifyCommand.Validate(verifyCommand, ctx.Cfg.Mcp.VerifyCommandAllowlist);

        ctx.Progress.Report("Indexing source files…");
        // Исходный текст: вхождения считаются по тем же строкам, которые потом переписываются с диска.
        var index = await CodeIndex.LoadAsync(ctx, paths, codeOnly: true, ctx.Ct, unredacted: true).ConfigureAwait(false);
        var defs = SymbolsTool.Definitions(index.Files, baseName);
        if (defs.Count == 0) throw new ToolException($"No definition of '{baseName}' found in scope; check the name (action=find in local_symbols) or widen paths.");

        // Перегрузки (C#, число параметров известно): «Svc.Run(int, string)» или «Svc.Run(2)» — только методы с таким числом
        // параметров; остальные перегрузки того же типа не трогаются. «Run()» без перегрузок с 0 параметров — весь метод, как раньше.
        var excluded = new List<(SourceFile File, CodeSymbol Symbol)>();
        if (wantParams is int want && defs.Any(IsKnownMethod))
        {
            var matching = defs.Where(d => IsKnownMethod(d) && d.Symbol.Params == want).ToList();
            if (matching.Count == 0 && !emptyParens)
                throw new ToolException($"No overload of '{baseName}' with {want} parameter(s); overloads: " +
                                        string.Join(", ", defs.Where(IsKnownMethod).Take(6).Select(d => $"{d.Symbol.QualifiedName}({d.Symbol.Params} params) {d.File.Display}:{d.Symbol.Line}")) + ".");
            if (matching.Count > 0)
            {
                excluded = defs.Where(d => IsKnownMethod(d) && d.Symbol.Params != want).ToList();
                defs = matching;
            }
            else wantParams = null;
        }
        else wantParams = null;
        var langs = defs.Select(d => d.File.Lang).ToHashSet();

        // Одноимённые объявления в других контейнерах переименовались бы тоже — только с force или более узкими paths.
        var sameNamed = SymbolsTool.Definitions(index.Files.Where(f => langs.Contains(f.Lang)), oldSimple)
            .Where(d => !defs.Any(x => x.File == d.File && x.Symbol.Line == d.Symbol.Line))
            .Where(d => !excluded.Any(x => x.File == d.File && x.Symbol.Line == d.Symbol.Line)).ToList();
        if (container is null)
        {
            // Неквалифицированное имя: разные объявления (кроме конструкторов и частей partial-типа) — тоже неоднозначность.
            var distinct = defs.Where(d => !(d.Symbol.Kind == "ctor" || d.Symbol.Container?.Split('.')[^1] == d.Symbol.Name))
                .GroupBy(d => d.Symbol.QualifiedName).Select(g => g.First()).ToList();
            if (distinct.Count > 1) sameNamed.AddRange(distinct.Skip(1));
        }
        if (sameNamed.Count > 0 && !force)
            throw new ToolException($"'{oldSimple}' is also declared elsewhere ({string.Join(", ", sameNamed.Take(5).Select(d => $"{d.Symbol.QualifiedName} {d.File.Display}:{d.Symbol.Line}"))}); " +
                                    "a textual rename would change those too. Narrow paths, or pass force=true if that is intended.");
        var conflicts = SymbolsTool.Definitions(index.Files.Where(f => langs.Contains(f.Lang)), newName)
            .Where(d => container is null || d.Symbol.Container == defs[0].Symbol.Container).ToList();
        if (conflicts.Count > 0 && !force)
            throw new ToolException($"'{newName}' already exists ({string.Join(", ", conflicts.Take(3).Select(d => $"{d.File.Display}:{d.Symbol.Line}"))}); pass force=true to rename anyway.");

        // Для членов типа «Other.Name» с квалификатором-типом не из проекта (Task.Run, File.Delete) — чужой API, не трогаем.
        var isMember = !defs.Any(d => d.Symbol.IsType);
        var owner = defs[0].Symbol.Container?.Split('.')[^1];
        var projectTypes = index.Files.SelectMany(f => f.Symbols).Where(s => s.IsType).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var plan = new RenamePlan(oldSimple, newName, isMember, owner, projectTypes, wantParams,
            defs.Select(d => d.Symbol.Container?.Split('.')[^1]).OfType<string>().ToHashSet(StringComparer.Ordinal));
        var files = index.Files.Where(f => langs.Contains(f.Lang)).ToList();
        if (wantParams is not null)
            foreach (var f in files.Where(f => f.Lang == CodeLang.CSharp)) plan.LearnOverloads(f.FullPath, f.Lines);

        var edits = new List<(SourceFile File, int Count, List<int> Lines)>();
        foreach (var f in files)
        {
            var found = plan.Edits(f.FullPath, f.Lang, f.Lines, f.Display, record: true);
            if (found.Count > 0) edits.Add((f, found.Count, found.Select(e => e.Line).Distinct().ToList()));
        }
        var notes = plan.Notes();

        // Файл типа: Foo.cs → Bar.cs (если имя файла совпадает с именем типа).
        var fileRenames = new List<(string From, string To)>();
        if (renameFile)
        {
            foreach (var (f, s) in defs.Where(d => d.Symbol.IsType))
            {
                var fileBase = Path.GetFileNameWithoutExtension(f.FullPath);
                if (fileBase != oldSimple && !fileBase.StartsWith(oldSimple + ".", StringComparison.Ordinal)) continue;
                var target = Path.Combine(Path.GetDirectoryName(f.FullPath)!, newName + Path.GetFileName(f.FullPath)[oldSimple.Length..]);
                if (fileRenames.Any(r => string.Equals(r.From, f.FullPath, StringComparison.OrdinalIgnoreCase))) continue;
                if (File.Exists(target)) throw new ToolException($"Cannot rename {f.Display}: {ctx.Display(target)} already exists.");
                fileRenames.Add((f.FullPath, target));
            }
        }

        var sb = new StringBuilder();
        var total = edits.Sum(e => e.Count);
        var how = langs.Contains(CodeLang.CSharp)
            ? "C#: syntax-aware (Roslyn) — strings and comments are skipped, doc crefs and [Attribute] usages are renamed" + (wantParams is null ? "" : $", only the {wantParams}-parameter overload(s)")
            : "textual rename outside strings/comments";
        if (dryRun)
        {
            sb.Append($"dry run: rename {name} → {newName}: {total} occurrence(s) in {edits.Count} file(s); nothing was written.\n");
            foreach (var e in edits.Take(40)) sb.Append($"  {e.File.Display} ×{e.Count} (lines {string.Join(", ", e.Lines.Take(8))}{(e.Lines.Count > 8 ? ", …" : "")})\n");
            foreach (var (from, to) in fileRenames) sb.Append($"  file: {ctx.Display(from)} → {ctx.Display(to)}\n");
            sb.Append(how).Append("; reflection, string references and other languages are not updated").Append(notes);
            return sb.ToString();
        }

        // Без явной проверки — сборка проекта (если её удаётся определить): неудачное переименование откатится.
        string? verifyNote = null;
        if (verify is null)
        {
            try { verify = await VerifyTool.ResolveCommandAsync(ctx, null, "build").ConfigureAwait(false); }
            catch (ToolException) { verifyNote = "UNVERIFIED: no allowlisted build command was found; pass verify_command"; }
        }

        var job = JobStore.Create(McpToolNames.Refactor, ctx.Roots[0], $"rename {name} → {newName}", ctx.ToolUseId);
        job.Mode = "rename";
        job.VerifyCommand = verify;
        var writes = new List<(string Canonical, byte[] Bytes)>();
        foreach (var e in edits)
        {
            var (_, canonical) = ctx.ResolveWrite(e.File.FullPath);
            var bytes = JobStore.ReadAllBytesShared(canonical);
            var (text, format) = TextCodec.Decode(bytes);
            var src = TextCodec.SplitLines(text);
            // Правки считаются заново по тексту с диска (тем же способом), чтобы позиции точно совпали с переписываемыми строками.
            foreach (var group in plan.Edits(e.File.FullPath, e.File.Lang, src, e.File.Display, record: false).GroupBy(x => x.Line))
            {
                var line = src[group.Key - 1];
                foreach (var x in group.OrderByDescending(x => x.Column))
                    line = string.Concat(line.AsSpan(0, x.Column), x.Replacement, line.AsSpan(x.Column + x.Length));
                src[group.Key - 1] = line;
            }
            writes.Add((canonical, TextCodec.Encode(string.Join("\n", src), format, out _)));
            JobStore.Snapshot(job, canonical, ctx.Display(canonical));
        }
        foreach (var (from, to) in fileRenames)
        {
            ctx.ResolveWrite(to);
            JobStore.Snapshot(job, from, ctx.Display(from));
            JobStore.Snapshot(job, to, ctx.Display(to));
        }
        try
        {
            foreach (var (canonical, bytes) in writes) JobStore.WriteBytesAtomic(canonical, bytes);
            foreach (var (from, to) in fileRenames) File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            JobStore.Revert(job, force: true);
            throw new ToolException($"Writing failed ({ex.Message}); all files were restored.");
        }
        JobStore.Finish(job, JobStatus.Applied);

        sb.Append($"renamed {name} → {newName}: {total} occurrence(s) in {edits.Count} file(s)");
        foreach (var (from, to) in fileRenames) sb.Append($"; file {ctx.Display(from)} → {ctx.Display(to)}");
        sb.Append('\n');
        if (verify is not null)
        {
            var result = await VerifyCommand.RunAsync(verify, ctx.Roots[0], TimeSpan.FromMinutes(15), ctx.Progress, ctx.Ct).ConfigureAwait(false);
            if (!result.Passed)
            {
                var undo = JobStore.Revert(job, force: true);
                return $"job_id: {job.Id} · status: ROLLED BACK (verify failed; files restored)\n{result.Summary(1)}\n{(undo.Ok ? "" : "warning: " + undo.Message)}".TrimEnd();
            }
            sb.Append(result.Summary(1)).Append('\n');
        }
        sb.Append($"job_id: {job.Id} · undo: local_job action=revert job_id={job.Id}\n");
        if (verifyNote is not null) sb.Append(verifyNote).Append('\n');
        sb.Append(how).Append("\nnote: string references, reflection and other languages (XAML, SQL, config) are not updated; check with local_search_code").Append(notes);
        return sb.ToString();
    }

    private static bool IsKnownMethod((SourceFile File, CodeSymbol Symbol) d) => d.Symbol.Kind == "method" && d.Symbol.Params >= 0;

    /// <summary>
    /// «Svc.Run(int, string)» → (Svc.Run, 2); «Svc.Run(2)» → (Svc.Run, 2); «Run()» → (Run, 0, пустые скобки); без скобок — (имя, null).
    /// Запятые внутри &lt;…&gt;, (…) и […] не разделяют параметры.
    /// </summary>
    internal static (string Name, int? Params, bool EmptyParens) ParseOverloadSpec(string name)
    {
        var t = name.Trim();
        var open = t.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0 || !t.EndsWith(')')) return (t, null, false);
        var baseName = t[..open].Trim();
        var inner = t[(open + 1)..^1].Trim();
        if (inner.Length == 0) return (baseName, 0, true);
        if (int.TryParse(inner, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) return (baseName, n, false);
        var depth = 0;
        var count = 1;
        foreach (var c in inner)
        {
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) count++;
        }
        return (baseName, count, false);
    }

    /// <summary>Одна замена: строка (с 1), столбец (с 0), длина старого текста и новый текст.</summary>
    private readonly record struct RenameEdit(int Line, int Column, int Length, string Replacement);

    /// <summary>
    /// Что и где заменять. C# — по синтаксическому дереву Roslyn (<see cref="CSharpReferences"/>): строки и комментарии не трогаются,
    /// квалификатор «X.Name» с using-алиасами раскрывается, при выбранной перегрузке вызовы сопоставляются по числу аргументов.
    /// Остальные языки — слово целиком вне строк/комментариев (как раньше).
    /// </summary>
    private sealed class RenamePlan(string oldName, string newName, bool isMember, string? owner, HashSet<string> projectTypes, int? wantParams,
        HashSet<string> owners)
    {
        private readonly List<(int Min, int Max, bool Ext)> _target = [];
        private readonly List<(int Min, int Max, bool Ext)> _other = [];
        private readonly List<string> _foreign = [];
        private readonly List<string> _ambiguous = [];
        private readonly System.Text.RegularExpressions.Regex _word = CodeIndex.WordRegex(oldName);

        /// <summary>Атрибут: переименование FooAttribute → BarAttribute меняет и «[Foo]» на «[Bar]».</summary>
        private readonly (string Old, string New)? _attribute =
            !isMember && oldName.Length > 9 && newName.Length > 9 && oldName.EndsWith("Attribute", StringComparison.Ordinal) && newName.EndsWith("Attribute", StringComparison.Ordinal)
                ? (oldName[..^9], newName[..^9]) : null;

        /// <summary>Диапазоны числа аргументов у выбранных и остальных перегрузок (по объявлениям в типах-владельцах).</summary>
        public void LearnOverloads(string path, string[] lines)
        {
            if (!ContainsWord(lines, oldName)) return;
            foreach (var o in CSharpReferences.Find(path, lines, oldName))
            {
                if (o.Declaration != "method" || o.DeclaringType is null || !owners.Contains(o.DeclaringType)) continue;
                (o.Params == wantParams ? _target : _other).Add((o.MinArgs, o.MaxArgs, o.IsExtension));
            }
        }

        public List<RenameEdit> Edits(string path, CodeLang lang, string[] lines, string display, bool record)
        {
            var edits = new List<RenameEdit>();
            if (lang != CodeLang.CSharp)
            {
                if (!ContainsWord(lines, oldName)) return edits;
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!line.Contains(oldName, StringComparison.Ordinal)) continue;
                    foreach (System.Text.RegularExpressions.Match m in _word.Matches(line))
                        if (TextReplaceable(line, m.Index, display, i + 1, record)) edits.Add(new RenameEdit(i + 1, m.Index, oldName.Length, newName));
                }
                return edits;
            }
            if (ContainsWord(lines, oldName))
                foreach (var o in CSharpReferences.Find(path, lines, oldName))
                    if (Decide(o, display, record)) edits.Add(new RenameEdit(o.Line, o.Column, o.Length, newName));
            if (_attribute is { } a && ContainsWord(lines, a.Old))
                foreach (var o in CSharpReferences.Find(path, lines, a.Old).Where(o => o.InAttribute))
                    edits.Add(new RenameEdit(o.Line, o.Column, o.Length, a.New));
            return edits;
        }

        private static bool ContainsWord(string[] lines, string word) => lines.Any(l => l.Contains(word, StringComparison.Ordinal));

        private bool IsForeign(string? receiver) =>
            isMember && receiver is { Length: > 0 } r && char.IsUpper(r[0]) && r != owner && !projectTypes.Contains(r);

        private bool TextReplaceable(string line, int index, string display, int lineNo, bool record)
        {
            if (CodeIndex.InStringOrComment(line, index)) return false;
            if (!isMember) return true;
            var (receiver, _) = SymbolsTool.ReceiverOf(line, index);
            if (!IsForeign(receiver)) return true;
            if (record && _foreign.Count < 20) _foreign.Add($"{display}:{lineNo} {receiver}.{oldName}");
            return false;
        }

        private bool Decide(CSharpOccurrence o, string display, bool record)
        {
            if (IsForeign(o.Receiver))
            {
                if (record && _foreign.Count < 20) _foreign.Add($"{display}:{o.Line} {o.Receiver}.{oldName}");
                return false;
            }
            if (wantParams is null) return true;
            if (o.Declaration == "method" && o.DeclaringType is not null && owners.Contains(o.DeclaringType)) return o.Params == wantParams;
            if (o.Declaration is not null) return true;
            if (o.ArgCount >= 0)
            {
                var viaReceiver = o.Receiver is not null;
                var target = _target.Any(r => Accepts(r, o.ArgCount, viaReceiver));
                var other = _other.Any(r => Accepts(r, o.ArgCount, viaReceiver));
                if (target && !other) return true;
                if (other && !target) return false;
            }
            // Группа методов (делегат, nameof) или вызов, подходящий к нескольким перегрузкам, — по синтаксису не определить.
            if (record && _ambiguous.Count < 20) _ambiguous.Add($"{display}:{o.Line}");
            return false;
        }

        private static bool Accepts((int Min, int Max, bool Ext) r, int args, bool viaReceiver) =>
            args >= r.Min && args <= r.Max || r.Ext && viaReceiver && args + 1 >= r.Min && args + 1 <= r.Max;

        public string Notes()
        {
            var sb = new StringBuilder();
            if (_foreign.Count > 0)
                sb.Append($"\nnot renamed (qualified by types outside the project): {string.Join(", ", _foreign.Take(8))}{(_foreign.Count > 8 ? ", …" : "")}");
            if (_ambiguous.Count > 0)
                sb.Append($"\nnot renamed (overload not determinable from syntax — check by hand): {string.Join(", ", _ambiguous.Take(8))}{(_ambiguous.Count > 8 ? ", …" : "")}");
            return sb.ToString();
        }
    }

    private static async Task<string> AgentRefactorAsync(ToolContext ctx, string act, string symbol, string? newName, string? targetFile, string? lines,
        string? instructions, string? verifyCommand, string[]? paths)
    {
        var index = await CodeIndex.LoadAsync(ctx, paths, codeOnly: true, ctx.Ct).ConfigureAwait(false);
        var defs = SymbolsTool.Definitions(index.Files, symbol);
        if (defs.Count == 0) throw new ToolException($"No definition of '{symbol}' found; check the name with local_symbols action=find.");
        var (file, s) = defs[0];
        var where = $"{file.Display} lines {s.Line}-{s.EndLine} ({s.Kind} {s.QualifiedName})";
        var spec = act switch
        {
            "extract_method" => $"Refactoring: extract method. In {where}, move the code {(string.IsNullOrWhiteSpace(lines) ? "described below" : $"at lines {lines}")} into a new method " +
                                $"named {RequireName(newName)} in the same type, pass what it needs as parameters, return what the caller needs, and replace the original code with a call.",
            "extract_class" => $"Refactoring: extract class. From {where}, move the members described below into a new type {RequireName(newName)}" +
                               $"{(string.IsNullOrWhiteSpace(targetFile) ? " in a new file next to the original" : $" in {targetFile}")}; make the original type use/delegate to it.",
            "move_symbol" => $"Refactoring: move {s.QualifiedName} ({where}) to {(string.IsNullOrWhiteSpace(targetFile) ? throw new ToolException("move_symbol needs target_file.") : targetFile)}; " +
                             "update namespaces/imports/usings and every reference.",
            "inline" => $"Refactoring: inline {s.QualifiedName} ({where}) into all of its call sites and remove it.",
            _ => $"Refactoring: change the signature of {s.QualifiedName} ({where}) as described below and update every call site.",
        };
        spec += "\nBehavior must stay exactly the same. Keep the code style of the file. Do not change unrelated code." +
                (string.IsNullOrWhiteSpace(instructions) ? "" : "\nDetails: " + instructions.Trim());
        var verify = verifyCommand;
        if (string.IsNullOrWhiteSpace(verify))
        {
            try { verify = await VerifyTool.ResolveCommandAsync(ctx, null, "build").ConfigureAwait(false); }
            catch (ToolException) { verify = null; }
        }
        var context = new List<string> { file.Display };
        if (!string.IsNullOrWhiteSpace(targetFile)) context.Add(targetFile!);
        return await AgentTaskTool.RunAsync(ctx, spec, verify, [.. context], "apply", 2, 20, background: false).ConfigureAwait(false);
    }

    private static string RequireName(string? n) =>
        !string.IsNullOrWhiteSpace(n) && Symbols.Identifier().IsMatch(n.Trim()) ? n.Trim() : throw new ToolException("new_name (a valid identifier) is required for this action.");
}
