using System.Diagnostics;
using System.Text;
using Offload.Core.Config;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

[Collection("AppPaths")]
public class PathGuardTests
{
    private static readonly List<string> Secrets = new McpSettings().SecretFilePatterns;

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("file.txt:hidden")]
    [InlineData("file.txt::$DATA")]
    [InlineData(@"\\server\share\x.cs")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData("//server/share")]
    [InlineData("NUL")]
    [InlineData("src/con.txt")]
    [InlineData("C:relative.txt")]
    [InlineData(@"\rooted.txt")]
    [InlineData("a\u0001b")]
    [InlineData("a|b")]
    public void Resolve_RejectsDangerousPaths(string raw)
    {
        using var env = new TestEnv();
        Assert.Throws<ToolException>(() => PathGuard.Resolve(raw, [env.Workspace]));
    }

    [Fact]
    public void Resolve_RelativeAndAbsolute()
    {
        using var env = new TestEnv();
        Assert.Equal(Path.Combine(env.Workspace, "src", "a.cs"), PathGuard.Resolve("src/a.cs", [env.Workspace]));
        Assert.Equal(Path.Combine(env.Workspace, "a.cs"), PathGuard.Resolve("\"./src/../a.cs\"", [env.Workspace]));
        Assert.Equal(@"C:\Windows\win.ini", PathGuard.Resolve(@"C:\Windows\win.ini", [env.Workspace]));
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".ENV.local", true)]
    [InlineData("prod.env", false)]
    [InlineData("id_rsa", true)]
    [InlineData("id_ed25519.pub", true)]
    [InlineData("server.PEM", true)]
    [InlineData("secrets.json", true)]
    [InlineData(".npmrc", true)]
    [InlineData(".git-credentials", true)]
    [InlineData(".env.", true)]
    [InlineData("Program.cs", false)]
    [InlineData("environment.ts", false)]
    public void SecretNames(string name, bool secret) => Assert.Equal(secret, PathGuard.IsSecretName(name, Secrets));

    [Fact]
    public void CheckRead_BlocksGitAndSecretsAndProfileSecrets()
    {
        using var env = new TestEnv();
        Assert.Equal(".git internals", PathGuard.CheckRead(Path.Combine(env.Workspace, ".git", "config"), Secrets));
        Assert.Equal(".git internals", PathGuard.CheckRead(Path.Combine(env.Workspace, ".GIT", "HEAD"), Secrets));
        Assert.Equal("secret", PathGuard.CheckRead(Path.Combine(env.Workspace, ".env"), Secrets));
        Assert.Equal("secret", PathGuard.CheckRead(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config"), Secrets));
        Assert.Equal("private location", PathGuard.CheckRead(Path.Combine(env.Home.Path, "config.json"), Secrets));
        Assert.Null(PathGuard.CheckRead(Path.Combine(env.Workspace, "src", "a.cs"), Secrets));
    }

    [Fact]
    public void CheckWrite_RestrictsToWorkspaceAndProtectsConfig()
    {
        using var env = new TestEnv();
        string[] roots = [env.Workspace];
        var ok = PathGuard.CheckWrite(Path.Combine(env.Workspace, "src", "new.cs"), roots, true, Secrets, "src/new.cs");
        Assert.Equal(Path.Combine(env.Workspace, "src", "new.cs"), ok);

        Assert.Throws<ToolException>(() => PathGuard.CheckWrite(Path.Combine(Path.GetTempPath(), "elsewhere.cs"), roots, true, Secrets, "x"));
        Assert.Throws<ToolException>(() => PathGuard.CheckWrite(Path.Combine(env.Workspace, ".git", "hooks", "pre-commit"), roots, true, Secrets, "x"));
        Assert.Throws<ToolException>(() => PathGuard.CheckWrite(Path.Combine(env.Workspace, ".claude", "settings.json"), roots, true, Secrets, "x"));
        Assert.Throws<ToolException>(() => PathGuard.CheckWrite(Path.Combine(env.Workspace, ".mcp.json"), roots, true, Secrets, "x"));
        Assert.Throws<ToolException>(() => PathGuard.CheckWrite(Path.Combine(env.Workspace, "CLAUDE.md"), roots, true, Secrets, "x"));
        Assert.Throws<ToolException>(() => PathGuard.CheckWrite(Path.Combine(env.Workspace, ".github", "workflows", "ci.yml"), roots, true, Secrets, "x"));
        // Остальные точки исполнения: CI, git-хуки менеджеров, dev-контейнер, конфигурации запуска IDE.
        foreach (var rel in new[] { ".gitlab-ci.yml", ".pre-commit-config.yaml", "lefthook.yml", ".github/actions/setup/action.yml", ".devcontainer/devcontainer.json", ".idea/runConfigurations/x.xml", ".github/copilot-instructions.md" })
            Assert.Throws<ToolException>(() => PathGuard.CheckWrite(Path.Combine(env.Workspace, rel.Replace('/', '\\')), roots, true, Secrets, rel));
        Assert.Throws<ToolException>(() => PathGuard.CheckWrite(Path.Combine(env.Workspace, "config", ".env"), roots, true, Secrets, "x"));
        Assert.Throws<ToolException>(() => PathGuard.CheckWrite(env.Workspace, roots, true, Secrets, "."));
        // Без ограничения рабочей папкой запись снаружи разрешена (но секреты — нет).
        PathGuard.CheckWrite(Path.Combine(Path.GetTempPath(), "elsewhere.cs"), roots, false, Secrets, "x");
    }

    [Fact]
    public void CheckWrite_JunctionEscapeIsBlocked()
    {
        using var env = new TestEnv();
        var outside = Path.Combine(Path.GetTempPath(), "pc-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            var link = Path.Combine(env.Workspace, "link");
            var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.WaitForExit(10_000);
            Assert.True(Directory.Exists(link), "junction was not created");
            var ex = Assert.Throws<ToolException>(() =>
                PathGuard.CheckWrite(Path.Combine(link, "evil.cs"), [env.Workspace], true, Secrets, "link/evil.cs"));
            Assert.Contains("outside the workspace", ex.Message);
            // Сама ссылка — тоже нельзя.
            Assert.Throws<ToolException>(() => PathGuard.CheckWrite(link, [env.Workspace], true, Secrets, "link"));
        }
        finally
        {
            try { Directory.Delete(Path.Combine(env.Workspace, "link")); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void Workspace_UnsafeRoots()
    {
        Assert.True(Workspace.IsUnsafeWriteRoot(@"C:\"));
        Assert.True(Workspace.IsUnsafeWriteRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.True(Workspace.IsUnsafeWriteRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(Workspace.IsForbiddenWriteLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "x.bat")));
        Assert.False(Workspace.IsUnsafeWriteRoot(Path.Combine(Path.GetTempPath(), "proj")));
        Assert.Throws<ToolException>(() => Workspace.WriteRoots([@"C:\"]));
    }

    [Fact]
    public void UriToLocalPath()
    {
        Assert.Equal(@"C:\proj\x", Workspace.UriToLocalPath("file:///C:/proj/x"));
        Assert.Equal(@"C:\Мои проекты", Workspace.UriToLocalPath("file:///C:/%D0%9C%D0%BE%D0%B8%20%D0%BF%D1%80%D0%BE%D0%B5%D0%BA%D1%82%D1%8B"));
        Assert.Null(Workspace.UriToLocalPath("file://server/share/x"));
        Assert.Null(Workspace.UriToLocalPath("https://example.com"));
    }
}

public class GlobTests
{
    [Theory]
    [InlineData("**/*.cs", "a.cs", true)]
    [InlineData("**/*.cs", "src/deep/a.cs", true)]
    [InlineData("*.cs", "src/a.cs", false)]
    [InlineData("src/*.{cs,xaml}", "src/A.XAML", true)]
    [InlineData("src/?.cs", "src/ab.cs", false)]
    [InlineData("**", "x/y/z", true)]
    [InlineData("a[1].cs", "a[1].cs", true)]
    public void ToRegex(string pattern, string path, bool match) => Assert.Equal(match, Glob.ToRegex(pattern).IsMatch(path));

    [Fact]
    public void Split()
    {
        Assert.Equal(("src", "**/*.cs"), Glob.Split("src/**/*.cs"));
        Assert.Equal(("", "*.md"), Glob.Split("*.md"));
        Assert.Equal(("C:/", "*.cs"), Glob.Split(@"C:\*.cs"));
    }

    [Theory]
    [InlineData(".env.*", ".env.production", true)]
    [InlineData("*.pem", "KEY.PEM", true)]
    [InlineData("id_rsa*", "id_rsa", true)]
    [InlineData("*.key", "keyboard.cs", false)]
    public void MatchName(string pattern, string name, bool match) => Assert.Equal(match, Glob.MatchName(name, pattern));
}

public class VerifyCommandTests
{
    private static readonly List<string> Allow = new McpSettings().VerifyCommandAllowlist;

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet test --filter FooTests")]
    [InlineData("dotnet test --filter \"FullyQualifiedName~Foo\"")]
    [InlineData("npm run test:unit")]
    [InlineData("pytest tests/test_x.py -k fast")]
    [InlineData("cargo test")]
    [InlineData("make")]
    [InlineData("mvn test -Dtest=FooTest")]
    [InlineData("dcc32 -Isrc Project.dpr")]
    public void Allowed(string cmd) => Assert.Equal(string.Join(' ', cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries)), VerifyCommand.Validate(cmd, Allow));

    [Theory]
    [InlineData("dotnet build & calc")]
    [InlineData("dotnet build && calc")]
    [InlineData("dotnet build | findstr x")]
    [InlineData("dotnet build; calc")]
    [InlineData("dotnet build\ncalc")]
    [InlineData("dotnet build\r\ncalc")]
    [InlineData("dotnet build > out.txt")]
    [InlineData("dotnet build ^& calc")]
    [InlineData("dotnet build %COMSPEC%")]
    [InlineData("dotnet build !x!")]
    [InlineData("dotnet build $(calc)")]
    [InlineData("dotnet build `calc`")]
    [InlineData("dotnet build (calc)")]
    [InlineData("dotnet build \uFF06 calc")]
    [InlineData("dotnet build -p:PreBuildEvent=calc")]
    [InlineData("dotnet build /p:PreBuildEvent=calc")]
    [InlineData("dotnet build -property:X=1")]
    [InlineData("dotnet build @evil.rsp")]
    [InlineData("dotnet test --test-adapter-path x")]
    [InlineData("dotnet build C:\\evil\\evil.csproj")]
    [InlineData("dotnet build ..\\other\\x.csproj")]
    [InlineData("dotnet build --output=C:\\x")]
    [InlineData("dotnet buildx")]
    [InlineData("npx jest-evil-package")]
    [InlineData("npm test --script-shell calc")]
    [InlineData("go test -exec calc")]
    [InlineData("cargo build --config build.rustc-wrapper=calc")]
    [InlineData("mvn test -Djvm=calc.exe")]
    [InlineData("make CC=calc")]
    [InlineData("make test --eval=x")]
    [InlineData("C:\\tools\\dotnet build")]
    [InlineData("calc")]
    [InlineData("powershell -c calc")]
    [InlineData("dotnet build \"unbalanced")]
    [InlineData("")]
    public void Rejected(string cmd) => Assert.Throws<ToolException>(() => VerifyCommand.Validate(cmd, Allow));

    [Theory]
    [InlineData("dotnet build", "dotnet build*", true)]
    [InlineData("dotnet build-server shutdown", "dotnet build*", false)]
    [InlineData("msbuild", "msbuild *", false)]
    [InlineData("msbuild a.sln", "msbuild *", true)]
    [InlineData("make", "make", true)]
    [InlineData("make install", "make", false)]
    public void Patterns(string cmd, string pattern, bool match) => Assert.Equal(match, VerifyCommand.MatchesPattern(cmd, pattern));

    // Проверки форматирования разрешены только в режиме «без записи».
    [Theory]
    [InlineData("dotnet format --verify-no-changes", true)]
    [InlineData("dotnet format --verify-no-changes Offload.slnx", true)]
    [InlineData("dotnet format", false)]
    [InlineData("dotnet format Offload.slnx", false)]
    [InlineData("cargo fmt --check", true)]
    [InlineData("cargo fmt", false)]
    [InlineData("npx prettier --check .", true)]
    [InlineData("npx prettier --write .", false)]
    [InlineData("npx eslint .", true)]
    [InlineData("npx eslint-evil", false)]
    [InlineData("npx eslint --fix .", false)]
    [InlineData("npx eslint -o report.json .", false)]
    [InlineData("npx eslint --output-file report.json .", false)]
    [InlineData("npx eslint --rulesdir rules .", false)]
    [InlineData("npx prettier --check --write .", false)]
    [InlineData("npx prettier --check -w .", false)]
    [InlineData("ruff check --fix .", false)]
    [InlineData("cargo clippy --fix", false)]
    public void FormatAndLintDefaults(string cmd, bool allowed)
    {
        if (allowed) Assert.Equal(cmd, VerifyCommand.Validate(cmd, Allow));
        else Assert.Throws<ToolException>(() => VerifyCommand.Validate(cmd, Allow));
    }

    [Fact]
    public async Task Run_CapturesExitCodeAndOutput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-verify-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "hay.txt"), "one\nneedle here\nthree\n");
            List<string> allow = ["findstr *"];
            var ok = await VerifyCommand.RunAsync(VerifyCommand.Validate("findstr /c:\"needle\" hay.txt", allow), dir, TimeSpan.FromSeconds(30),
                new ProgressReporter(null, null), CancellationToken.None);
            Assert.True(ok.Passed);
            Assert.Contains(ok.Tail, l => l.Contains("needle here"));
            var fail = await VerifyCommand.RunAsync(VerifyCommand.Validate("findstr /c:\"absent\" hay.txt", allow), dir, TimeSpan.FromSeconds(30),
                new ProgressReporter(null, null), CancellationToken.None);
            Assert.False(fail.Passed);
            Assert.Equal(1, fail.ExitCode);
            Assert.Contains("exit 1", fail.Summary(1));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Run_TimeoutKillsProcessTree()
    {
        var dir = Path.GetTempPath();
        List<string> allow = ["ping *"];
        var sw = Stopwatch.StartNew();
        var res = await VerifyCommand.RunAsync(VerifyCommand.Validate("ping -n 30 127.0.0.1", allow), dir, TimeSpan.FromSeconds(2),
            new ProgressReporter(null, null), CancellationToken.None);
        Assert.True(res.TimedOut);
        Assert.False(res.Passed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        Assert.Contains("TIMED OUT", res.Summary(1));
    }
}
