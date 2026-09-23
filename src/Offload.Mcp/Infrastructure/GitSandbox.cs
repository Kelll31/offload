using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Mcp.Infrastructure;

/// <summary>Состояние песочницы агентной задачи (хранится в job.json).</summary>
internal sealed class SandboxInfo
{
    /// <summary>Корень рабочей копии git (или папка проекта, если проект не под git и используется теневой репозиторий).</summary>
    public string RepoRoot { get; set; } = "";

    /// <summary>Теневой bare-репозиторий в папке данных Offload (проект не под git) или null.</summary>
    public string? ShadowGitDir { get; set; }

    /// <summary>Папка проекта относительно RepoRoot ("" — корень).</summary>
    public string SubDir { get; set; } = "";

    public string WorktreeDir { get; set; } = "";

    /// <summary>
    /// Служебная папка worktree (…/.git/worktrees/&lt;id&gt;), определённая при создании. Все git-команды в песочнице получают её
    /// явно (--git-dir/--work-tree): файл «.git» в worktree доступен агенту и мог бы увести git в чужой репозиторий с фильтрами.
    /// </summary>
    public string? WorktreeGitDir { get; set; }

    public string Branch { get; set; } = "";

    /// <summary>Коммит-снимок рабочего дерева на момент запуска (от него ответвлена ветка агента).</summary>
    public string BaseCommit { get; set; } = "";

    /// <summary>Снимок совпал с HEAD (рабочее дерево было чистым) — возможен fast-forward слияния.</summary>
    public bool BaseIsHead { get; set; }

    public string? OriginalHead { get; set; }

    /// <summary>Последний коммит агента в ветке песочницы (null — ещё не зафиксирован).</summary>
    public string? HeadCommit { get; set; }

    /// <summary>pending | conflict | merged | discarded.</summary>
    public string State { get; set; } = SandboxState.Pending;

    /// <summary>Папки зависимостей проекта (node_modules, .venv), подключённые в песочницу через junction.</summary>
    public List<string> LinkedDirs { get; set; } = [];

    [JsonIgnore]
    public string AgentDir => SubDir.Length == 0 ? WorktreeDir : System.IO.Path.Combine(WorktreeDir, SubDir);
}

internal static class SandboxState
{
    public const string Pending = "pending";
    public const string Conflict = "conflict";
    public const string Merged = "merged";
    public const string Discarded = "discarded";

    public static bool IsOpen(string? state) => state is Pending or Conflict;
}

/// <summary>Изменение в ветке песочницы относительно снимка. Path — относительно RepoRoot, через «/».</summary>
internal sealed record SandboxChange(string Path, int Added, int Removed, bool Binary);

/// <summary>Результат фиксации работы агента: есть ли изменения и какие пути отброшены (секреты, вне проекта, ссылки).</summary>
internal sealed record SandboxCommit(bool HasChanges, IReadOnlyList<string> Dropped);

/// <summary>
/// Git-песочница для локального агента. Рабочее дерево проекта (вместе с незакоммиченными и новыми файлами, без секретов)
/// фиксируется во временном индексе как коммит-снимок, от него создаётся git worktree на ветке offload/&lt;job&gt; в папке данных
/// Offload. Агент работает только там; его результат коммитится в ветку и возвращается в проект патчем (git apply) или
/// fast-forward слиянием. Проект без git получает теневой bare-репозиторий (--git-dir/--work-tree), сам проект не меняется.
/// Реальный индекс и HEAD пользователя не трогаются (кроме явного слияния коммитом).
/// </summary>
internal static class GitSandbox
{
    public const string BranchPrefix = "offload/";

    public static string SandboxesDir => System.IO.Path.Combine(AppPaths.DataDir, "sandboxes");

