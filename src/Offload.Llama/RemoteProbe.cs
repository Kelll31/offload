using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.Llama;

/// <summary>Состояние удалённого сервера: готовность, задержка ответа /health, английская подробность сбоя (для MCP).</summary>
public sealed record RemoteHealth(HealthState State, TimeSpan Latency, string? Detail);

/// <summary>Итог «Проверить подключение» (клиентский режим).</summary>
public sealed record RemoteCheckResult(
    HealthState State,
    TimeSpan Latency,
    /// <summary>Модели из /v1/models (пусто — сервер не отдал список).</summary>
    IReadOnlyList<string> Models,
    /// <summary>Модель, которую будет использовать Offload.</summary>
    string ModelId,
    /// <summary>Контекст на запрос по /props (null — неизвестен).</summary>
    int? ContextPerRequest,
    int? Slots,
    /// <summary>Ключ принят (/props ответил 200); false — 401/403; null — проверить не удалось (нет /props).</summary>
    bool? KeyAccepted,
    /// <summary>Текст ошибки для пользователя (null — подключение работает).</summary>
    string? Error)
{
    public bool Ok => Error is null && State == HealthState.Ready;
}

/// <summary>
/// Проверки удалённого OpenAI-совместимого сервера (клиентский режим): /health, /v1/models, /props. Ключ — только в заголовке
/// Authorization, не в адресе; ответы ограничены по размеру; время каждого запроса ограничено (LAN/VPN — секунды, не минуты).
/// </summary>
public static class RemoteProbe
{
    /// <summary>Потолок одной проверки: в локальной сети и через VPN сервер отвечает за доли секунды.</summary>
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(8);

    private const int MaxBodyChars = 256 * 1024;

