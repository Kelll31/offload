using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Mcp.Http;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Resources;
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
        try { Log.Init("mcp"); Log.ApplyLevel(ConfigStore.Current.Ui.VerboseLog); } catch { }
        try
        {
            NativeMethods.MakeStdHandlesNonInheritable();
            WslPaths.Configure(args, Environment.CurrentDirectory);
            Log.Info("mcp", $"MCP-сервер {AppInfo.Version} запущен (pid {Environment.ProcessId}, папка {Environment.CurrentDirectory}"
                            + (WslPaths.Distro is { } distro ? $", WSL {distro})" : ")"));
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
            // Статистика пишется в фоне — дожидаемся её, чтобы последние вызовы не потерялись.
            await UsageRecorder.FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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

    /// <summary>
    /// Аргумент режима Streamable HTTP: <c>Offload.exe --mcp-http --root &lt;папка&gt; [--root &lt;папка&gt;…] [--port N] [--allow-exec]</c>.
    /// </summary>
    public const string HttpArg = "--mcp-http";

    public const string PortArg = "--port";

    /// <summary>Корень рабочей области HTTP-режима (обязателен, можно несколько): корни клиента по HTTP не принимаются.</summary>
    public const string RootArg = "--root";

    /// <summary>Разрешить по HTTP инструменты, которые выполняют код проекта или пишут файлы (по умолчанию выключены).</summary>
    public const string AllowExecArg = "--allow-exec";

    /// <summary>
    /// Точка входа «Offload.exe --mcp-http --root &lt;папка&gt;»: тот же сервер по Streamable HTTP на 127.0.0.1 (порт —
    /// Mcp.HttpPort из config.json, случайный для пользователя), каждый запрос — с Authorization: Bearer &lt;Mcp.HttpToken&gt;.
    /// Корни рабочей области закреплены аргументами; без <see cref="AllowExecArg"/> инструменты, выполняющие код проекта или
    /// пишущие файлы, отключены. Процесс, как и stdio-режим, конфиг не пишет: токен и порт по IPC создаёт трей.
    /// </summary>
    public static async Task<int> RunHttpAsync(string[] args)
    {
        NativeMethods.AttachParentConsole();
        ConfigStore.ReadOnly = true;
        try { Log.Init("mcp"); Log.ApplyLevel(ConfigStore.Current.Ui.VerboseLog); } catch { }
        try
        {
            if (!TryParsePort(args, out var explicitPort))
            {
                WriteStderr($"Offload: invalid {PortArg} value (expected 1-65535).");
                return 2;
            }
            List<string> roots;
            try
            {
                roots = Workspace.PinRoots(ParseRootArgs(args));
            }
            catch (ArgumentException ex)
            {
                WriteStderr("Offload: " + ex.Message);
                return 2;
            }
            if (roots.Count == 0)
            {
                WriteStderr($"Offload: {HttpArg} needs the project folder: pass {RootArg} <dir> (one or more). Over HTTP the workspace " +
                            "is fixed at startup; roots sent by the client are ignored.");
                return 2;
            }
            var allowExec = args.Any(a => string.Equals(a, AllowExecArg, StringComparison.OrdinalIgnoreCase));
            var creds = await EnsureHttpCredentialsAsync(RequestTokenFromTrayAsync).ConfigureAwait(false);
            if (creds.Error is { } error)
            {
                WriteStderr("Offload: " + error);
                return 2;
            }
            var port = explicitPort ?? creds.Port;
            await using var host = new McpHttpHost(new McpHttpOptions(IPAddress.Loopback, port, creds.Token, roots)
            {
                AllowExec = allowExec,
                TokenSource = () => ConfigStore.Reload().Mcp.HttpToken,
                ExtraHosts = McpHttpHost.ValidExtraHosts(ConfigStore.Reload().Mcp.HttpAllowedHosts),
            });
            try
            {
                host.Start();
            }
            catch (SocketException ex)
            {
                // Другой порт молча не выбираем: клиент настроен на этот, а занявший его процесс мог бы получать токен.
                WriteStderr($"Offload: cannot listen on 127.0.0.1:{port}: {ex.Message}. Another program is using this port - " +
                            "if you did not start it, it may be trying to capture the bearer token. Stop it, or ask the Offload tray app for a " +
                            $"new port (clear mcp.httpPort in config.json and restart Offload), or pass {PortArg} N.");
                Log.Warn("mcp", $"MCP по HTTP: порт 127.0.0.1:{port} занят ({ex.Message})");
                return 1;
            }
            StartBackgroundCleanup();
            Log.Info("mcp", $"MCP по HTTP {AppInfo.Version} запущен (pid {Environment.ProcessId}, {host.Endpoint}, корни: {string.Join("; ", roots)}, " +
                            $"выполнение кода {(allowExec ? "разрешено" : "запрещено")})");
            WriteStderr($"Offload MCP (Streamable HTTP) is listening on {host.Endpoint} (use 127.0.0.1, not localhost). Clients must send " +
                        $"'Authorization: Bearer <mcp.httpToken from {AppPaths.ConfigFile}>'. Workspace: {string.Join(", ", roots)}. " +
                        (allowExec
                            ? "Code-executing and writing tools are ENABLED (--allow-exec). "
                            : $"Code-executing and writing tools are disabled (pass {AllowExecArg} to enable). ") +
                        "Press Ctrl+C to stop.");

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            try { await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await UsageRecorder.FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Log.Info("mcp", "MCP по HTTP остановлен");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("mcp", "MCP по HTTP аварийно завершён", ex);
            WriteStderr("Offload MCP HTTP server failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary><c>--port N</c> (1–65535); без аргумента — null (порт из Mcp.HttpPort).</summary>
    internal static bool TryParsePort(IReadOnlyList<string> args, out int? port)
    {
        port = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], PortArg, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535)
            {
                port = p;
                return true;
            }
            return false;
        }
        return true;
    }

    /// <summary>Значения всех <c>--root &lt;папка&gt;</c> (без проверки; <c>--root</c> без значения — пустая строка → ошибка проверки).</summary>
    internal static List<string> ParseRootArgs(IReadOnlyList<string> args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], RootArg, StringComparison.OrdinalIgnoreCase)) continue;
            var value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
            list.Add(value);
        }
        return list;
    }

    /// <summary>Учётные данные HTTP-режима или понятная причина, почему их нет (английский — печатается в терминал).</summary>
    internal sealed record HttpCredentials(string Token, int Port, string? Error)
    {
        public static HttpCredentials Fail(string error) => new("", 0, error);
    }

    /// <summary>
    /// Токен и порт HTTP-режима из конфига; если чего-то нет — попросить трей создать (<paramref name="askTray"/>: ответ трея,
    /// null — трей не ответил) и перечитать конфиг. Трей старой версии команду не знает и отвечает отказом — это отдельная
    /// причина (обновить или перезапустить Offload), а не «трей не запущен».
    /// </summary>
    internal static async Task<HttpCredentials> EnsureHttpCredentialsAsync(Func<Task<IpcResponse?>> askTray)
    {
        if (Read() is { } ready) return ready;
        var resp = await askTray().ConfigureAwait(false);
        if (resp is null)
            return HttpCredentials.Fail("the HTTP bearer token and port (mcp.httpToken, mcp.httpPort in config.json) do not exist yet and the " +
                                        "Offload tray app is not running to create them. Start Offload once and run this command again.");
        if (!resp.Ok)
        {
            Log.Warn("mcp", $"Трей отклонил {IpcCommands.EnsureMcpHttpToken}: {resp.Message}");
            return HttpCredentials.Fail("the running Offload tray app does not know how to create the HTTP token and port (it is an older " +
                                        "version). Update Offload or restart it (exit it from the tray icon and start it again), then run this command again.");
        }
        return Read() ?? HttpCredentials.Fail(
            "the Offload tray app answered but mcp.httpToken / mcp.httpPort are still missing in config.json (an older tray creates only the " +
            "token). Update or restart Offload, then run this command again.");

        static HttpCredentials? Read()
        {
            var mcp = ConfigStore.Reload().Mcp;
            var token = mcp.HttpToken?.Trim();
            return string.IsNullOrEmpty(token) || !McpSettings.IsValidHttpPort(mcp.HttpPort) ? null : new HttpCredentials(token, mcp.HttpPort, null);
        }
    }

    private static Task<IpcResponse?> RequestTokenFromTrayAsync() =>
        IpcClient.SendAsync(new IpcRequest(IpcCommands.EnsureMcpHttpToken), TimeSpan.FromSeconds(5));

    private static void WriteStderr(string text)
    {
        try { Console.Error.WriteLine(text); } catch { }
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
            .WithTools<OffloadTools>()
            .WithPrompts<OffloadPrompts>()
            .WithResources<OffloadResources>()
            .WithCompleteHandler((rc, ct) => OffloadCompletions.HandleAsync(rc, state, ct));
    }

    /// <summary>Удаление старых задач правки и их песочниц — в фоне, не задерживая initialize.</summary>
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
                var orphans = GitSandbox.CleanupOrphans();
                if (orphans > 0) Log.Info("mcp", $"Удалено брошенных песочниц: {orphans}");
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
