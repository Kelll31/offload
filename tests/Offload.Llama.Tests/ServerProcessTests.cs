using System.Net;
using System.Net.Sockets;
using Offload.Core.Config;

namespace Offload.Llama.Tests;

/// <summary>Жизненный цикл LlamaServerProcess с настоящим llama-server (см. TestEnv).</summary>
[Collection("AppPaths")]
public sealed class ServerProcessTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private static List<ServerState> Track(LlamaServerProcess p)
    {
        var list = new List<ServerState>();
        p.StateChanged += s =>
        {
            lock (list) list.Add(s);
        };
        return list;
    }

    [Fact]
    public async Task NotConfigured_WhenNoInstall()
    {
        using var p = new LlamaServerProcess();
        var ex = await Assert.ThrowsAsync<LlamaServerException>(() => p.StartAsync(ConfigStore.Current));
        Assert.Equal(ServerState.NotConfigured, p.State);
        Assert.Contains("llama.cpp не установлен", ex.Message);
        Assert.Equal(ex.Message, p.LastError);
    }

    [Fact]
    public async Task NotConfigured_WhenModelMissing()
    {
        TestEnv.RequireLlama();
        var cfg = TestEnv.Configure(TestEnv.LlamaBin!, Path.Combine(_home.Path, "missing.gguf"));
        using var p = new LlamaServerProcess();
        var ex = await Assert.ThrowsAsync<LlamaServerException>(() => p.StartAsync(cfg));
        Assert.Equal(ServerState.NotConfigured, p.State);
        Assert.Contains("Файл модели не найден", ex.Message);
    }

    [Fact]
    public async Task StartChatStop_FullCycle()
    {
        TestEnv.RequireLlamaAndModel();
        var cfg = TestEnv.Configure(TestEnv.LlamaBin!, TestEnv.Model!);
        using var p = new LlamaServerProcess();
        var states = Track(p);
        var lines = 0;
        p.OutputLine += _ => Interlocked.Increment(ref lines);

        await p.StartAsync(cfg, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Running, p.State);
        Assert.NotNull(p.StartedAtUtc);
        Assert.True(TestEnv.ProcessAlive(p.ProcessId));
        Assert.NotNull(p.CurrentPlan);
        Assert.True(lines > 0);

        // Повторный запуск при Running — ничего не делает.
        var pid = p.ProcessId;
        await p.StartAsync(cfg, ct: TestContext.Current.CancellationToken);
        Assert.Equal(pid, p.ProcessId);

        var client = LlamaClient.FromConfig(cfg);
        Assert.Equal(HealthState.Ready, await client.GetHealthAsync(TestContext.Current.CancellationToken));
        var props = await client.GetPropsAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(props);
        Assert.Equal(LlamaServerArgs.DefaultAlias, props.ModelAlias);
        Assert.Equal(1, props.TotalSlots);
        Assert.True(await client.CountTokensAsync("def add(a, b): return a + b", TestContext.Current.CancellationToken) > 3);
        var deltas = 0;
        var r = await client.ChatAsync(new ChatRequest([ChatMessage.User("Say hello")], MaxTokens: 16), _ => deltas++,
            TestContext.Current.CancellationToken);
        Assert.True(r.CompletionTokens > 0);
        Assert.True(r.PromptTokens > 0);
        Assert.True(deltas > 0);

        // Неверный ключ — 401 с русским текстом.
        var bad = new LlamaClient(cfg.Server.BaseUrl, "wrong");
        var ex = await Assert.ThrowsAsync<LlamaApiException>(() => bad.ChatAsync(new ChatRequest([ChatMessage.User("x")]), ct: TestContext.Current.CancellationToken));
        Assert.Equal(401, ex.StatusCode);

        await p.StopAsync();
        Assert.Equal(ServerState.Stopped, p.State);
        Assert.Null(p.ProcessId);
        Assert.False(TestEnv.ProcessAlive(pid));
        lock (states) Assert.Equal([ServerState.Starting, ServerState.Running, ServerState.Stopping, ServerState.Stopped], states);

        var log = await File.ReadAllTextAsync(LlamaServerProcess.LogFilePath, TestContext.Current.CancellationToken);
        Assert.Contains("--api-key ***", log);
        Assert.DoesNotContain(cfg.Server.ApiKey, log);
        Assert.Contains("завершился", log);

        // Перезапуск на том же порту (TIME_WAIT после остановки не должен сдвигать порт).
        var port = cfg.Server.Port;
        await p.StartAsync(cfg, ct: TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Running, p.State);
        Assert.Equal(port, ConfigStore.Current.Server.Port);
        await p.RestartAsync(cfg, TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Running, p.State);
        Assert.Equal(port, ConfigStore.Current.Server.Port);
        await p.StopAsync();
    }

    [Fact]
    public async Task BusyPort_PicksNextAndSavesConfig()
    {
        TestEnv.RequireLlamaAndModel();
        // Порт вне динамического диапазона: там Windows резервирует целые блоки (Hyper-V/WSL).
        var (busy, port) = BindLowPort();
        try
        {
            var cfg = TestEnv.Configure(TestEnv.LlamaBin!, TestEnv.Model!, port);
            using var p = new LlamaServerProcess();
            await p.StartAsync(cfg, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.Equal(ServerState.Running, p.State);
            Assert.NotEqual(port, ConfigStore.Current.Server.Port);
            // Слушатель на 0.0.0.0 — привязка к 127.0.0.1 прошла бы, но порт всё равно занят.
            Assert.Equal(port + 1, ConfigStore.Current.Server.Port);
            Assert.Equal(ConfigStore.Current.Server.Port, cfg.Server.Port);
            // Другие поля конфига не затёрты.
            Assert.Equal("test-model", ConfigStore.Reload().Models.ActiveModelId);
            Assert.Equal(HealthState.Ready, await LlamaClient.FromConfig(cfg).GetHealthAsync(TestContext.Current.CancellationToken));
            await p.StopAsync();
        }
        finally
        {
            busy.Stop();
        }
    }

    private static (TcpListener Listener, int Port) BindLowPort()
    {
        for (var port = 18100; port < 19000; port += 7)
        {
            if (!PortProbe.IsFree("127.0.0.1", port) || !PortProbe.IsFree("127.0.0.1", port + 1)) continue;
            var l = new TcpListener(IPAddress.Any, port);
            try
            {
                l.Start();
                return (l, port);
            }
            catch (SocketException)
            {
            }
        }
        throw new InvalidOperationException("нет свободного порта для теста");
    }

    [Fact]
    public async Task CorruptModel_FailedWithRussianError()
    {
        TestEnv.RequireLlama();
        var bad = Path.Combine(_home.Path, "broken.gguf");
        await File.WriteAllBytesAsync(bad, new byte[4096], TestContext.Current.CancellationToken);
        var cfg = TestEnv.Configure(TestEnv.LlamaBin!, bad);
        using var p = new LlamaServerProcess();
        var states = Track(p);
        var ex = await Assert.ThrowsAsync<LlamaServerException>(() => p.StartAsync(cfg, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.Equal(ServerState.Failed, p.State);
        Assert.Equal(ex.Message, p.LastError);
        Assert.Contains("llama-server не запустился", ex.Message);
        Assert.Contains("Не удалось загрузить модель", ex.Message);
        Assert.Null(p.ProcessId);
        lock (states) Assert.Equal([ServerState.Starting, ServerState.Failed], states);
    }

    [Fact]
    public async Task Timeout_KillsAndFails()
    {
        TestEnv.RequireLlamaAndModel();
        var cfg = TestEnv.Configure(TestEnv.LlamaBin!, TestEnv.Model!);
        using var p = new LlamaServerProcess();
        var ex = await Assert.ThrowsAsync<LlamaServerException>(() => p.StartAsync(cfg, TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken));
        Assert.Contains("не загрузил модель", ex.Message);
        Assert.Equal(ServerState.Failed, p.State);
        Assert.Null(p.ProcessId);
    }

    [Fact]
    public async Task CrashWhileRunning_FailedEvent()
    {
        TestEnv.RequireLlamaAndModel();
        var cfg = TestEnv.Configure(TestEnv.LlamaBin!, TestEnv.Model!);
        using var p = new LlamaServerProcess();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.StateChanged += s =>
        {
            if (s == ServerState.Failed) failed.TrySetResult();
        };
        await p.StartAsync(cfg, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        using (var proc = System.Diagnostics.Process.GetProcessById(p.ProcessId!.Value)) proc.Kill();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Failed, p.State);
        Assert.Contains("неожиданно завершился", p.LastError);
        Assert.Null(p.ProcessId);

        // После падения можно запустить снова.
        await p.StartAsync(cfg, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal(ServerState.Running, p.State);
        await p.StopAsync();
    }

    [Fact]
    public async Task CancelDuringStart_StopsProcess()
    {
        TestEnv.RequireLlamaAndModel();
        var cfg = TestEnv.Configure(TestEnv.LlamaBin!, TestEnv.Model!);
        using var p = new LlamaServerProcess();
        int? pid = null;
        p.StateChanged += s =>
        {
            if (s == ServerState.Starting) pid = p.ProcessId;
        };
        using var cts = new CancellationTokenSource();
        p.StateChanged += s =>
        {
            if (s == ServerState.Starting) cts.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.StartAsync(cfg, TimeSpan.FromMinutes(2), cts.Token));
        Assert.Equal(ServerState.Stopped, p.State);
        Assert.Null(p.ProcessId);
        Assert.False(TestEnv.ProcessAlive(pid));
    }

    [Fact]
    public async Task StopDuringStart_CancelsStart()
    {
        TestEnv.RequireLlamaAndModel();
        var cfg = TestEnv.Configure(TestEnv.LlamaBin!, TestEnv.Model!);
        using var p = new LlamaServerProcess();
        Task? stop = null;
        p.StateChanged += s =>
        {
            if (s == ServerState.Starting) stop = Task.Run(p.StopAsync);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.StartAsync(cfg, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
        await stop!;
        Assert.Equal(ServerState.Stopped, p.State);
        Assert.Null(p.ProcessId);
    }

    [Fact]
    public async Task Dispose_KillsProcess()
    {
        TestEnv.RequireLlamaAndModel();
        var cfg = TestEnv.Configure(TestEnv.LlamaBin!, TestEnv.Model!);
        var p = new LlamaServerProcess();
        await p.StartAsync(cfg, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        var pid = p.ProcessId;
        p.Dispose();
        Assert.False(TestEnv.ProcessAlive(pid));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => p.StartAsync(cfg));
    }

    [Fact]
    public void DescribeExit_Hints()
    {
        var oom = LlamaServerProcess.DescribeExit(["0.00.100.000 E ggml_backend_cuda_buffer_type_alloc_buffer: allocating 20000 MiB on device 0: cudaMalloc failed: out of memory"],
            1, whileStarting: true, port: 8765);
        Assert.Contains("Не хватило памяти", oom);
        Assert.Contains("cudaMalloc failed", oom);
        Assert.DoesNotContain("0.00.100.000", oom);

        var arg = LlamaServerProcess.DescribeExit(["error: invalid argument: --no-mmap"], 1, true, 8765);
        Assert.Contains("Дополнительные аргументы", arg);

        var vc = LlamaServerProcess.DescribeExit([], unchecked((int)0xC0000135), true, 8765);
        Assert.Contains("Visual C++", vc);

        var crash = LlamaServerProcess.DescribeExit(["0.01.000.000 I slot launch_slot_: id 0"], unchecked((int)0xC0000409), false, 8765);
        Assert.Contains("неожиданно завершился (код 0xC0000409)", crash);
        Assert.Contains("аварийно", crash);
    }
}
