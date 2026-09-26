using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Core.Ipc;

/// <summary>Команды, которые MCP-процесс и повторные запуски exe отправляют работающему трею.</summary>
public static class IpcCommands
{
    public const string Ping = "ping";
    public const string Show = "show";
    public const string Status = "status";
    public const string StartServer = "start-server";
    public const string StopServer = "stop-server";
    public const string RestartServer = "restart-server";
    public const string OpenSetup = "open-setup";

    /// <summary>Записать UsageRecord (Args["record"] — JSON). MCP-процессы передают статистику трею.</summary>
    public const string RecordUsage = "record-usage";

    /// <summary>
    /// Создать Mcp.HttpToken и случайный Mcp.HttpPort, если их ещё нет (MCP-процесс в режиме --mcp-http конфиг не пишет).
    /// Токен в ответе не передаётся — процесс перечитывает config.json.
    /// </summary>
    public const string EnsureMcpHttpToken = "ensure-mcp-http-token";

    /// <summary>
    /// Заменить Mcp.HttpToken новым (отзыв утёкшего токена). Работающий --mcp-http подхватывает новый токен из config.json
    /// за несколько секунд; клиентам нужно передать новый. Токен в ответе не передаётся.
    /// </summary>
    public const string RotateMcpHttpToken = "rotate-mcp-http-token";

    /// <summary>
    /// Выполнить фоновую задачу агента в трее (Args["spec"] — JSON описания задачи). Трей заново проверяет описание и запускает
    /// только движок задач агента; старый трей без этой команды отвечает отказом — MCP выполняет задачу сам.
    /// </summary>
    public const string JobStart = "job-start";
}

/// <param name="V">Мажорная версия протокола отправителя (JSON «v»). Нет поля — клиент до введения версий, это v1.</param>
public sealed record IpcRequest(string Command, Dictionary<string, string>? Args = null, int? V = null);

/// <param name="V">Версия протокола трея, ответившего на запрос.</param>
public sealed record IpcResponse(bool Ok, string? Message = null, Dictionary<string, string>? Data = null, int? V = null);

/// <summary>
/// Версия протокола IPC. Меняется (мажорная) только при несовместимом изменении формата команд: старый MCP-процесс после
/// обновления трея получает понятный отказ, а не молча неверный ответ. Добавление команд и полей версию не меняет.
/// </summary>
public static class IpcProtocol
{
    /// <summary>Текущая мажорная версия, которую отправляет клиент и понимает сервер.</summary>
    public const int Version = 1;

    /// <summary>Версии, которые понимает этот трей.</summary>
    public const int MinSupported = 1;

    /// <summary>Версия запроса: отсутствие поля «v» — клиент до введения версий (v1).</summary>
    public static int VersionOf(IpcRequest request) => request.V ?? 1;

    /// <summary>Отказ для неподдерживаемой версии или null, если запрос можно обрабатывать.</summary>
    public static IpcResponse? CheckVersion(IpcRequest request)
    {
        var v = VersionOf(request);
        if (v is >= MinSupported and <= Version) return null;
        return new IpcResponse(false,
            L.F("Неподдерживаемая версия протокола IPC: {0} (трей Offload понимает {1}–{2}). Перезапустите IDE или обновите Offload, чтобы версии совпали.",
                v, MinSupported, Version),
            new Dictionary<string, string> { ["error"] = "unsupported-version", ["supported"] = $"{MinSupported}-{Version}" },
            Version);
    }
}

public static class IpcNames
{
    /// <summary>Имя канала уникально для пользователя и корня данных (разные OFFLOAD_HOME не пересекаются).</summary>
    public static string PipeName
    {
        get
        {
            var key = (Environment.UserDomainName + "\\" + Environment.UserName + "|" + AppPaths.DataDir).ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            return $"Offload.{hash}";
        }
    }

    public static string MutexName => $"Local\\{PipeName}.instance";

    /// <summary>Фиксированное имя мьютекса для установщика (Inno Setup AppMutex).</summary>
    public const string InstallerMutexName = "Offload.Running";
}

/// <summary>Клиент именованного канала (одна строка JSON запрос → одна строка JSON ответ).</summary>
public static class IpcClient
{
    public static async Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", IpcNames.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cts.Token);
            using var reader = new StreamReader(pipe, FileUtil.Utf8NoBom, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, FileUtil.Utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true };
            var versioned = request.V is null ? request with { V = IpcProtocol.Version } : request;
            await writer.WriteLineAsync(JsonSerializer.Serialize(versioned, Json.Compact).AsMemory(), cts.Token);
            var line = await reader.ReadLineAsync(cts.Token);
            return line is null ? null : JsonSerializer.Deserialize<IpcResponse>(line, Json.Compact);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug("ipc", $"IPC '{request.Command}' не выполнен: {ex.Message}");
            return null;
        }
    }

    /// <summary>Запущен ли трей (есть ли процесс, владеющий мьютексом экземпляра).</summary>
    public static bool IsTrayRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(IpcNames.MutexName, out var m))
            {
                m.Dispose();
                return true;
            }
        }
        catch
        {
            // Нет доступа — считаем, что не запущен.
        }
        return false;
    }
}

/// <summary>Сервер именованного канала внутри трей-приложения.</summary>
public sealed class IpcServer : IDisposable
{
    private readonly Func<IpcRequest, Task<IpcResponse>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public IpcServer(Func<IpcRequest, Task<IpcResponse>> handler) => _handler = handler;

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(IpcNames.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(connected, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("ipc", $"Ошибка сервера IPC: {ex.Message}");
                await Task.Delay(500, CancellationToken.None);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, FileUtil.Utf8NoBom, false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, FileUtil.Utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct);
                if (line is null) return;
                var req = JsonSerializer.Deserialize<IpcRequest>(line, Json.Compact);
                IpcResponse resp;
                if (req is null)
                {
                    resp = new IpcResponse(false, L.T("Пустой запрос"));
                }
                else if (IpcProtocol.CheckVersion(req) is { } rejected)
                {
                    Log.Warn("ipc", $"Запрос '{req.Command}' с неподдерживаемой версией протокола {IpcProtocol.VersionOf(req)} отклонён");
                    resp = rejected;
                }
                else
                {
                    try { resp = await _handler(req); }
                    catch (Exception ex) { resp = new IpcResponse(false, ex.Message); }
                }
                resp = resp with { V = IpcProtocol.Version };
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp, Json.Compact).AsMemory(), ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                Log.Debug("ipc", $"Соединение IPC прервано: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
