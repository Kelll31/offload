using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core.Logging;
using Offload.Mcp.Infrastructure;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Offload.Mcp.Http;

/// <summary>
/// Параметры HTTP-режима MCP: адрес (только loopback), порт (Mcp.HttpPort — случайный для пользователя), токен Bearer и
/// закреплённые корни рабочей области (<c>--root</c>; корни клиента не принимаются).
/// </summary>
internal sealed record McpHttpOptions(IPAddress Address, int Port, string Token, IReadOnlyList<string> Roots)
{
    public const string EndpointPath = "/mcp";

    /// <summary>
    /// Разрешить инструменты, которые выполняют код проекта или пишут файлы (<c>--allow-exec</c>). По умолчанию выключено:
    /// см. <see cref="HttpExecPolicy"/>.
    /// </summary>
    public bool AllowExec { get; init; }

    /// <summary>
    /// Актуальный токен (config.json): после ротации (IPC rotate-mcp-http-token) работающий сервер переходит на новый токен
    /// без перезапуска. null — токен фиксирован (<see cref="Token"/>).
    /// </summary>
    public Func<string?>? TokenSource { get; init; }

    /// <summary>
    /// Дополнительные допустимые значения Host/Origin («хост:порт», Mcp.HttpAllowedHosts): проброс порта (ssh -L → localhost:1234),
    /// devcontainer (host.docker.internal:&lt;порт&gt;). Точное совпадение без учёта регистра; адрес прослушивания не меняется.
    /// </summary>
    public IReadOnlyList<string> ExtraHosts { get; init; } = [];

    /// <summary>Сколько ждать строку запроса и заголовки (медленный клиент не держит соединение).</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Сколько ждать тело запроса после проверки доступа.</summary>
    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько сессий (ревизии протокола с initialize) держится одновременно.</summary>
    public int MaxSessions { get; init; } = 32;

    /// <summary>Сессия без запросов дольше этого срока закрывается.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Сколько соединений обрабатывается одновременно (остальные ждут в очереди ОС).</summary>
    public int MaxConnections { get; init; } = 64;
}

/// <summary>
/// MCP-сервер Offload по Streamable HTTP (<c>Offload.exe --mcp-http --root &lt;папка&gt;</c>) — для клиентов, которым stdio
/// недоступен. Слушает только 127.0.0.1; каждый запрос — с <c>Authorization: Bearer</c> (Mcp.HttpToken); Host и Origin проверяются
/// (защита от DNS rebinding из браузера): по умолчанию допустимы только 127.0.0.1:&lt;порт&gt; и localhost:&lt;порт&gt;, поэтому
/// проброс порта (ssh -L, devcontainer через host.docker.internal) работает, только если его «хост:порт» внесён в
/// Mcp.HttpAllowedHosts. Тело запроса читается только после этих проверок.
/// Корни рабочей области закреплены при запуске; инструменты, выполняющие код или пишущие файлы, без <c>--allow-exec</c>
/// отклоняются (<see cref="HttpExecPolicy"/>). По умолчанию ничего не слушает — только при явном запуске.
/// </summary>
/// <remarks>
/// Ревизии протокола до 2026-07-28: initialize создаёт сессию (заголовок Mcp-Session-Id) со своим сервером и своим
/// <see cref="SessionState"/>, как отдельный stdio-процесс. С 2026-07-28 сессий нет: запросы без Mcp-Session-Id с
/// MCP-Protocol-Version ≥ 2026-07-28 обрабатывает общий сервер без сессии (состояние транспорта сохраняется, поэтому MRTR —
/// корни, повтор вызова — работает; возможности клиента берутся из _meta каждого запроса).
/// </remarks>
internal sealed partial class McpHttpHost : IAsyncDisposable
{
    private readonly McpHttpOptions _options;
    private readonly List<string> _extraHosts;
    private byte[] _token;
    private long _tokenCheckedTicks;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connections;
    private readonly SemaphoreSlim _sessionlessLock = new(1, 1);
    private Session? _sessionless;
    private Task? _acceptLoop;

