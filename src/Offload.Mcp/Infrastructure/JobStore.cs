using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Mcp.Infrastructure;

internal static class JobStatus
{
    public const string Running = "running";
    public const string Applied = "applied";
    public const string DryRun = "dry_run";
    public const string Failed = "failed";
    public const string Reverted = "reverted";
    public const string NoChanges = "no_changes";
    public const string Cancelled = "cancelled";

    /// <summary>Работа агента зафиксирована в песочнице и ждёт слияния (local_job action=merge / discard).</summary>
    public const string PendingMerge = "pending_merge";

    /// <summary>Патч песочницы не применяется к текущему рабочему дереву.</summary>
    public const string Conflict = "conflict";

    /// <summary>Работа агента влита в текущую ветку проекта коммитом (fast-forward).</summary>
    public const string Committed = "committed";

    /// <summary>Песочница удалена без слияния.</summary>
    public const string Discarded = "discarded";
}

/// <summary>Файл, затронутый задачей: исходные байты (снимок) и хэш на момент завершения.</summary>
internal sealed class JobFile
{
    public string Path { get; set; } = "";
    public string Display { get; set; } = "";
    public bool ExistedBefore { get; set; }
    /// <summary>Имя файла снимка в папке задачи (null — файла до задачи не было).</summary>
    public string? Snapshot { get; set; }
    public string? OriginalSha256 { get; set; }
    public bool FinalExists { get; set; }
    public string? FinalSha256 { get; set; }
    /// <summary>Предлагаемое содержимое (dry_run) — имя файла в папке задачи.</summary>
    public string? Proposed { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    /// <summary>false — «охранный» снимок файла вне списка разрешённых (для отката посторонних правок агента).</summary>
    public bool Allowlisted { get; set; } = true;
    public string? Note { get; set; }
}

internal sealed class JobInfo
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Status { get; set; } = JobStatus.Running;
    public string Root { get; set; } = "";
    public string Task { get; set; } = "";
    public string? Mode { get; set; }
    public string? VerifyCommand { get; set; }
    public string? VerifySummary { get; set; }
    public string? Summary { get; set; }
    public string? ToolUseId { get; set; }
    public List<string> Notes { get; set; } = [];
    public List<JobFile> Files { get; set; } = [];
    public DateTime? RevertedUtc { get; set; }

    /// <summary>Git-песочница агентной задачи (local_agent_task) или null.</summary>
    public SandboxInfo? Sandbox { get; set; }
}

internal sealed record RevertOutcome(bool Ok, string Message);

/// <summary>
/// Задачи записи/правки в %LOCALAPPDATA%\Offload\jobs\&lt;id&gt;\: job.json + исходные байты файлов.
/// Позволяют показать diff и откатить изменения; старые задачи удаляются через JobRetentionDays.
/// </summary>
internal static partial class JobStore
{
    public const int MaxSnapshotBytes = 32 * 1024 * 1024;

    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{4}$")]
    private static partial Regex IdPattern();

    public static string JobsDir => System.IO.Path.Combine(AppPaths.DataDir, "jobs");

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static string DirOf(string id)
    {
        if (!IsValidId(id)) throw new ToolException($"Invalid job_id '{id}'. It looks like 20260922-183005-ab12 (from a local_write_file/local_edit_files result).");
        return System.IO.Path.Combine(JobsDir, id);
    }

    public static JobInfo Create(string tool, string root, string task, string? toolUseId)
    {
        for (var attempt = 0; ; attempt++)
        {
            var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";
            var dir = DirOf(id);
            if (Directory.Exists(dir) && attempt < 10) continue;
            Directory.CreateDirectory(dir);
            var job = new JobInfo
            {
                Id = id,
                Tool = tool,
                CreatedUtc = DateTime.UtcNow,
                Root = root,
                Task = task.Length > 600 ? task[..600] + "…" : task,
                ToolUseId = toolUseId,
            };
            Save(job);
            return job;
        }
    }

