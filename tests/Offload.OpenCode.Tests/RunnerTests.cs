using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Core.Config;

namespace Offload.OpenCode.Tests;

/// <summary>Заглушка llama-server: /health = 200, /props — контекст слота.</summary>
internal sealed class FakeLlamaServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public int Port { get; }

    public FakeLlamaServer(int nCtx = 12288)
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var body = ctx.Request.Url!.AbsolutePath == "/props"
                    ? $"{{\"default_generation_settings\":{{\"n_ctx\":{nCtx}}},\"total_slots\":1}}"
                    : "{\"status\":\"ok\"}";
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

[Collection("AppPaths")]
public class RunnerTests
{
    private static string Ev(string json) => json.Replace("\r", "").Replace("\n", "");

    /// <summary>Поддельный opencode: .cmd пишет аргументы и окружение, читает stdin до EOF, выводит заготовленные события.</summary>
    private static string MakeFake(string dir, string[] events, string body = "echo hi> hello.txt", int exitCode = 0, bool captureStdin = false)
    {
        File.WriteAllLines(Path.Combine(dir, "events.jsonl"), events, new UTF8Encoding(false));
        var read = captureStdin
            ? "powershell -NoProfile -Command \"$s=[Console]::OpenStandardInput(); $m=New-Object IO.MemoryStream; $s.CopyTo($m); [IO.File]::WriteAllBytes('%~dp0stdin.bin', $m.ToArray())\""
            : "more > nul";
        var script = $"""
            @echo off
            echo %*> "%~dp0args.txt"
            set > "%~dp0env.txt"
            {read}
            {body}
            type "%~dp0events.jsonl"
            exit /b {exitCode}
            """;
        var path = Path.Combine(dir, "opencode.cmd");
        File.WriteAllText(path, script.Replace("\n", "\r\n"), Encoding.ASCII);
        return path;
    }

    private static (AppConfig Cfg, string Wd, string FakeDir) Setup(TempHome home, FakeLlamaServer? server, string[] events,
        string body = "echo hi> hello.txt", int exitCode = 0, bool captureStdin = false)
    {
        var cfg = TestConfig.Make();
        cfg.Server.Port = server?.Port ?? FakeLlamaServer.FreePort();
        var fakeDir = Path.Combine(home.Path, "fake");
        Directory.CreateDirectory(fakeDir);
        cfg.OpenCode.ExecutablePath = MakeFake(fakeDir, events, body, exitCode, captureStdin);
        var wd = Path.Combine(home.Path, "project");
        Directory.CreateDirectory(wd);
        return (cfg, wd, fakeDir);
    }

    private static readonly string[] HappyEvents =
    [
        Ev("""{"type":"step_start","part":{"type":"step-start"}}"""),
        Ev("""{"type":"tool_use","part":{"tool":"write","state":{"status":"completed","input":{"filePath":"hello.txt"},"title":"hello.txt"}}}"""),
        Ev("""{"type":"step_finish","part":{"reason":"tool-calls","tokens":{"input":900,"output":20,"reasoning":0,"cache":{"read":100,"write":0}}}}"""),
        "не JSON — строка журнала",
        Ev("""{"type":"step_start","part":{"type":"step-start"}}"""),
        Ev("""{"type":"text","part":{"type":"text","text":"Result: created hello.txt"}}"""),
        Ev("""{"type":"step_finish","part":{"reason":"stop","tokens":{"input":1000,"output":10,"reasoning":0,"cache":{"read":0,"write":0}}}}"""),
    ];

    [Fact]
    public async Task Run_HappyPath_ParsesEventsDetectsFilesAndPassesEnv()
    {
        using var home = new TempHome();
        using var server = new FakeLlamaServer(nCtx: 12288);
        var (cfg, wd, fake) = Setup(home, server, HappyEvents, captureStdin: true);
        var progress = new List<string>();
        const string task = "Создай файл hello.txt со словом hi";

        var r = await OpenCodeRunner.RunAsync(cfg, task, wd, new OpenCodeRunOptions(false, TimeSpan.FromMinutes(1)),
            m => { lock (progress) progress.Add(m); });

        Assert.True(r.Success, r.Error);
        Assert.Null(r.Error);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Result: created hello.txt", r.FinalText);
        Assert.Equal(new[] { "write hello.txt" }, r.ToolCalls);
        Assert.Equal(new[] { "A hello.txt" }, r.ChangedFiles);
        Assert.Equal(2000, r.PromptTokens);
        Assert.Equal(30, r.CompletionTokens);
        lock (progress) Assert.Contains("шаг 1: запись: hello.txt", progress);

        // Задача пришла через stdin в UTF-8 без BOM.
        Assert.Equal(Encoding.UTF8.GetBytes(task), File.ReadAllBytes(Path.Combine(fake, "stdin.bin")));

        var args = File.ReadAllText(Path.Combine(fake, "args.txt"));
        Assert.Contains($"run --dir {wd} -m offload/local-coder --agent offload --format json --title offload", args);

        var env = File.ReadAllText(Path.Combine(fake, "env.txt"));
        Assert.Contains($"OPENCODE_CONFIG={AppPaths.OpenCodeConfigFile}", env);
        Assert.Contains($"OFFLOAD_API_KEY={TestConfig.Key}", env);
        Assert.Contains($"XDG_DATA_HOME={Path.Combine(AppPaths.OpenCodeDir, "data")}", env);
        Assert.Contains("OPENCODE_DISABLE_AUTOUPDATE=1", env);
        Assert.Contains($"PWD={wd}", env);
        Assert.DoesNotContain("OPENCODE_CONFIG_CONTENT=", env); // оболочка совпадает с настройкой

        // Управляемый конфиг записан с контекстом работающего сервера.
        var managed = JsonNode.Parse(File.ReadAllText(AppPaths.OpenCodeConfigFile))!;
        Assert.Equal(12288, (int)managed["provider"]!["offload"]!["models"]!["local-coder"]!["limit"]!["context"]!);
        Assert.True(File.Exists(OpenCodeRunner.RunLogFile));
    }

