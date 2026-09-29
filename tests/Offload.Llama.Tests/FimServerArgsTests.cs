using Offload.Core.Config;

namespace Offload.Llama.Tests;

/// <summary>Аргументы сервера автодополнения (роль fim) и его адрес для IDE.</summary>
public sealed class FimServerArgsTests
{
    private static (AppConfig Cfg, InstalledModel Model) Setup(bool cpuOnly = false)
    {
        var cfg = new AppConfig();
        cfg.Server.ApiKey = "secret-key";
        cfg.Server.ExtraArgs = "--n-cpu-moe 10";
        cfg.Server.CacheType = "q8_0";
        cfg.Server.IdleUnloadMinutes = 15;
        cfg.Autocomplete.CpuOnly = cpuOnly;
        var model = new InstalledModel
        {
            Id = "qwen2.5-coder-1.5b-fim-q8", FilePath = @"C:\models\coder.gguf", Kind = ModelKind.Fim,
            NativeContext = 32768, RecommendedContext = 8192,
        };
        return (cfg, model);
    }

    private static string? ValueOf(IReadOnlyList<string> a, string flag)
    {
        var i = a.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
    }

    [Fact]
    public void Fim_LowLatencyArgs_NoSleep_NoChatFlags_NoKeyInArgs()
    {
        var (cfg, model) = Setup();
        var plan = AuxServerArgs.Build(cfg, ModelRole.Fim, model, @"C:\llama\llama-server.exe", 8012);
        var a = plan.Arguments;

        Assert.Equal("8012", ValueOf(a, "--port"));
        Assert.Equal("127.0.0.1", ValueOf(a, "--host"));
        Assert.Equal("offload-fim", ValueOf(a, "--alias"));
        Assert.Equal("8192", ValueOf(a, "-c"));
        Assert.Equal("1", ValueOf(a, "-np"));
        Assert.Equal("1024", ValueOf(a, "-b"));
        Assert.Equal("1024", ValueOf(a, "-ub"));
        Assert.Equal("256", ValueOf(a, "--cache-reuse"));
        // Автодополнение не выгружается по простою, -ngl не задан (решает --fit), чат-флагов и доп. аргументов нет.
        Assert.DoesNotContain("--sleep-idle-seconds", a);
        Assert.DoesNotContain("-ngl", a);
        Assert.DoesNotContain("--jinja", a);
        Assert.DoesNotContain("-ctk", a);
        Assert.DoesNotContain("--n-cpu-moe", a);
        Assert.DoesNotContain(a, x => x.Contains("secret-key", StringComparison.Ordinal));
        Assert.Equal((8192, 1), (plan.ContextSize, plan.Parallel));
    }

    [Fact]
    public void Fim_CpuOnly_ZeroGpuLayers()
    {
        var (cfg, model) = Setup(cpuOnly: true);
        var a = AuxServerArgs.Build(cfg, ModelRole.Fim, model, @"C:\llama\llama-server.exe", 8012).Arguments;
        Assert.Equal("0", ValueOf(a, "-ngl"));
    }

    [Fact]
    public void Fim_ContextCappedByNativeContext()
    {
        var (cfg, model) = Setup();
        model.NativeContext = 4096;
        Assert.Equal("4096", ValueOf(AuxServerArgs.Build(cfg, ModelRole.Fim, model, @"C:\l\llama-server.exe", 1).Arguments, "-c"));
    }

    [Fact]
    public void Fim_OtherRolesKeepIdleSleep()
    {
        var (cfg, model) = Setup();
        model.Kind = ModelKind.Chat;
        Assert.Contains("--sleep-idle-seconds", AuxServerArgs.Build(cfg, ModelRole.Fast, model, @"C:\l\llama-server.exe", 1).Arguments);
    }

    [Fact]
    public void ClientBaseUrl_FimPort_WildcardHostMappedToLoopback()
    {
        var cfg = new AppConfig();
        Assert.Equal("http://127.0.0.1:8012", AuxServerArgs.ClientBaseUrl(cfg, ModelRole.Fim));
        cfg.Server.AuxPorts["fim"] = 8020;
        cfg.Server.Host = "0.0.0.0";
        Assert.Equal("http://127.0.0.1:8020", AuxServerArgs.ClientBaseUrl(cfg, ModelRole.Fim));
        Assert.EndsWith("llama-server-fim.log", AuxServerArgs.LogFilePath(ModelRole.Fim), StringComparison.Ordinal);
    }
}
