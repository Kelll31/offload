using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Offload.Models.Tests;

/// <summary>
/// Локальная имитация Hugging Face: tree API с постраничной выдачей (Link: rel="next") и resolve
/// с 302-перенаправлением на «CDN», поддерживающий Range (как настоящий хаб).
/// </summary>
internal sealed class LocalHub : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, List<object>> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public string BaseUrl { get; }
    public int PageSize { get; set; } = 1000;
    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>Оборвать ответ файла после стольких байт (имитация обрыва связи); -1 — нет.</summary>
    public long FailAfterBytes { get; set; } = -1;

    public LocalHub()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Добавить файл в репозиторий (и в выдачу tree API).</summary>
    public void AddFile(string repo, string path, byte[] data, string sha256)
    {
        _files[$"{repo}/{path}"] = data;
        Tree(repo).Add(new { type = "file", oid = "0", size = data.Length, path, lfs = new { oid = sha256, size = data.Length, pointerSize = 132 } });
    }

    public void AddTreeEntry(string repo, object entry) => Tree(repo).Add(entry);

    private List<object> Tree(string repo) => _trees.TryGetValue(repo, out var t) ? t : _trees[repo] = [];

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
        Log.Enqueue($"{ctx.Request.HttpMethod} {ctx.Request.Url.PathAndQuery} range={ctx.Request.Headers["Range"]}");
        try
        {
            if (path.StartsWith("/api/models/", StringComparison.Ordinal) && path.Contains("/tree/"))
            {
                var repo = path["/api/models/".Length..path.IndexOf("/tree/", StringComparison.Ordinal)];
                if (!_trees.TryGetValue(repo, out var tree))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                var cursor = int.TryParse(ctx.Request.QueryString["cursor"], out var c) ? c : 0;
                var page = tree.Skip(cursor).Take(PageSize).ToList();
                if (cursor + PageSize < tree.Count)
                    ctx.Response.AddHeader("Link", $"<{BaseUrl}/api/models/{repo}/tree/main?recursive=true&cursor={cursor + PageSize}>; rel=\"next\"");
                Write(ctx, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(page)), "application/json");
                return;
            }
            if (path.Contains("/resolve/"))
            {
                // Как HF: resolve → 302 на подписанный адрес CDN.
                var i = path.IndexOf("/resolve/", StringComparison.Ordinal);
                var repo = path[1..i];
                var rest = path[(i + "/resolve/".Length)..];
                var file = rest[(rest.IndexOf('/') + 1)..];
                if (!_files.ContainsKey($"{repo}/{file}"))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                ctx.Response.StatusCode = 302;
                ctx.Response.RedirectLocation = $"{BaseUrl}/cdn/{repo}/{file}?Expires=3600";
                return;
            }
            if (path.StartsWith("/cdn/", StringComparison.Ordinal))
            {
                var key = path["/cdn/".Length..];
                var data = _files[key];
                long start = 0;
                var range = ctx.Request.Headers["Range"];
                if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    start = long.Parse(range[6..].Split('-')[0]);
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", $"bytes {start}-{data.Length - 1}/{data.Length}");
                }
                ctx.Response.ContentLength64 = data.Length - start;
                var limit = FailAfterBytes >= 0 ? Math.Min(data.Length - start, FailAfterBytes) : data.Length - start;
                ctx.Response.OutputStream.Write(data, (int)start, (int)limit);
                if (limit < data.Length - start)
                {
                    FailAfterBytes = -1; // оборвать один раз
                    ctx.Response.Abort();
                    return;
                }
                return;
            }
            ctx.Response.StatusCode = 404;
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static void Write(HttpListenerContext ctx, byte[] body, string type)
    {
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
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
