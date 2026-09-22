using System.Net;
using System.Net.Sockets;
using Offload.Core.Config;

namespace Offload.Llama.Tests;

/// <summary>
/// Интеграционные тесты с настоящим llama-server:
/// OFFLOAD_TEST_LLAMA_BIN — папка распакованной сборки (например, win-cpu-x64),
/// OFFLOAD_TEST_MODEL — небольшой GGUF (например, SmolLM2-135M-Q4_K_M.gguf).
/// Без них тесты пропускаются.
/// </summary>
internal static class TestEnv
{
    public static string? LlamaBin
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("OFFLOAD_TEST_LLAMA_BIN");
            return !string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, "llama-server.exe")) ? d : null;
        }
    }

    public static string? Model
    {
        get
        {
            var m = Environment.GetEnvironmentVariable("OFFLOAD_TEST_MODEL");
            return !string.IsNullOrWhiteSpace(m) && File.Exists(m) ? m : null;
        }
    }

    public static void RequireLlama()
    {
        Assert.SkipWhen(LlamaBin is null, "Не задана OFFLOAD_TEST_LLAMA_BIN (папка с llama-server.exe)");
    }

    public static void RequireLlamaAndModel()
    {
        RequireLlama();
        Assert.SkipWhen(Model is null, "Не задана OFFLOAD_TEST_MODEL (путь к небольшому GGUF)");
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Конфиг текущего TempHome: установленная сборка, модель, свободный порт.</summary>
    public static AppConfig Configure(string binDir, string modelPath, int? port = null)
    {
        var cfg = ConfigStore.Current;
        cfg.Llama.InstallDir = binDir;
        cfg.Llama.InstalledTag = "b11102";
        cfg.Llama.InstalledBackend = LlamaBackend.Cpu;
        cfg.Server.Port = port ?? FreePort();
        cfg.Server.ContextSize = 2048;
        cfg.Server.CacheType = "f16";
        cfg.Models.Installed.Clear();
        cfg.Models.Installed.Add(new InstalledModel
        {
            Id = "test-model",
            DisplayName = "Test model",
            FilePath = modelPath,
            NativeContext = 8192,
            RecommendedContext = 2048,
            Sampling = new SamplingSettings { Temperature = 0.2, TopP = 0.9, TopK = 40, MinP = 0.05, RepeatPenalty = 1.0 },
        });
        cfg.Models.ActiveModelId = "test-model";
        ConfigStore.Save(cfg);
        return cfg;
    }

    public static bool ProcessAlive(int? pid)
    {
        if (pid is null) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid.Value);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
