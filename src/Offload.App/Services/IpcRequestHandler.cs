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
internal sealed class IpcRequestHandler(IAppShell shell)
{
    public async Task<IpcResponse> HandleAsync(IpcRequest request)
    {
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
                server.MarkActivity();
                return new IpcResponse(true, server.Summary, server.StatusData());

            case IpcCommands.StartServer:
            {
                Log.Info("ipc", "Запрос запуска сервера из IDE");
                var ok = await server.EnsureRunningAsync().ConfigureAwait(false);
                return new IpcResponse(ok, ok ? L.T("Сервер работает") : server.LastError ?? server.Summary, server.StatusData());
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
                return new IpcResponse(true);
            }

            case AppIpcCommands.Exit:
                shell.PostToUi(() => _ = shell.ExitAsync());
                return new IpcResponse(true);

            default:
                return new IpcResponse(false, L.F("Неизвестная команда: {0}", request.Command));
        }
    }
}
