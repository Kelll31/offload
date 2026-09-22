using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Util;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tests;

[CollectionDefinition("AppPaths", DisableParallelization = true)]
public sealed class AppPathsCollection;

/// <summary>Временный корень данных Offload, чтобы тесты не трогали профиль пользователя.</summary>
public sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-" + Guid.NewGuid().ToString("N"));

    public TempHome()
    {
        Directory.CreateDirectory(Path);
        AppPaths.OverrideDataDir(Path);
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(null);
        try { Directory.Delete(Path, true); } catch { }
    }
}

/// <summary>Домашняя папка + рабочая папка проекта + config.json (сервер на заданном порту).</summary>
internal sealed class TestEnv : IDisposable
{
    public const string ApiKey = "pc-test-key";
    public TempHome Home { get; } = new();
    public string Workspace { get; }

    public TestEnv(int? port = null, bool setupCompleted = true, Action<AppConfig>? configure = null)
    {
        ConfigStore.ReadOnly = true;
        Workspace = Path.Combine(Path.GetTempPath(), "pc-ws-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Workspace);
        Workspace = PathGuard.Canonicalize(Workspace);
        var cfg = new AppConfig { SetupCompleted = setupCompleted };
        cfg.Server.Port = port ?? FreePort();
        cfg.Server.ApiKey = ApiKey;
        cfg.Models.Installed.Add(new InstalledModel
        {
            Id = "test-coder",
            DisplayName = "TestCoder 7B",
            Quant = "Q4_K_M",
            RecommendedContext = 8192,
            Reasoning = ReasoningControl.EnableThinkingKwarg,
        });
        cfg.Models.ActiveModelId = "test-coder";
        cfg.Mcp.ServerStartTimeoutSeconds = 10;
        configure?.Invoke(cfg);
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(cfg, Json.Options));
    }

    public string WriteFile(string rel, string content, Encoding? enc = null)
    {
        var p = Path.Combine(Workspace, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, enc ?? new UTF8Encoding(false));
        return p;
    }

    public string PathOf(string rel) => Path.Combine(Workspace, rel.Replace('/', '\\'));

    public ToolContext Context(SessionState? state = null, CancellationToken ct = default) => new()
    {
        Tool = "test",
        Cfg = ConfigStore.Reload(),
        State = state ?? new SessionState(),
        Progress = new ProgressReporter(null, null),
        Roots = [Workspace],
        Ct = ct,
    };

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
        Home.Dispose();
        try { Directory.Delete(Workspace, true); } catch { }
    }
}

/// <summary>
/// Поддельный llama-server: /health, /props, /tokenize, /v1/chat/completions (SSE с usage и timings), проверка ключа API.
/// </summary>
internal sealed class FakeLlamaServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _requests = [];

    public int Port { get; }
    public int ContextSize { get; set; } = 8192;
    public string FinishReason { get; set; } = "stop";
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>Ответ модели по JSON запроса (messages, max_tokens…).</summary>
    public Func<JsonElement, string> Responder { get; set; } = _ => "ok";

    public IReadOnlyList<string> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    public FakeLlamaServer()
    {
        Port = TestEnv.FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/health")
            {
                await WriteJson(ctx, 200, "{\"status\":\"ok\"}");
                return;
            }
            if (ctx.Request.Headers["Authorization"] != "Bearer " + TestEnv.ApiKey)
            {
                await WriteJson(ctx, 401, "{\"error\":{\"code\":401,\"message\":\"Invalid API Key\",\"type\":\"authentication_error\"}}");
                return;
            }
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
            switch (path)
            {
                case "/props":
                    await WriteJson(ctx, 200, $"{{\"default_generation_settings\":{{\"n_ctx\":{ContextSize}}},\"total_slots\":1,\"model_alias\":\"offload\"}}");
                    return;
                case "/tokenize":
                    await WriteJson(ctx, 200, "{\"tokens\":[1,2,3]}");
                    return;
                case "/v1/chat/completions":
                    lock (_requests) _requests.Add(body);
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var content = Responder(doc.RootElement.Clone());
                        await StreamAsync(ctx, content);
                    }
                    return;
                default:
                    await WriteJson(ctx, 404, "{}");
                    return;
            }
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private async Task StreamAsync(HttpListenerContext ctx, string content)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
        var output = ctx.Response.OutputStream;
        async Task Send(string json)
        {
            var bytes = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
        var parts = Math.Max(1, Math.Min(4, content.Length));
        var size = (int)Math.Ceiling(content.Length / (double)parts);
        for (var i = 0; i < content.Length; i += Math.Max(1, size))
        {
            var piece = content.Substring(i, Math.Min(size, content.Length - i));
            await Send(JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = piece } } } }));
        }
        await Send($"{{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{FinishReason}\"}}]}}");
        await Send($"{{\"choices\":[],\"usage\":{{\"prompt_tokens\":120,\"completion_tokens\":{Math.Max(1, content.Length / 4)}}},\"timings\":{{\"predicted_per_second\":42.5,\"prompt_per_second\":900.0}}}}");
        await output.WriteAsync("data: [DONE]\n\n"u8.ToArray());
        ctx.Response.Close();
    }

    private static async Task WriteJson(HttpListenerContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    /// <summary>Текст последнего user-сообщения запроса.</summary>
    public static string UserText(JsonElement req) =>
        req.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString() ?? "";

    public static string SystemText(JsonElement req) =>
        req.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString() ?? "";

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