    /// <summary>Снимок файла до изменений (исходные байты + хэш). Повторный вызов для того же пути — без изменений.</summary>
    public static JobFile Snapshot(JobInfo job, string canonicalPath, string display, bool allowlisted = true)
    {
        var existing = job.Files.FirstOrDefault(f => f.Path.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var jf = new JobFile { Path = canonicalPath, Display = display, Allowlisted = allowlisted };
        if (File.Exists(canonicalPath))
        {
            var info = new FileInfo(canonicalPath);
            if (info.Length > MaxSnapshotBytes)
                throw new ToolException($"'{display}' is too large to snapshot ({info.Length / 1024 / 1024} MB); refusing to modify it.");
            var bytes = ReadAllBytesShared(canonicalPath);
            var name = $"f{job.Files.Count}.orig";
            File.WriteAllBytes(System.IO.Path.Combine(DirOf(job.Id), name), bytes);
            jf.ExistedBefore = true;
            jf.Snapshot = name;
            jf.OriginalSha256 = Sha(bytes);
        }
        job.Files.Add(jf);
        Save(job);
        return jf;
    }

    public static void SaveProposed(JobInfo job, JobFile jf, byte[] content)
    {
        var name = $"f{job.Files.IndexOf(jf)}.new";
        File.WriteAllBytes(System.IO.Path.Combine(DirOf(job.Id), name), content);
        jf.Proposed = name;
    }

    public static byte[]? ReadSnapshot(JobInfo job, JobFile jf) =>
        jf.Snapshot is null ? null : File.ReadAllBytes(System.IO.Path.Combine(DirOf(job.Id), jf.Snapshot));

    public static byte[]? ReadProposed(JobInfo job, JobFile jf) =>
        jf.Proposed is null ? null : File.ReadAllBytes(System.IO.Path.Combine(DirOf(job.Id), jf.Proposed));

    public static void Save(JobInfo job)
    {
        var json = JsonSerializer.Serialize(job, Json.Options);
        FileUtil.WriteAllTextAtomic(System.IO.Path.Combine(DirOf(job.Id), "job.json"), json);
    }

    public static JobInfo Load(string id)
    {
        var dir = DirOf(id);
        var file = System.IO.Path.Combine(dir, "job.json");
        if (!File.Exists(file))
            throw new ToolException($"Job '{id}' not found (jobs are kept for a limited number of days).");
        try
        {
            return JsonSerializer.Deserialize<JobInfo>(File.ReadAllText(file), Json.Options)
                ?? throw new ToolException($"Job '{id}' is corrupted.");
        }
        catch (JsonException)
        {
            throw new ToolException($"Job '{id}' is corrupted.");
        }
    }

    /// <summary>Записать итоговые хэши и статистику +/− (по сравнению со снимком). Задача помечается завершённой.</summary>
    public static void Finish(JobInfo job, string status)
    {
        foreach (var jf in job.Files)
        {
            if (File.Exists(jf.Path))
            {
                var bytes = ReadAllBytesShared(jf.Path);
                jf.FinalExists = true;
                jf.FinalSha256 = Sha(bytes);
                if (jf.Allowlisted) (jf.Added, jf.Removed) = CountChanges(ReadSnapshot(job, jf), bytes);
            }
            else
            {
                jf.FinalExists = false;
                jf.FinalSha256 = null;
                if (jf.Allowlisted) (jf.Added, jf.Removed) = CountChanges(ReadSnapshot(job, jf), null);
            }
        }
        job.Status = status;
        job.FinishedUtc = DateTime.UtcNow;
        Save(job);
    }

    public static (int Added, int Removed) CountChanges(byte[]? before, byte[]? after)
    {
        var a = before is null ? [] : TextCodec.SplitLines(TextCodec.Decode(before).Text);
        var b = after is null ? [] : TextCodec.SplitLines(TextCodec.Decode(after).Text);
        return LineDiff.Stats(a, b);
    }

    /// <summary>Файлы задачи, текущее содержимое которых отличается от снимка (до задачи).</summary>
    public static List<JobFile> ChangedFromSnapshot(JobInfo job)
    {
        var list = new List<JobFile>();
        foreach (var jf in job.Files)
        {
            var exists = File.Exists(jf.Path);
            if (exists != jf.ExistedBefore) list.Add(jf);
            else if (exists && Sha(ReadAllBytesShared(jf.Path)) != jf.OriginalSha256) list.Add(jf);
        }
        return list;
    }

    /// <summary>Файлы задачи, изменившиеся на диске после её завершения.</summary>
    public static List<JobFile> ChangedSinceFinish(JobInfo job)
    {
        var list = new List<JobFile>();
        foreach (var jf in job.Files)
        {
            var exists = File.Exists(jf.Path);
            if (exists != jf.FinalExists) list.Add(jf);
            else if (exists && Sha(ReadAllBytesShared(jf.Path)) != jf.FinalSha256) list.Add(jf);
        }
        return list;
    }

    /// <summary>
    /// Откат: восстановить снимки, удалить созданные задачей файлы. Если файл менялся после задачи — отказ (кроме force).
    /// </summary>
    public static RevertOutcome Revert(JobInfo job, bool force)
    {
        if (job.Status == JobStatus.Reverted) return new(true, $"Job {job.Id} was already reverted at {job.RevertedUtc:u}; nothing to do.");
        if (job.Status == JobStatus.DryRun) return new(true, $"Job {job.Id} was a dry run; nothing was applied, nothing to revert.");
        if (job.Status is JobStatus.PendingMerge or JobStatus.Conflict or JobStatus.Discarded)
            return new(true, $"Job {job.Id} was not merged into the project (status {job.Status}); nothing to revert. Use local_job action=discard to drop its sandbox.");
        if (job.Status == JobStatus.Running && !force)
            return new(false, $"Job {job.Id} is still running (or was interrupted). Retry later, or use force=true to restore the snapshot now.");

        var changed = job.Status == JobStatus.Running ? [] : ChangedSinceFinish(job);
        if (changed.Count > 0 && !force)
        {
            return new(false,
                $"Refusing to revert job {job.Id}: {changed.Count} file(s) changed after the job finished: " +
                string.Join(", ", changed.Take(10).Select(f => f.Display)) +
                ". Reverting would discard those later edits. Call again with force=true to revert anyway.");
        }

        var restored = new List<string>();
        var deleted = new List<string>();
        var errors = new List<string>();
        foreach (var jf in job.Files)
        {
            try
            {
                // Путь из job.json перепроверяем: не .git, не секрет, не ссылка, внутри корня задачи.
                if (PathGuard.CheckRead(jf.Path, null) is { } why) throw new IOException($"blocked ({why})");
                if (PathGuard.IsReparsePoint(jf.Path)) throw new IOException("target is a link");
                if (!string.IsNullOrEmpty(job.Root) && !PathGuard.IsInside(jf.Path, job.Root)) throw new IOException("outside the job's workspace");
                if (jf.ExistedBefore)
                {
                    var bytes = ReadSnapshot(job, jf) ?? throw new IOException("snapshot missing");
                    if (File.Exists(jf.Path) && Sha(ReadAllBytesShared(jf.Path)) == jf.OriginalSha256) continue;
                    WriteBytesAtomic(jf.Path, bytes);
                    restored.Add(jf.Display);
                }
                else if (File.Exists(jf.Path))
                {
                    File.Delete(jf.Path);
                    deleted.Add(jf.Display);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{jf.Display}: {ex.Message}");
            }
        }
        if (errors.Count == 0)
        {
            job.Status = JobStatus.Reverted;
            job.RevertedUtc = DateTime.UtcNow;
        }
        else
        {
            job.Notes.Add("revert errors: " + string.Join("; ", errors));
        }
        Save(job);
        var sb = new StringBuilder(errors.Count == 0 ? $"Reverted job {job.Id}." : $"Job {job.Id} reverted partially.");
        if (restored.Count > 0) sb.Append($" restored: {string.Join(", ", restored.Take(20))}.");
        if (deleted.Count > 0) sb.Append($" deleted (created by the job): {string.Join(", ", deleted.Take(20))}.");
        if (restored.Count == 0 && deleted.Count == 0 && errors.Count == 0) sb.Append(" Files already matched the snapshot.");
        if (errors.Count > 0) sb.Append(" errors: " + string.Join("; ", errors));
        return new(errors.Count == 0, sb.ToString());
    }

    /// <summary>Unified diff снимок → текущее (или предлагаемое, для dry_run) содержимое, с ограничением по строкам/символам.</summary>
    public static string Diff(JobInfo job, int maxLines, int maxChars, IReadOnlyCollection<string>? pathFilter)
    {
        var sb = new StringBuilder();
        var lines = 0;
        var omittedFiles = new List<string>();
        foreach (var jf in job.Files.Where(f => f.Allowlisted))
        {
            if (pathFilter is { Count: > 0 } && !pathFilter.Any(p => MatchesFilter(jf, p))) continue;
            var before = ReadSnapshot(job, jf);
            byte[]? after;
            if (job.Status == JobStatus.DryRun) after = ReadProposed(job, jf) ?? before;
            else after = File.Exists(jf.Path) ? ReadAllBytesShared(jf.Path) : null;
            var a = before is null ? [] : TextCodec.SplitLines(TextCodec.Decode(before).Text);
            var b = after is null ? [] : TextCodec.SplitLines(TextCodec.Decode(after).Text);
            var oldName = before is null ? "/dev/null" : "a/" + jf.Display;
            var newName = after is null ? "/dev/null" : "b/" + jf.Display;
            var diff = LineDiff.Unified(a, b, oldName, newName);
            if (diff.Length == 0) continue;
            var diffLines = diff.Split('\n');
            foreach (var line in diffLines)
            {
                if (line.Length == 0) continue;
                if (lines >= maxLines || sb.Length + line.Length + 1 > maxChars)
                {
                    omittedFiles.Add(jf.Display);
                    break;
                }
                sb.Append(line.Length > 1000 ? line[..1000] + "…" : line).Append('\n');
                lines++;
            }
        }
        if (sb.Length == 0 && omittedFiles.Count == 0) return "(no differences)";
        if (omittedFiles.Count > 0)
            sb.Append($"…[diff truncated at {maxLines} lines / {maxChars} chars; more in: {string.Join(", ", omittedFiles.Distinct().Take(10))}. Use paths=[…] or a larger max_lines.]\n");
        return sb.ToString();
    }

    private static bool MatchesFilter(JobFile jf, string filter)
    {
        var f = filter.Replace('\\', '/').Trim();
        if (f.Length == 0) return true;
        if (Glob.HasWildcards(f))
        {
            try { return Glob.ToRegex(f).IsMatch(jf.Display); } catch { return false; }
        }
        return jf.Display.Equals(f, StringComparison.OrdinalIgnoreCase)
            || jf.Display.EndsWith("/" + f, StringComparison.OrdinalIgnoreCase)
            || jf.Path.Equals(f.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Последние задачи (новые первыми); filter — отбор, например по рабочей области.</summary>
    public static List<JobInfo> List(int max, Func<JobInfo, bool>? filter = null)
    {
        var list = new List<JobInfo>();
        if (!Directory.Exists(JobsDir)) return list;
        foreach (var dir in Directory.EnumerateDirectories(JobsDir).Select(System.IO.Path.GetFileName).Where(IsValidId)
                     .OrderByDescending(n => n, StringComparer.Ordinal).Take(500))
        {
            JobInfo job;
            try { job = Load(dir!); }
            catch (ToolException) { continue; }
            if (filter is not null && !filter(job)) continue;
            list.Add(job);
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>Удалить задачи старше retentionDays (по дате в id). Быстро и ограниченно — вызывается при старте.</summary>
    public static int CleanupOld(int retentionDays)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(JobsDir)) return 0;
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, retentionDays));
            foreach (var dir in Directory.EnumerateDirectories(JobsDir).Take(2000))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (!IsValidId(name)) continue;
                if (!DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var created)) continue;
                if (created >= cutoff) continue;
                RemoveSandbox(name);
                FileUtil.TryDeleteDirectory(dir);
                removed++;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("mcp", $"Очистка задач: {ex.Message}");
        }
        return removed;
    }

    /// <summary>Перед удалением старой задачи убрать её песочницу (worktree и ветку offload/&lt;id&gt;), если она осталась.</summary>
    private static void RemoveSandbox(string id)
    {
        try
        {
            var job = Load(id);
            if (job.Sandbox is { } sb && SandboxState.IsOpen(sb.State))
                GitSandbox.RemoveAsync(sb, deleteBranch: true, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Debug("mcp", $"Песочница задачи {id} не удалена: {ex.Message}");
        }
    }

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream(fs.CanSeek ? (int)Math.Min(fs.Length, int.MaxValue) : 0);
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Атомарная запись байтов: временный файл рядом и замена (жёсткие ссылки не «протекают» в другие файлы).</summary>
    public static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new IOException("file is read-only");
        var tmp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(path)}.offload-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                    else File.Move(tmp, path);
                    return;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 8)
                {
                    Thread.Sleep(60 * (attempt + 1));
                }
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
