using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Mcp.Infrastructure;
using CoreLogLevel = Offload.Core.Logging.LogLevel;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Offload.Mcp;

/// <summary>
/// Точка входа режима «Offload.exe --mcp»: stdio MCP-сервер для Claude Code и других IDE.
/// stdout зарезервирован под протокол — весь журнал идёт в logs\mcp.log (и stderr для предупреждений).
/// Старт дешёвый: без сети, без запуска процессов, без блокировок единственного экземпляра.
/// </summary>
public static class McpEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        // До любого чтения конфигурации: MCP-процесс никогда не пишет config.json.
        ConfigStore.ReadOnly = true;
        try { Log.Init("mcp"); } catch { }
        try
        {
            NativeMethods.MakeStdHandlesNonInheritable();
            Log.Info("mcp", $"MCP-сервер {AppInfo.Version} запущен (pid {Environment.ProcessId}, папка {Environment.CurrentDirectory})");
            StartBackgroundCleanup();

            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ApplicationName = "Offload.Mcp",
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new CoreLogProvider(stderrMinLevel: MsLogLevel.Warning));
            builder.Logging.SetMinimumLevel(MsLogLevel.Information);
            builder.Logging.AddFilter("Microsoft", MsLogLevel.Warning);

            AddOffloadServer(builder.Services).WithStdioServerTransport();
            using var host = builder.Build();
            await host.RunAsync().ConfigureAwait(false);
            Log.Info("mcp", "MCP-сервер завершён (stdin закрыт)");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("mcp", "MCP-сервер аварийно завершён", ex);
            try { Console.Error.WriteLine("Offload MCP server failed: " + ex.Message); } catch { }
            return 1;
        }
    }

    /// <summary>Регистрация сервера и инструментов (общая для stdio и тестов со stream-транспортом).</summary>
    internal static IMcpServerBuilder AddOffloadServer(IServiceCollection services, SessionState? state = null)
    {
        state ??= new SessionState();
        services.AddSingleton(state);
        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = AppInfo.McpServerId, Title = "Offload", Version = AppInfo.Version };
                AppConfig? cfg = null;
                try { cfg = ConfigStore.Reload(); } catch (Exception ex) { Log.Warn("mcp", $"Конфигурация не прочитана: {ex.Message}"); }
                o.ServerInstructions = ServerInstructions.Build(cfg);
#pragma warning disable MCP9005 // roots устарели в 2026-07-28, но Claude Code ими пользуется
                o.Handlers.NotificationHandlers =
                [
                    new(NotificationMethods.RootsListChangedNotification, (_, _) =>
                    {
                        state.InvalidateRoots();
                        return ValueTask.CompletedTask;
                    }),
                ];
#pragma warning restore MCP9005
            })
            .WithTools<OffloadTools>();
    }

    /// <summary>Удаление старых задач правки — в фоне, не задерживая initialize.</summary>
    private static void StartBackgroundCleanup()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var days = ConfigStore.Reload().Mcp.JobRetentionDays;
                var removed = JobStore.CleanupOld(days);
                if (removed > 0) Log.Info("mcp", $"Удалено старых задач: {removed}");
            }
            catch (Exception ex)
            {
                Log.Debug("mcp", $"Очистка задач: {ex.Message}");
            }
        });
    }
}

/// <summary>Мост Microsoft.Extensions.Logging → файловый журнал Core (и stderr для предупреждений). Никогда не stdout.</summary>
internal sealed class CoreLogProvider(MsLogLevel stderrMinLevel) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CoreLogger(categoryName, stderrMinLevel);

    public void Dispose()
    {
    }

    private sealed class CoreLogger(string category, MsLogLevel stderrMin) : ILogger
    {
        private readonly string _short = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MsLogLevel logLevel) => logLevel != MsLogLevel.None;

        public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message;
            try { message = formatter(state, exception); }
            catch { return; }
            if (exception is not null) message += $" | {exception.GetType().Name}: {exception.Message}";
            var level = logLevel switch
            {
                MsLogLevel.Trace or MsLogLevel.Debug => CoreLogLevel.Debug,
                MsLogLevel.Information => CoreLogLevel.Info,
                MsLogLevel.Warning => CoreLogLevel.Warn,
                _ => CoreLogLevel.Error,
            };
            Core.Logging.Log.Write(level, "sdk." + _short, message);
            if (logLevel >= stderrMin)
            {
                try { Console.Error.WriteLine($"[{logLevel}] {_short}: {message}"); } catch { }
            }
        }
    }
}
