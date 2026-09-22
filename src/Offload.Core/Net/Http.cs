using System.Net;
using System.Net.Http.Headers;

namespace Offload.Core.Net;

/// <summary>Общие экземпляры HttpClient (переиспользуются во всём процессе).</summary>
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
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }
}
