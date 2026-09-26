using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Offload.Core.Tests;

/// <summary>Запрос, увиденный <see cref="RangeServer"/>.</summary>
public sealed record SeenRequest(string Path, string? Range, string? Authorization, string? TestHeader);

/// <summary>
/// Файловый HTTP-сервер для тестов загрузки (127.0.0.1, случайный порт): /file.bin с поддержкой Range «bytes=a-b» и «bytes=a-»,
/// /redirect → 302 на /file.bin, /api — пустой JSON. Записывает заголовки запросов и наибольшее число одновременных ответов.
/// </summary>
public sealed class RangeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _data;
    private int _active;
    private int _maxActive;
    private int _failOnce;

    public RangeServer(byte[] data)
    {
        _data = data;
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }
    public string FileUrl => BaseUrl + "/file.bin";

    /// <summary>Отвечать на Range-запросы частью файла (206); false — всегда весь файл (200).</summary>
    public bool SupportRange { get; init; } = true;

    /// <summary>Пауза перед телом ответа — чтобы части загружались одновременно.</summary>
    public int DelayMs { get; init; }

    /// <summary>Оборвать один ответ после стольких байт (имитация обрыва связи); 0 — нет.</summary>
    public int FailOnceAfterBytes
    {
        init => _failOnce = value;
    }

    public ConcurrentQueue<SeenRequest> Requests { get; } = new();

    public int MaxConcurrent => Volatile.Read(ref _maxActive);

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var active = Interlocked.Increment(ref _active);
        int seen;
        while (active > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, seen) != seen)
        {
        }
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var range = ctx.Request.Headers["Range"];
            Requests.Enqueue(new SeenRequest(path, range, ctx.Request.Headers["Authorization"], ctx.Request.Headers["X-Test"]));
            if (path == "/redirect")
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.RedirectLocation = FileUrl;
                return;
            }
            if (path == "/api")
            {
                var body = "{}"u8.ToArray();
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                return;
            }
            if (path != "/file.bin")
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            long from = 0, to = _data.Length - 1;
            if (SupportRange && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var parts = range[6..].Split('-');
                from = long.Parse(parts[0]);
                if (parts.Length > 1 && parts[1].Length > 0) to = Math.Min(to, long.Parse(parts[1]));
                ctx.Response.StatusCode = 206;
                ctx.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{_data.Length}");
            }
            var length = (int)(to - from + 1);
            ctx.Response.ContentLength64 = length;
            if (DelayMs > 0) await Task.Delay(DelayMs);

            var limit = length;
            var fail = Interlocked.Exchange(ref _failOnce, 0);
            if (fail > 0 && fail < length) limit = fail;
            await ctx.Response.OutputStream.WriteAsync(_data.AsMemory((int)from, limit));
            if (limit < length)
            {
                ctx.Response.Abort();
                return;
            }
        }
        catch
        {
            // Клиент оборвал соединение.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
