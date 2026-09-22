using Offload.Core.Logging;
using Offload.Core.Processes;

namespace Offload.OpenCode;

/// <summary>Отметка файла: размер и max(время изменения, время создания) — копия со старой датой тоже заметна.</summary>
internal sealed record FileStamp(long Size, long Ticks)
{
    public static FileStamp? Of(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return null;
            return new FileStamp(fi.Length, Math.Max(fi.LastWriteTimeUtc.Ticks, fi.CreationTimeUtc.Ticks));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Запись «git status --porcelain=v1 -z»: XY, путь от корня репозитория, исходный путь (R/C).</summary>
internal sealed record GitEntry(string Status, string Path, string? OrigPath, FileStamp? Stamp = null);

/// <summary>Состояние рабочей папки до/после запуска агента.</summary>
internal sealed class WorkspaceState
{
    public required string WorkDir { get; init; }

    /// <summary>Корень git-репозитория; null — режим снимка файлов.</summary>
    public string? GitRoot { get; init; }

    public Dictionary<string, GitEntry> Git { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Снимок (путь относительно WorkDir → отметка).</summary>
    public Dictionary<string, FileStamp> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Снимок неполный (превышен лимит файлов).</summary>
    public bool Truncated { get; init; }

    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Состояние получить не удалось — изменения не определяются.</summary>
    public bool Failed { get; init; }
}

/// <summary>
/// Определение файлов, изменённых агентом: в git-репозитории — разница «git status» до/после
/// (плюс отметки уже изменённых файлов: повторная правка не меняет статус), иначе — снимок
/// (путь, размер, время) до ~20 тыс. файлов без .git/node_modules/bin/obj.
/// </summary>
internal static class ChangeTracker
{
    internal const int MaxSnapshotFiles = 20000;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", ".venv",
    };

    public static async Task<WorkspaceState> CaptureAsync(string workDir, CancellationToken ct)
    {
        var git = ProcessRunner.FindOnPath("git.exe");
        if (git is not null)
        {
            var root = await GitRootAsync(git, workDir, ct);
            if (root is not null)
            {
                var entries = await GitStatusAsync(git, root, ct);
                if (entries is not null) return new WorkspaceState { WorkDir = workDir, GitRoot = root, Git = entries };
                Log.Warn("opencode", "git status не выполнен — изменения отслеживаются по снимку файлов");
            }
        }
        return Snapshot(workDir, MaxSnapshotFiles);
    }

    /// <summary>Состояние «после» тем же способом, что и «до».</summary>
    public static async Task<WorkspaceState> CaptureAfterAsync(WorkspaceState before, CancellationToken ct)
    {
        if (before.GitRoot is null) return Snapshot(before.WorkDir, MaxSnapshotFiles + 5000);
        var git = ProcessRunner.FindOnPath("git.exe");
        var entries = git is null ? null : await GitStatusAsync(git, before.GitRoot, ct);
        if (entries is null)
        {
            Log.Warn("opencode", "git status после запуска не выполнен — список изменённых файлов неизвестен");
            return new WorkspaceState { WorkDir = before.WorkDir, GitRoot = before.GitRoot, Failed = true };
        }
        return new WorkspaceState { WorkDir = before.WorkDir, GitRoot = before.GitRoot, Git = entries };
    }

    private static async Task<string?> GitRootAsync(string git, string workDir, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(git, ["-C", workDir, "rev-parse", "--show-toplevel"],
                workingDirectory: workDir, environment: GitEnv, timeout: GitTimeout, ct: ct);
            if (!r.Success) return null;
            var line = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrEmpty(line) ? null : Path.GetFullPath(line.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("opencode", $"git rev-parse: {ex.Message}");
            return null;
        }
    }

    private static async Task<Dictionary<string, GitEntry>?> GitStatusAsync(string git, string root, CancellationToken ct)
    {
        try
        {
            // -z: пути без кавычек и экранирования (core.quotepath не влияет), в UTF-8, относительно корня.
            var r = await ProcessRunner.RunAsync(git,
                ["-c", "core.quotepath=off", "--no-optional-locks", "-C", root,
                 "status", "--porcelain=v1", "-z", "--untracked-files=all"],
                workingDirectory: root, environment: GitEnv, timeout: GitTimeout, ct: ct);
            if (!r.Success)
            {
                Log.Debug("opencode", $"git status: код {r.ExitCode}: {r.StdErr.Trim()}");
                return null;
            }
            var map = new Dictionary<string, GitEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in ParsePorcelainZ(r.StdOut))
                map[e.Path] = e with { Stamp = FileStamp.Of(Full(root, e.Path)) };
            return map;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("opencode", $"git status: {ex.Message}");
            return null;
        }
    }

    private static readonly Dictionary<string, string?> GitEnv = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    /// <summary>
    /// Разбор «XY path\0» записей; у переименований/копий за путём следует исходный путь: «R  new\0old\0».
    /// </summary>
    internal static List<GitEntry> ParsePorcelainZ(string output)
    {
        var list = new List<GitEntry>();
        // ProcessRunner добавляет перевод строки после вывода; сами записи разделены только \0.
        var parts = output.TrimEnd('\r', '\n').Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var e = parts[i];
            if (e.Length > 0 && (e[0] == '\n' || e[0] == '\r')) e = e.TrimStart('\r', '\n');
            if (e.Length < 4 || e[2] != ' ') continue;
            var status = e[..2];
            var path = e[3..];
            string? orig = null;
            if (status[0] is 'R' or 'C' || status[1] is 'R' or 'C')
            {
                if (i + 1 < parts.Length) orig = parts[++i];
            }
            list.Add(new GitEntry(status, path, orig));
        }
        return list;
    }

