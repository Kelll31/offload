using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Core.Usage;
using Offload.Llama;

namespace Offload.App.Services;

/// <summary>Команды, понятные только трею Offload (дополнение к IpcCommands).</summary>
internal static class AppIpcCommands
{
    /// <summary>Завершить трей (используется при удалении программы).</summary>
    public const string Exit = "exit";
}

/// <summary>
/// Обработка IPC-запросов в трее. Вызывается из фоновых потоков IpcServer;
/// действия с окнами передаются в поток интерфейса через IAppShell.
/// </summary>
/// <param name="jobs">Фоновые задачи агента в трее (null — трей их не принимает: MCP выполнит задачу сам).</param>
internal sealed class IpcRequestHandler(IAppShell shell, BackgroundJobHost? jobs = null)
{
    public async Task<IpcResponse> HandleAsync(IpcRequest request)
    {
        // IpcServer уже отклоняет чужие версии; проверка здесь — на случай прямого вызова обработчика.
        if (IpcProtocol.CheckVersion(request) is { } rejected) return rejected;
        var server = shell.Server;
        switch (request.Command)
        {
            case IpcCommands.Ping:
                return new IpcResponse(true, "pong");

            case IpcCommands.Show:
                shell.PostToUi(() => shell.ShowMainWindow());
                return new IpcResponse(true);

            case IpcCommands.OpenSetup:
                shell.PostToUi(shell.ShowSetupWizard);
                return new IpcResponse(true);

            case IpcCommands.Status:
                // Опрос состояния — не обращение к модели: активность отмечает только запись статистики.
                return new IpcResponse(true, server.Summary, server.StatusData());

            case IpcCommands.StartServer:
            {
                // Роль (fast/embed/rerank) — необязательный аргумент: старые клиенты его не передают и получают основной сервер.
                if (ModelRoleConfig.TryParse(request.Args?.GetValueOrDefault(IpcRoleArgs.Role), out var role) && role != ModelRole.Quality)
                    return await StartRoleAsync(server, role).ConfigureAwait(false);
                Log.Info("ipc", "Запрос запуска сервера из IDE");
                var ok = await server.EnsureRunningAsync().ConfigureAwait(false);
                var data = server.StatusData();
                data[IpcRoleArgs.Role] = ModelRole.Quality.Key();
                return new IpcResponse(ok, ok ? L.T("Сервер работает") : server.LastError ?? server.Summary, data);
            }

            case IpcCommands.StopServer:
                await server.StopAsync().ConfigureAwait(false);
                return new IpcResponse(true, server.Summary, server.StatusData());

            case IpcCommands.RestartServer:
            {
                var ok = await server.RestartAsync().ConfigureAwait(false);
                return new IpcResponse(ok, ok ? L.T("Сервер перезапущен") : server.LastError ?? server.Summary, server.StatusData());
            }

            case IpcCommands.RecordUsage:
            {
                // Запись статистики от MCP-процесса: трей дописывает её в свой usage.jsonl.
                var json = request.Args?.GetValueOrDefault("record");
                var record = string.IsNullOrWhiteSpace(json) ? null : UsageLog.Parse(json);
                if (record is null) return new IpcResponse(false, L.T("Некорректная запись статистики"));
                UsageLog.AppendLocal(record);
                server.MarkActivity();
                shell.ReportUsageRecorded(record);
                return new IpcResponse(true);
            }

            case IpcCommands.JobStart:
                // Только движок задач агента по описанию, которое трей проверяет заново; канал — только текущего пользователя.
                if (jobs is null || shell.IsExiting) return new IpcResponse(false, L.T("Трей не принимает фоновые задачи"));
                return jobs.Start(request);

            case IpcCommands.EnsureMcpHttpToken:
            {
                var created = false;
                ConfigStore.Update(c => created = c.Mcp.EnsureHttpCredentials(CanListenOnLoopback));
                if (created) Log.Info("ipc", "Созданы токен и порт MCP по HTTP (Mcp.HttpToken, Mcp.HttpPort)");
                return new IpcResponse(true);
            }

            case IpcCommands.RotateMcpHttpToken:
                ConfigStore.Update(c => c.Mcp.RotateHttpToken(CanListenOnLoopback));
                Log.Info("ipc", "Токен MCP по HTTP заменён новым (Mcp.HttpToken)");
                return new IpcResponse(true);

            case AppIpcCommands.Exit:
                shell.PostToUi(() => _ = shell.ExitAsync());
                return new IpcResponse(true);

            default:
                return new IpcResponse(false, L.F("Неизвестная команда: {0}", request.Command));
        }
    }

    /// <summary>Порт на 127.0.0.1 сейчас можно занять (проверка кандидата для Mcp.HttpPort: не занят и не зарезервирован Hyper-V).</summary>
    private static bool CanListenOnLoopback(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>Запуск вспомогательного сервера роли. Data["role"] в ответе — признак того, что трей знает роли.</summary>
    private static async Task<IpcResponse> StartRoleAsync(ServerController server, ModelRole role)
    {
        Log.Info("ipc", $"Запрос запуска сервера роли {role.Key()} из IDE");
        server.MarkActivity();
        var result = await server.Aux.EnsureRunningAsync(role).ConfigureAwait(false);
        var data = new Dictionary<string, string> { [IpcRoleArgs.Role] = role.Key() };
        if (result.BaseUrl is { } url) data[IpcRoleArgs.BaseUrl] = url;
        if (result.NotAssigned) data[IpcRoleArgs.Error] = IpcRoleArgs.RoleNotAssigned;
        return new IpcResponse(result.Ok, result.Ok ? L.T("Сервер роли работает") : result.Message, data);
    }
}
