using System.Diagnostics;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Logging;
using Offload.Llama;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Перед обращением к модели: сервер готов → ок; загружается → ждём; не запущен → просим трей (IPC) или запускаем трей
/// («Offload.exe --background»), затем опрашиваем /health раз в секунду с уведомлениями о прогрессе.
/// </summary>
internal static class ServerEnsurer
{
    public const string NotSetUpMessage =
        "Offload is not set up yet: open the Offload tray app and finish the setup wizard (it installs llama.cpp and a local model). Until then, do this task yourself.";

    public static async Task<LlamaClient> EnsureAsync(AppConfig cfg, SessionState state, ProgressReporter progress, CancellationToken ct)
    {
        var client = LlamaClient.FromConfig(cfg);
        var health = await client.GetHealthAsync(ct).ConfigureAwait(false);
        if (health == HealthState.Ready) return client;

        if (!cfg.SetupCompleted || cfg.ActiveModel() is null)
        {
            // Сервер мог быть запущен вручную (портативный режим) — тогда он уже ответил бы Ready.
            throw new ToolException(NotSetUpMessage);
        }

        await state.EnsureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            health = await client.GetHealthAsync(ct).ConfigureAwait(false);
            if (health == HealthState.Ready) return client;

            var timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.Mcp.ServerStartTimeoutSeconds, 10, 1800));
            var sw = Stopwatch.StartNew();
            Task<IpcResponse?>? startTask = null;

            if (health == HealthState.Down)
            {
                if (!IpcClient.IsTrayRunning())
                {
                    progress.Report("Starting Offload tray app…");
                    if (!LaunchTray(state))
                        throw new ToolException(
                            "The local model server is not running and the Offload tray app could not be started. Start Offload from the Start menu, then retry.");
                    // Ждём, пока трей поднимет IPC (он сам может запустить сервер при старте).
                    while (!IpcClient.IsTrayRunning() && sw.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        if (await client.GetHealthAsync(ct).ConfigureAwait(false) == HealthState.Ready) return client;
                    }
                }
                progress.Report("Starting local model…");
                startTask = IpcClient.SendAsync(new IpcRequest(IpcCommands.StartServer), timeout, ct);
            }

            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                health = await client.GetHealthAsync(ct).ConfigureAwait(false);
                if (health == HealthState.Ready)
                {
                    progress.Report($"Local model ready ({sw.Elapsed.TotalSeconds:0} s)");
                    return client;
                }
                if (startTask is { IsCompleted: true })
                {
                    var resp = await startTask.ConfigureAwait(false);
                    startTask = null;
                    if (resp is null)
                    {
                        Log.Warn("mcp", "Трей не ответил на start-server");
                    }
                    else if (!resp.Ok)
                    {
                        throw new ToolException(
                            $"The Offload tray app could not start the local model: {resp.Message}. Open Offload to see details; meanwhile do this task yourself.");
                    }
                }
                progress.Report(health == HealthState.Loading
                    ? $"Loading local model… {sw.Elapsed.TotalSeconds:0} s"
                    : $"Starting local model… {sw.Elapsed.TotalSeconds:0} s");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            throw new ToolException(
                $"The local model did not become ready within {timeout.TotalSeconds:0} s. Check the Offload tray app (model loading or out of memory); do this task yourself for now.");
        }
        finally
        {
            state.EnsureLock.Release();
        }
    }

    /// <summary>Запуск трея без окна. Дочерний процесс не получает stdio MCP (всё перенаправлено и сразу закрыто).</summary>
    private static bool LaunchTray(SessionState state)
    {
        if (state.TrayLauncherOverride is { } over) return over();
        var exe = AppPaths.ExecutablePath;
        // Защита: запускаем только настоящий Offload.exe (не тестовый хост и не чужой процесс).
        if (!File.Exists(exe) || !Path.GetFileNameWithoutExtension(exe).Equals(AppInfo.Name, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("mcp", $"Не запускаю трей: неподходящий исполняемый файл {exe}");
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--background");
            // Трей не должен наследовать переменные IDE, влияющие на расположение данных, — кроме OFFLOAD_HOME.
            psi.Environment.Remove(Workspace.ClaudeProjectDirEnv);
            using var p = Process.Start(psi);
            if (p is null) return false;
            try { p.StandardInput.Close(); } catch { }
            try { p.StandardOutput.Close(); } catch { }
            try { p.StandardError.Close(); } catch { }
            Log.Info("mcp", $"Запущен трей Offload (pid {p.Id})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("mcp", "Не удалось запустить трей", ex);
            return false;
        }
    }
}
