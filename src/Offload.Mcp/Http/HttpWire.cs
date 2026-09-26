using System.Globalization;
using System.Text;

namespace Offload.Mcp.Http;

/// <summary>Запрос HTTP/1.1, разобранный <see cref="HttpWire"/>: метод, путь, заголовки (без учёта регистра имён) и тело.</summary>
internal sealed class HttpRequest(string method, string target, Dictionary<string, string> headers, byte[] body)
{
    public string Method { get; } = method;
    public string Target { get; } = target;
    public byte[] Body { get; } = body;

    /// <summary>Байты тела, прочитанные вместе с заголовками (до <see cref="HttpWire.ReadBodyAsync"/>).</summary>
    internal byte[] Prefetched { get; init; } = [];

    public string? Header(string name) => headers.TryGetValue(name, out var v) ? v : null;

    internal HttpRequest WithBody(byte[] newBody) => new(Method, Target, headers, newBody);
}

/// <summary>Ошибка разбора запроса: код ответа и короткая причина (английский — её видит клиент).</summary>
internal sealed class HttpWireException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Минимальный разбор HTTP/1.1 поверх TCP для локального MCP-сервера: одна пара «запрос → ответ» на соединение
/// (Connection: close), строгие лимиты размера заголовков и тела, Content-Length или chunked. Kestrel не используем:
/// exe самодостаточный, а ModelContextProtocol.AspNetCore и общая среда ASP.NET Core заметно увеличили бы его.
/// </summary>
internal static class HttpWire
{
    public const int MaxHeaderBytes = 32 * 1024;
    public const int MaxHeaders = 100;
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    private static readonly string[] SingleValueHeaders =
        ["host", "authorization", "content-length", "transfer-encoding", "origin", "mcp-session-id", "mcp-protocol-version", "content-type"];

    /// <summary>Запрос целиком: заголовки и тело (для тестов и простых вызовов; сервер читает тело только после проверки доступа).</summary>
    public static async Task<HttpRequest> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var head = await ReadHeadersAsync(stream, ct).ConfigureAwait(false);
        return await ReadBodyAsync(stream, head, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Строка запроса и заголовки, без тела: тело выделяется и читается <see cref="ReadBodyAsync"/> только после проверки
    /// токена и Host — чужой локальный процесс без токена не заставит сервер держать мегабайты.
    /// </summary>
    public static async Task<HttpRequest> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var head = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
        var text = Encoding.Latin1.GetString(head.Head);
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new HttpWireException(400, "Malformed request line.");
        var method = requestLine[0];
        var target = requestLine[1];
        if (method.Length is 0 or > 16 || !method.All(char.IsAsciiLetterUpper)) throw new HttpWireException(400, "Malformed method.");
        // Только видимые ASCII (0x21–0x7E): одиночный LF или управляющие символы внутри пути — не HTTP, а попытка подмешать строки.
        if (target.Length is 0 or > 2048 || target[0] != '/' || !target.All(c => c is >= '!' and <= '~'))
            throw new HttpWireException(400, "Malformed request target.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            if (++count > MaxHeaders) throw new HttpWireException(431, "Too many headers.");
            if (line[0] is ' ' or '\t') throw new HttpWireException(400, "Obsolete header folding is not supported.");
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new HttpWireException(400, "Malformed header.");
            var name = line[..colon];
            if (name.Any(c => c <= ' ' || c >= 0x7F)) throw new HttpWireException(400, "Malformed header name.");
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(c => c < ' ' && c != '\t')) throw new HttpWireException(400, "Malformed header value.");
            if (headers.TryGetValue(name, out var existing))
            {
                if (SingleValueHeaders.Contains(name.ToLowerInvariant())) throw new HttpWireException(400, $"Duplicate '{name}' header.");
                headers[name] = existing + ", " + value;
            }
            else
            {
                headers[name] = value;
            }
        }

