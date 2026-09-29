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

    /// <summary>Заголовок Authorization каждого запроса (путь, значение или null).</summary>
    public ConcurrentQueue<(string Path, string? Authorization)> Auth { get; } = new();

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

    /// <summary>Ответ поиска GET /api/models (JSON-массив).</summary>
    public string SearchJson { get; set; } = "[]";

    /// <summary>Сведения о репозитории GET /api/models/{repo} (JSON); нет записи — 401, как у настоящего хаба.</summary>
    public Dictionary<string, string> Infos { get; } = new(StringComparer.Ordinal);

    /// <summary>Закрытые репозитории: resolve без заголовка Authorization — 401.</summary>
    public HashSet<string> Gated { get; } = new(StringComparer.Ordinal);

    /// <summary>Сколько байт файлов отдано CDN (проверка ограничения чтения заголовка).</summary>
    public long ServedBytes => Interlocked.Read(ref _served);

    private long _served;

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
        Auth.Enqueue((path, ctx.Request.Headers["Authorization"]));
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
            if (path == "/api/models")
            {
                Write(ctx, Encoding.UTF8.GetBytes(SearchJson), "application/json");
                return;
            }
            if (path.StartsWith("/api/models/", StringComparison.Ordinal))
            {
                if (Infos.TryGetValue(path["/api/models/".Length..], out var info)) Write(ctx, Encoding.UTF8.GetBytes(info), "application/json");
                else ctx.Response.StatusCode = 401;
                return;
            }
            if (path.Contains("/resolve/"))
            {
                // Как HF: resolve → 302 на подписанный адрес CDN.
                var i = path.IndexOf("/resolve/", StringComparison.Ordinal);
                var repo = path[1..i];
                var rest = path[(i + "/resolve/".Length)..];
                var file = rest[(rest.IndexOf('/') + 1)..];
                if (Gated.Contains(repo) && ctx.Request.Headers["Authorization"] is null)
                {
                    ctx.Response.StatusCode = 401;
                    return;
                }
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
                long end = data.Length - 1;
                var range = ctx.Request.Headers["Range"];
                if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var bounds = range[6..].Split('-');
                    start = long.Parse(bounds[0]);
                    if (bounds.Length > 1 && bounds[1].Length > 0) end = Math.Min(end, long.Parse(bounds[1]));
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{data.Length}");
                }
                var length = end - start + 1;
                ctx.Response.ContentLength64 = length;
                var limit = FailAfterBytes >= 0 ? Math.Min(length, FailAfterBytes) : length;
                ctx.Response.OutputStream.Write(data, (int)start, (int)limit);
                Interlocked.Add(ref _served, limit);
                if (limit < length)
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
