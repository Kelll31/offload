using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>
/// Внутренние команды local_dependency_check (фиксированный белый список, инъекции через имя манифеста), npx только с локальным
/// бинарником, защита файлов сборки (Mcp.ProtectBuildFiles + allow_build_files).
/// </summary>
[Collection("AppPaths")]
public class BuildAndCommandGuardTests
{
    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n";

    // ───────────── ValidateInternal ─────────────

    [Theory]
    [InlineData("dotnet list Offload.slnx package --outdated --format json")]
    [InlineData("dotnet list src/App/App.csproj package --vulnerable --include-transitive --format json")]
    [InlineData("dotnet list \"My App.sln\" package --outdated --format json")]
    [InlineData("npm audit --json")]
    [InlineData("npm outdated --json")]
    [InlineData("pip list --outdated --format=json")]
    public void ValidateInternal_FixedCommands_Accepted(string cmd)
    {
        Assert.Equal(cmd, VerifyCommand.ValidateInternal(cmd));
    }

    [Theory]
    [InlineData("dotnet list -p:BuildProjectReferences=x.csproj package --outdated --format json")]
    [InlineData("dotnet list @evil.csproj package --outdated --format json")]
    [InlineData("dotnet list a&calc.csproj package --outdated --format json")]
    [InlineData("dotnet list a%PATH%.csproj package --outdated --format json")]
    [InlineData("dotnet list ../other/x.csproj package --outdated --format json")]
    [InlineData("dotnet list C:/x/x.csproj package --outdated --format json")]
    [InlineData("dotnet list -restore.csproj package --outdated --format json")]
    [InlineData("dotnet list evil.targets package --outdated --format json")]
    [InlineData("dotnet list x.csproj package --outdated --format json --source http://evil")]
    [InlineData("dotnet list x.csproj package --outdated --format json --configfile evil.config")]
    [InlineData("dotnet build x.csproj")]
    [InlineData("npm audit --json --registry=http://evil")]
    [InlineData("npm run evil")]
    [InlineData("pip install evil")]
    [InlineData("dotnet list \"a b\"c.csproj package --outdated --format json")]
    public void ValidateInternal_InjectionAttempts_Rejected(string cmd)
    {
        Assert.Throws<ToolException>(() => VerifyCommand.ValidateInternal(cmd));
    }