    public McpHttpHost(McpHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IPAddress.IsLoopback(options.Address))
            throw new ArgumentException($"Offload MCP over HTTP listens only on loopback; '{options.Address}' is not allowed.", nameof(options));
        if (options.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "Port must be 0–65535.");
        if (string.IsNullOrWhiteSpace(options.Token) || options.Token.Length < 16)
            throw new ArgumentException("A bearer token of at least 16 characters is required.", nameof(options));
        if (options.Roots is not { Count: > 0 } || options.Roots.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Offload MCP over HTTP needs at least one pinned workspace root (--root <dir>).", nameof(options));
        _options = options;
        _extraHosts = ValidExtraHosts(options.ExtraHosts);
        _token = Encoding.UTF8.GetBytes(options.Token);
        _listener = new TcpListener(options.Address, options.Port);
        _listener.Server.ExclusiveAddressUse = true;
        _connections = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    /// <summary>Состояние новой сессии: закреплённые корни, признак HTTP (без одноразовых разрешений команд).</summary>
    private SessionState NewState() => new() { PinnedRoots = [.. _options.Roots], IsHttpTransport = true };

    /// <summary>Фактический порт (после Start; при Port = 0 выбирается свободный).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Uri Endpoint => new($"http://127.0.0.1:{Port}{McpHttpOptions.EndpointPath}");

    public void Start()
    {
        _listener.Start(backlog: 64);
        _acceptLoop = Task.Run(AcceptLoopAsync);
        Log.Info("mcp-http", $"MCP по HTTP слушает {Endpoint}");
    }

    /// <summary>Работать до отмены <paramref name="ct"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Start();
        try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        var lastSweep = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                await _connections.WaitAsync(ct).ConfigureAwait(false);
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _connections.Release();
                if (ct.IsCancellationRequested) return;
                Log.Debug("mcp-http", $"accept: {ex.Message}");
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(client, ct).ConfigureAwait(false); }
                finally { _connections.Release(); }
            }, CancellationToken.None);
            if (DateTime.UtcNow - lastSweep > TimeSpan.FromMinutes(1))
            {
                lastSweep = DateTime.UtcNow;
                _ = Task.Run(SweepIdleSessionsAsync, CancellationToken.None);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address)) return;
        var stream = client.GetStream();
        HttpRequest request;
        try
        {
            using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                headerCts.CancelAfter(_options.HeaderTimeout);
                request = await HttpWire.ReadHeadersAsync(stream, headerCts.Token).ConfigureAwait(false);
            }
            // Доступ (Host/Origin/токен/путь/метод) — до выделения памяти под тело и его чтения.
            if (CheckAccess(request) is { } denied)
            {
                var extra = denied.Status == 401 ? new[] { new KeyValuePair<string, string>("WWW-Authenticate", "Bearer realm=\"offload\"") } : null;
                Log.Debug("mcp-http", $"Отказ {denied.Status}: {request.Method} {Printable(request.Target)}");
                await WriteErrorAsync(stream, denied.Status, denied.Message, ct, extra).ConfigureAwait(false);
                return;
            }
            using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyCts.CancelAfter(_options.BodyTimeout);
            request = await HttpWire.ReadBodyAsync(stream, request, bodyCts.Token).ConfigureAwait(false);
        }
        catch (HttpWireException ex)
        {
            await TryWriteErrorAsync(stream, ex.Status, ex.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            await HandleRequestAsync(request, stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException || (ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Клиент закрыл соединение или сервер останавливается.
        }
        catch (Exception ex)
        {
            Log.Warn("mcp-http", $"Ошибка обработки запроса {request.Method} {Printable(request.Target)}: {ex.GetType().Name}: {ex.Message}");
            await TryWriteErrorAsync(stream, 500, "Internal server error.", ct).ConfigureAwait(false);
        }
    }

    /// <summary>Проверки доступа: Host/Origin (403), токен (401), путь (404), метод (405). null — запрос допустим.</summary>
    internal (int Status, string Message)? CheckAccess(HttpRequest request)
    {
        var port = Port;
        var host = request.Header("Host");
        if (host is null) return (400, "Missing Host header.");
        if (!IsLocalAuthority(host, port) && !IsExtraHost(host, _extraHosts)) return (403, "Forbidden host.");
        var origin = request.Header("Origin");
        if (origin is not null && !IsLocalOrigin(origin, port)
            && !(origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && IsExtraHost(origin[7..].TrimEnd('/'), _extraHosts)))
            return (403, "Forbidden origin.");
        if (!HasValidToken(request.Header("Authorization"))) return (401, "Missing or invalid bearer token.");
        var path = request.Target.Split('?', 2)[0];
        if (!string.Equals(path, McpHttpOptions.EndpointPath, StringComparison.Ordinal)) return (404, "Not found.");
        if (request.Method is not ("POST" or "GET" or "DELETE")) return (405, "Method not allowed.");
        return null;
    }

    internal static bool IsLocalAuthority(string host, int port)
    {
        var h = host.Trim();
        var p = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(h, "127.0.0.1:" + p, StringComparison.OrdinalIgnoreCase)
               || string.Equals(h, "localhost:" + p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Host из явного списка Mcp.HttpAllowedHosts («хост:порт», точное совпадение без учёта регистра).</summary>
    internal static bool IsExtraHost(string host, IReadOnlyList<string> extra)
    {
        var h = host.Trim();
        return h.Length > 0 && extra.Any(e => string.Equals(e?.Trim(), h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Значения Mcp.HttpAllowedHosts, пригодные для сравнения с Host: «хост:порт» (имя или IPv4, порт 1–65535), без схемы, пути,
    /// пробелов и подстановок. Остальные отбрасываются (null/пустые — тоже).
    /// </summary>
    internal static List<string> ValidExtraHosts(IEnumerable<string?>? values)
    {
        var list = new List<string>();
        foreach (var raw in values ?? [])
        {
            var v = raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(v) || !ExtraHostPattern().IsMatch(v)) continue;
            var port = int.Parse(v[(v.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            if (port is < 1 or > 65535 || list.Contains(v)) continue;
            list.Add(v);
        }
        return list;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?:[0-9]{1,5}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ExtraHostPattern();

    internal static bool IsLocalOrigin(string origin, int port) =>
        origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && IsLocalAuthority(origin[7..].TrimEnd('/'), port);

    private bool HasValidToken(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var presented = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        var expected = CurrentToken();
        return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    /// <summary>Токен с учётом ротации: config.json перечитывается не чаще раза в 2 с (Reload дешёвый — по отметке времени файла).</summary>
    private byte[] CurrentToken()
    {
        if (_options.TokenSource is not { } source) return Volatile.Read(ref _token);
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _tokenCheckedTicks) >= TimeSpan.FromSeconds(2).Ticks)
        {
            Interlocked.Exchange(ref _tokenCheckedTicks, now);
            string? fresh = null;
            try { fresh = source()?.Trim(); }
            catch (Exception ex) { Log.Debug("mcp-http", $"Токен из конфигурации не прочитан: {ex.Message}"); }
            // Пустой или короткий токен в конфиге — не повод открыть доступ: остаётся прежний.
            if (fresh is { Length: >= 16 })
            {
                var bytes = Encoding.UTF8.GetBytes(fresh);
                var current = Volatile.Read(ref _token);
                if (bytes.Length != current.Length || !CryptographicOperations.FixedTimeEquals(bytes, current))
                {
                    Volatile.Write(ref _token, bytes);
                    Log.Info("mcp-http", "Токен MCP по HTTP сменился в config.json — принимается только новый");
                }
            }
        }
        return Volatile.Read(ref _token);
    }

    /// <summary>Путь запроса для журнала (без управляющих символов).</summary>
    private static string Printable(string s) =>
        new([.. s.Take(200).Select(c => c is >= ' ' and < (char)0x7F ? c : '?')]);

    private async Task HandleRequestAsync(HttpRequest request, Stream stream, CancellationToken ct)
    {
        // Доступ уже проверен до чтения тела; повторная проверка — на случай прямого вызова.
        if (CheckAccess(request) is { } denied)
        {
            await WriteErrorAsync(stream, denied.Status, denied.Message, ct).ConfigureAwait(false);
            return;
        }
        switch (request.Method)
        {
            case "POST":
                await HandlePostAsync(request, stream, ct).ConfigureAwait(false);
                break;
            case "GET":
                await HandleGetAsync(request, stream, ct).ConfigureAwait(false);
                break;
            default:
                await HandleDeleteAsync(request, stream, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandlePostAsync(HttpRequest request, Stream stream, CancellationToken ct)
    {
        var contentType = request.Header("Content-Type");
        if (contentType is null || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(stream, 415, "Content-Type must be application/json.", ct).ConfigureAwait(false);
            return;
        }
        var accept = request.Header("Accept");
        if (accept is not null && !accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) && !accept.Contains("*/*", StringComparison.Ordinal))
        {
            await WriteErrorAsync(stream, 406, "Accept must include text/event-stream.", ct).ConfigureAwait(false);
            return;
        }

        JsonRpcMessage? message;
        try
        {
            if (request.Body.Length > 0 && request.Body.AsSpan().TrimStart(" \t\r\n"u8).StartsWith("["u8))
            {
                await WriteErrorAsync(stream, 400, "JSON-RPC batches are not supported.", ct).ConfigureAwait(false);
                return;
            }
            message = JsonSerializer.Deserialize<JsonRpcMessage>(request.Body, McpJsonUtilities.DefaultOptions);
        }
        catch (JsonException)
        {
            message = null;
        }
        if (message is null)
        {
            await WriteErrorAsync(stream, 400, "Invalid JSON-RPC message.", ct).ConfigureAwait(false);
            return;
        }

        var protocol = request.Header("MCP-Protocol-Version");
        var sessionId = request.Header("Mcp-Session-Id");
        Session? session;
        if (sessionId is not null)
        {
            if (!_sessions.TryGetValue(sessionId, out session))
            {
                await WriteErrorAsync(stream, 404, "Unknown or expired session; send initialize again.", ct).ConfigureAwait(false);
                return;
            }
        }
        else if (message is JsonRpcRequest { Method: RequestMethods.Initialize })
        {
            session = await CreateSessionAsync().ConfigureAwait(false);
            if (session is null)
            {
                await WriteErrorAsync(stream, 503, "Too many sessions.", ct).ConfigureAwait(false);
                return;
            }
        }
        else if (Workspace.IsPerRequestProtocol(protocol) || Workspace.IsPerRequestProtocol(MetaProtocolVersion(message)))
        {
            session = await GetSessionlessAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await WriteErrorAsync(stream, 400, "Missing Mcp-Session-Id header (initialize first).", ct).ConfigureAwait(false);
            return;
        }

        session.Touch();
        if (protocol is not null && message is JsonRpcRequest { Method: not RequestMethods.Initialize })
            message.Context = new JsonRpcMessageContext { ProtocolVersion = protocol };

        if (message is not JsonRpcRequest)
        {
            // Уведомление или ответ клиента на запрос сервера: тела ответа нет.
            await session.Transport.HandlePostRequestAsync(message, Stream.Null, ct).ConfigureAwait(false);
            await HttpWire.WriteResponseAsync(stream, 202, null, [], null, ct).ConfigureAwait(false);
            return;
        }

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Content-Type", "text/event-stream"),
            new("Transfer-Encoding", "chunked"),
        };
        if (session.Transport.SessionId is { } id) headers.Add(new("Mcp-Session-Id", id));
        var body = new ChunkedResponseStream(stream, HttpWire.BuildHead(200, headers));
        await using (body.ConfigureAwait(false))
        {
            await session.Transport.HandlePostRequestAsync(message, body, ct).ConfigureAwait(false);
            await body.CompleteAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task HandleGetAsync(HttpRequest request, Stream stream, CancellationToken ct)
    {
        var accept = request.Header("Accept");
        if (accept is null || !accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(stream, 406, "Accept must include text/event-stream.", ct).ConfigureAwait(false);
            return;
        }
        var sessionId = request.Header("Mcp-Session-Id");
        if (sessionId is null)
        {
            await WriteErrorAsync(stream, 405, "The standalone SSE stream needs Mcp-Session-Id (protocol 2026-07-28+ uses subscriptions/listen).", ct,
                [new("Allow", "POST, DELETE")]).ConfigureAwait(false);
            return;
        }
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            await WriteErrorAsync(stream, 404, "Unknown or expired session.", ct).ConfigureAwait(false);
            return;
        }
        session.Touch();
        var body = new ChunkedResponseStream(stream, HttpWire.BuildHead(200,
            [new("Content-Type", "text/event-stream"), new("Transfer-Encoding", "chunked"), new("Mcp-Session-Id", sessionId)]));
        await using (body.ConfigureAwait(false))
        {
            try
            {
                await session.Transport.HandleGetRequestAsync(body, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (!body.Started)
            {
                await WriteErrorAsync(stream, 400, ex.Message, ct).ConfigureAwait(false);
                return;
            }
            await body.CompleteAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task HandleDeleteAsync(HttpRequest request, Stream stream, CancellationToken ct)
    {
        var sessionId = request.Header("Mcp-Session-Id");
        if (sessionId is null || !_sessions.TryRemove(sessionId, out var session))
        {
            await WriteErrorAsync(stream, 404, "Unknown or expired session.", ct).ConfigureAwait(false);
            return;
        }
        await session.DisposeAsync().ConfigureAwait(false);
        Log.Info("mcp-http", $"Сессия {sessionId} закрыта клиентом");
        await HttpWire.WriteResponseAsync(stream, 204, null, [], null, ct).ConfigureAwait(false);
    }

    private static string? MetaProtocolVersion(JsonRpcMessage message) =>
        message is JsonRpcRequest { Params: JsonObject p } && p["_meta"] is JsonObject meta
        && meta[MetaKeys.ProtocolVersion] is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : null;

    private async Task<Session?> CreateSessionAsync()
    {
        if (_sessions.Count >= _options.MaxSessions)
        {
            await SweepIdleSessionsAsync().ConfigureAwait(false);
            if (_sessions.Count >= _options.MaxSessions) return null;
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var session = Session.Create(new StreamableHttpServerTransport(LoggerFactory) { SessionId = id }, NewState(), _options.AllowExec, _cts.Token);
        _sessions[id] = session;
        Log.Info("mcp-http", $"Новая сессия {id} (всего {_sessions.Count})");
        return session;
    }

    private async Task<Session> GetSessionlessAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _sessionless) is { } existing) return existing;
        await _sessionlessLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _sessionless ??= Session.Create(new StreamableHttpServerTransport(LoggerFactory), NewState(), _options.AllowExec, _cts.Token);
        }
        finally
        {
            _sessionlessLock.Release();
        }
    }

    private async Task SweepIdleSessionsAsync()
    {
        var cutoff = DateTime.UtcNow - _options.SessionIdleTimeout;
        foreach (var (id, s) in _sessions)
        {
            if (s.LastUsedUtc >= cutoff || !_sessions.TryRemove(id, out _)) continue;
            Log.Info("mcp-http", $"Сессия {id} закрыта по простою");
            try { await s.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("mcp-http", $"Закрытие сессии {id}: {ex.Message}"); }
        }
    }

    private static readonly ILoggerFactory LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
    {
        b.ClearProviders();
        b.AddProvider(new CoreLogProvider(stderrMinLevel: MsLogLevel.Warning));
        b.SetMinimumLevel(MsLogLevel.Information);
        b.AddFilter("Microsoft", MsLogLevel.Warning);
    });

    private static async Task WriteErrorAsync(Stream stream, int status, string message, CancellationToken ct,
        IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message },
            ["id"] = null,
        });
        await HttpWire.WriteResponseAsync(stream, status, "application/json", body, extra, ct).ConfigureAwait(false);
    }

    private static async Task TryWriteErrorAsync(Stream stream, int status, string message, CancellationToken ct)
    {
        try { await WriteErrorAsync(stream, status, message, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Соединение уже закрыто.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch (SocketException) { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        foreach (var id in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(id, out var s)) await s.DisposeAsync().ConfigureAwait(false);
        }
        if (_sessionless is { } sl) await sl.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _connections.Dispose();
        _sessionlessLock.Dispose();
    }

    /// <summary>Сервер MCP одной сессии: свой контейнер DI (инструменты, SessionState), транспорт и цикл обработки.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly McpServer _server;
        private readonly CancellationTokenSource _cts;
        private readonly Task _run;
        private long _lastUsedTicks = DateTime.UtcNow.Ticks;

        private Session(StreamableHttpServerTransport transport, ServiceProvider services, McpServer server, CancellationTokenSource cts, Task run)
        {
            Transport = transport;
            _services = services;
            _server = server;
            _cts = cts;
            _run = run;
        }

        public StreamableHttpServerTransport Transport { get; }

        public DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

        public static Session Create(StreamableHttpServerTransport transport, SessionState state, bool allowExec, CancellationToken hostToken)
        {
            var services = new ServiceCollection();
            services.AddSingleton(LoggerFactory);
            services.AddLogging();
            var builder = McpEntry.AddOffloadServer(services, state);
            if (!allowExec) AddExecGuard(builder);
            var sp = services.BuildServiceProvider();
            var options = sp.GetRequiredService<IOptions<McpServerOptions>>().Value;
            var server = McpServer.Create(transport, options, LoggerFactory, sp);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            var run = Task.Run(async () =>
            {
                try { await server.RunAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                catch (Exception ex) { Log.Warn("mcp-http", $"Сессия {transport.SessionId ?? "(без сессии)"} завершилась с ошибкой: {ex.Message}"); }
            }, CancellationToken.None);
            return new Session(transport, sp, server, cts, run);
        }

        /// <summary>
        /// Без --allow-exec: инструменты, выполняющие код проекта или пишущие файлы, отклоняются до вызова (isError с объяснением),
        /// полностью отключённые не показываются в tools/list.
        /// </summary>
        private static void AddExecGuard(IMcpServerBuilder builder) =>
            builder.WithRequestFilters(f =>
            {
                f.AddCallToolFilter(next => async (rc, ct) =>
                {
                    if (HttpExecPolicy.Refusal(rc.Params?.Name, rc.Params?.Arguments) is { } refusal)
                    {
                        Log.Info("mcp-http", $"{rc.Params?.Name}: отклонён (HTTP без --allow-exec)");
                        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = refusal }] };
                    }
                    return await next(rc, ct).ConfigureAwait(false);
                });
                f.AddListToolsFilter(next => async (rc, ct) =>
                {
                    var result = await next(rc, ct).ConfigureAwait(false);
                    result.Tools = [.. result.Tools.Where(t => !HttpExecPolicy.AlwaysBlocked.Contains(t.Name))];
                    return result;
                });
            });

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await Transport.DisposeAsync().ConfigureAwait(false);
            try { await _run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
            await _server.DisposeAsync().ConfigureAwait(false);
            await _services.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