    internal static WorkspaceState Snapshot(string workDir, int maxFiles)
    {
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint, // ссылки и точки соединения — без зацикливания
            ReturnSpecialDirectories = false,
        };
        var stack = new Stack<string>();
        stack.Push(workDir);
        try
        {
            while (stack.Count > 0 && !truncated)
            {
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> items;
                try
                {
                    items = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
                }
                catch
                {
                    continue;
                }
                foreach (var item in items)
                {
                    if (item is DirectoryInfo d)
                    {
                        if (!SkippedDirs.Contains(d.Name)) stack.Push(d.FullName);
                        continue;
                    }
                    if (item is not FileInfo f) continue;
                    if (files.Count >= maxFiles)
                    {
                        truncated = true;
                        break;
                    }
                    try
                    {
                        files[Rel(workDir, f.FullName)] = new FileStamp(f.Length, Math.Max(f.LastWriteTimeUtc.Ticks, f.CreationTimeUtc.Ticks));
                    }
                    catch
                    {
                        // Файл исчез во время обхода.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("opencode", $"Снимок файлов: {ex.Message}");
        }
        if (truncated) Log.Info("opencode", $"В папке больше {maxFiles} файлов — часть изменений может быть не замечена");
        return new WorkspaceState { WorkDir = workDir, Files = files, Truncated = truncated };
    }

    /// <summary>Изменения «до → после»: «M путь», «A путь», «D путь», «R старый -> новый» (пути относительно рабочей папки, через /).</summary>
    public static List<string> Diff(WorkspaceState before, WorkspaceState after)
    {
        if (before.Failed || after.Failed) return [];
        var changes = before.GitRoot is not null && after.GitRoot is not null ? DiffGit(before, after) : DiffSnapshot(before, after);
        return changes
            .OrderBy(c => c.SortKey, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Line)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<(string SortKey, string Line)> DiffGit(WorkspaceState before, WorkspaceState after)
    {
        var root = before.GitRoot!;
        var wd = before.WorkDir;
        var list = new List<(string, string)>();
        void Add(string code, string gitPath, string? orig = null)
        {
            var rel = RelFromRoot(root, wd, gitPath);
            list.Add((rel, orig is null ? $"{code} {rel}" : $"{code} {RelFromRoot(root, wd, orig)} -> {rel}"));
        }

        foreach (var (path, a) in after.Git)
        {
            if (!before.Git.TryGetValue(path, out var b))
            {
                var x = a.Status[0];
                var y = a.Status[1];
                if (a.Status == "??" || x == 'A') Add("A", path);
                else if ((x == 'R' || y == 'R') && a.OrigPath is not null) Add("R", path, a.OrigPath);
                else if (x == 'C' || y == 'C') Add("A", path);
                else if (a.Stamp is null && !File.Exists(Full(root, path))) Add("D", path);
                else Add("M", path);
                continue;
            }
            // Файл был изменён и до запуска: смотрим на смену статуса и отметку.
            if (b.Status != a.Status || b.Stamp != a.Stamp)
                Add(File.Exists(Full(root, path)) ? "M" : "D", path);
        }

        foreach (var (path, b) in before.Git)
        {
            if (after.Git.ContainsKey(path)) continue;
            // Изменение исчезло из статуса: файл вернули к версии из git, удалили или закоммитили.
            var now = FileStamp.Of(Full(root, path));
            if (now is null)
            {
                if (b.Stamp is not null) Add("D", path);
            }
            else if (b.Status.Contains('D')) Add("A", path);
            else if (now != b.Stamp) Add("M", path);
        }
        return list;
    }

    private static List<(string SortKey, string Line)> DiffSnapshot(WorkspaceState before, WorkspaceState after)
    {
        var wd = before.WorkDir;
        var list = new List<(string, string)>();
        var threshold = before.CapturedAtUtc.Ticks - TimeSpan.FromSeconds(2).Ticks;
        foreach (var (path, a) in after.Files)
        {
            var rel = path.Replace('\\', '/');
            if (!before.Files.TryGetValue(path, out var b))
            {
                // Снимок «до» неполный — новым считаем только файл, появившийся после начала.
                if (!before.Truncated || a.Ticks >= threshold) list.Add((rel, $"A {rel}"));
            }
            else if (a != b)
            {
                list.Add((rel, $"M {rel}"));
            }
        }
        foreach (var path in before.Files.Keys)
        {
            if (after.Files.ContainsKey(path)) continue;
            if (File.Exists(Path.Combine(wd, path))) continue; // просто не попал в неполный снимок
            var rel = path.Replace('\\', '/');
            list.Add((rel, $"D {rel}"));
        }
        return list;
    }

    private static string Full(string root, string gitPath) =>
        Path.Combine(root, gitPath.Replace('/', Path.DirectorySeparatorChar));

    private static string RelFromRoot(string root, string wd, string gitPath) =>
        Rel(wd, Full(root, gitPath));

    private static string Rel(string baseDir, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }
}
