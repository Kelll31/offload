using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Offload.Llama.Tests;

/// <summary>Запрос к поддельному серверу.</summary>
public sealed record FakeRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Минимальный HTTP/1.1-сервер на TcpListener (127.0.0.1, случайный порт): каждый ответ завершается закрытием
/// соединения, поэтому обработчик может писать «сырые» байты — в том числе SSE по частям.
/// </summary>
public sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<FakeRequest, Stream, CancellationToken, Task> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<FakeRequest> _requests = [];

    public FakeHttpServer(Func<FakeRequest, Stream, CancellationToken, Task> handler)
    {
        _handler = handler;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<FakeRequest> Requests
    {
        get
        {
            lock (_requests) return _requests.ToArray();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var req = await ReadRequestAsync(stream);
                if (req is null) return;
                lock (_requests) _requests.Add(req);
                await _handler(req, stream, _cts.Token);
                await stream.FlushAsync();
            }
            catch
            {
                // Клиент отключился — нормально для тестов отмены.
            }
        }
    }

    private static async Task<FakeRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one);
            if (n == 0) return null;
            buffer.Add(one[0]);
            var c = buffer.Count;
            if (c >= 4 && buffer[c - 4] == '\r' && buffer[c - 3] == '\n' && buffer[c - 2] == '\r' && buffer[c - 1] == '\n') break;
        }
        var head = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var i = l.IndexOf(':');
            if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
        }
        var body = "";
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) && len > 0)
        {
            var data = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = await stream.ReadAsync(data.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
            body = Encoding.UTF8.GetString(data, 0, read);
        }
        return new FakeRequest(first[0], first[1], headers, body);
    }

    /// <summary>Готовый ответ с телом.</summary>
    public static async Task WriteResponseAsync(Stream s, int status, string body, string contentType = "application/json",
        IDictionary<string, string>? headers = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {Reason(status)}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {bytes.Length}\r\n");
        sb.Append("Connection: close\r\n");
        if (headers is not null)
            foreach (var (k, v) in headers) sb.Append($"{k}: {v}\r\n");
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
        await s.WriteAsync(bytes);
    }

    public static async Task WriteBytesResponseAsync(Stream s, byte[] bytes, string contentType = "application/octet-stream")
    {
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(bytes);
    }

    /// <summary>Заголовки SSE-ответа (тело — до закрытия соединения).</summary>
    public static Task WriteSseHeadersAsync(Stream s) =>
        s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n")).AsTask();

    public static async Task WriteRawAsync(Stream s, string text)
    {
        await s.WriteAsync(Encoding.UTF8.GetBytes(text));
        await s.FlushAsync();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        302 => "Found",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}
