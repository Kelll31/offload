using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core.Logging;
using Offload.Core.Net;

namespace Offload.Models;

/// <summary>Порядок выдачи поиска моделей на Hugging Face.</summary>
public enum HfSort
{
    /// <summary>По числу загрузок за месяц (downloads).</summary>
    Downloads,
    /// <summary>Популярные сейчас (trendingScore).</summary>
    Trending,
    /// <summary>Недавно обновлённые (lastModified).</summary>
    Updated,
    /// <summary>По отметкам «нравится» (likes).</summary>
    Likes,
}

/// <summary>Доступ к репозиторию: открытый или закрытый (gated: auto — условия принимаются сразу, manual — после одобрения).</summary>
public enum HfGate
{
    None,
    Auto,
    Manual,
}

/// <summary>Репозиторий модели на Hugging Face (из поиска или /api/models/{repo}).</summary>
/// <param name="Sha">Последний коммит репозитория (40 hex) — им закрепляется ревизия; null — хаб не сообщил.</param>
/// <param name="License">Лицензия из карточки модели (cardData.license / license_name); null — не указана.</param>
public sealed record HfRepoInfo(
    string Repo,
    string? Sha,
    long Downloads,
    long Likes,
    DateTimeOffset? LastModified,
    string? License,
    HfGate Gated,
    string? PipelineTag)
{
    public bool IsGated => Gated != HfGate.None;
}

/// <summary>Квантизация из репозитория: все части разбитого файла по порядку (для неразбитого — один файл).</summary>
public sealed record HfQuantOption(string Quant, IReadOnlyList<HfFile> Files)
{
    public long TotalSize => Files.Sum(f => f.Size);

    public bool IsSplit => Files.Count > 1;
}

public static partial class HfClient
{
    /// <summary>Сколько результатов поиска запрашивается по умолчанию.</summary>
    public const int DefaultSearchLimit = 30;

    private const int MaxSearchQuery = 200;

    /// <summary>Поля ответа /api/models (expand[] заменяет набор полей по умолчанию: id возвращается всегда).</summary>
    private static readonly string[] RepoFields = ["sha", "cardData", "gated", "downloads", "likes", "lastModified", "pipeline_tag"];

    /// <summary>Страница репозитория на хабе (или зеркале).</summary>
    public static string RepoUrl(string repo) => $"{Endpoint}/{repo}";