    [Fact]
    public async Task Run_ReadOnlyAgentAndShellOverride()
    {
        using var home = new TempHome();
        using var server = new FakeLlamaServer();
        var (cfg, wd, fake) = Setup(home, server, HappyEvents, body: "rem");

        var r = await OpenCodeRunner.RunAsync(cfg, "Объясни проект", wd, new OpenCodeRunOptions(true, TimeSpan.FromMinutes(1), "plan"));
        Assert.True(r.Success, r.Error);
        Assert.Empty(r.ChangedFiles);
        Assert.Contains("--agent offload-readonly", File.ReadAllText(Path.Combine(fake, "args.txt")));
        Assert.DoesNotContain("OPENCODE_CONFIG_CONTENT=", File.ReadAllText(Path.Combine(fake, "env.txt")));

        // Разрешение оболочки из параметров задачи перекрывает настройку (false).
        r = await OpenCodeRunner.RunAsync(cfg, "Собери проект", wd, new OpenCodeRunOptions(true, TimeSpan.FromMinutes(1)));
        Assert.True(r.Success, r.Error);
        var env = File.ReadAllText(Path.Combine(fake, "env.txt"));
        Assert.Contains("OPENCODE_CONFIG_CONTENT=", env);
        Assert.Contains("\"git push*\":\"deny\"", env);
    }

    [Fact]
    public async Task Run_ErrorEventAndExitCode()
    {
        using var home = new TempHome();
        using var server = new FakeLlamaServer();
        var (cfg, wd, _) = Setup(home, server,
            [Ev("""{"type":"error","error":{"name":"APIError","data":{"message":"model not found"}}}""")],
            body: "echo boom 1>&2", exitCode: 1);

        var r = await OpenCodeRunner.RunAsync(cfg, "x", wd, new OpenCodeRunOptions(false, TimeSpan.FromMinutes(1)));
        Assert.False(r.Success);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("model not found", r.Error);
        Assert.Contains("код 1", r.Error);
    }

    [Fact]
    public async Task Run_NonZeroExitWithoutEvents_UsesStderrTail()
    {
        using var home = new TempHome();
        using var server = new FakeLlamaServer();
        var (cfg, wd, _) = Setup(home, server, [], body: "echo \u001b[31mError: agent failed\u001b[0m 1>&2", exitCode: 3);
        var r = await OpenCodeRunner.RunAsync(cfg, "x", wd, new OpenCodeRunOptions(false, TimeSpan.FromMinutes(1)));
        Assert.False(r.Success);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("Error: agent failed", r.Error);
        Assert.False(r.Error!.Contains('\u001b'), r.Error); // строковое сравнение xUnit культурное — ESC для него «невидим»
    }

