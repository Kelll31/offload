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

    /// <summary>
    /// Сервер роли по маршруту: вспомогательный (fast…) — если он запускается; при любой неудаче (трей старой версии,
    /// модель не загрузилась, роль снята) — основной сервер. Возвращает клиента и фактическую роль.
    /// </summary>
    public static async Task<(LlamaClient Client, ModelRole Role)> EnsureRoutedAsync(AppConfig cfg, SessionState state, ProgressReporter progress,
        ModelRole role, CancellationToken ct)
    {
        if (role != ModelRole.Quality)
        {
            try
            {
                return (await EnsureAsync(cfg, state, progress, ct, role).ConfigureAwait(false), role);
            }
            catch (ToolException ex)
            {
                Log.Warn("mcp", $"Сервер роли {role.Key()} недоступен, используется основная модель: {ex.Message}");
                progress.Report($"The {role.Key()} model is unavailable; using the main local model");
            }
        }
        return (await EnsureAsync(cfg, state, progress, ct).ConfigureAwait(false), ModelRole.Quality);
    }

    /// <summary>Сервер роли <paramref name="role"/> готов (при необходимости запускается через трей) — клиент к нему.</summary>
    public static async Task<LlamaClient> EnsureAsync(AppConfig cfg, SessionState state, ProgressReporter progress, CancellationToken ct,
        ModelRole role = ModelRole.Quality)
    {
        if (role != ModelRole.Quality) return await EnsureAuxAsync(cfg, state, progress, role, ct).ConfigureAwait(false);
        if (cfg.IsRemote()) return await EnsureRemoteAsync(cfg, progress, ct).ConfigureAwait(false);
        var client = LlamaClient.ForRole(cfg, role);
        var health = await client.GetHealthAsync(ct).ConfigureAwait(false);
        if (health == HealthState.Ready) return await VerifiedAsync(client, role, fromTray: false, ct).ConfigureAwait(false);

        if (!cfg.SetupCompleted || cfg.ActiveModel() is null)
        {
            // Сервер мог быть запущен вручную (портативный режим) — тогда он уже ответил бы Ready.
            throw new ToolException(NotSetUpMessage);
        }

        await state.EnsureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            health = await client.GetHealthAsync(ct).ConfigureAwait(false);
            if (health == HealthState.Ready) return await VerifiedAsync(client, role, fromTray: false, ct).ConfigureAwait(false);

            var timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.Mcp.ServerStartTimeoutSeconds, 10, 1800));
            var sw = Stopwatch.StartNew();
            progress.Warn(health == HealthState.Loading
                ? "Offload: the local model is still loading; this call waits for it."
                : $"Offload: the local model server is not running; starting it (up to {timeout.TotalSeconds:0} s).");
            Task<IpcResponse?>? startTask = null;

            if (health == HealthState.Down)
            {
                if (await EnsureTrayAsync(state, progress, sw, () => client.GetHealthAsync(ct), ct).ConfigureAwait(false))
                    return await VerifiedAsync(client, role, fromTray: false, ct).ConfigureAwait(false);
                progress.Report("Starting local model…");
                startTask = SendAsync(state, new IpcRequest(IpcCommands.StartServer), timeout, ct);
            }

            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                health = await client.GetHealthAsync(ct).ConfigureAwait(false);
                if (health == HealthState.Ready)
                {
                    progress.Report($"Local model ready ({sw.Elapsed.TotalSeconds:0} s)");
                    return await VerifiedAsync(client, role, fromTray: false, ct).ConfigureAwait(false);
                }
                if (startTask is { IsCompleted: true })
                {
                    var resp = await startTask.ConfigureAwait(false);
                    startTask = null;
                    if (resp is null) Log.Warn("mcp", "Трей не ответил на start-server");
                    else if (!resp.Ok) throw TrayFailed(resp);
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

    /// <summary>
    /// Сервер вспомогательной роли — только по адресу, который вернул трей (IPC start-server с ролью): порт роли (основной + 1…3)
    /// мог занять чужой процесс, и опрос адреса из конфига до ответа трея отдал бы ему код и ключ API. Затем — сверка
    /// псевдонима модели (offload-&lt;роль&gt;) по /v1/models без ключа. Любая неудача — ToolException (вызывающий перейдёт на основную).
    /// </summary>
    private static async Task<LlamaClient> EnsureAuxAsync(AppConfig cfg, SessionState state, ProgressReporter progress, ModelRole role, CancellationToken ct)
    {
        // В клиентском режиме основной модели здесь нет, но локальные серверы ролей работают, если модели назначены.
        if (!cfg.IsRemote() && (!cfg.SetupCompleted || cfg.ActiveModel() is null)) throw new ToolException(NotSetUpMessage);
        if (cfg.RoleModel(role) is null) throw new ToolException($"No model is assigned to the '{role.Key()}' role in Offload.");

        await state.EnsureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.Mcp.ServerStartTimeoutSeconds, 10, 1800));
            var sw = Stopwatch.StartNew();
            await EnsureTrayAsync(state, progress, sw, null, ct).ConfigureAwait(false);
            progress.Report($"Starting the {role.Key()} local model…");
            var args = new Dictionary<string, string> { [IpcRoleArgs.Role] = role.Key() };
            var resp = await SendAsync(state, new IpcRequest(IpcCommands.StartServer, args), timeout, ct).ConfigureAwait(false)
                       ?? throw new ToolException("The Offload tray app did not answer the request to start the auxiliary model.");
            if (!resp.Ok) throw TrayFailed(resp);
            var client = CheckRoleResponse(resp, role, LlamaClient.ForRole(cfg, role));
            // Трей отвечает после готовности сервера; короткое ожидание — на случай гонки с перезапуском.
            while (await client.GetHealthAsync(ct).ConfigureAwait(false) != HealthState.Ready)
            {
                if (sw.Elapsed >= timeout)
                    throw new ToolException($"The {role.Key()} model server did not become ready within {timeout.TotalSeconds:0} s.");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            return await VerifiedAsync(client, role, fromTray: true, ct).ConfigureAwait(false);
        }
        finally
        {
            state.EnsureLock.Release();
        }
    }

    /// <summary>
    /// Клиентский режим: основная модель на удалённом сервере. Трей не нужен (он этот сервер не запускает): /health — готов,
    /// 503 — ждём загрузку модели не дольше ServerStartTimeoutSeconds, нет ответа — сразу ToolException (сеть/VPN/выключенный ПК).
    /// Модель — id из /v1/models (предпочтительно сохранённый трем); псевдоним «offload» не сверяется: сервер может быть любым
    /// OpenAI-совместимым, пользователь выбрал его адрес явно.
    /// </summary>
    internal static async Task<LlamaClient> EnsureRemoteAsync(AppConfig cfg, ProgressReporter progress, CancellationToken ct)
    {
        var client = LlamaClient.FromConfig(cfg);
        var host = RemoteServer.DisplayHost(client.BaseUrl);
        if (string.IsNullOrEmpty(client.ApiKey))
            throw new ToolException(
                $"The API key of the remote model server ({host}) is not available on this PC (not saved, or encrypted for another Windows user). " +
                "Re-enter it in Offload → Settings → Remote server; do this task yourself for now.");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.Mcp.ServerStartTimeoutSeconds, 10, 1800));
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var health = await RemoteProbe.HealthAsync(client, ct).ConfigureAwait(false);
            if (health.State == HealthState.Ready) break;
            if (health.State == HealthState.Down)
            {
                Log.Warn("mcp", $"Удалённый сервер {host} недоступен: {health.Detail}");
                var why = LocalModel.EnglishDetail(health.Detail) is { } d ? $" ({d})" : "";
                throw new ToolException(
                    $"The remote model server {host} is not reachable{why}. Check that the PC hosting the model is on, Offload runs there with network access enabled, " +
                    "and the network/VPN is up; or turn off the remote server in Offload settings. Do this task yourself for now.");
            }
            if (sw.Elapsed >= timeout)
                throw new ToolException($"The remote model server {host} is still loading its model after {timeout.TotalSeconds:0} s; retry later or do this task yourself.");
            progress.Report($"Remote model on {host} is loading… {sw.Elapsed.TotalSeconds:0} s");
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        var served = await RemoteProbe.ModelIdsAsync(client, ct).ConfigureAwait(false);
        var model = RemoteProbe.PickModel(served, client.Model);
        if (!string.Equals(model, client.Model, StringComparison.Ordinal))
            Log.Info("mcp", $"Удалённый сервер {host} не отдаёт модель {client.Model} — используется {model}");
        return client.WithModel(model);
    }

    /// <summary>Трей запущен (при необходимости — запустить и дождаться IPC). true — сервер уже ответил Ready, пока ждали.</summary>
    private static async Task<bool> EnsureTrayAsync(SessionState state, ProgressReporter progress, Stopwatch sw,
        Func<Task<HealthState>>? health, CancellationToken ct)
    {
        if (IsTrayRunning(state)) return false;
        progress.Report("Starting Offload tray app…");
        if (!LaunchTray(state))
            throw new ToolException(
                "The local model server is not running and the Offload tray app could not be started. Start Offload from the Start menu, then retry.");
        // Ждём, пока трей поднимет IPC (он сам может запустить сервер при старте).
        while (!IsTrayRunning(state) && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (health is not null && await health().ConfigureAwait(false) == HealthState.Ready) return true;
        }
        return false;
    }

    private static bool IsTrayRunning(SessionState state) => state.IpcOverride is not null || IpcClient.IsTrayRunning();

    private static Task<IpcResponse?> SendAsync(SessionState state, IpcRequest request, TimeSpan timeout, CancellationToken ct) =>
        state.IpcOverride is { } over ? over(request, ct) : IpcClient.SendAsync(request, timeout, ct);

    /// <summary>Отказ трея: его текст локализован для пользователя — в ответ модели попадает только английская подробность.</summary>
    private static ToolException TrayFailed(IpcResponse resp)
    {
        Log.Warn("mcp", $"Трей не запустил сервер: {resp.Message}");
        var why = LocalModel.EnglishDetail(resp.Message) is { } d ? ": " + d.TrimEnd('.') : " (see the Offload log)";
        return new ToolException($"The Offload tray app could not start the local model{why}. Open Offload to see details; meanwhile do this task yourself.");
    }

    /// <summary>
    /// Ответ трея на запуск сервера роли: трей старой версии роль не знает (запустил основной сервер и не вернул Data["role"]) —
    /// ToolException, вызывающий перейдёт на основную модель. Адрес сервера роли — только из ответа трея и только петлевой
    /// http: адрес из конфига не используется (порт мог занять чужой процесс), ответ трея не уводит запросы с ключом на чужой хост.
    /// </summary>
    internal static LlamaClient CheckRoleResponse(IpcResponse resp, ModelRole role, LlamaClient client)
    {
        if (resp.Data?.GetValueOrDefault(IpcRoleArgs.Role) != role.Key())
            throw new ToolException("The running Offload tray app does not support auxiliary model roles. Restart Offload to use them.");
        var url = resp.Data?.GetValueOrDefault(IpcRoleArgs.BaseUrl);
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ToolException($"The Offload tray app did not report a local address for the {role.Key()} model server.");
        return new LlamaClient(uri.GetLeftPart(UriPartial.Authority), client.ApiKey) { Model = client.Model };
    }

    /// <summary>
    /// Сверка, что по адресу отвечает наш llama-server: GET /v1/models без ключа (публичный путь llama-server) и псевдоним
    /// модели offload / offload-&lt;роль&gt;. Другой псевдоним — отказ (порт занят чужим сервером), код и ключ туда не уходят.
    /// Сервер без публичного /v1/models (401/404, старые сборки) принимается: для роли адрес всё равно из трея, для основного
    /// сервера — прежнее поведение. Защита от случайного конфликта портов, а не от процесса, подделывающего ответ.
    /// </summary>
    internal static async Task<LlamaClient> VerifiedAsync(LlamaClient client, ModelRole role, bool fromTray, CancellationToken ct)
    {
        var expected = AuxServerArgs.AliasFor(role);
        var aliases = await ModelAliasesAsync(client.BaseUrl, ct).ConfigureAwait(false);
        if (aliases is null || aliases.Count == 0 || aliases.Contains(expected, StringComparer.Ordinal)) return client;
        Log.Warn("mcp", $"По адресу {client.BaseUrl} отвечает не сервер Offload ({string.Join(", ", aliases.Take(3))} вместо {expected}; адрес {(fromTray ? "от трея" : "из конфига")})");
        throw new ToolException(
            $"Another program answers on {client.BaseUrl} instead of the Offload {(role == ModelRole.Quality ? "local model" : role.Key() + " model")} server " +
            "(port conflict). Offload did not send anything to it. Restart Offload or change the server port in Offload settings; do this task yourself for now.");
    }

    private static readonly HttpClient ProbeHttp = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Идентификаторы моделей из GET /v1/models (без ключа). null — сервер не отдал список (401, 404, ошибка).</summary>
    private static async Task<List<string>?> ModelAliasesAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await ProbeHttp.GetAsync(baseUrl.TrimEnd('/') + "/v1/models", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length > 1_000_000) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var m in data.EnumerateArray())
            {
                if (m.ValueKind == System.Text.Json.JsonValueKind.Object && m.TryGetProperty("id", out var id)
                    && id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() is { } s)
                    list.Add(s);
            }
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        {
            Log.Debug("mcp", $"/v1/models недоступен: {ex.Message}");
            return null;
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