    /// <summary>
    /// Поиск репозиториев с файлами GGUF: <c>GET /api/models?search=…&amp;filter=gguf&amp;sort=…</c> (зеркало — из настроек).
    /// </summary>
    public static async Task<IReadOnlyList<HfRepoInfo>> SearchAsync(string query, HfSort sort = HfSort.Downloads,
        int limit = DefaultSearchLimit, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length > MaxSearchQuery) q = q[..MaxSearchQuery];
        var url = SearchUrl(q, sort, limit);
        var json = await GetJsonAsync(url, code => SearchStatusError(code), ct).ConfigureAwait(false);
        try
        {
            var list = ParseSearch(json);
            Log.Debug("hf", $"Поиск «{q}» ({sort}): {list.Count}");
            return list;
        }
        catch (JsonException ex)
        {
            throw new ModelException(L.T("Hugging Face вернул некорректный ответ на поиск моделей."), ex);
        }
    }

    internal static string SearchUrl(string query, HfSort sort, int limit)
    {
        var sortKey = sort switch
        {
            HfSort.Trending => "trendingScore",
            HfSort.Updated => "lastModified",
            HfSort.Likes => "likes",
            _ => "downloads",
        };
        var parts = new List<string>();
        if (query.Length > 0) parts.Add("search=" + Uri.EscapeDataString(query));
        parts.Add("filter=gguf");
        parts.Add("sort=" + sortKey);
        parts.Add("direction=-1");
        parts.Add("limit=" + Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture));
        parts.AddRange(RepoFields.Select(f => "expand%5B%5D=" + f));
        return $"{Endpoint}/api/models?{string.Join('&', parts)}";
    }

    /// <summary>Сведения о репозитории (ревизия, лицензия, закрытость): <c>GET /api/models/{repo}</c>.</summary>
    public static async Task<HfRepoInfo> GetRepoInfoAsync(string repo, CancellationToken ct = default)
    {
        ValidateRepo(repo);
        var url = $"{Endpoint}/api/models/{repo}?{string.Join('&', RepoFields.Select(f => "expand%5B%5D=" + f))}";
        var json = await GetJsonAsync(url, code => InfoStatusError(code, repo), ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseRepo(doc.RootElement) ?? throw new JsonException("Нет id."); // l10n-ignore: внутреннее, заменяется ModelException
        }
        catch (JsonException ex)
        {
            throw new ModelException(L.F("Hugging Face вернул некорректные сведения о репозитории {0}.", repo), ex);
        }
    }

    /// <summary>Разбор ответа поиска (массив репозиториев); записи без id пропускаются.</summary>
    internal static List<HfRepoInfo> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Ожидался массив."); // l10n-ignore: внутреннее, заменяется ModelException
        var list = new List<HfRepoInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (ParseRepo(e) is { } info) list.Add(info);
        }
        return list;
    }

    internal static HfRepoInfo? ParseRepo(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = Str(e, "id") ?? Str(e, "modelId");
        if (id is null || !RepoRegex().IsMatch(id)) return null;

        var sha = Str(e, "sha") is { } s && Sha1Regex().IsMatch(s) ? s.ToLowerInvariant() : null;
        DateTimeOffset? modified = Str(e, "lastModified") is { } lm &&
                                   DateTimeOffset.TryParse(lm, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
        var gated = e.TryGetProperty("gated", out var g) ? g.ValueKind switch
        {
            JsonValueKind.String when string.Equals(g.GetString(), "manual", StringComparison.OrdinalIgnoreCase) => HfGate.Manual,
            JsonValueKind.String when string.Equals(g.GetString(), "false", StringComparison.OrdinalIgnoreCase) => HfGate.None,
            JsonValueKind.String => HfGate.Auto,
            JsonValueKind.True => HfGate.Auto,
            _ => HfGate.None,
        } : HfGate.None;

        string? license = null;
        if (e.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object)
        {
            license = LicenseOf(card, "license");
            // «other» — своя лицензия: её имя в license_name.
            if (string.Equals(license, "other", StringComparison.OrdinalIgnoreCase) && LicenseOf(card, "license_name") is { } named) license = named;
        }
        license ??= e.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.String ? t.GetString() : null)
                .FirstOrDefault(t => t?.StartsWith("license:", StringComparison.OrdinalIgnoreCase) == true)?["license:".Length..]
            : null;

        return new HfRepoInfo(id, sha, Num(e, "downloads"), Num(e, "likes"), modified, Clean(license, 100), gated,
            Clean(Str(e, "pipeline_tag"), 64));

        static string? LicenseOf(JsonElement card, string name)
        {
            if (!card.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
                _ => null,
            } is { Length: > 0 } text ? text : null;
        }
    }

    /// <summary>
    /// Квантизации репозитория: файлы GGUF основной модели (без проекторов mmproj, черновиков и не-GGUF), разбитые
    /// (-00001-of-0000N) — одним вариантом со всеми частями. Варианты без SHA-256 у любой части не предлагаются:
    /// загрузка всегда проверяется. Порядок — по размеру (от меньшего).
    /// </summary>
    public static IReadOnlyList<HfQuantOption> GroupQuants(IReadOnlyList<HfFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var groups = files
            .Where(f => IsModelGguf(f.Path) && !f.Path.Contains("..", StringComparison.Ordinal) && !f.Path.StartsWith('/'))
            .GroupBy(f => GroupKey(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => ShardInfo(f.Path)?.Index ?? 0).ToList())
            .Where(g => IsCompleteGroup(g) && g.All(f => f.Size > 0 && f.Sha256 is not null))
            .ToList();

        // Одна метка — один вариант (как SelectQuantFiles: меньше частей, короче путь).
        var result = new List<HfQuantOption>();
        foreach (var byTag in groups.GroupBy(g => QuantTag(g[0].Path) ?? QuantlessName(g[0].Path), StringComparer.OrdinalIgnoreCase))
        {
            var best = byTag.OrderBy(g => g.Count).ThenBy(g => g[0].Path.Length).ThenBy(g => g[0].Path, StringComparer.Ordinal).First();
            result.Add(new HfQuantOption(byTag.Key, best));
        }
        return result.OrderBy(o => o.TotalSize).ThenBy(o => o.Quant, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Файл без метки квантизации: имя без .gguf и суффикса части.</summary>
    private static string QuantlessName(string path)
    {
        var name = StripShardSuffix(FileName(path));
        return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    /// <summary>
    /// Заголовок GGUF файла на закреплённой ревизии — HTTP Range-запросами (не больше
    /// <see cref="GgufRangeReader.MaxHeaderBytes"/>), без загрузки весов.
    /// </summary>
    public static async Task<GgufInfo> ReadGgufHeaderAsync(string repo, string revision, HfFile file, CancellationToken ct = default)
    {
        ValidateRepo(repo);
        ArgumentNullException.ThrowIfNull(file);
        if (!Sha1Regex().IsMatch(revision ?? "")) throw new ModelException(L.F("Не закреплена ревизия репозитория {0}.", repo));
        var url = DownloadUrl(repo, file.Path, revision);
        try
        {
            var (info, bytes) = await GgufRangeReader.ReadAsync((from, count, token) => ReadRangeAsync(url, from, count, repo, token), file.Size, ct: ct)
                .ConfigureAwait(false);
            Log.Debug("hf", $"Заголовок GGUF {repo}/{file.Path}: {bytes} байт, {info.Architecture}");
            return info;
        }
        catch (InvalidDataException ex)
        {
            throw new ModelException(L.F("Не удалось прочитать заголовок {0}: {1}", FileName(file.Path), ex.Message), ex);
        }
    }

    /// <summary>Байты файла [from, from + count): Range-запрос; если сервер отдаёт файл целиком — лишнее пропускается.</summary>
    internal static async Task<byte[]> ReadRangeAsync(string url, long from, int count, string repo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, from + count - 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        HttpResponseMessage response;
        try
        {
            response = await Http.Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelException(L.T("Hugging Face не ответил вовремя. Проверьте подключение к интернету и повторите попытку."));
        }
        catch (HttpRequestException ex)
        {
            throw new ModelException(L.F("Нет связи с Hugging Face ({0}): {1}", Endpoint, ex.Message), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? GatedError(repo)
                    : new ModelException(L.F("Hugging Face вернул ошибку {0} при чтении заголовка модели {1}.", (int)response.StatusCode, repo));
            }
            long skip = 0;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange is { From: { } start } && start != from)
                    throw new ModelException(L.F("Hugging Face вернул не ту часть файла модели {0}.", repo));
            }
            else
            {
                skip = from; // сервер не поддерживает Range: пропускаем начало
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var scratch = new byte[81920];
            while (skip > 0)
            {
                var n = await stream.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, skip)), timeout.Token).ConfigureAwait(false);
                if (n == 0) return [];
                skip -= n;
            }
            var result = new byte[count];
            var have = 0;
            while (have < count)
            {
                var n = await stream.ReadAsync(result.AsMemory(have, count - have), timeout.Token).ConfigureAwait(false);
                if (n == 0) break;
                have += n;
            }
            return have == count ? result : result[..have];
        }
    }

    /// <summary>GET JSON с общими сетевыми ошибками; коды ответа ≠ 2xx — через statusError.</summary>
    private static async Task<string> GetJsonAsync(string url, Func<HttpStatusCode, ModelException> statusError, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await Http.Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelException(L.T("Hugging Face не ответил вовремя. Проверьте подключение к интернету и повторите попытку."));
        }
        catch (HttpRequestException ex)
        {
            throw new ModelException(L.F("Нет связи с Hugging Face ({0}): {1}", Endpoint, ex.Message), ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw statusError(response.StatusCode);
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }

    private static ModelException SearchStatusError(HttpStatusCode code) => code switch
    {
        HttpStatusCode.TooManyRequests => new ModelException(L.T("Hugging Face временно ограничил число запросов (429). Повторите через несколько минут.")),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ModelException(L.T("Hugging Face отклонил запрос: проверьте токен в разделе «Настройки» → «Сеть».")),
        _ => new ModelException(L.F("Hugging Face вернул ошибку {0} при поиске моделей.", (int)code)),
    };

    /// <summary>Хаб отвечает 401 и на несуществующий репозиторий, и на закрытый без токена.</summary>
    private static ModelException InfoStatusError(HttpStatusCode code, string repo) => code switch
    {
        HttpStatusCode.NotFound => new ModelException(L.F("Репозиторий {0} не найден на Hugging Face.", repo)),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => NetworkOptions.HfToken is null
            ? new ModelException(L.F("Репозиторий {0} не найден или закрыт: для закрытых моделей укажите токен Hugging Face в разделе «Настройки» → «Сеть».", repo))
            : new ModelException(L.F("Репозиторий {0} не найден или токен Hugging Face не даёт к нему доступа.", repo)),
        HttpStatusCode.TooManyRequests => new ModelException(L.T("Hugging Face временно ограничил число запросов (429). Повторите через несколько минут.")),
        _ => new ModelException(L.F("Hugging Face вернул ошибку {0} при получении сведений о {1}.", (int)code, repo)),
    };

    private static ModelException GatedError(string repo) => new(NetworkOptions.HfToken is null
        ? L.F("Модель {0} закрыта (gated): укажите токен Hugging Face в разделе «Настройки» → «Сеть» и примите условия на странице модели.", repo)
        : L.F("Нет доступа к модели {0}: токен Hugging Face не подошёл или условия модели не приняты на её странице.", repo));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? Math.Max(0, n) : 0;

    /// <summary>Строка из ответа сервера для показа: без управляющих символов, не длиннее max.</summary>
    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length == 0 ? null : t.Length > max ? t[..max] : t;
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex Sha1Regex();
}
