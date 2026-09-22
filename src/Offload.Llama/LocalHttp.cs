using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Offload.Core;

namespace Offload.Llama;

/// <summary>
/// HttpClient для локального llama-server: без прокси (корпоративный прокси не должен перехватывать 127.0.0.1),
/// без общего таймаута — длительность запроса ограничивает только CancellationToken вызывающего.
/// Ключ API добавляется в каждый запрос, а не в DefaultRequestHeaders (у разных клиентов разные ключи).
/// </summary>
internal static class LocalHttp
{
    private static readonly Lazy<HttpClient> ClientLazy = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        return client;
    });

    public static HttpClient Client => ClientLazy.Value;

    public static HttpRequestMessage Request(HttpMethod method, string url, string? apiKey)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return req;
    }

    /// <summary>Отказ в соединении (сервер не слушает порт).</summary>
    public static bool IsConnectionRefused(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.AddressNotAvailable })
                return true;
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError })
                return true;
        }
        return false;
    }

    /// <summary>URL для подключения клиента: 0.0.0.0/:: заменяются на петлевой адрес, IPv6 — в скобках.</summary>
    public static string ClientBaseUrl(string host, int port)
    {
        var h = host.Trim();
        if (h is "0.0.0.0" or "*" or "+" || h.Length == 0) h = "127.0.0.1";
        else if (h is "::" or "[::]") h = "[::1]";
        else if (h.Contains(':') && !h.StartsWith('[')) h = $"[{h}]";
        return $"http://{h}:{port}";
    }
}
