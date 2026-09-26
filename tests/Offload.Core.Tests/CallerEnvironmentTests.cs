using System.Collections;
using Offload.Core.Processes;

namespace Offload.Core.Tests;

/// <summary>
/// Снимок окружения IDE для фоновых задач в трее: только белый список (PATH, venv, JAVA_HOME, VsDevCmd…, npm_config_*),
/// без секретов; снимок действует только в своём потоке выполнения и накладывается под принудительные переменные.
/// </summary>
public sealed class CallerEnvironmentTests
{
    [Fact]
    public void Capture_KeepsAllowlist_DropsSecretsAndUnknown()
    {
        var source = new Hashtable
        {
            ["Path"] = @"C:\nvm\v20;C:\Windows\system32",
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["PYTHONPATH"] = @"C:\p\src",
            ["LIBPATH"] = @"C:\VS\lib",
            ["VIRTUAL_ENV"] = @"C:\p\.venv",
            ["JAVA_HOME"] = @"C:\jdk-21",
            ["WindowsSdkDir"] = @"C:\Kits\10\",
            ["CLAUDE_PROJECT_DIR"] = @"C:\p",
            ["npm_config_cache"] = @"C:\npm-cache",
            // Похожие на секреты — отбрасываются, даже если это npm_config_*.
            ["npm_config__authToken"] = "npm_abc",
            ["npm_config_password"] = "hunter2",
            ["npm_config_pat"] = "x",
            ["npm_config_keyfile"] = @"C:\k.pem",
            ["npm_config_registry"] = "https://user:pass@registry.example.com/",
            // Не из белого списка.
            ["GITHUB_TOKEN"] = "ghp_x",
            ["OPENAI_API_KEY"] = "sk-x",
            ["USERPROFILE"] = @"C:\Users\u",
            ["HF_TOKEN"] = "hf_x",
        };

        var env = CallerEnvironment.Capture(source);

        Assert.Equal(
            ["CLAUDE_PROJECT_DIR", "JAVA_HOME", "LIBPATH", "npm_config_cache", "Path", "PATHEXT", "PYTHONPATH", "VIRTUAL_ENV", "WindowsSdkDir"],
            env.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(@"C:\nvm\v20;C:\Windows\system32", env["PATH"]);
    }

    [Theory]
    [InlineData("PATH", false)]
    [InlineData("PYTHONPATH", false)]
    [InlineData("NODE_PATH", false)]
    [InlineData("GITHUB_PAT", true)]
    [InlineData("AZURE_DEVOPS_EXT_PAT", true)]
    [InlineData("MY_API_KEY", true)]
    [InlineData("npm_config__auth", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("CLIENT_SECRET", true)]
    public void LooksSecret_PatOnlyAsWord(string name, bool secret) =>
        Assert.Equal(secret, CallerEnvironment.LooksSecret(name));

    [Fact]
    public void Sanitize_AppliesLimits_AndRejectsNul()
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = @"C:\a",
            ["JAVA_HOME"] = "C:\\j\0dk",
            ["GOPATH"] = new string('x', CallerEnvironment.MaxValueChars + 1),
            ["bad=name"] = "x",
        };
        for (var i = 0; i < CallerEnvironment.MaxEntries + 20; i++) env["npm_config_opt" + i] = "1";

        var clean = CallerEnvironment.Sanitize(env);

        Assert.Equal(@"C:\a", clean["PATH"]);
        Assert.False(clean.ContainsKey("JAVA_HOME"));
        Assert.False(clean.ContainsKey("GOPATH"));
        Assert.False(clean.ContainsKey("bad=name"));
        Assert.Equal(CallerEnvironment.MaxEntries, clean.Count);
        Assert.Empty(CallerEnvironment.Sanitize(null));
    }

    [Fact]
    public async Task Use_IsScopedToExecutionFlow_ApplyToOverridesInherited()
    {
        Assert.Null(CallerEnvironment.Current);
        var target = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "tray", ["CI"] = "1" };
        CallerEnvironment.ApplyTo(target);
        Assert.Equal("tray", target["PATH"]);

        Task other;
        using (CallerEnvironment.Use(new Dictionary<string, string> { ["PATH"] = "ide" }))
        {
            CallerEnvironment.ApplyTo(target);
            Assert.Equal("ide", target["PATH"]);
            Assert.Equal("1", target["CI"]);
            await Task.Yield();
            Assert.Equal("ide", CallerEnvironment.Current!["PATH"]);
            // Работа, начатая внутри области (задача в трее), наследует снимок.
            other = Task.Run(() => Assert.Equal("ide", CallerEnvironment.Current?["PATH"]), TestContext.Current.CancellationToken);
        }
        await other;
        // После области (и в работе, начатой вне её) снимка нет.
        Assert.Null(CallerEnvironment.Current);
        await Task.Run(() => Assert.Null(CallerEnvironment.Current), TestContext.Current.CancellationToken);
    }
}
