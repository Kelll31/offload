using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Llama.Tests;

public sealed class ServerArgsTests
{
    private const string Exe = @"C:\pc\llama.cpp\b11102-cuda12\llama-server.exe";

    private static AppConfig Cfg(Action<ServerSettings>? edit = null)
    {
        var cfg = new AppConfig();
        cfg.Server.ApiKey = "pc-secret";
        cfg.Server.Port = 18080;
        edit?.Invoke(cfg.Server);
        return cfg;
    }

    private static InstalledModel Model(Action<InstalledModel>? edit = null)
    {
        var m = new InstalledModel
        {
            Id = "qwen3-coder-30b",
            DisplayName = "Qwen3-Coder 30B",
            FilePath = @"C:\pc\models\Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf",
            NativeContext = 262144,
            RecommendedContext = 65536,
            IsMoe = true,
            Sampling = new SamplingSettings { Temperature = 0.7, TopP = 0.8, TopK = 20, MinP = 0, RepeatPenalty = 1.05 },
        };
        edit?.Invoke(m);
        return m;
    }

    private static string? ValueOf(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void Defaults_ExplicitCoreFlags()
    {
        var plan = LlamaServerArgs.Build(Cfg(), Model(), Exe);
        var a = plan.Arguments;
        Assert.Equal(Exe, plan.ExePath);
        Assert.Equal(@"C:\pc\models\Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf", ValueOf(a, "-m"));
        Assert.Equal("127.0.0.1", ValueOf(a, "--host"));
        Assert.Equal("18080", ValueOf(a, "--port"));
        // Ключ — в окружении процесса, не в командной строке (её видит любой процесс пользователя).
        Assert.DoesNotContain("--api-key", a);
        Assert.DoesNotContain("--api-key-file", a);
        Assert.DoesNotContain(a, x => x.Contains("pc-secret", StringComparison.Ordinal));
        Assert.Equal(LlamaServerArgs.DefaultAlias, ValueOf(a, "--alias"));
        Assert.Equal("65536", ValueOf(a, "-c"));
        Assert.Equal("1", ValueOf(a, "-np"));
        Assert.Equal("auto", ValueOf(a, "-fa"));
        Assert.Equal("q8_0", ValueOf(a, "-ctk"));
        Assert.Equal("q8_0", ValueOf(a, "-ctv"));
        Assert.Equal("off", ValueOf(a, "--log-colors"));
        Assert.Contains("--jinja", a);
        Assert.DoesNotContain("-kvu", a);
        // -ngl не передаётся (GpuLayers = -1): слои размещает --fit.
        Assert.DoesNotContain("-ngl", a);
        // CpuMoeLayers = -1 — решает --fit.
        Assert.DoesNotContain("--n-cpu-moe", a);
        Assert.Equal(-1, plan.CpuMoeLayers);
        Assert.DoesNotContain("-t", a);
        Assert.DoesNotContain("--sleep-idle-seconds", a);
        // Удалённые флаги — никогда.
        Assert.DoesNotContain("--no-mmap", a);
        Assert.DoesNotContain("--mlock", a);
        Assert.Equal(65536, plan.ContextSize);
        Assert.Equal(1, plan.Parallel);
        Assert.Equal(LlamaServerArgs.DefaultAlias, plan.ModelAlias);
    }

    [Fact]
    public void FlashAttention_AlwaysHasValue()
    {
        foreach (var (input, expected) in new[] { ("on", "on"), ("OFF", "off"), ("", "auto"), ("garbage", "auto"), ("true", "on") })
        {
            var a = LlamaServerArgs.Build(Cfg(s => s.FlashAttention = input), Model(), Exe).Arguments;
            var i = a.ToList().IndexOf("-fa");
            Assert.True(i >= 0 && i + 1 < a.Count);
            Assert.Equal(expected, a[i + 1]);
            Assert.Single(a, x => x == "-fa");
        }
    }

    [Fact]
    public void FlashAttentionOff_QuantizedVCacheSkipped()
    {
        var a = LlamaServerArgs.Build(Cfg(s => { s.FlashAttention = "off"; s.CacheType = "q8_0"; }), Model(), Exe).Arguments;
        Assert.Equal("q8_0", ValueOf(a, "-ctk"));
        Assert.DoesNotContain("-ctv", a);

        var f16 = LlamaServerArgs.Build(Cfg(s => { s.FlashAttention = "off"; s.CacheType = "f16"; }), Model(), Exe).Arguments;
        Assert.Equal("f16", ValueOf(f16, "-ctv"));

        var bad = LlamaServerArgs.Build(Cfg(s => s.CacheType = "q3_x"), Model(), Exe).Arguments;
        Assert.DoesNotContain("-ctk", bad);
    }

    [Fact]
    public void Parallel_AddsKvUnifiedAndScalesPool()
    {
        var plan = LlamaServerArgs.Build(Cfg(s => s.Parallel = 2), Model(), Exe);
        Assert.Contains("-kvu", plan.Arguments);
        Assert.Equal("2", ValueOf(plan.Arguments, "-np"));
        Assert.Equal("131072", ValueOf(plan.Arguments, "-c"));
        Assert.Equal(65536, plan.ContextSize);
        Assert.Equal(2, plan.Parallel);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(ServerSettings.MaxParallel, ServerSettings.MaxParallel)]
    [InlineData(ServerSettings.MaxParallel + 1, ServerSettings.MaxParallel)]
    [InlineData(64, ServerSettings.MaxParallel)]
    public void Parallel_ClampedToSharedLimit(int configured, int expected)
    {
        var plan = LlamaServerArgs.Build(Cfg(s => s.Parallel = configured), Model(), Exe);
        Assert.Equal(expected, plan.Parallel);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), ValueOf(plan.Arguments, "-np"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("--top-n-sigma 1.5", false)]
    [InlineData("--chat-template-kwargs '{\"enable_thinking\":false}'", false)]
    [InlineData("--x \"a \\\" b\"", false)]
    [InlineData("--x \"\"", false)]
    [InlineData("--x \"abc", true)]
    [InlineData("--x 'abc", true)]
    [InlineData("--x \"abc\\\"", true)]
    [InlineData("--x '\"' \"'\"", false)]
    public void HasUnclosedQuote_MatchesSplitRules(string line, bool expected)
    {
        Assert.Equal(expected, LlamaServerArgs.HasUnclosedQuote(line));
    }

    [Fact]
    public void GpuLayersAndMoe_WhenSet()
    {
        var plan = LlamaServerArgs.Build(Cfg(s => { s.GpuLayers = 30; s.CpuMoeLayers = 12; s.Threads = 8; s.IdleUnloadMinutes = 30; }), Model(), Exe);
        Assert.Equal("30", ValueOf(plan.Arguments, "-ngl"));
        Assert.Equal("12", ValueOf(plan.Arguments, "--n-cpu-moe"));
        Assert.Equal(12, plan.CpuMoeLayers);
        Assert.Equal("8", ValueOf(plan.Arguments, "-t"));
        Assert.Equal("1800", ValueOf(plan.Arguments, "--sleep-idle-seconds"));

        var zero = LlamaServerArgs.Build(Cfg(s => { s.GpuLayers = 0; s.CpuMoeLayers = 0; }), Model(), Exe);
        Assert.Equal("0", ValueOf(zero.Arguments, "-ngl"));
        Assert.DoesNotContain("--n-cpu-moe", zero.Arguments);
        Assert.Equal(0, zero.CpuMoeLayers);

        // Плотная каталожная модель — --n-cpu-moe не нужен.
        var dense = LlamaServerArgs.Build(Cfg(s => s.CpuMoeLayers = 12), Model(m => m.IsMoe = false), Exe);
        Assert.DoesNotContain("--n-cpu-moe", dense.Arguments);
    }

    [Fact]
    public void Context_ZeroUsesModel_ClampedToNative()
    {
        Assert.Equal("65536", ValueOf(LlamaServerArgs.Build(Cfg(s => s.ContextSize = 0), Model(), Exe).Arguments, "-c"));
        Assert.Equal("32768", ValueOf(LlamaServerArgs.Build(Cfg(), Model(m => m.RecommendedContext = 0), Exe).Arguments, "-c"));
        Assert.Equal("8192", ValueOf(LlamaServerArgs.Build(Cfg(s => s.ContextSize = 100000), Model(m => m.NativeContext = 8192), Exe).Arguments, "-c"));
        Assert.Equal("40000", ValueOf(LlamaServerArgs.Build(Cfg(s => s.ContextSize = 40000), Model(), Exe).Arguments, "-c"));
    }

    [Fact]
    public void Sampling_InvariantCulture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        try
        {
            var a = LlamaServerArgs.Build(Cfg(), Model(m => m.Sampling.PresencePenalty = 1.5), Exe).Arguments;
            Assert.Equal("0.7", ValueOf(a, "--temp"));
            Assert.Equal("0.8", ValueOf(a, "--top-p"));
            Assert.Equal("20", ValueOf(a, "--top-k"));
            Assert.Equal("0", ValueOf(a, "--min-p"));
            Assert.Equal("1.05", ValueOf(a, "--repeat-penalty"));
            Assert.Equal("1.5", ValueOf(a, "--presence-penalty"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
        Assert.DoesNotContain("--presence-penalty", LlamaServerArgs.Build(Cfg(), Model(), Exe).Arguments);
    }

    [Fact]
    public void Mtp_OnlyWithSingleSlotAndMtpModel()
    {
        var a = LlamaServerArgs.Build(Cfg(s => s.EnableMtp = true), Model(m => m.HasMtp = true), Exe).Arguments;
        Assert.Equal("draft-mtp", ValueOf(a, "--spec-type"));
        Assert.Equal("2", ValueOf(a, "--spec-draft-n-max"));

        Assert.DoesNotContain("--spec-type", LlamaServerArgs.Build(Cfg(s => { s.EnableMtp = true; s.Parallel = 2; }), Model(m => m.HasMtp = true), Exe).Arguments);
        Assert.DoesNotContain("--spec-type", LlamaServerArgs.Build(Cfg(s => s.EnableMtp = true), Model(), Exe).Arguments);
        Assert.DoesNotContain("--spec-type", LlamaServerArgs.Build(Cfg(), Model(m => m.HasMtp = true), Exe).Arguments);
    }

    [Fact]
    public void ExtraArgs_AppendedLast_Sanitized()
    {
        var plan = LlamaServerArgs.Build(
            Cfg(s => s.ExtraArgs = "--chat-template-kwargs '{\"enable_thinking\":false}' --no-mmap -ub 1024 --port 9999 --mlock -fa"),
            Model(), Exe);
        var a = plan.Arguments;
        Assert.Equal(["--chat-template-kwargs", "{\"enable_thinking\":false}", "-ub", "1024", "-fa", "on"], a.Skip(a.Count - 6).ToArray());
        Assert.DoesNotContain("--no-mmap", a);
        Assert.DoesNotContain("--mlock", a);
        Assert.DoesNotContain("9999", a);
        Assert.Single(a, x => x == "--port");
    }

    [Fact]
    public void SplitArgs_Quotes()
    {
        Assert.Empty(LlamaServerArgs.SplitArgs(""));
        Assert.Empty(LlamaServerArgs.SplitArgs("   \r\n "));
        Assert.Equal(["-ub", "1024", "--threads-http", "4"], LlamaServerArgs.SplitArgs("  -ub 1024\n --threads-http\t4 "));
        Assert.Equal(["--chat-template-file", @"C:\My Templates\qwen.jinja"], LlamaServerArgs.SplitArgs(@"--chat-template-file ""C:\My Templates\qwen.jinja"""));
        Assert.Equal(["--chat-template-kwargs", "{\"enable_thinking\":false}"], LlamaServerArgs.SplitArgs("--chat-template-kwargs '{\"enable_thinking\":false}'"));
        Assert.Equal(["--x", "say \"hi\""], LlamaServerArgs.SplitArgs("--x \"say \\\"hi\\\"\""));
        Assert.Equal(["--a", "", "--b"], LlamaServerArgs.SplitArgs("--a \"\" --b"));
        Assert.Equal(["--prefixed=some value"], LlamaServerArgs.SplitArgs("--prefixed=\"some value\""));
        Assert.Equal(["--open", "not closed"], LlamaServerArgs.SplitArgs("--open \"not closed"));
    }

    [Fact]
    public void Describe_HasNoApiKey()
    {
        var text = LlamaServerArgs.Describe(LlamaServerArgs.Build(Cfg(), Model(), Exe));
        Assert.DoesNotContain("pc-secret", text);
        Assert.DoesNotContain("--api-key", text);
    }

    [Fact]
    public void NoApiKey_NoKeyFileArgument()
    {
        var a = LlamaServerArgs.Build(Cfg(s => s.ApiKey = ""), Model(), Exe).Arguments;
        Assert.DoesNotContain("--api-key-file", a);
        Assert.DoesNotContain("--api-key", a);
    }

    [Fact]
    public void Environment_CarriesKey_AndRemovesInheritedWhenEmpty()
    {
        var env = LlamaServerArgs.BuildEnvironment(Cfg());
        Assert.Equal("pc-secret", env[LlamaServerArgs.ApiKeyEnvVar]);
        // Без ключа унаследованный LLAMA_API_KEY из окружения пользователя убирается.
        var none = LlamaServerArgs.BuildEnvironment(Cfg(s => s.ApiKey = "  "));
        Assert.True(none.ContainsKey(LlamaServerArgs.ApiKeyEnvVar));
        Assert.Null(none[LlamaServerArgs.ApiKeyEnvVar]);
    }

    [Fact]
    public void ExtraArgs_CannotOverrideApiKey()
    {
        var a = LlamaServerArgs.Build(Cfg(s => s.ExtraArgs = "--api-key other --api-key-file C:/keys/x.txt -ub 512"), Model(), Exe).Arguments;
        Assert.DoesNotContain("other", a);
        Assert.DoesNotContain("C:/keys/x.txt", a);
        Assert.DoesNotContain("--api-key-file", a);
        Assert.DoesNotContain("--api-key", a);
        Assert.Equal("512", ValueOf(a, "-ub"));
    }

    [Fact]
    public void AutoPlacement_ExplicitContextAndCpuMoe()
    {
        var placement = new ServerPlacement(16384, 12, "оценка");
        var plan = LlamaServerArgs.Build(Cfg(), Model(), Exe, placement);
        Assert.Equal("16384", ValueOf(plan.Arguments, "-c"));
        Assert.Equal("12", ValueOf(plan.Arguments, "--n-cpu-moe"));
        Assert.Equal(16384, plan.ContextSize);
        Assert.Equal(12, plan.CpuMoeLayers);
        Assert.Same(placement, plan.Placement);

        // -c — общий пул: контекст слота × слоты.
        var two = LlamaServerArgs.Build(Cfg(s => s.Parallel = 2), Model(), Exe, placement);
        Assert.Equal("32768", ValueOf(two.Arguments, "-c"));
        Assert.Equal(16384, two.ContextSize);

        // Все эксперты в видеопамяти — флаг не нужен, но это уже решение «Авто», а не --fit.
        var full = LlamaServerArgs.Build(Cfg(), Model(), Exe, new ServerPlacement(65536, 0, "оценка"));
        Assert.DoesNotContain("--n-cpu-moe", full.Arguments);
        Assert.Equal(0, full.CpuMoeLayers);

        // Контекст не больше родного; у плотной модели выгрузка экспертов не передаётся.
        Assert.Equal("8192", ValueOf(LlamaServerArgs.Build(Cfg(), Model(m => m.NativeContext = 8192), Exe, placement).Arguments, "-c"));
        Assert.DoesNotContain("--n-cpu-moe", LlamaServerArgs.Build(Cfg(), Model(m => m.IsMoe = false), Exe, placement).Arguments);
    }

    [Fact]
    public void AutoPlacement_IgnoredWhenUserSetValues()
    {
        var placement = new ServerPlacement(16384, 12, "оценка");
        var ctx = LlamaServerArgs.Build(Cfg(s => s.ContextSize = 40000), Model(), Exe, placement);
        Assert.Equal("40000", ValueOf(ctx.Arguments, "-c"));
        Assert.DoesNotContain("--n-cpu-moe", ctx.Arguments);
        Assert.Null(ctx.Placement);

        var moeOff = LlamaServerArgs.Build(Cfg(s => s.CpuMoeLayers = 0), Model(), Exe, placement);
        Assert.Equal("65536", ValueOf(moeOff.Arguments, "-c"));
        Assert.DoesNotContain("--n-cpu-moe", moeOff.Arguments);

        var moeSet = LlamaServerArgs.Build(Cfg(s => s.CpuMoeLayers = 5), Model(), Exe, placement);
        Assert.Equal("5", ValueOf(moeSet.Arguments, "--n-cpu-moe"));
        Assert.Equal("65536", ValueOf(moeSet.Arguments, "-c"));

        // Без оценки — как раньше: рекомендованный контекст, размещение решает --fit.
        var none = LlamaServerArgs.Build(Cfg(), Model(), Exe);
        Assert.Equal("65536", ValueOf(none.Arguments, "-c"));
        Assert.Equal(-1, none.CpuMoeLayers);
        Assert.Null(none.Placement);
    }
}
