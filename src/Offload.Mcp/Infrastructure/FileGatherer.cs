using System.Text;
using System.Text.RegularExpressions;
using Offload.Core.Logging;

namespace Offload.Mcp.Infrastructure;

internal sealed class GatheredFile
{
    public required string FullPath { get; init; }
    /// <summary>Путь для вывода и ссылок path:line (относительно корня, с «/»).</summary>
    public required string Display { get; init; }
    public required string Text { get; init; }
    public required TextFormat Format { get; init; }
    public required long SizeBytes { get; init; }
    /// <summary>Файл прочитан не полностью (больше MaxFileBytes).</summary>
    public bool Truncated { get; init; }

    public string[] Lines => _lines ??= TextCodec.SplitLines(Text);
    private string[]? _lines;

    /// <summary>Оценка токенов текста с номерами строк.</summary>
    public int EstTokens => _tokens ??= Tokens.Estimate(Text) + Lines.Length * 2;
    private int? _tokens;

    /// <summary>Текст с номерами строк («12| code») — чтобы модель могла ссылаться на path:line.</summary>
    public string Numbered(int fromLine = 1, int toLine = int.MaxValue)
    {
        var lines = Lines;
        var sb = new StringBuilder(Text.Length + lines.Length * 6);
        var last = Math.Min(toLine, lines.Length);
        for (var i = Math.Max(1, fromLine); i <= last; i++) sb.Append(i).Append("| ").Append(lines[i - 1]).Append('\n');
        return sb.ToString();
    }
}

internal sealed record SkippedFile(string Display, string Reason);

internal sealed class GatherResult
{
    public List<GatheredFile> Files { get; } = [];
    public List<SkippedFile> Skipped { get; } = [];
    public long TotalBytes { get; set; }
    /// <summary>Сработал лимит перебора (слишком много файлов под шаблоном).</summary>
    public string? LimitNote { get; set; }

    public int TotalTokens => Files.Sum(f => f.EstTokens);

    /// <summary>«coverage: full (5 files)» / «coverage: 3/5 files; skipped: a.bin (binary), …».</summary>
    public string CoverageLine(int? coveredFiles = null, IEnumerable<string>? extraNotes = null)
    {
        var total = Files.Count + Skipped.Count;
        var covered = coveredFiles ?? Files.Count;
        var truncated = Files.Where(f => f.Truncated).Select(f => $"{f.Display} (truncated)").ToList();
        var notes = new List<string>();
        notes.AddRange(truncated);
        notes.AddRange(Skipped.Select(s => $"{s.Display} ({s.Reason})"));
        if (extraNotes is not null) notes.AddRange(extraNotes);
        if (LimitNote is not null) notes.Add(LimitNote);
        if (covered == total && notes.Count == 0) return $"coverage: full ({total} file{(total == 1 ? "" : "s")})";
        var sb = new StringBuilder($"coverage: {covered}/{total} files");
        if (notes.Count > 0)
        {
            sb.Append("; not fully read: ");
            sb.Append(string.Join(", ", notes.Take(8)));
            if (notes.Count > 8) sb.Append($", +{notes.Count - 8} more");
        }
        return sb.ToString();
    }
}

internal sealed record GatherOptions(
    int MaxFileBytes,
    long MaxTotalBytes,
    IReadOnlyList<string> SecretPatterns,
    int MaxFiles = 400,
    int MaxEntriesVisited = 30_000);

