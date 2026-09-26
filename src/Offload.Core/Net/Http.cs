using System.Net;
using System.Net.Http.Headers;

namespace Offload.Core.Net;

/// <summary>
/// Общие экземпляры HttpClient (переиспользуются во всём процессе). Сетевые настройки (<see cref="NetworkOptions"/>)
/// применяются к каждому запросу: прокси, зеркала GitHub и токен Hugging Face — без пересоздания клиентов.
/// </summary>
public static class Http
{
    private static readonly Lazy<HttpClient> ApiLazy = new(() => Create(TimeSpan.FromSeconds(60)));
    private static readonly Lazy<HttpClient> DownloadLazy = new(() => Create(Timeout.InfiniteTimeSpan));

    /// <summary>Для JSON-API (GitHub, Hugging Face): таймаут 60 секунд.</summary>
    public static HttpClient Api => ApiLazy.Value;

    /// <summary>Для больших загрузок: без общего таймаута (контроль через CancellationToken и таймаут чтения).</summary>
    public static HttpClient Download => DownloadLazy.Value;

    private static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            UseProxy = true,
            Proxy = ConfiguredProxy.Instance,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        var client = new HttpClient(new NetworkHandler(handler), disposeHandler: true) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    /// <summary>
    /// Зеркала GitHub (подмена адреса) и токен Hugging Face (только хабу, если заголовок не задан вызывающим кодом).
    /// Стоит снаружи обработки перенаправлений: адреса, на которые перенаправил сервер, не меняются и токена не получают.
    /// </summary>
    internal sealed class NetworkHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { IsAbsoluteUri: true } uri && !uri.IsLoopback)
            {
                request.RequestUri = NetworkOptions.RewriteForMirror(uri);
            }
            if (request.RequestUri is { } target && request.Headers.Authorization is null &&
                NetworkOptions.AuthorizationFor(target) is { } auth)
            {
                request.Headers.Authorization = auth;
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
