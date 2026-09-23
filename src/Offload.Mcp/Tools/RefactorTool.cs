using System.Text;
using Offload.Core;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_refactor: rename — детерминированно (без модели): все вхождения символа как слова вне строк/комментариев в файлах того же
/// языка, переименование файла типа, проверка конфликтов, снимок, проверка командой и автооткат. extract_method / extract_class /
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
        var (oldSimple, container) = SymbolsTool.SplitName(name);
        if (!Symbols.Identifier().IsMatch(newName)) throw new ToolException($"new_name '{newName}' is not a valid identifier.");
        if (newName == oldSimple) throw new ToolException("new_name equals the current name.");
        var verify = string.IsNullOrWhiteSpace(verifyCommand) ? null : VerifyCommand.Validate(verifyCommand, ctx.Cfg.Mcp.VerifyCommandAllowlist);

        ctx.Progress.Report("Indexing source files…");
        var index = await CodeIndex.LoadAsync(ctx, paths, codeOnly: true, ctx.Ct).ConfigureAwait(false);
        var defs = SymbolsTool.Definitions(index.Files, name);
        if (defs.Count == 0) throw new ToolException($"No definition of '{name}' found in scope; check the name (action=find in local_symbols) or widen paths.");
        var langs = defs.Select(d => d.File.Lang).ToHashSet();

        // Одноимённые объявления в других контейнерах переименовались бы тоже — только с force или более узкими paths.
        var sameNamed = SymbolsTool.Definitions(index.Files.Where(f => langs.Contains(f.Lang)), oldSimple)
            .Where(d => !defs.Any(x => x.File == d.File && x.Symbol.Line == d.Symbol.Line)).ToList();
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

        var word = CodeIndex.WordRegex(oldSimple);
        // Для членов типа «Other.Name» с квалификатором-типом не из проекта (Task.Run, File.Delete) — чужой API, не трогаем.
        var isMember = !defs.Any(d => d.Symbol.IsType);
        var owner = defs[0].Symbol.Container?.Split('.')[^1];
        var projectTypes = index.Files.SelectMany(f => f.Symbols).Where(s => s.IsType).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var skippedForeign = new List<string>();
        bool Replaceable(SourceFile f, string line, int index, int lineNo, bool record = true)
        {
            if (CodeIndex.InStringOrComment(line, index)) return false;
            if (!isMember) return true;
            var (receiver, _) = SymbolsTool.ReceiverOf(line, index);
            if (receiver is { Length: > 0 } r && char.IsUpper(r[0]) && r != owner && !projectTypes.Contains(r))
            {
                if (record && skippedForeign.Count < 20) skippedForeign.Add($"{f.Display}:{lineNo} {r}.{oldSimple}");
                return false;
            }
            return true;
        }
        var edits = new List<(SourceFile File, int Count, List<int> Lines)>();
        foreach (var f in index.Files.Where(f => langs.Contains(f.Lang)))
        {
            var count = 0;
            var at = new List<int>();
            for (var i = 0; i < f.Lines.Length; i++)
            {
                var line = f.Lines[i];
                if (!line.Contains(oldSimple, StringComparison.Ordinal)) continue;
                var n = word.Matches(line).Count(m => Replaceable(f, line, m.Index, i + 1));
                if (n == 0) continue;
                count += n;
                at.Add(i + 1);
            }
            if (count > 0) edits.Add((f, count, at));
        }
        var foreignNote = skippedForeign.Count == 0 ? "" : $"\nnot renamed (qualified by types outside the project): {string.Join(", ", skippedForeign.Take(8))}{(skippedForeign.Count > 8 ? ", …" : "")}";

        // Файл типа: Foo.cs → Bar.cs (если имя файла совпадает с именем типа).
        var fileRenames = new List<(string From, string To)>();
        if (renameFile)
        {
            foreach (var (f, s) in defs.Where(d => d.Symbol.IsType))
            {
                var baseName = Path.GetFileNameWithoutExtension(f.FullPath);
                if (baseName != oldSimple && !baseName.StartsWith(oldSimple + ".", StringComparison.Ordinal)) continue;
                var target = Path.Combine(Path.GetDirectoryName(f.FullPath)!, newName + Path.GetFileName(f.FullPath)[oldSimple.Length..]);
                if (File.Exists(target)) throw new ToolException($"Cannot rename {f.Display}: {ctx.Display(target)} already exists.");
                fileRenames.Add((f.FullPath, target));
            }
        }

        var sb = new StringBuilder();
        var total = edits.Sum(e => e.Count);
        if (dryRun)
        {
            sb.Append($"dry run: rename {name} → {newName}: {total} occurrence(s) in {edits.Count} file(s); nothing was written.\n");
            foreach (var e in edits.Take(40)) sb.Append($"  {e.File.Display} ×{e.Count} (lines {string.Join(", ", e.Lines.Take(8))}{(e.Lines.Count > 8 ? ", …" : "")})\n");
            foreach (var (from, to) in fileRenames) sb.Append($"  file: {ctx.Display(from)} → {ctx.Display(to)}\n");
            sb.Append("textual rename outside strings/comments; reflection, string references and other languages are not updated").Append(foreignNote);
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
            for (var i = 0; i < src.Length; i++)
            {
                var line = src[i];
                if (!line.Contains(oldSimple, StringComparison.Ordinal)) continue;
                var lineNo = i + 1;
                src[i] = word.Replace(line, m => Replaceable(e.File, line, m.Index, lineNo, record: false) ? newName : m.Value);
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
        sb.Append("note: string references, reflection and other languages (XAML, SQL, config) are not updated; check with local_search_code").Append(foreignNote);
        return sb.ToString();
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