    [Fact]
    public async Task Run_Timeout_KillsProcessTree()
    {
        using var home = new TempHome();
        using var server = new FakeLlamaServer();
        var (cfg, wd, _) = Setup(home, server, HappyEvents, body: "ping -n 60 127.0.0.1 > nul");
        var started = DateTime.UtcNow;
        var r = await OpenCodeRunner.RunAsync(cfg, "x", wd, new OpenCodeRunOptions(false, TimeSpan.FromSeconds(3)));
        Assert.False(r.Success);
        Assert.Equal("Превышено время ожидания (3 с).", r.Error);
        Assert.Equal(-1, r.ExitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Run_Cancellation_ReturnsFailure()
    {
        using var home = new TempHome();
        using var server = new FakeLlamaServer();
        var (cfg, wd, _) = Setup(home, server, HappyEvents, body: "ping -n 60 127.0.0.1 > nul");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var r = await OpenCodeRunner.RunAsync(cfg, "x", wd, new OpenCodeRunOptions(false, TimeSpan.FromMinutes(5)), ct: cts.Token);
        Assert.False(r.Success);
        Assert.Equal("Задача отменена.", r.Error);
    }

    [Fact]
    public async Task Run_Heartbeat_WhileSilent()
    {
        using var home = new TempHome();
        using var server = new FakeLlamaServer();
        var (cfg, wd, _) = Setup(home, server, HappyEvents, body: "ping -n 3 127.0.0.1 > nul");
        var saved = OpenCodeRunner.HeartbeatInterval;
        OpenCodeRunner.HeartbeatInterval = TimeSpan.FromMilliseconds(300);
        var progress = new List<string>();
        try
        {
            var r = await OpenCodeRunner.RunAsync(cfg, "x", wd, new OpenCodeRunOptions(false, TimeSpan.FromMinutes(1)),
                m => { lock (progress) progress.Add(m); });
            Assert.True(r.Success, r.Error);
        }
        finally
        {
            OpenCodeRunner.HeartbeatInterval = saved;
        }
        lock (progress) Assert.Contains(progress, m => m.StartsWith("запуск агента…", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_Preconditions()
    {
        using var home = new TempHome();
        var (cfg, wd, _) = Setup(home, null, HappyEvents);

        var r = await OpenCodeRunner.RunAsync(cfg, "x", Path.Combine(home.Path, "нет-такой"), new OpenCodeRunOptions(false, TimeSpan.FromMinutes(1)));
        Assert.False(r.Success);
        Assert.StartsWith("Рабочая папка не найдена", r.Error);

        r = await OpenCodeRunner.RunAsync(cfg, "  ", wd, new OpenCodeRunOptions(false, TimeSpan.FromMinutes(1)));
        Assert.StartsWith("Пустая задача", r.Error);

        // Сервер не слушает порт — ошибка сразу, без запуска OpenCode.
        r = await OpenCodeRunner.RunAsync(cfg, "x", wd, new OpenCodeRunOptions(false, TimeSpan.FromMinutes(1)));
        Assert.False(r.Success);
        Assert.Contains("не отвечает", r.Error);

        cfg.OpenCode.ExecutablePath = null;
        if (Core.Processes.ProcessRunner.FindOnPath("opencode.exe") is null)
        {
            r = await OpenCodeRunner.RunAsync(cfg, "x", wd, new OpenCodeRunOptions(false, TimeSpan.FromMinutes(1)));
            Assert.StartsWith("OpenCode не установлен", r.Error);
        }
    }

    [Fact]
    public async Task QueueLock_SerializesAndTimesOut()
    {
        var name = @"Local\Offload.Test." + Guid.NewGuid().ToString("N");
        var first = await RunQueueLock.AcquireAsync(name, TimeSpan.FromSeconds(5), null, CancellationToken.None);
        Assert.NotNull(first);
        var second = await RunQueueLock.AcquireAsync(name, TimeSpan.FromMilliseconds(600), null, CancellationToken.None);
        Assert.Null(second);

        var waiting = RunQueueLock.AcquireAsync(name, TimeSpan.FromSeconds(10), null, CancellationToken.None);
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        first!.Dispose();
        using var third = await waiting;
        Assert.NotNull(third);

        using var cts = new CancellationTokenSource(300);
        Assert.Null(await RunQueueLock.AcquireAsync(name, TimeSpan.FromSeconds(30), null, cts.Token));
    }

    [Fact]
    public void LaunchScript_QuotesAndSetsEnvironment()
    {
        var env = new Dictionary<string, string?> { ["A_VAR"] = "it's", ["GONE"] = null };
        var script = OpenCodeRunner.BuildLaunchScript(@"C:\Program Files\O'Code\opencode.exe", @"C:\Проекты\‘x’", env);
        Assert.Contains("[Environment]::SetEnvironmentVariable('A_VAR', 'it''s', 'Process')", script);
        Assert.Contains("[Environment]::SetEnvironmentVariable('GONE', $null, 'Process')", script);
        Assert.Contains(@"Set-Location -LiteralPath 'C:\Проекты\‘‘x’’'", script);
        Assert.Contains(@"& 'C:\Program Files\O''Code\opencode.exe'", script);
        Assert.Equal("'a''b'", OpenCodeRunner.PsQuote("a'b"));
    }

    [Fact]
    public void Arguments_AndAgentMapping()
    {
        Assert.True(OpenCodeRunner.IsReadOnlyAgent("plan"));
        Assert.True(OpenCodeRunner.IsReadOnlyAgent("PLAN"));
        Assert.False(OpenCodeRunner.IsReadOnlyAgent("build"));
        Assert.False(OpenCodeRunner.IsReadOnlyAgent(null));
        var args = OpenCodeRunner.BuildArguments(TestConfig.Make(), @"C:\p", readOnly: false);
        Assert.Equal(new[] { "run", "--dir", @"C:\p", "-m", "offload/local-coder", "--agent", "offload", "--format", "json", "--title", "offload" }, args);
        Assert.Equal("15 мин", OpenCodeRunner.FormatTimeout(TimeSpan.FromMinutes(15)));
    }
}