        return new HttpRequest(method, target, headers, []) { Prefetched = head.Tail };
    }

    /// <summary>Тело запроса по Content-Length или chunked (с учётом байтов, прочитанных вместе с заголовками).</summary>
    public static async Task<HttpRequest> ReadBodyAsync(Stream stream, HttpRequest head, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Transfer-Encoding", "Content-Length" })
        {
            if (head.Header(name) is { } v) headers[name] = v;
        }
        var body = await ReadBodyAsync(stream, headers, head.Prefetched, ct).ConfigureAwait(false);
        return head.WithBody(body);
    }

    /// <summary>Байты до пустой строки (без неё) и то, что уже прочитано после неё.</summary>
    private static async Task<(byte[] Head, byte[] Tail)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes + 4];
        var filled = 0;
        while (true)
        {
            if (filled >= buffer.Length) throw new HttpWireException(431, "Request headers are too large.");
            var n = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), ct).ConfigureAwait(false);
            if (n == 0) throw new HttpWireException(400, "Connection closed before the request was complete.");
            var searchFrom = Math.Max(0, filled - 3);
            filled += n;
            var end = buffer.AsSpan(searchFrom, filled - searchFrom).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                end += searchFrom;
                if (end > MaxHeaderBytes) throw new HttpWireException(431, "Request headers are too large.");
                return (buffer[..end], buffer[(end + 4)..filled]);
            }
        }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, Dictionary<string, string> headers, byte[] already, CancellationToken ct)
    {
        var te = headers.GetValueOrDefault("Transfer-Encoding");
        var cl = headers.GetValueOrDefault("Content-Length");
        if (te is not null && cl is not null) throw new HttpWireException(400, "Both Content-Length and Transfer-Encoding are present.");
        var reader = new PrefixedReader(stream, already);
        if (te is not null)
        {
            if (!te.Equals("chunked", StringComparison.OrdinalIgnoreCase)) throw new HttpWireException(501, "Only chunked transfer encoding is supported.");
            return await ReadChunkedAsync(reader, ct).ConfigureAwait(false);
        }
        if (cl is null) return [];
        if (!long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out var length)) throw new HttpWireException(400, "Invalid Content-Length.");
        if (length > MaxBodyBytes) throw new HttpWireException(413, "Request body is too large.");
        var body = new byte[length];
        await reader.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    private static async Task<byte[]> ReadChunkedAsync(PrefixedReader reader, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            var semi = sizeLine.IndexOf(';');
            if (semi >= 0) sizeLine = sizeLine[..semi];
            if (!int.TryParse(sizeLine.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new HttpWireException(400, "Invalid chunk size.");
            if (size == 0)
            {
                // Трейлеры не поддерживаем: ждём пустую строку.
                while ((await reader.ReadLineAsync(ct).ConfigureAwait(false)).Length > 0)
                {
                }
                return ms.ToArray();
            }
            if (ms.Length + size > MaxBodyBytes) throw new HttpWireException(413, "Request body is too large.");
            var chunk = new byte[size];
            await reader.ReadExactlyAsync(chunk, ct).ConfigureAwait(false);
            ms.Write(chunk);
            if ((await reader.ReadLineAsync(ct).ConfigureAwait(false)).Length != 0) throw new HttpWireException(400, "Malformed chunk.");
        }
    }

    public static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        411 => "Length Required",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        _ => "Status",
    };

    /// <summary>Строка статуса и заголовки ответа (всегда Connection: close и no-store).</summary>
    public static byte[] BuildHead(int status, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
        foreach (var (k, v) in headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Полный ответ с телом известной длины.</summary>
    public static async Task WriteResponseAsync(Stream stream, int status, string? contentType, byte[] body,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders, CancellationToken ct)
    {
        var headers = new List<KeyValuePair<string, string>>(extraHeaders ?? []);
        if (contentType is not null) headers.Add(new("Content-Type", contentType));
        headers.Add(new("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture)));
        await stream.WriteAsync(BuildHead(status, headers), ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Чтение из потока с уже прочитанным «хвостом» после заголовков.</summary>
    private sealed class PrefixedReader(Stream stream, byte[] prefix)
    {
        private int _pos;

        private async ValueTask<int> ReadByteAsync(CancellationToken ct)
        {
            if (_pos < prefix.Length) return prefix[_pos++];
            var one = new byte[1];
            var n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            return n == 0 ? -1 : one[0];
        }

        public async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
        {
            var offset = 0;
            var fromPrefix = Math.Min(buffer.Length, prefix.Length - _pos);
            if (fromPrefix > 0)
            {
                Array.Copy(prefix, _pos, buffer, 0, fromPrefix);
                _pos += fromPrefix;
                offset = fromPrefix;
            }
            if (offset < buffer.Length)
            {
                try { await stream.ReadExactlyAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false); }
                catch (EndOfStreamException) { throw new HttpWireException(400, "Connection closed before the body was complete."); }
            }
        }

        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var b = await ReadByteAsync(ct).ConfigureAwait(false);
                if (b < 0) throw new HttpWireException(400, "Connection closed inside a chunked body.");
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                if (sb.Length > 1024) throw new HttpWireException(400, "Chunk header is too long.");
                sb.Append((char)b);
            }
        }
    }
}

/// <summary>
/// Поток тела ответа для SSE: заголовки уходят при первой записи или сбросе (транспорт SDK сбрасывает поток сразу),
/// тело кодируется chunked; <see cref="CompleteAsync"/> пишет завершающий блок.
/// </summary>
internal sealed class ChunkedResponseStream(Stream inner, byte[] head) : Stream
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _headSent;
    private bool _completed;

    public bool Started => _headSent;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureHeadAsync(cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_completed) throw new InvalidOperationException("The response is already complete.");
            await EnsureHeadAsync(cancellationToken).ConfigureAwait(false);
            var size = Encoding.ASCII.GetBytes(buffer.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
            await inner.WriteAsync(size, cancellationToken).ConfigureAwait(false);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await inner.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Завершить тело (если заголовки уже ушли).</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_headSent || _completed) return;
            _completed = true;
            await inner.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            await inner.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureHeadAsync(CancellationToken ct)
    {
        if (_headSent) return;
        _headSent = true;
        await inner.WriteAsync(head, ct).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _lock.Dispose();
        base.Dispose(disposing);
    }
}
