using Offload.Core.Config;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

/// <summary>Git-песочница local_agent_task: снимок, изоляция от репозитория пользователя, перенос изменений, теневой репозиторий.</summary>
[Collection("AppPaths")]
public sealed class GitSandboxTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string _project = Path.Combine(Path.GetTempPath(), "gs-" + Guid.NewGuid().ToString("N")[..10]);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly List<string> Secrets = new McpSettings().SecretFilePatterns;

    public GitSandboxTests()
    {
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        ForceDelete(_project);
        _home.Dispose();
    }

    [Fact]
    public async Task Create_SnapshotsWorkingTree_WithoutSecrets_AndLeavesRepoUntouched()
    {
        await InitRepoAsync();
        Write("a.txt", "one\nchanged\n");
        Write("new.txt", "untracked\n");
        Write(".env", "SECRET=1\n");
        var before = await StateAsync();

        var sb = await CreateAsync();

        Assert.Null(sb.ShadowGitDir);
        Assert.False(sb.BaseIsHead);
        Assert.Equal("one\nchanged\n", Read(sb.WorktreeDir, "a.txt"));
        Assert.Equal("untracked\n", Read(sb.WorktreeDir, "new.txt"));
        Assert.False(File.Exists(Path.Combine(sb.WorktreeDir, ".env")), "секрет не должен попадать в песочницу");
        Assert.Equal(before, await StateAsync());
        Assert.False(File.Exists(Path.Combine(_jobDir!, "snapshot.index")), "временный индекс удаляется");
    }

    [Fact]
    public async Task CommitAndApply_TransfersAllowedChangesOnly()
    {
        await InitRepoAsync();
        var headBefore = await GitAsync("rev-parse", "HEAD");
        var sb = await CreateAsync();

        Write(sb.WorktreeDir, "a.txt", "one\nagent\n");
        Write(sb.WorktreeDir, "src/c.txt", "created\n");
        Write(sb.WorktreeDir, ".env.local", "TOKEN=x\n");
        Write(sb.WorktreeDir, "blocked.txt", "nope\n");

        var commit = await GitSandbox.CommitAsync(sb, "offload: test", p => p == "blocked.txt" ? "blocked by test" : null, Secrets, Ct);
        Assert.True(commit.HasChanges);
        Assert.Contains(commit.Dropped, d => d.StartsWith(".env.local", StringComparison.Ordinal));
        Assert.Contains(commit.Dropped, d => d.StartsWith("blocked.txt", StringComparison.Ordinal));

        var changes = await GitSandbox.ChangesAsync(sb, Ct);
        Assert.Equal(new[] { "a.txt", "src/c.txt" }, changes.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));

        var patch = Path.Combine(_home.Path, "changes.patch");
        await GitSandbox.WritePatchAsync(sb, patch, Ct);
        Assert.Null(await GitSandbox.CheckApplyAsync(sb, patch, Ct));
        Assert.Null(await GitSandbox.ApplyAsync(sb, patch, Ct));

        Assert.Equal("one\nagent\n", Read(_project, "a.txt"));
        Assert.Equal("created\n", Read(_project, "src/c.txt"));
        Assert.False(File.Exists(Path.Combine(_project, ".env.local")));
        Assert.False(File.Exists(Path.Combine(_project, "blocked.txt")));
        // Коммит пользователя и индекс не менялись: правки лежат в рабочем дереве незакоммиченными.
        Assert.Equal(headBefore, await GitAsync("rev-parse", "HEAD"));
        Assert.Equal("", await GitAsync("diff", "--cached", "--name-only"));

        await GitSandbox.RemoveAsync(sb, deleteBranch: true, Ct);
        Assert.False(Directory.Exists(sb.WorktreeDir));
        Assert.Equal("", await GitAsync("branch", "--list", sb.Branch));
    }

    [Fact]
    public async Task CheckApply_ReportsConflictWithLaterUserEdit()
    {
        await InitRepoAsync();
        var sb = await CreateAsync();
        Write(sb.WorktreeDir, "a.txt", "one\nagent\n");
        await GitSandbox.CommitAsync(sb, "offload: test", _ => null, null, Ct);
        Write("a.txt", "one\nuser\n");

        var patch = Path.Combine(_home.Path, "changes.patch");
        await GitSandbox.WritePatchAsync(sb, patch, Ct);
        var conflict = await GitSandbox.CheckApplyAsync(sb, patch, Ct);

        Assert.NotNull(conflict);
        Assert.Contains("a.txt", conflict);
        Assert.Equal("one\nuser\n", Read(_project, "a.txt"));
    }

    [Fact]
    public async Task FastForward_CleanTree_MovesBranchToAgentCommit()
    {
        await InitRepoAsync();
        var sb = await CreateAsync();
        Assert.True(sb.BaseIsHead);
        Write(sb.WorktreeDir, "b.txt", "new\n");
        await GitSandbox.CommitAsync(sb, "offload: add b", _ => null, null, Ct);

        Assert.Null(await GitSandbox.FastForwardAsync(sb, Ct));

        Assert.Equal(sb.HeadCommit, await GitAsync("rev-parse", "HEAD"));
        Assert.Equal("new\n", Read(_project, "b.txt"));
        Assert.Equal("Offload", await GitAsync("log", "-1", "--format=%an"));
    }

    [Fact]
    public async Task FastForward_RefusedWhenTreeWasDirty()
    {
        await InitRepoAsync();
        Write("a.txt", "dirty\n");
        var sb = await CreateAsync();
        Write(sb.WorktreeDir, "b.txt", "new\n");
        await GitSandbox.CommitAsync(sb, "offload: add b", _ => null, null, Ct);

        Assert.NotNull(await GitSandbox.FastForwardAsync(sb, Ct));
        Assert.False(File.Exists(Path.Combine(_project, "b.txt")));
    }

    [Fact]
    public async Task ShadowRepository_NonGitProject_StaysWithoutGit()
    {
        Write("a.txt", "one\r\ntwo\r\n");
        Write("node_modules/dep/index.js", "module.exports = 1;\n");
        var sb = await CreateAsync();

        Assert.NotNull(sb.ShadowGitDir);
        Assert.True(PathGuard.IsInside(sb.ShadowGitDir!, GitSandbox.SandboxesDir));
        // Зависимости не копируются в снимок, а подключаются ссылкой на папку проекта.
        Assert.True(PathGuard.IsReparsePoint(Path.Combine(sb.WorktreeDir, "node_modules")), "node_modules должна быть ссылкой, а не копией");
        Assert.Equal("one\r\ntwo\r\n", Read(sb.WorktreeDir, "a.txt"));

        Write(sb.WorktreeDir, "a.txt", "one\r\nagent\r\n");
        var commit = await GitSandbox.CommitAsync(sb, "offload: test", _ => null, null, Ct);
        Assert.True(commit.HasChanges);
        var patch = Path.Combine(_home.Path, "changes.patch");
        await GitSandbox.WritePatchAsync(sb, patch, Ct);
        Assert.Null(await GitSandbox.CheckApplyAsync(sb, patch, Ct));
        Assert.Null(await GitSandbox.ApplyAsync(sb, patch, Ct));

        Assert.Equal("one\r\nagent\r\n", Read(_project, "a.txt"));
        Assert.False(Directory.Exists(Path.Combine(_project, ".git")), "в проекте не должно появиться .git");
        await GitSandbox.RemoveAsync(sb, deleteBranch: true, Ct);
    }

    [Fact]
    public async Task Commit_DropsSymlinksAndGitPaths()
    {
        await InitRepoAsync();
        var sb = await CreateAsync();
        Write(sb.WorktreeDir, "ok.txt", "fine\n");
        // При core.symlinks=false ссылка хранится обычным файлом с текстом цели и режимом 120000 в индексе.
        Write(sb.WorktreeDir, "link", "target");
        await GitAsyncIn(sb.WorktreeDir, "update-index", "--add", "--cacheinfo", "120000," + await HashAsync(sb.WorktreeDir, "target") + ",link");

        var commit = await GitSandbox.CommitAsync(sb, "offload: test", _ => null, null, Ct);

        Assert.Contains(commit.Dropped, d => d.StartsWith("link", StringComparison.Ordinal) && d.Contains("symbolic link"));
        var changes = await GitSandbox.ChangesAsync(sb, Ct);
        Assert.Equal(new[] { "ok.txt" }, changes.Select(c => c.Path));
    }

    [Fact]
    public async Task Commit_DropsNewBuildArtifacts_KeepsSources()
    {
        await InitRepoAsync();
        var sb = await CreateAsync();
        Write(sb.WorktreeDir, "calc.py", "def f():\n    return 1\n");
        Write(sb.WorktreeDir, "__pycache__/calc.cpython-312.pyc", "\u0001bytecode");
        Write(sb.WorktreeDir, "src/App/bin/Release/App.dll", "MZ");
        Write(sb.WorktreeDir, "node_modules/x/index.js", "x");

        var commit = await GitSandbox.CommitAsync(sb, "offload: test", _ => null, Secrets, Ct);

        Assert.Equal(new[] { "calc.py" }, (await GitSandbox.ChangesAsync(sb, Ct)).Select(c => c.Path));
        Assert.Contains(commit.Dropped, d => d.StartsWith("__pycache__/", StringComparison.Ordinal) && d.Contains("artifact"));
    }

    [Theory]
    [InlineData("__pycache__/a.pyc", true)]
    [InlineData("src/bin/Debug/x.dll", true)]
    [InlineData("pkg/node_modules/y/z.js", true)]
    [InlineData("build.log", true)]
    [InlineData("src/binary.cs", false)]
    [InlineData("src/objects/model.py", false)]
    [InlineData("docs/bin.md", false)]
    public void IsBuildArtifact_RecognizesCachesAndOutputs(string path, bool expected) => Assert.Equal(expected, GitSandbox.IsBuildArtifact(path));

    [Fact]
    public async Task Dependencies_LinkedIntoSandbox_NotCommitted_SourceSurvivesRemoval()
    {
        await InitRepoAsync();
        File.WriteAllText(Path.Combine(_project, ".gitignore"), "node_modules/\n");
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", "ignore");
        Write("node_modules/dep/index.js", "module.exports = 42;\n");
        var sb = await CreateAsync();

        Assert.Contains("node_modules", sb.LinkedDirs);
        Assert.Equal("module.exports = 42;\n", Read(sb.WorktreeDir, "node_modules/dep/index.js"));
        Write(sb.WorktreeDir, "app.js", "require('dep');\n");
        await GitSandbox.CommitAsync(sb, "offload: app", _ => null, Secrets, Ct);
        Assert.Equal(new[] { "app.js" }, (await GitSandbox.ChangesAsync(sb, Ct)).Select(c => c.Path));

        await GitSandbox.RemoveAsync(sb, deleteBranch: true, Ct);
        Assert.False(Directory.Exists(sb.WorktreeDir));
        // Удаление песочницы снимает только ссылку: зависимости проекта целы.
        Assert.Equal("module.exports = 42;\n", Read(_project, "node_modules/dep/index.js"));
    }

    [Fact]
    public async Task Commit_IgnoresReplacedDotGitFile_NoForeignFilterRuns()
    {
        await InitRepoAsync();
        var sb = await CreateAsync();
        Assert.NotNull(sb.WorktreeGitDir);

        // Агент подменяет «.git» в worktree на свой gitdir с фильтром, выполняющим команду при git add.
        var evil = Path.Combine(_home.Path, "evil-git");
        await GitAsyncIn(_home.Path, "init", "-q", "--bare", evil);
        var marker = Path.Combine(_home.Path, "pwned.txt");
        File.AppendAllText(Path.Combine(evil, "config"),
            $"[filter \"x\"]\n\tclean = cmd /c echo pwned> \"{marker.Replace("\\", "/")}\"\n\trequired = true\n");
        var dotGit = Path.Combine(sb.WorktreeDir, ".git");
        File.SetAttributes(dotGit, FileAttributes.Normal); // git помечает его скрытым
        File.WriteAllText(dotGit, "gitdir: " + evil.Replace('\\', '/') + "\n");
        Write(sb.WorktreeDir, ".gitattributes", "* filter=x\n");
        Write(sb.WorktreeDir, "b.txt", "new\n");

        var commit = await GitSandbox.CommitAsync(sb, "offload: test", _ => null, Secrets, Ct);

        Assert.False(File.Exists(marker), "фильтр из подменённого gitdir не должен выполняться");
        Assert.True(commit.HasChanges);
        // Коммит попал в настоящую ветку песочницы, а не в чужой репозиторий.
        Assert.Equal(sb.HeadCommit, await GitAsync("rev-parse", "refs/heads/" + sb.Branch));
    }

    [Fact]
    public async Task ShadowSnapshot_SecretDirsAndFiles_NeverHashed()
    {
        const string secret = "kube-token-9f3a7c1e5b";
        Write("a.txt", "one\n");
        Write(".kube/config", "token: " + secret + "\n");
        Write(".env", "API_KEY=" + secret + "\n");
        var sb = await CreateAsync();

        Assert.False(File.Exists(Path.Combine(sb.WorktreeDir, ".kube", "config")), "секретная папка не попадает в песочницу");
        Assert.False(File.Exists(Path.Combine(sb.WorktreeDir, ".env")));
        var objects = await GitAsyncIn(_project, "--git-dir=" + sb.ShadowGitDir, "cat-file", "--batch-all-objects", "--batch");
        Assert.DoesNotContain(secret, objects);
        await GitSandbox.RemoveAsync(sb, deleteBranch: true, Ct);
    }

    [Fact]
    public async Task FastForward_RefusedWhenBranchMovedAfterCheck()
    {
        await InitRepoAsync();
        var sb = await CreateAsync();
        Write(sb.WorktreeDir, "b.txt", "checked\n");
        await GitSandbox.CommitAsync(sb, "offload: add b", _ => null, Secrets, Ct);
        // Кто-то сдвинул ветку песочницы после проверки.
        Write(sb.WorktreeDir, "c.txt", "unchecked\n");
        await GitAsyncIn(sb.WorktreeDir, "add", "-A");
        await GitAsyncIn(sb.WorktreeDir, "-c", "user.name=x", "-c", "user.email=x@x", "commit", "-q", "-m", "sneaky");

        var error = await GitSandbox.FastForwardAsync(sb, Ct);

        Assert.NotNull(error);
        Assert.Contains("changed after the check", error);
        Assert.False(File.Exists(Path.Combine(_project, "c.txt")));
    }

    // ───────────── помощники ─────────────

    private string? _jobDir;

    private Task<SandboxInfo> CreateAsync()
    {
        var job = JobStore.Create("local_agent_task", _project, "t", null);
        _jobDir = JobStore.DirOf(job.Id);
        return GitSandbox.CreateAsync(_project, job.Id, _jobDir, Secrets, Ct);
    }

    private async Task InitRepoAsync()
    {
        await GitAsync("init", "-q");
        await GitAsync("config", "core.autocrlf", "false");
        await GitAsync("config", "core.symlinks", "false");
        await GitAsync("config", "user.name", "Test");
        await GitAsync("config", "user.email", "test@example.com");
        Write("a.txt", "one\ntwo\n");
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", "init");
    }

    /// <summary>HEAD, текущая ветка, статус и индекс пользователя (ветку offload/* песочница создаёт намеренно).</summary>
    private async Task<string> StateAsync() =>
        await GitAsync("rev-parse", "HEAD") + "|" + await GitAsync("rev-parse", "--abbrev-ref", "HEAD") + "|" + await GitAsync("status", "--porcelain") +
        "|" + await GitAsync("diff", "--cached", "--name-only");

    private Task<string> GitAsync(params string[] args) => GitAsyncIn(_project, args);

    private static async Task<string> GitAsyncIn(string dir, params string[] args)
    {
        var r = await Git.RunAsync(dir, args, Ct);
        Assert.True(r.Success, $"git {string.Join(' ', args)}: {r.StdErr}");
        return r.StdOut.Trim();
    }

    private static async Task<string> HashAsync(string dir, string content)
    {
        var tmp = Path.Combine(dir, ".hash-tmp");
        File.WriteAllText(tmp, content);
        var sha = await GitAsyncIn(dir, "hash-object", "-w", tmp);
        File.Delete(tmp);
        return sha;
    }

    private void Write(string rel, string content) => Write(_project, rel, content);

    private static void Write(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel.Replace('/', '\\')));

    private static void ForceDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch
        {
            // Временная папка — не критично.
        }
    }
}
