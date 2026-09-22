using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.OpenCode;

/// <summary>Результат проверки llama-server перед запуском агента. ContextPerSlot — из /props (null — неизвестен).</summary>
internal sealed record ServerProbe(bool Reachable, int? ContextPerSlot, string? Error);

/// <summary>
/// Быстрая проверка локального llama-server до запуска OpenCode: при недоступном сервере OpenCode
/// долго повторяет запросы (до 5 раз с паузами до 30 с), а ошибку лучше сообщить сразу.
/// </summary>
internal static class LocalServer
{
    private static readonly Lazy<HttpClient> ClientLazy = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, // корпоративный прокси не должен перехватывать 127.0.0.1
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        return client;
    });

    /// <summary>URL для клиента: 0.0.0.0/:: (прослушивание всех адресов) заменяются на петлевой адрес, IPv6 — в скобках.</summary>
    internal static string ClientBaseUrl(ServerSettings s)
    {
        var h = (s.Host ?? "").Trim();
        if (h is "" or "0.0.0.0" or "*" or "+") h = "127.0.0.1";
        else if (h is "::" or "[::]") h = "[::1]";
        else if (h.Contains(':') && !h.StartsWith('[')) h = $"[{h}]";
        return $"http://{h}:{s.Port}";
    }

    /// <param name="maxLoadingWait">Сколько ждать, пока сервер загружает модель (/health = 503).</param>
    public static async Task<ServerProbe> ProbeAsync(AppConfig cfg, Action<string>? report, TimeSpan maxLoadingWait, CancellationToken ct)
    {
        var baseUrl = ClientBaseUrl(cfg.Server);
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        while (true)
        {
            HttpStatusCode code;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/health");
                using var resp = await ClientLazy.Value.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                code = resp.StatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                Log.Warn("opencode", $"llama-server недоступен ({baseUrl}): {ex.Message}");
                return new ServerProbe(false, null,
                    $"Локальный сервер модели не отвечает ({baseUrl}). Запустите сервер в Offload и повторите попытку.");
            }

            if (code != HttpStatusCode.ServiceUnavailable || sw.Elapsed >= maxLoadingWait) break;
            // 503 — модель ещё загружается.
            if (sw.Elapsed - lastReport >= TimeSpan.FromSeconds(10))
            {
                report?.Invoke($"ожидание загрузки модели… {(int)sw.Elapsed.TotalSeconds} с");
                lastReport = sw.Elapsed;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return new ServerProbe(true, await TryGetContextAsync(baseUrl, cfg.Server.ApiKey, ct), null);
    }

    /// <summary>Контекст одного слота из /props (default_generation_settings.n_ctx).</summary>
    private static async Task<int?> TryGetContextAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/props");
            if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await ClientLazy.Value.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseContext(await resp.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("opencode", $"/props недоступен: {ex.Message}");
            return null;
        }
    }

    internal static int? ParseContext(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("default_generation_settings", out var dgs) && dgs.ValueKind == JsonValueKind.Object
            && GetInt(dgs, "n_ctx") is > 0 and var n)
            return n;
        return GetInt(root, "n_ctx") is > 0 and var m ? m : null;
    }

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
