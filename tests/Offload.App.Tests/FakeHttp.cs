using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Offload.App.Tests;

/// <summary>
/// Минимальный HTTP-сервер для тестов (127.0.0.1, случайный порт): ответы по пути из таблицы, каждый ответ закрывает
/// соединение. Неизвестный путь — 404. Счётчик запросов — для проверки кэша.
/// </summary>
public sealed class FakeHttp : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (int Status, byte[] Body)> _routes = new(StringComparer.Ordinal);
    private readonly List<string> _requests = [];
    private readonly Task _loop;

    public FakeHttp()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests) return _requests.ToArray();
        }
    }

    public void Map(string path, string body, int status = 200) => Map(path, Encoding.UTF8.GetBytes(body), status);

    public void Map(string path, byte[] body, int status = 200)
    {
        lock (_routes) _routes[path] = (status, body);
    }

    private async Task LoopAsync()
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
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one) == 0) return;
                    head.Append((char)one[0]);
                }
                var path = head.ToString().Split(' ')[1];
                lock (_requests) _requests.Add(path);
                (int Status, byte[] Body) route;
                lock (_routes) route = _routes.TryGetValue(path, out var r) ? r : (404, Encoding.UTF8.GetBytes("{\"message\":\"Not Found\"}"));
                var header = $"HTTP/1.1 {route.Status} X\r\nContent-Type: application/octet-stream\r\nContent-Length: {route.Body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(route.Body);
                await stream.FlushAsync();
            }
            catch
            {
                // Клиент отключился.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}
