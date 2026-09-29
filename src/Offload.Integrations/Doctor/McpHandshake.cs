using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Core.Processes;

namespace Offload.Integrations;

/// <summary>Итог рукопожатия с MCP-сервером: initialize + tools/list.</summary>
public sealed record HandshakeResult(
    bool Ok,
    string? ServerName,
    string? ServerVersion,
    string? ProtocolVersion,
    int ToolCount,
    TimeSpan Elapsed,
    string? Error,
    int NonJsonLines,
    FailureKind Kind = FailureKind.None);

/// <summary>
/// Минимальный MCP-клиент stdio (JSON-RPC 2.0, по сообщению в строке) — как это делают IDE и scripts/mcp-smoke.mjs:
/// initialize → notifications/initialized → tools/list (с постраничной выдачей). Строки stdout, которые не являются JSON,
/// считаются (IDE на них ломаются). Запросы сервера к клиенту (roots/list и др.) получают пустой ответ.
/// </summary>
internal static class McpHandshake
{
    public const string ProtocolVersion = "2025-06-18";
    public const string ClientName = "offload-doctor";

    private const int MaxPages = 20;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Рукопожатие поверх готовых потоков (тесты подставляют сервер в памяти).</summary>
    public static async Task<HandshakeResult> RunAsync(Stream toServer, Stream fromServer, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var session = new Session(toServer, fromServer);
        try
        {
            var init = await session.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = ClientName, ["version"] = AppInfo.Version },
            }, cts.Token);
            if (ErrorText(init) is { } initError)
                return Fail(L.F("сервер отклонил initialize: {0}", initError), sw, session);
            var result = init["result"] as JsonObject;
            var info = result?["serverInfo"] as JsonObject;
            var name = Str(info?["name"]);
            var version = Str(info?["version"]);
            var protocol = Str(result?["protocolVersion"]);

            await session.NotifyAsync("notifications/initialized", cts.Token);

            var tools = 0;
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var p = new JsonObject();
                if (cursor is not null) p["cursor"] = cursor;
                var list = await session.RequestAsync("tools/list", p, cts.Token);
                if (ErrorText(list) is { } listError)
                    return Fail(L.F("сервер отклонил tools/list: {0}", listError), sw, session) with { ServerName = name, ServerVersion = version };
                tools += (list["result"]?["tools"] as JsonArray)?.Count ?? 0;
                cursor = Str(list["result"]?["nextCursor"]);
                if (string.IsNullOrEmpty(cursor)) break;
            }
            return new HandshakeResult(true, name, version, protocol, tools, sw.Elapsed, null, session.NonJsonLines);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(L.F("сервер не ответил за {0} с", (int)timeout.TotalSeconds), sw, session, FailureKind.Timeout);
        }
        catch (EndOfStreamException)
        {
            return Fail(L.T("сервер закрыл соединение, не ответив"), sw, session);
        }
        catch (IOException ex)
        {
            return Fail(L.F("обмен с сервером прерван: {0}", ex.Message), sw, session);
        }
    }

    /// <summary>
    /// Запустить exe (без оболочки, аргументы списком) и пройти рукопожатие. Процесс привязан к общему JobObject, после
    /// проверки stdin закрывается (сервер завершается сам), иначе через 3 с процесс завершается принудительно.
    /// Сам метод не решает, можно ли запускать команду: вызывающий обязан пропускать сюда только проверенный путь Offload
    /// (<see cref="IntegrationDoctor.MayLaunch"/>). Сетевые пути отклоняются и здесь — на всякий случай.
    /// </summary>
    public static async Task<HandshakeResult> RunProcessAsync(string command, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (Editing.CommandPath.IsUnc(command))
            return new HandshakeResult(false, null, null, null, 0, sw.Elapsed, L.F("сетевой путь не запускается: {0}", command), 0, FailureKind.ExeBlocked);
        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = Path.GetDirectoryName(command) is { Length: > 0 } dir && Directory.Exists(dir) ? dir : Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                if (stderr.Length < 4000) stderr.AppendLine(e.Data);
            }
        };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new HandshakeResult(false, null, null, null, 0, sw.Elapsed, L.F("не удалось запустить {0}: {1}", command, ex.Message), 0, LaunchFailureKind(ex));
        }
        JobObject.Shared?.TryAdd(process);
        process.BeginErrorReadLine();
        try
        {
            var r = await RunAsync(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, timeout, ct);
            if (!r.Ok)
            {
                string tail;
                lock (stderr) tail = stderr.ToString().Trim();
                var exited = process.HasExited ? L.F("процесс завершился с кодом {0}", SafeExitCode(process)) : null;
                var detail = string.Join("; ", new[] { r.Error, exited, tail.Length == 0 ? null : Last(tail, 400) }.Where(s => !string.IsNullOrEmpty(s)));
                r = r with { Error = detail };
            }
            return r with { Elapsed = sw.Elapsed };
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Процесс уже завершился.
            }
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(exit.Token);
            }
            catch (OperationCanceledException)
            {
                ProcessRunner.KillTree(process);
            }
        }
    }

    /// <summary>Коды Win32: 5 — отказано в доступе, 225/226 — антивирус, 740 — нужны права, 1260 — запрещено политикой.</summary>
    internal static FailureKind LaunchFailureKind(Exception ex) => ex switch
    {
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 or 225 or 226 or 740 or 1260 } => FailureKind.ExeBlocked,
        System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 } => FailureKind.ExeMissing,
        UnauthorizedAccessException => FailureKind.ExeBlocked,
        _ => FailureKind.StartFailed,
    };

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return -1; }
    }

    private static string Last(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private static HandshakeResult Fail(string error, Stopwatch sw, Session session, FailureKind kind = FailureKind.StartFailed) =>
        new(false, null, null, null, 0, sw.Elapsed, error, session.NonJsonLines, kind);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? ErrorText(JsonObject response) =>
        response["error"] is JsonObject e ? Str(e["message"]) ?? e.ToJsonString() : null;

    /// <summary>Сессия JSON-RPC поверх пары потоков.</summary>
    private sealed class Session(Stream toServer, Stream fromServer) : IDisposable
    {
        private readonly StreamReader _reader = new(fromServer, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        private int _nextId;

        public int NonJsonLines { get; private set; }

        public void Dispose() => _reader.Dispose();

        public async Task<JsonObject> RequestAsync(string method, JsonObject @params, CancellationToken ct)
        {
            var id = ++_nextId;
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }, ct);
            while (true)
            {
                // Чтение из канала процесса может не реагировать на отмену — ждём с таймаутом (после него сессия не используется).
                var line = await _reader.ReadLineAsync(ct).AsTask().WaitAsync(ct) ?? throw new EndOfStreamException();
                if (line.Trim().Length == 0) continue;
                JsonObject? msg;
                try
                {
                    msg = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    msg = null;
                }
                if (msg is null)
                {
                    NonJsonLines++;
                    continue;
                }
                var hasMethod = msg["method"] is not null;
                if (hasMethod)
                {
                    // Запрос сервера к клиенту — отвечаем, чтобы он не ждал; уведомления пропускаем.
                    if (msg["id"] is { } reqId) await AnswerServerRequestAsync(reqId, Str(msg["method"]), ct);
                    continue;
                }
                if (msg["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id) return msg;
            }
        }

        public Task NotifyAsync(string method, CancellationToken ct) =>
            WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }, ct);

        private Task AnswerServerRequestAsync(JsonNode id, string? method, CancellationToken ct)
        {
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            if (method == "roots/list") response["result"] = new JsonObject { ["roots"] = new JsonArray() };
            else if (method == "ping") response["result"] = new JsonObject();
            else response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
            return WriteAsync(response, ct);
        }

        private async Task WriteAsync(JsonObject message, CancellationToken ct)
        {
            var bytes = Utf8.GetBytes(message.ToJsonString() + "\n");
            await toServer.WriteAsync(bytes, ct);
            await toServer.FlushAsync(ct);
        }
    }
}
