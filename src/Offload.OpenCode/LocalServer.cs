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

    /// <summary>
    /// URL своего сервера для клиента: 0.0.0.0/:: (прослушивание всех адресов) заменяются на петлевой адрес, IPv6 — в скобках;
    /// в режиме «Доступ из сети» с конкретным адресом — этот адрес (петлевой тогда не отвечает).
    /// </summary>
    internal static string ClientBaseUrl(ServerSettings s) => LanServer.ClientBaseUrl(s.ListenHost(), s.Port);

    /// <param name="maxLoadingWait">Сколько ждать, пока сервер загружает модель (/health = 503).</param>
    public static async Task<ServerProbe> ProbeAsync(AppConfig cfg, Action<string>? report, TimeSpan maxLoadingWait, CancellationToken ct)
    {
        // Клиентский режим: удалённый сервер (адрес и ключ — из настроек «Удалённый сервер»).
        var ep = cfg.MainEndpoint();
        var baseUrl = ep.BaseUrl;
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
                // Удалённый сервер может стоять за обратным прокси, требующим ключ и для /health.
                if (ep.IsRemote && ep.ApiKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
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
                return new ServerProbe(false, null, ep.IsRemote
                    ? L.F("Удалённый сервер модели не отвечает ({0}). Проверьте сеть или VPN и Offload на том компьютере.", baseUrl)
                    : L.F("Локальный сервер модели не отвечает ({0}). Запустите сервер в Offload и повторите попытку.", baseUrl));
            }

            if (code != HttpStatusCode.ServiceUnavailable || sw.Elapsed >= maxLoadingWait) break;
            // 503 — модель ещё загружается.
            if (sw.Elapsed - lastReport >= TimeSpan.FromSeconds(10))
            {
                report?.Invoke(L.F("ожидание загрузки модели… {0} с", (int)sw.Elapsed.TotalSeconds));
                lastReport = sw.Elapsed;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // Настройки слотов удалённого сервера неизвестны — KV считаем общим (оценка контекста с запасом, как в LlamaClient.FromConfig).
        return new ServerProbe(true, await TryGetContextAsync(baseUrl, ep.ApiKey, ep.IsRemote || UsesUnifiedKv(cfg.Server), ct), null);
    }

    /// <summary>
    /// Слоты делят общий KV-кэш: Offload запускает llama-server с -kvu при нескольких слотах
    /// (если -no-kvu / --no-kv-unified не передан доп. аргументом). То же правило — в LlamaServerArgs.UsesUnifiedKv.
    /// </summary>
    internal static bool UsesUnifiedKv(ServerSettings s) =>
        Math.Clamp(s.Parallel, 1, ServerSettings.MaxParallel) > 1
        && !(s.ExtraArgs ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(a => a.Trim('"') is "-no-kvu" or "--no-kv-unified");

    /// <summary>Контекст одного запроса из /props (default_generation_settings.n_ctx; при общем KV — доля слота).</summary>
    private static async Task<int?> TryGetContextAsync(string baseUrl, string apiKey, bool unifiedKv, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/props");
            if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await ClientLazy.Value.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseContext(await resp.Content.ReadAsStringAsync(cts.Token), unifiedKv);
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

    /// <param name="unifiedKv">Общий KV-кэш (-kvu): n_ctx из /props — весь буфер, запросу гарантирована доля n_ctx / total_slots.</param>
    internal static int? ParseContext(string json, bool unifiedKv = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        int? ctx = null;
        if (root.TryGetProperty("default_generation_settings", out var dgs) && dgs.ValueKind == JsonValueKind.Object
            && GetInt(dgs, "n_ctx") is > 0 and var n)
            ctx = n;
        else if (GetInt(root, "n_ctx") is > 0 and var m)
            ctx = m;
        if (ctx is null) return null;
        var slots = GetInt(root, "total_slots") ?? 1;
        return unifiedKv && slots > 1 ? Math.Max(1, ctx.Value / slots) : ctx;
    }

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