    [Theory]
    [InlineData("evil&calc.csproj")]
    [InlineData("@evil.csproj")]
    [InlineData("-restore.csproj")]
    [InlineData("a;b.csproj")]
    public async Task DependencyCheck_CraftedManifestName_IsNotExecuted(string manifest)
    {
        using var env = new TestEnv();
        env.WriteFile(manifest, Csproj);

        var r = await DependencyCheckTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), "outdated", null, null, 0);

        Assert.Contains("`dotnet list` skipped", r);
        Assert.False(Directory.Exists(env.PathOf(".offload/runs")), "команда не должна запускаться (нет лога прогона)");
    }

    // ───────────── npx ─────────────

    [Fact]
    public void ResolveNpx_NoLocalPackage_RefusesWithInstallHint()
    {
        using var env = new TestEnv();

        var ex = Assert.Throws<ToolException>(() => VerifyCommand.ResolveNpx("npx tsc --noEmit", env.Workspace));

        Assert.Contains("not installed", ex.Message);
        Assert.Contains("npm install --save-dev typescript", ex.Message);
    }

    [Fact]
    public void ResolveNpx_LocalBinary_RewritesToNodeModulesBin()
    {
        using var env = new TestEnv();
        env.WriteFile("node_modules/.bin/eslint.cmd", "@echo off\r\n");

        Assert.Equal(@".\node_modules\.bin\eslint.cmd src --max-warnings 0", VerifyCommand.ResolveNpx("npx eslint src --max-warnings 0", env.Workspace));
        Assert.Equal("npm test", VerifyCommand.ResolveNpx("npm test", env.Workspace));
    }

    [Theory]
    [InlineData("npx eslint@8 .")]
    [InlineData("npx -y eslint .")]
    [InlineData("npx --yes jest")]
    [InlineData("npx")]
    public void ResolveNpx_PackageSpecsAndFlags_Refused(string cmd)
    {
        using var env = new TestEnv();
        env.WriteFile("node_modules/.bin/eslint.cmd", "@echo off\r\n");

        Assert.Throws<ToolException>(() => VerifyCommand.ResolveNpx(cmd, env.Workspace));
    }

    [Fact]
    public async Task RunAsync_Npx_RunsLocalBinaryNotRegistry()
    {
        using var env = new TestEnv();
        env.WriteFile("node_modules/.bin/eslint.cmd", "@echo fake-eslint %*\r\n");
        var cmd = VerifyCommand.Validate("npx eslint .", env.Context().Cfg.Mcp.VerifyCommandAllowlist);

        var r = await VerifyCommand.RunAsync(cmd, env.Workspace, TimeSpan.FromSeconds(60), new ProgressReporter(null, null), TestContext.Current.CancellationToken);

        Assert.True(r.Passed, r.Summary(1));
        Assert.Contains(r.Tail, l => l.Contains("fake-eslint .", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_NpxWithoutPackage_Throws()
    {
        using var env = new TestEnv();

        await Assert.ThrowsAsync<ToolException>(() =>
            VerifyCommand.RunAsync("npx vitest run", env.Workspace, TimeSpan.FromSeconds(30), new ProgressReporter(null, null), TestContext.Current.CancellationToken));
    }

    // ───────────── файлы сборки ─────────────

    [Theory]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.rsp")]
    [InlineData("Directory.Packages.props")]
    [InlineData("src/App/App.csproj")]
    [InlineData("build/common.targets")]
    [InlineData("package.json")]
    [InlineData("Makefile")]
    [InlineData("tests/conftest.py")]
    [InlineData("setup.py")]
    [InlineData("build.rs")]
    [InlineData("app/build.gradle")]
    [InlineData("settings.gradle.kts")]
    [InlineData("jest.config.js")]
    [InlineData("vite.config.ts")]
    public void IsBuildFile_BuildScripts(string path)
    {
        Assert.True(PathGuard.IsBuildFile(path), path);
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("README.md")]
    [InlineData("docs/build.md")]
    [InlineData("src/config.ts")]
    [InlineData("package-lock.json.bak")]
    [InlineData("tests/test_api.py")]
    public void IsBuildFile_OrdinaryFiles(string path)
    {
        Assert.False(PathGuard.IsBuildFile(path), path);
    }

    private static Task<string> ResolveUnderPolicy(TestEnv env, bool allow, string rel) =>
        PathGuard.WithBuildFilePolicy(allow, c => Task.FromResult(c.ResolveWrite(rel).Canonical))(env.Context());

    [Fact]
    public async Task ProtectBuildFiles_On_RefusesWithoutAllowFlag()
    {
        using var env = new TestEnv(configure: c => c.Mcp.ProtectBuildFiles = true);

        var ex = await Assert.ThrowsAsync<ToolException>(() => ResolveUnderPolicy(env, false, "Directory.Build.targets"));
        Assert.Contains("allow_build_files=true", ex.Message);
        await Assert.ThrowsAsync<ToolException>(() => ResolveUnderPolicy(env, false, "src/App/App.csproj"));

        // Явное разрешение и обычные файлы — можно.
        Assert.EndsWith("Directory.Build.targets", await ResolveUnderPolicy(env, true, "Directory.Build.targets"));
        Assert.EndsWith("Program.cs", await ResolveUnderPolicy(env, false, "src/Program.cs"));
    }

    [Fact]
    public async Task ProtectBuildFiles_Off_ByDefault()
    {
        using var env = new TestEnv();

        Assert.EndsWith("Directory.Build.targets", await ResolveUnderPolicy(env, false, "Directory.Build.targets"));
    }

    [Fact]
    public async Task ProtectBuildFiles_ApplyPatch_RefusedAtomically()
    {
        using var env = new TestEnv(configure: c => c.Mcp.ProtectBuildFiles = true);
        var patch = string.Join('\n',
            "--- /dev/null", "+++ b/src/ok.txt", "@@ -0,0 +1,1 @@", "+fine",
            "--- /dev/null", "+++ b/Directory.Build.targets", "@@ -0,0 +1,1 @@", "+<Project><Target Name=\"X\" BeforeTargets=\"Build\" /></Project>", "");
        var run = PathGuard.WithBuildFilePolicy(false, c => ApplyPatchTool.RunAsync(c, patch, null, false, true, 0));

        await Assert.ThrowsAsync<ToolException>(() => run(env.Context(ct: TestContext.Current.CancellationToken)));

        Assert.False(File.Exists(env.PathOf("Directory.Build.targets")), "файл сборки не должен быть создан");
        Assert.False(File.Exists(env.PathOf("src/ok.txt")), "патч применяется целиком или никак");

        var allowed = PathGuard.WithBuildFilePolicy(true, c => ApplyPatchTool.RunAsync(c, patch, null, false, true, 0));
        await allowed(env.Context(ct: TestContext.Current.CancellationToken));
        Assert.True(File.Exists(env.PathOf("Directory.Build.targets")));
    }
}