/// <summary>
/// Сбор файлов по путям/папкам/glob: в git-репозитории — через «git ls-files -co --exclude-standard» (учёт .gitignore),
/// иначе обход с пропуском служебных папок. Пропускает двоичные файлы и секреты, ограничивает объём.
/// </summary>
internal static class FileGatherer
{
    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "target", ".venv", "venv", "__pycache__", ".idea", ".vs", "packages",
        ".hg", ".svn", ".next", ".nuxt", ".gradle", ".terraform", "__history", "__recovery",
    };

    public static async Task<GatherResult> GatherAsync(IEnumerable<string> specs, IReadOnlyList<string> roots, GatherOptions options,
        CancellationToken ct)
    {
        var result = new GatherResult();
        var candidates = await ExpandAsync(specs, roots, options, result, ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirVerdicts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var over = 0;
        foreach (var (full, isExplicit) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(full)) continue;
            if (result.Files.Count >= options.MaxFiles)
            {
                over++;
                continue;
            }
            var display = PathGuard.Display(full, roots);
            var reason = CheckFile(full, options, roots, isExplicit) ?? (isExplicit ? null : CheckParentDir(full, roots, dirVerdicts));
            if (reason is not null)
            {
                // Файлы, найденные обходом, в .git и т.п. не перечисляем — только явные.
                if (isExplicit || reason != ".git internals") result.Skipped.Add(new(display, reason));
                continue;
            }
            var item = ReadFile(full, display, options, result);
            if (item is not null) result.Files.Add(item);
        }
        if (over > 0) result.LimitNote = $"{over} more files not read (limit {options.MaxFiles} files; narrow the paths)";
        return result;
    }

    /// <summary>Развернуть пути/папки/glob в список существующих файлов (без чтения). Ненайденное — в result.Skipped.</summary>
    public static async Task<List<(string Full, bool Explicit)>> ExpandAsync(IEnumerable<string> specs, IReadOnlyList<string> roots,
        GatherOptions options, GatherResult result, CancellationToken ct)
    {
        var candidates = new List<(string Full, bool Explicit)>();
        var visited = 0;
        foreach (var rawSpec in specs)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawSpec)) continue;
            var spec = PathGuard.Clean(rawSpec);
            try
            {
                if (Glob.HasWildcards(spec))
                {
                    var (baseRaw, pattern) = Glob.Split(spec);
                    var baseFull = PathGuard.Resolve(baseRaw.Length == 0 ? "." : baseRaw, roots);
                    if (!Directory.Exists(baseFull))
                    {
                        result.Skipped.Add(new(spec, "not found"));
                        continue;
                    }
                    var why = BaseDirProblem(baseFull, roots, options);
                    if (why is not null)
                    {
                        result.Skipped.Add(new(spec, why));
                        continue;
                    }
                    Regex regex;
                    try { regex = Glob.ToRegex(pattern); }
                    catch (Exception) { throw new ToolException($"Invalid glob pattern '{spec}'."); }
                    var before = candidates.Count;
                    await foreach (var f in EnumerateAsync(baseFull, options, () => ++visited, result, ct).ConfigureAwait(false))
                    {
                        var rel = Path.GetRelativePath(baseFull, f).Replace('\\', '/');
                        if (regex.IsMatch(rel)) candidates.Add((f, false));
                        if (candidates.Count > options.MaxFiles * 4) break;
                    }
                    if (candidates.Count == before) result.Skipped.Add(new(spec, "no files match"));
                }
                else
                {
                    var full = PathGuard.Resolve(spec, roots);
                    if (Directory.Exists(full))
                    {
                        var why = BaseDirProblem(full, roots, options);
                        if (why is not null)
                        {
                            result.Skipped.Add(new(PathGuard.Display(full, roots), why));
                            continue;
                        }
                        var before = candidates.Count;
                        await foreach (var f in EnumerateAsync(full, options, () => ++visited, result, ct).ConfigureAwait(false))
                        {
                            candidates.Add((f, false));
                            if (candidates.Count > options.MaxFiles * 4) break;
                        }
                        if (candidates.Count == before) result.Skipped.Add(new(PathGuard.Display(full, roots), "empty directory"));
                    }
                    else if (File.Exists(full))
                    {
                        candidates.Add((full, true));
                    }
                    else
                    {
                        result.Skipped.Add(new(PathGuard.Display(full, roots), "not found"));
                    }
                }
            }
            catch (ToolException ex)
            {
                result.Skipped.Add(new(spec, ex.Message));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Файлы по путям/папкам/glob, прошедшие те же проверки, что и при чтении (секреты, .git, ссылки наружу, двоичные расширения),
    /// но без чтения содержимого — для индекса кода и поиска.
    /// </summary>
    public static async Task<List<string>> ListFilesAsync(IEnumerable<string> specs, IReadOnlyList<string> roots, GatherOptions options,
        GatherResult result, CancellationToken ct)
    {
        var candidates = await ExpandAsync(specs, roots, options, result, ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirVerdicts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var (full, isExplicit) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(full)) continue;
            var reason = CheckFile(full, options, roots, isExplicit) ?? (isExplicit ? null : CheckParentDir(full, roots, dirVerdicts));
            if (reason is not null)
            {
                if (isExplicit) result.Skipped.Add(new(PathGuard.Display(full, roots), reason));
                continue;
            }
            list.Add(full);
            if (list.Count >= options.MaxFiles)
            {
                result.LimitNote = $"more than {options.MaxFiles} files; narrow the paths";
                break;
            }
        }
        return list;
    }

    /// <summary>
    /// Проверка одного файла перед чтением: секреты, .git, двоичное расширение. Явно указанные файлы и ссылки
    /// проверяются и в канонической форме (junction/symlink, короткие имена 8.3) и с ограничениями вне проекта.
    /// </summary>
    public static string? CheckFile(string full, GatherOptions options, IReadOnlyList<string> roots, bool isExplicit)
    {
        var reason = isExplicit || PathGuard.IsReparsePoint(full)
            ? PathGuard.CheckReadResolved(full, roots, options.SecretPatterns)
            : PathGuard.CheckRead(full, options.SecretPatterns);
        if (reason is not null) return reason;
        if (TextCodec.IsBinaryExtension(full)) return "binary";
        return null;
    }

    /// <summary>
    /// Папка найденного перебором файла не должна вести наружу через ссылку (junction посередине пути).
    /// Результат кэшируется по папке — один системный вызов на каталог.
    /// </summary>
    private static string? CheckParentDir(string file, IReadOnlyList<string> roots, Dictionary<string, string?> cache)
    {
        var dir = Path.GetDirectoryName(file);
        if (dir is null) return null;
        if (cache.TryGetValue(dir, out var verdict)) return verdict;
        try
        {
            var canonical = PathGuard.Canonicalize(dir);
            var wasInside = PathGuard.FindRoot(dir, roots) is not null;
            verdict = wasInside && PathGuard.FindRoot(canonical, roots) is null ? "link to outside the workspace" : PathGuard.CheckRead(canonical, null);
        }
        catch (ToolException)
        {
            verdict = "broken link";
        }
        cache[dir] = verdict;
        return verdict;
    }

    /// <summary>Папку/базу glob обходим только внутри проекта (и не через ссылку наружу).</summary>
    private static string? BaseDirProblem(string dir, IReadOnlyList<string> roots, GatherOptions options)
    {
        var why = PathGuard.CheckRead(dir, options.SecretPatterns);
        if (why is not null) return why;
        string canonical;
        try { canonical = PathGuard.Canonicalize(dir); }
        catch (ToolException) { return "broken link"; }
        if (PathGuard.FindRoot(canonical, roots) is null)
            return "directories and globs outside the workspace are not allowed; pass explicit file paths";
        return PathGuard.CheckRead(canonical, options.SecretPatterns);
    }

    private static GatheredFile? ReadFile(string full, string display, GatherOptions options, GatherResult result)
    {
        try
        {
            var info = new FileInfo(full);
            var size = info.Length;
            var toRead = (int)Math.Min(size, options.MaxFileBytes);
            if (result.TotalBytes + toRead > options.MaxTotalBytes)
            {
                result.Skipped.Add(new(display, "total size limit"));
                return null;
            }
            var buffer = new byte[toRead];
            int read;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = fs.ReadAtLeast(buffer, toRead, throwOnEndOfStream: false);
            }
            var data = buffer.AsSpan(0, read);
            if (TextCodec.LooksBinary(data))
            {
                result.Skipped.Add(new(display, "binary"));
                return null;
            }
            var truncated = size > read;
            var (text, format) = TextCodec.Decode(data, truncated);
            result.TotalBytes += read;
            return new GatheredFile
            {
                FullPath = full,
                Display = display,
                Text = text,
                Format = format,
                SizeBytes = size,
                Truncated = truncated,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Skipped.Add(new(display, ex is UnauthorizedAccessException ? "access denied" : "unreadable"));
            return null;
        }
    }

    /// <summary>Файлы под папкой: git ls-files (если это рабочая копия), иначе обход без служебных папок и ссылок.</summary>
    private static async IAsyncEnumerable<string> EnumerateAsync(string baseDir, GatherOptions options, Func<int> tick, GatherResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var listed = await TryGitListAsync(baseDir, options, ct).ConfigureAwait(false);
        if (listed is { Files.Count: > 0 })
        {
            if (listed.Value.Capped) result.LimitNote = $"too many files under {baseDir}; only the first {options.MaxEntriesVisited} were considered";
            foreach (var f in listed.Value.Files) yield return f;
            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(baseDir);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false,
                }).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch
            {
                continue;
            }
            var subdirs = new List<string>();
            foreach (var e in entries)
            {
                if (tick() > options.MaxEntriesVisited)
                {
                    result.LimitNote = $"too many entries under {baseDir}; narrow the path or glob";
                    yield break;
                }
                if (e is DirectoryInfo d)
                {
                    // Ссылки на папки (junction/symlink) не обходим: выход за пределы проекта и циклы.
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (IgnoredDirs.Contains(d.Name)) continue;
                    subdirs.Add(d.FullName);
                }
                else
                {
                    yield return e.FullName;
                }
            }
            for (var i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
    }

    private static async Task<(List<string> Files, bool Capped)?> TryGitListAsync(string baseDir, GatherOptions options, CancellationToken ct)
    {
        if (Git.Executable is null || Git.FindWorkTreeRoot(baseDir) is null) return null;
        try
        {
            var files = new List<string>();
            var capped = false;
            var res = await Git.RunAsync(baseDir, ["ls-files", "-co", "--exclude-standard"], ct, maxChars: 1, timeout: TimeSpan.FromSeconds(30),
                onLine: line =>
                {
                    if (line.Length == 0) return true;
                    var rel = Git.Unquote(line);
                    try
                    {
                        var full = Path.GetFullPath(Path.Combine(baseDir, rel));
                        if (PathGuard.IsInside(full, baseDir)) files.Add(full);
                    }
                    catch
                    {
                        // Странное имя — пропускаем.
                    }
                    if (files.Count >= options.MaxEntriesVisited)
                    {
                        capped = true;
                        return false;
                    }
                    return true;
                }).ConfigureAwait(false);
            if (!capped && !res.Success)
            {
                Log.Debug("mcp", $"git ls-files в {baseDir}: код {res.ExitCode}: {res.StdErr.Trim()}");
                return null;
            }
            // ls-files -c перечисляет и удалённые с диска файлы, и подмодули (папки).
            var existing = files.Where(File.Exists).ToList();
            return (existing, capped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("mcp", $"git ls-files не удался: {ex.Message}");
            return null;
        }
    }
}
