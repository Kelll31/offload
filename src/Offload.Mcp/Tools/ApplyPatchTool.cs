using System.Text;
using Offload.Core;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_apply_patch: применить unified diff атомарно — сначала вычисляется результат для всех файлов (любой несовпавший хунк —
/// отказ без изменений), затем снимок в JobStore, запись, проверка командой и автоматический откат при провале.
/// </summary>
internal static class ApplyPatchTool
{
    public const int MaxPatchChars = 400_000;
    public const int MaxFiles = 60;

    public static async Task<string> RunAsync(ToolContext ctx, string? patch, string? verifyCommand, bool dryRun, bool rollbackOnFailure, int timeoutSec)
    {
        var text = ToolHelpers.RequireText(patch, "patch", MaxPatchChars);
        var verify = string.IsNullOrWhiteSpace(verifyCommand) ? null : VerifyCommand.Validate(verifyCommand, ctx.Cfg.Mcp.VerifyCommandAllowlist);
        var files = UnifiedPatch.Parse(text);
        if (files.Count == 0) throw new ToolException("No file changes found: pass a unified diff with ---/+++ headers and @@ hunks (git diff format).");
        if (files.Count > MaxFiles) throw new ToolException($"The patch touches {files.Count} files (max {MaxFiles}); split it.");
        UnifiedPatch.StripPrefixes(files);
        files = MergeSections(files);

        // 1. Вычислить всё в памяти.
        var plan = new List<(FilePatch Patch, string Full, string Canonical, string Display, byte[]? NewBytes, string? MoveFrom, int Added, int Removed)>();
        foreach (var f in files)
        {
            var (full, canonical) = ctx.ResolveWrite(f.Target);
            var display = ctx.Display(canonical);
            byte[]? current = File.Exists(canonical) ? JobStore.ReadAllBytesShared(canonical) : null;
            string? moveFrom = null;
            if (!f.IsNew && !f.IsDelete && f.OldPath != f.NewPath)
            {
                var (_, oldCanonical) = ctx.ResolveWrite(f.OldPath!);
                if (!File.Exists(oldCanonical)) throw new ToolException($"{f.OldPath}: file to rename does not exist.");
                if (current is not null) throw new ToolException($"{f.NewPath}: rename target already exists.");
                current = JobStore.ReadAllBytesShared(oldCanonical);
                moveFrom = oldCanonical;
            }
            if (f.IsNew && current is not null && current.Length > 0) throw new ToolException($"{display}: the patch creates it, but the file already exists.");
            if (!f.IsNew && current is null) throw new ToolException($"{display}: file not found (the patch modifies/deletes it).");
            if (current is not null && TextCodec.LooksBinary(current)) throw new ToolException($"{display}: binary file; cannot patch.");
            if (current is not null && current.Length > JobStore.MaxSnapshotBytes) throw new ToolException($"{display}: too large to patch safely.");

            if (f.IsDelete)
            {
                plan.Add((f, full, canonical, display, null, null, 0, TextCodec.SplitLines(TextCodec.Decode(current!).Text).Length));
                continue;
            }
            var (oldText, format) = current is null ? ("", TextFormat.DefaultUtf8Lf with { NewLine = GuessNewLine(canonical) }) : TextCodec.Decode(current);
            var oldLines = TextCodec.SplitLines(oldText);
            var (newLines, fin, error) = UnifiedPatch.Apply(oldLines, current is null || format.FinalNewline, f);
            if (error is not null) throw new ToolException($"{display}: {error}. Nothing was changed. Re-read the current lines and regenerate the hunk.");
            var bytes = TextCodec.Encode(string.Join("\n", newLines!), format with { FinalNewline = fin }, out _);
            var (added, removed) = LineDiff.Stats(oldLines, newLines!.ToArray());
            plan.Add((f, full, canonical, display, bytes, moveFrom, added, removed));
        }

        var sb = new StringBuilder();
        if (dryRun)
        {
            sb.Append($"dry run: the patch applies cleanly to {plan.Count} file(s); nothing was written.\n");
            foreach (var p in plan) sb.Append($"  {p.Display}  {Describe(p.Patch, p.MoveFrom, ctx)} +{p.Added} −{p.Removed}\n");
            return sb.ToString().TrimEnd();
        }

        // 2. Снимок и запись.
        var job = JobStore.Create(McpToolNames.ApplyPatch, ctx.Roots[0], $"apply patch to {plan.Count} file(s): " + string.Join(", ", plan.Take(6).Select(p => p.Display)), ctx.ToolUseId);
        job.Mode = "patch";
        job.VerifyCommand = verify;
        foreach (var p in plan)
        {
            JobStore.Snapshot(job, p.Canonical, p.Display);
            if (p.MoveFrom is not null) JobStore.Snapshot(job, p.MoveFrom, ctx.Display(p.MoveFrom));
        }
        try
        {
            foreach (var p in plan)
            {
                if (p.NewBytes is null)
                {
                    File.Delete(p.Canonical);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(p.Canonical)!);
                JobStore.WriteBytesAtomic(p.Canonical, p.NewBytes);
                if (p.MoveFrom is not null) File.Delete(p.MoveFrom);
                ctx.Stats.TokensWritten += p.Added * 8;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var undo = JobStore.Revert(job, force: true);
            JobStore.Finish(job, JobStatus.Failed);
            throw new ToolException($"Writing failed ({ex.Message}); all files were restored. {undo.Message}");
        }
        JobStore.Finish(job, JobStatus.Applied);

        // 3. Проверка и откат.
        VerifyResult? result = null;
        var status = JobStatus.Applied;
        if (verify is not null)
        {
            result = await VerifyCommand.RunAsync(verify, ctx.Roots[0], TimeSpan.FromSeconds(Math.Clamp(timeoutSec <= 0 ? 900 : timeoutSec, 10, 3600)),
                ctx.Progress, ctx.Ct).ConfigureAwait(false);
            job.VerifySummary = result.Summary(1, 20, 1500);
            if (!result.Passed && rollbackOnFailure)
            {
                var undo = JobStore.Revert(job, force: true);
                status = JobStatus.Reverted;
                job = JobStore.Load(job.Id);
                job.Notes.Add("rolled back automatically: verify failed");
                job.VerifySummary = result.Summary(1, 20, 1500);
                JobStore.Save(job);
                sb.Append($"job_id: {job.Id} · status: ROLLED BACK (verify failed; your files are unchanged)\n");
                sb.Append(result.Summary(1)).Append('\n');
                if (!undo.Ok) sb.Append("warning: ").Append(undo.Message).Append('\n');
                return sb.ToString().TrimEnd();
            }
            JobStore.Save(job);
        }

        sb.Append($"job_id: {job.Id} · status: {(result is { Passed: false } ? "applied, VERIFY FAILED (kept: rollback_on_failure=false)" : status)}\n");
        foreach (var p in plan) sb.Append($"  {p.Display}  {Describe(p.Patch, p.MoveFrom, ctx)}+{p.Added} −{p.Removed}\n");
        if (result is not null) sb.Append(result.Summary(1)).Append('\n');
        sb.Append($"undo: local_job action=revert job_id={job.Id}");
        return sb.ToString();
    }

    /// <summary>
    /// Несколько секций ---/+++ для одного файла (модели так пишут): хунки объединяются в одну правку по порядку строк,
    /// иначе вторая секция молча затёрла бы первую. Для создания/удаления/переименования дубликаты — ошибка.
    /// </summary>
    internal static List<FilePatch> MergeSections(List<FilePatch> files)
    {
        var result = new List<FilePatch>();
        foreach (var g in files.GroupBy(f => f.Target.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
        {
            var list = g.ToList();
            if (list.Count == 1)
            {
                result.Add(list[0]);
                continue;
            }
            if (list.Any(f => f.IsNew || f.IsDelete || f.OldPath != f.NewPath))
                throw new ToolException($"{g.Key}: the patch has several sections for this file that create, delete or rename it; merge them into one.");
            var merged = new FilePatch { OldPath = list[0].OldPath, NewPath = list[0].NewPath };
            merged.Hunks.AddRange(list.SelectMany(f => f.Hunks).OrderBy(h => h.OldStart));
            result.Add(merged);
        }
        return result;
    }

    private static string Describe(FilePatch p, string? moveFrom, ToolContext ctx) =>
        p.IsNew ? "(new) " : p.IsDelete ? "(deleted) " : moveFrom is not null ? $"(renamed from {ctx.Display(moveFrom)}) " : "";

    /// <summary>Перевод строки для нового файла — как у соседних файлов в папке (CRLF-проекты на Windows).</summary>
    private static string GuessNewLine(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            var ext = Path.GetExtension(path);
            var sibling = dir is null || !Directory.Exists(dir) ? null
                : Directory.EnumerateFiles(dir, "*" + ext).FirstOrDefault(f => new FileInfo(f).Length is > 0 and < 1_000_000);
            if (sibling is null) return "\n";
            var bytes = File.ReadAllBytes(sibling);
            return TextCodec.DetectNewLine(TextCodec.Decode(bytes).Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "\n";
        }
    }
}