    internal static string ShadowDirFor(string projectRoot) =>
        System.IO.Path.Combine(SandboxesDir, "shadow",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectRoot.ToLowerInvariant())))[..16].ToLowerInvariant());

    /// <summary>Автор коммитов песочницы.</summary>
    private static readonly IReadOnlyDictionary<string, string?> Identity = new Dictionary<string, string?>
    {
        ["GIT_AUTHOR_NAME"] = "Offload",
        ["GIT_AUTHOR_EMAIL"] = "offload@localhost",
        ["GIT_COMMITTER_NAME"] = "Offload",
        ["GIT_COMMITTER_EMAIL"] = "offload@localhost",
    };

    /// <summary>Длинные пути, без автоматической сборки мусора и подписи (подпись может ждать ввода пароля).</summary>
    private static readonly string[] SandboxConfig =
        ["-c", "core.longpaths=true", "-c", "gc.auto=0", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false"];

    /// <summary>Папки, которые теневой репозиторий не снимает даже без .gitignore (зависимости и артефакты сборки).</summary>
    private static readonly string[] ShadowExcludes =
    [
        "node_modules/", "bin/", "obj/", ".vs/", ".idea/", ".vscode/", "dist/", "build/", "out/", "target/", "__pycache__/",
        ".venv/", "venv/", ".gradle/", ".next/", ".nuxt/", "coverage/", ".offload/", "*.log", "*.tmp",
    ];

    // ───────────────────────── создание ─────────────────────────

    /// <summary>Снимок рабочего дерева проекта и worktree на новой ветке. jobDir — папка задачи (для временного индекса).</summary>
    public static async Task<SandboxInfo> CreateAsync(string projectRoot, string jobId, string jobDir, IEnumerable<string>? secretPatterns,
        CancellationToken ct)
    {
        if (Git.Executable is null) throw new ToolException("The git sandbox needs git: install Git for Windows and restart the IDE.");
        if (!JobStore.IsValidId(jobId)) throw new ArgumentException("Некорректный id задачи", nameof(jobId));
        projectRoot = PathGuard.TrimTrailingSeparator(System.IO.Path.GetFullPath(projectRoot));

        var s = new SandboxInfo
        {
            Branch = BranchPrefix + jobId,
            WorktreeDir = System.IO.Path.Combine(SandboxesDir, jobId),
        };

        var repoRoot = Git.FindWorkTreeRoot(projectRoot);
        if (repoRoot is not null)
        {
            var top = await Git.RunAsync(repoRoot, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
            repoRoot = top.Success && top.StdOut.Trim().Length > 0
                ? PathGuard.TrimTrailingSeparator(System.IO.Path.GetFullPath(top.StdOut.Trim()))
                : null;
        }
        if (repoRoot is null)
        {
            s.RepoRoot = projectRoot;
            s.ShadowGitDir = ShadowDirFor(projectRoot);
            await EnsureShadowAsync(s.ShadowGitDir, ct).ConfigureAwait(false);
        }
        else
        {
            s.RepoRoot = repoRoot;
        }
        var sub = System.IO.Path.GetRelativePath(s.RepoRoot, projectRoot);
        s.SubDir = sub == "." || sub.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(sub) ? "" : sub;

        await SnapshotAsync(s, jobDir, secretPatterns, ct).ConfigureAwait(false);

        Directory.CreateDirectory(SandboxesDir);
        if (Directory.Exists(s.WorktreeDir)) FileUtil.TryDeleteDirectory(s.WorktreeDir);
        Ensure(await RepoGitAsync(s, ["worktree", "add", "-q", "-b", s.Branch, s.WorktreeDir, s.BaseCommit], ct,
            timeout: TimeSpan.FromMinutes(10), workTree: false).ConfigureAwait(false), "worktree add");
        // Сразу после создания (агент ещё не работал) запоминаем настоящую служебную папку worktree.
        var gd = Ensure(await Git.RunAsync(s.WorktreeDir, [.. SandboxConfig, "rev-parse", "--absolute-git-dir"], ct).ConfigureAwait(false), "rev-parse --absolute-git-dir");
        s.WorktreeGitDir = PathGuard.TrimTrailingSeparator(System.IO.Path.GetFullPath(gd.StdOut.Trim()));
        LinkDependencies(s, projectRoot);
        Log.Info("mcp", $"Песочница {s.Branch}: {s.WorktreeDir} (снимок {Short(s.BaseCommit)}{(s.ShadowGitDir is null ? "" : ", теневой репозиторий")}" +
                        $"{(s.LinkedDirs.Count > 0 ? ", зависимости: " + string.Join(", ", s.LinkedDirs) : "")})");
        return s;
    }

    /// <summary>Папки зависимостей, без которых не проходят проверки (git их игнорирует и в worktree не копирует).</summary>
    internal static readonly string[] DependencyDirs = ["node_modules", ".venv", "venv"];

    /// <summary>
    /// Подключить в песочницу зависимости проекта через junction: проверка (npm test, pytest) иначе падала бы не из-за правок агента.
    /// Агенту запрещено их менять (правила OpenCode и бриф); в коммит они не попадают; перед удалением песочницы ссылки снимаются.
    /// </summary>
    private static void LinkDependencies(SandboxInfo s, string projectRoot)
    {
        foreach (var name in DependencyDirs)
        {
            var source = System.IO.Path.Combine(projectRoot, name);
            var link = System.IO.Path.Combine(s.AgentDir, name);
            if (!Directory.Exists(source) || PathGuard.IsReparsePoint(source) || Directory.Exists(link)) continue;
            if (NativeMethods.TryCreateJunction(link, source)) s.LinkedDirs.Add(name);
        }
    }

    /// <summary>Снять junction зависимостей (удаляется только ссылка, не содержимое проекта).</summary>
    private static void UnlinkDependencies(SandboxInfo s)
    {
        foreach (var name in s.LinkedDirs)
        {
            var link = System.IO.Path.Combine(s.AgentDir, name);
            try
            {
                if (Directory.Exists(link) && PathGuard.IsReparsePoint(link)) Directory.Delete(link, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("mcp", $"Не удалось снять ссылку {link}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Коммит-снимок рабочего дерева через временный индекс (копия настоящего — ради кэша stat): add -A, без секретов.
    /// Если дерево совпало с HEAD — базой становится сам HEAD.
    /// </summary>
    private static async Task SnapshotAsync(SandboxInfo s, string jobDir, IEnumerable<string>? secretPatterns, CancellationToken ct)
    {
        Directory.CreateDirectory(jobDir);
        var index = System.IO.Path.Combine(jobDir, "snapshot.index");
        var env = new Dictionary<string, string?>(Identity) { ["GIT_INDEX_FILE"] = index };
        try
        {
            string? head = null;
            var h = await RepoGitAsync(s, ["rev-parse", "-q", "--verify", "HEAD^{commit}"], ct).ConfigureAwait(false);
            if (h.Success && h.StdOut.Trim().Length > 0) head = h.StdOut.Trim();

            var seeded = false;
            if (s.ShadowGitDir is null)
            {
                var ip = await RepoGitAsync(s, ["rev-parse", "--git-path", "index"], ct).ConfigureAwait(false);
                if (ip.Success)
                {
                    try
                    {
                        var real = System.IO.Path.GetFullPath(System.IO.Path.Combine(s.RepoRoot, ip.StdOut.Trim()));
                        if (File.Exists(real))
                        {
                            File.Copy(real, index, overwrite: true);
                            seeded = true;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        Log.Debug("mcp", $"Индекс не скопирован: {ex.Message}");
                    }
                }
            }
            if (!seeded && head is not null)
                Ensure(await RepoGitAsync(s, ["read-tree", head], ct, env).ConfigureAwait(false), "read-tree");

            // Секреты исключаются ещё до add: их содержимое не хешируется в хранилище объектов.
            Ensure(await RepoGitAsync(s, ["add", "-A", "--", ".", .. PathGuard.SecretPathspecExcludes(secretPatterns)], ct, env, TimeSpan.FromMinutes(10))
                .ConfigureAwait(false), "add (snapshot)");

            // Отслеживаемые секреты (уже в индексе) убираем из снимка: агент их не видит, а патч их не трогает.
            var ls = Ensure(await RepoGitAsync(s, ["ls-files", "-z"], ct, env).ConfigureAwait(false), "ls-files");
            var secrets = SplitZ(ls.StdOut).Where(p => IsSecretPath(s.RepoRoot, p, secretPatterns)).ToList();
            foreach (var chunk in secrets.Chunk(50))
                Ensure(await RepoGitAsync(s, ["update-index", "--force-remove", "--", .. chunk], ct, env).ConfigureAwait(false), "update-index");

            var tree = Ensure(await RepoGitAsync(s, ["write-tree"], ct, env).ConfigureAwait(false), "write-tree").StdOut.Trim();
            s.OriginalHead = head;
            if (head is not null)
            {
                var ht = await RepoGitAsync(s, ["rev-parse", head + "^{tree}"], ct).ConfigureAwait(false);
                if (ht.Success && ht.StdOut.Trim() == tree)
                {
                    s.BaseCommit = head;
                    s.BaseIsHead = true;
                    return;
                }
            }
            List<string> args = ["commit-tree", tree, "-m", "offload: snapshot of the working tree"];
            if (head is not null) args.InsertRange(2, ["-p", head]);
            s.BaseCommit = Ensure(await RepoGitAsync(s, args, ct, env).ConfigureAwait(false), "commit-tree").StdOut.Trim();
        }
        finally
        {
            TryDelete(index);
            TryDelete(index + ".lock");
        }
    }

    private static async Task EnsureShadowAsync(string gitDir, CancellationToken ct)
    {
        if (File.Exists(System.IO.Path.Combine(gitDir, "HEAD"))) return;
        Directory.CreateDirectory(gitDir);
        Ensure(await Git.RunAsync(gitDir, ["init", "-q", "--bare", gitDir], ct).ConfigureAwait(false), "init (shadow repository)");
        foreach (var (key, value) in new[] { ("core.autocrlf", "false"), ("core.longpaths", "true"), ("gc.auto", "0") })
            Ensure(await Git.RunAsync(gitDir, ["--git-dir=" + gitDir, "config", key, value], ct).ConfigureAwait(false), "config");
        var info = System.IO.Path.Combine(gitDir, "info");
        Directory.CreateDirectory(info);
        await File.WriteAllTextAsync(System.IO.Path.Combine(info, "exclude"), string.Join('\n', ShadowExcludes) + "\n", ct).ConfigureAwait(false);
    }

    // ───────────────────────── работа агента ─────────────────────────

    /// <summary>Есть ли в песочнице незафиксированные изменения (для решения, запускать ли проверку).</summary>
    public static async Task<bool> IsDirtyAsync(SandboxInfo s, CancellationToken ct)
    {
        var r = await WorktreeGitAsync(s, ["status", "--porcelain", "-z", "--untracked-files=all"], ct).ConfigureAwait(false);
        if (r.Success && r.StdOut.Length > 0) return true;
        var head = await WorktreeGitAsync(s, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
        return head.Success && head.StdOut.Trim() != s.BaseCommit;
    }

    /// <summary>
    /// Зафиксировать работу агента в ветке песочницы. Пути, которые нельзя переносить в проект (isAllowed = false, секреты,
    /// символические ссылки, вложенные репозитории), возвращаются к состоянию снимка и попадают в Dropped.
    /// </summary>
    public static async Task<SandboxCommit> CommitAsync(SandboxInfo s, string message, Func<string, string?> whyNotAllowed,
        IEnumerable<string>? secretPatterns, CancellationToken ct)
    {
        // Секретные файлы, созданные агентом, исключаются ещё до add (не хешируются) — но в отчёте их видно.
        var dropped = new List<string>();
        var status = await WorktreeGitAsync(s, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct).ConfigureAwait(false);
        foreach (var entry in SplitZ(status.StdOut))
        {
            if (entry.Length < 4 || !entry.StartsWith("??", StringComparison.Ordinal)) continue;
            var path = entry[3..];
            if (IsSecretPath(s.WorktreeDir, path, secretPatterns)) dropped.Add($"{path} (secret file)");
        }
        Ensure(await WorktreeGitAsync(s, ["add", "-A", "--", ".", .. PathGuard.SecretPathspecExcludes(secretPatterns)], ct,
            timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false), "add");
        var raw = Ensure(await WorktreeGitAsync(s, ["diff", "--cached", "--raw", "-z", "--no-renames", "--no-ext-diff", s.BaseCommit], ct)
            .ConfigureAwait(false), "diff --raw");

        var reset = new List<string>();
        // Новые файлы (которых не было в снимке): артефакты сборки/кэшей от прогонов проверки не переносим.
        var addedRes = await WorktreeGitAsync(s, ["diff", "--cached", "--name-only", "-z", "--no-renames", "--diff-filter=A", s.BaseCommit], ct).ConfigureAwait(false);
        var added = SplitZ(addedRes.StdOut).ToHashSet(StringComparer.Ordinal);
        foreach (var (newMode, path) in ParseRaw(raw.StdOut))
        {
            string? why = null;
            if (added.Contains(path) && IsBuildArtifact(path)) why = "build/cache artifact";
            else if (newMode is "120000") why = "symbolic link";
            else if (newMode is "160000") why = "nested repository";
            else if (IsSecretPath(s.WorktreeDir, path, secretPatterns)) why = "secret file";
            else if (IsGitInternal(path)) why = ".git path";
            else why = whyNotAllowed(path);
            if (why is null) continue;
            reset.Add(path);
            dropped.Add($"{path} ({why})");
        }
        foreach (var chunk in reset.Chunk(50))
        {
            Ensure(await WorktreeGitAsync(s, ["reset", "-q", s.BaseCommit, "--", .. chunk.Select(p => ":(literal)" + p)], ct)
                .ConfigureAwait(false), "reset");
        }

        var staged = await WorktreeGitAsync(s, ["diff", "--cached", "--quiet", "HEAD"], ct).ConfigureAwait(false);
        if (staged.ExitCode == 1)
        {
            var subject = message.Replace("\r", " ").Replace("\n", " ").Trim();
            if (subject.Length > 72) subject = subject[..72].TrimEnd() + "…";
            Ensure(await WorktreeGitAsync(s, ["commit", "-q", "--no-verify", "--allow-empty-message", "-m", subject], ct, Identity)
                .ConfigureAwait(false), "commit");
        }
        var head = Ensure(await WorktreeGitAsync(s, ["rev-parse", "HEAD"], ct).ConfigureAwait(false), "rev-parse HEAD").StdOut.Trim();
        s.HeadCommit = head;
        var any = await WorktreeGitAsync(s, ["diff", "--quiet", "--no-ext-diff", s.BaseCommit, head], ct).ConfigureAwait(false);
        return new SandboxCommit(any.ExitCode == 1, dropped);
    }

    /// <summary>Изменения ветки агента относительно снимка (+/− по строкам; двоичные — без счёта).</summary>
    public static async Task<List<SandboxChange>> ChangesAsync(SandboxInfo s, CancellationToken ct)
    {
        if (s.HeadCommit is null) return [];
        var r = Ensure(await RepoGitAsync(s, ["diff", "--numstat", "-z", "--no-renames", "--no-ext-diff", s.BaseCommit, s.HeadCommit], ct,
            workTree: false).ConfigureAwait(false), "diff --numstat");
        var list = new List<SandboxChange>();
        foreach (var entry in SplitZ(r.StdOut))
        {
            var parts = entry.Split('\t', 3);
            if (parts.Length < 3) continue;
            var binary = parts[0] == "-";
            _ = int.TryParse(parts[0], out var added);
            _ = int.TryParse(parts[1], out var removed);
            list.Add(new SandboxChange(parts[2], added, removed, binary));
        }
        return list;
    }

    /// <summary>Текстовый diff ветки агента (для ревью до слияния), не длиннее maxChars.</summary>
    public static async Task<string> DiffTextAsync(SandboxInfo s, int maxChars, IReadOnlyCollection<string>? paths, CancellationToken ct)
    {
        if (s.HeadCommit is null) return "(the agent has not committed anything yet)";
        List<string> args = ["diff", "--no-renames", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/", s.BaseCommit, s.HeadCommit];
        if (paths is { Count: > 0 }) args.AddRange(["--", .. paths.Select(p => ":(glob)" + p.Replace('\\', '/'))]);
        var stat = await RepoGitAsync(s, ["diff", "--stat=120", "--no-renames", s.BaseCommit, s.HeadCommit], ct, workTree: false).ConfigureAwait(false);
        var diff = await RepoGitAsync(s, args, ct, workTree: false, maxChars: Math.Max(1000, maxChars)).ConfigureAwait(false);
        var text = stat.StdOut.TrimEnd() + "\n\n" + diff.StdOut;
        return text.Length <= maxChars ? text : text[..maxChars] + "\n…[diff truncated]";
    }

    // ───────────────────────── слияние ─────────────────────────

    /// <summary>Записать двоичный патч ветки агента в файл (git сам пишет файл — байты не перекодируются).</summary>
    public static async Task WritePatchAsync(SandboxInfo s, string patchPath, CancellationToken ct)
    {
        if (s.HeadCommit is null) throw new ToolException("The sandbox has no committed changes.");
        Ensure(await RepoGitAsync(s,
        [
            "diff", "--binary", "--full-index", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-relative", "--no-color",
            "--src-prefix=a/", "--dst-prefix=b/", "--output=" + patchPath, s.BaseCommit, s.HeadCommit,
        ], ct, workTree: false).ConfigureAwait(false), "diff --binary");
    }

    /// <summary>Применится ли патч к текущему рабочему дереву проекта (без изменений). null — да, иначе текст ошибки git.</summary>
    public static async Task<string?> CheckApplyAsync(SandboxInfo s, string patchPath, CancellationToken ct)
    {
        var r = await RepoGitAsync(s, ["apply", "--check", "--whitespace=nowarn", patchPath], ct).ConfigureAwait(false);
        return r.Success ? null : FirstLines(r.StdErr, 12);
    }

    /// <summary>Применить патч к рабочему дереву проекта (индекс и HEAD пользователя не меняются).</summary>
    public static async Task<string?> ApplyAsync(SandboxInfo s, string patchPath, CancellationToken ct)
    {
        var r = await RepoGitAsync(s, ["apply", "--whitespace=nowarn", patchPath], ct).ConfigureAwait(false);
        return r.Success ? null : FirstLines(r.StdErr, 12);
    }

    /// <summary>
    /// Fast-forward текущей ветки проекта на коммит агента. Возможно только если снимок был равен HEAD и HEAD с тех пор не
    /// сдвинулся; git сам откажет, если это перезапишет незакоммиченные правки. null — успех, иначе причина.
    /// </summary>
    public static async Task<string?> FastForwardAsync(SandboxInfo s, CancellationToken ct)
    {
        if (s.ShadowGitDir is not null) return "the project is not a git repository";
        if (!s.BaseIsHead) return "the working tree had uncommitted changes when the task started";
        if (s.HeadCommit is null) return "the sandbox has no committed changes";
        var head = await RepoGitAsync(s, ["rev-parse", "-q", "--verify", "HEAD^{commit}"], ct).ConfigureAwait(false);
        if (!head.Success || head.StdOut.Trim() != s.OriginalHead) return "HEAD moved since the task started";
        // Вливаем именно проверенный коммит, а не то, на что сейчас указывает ветка.
        var branch = await RepoGitAsync(s, ["rev-parse", "-q", "--verify", "refs/heads/" + s.Branch], ct, workTree: false).ConfigureAwait(false);
        if (!branch.Success || branch.StdOut.Trim() != s.HeadCommit) return $"branch {s.Branch} changed after the check";
        var r = await RepoGitAsync(s, ["merge", "-q", "--ff-only", "--no-verify", s.HeadCommit], ct, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        return r.Success ? null : FirstLines(r.StdErr.Length > 0 ? r.StdErr : r.StdOut, 8);
    }

    /// <summary>Удалить worktree и (по желанию) ветку. Ошибки только журналируются.</summary>
    public static async Task RemoveAsync(SandboxInfo s, bool deleteBranch, CancellationToken ct)
    {
        try
        {
            // Сначала снимаем junction: удаление worktree не должно зайти внутрь node_modules проекта.
            UnlinkDependencies(s);
            if (Directory.Exists(s.WorktreeDir))
                await RepoGitAsync(s, ["worktree", "remove", "--force", "--force", s.WorktreeDir], ct, workTree: false).ConfigureAwait(false);
            FileUtil.TryDeleteDirectory(s.WorktreeDir);
            await RepoGitAsync(s, ["worktree", "prune"], ct, workTree: false).ConfigureAwait(false);
            if (deleteBranch)
                await RepoGitAsync(s, ["branch", "-D", s.Branch], ct, workTree: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug("mcp", $"Песочница {s.Branch} не удалена полностью: {ex.Message}");
        }
    }

    /// <summary>Удалить папки песочниц без задачи (задача удалена по сроку хранения). Вызывается при старте, в фоне.</summary>
    public static int CleanupOrphans()
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(SandboxesDir)) return 0;
            foreach (var dir in Directory.EnumerateDirectories(SandboxesDir).Take(500))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (!JobStore.IsValidId(name) || Directory.Exists(System.IO.Path.Combine(JobStore.JobsDir, name))) continue;
                FileUtil.TryDeleteDirectory(dir);
                removed++;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("mcp", $"Очистка песочниц: {ex.Message}");
        }
        return removed;
    }

    // ───────────────────────── вспомогательное ─────────────────────────

    private static Task<ChildResult> RepoGitAsync(SandboxInfo s, IEnumerable<string> args, CancellationToken ct,
        IReadOnlyDictionary<string, string?>? env = null, TimeSpan? timeout = null, bool workTree = true, int maxChars = 16_000_000)
    {
        List<string> all = [.. SandboxConfig];
        if (s.ShadowGitDir is not null)
        {
            all.Add("--git-dir=" + s.ShadowGitDir);
            if (workTree) all.Add("--work-tree=" + s.RepoRoot);
        }
        all.AddRange(args);
        return Git.RunAsync(s.RepoRoot, all, ct, maxChars, timeout ?? TimeSpan.FromMinutes(2), extraEnv: env);
    }

    private static Task<ChildResult> WorktreeGitAsync(SandboxInfo s, IEnumerable<string> args, CancellationToken ct,
        IReadOnlyDictionary<string, string?>? env = null, TimeSpan? timeout = null)
    {
        if (s.WorktreeGitDir is null) throw new ToolException("The sandbox has no recorded git directory; discard it and start a new task.");
        return Git.RunAsync(s.WorktreeDir, [.. SandboxConfig, "--git-dir=" + s.WorktreeGitDir, "--work-tree=" + s.WorktreeDir, .. args], ct,
            timeout: timeout ?? TimeSpan.FromMinutes(2), extraEnv: env);
    }

    /// <summary>Секрет по имени файла или по папке (.ssh, .kube, .docker…), а также .git — то же правило, что у проверок чтения.</summary>
    private static bool IsSecretPath(string root, string gitPath, IEnumerable<string>? secretPatterns) =>
        PathGuard.IsSecretName(FileName(gitPath), secretPatterns)
        || PathGuard.CheckRead(System.IO.Path.Combine(root, gitPath.Replace('/', System.IO.Path.DirectorySeparatorChar)), secretPatterns) is "secret" or ".git internals";

    private static ChildResult Ensure(ChildResult r, string what)
    {
        if (r.Success) return r;
        var detail = FirstLines(r.StdErr.Length > 0 ? r.StdErr : r.StdOut, 4);
        throw new ToolException(r.TimedOut
            ? $"git {what} timed out in the sandbox."
            : $"git {what} failed (exit {r.ExitCode}){(detail.Length > 0 ? ": " + detail : "")}");
    }

    /// <summary>Разбор «git diff --raw -z»: пары (новый режим, путь).</summary>
    internal static IEnumerable<(string NewMode, string Path)> ParseRaw(string output)
    {
        var items = output.Split('\0');
        for (var i = 0; i + 1 < items.Length; i += 2)
        {
            var meta = items[i];
            if (!meta.StartsWith(':')) yield break;
            var fields = meta[1..].Split(' ');
            yield return (fields.Length > 1 ? fields[1] : "", items[i + 1]);
        }
    }

    internal static IEnumerable<string> SplitZ(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static readonly HashSet<string> ArtifactDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".venv", "venv", "node_modules", ".gradle", ".vs",
        ".next", ".nuxt", ".turbo", ".parcel-cache", "coverage", ".nyc_output", "bin", "obj", "target", ".offload", "TestResults",
    };

    private static readonly HashSet<string> ArtifactExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pyc", ".pyo", ".class", ".o", ".obj", ".pdb", ".tsbuildinfo", ".log", ".tmp", ".cache",
    };

    /// <summary>Файл сборки/кэша (bin/obj, __pycache__, node_modules, *.pyc…): такие новые файлы — след прогона проверки, а не работа агента.</summary>
    internal static bool IsBuildArtifact(string gitPath)
    {
        var segments = gitPath.Split('/');
        if (segments.Take(segments.Length - 1).Any(ArtifactDirs.Contains)) return true;
        return ArtifactExtensions.Contains(System.IO.Path.GetExtension(gitPath));
    }

    private static string FileName(string gitPath) => gitPath[(gitPath.LastIndexOf('/') + 1)..];

    private static bool IsGitInternal(string gitPath) =>
        gitPath.Split('/').Any(seg => seg.TrimEnd(' ', '.').Equals(".git", StringComparison.OrdinalIgnoreCase));

    internal static string FirstLines(string text, int max)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(max);
        return string.Join("\n", lines);
    }

    private static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