    /// <summary>
    /// GET /health (с ключом: за обратным прокси может требоваться и он). 200 → Ready, 503 → Loading. Сервер без /health (404,
    /// не llama.cpp) — готовность по GET /v1/models. Нет ответа → Down с подробностью.
    /// </summary>
    public static async Task<RemoteHealth> HealthAsync(LlamaClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var sw = Stopwatch.StartNew();
        try
        {
            var (code, _) = await GetAsync(client, "/health", readBody: false, ct).ConfigureAwait(false);
            var latency = sw.Elapsed;
            if (code == HttpStatusCode.NotFound)
            {
                var (modelsCode, _) = await GetAsync(client, "/v1/models", readBody: false, ct).ConfigureAwait(false);
                return modelsCode == HttpStatusCode.OK
                    ? new RemoteHealth(HealthState.Ready, latency, null)
                    : new RemoteHealth(HealthState.Down, latency, $"the server has no /health and /v1/models answered HTTP {(int)modelsCode}");
            }
            return code switch
            {
                HttpStatusCode.OK => new RemoteHealth(HealthState.Ready, latency, null),
                HttpStatusCode.ServiceUnavailable => new RemoteHealth(HealthState.Loading, latency, null),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new RemoteHealth(HealthState.Down, latency, $"/health answered HTTP {(int)code} (API key rejected)"),
                _ => new RemoteHealth(HealthState.Down, latency, $"/health answered HTTP {(int)code}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new RemoteHealth(HealthState.Down, sw.Elapsed, $"no answer within {RequestTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new RemoteHealth(HealthState.Down, sw.Elapsed, LocalHttp.InvariantReason(ex));
        }
    }

    /// <summary>Текст ошибки «ключ отклонён» для интерфейса.</summary>
    public static string KeyRejectedError => L.T("Сервер отклонил ключ API (401). Скопируйте ключ из раздела «Доступ из сети» на том компьютере.");

    /// <summary>
    /// Принят ли ключ: GET /props (у llama-server он, в отличие от /health и /v1/models, требует ключ). true — 200;
    /// false — 401/403; null — проверить не удалось (нет ответа, нет /props у стороннего сервера).
    /// </summary>
    public static async Task<bool?> KeyAcceptedAsync(LlamaClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            var (code, _) = await GetAsync(client, "/props", readBody: false, ct).ConfigureAwait(false);
            return code switch
            {
                HttpStatusCode.OK => true,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => false,
                _ => null,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            Log.Debug("remote", $"/props удалённого сервера: {LocalHttp.InvariantReason(ex)}");
            return null;
        }
    }

    /// <summary>Идентификаторы моделей из GET /v1/models (с ключом). null — список не получен.</summary>
    public static async Task<IReadOnlyList<string>?> ModelIdsAsync(LlamaClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            var (code, body) = await GetAsync(client, "/v1/models", readBody: true, ct).ConfigureAwait(false);
            return code == HttpStatusCode.OK && body is not null ? ParseModelIds(body) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            Log.Debug("llama", $"/v1/models удалённого сервера недоступен: {LocalHttp.InvariantReason(ex)}");
            return null;
        }
    }

    /// <summary>
    /// id моделей из ответа /v1/models (OpenAI: {"data":[{"id":…}]}); не больше 64. Небезопасные id (см. <see cref="RemoteServer.IsSafeModelId"/>)
    /// пропускаются: сервер недоверенный, а id уходит в конфиг OpenCode.
    /// </summary>
    internal static IReadOnlyList<string> ParseModelIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<string>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var m in data.EnumerateArray())
        {
            if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } s && RemoteServer.IsSafeModelId(s) && !list.Contains(s))
                list.Add(s);
            if (list.Count >= 64) break;
        }
        return list;
    }

    /// <summary>
    /// Модель для запросов: предпочтительная (из настроек), если сервер её отдаёт; иначе псевдоним Offload «offload»;
    /// иначе первая из списка. Пустой список — предпочтительная как есть.
    /// </summary>
    public static string PickModel(IReadOnlyList<string>? served, string? preferred)
    {
        var want = RemoteServer.IsSafeModelId(preferred?.Trim()) ? preferred!.Trim() : RemoteServer.DefaultModel;
        if (served is not { Count: > 0 }) return want;
        if (served.Contains(want, StringComparer.Ordinal)) return want;
        if (served.Contains(RemoteServer.DefaultModel, StringComparer.Ordinal)) return RemoteServer.DefaultModel;
        return served[0];
    }

    /// <summary>
    /// «Проверить подключение»: /health (задержка), /v1/models (модель), /props (контекст и проверка ключа). Ошибки — по-русски
    /// для интерфейса; исключений (кроме отмены) нет.
    /// </summary>
    public static async Task<RemoteCheckResult> CheckAsync(string baseUrl, string apiKey, string? preferredModel, CancellationToken ct = default)
    {
        var client = new LlamaClient(baseUrl, apiKey) { UnifiedKv = true, IsRemote = true };
        var host = RemoteServer.DisplayHost(baseUrl);
        var health = await HealthAsync(client, ct).ConfigureAwait(false);
        if (health.State == HealthState.Down)
        {
            Log.Info("remote", $"Проверка удалённого сервера {host}: нет ответа ({health.Detail})");
            return new RemoteCheckResult(health.State, health.Latency, [], PickModel(null, preferredModel), null, null, null,
                L.F("Сервер {0} не отвечает. Проверьте, что на том компьютере запущен Offload с включённым «Доступом из сети», адрес и порт верны, а сеть или VPN работают.", host));
        }

        var models = await ModelIdsAsync(client, ct).ConfigureAwait(false) ?? [];
        var model = PickModel(models, preferredModel);

        int? ctx = null, slots = null;
        bool? keyOk = null;
        try
        {
            var (code, body) = await GetAsync(client, "/props", readBody: true, ct).ConfigureAwait(false);
            if (code == HttpStatusCode.OK && body is not null)
            {
                keyOk = true;
                var props = LlamaClient.ParseProps(body, unifiedKv: true);
                ctx = props.ContextPerSlot > 0 ? props.ContextPerSlot : null;
                slots = props.TotalSlots;
            }
            else if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                keyOk = false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            Log.Debug("remote", $"/props удалённого сервера: {LocalHttp.InvariantReason(ex)}");
        }

        string? error = keyOk == false ? KeyRejectedError : null;
        var keyText = keyOk switch { true => "принят", false => "отклонён", _ => "не проверен" }; // l10n-ignore — только для журнала
        Log.Info("remote", $"Проверка удалённого сервера {host}: {health.State}, {health.Latency.TotalMilliseconds:0} мс, модель {model}, контекст {ctx?.ToString() ?? "?"}, ключ {keyText}");
        return new RemoteCheckResult(health.State, health.Latency, models, model, ctx, slots, keyOk, error);
    }

    private static async Task<(HttpStatusCode Code, string? Body)> GetAsync(LlamaClient client, string path, bool readBody, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        using var req = LocalHttp.Request(HttpMethod.Get, client.BaseUrl + path, string.IsNullOrEmpty(client.ApiKey) ? null : client.ApiKey);
        using var resp = await LocalHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!readBody || resp.StatusCode != HttpStatusCode.OK) return (resp.StatusCode, null);
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(s);
        var buf = new char[MaxBodyChars + 1];
        var n = await reader.ReadBlockAsync(buf.AsMemory(), cts.Token).ConfigureAwait(false);
        return n > MaxBodyChars ? (resp.StatusCode, null) : (resp.StatusCode, new string(buf, 0, n));
    }
}
