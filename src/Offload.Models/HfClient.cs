using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core.Logging;
using Offload.Core.Net;

namespace Offload.Models;

/// <summary>Файл в репозитории Hugging Face (из tree API).</summary>
public sealed record HfFile(string Path, long Size, string? Sha256);

/// <summary>Конкретные файлы для загрузки (для разбитых моделей — все шарды по порядку).</summary>
public sealed record ResolvedModel(CatalogModel Model, string Quant, IReadOnlyList<HfFile> Files)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>Ошибка работы с моделями, понятная пользователю (сообщение на русском).</summary>
public sealed class ModelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Клиент Hugging Face: список файлов репозитория и ссылки на скачивание.</summary>
public static partial class HfClient
{
    public const string DefaultEndpoint = "https://huggingface.co";

    /// <summary>Адрес хаба. Переменная окружения HF_ENDPOINT позволяет указать зеркало (как в huggingface_hub).</summary>
    internal static string Endpoint { get; set; } =
        (Environment.GetEnvironmentVariable("HF_ENDPOINT") is { Length: > 0 } e ? e.Trim() : DefaultEndpoint).TrimEnd('/');

    private const int MaxPages = 100;

    public static Task<IReadOnlyList<HfFile>> ListFilesAsync(string repo, CancellationToken ct = default) =>
        ListFilesAsync(repo, null, ct);

    /// <summary>Все файлы репозитория (рекурсивно) на ревизии revision (commit SHA или ветка; null — main).</summary>
    public static async Task<IReadOnlyList<HfFile>> ListFilesAsync(string repo, string? revision, CancellationToken ct = default)
    {
        ValidateRepo(repo);
        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim();
        var url = $"{Endpoint}/api/models/{repo}/tree/{Uri.EscapeDataString(rev)}?recursive=true";
        var result = new List<HfFile>();

        for (var page = 0; url is not null; page++)
        {
            if (page >= MaxPages) throw new ModelException(L.F("Слишком длинный список файлов в репозитории {0}.", repo));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

            HttpResponseMessage response;
            try
            {
                response = await Http.Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
                if (!response.IsSuccessStatusCode) throw StatusError(response.StatusCode, repo, rev);
                var json = await response.Content.ReadAsStringAsync(ct);
                try
                {
                    result.AddRange(ParseTree(json));
                }
                catch (JsonException ex)
                {
                    throw new ModelException(L.F("Hugging Face вернул некорректный список файлов репозитория {0}.", repo), ex);
                }
                url = response.Headers.TryGetValues("Link", out var links) ? NextLink(links, url) : null;
            }
        }
        Log.Debug("hf", $"{repo}@{rev}: {result.Count} файлов");
        return result;
    }

    /// <summary>Подобрать файлы GGUF нужной квантизации (или первой доступной из списка предпочтений).</summary>
    public static async Task<ResolvedModel> ResolveAsync(CatalogModel model, string? quant = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var q = string.IsNullOrWhiteSpace(quant) ? model.DefaultQuant : quant.Trim();
        if (string.IsNullOrEmpty(q)) throw new ModelException(L.F("Для модели {0} не указана квантизация.", model.LocalizedDisplayName));

        // Файл из каталога: размеры и SHA-256 уже известны (и закреплены) — сеть не нужна.
        if (model.FindFile(q) is { } known)
        {
            if (known.ExtraShards is not { Count: > 0 })
                return new ResolvedModel(model, known.Quant, [new HfFile(known.Path, known.Size, known.Sha256)]);

            var tree = await ListFilesAsync(model.Repo, model.Revision, ct);
            var byPath = tree.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var shards = new List<HfFile> { new(known.Path, known.Size, known.Sha256 ?? byPath.GetValueOrDefault(known.Path)?.Sha256) };
            foreach (var p in known.ExtraShards)
            {
                shards.Add(byPath.GetValueOrDefault(p)
                    ?? throw new ModelException(L.F("В репозитории {0} нет части модели {1}.", model.Repo, p)));
            }
            return new ResolvedModel(model, known.Quant, shards);
        }

        var files = await ListFilesAsync(model.Repo, model.Revision, ct);
        var selected = SelectQuantFiles(files, q);
        if (selected.Count == 0)
        {
            var available = AvailableQuants(files);
            throw new ModelException(
                L.F("В репозитории {0} не найден файл GGUF с квантизацией {1}.", model.Repo, q) +
                (available.Count > 0 ? L.F(" Доступные: {0}.", string.Join(", ", available)) : ""));
        }
        return new ResolvedModel(model, QuantTag(selected[0].Path) ?? q, selected);
    }

    public static string DownloadUrl(string repo, string path) => DownloadUrl(repo, path, null);

    /// <summary>Ссылка resolve на файл (с закреплённой ревизией, если указана).</summary>
    public static string DownloadUrl(string repo, string path, string? revision)
    {
        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim();
        return $"{Endpoint}/{repo}/resolve/{Uri.EscapeDataString(rev)}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
    }

    /// <summary>Разбор ответа tree API: только файлы; размер и SHA-256 — из lfs, если есть.</summary>
    internal static List<HfFile> ParseTree(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<HfFile>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Ожидался массив."); // l10n-ignore: внутреннее, заменяется ModelException
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (e.TryGetProperty("type", out var type) && type.GetString() != "file") continue;
            if (!e.TryGetProperty("path", out var pathEl) || pathEl.GetString() is not { Length: > 0 } path) continue;
            long size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            string? sha = null;
            if (e.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
            {
                if (lfs.TryGetProperty("size", out var ls) && ls.TryGetInt64(out var lsv) && lsv > 0) size = lsv;
                if (lfs.TryGetProperty("oid", out var oid) && oid.GetString() is { } o && Sha256Regex().IsMatch(o)) sha = o.ToLowerInvariant();
            }
            list.Add(new HfFile(path, size, sha));
        }
        return list;
    }

    /// <summary>Следующая страница из заголовка Link: &lt;url&gt;; rel="next".</summary>
    internal static string? NextLink(IEnumerable<string> linkHeaders, string currentUrl)
    {
        foreach (var header in linkHeaders)
        {
            foreach (Match m in LinkRegex().Matches(header))
            {
                if (!m.Groups["rel"].Value.Split(' ').Contains("next", StringComparer.OrdinalIgnoreCase)) continue;
                var target = m.Groups["url"].Value;
                return Uri.TryCreate(new Uri(currentUrl), target, out var abs) ? abs.ToString() : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Файлы GGUF квантизации quant: точное совпадение метки в конце имени (Q4_K_M ≠ UD-Q4_K_M),
    /// разбитые модели (-00001-of-0000N, в т.ч. в подпапках) — все шарды по порядку.
    /// </summary>
    internal static IReadOnlyList<HfFile> SelectQuantFiles(IReadOnlyList<HfFile> files, string quant)
    {
        var ggufs = files.Where(f => IsModelGguf(f.Path)).ToList();
        var exact = ggufs.Where(f => string.Equals(QuantTag(f.Path), quant, StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = exact.Count > 0
            ? exact
            : ggufs.Where(f => FileName(f.Path).Contains(quant, StringComparison.OrdinalIgnoreCase)).ToList();

        var groups = candidates
            .GroupBy(f => GroupKey(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => ShardInfo(f.Path)?.Index ?? 0).ToList())
            .Where(IsCompleteGroup)
            .OrderBy(g => g.Count)
            .ThenBy(g => g[0].Path.Length)
            .ThenBy(g => g[0].Path, StringComparer.Ordinal)
            .ToList();
        return groups.Count > 0 ? groups[0] : [];
    }

    /// <summary>Метки квантизаций, доступных в репозитории (для сообщения об ошибке).</summary>
    internal static IReadOnlyList<string> AvailableQuants(IReadOnlyList<HfFile> files) =>
        files.Where(f => IsModelGguf(f.Path))
            .Select(f => QuantTag(f.Path))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Метка квантизации из имени файла: «Qwen3-Coder-Next-UD-Q4_K_XL.gguf» → «UD-Q4_K_XL».</summary>
    public static string? QuantTag(string path)
    {
        var stem = ShardRegex().Replace(FileName(path), "");
        if (stem.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) stem = stem[..^5];
        var m = QuantTagRegex().Match(stem);
        return m.Success ? m.Groups["tag"].Value : null;
    }

    private static bool IsModelGguf(string path)
    {
        var name = FileName(path);
        if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return false;
        // Проекторы изображений и модели-черновики (MTP/EAGLE/DFlash) — не основная модель.
        string[] skip = ["mmproj", "mtp-", "eagle", "dflash", "draft-", "imatrix"];
        return !skip.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase))
               && !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string GroupKey(string path) => ShardRegex().Replace(path, "");

    /// <summary>Имя без суффикса шарда: «m-00002-of-00003.gguf» → «m.gguf».</summary>
    internal static string StripShardSuffix(string fileName) => ShardRegex().Replace(fileName, "");

    internal static (int Index, int Total)? ShardInfo(string path)
    {
        var m = ShardRegex().Match(FileName(path));
        return m.Success ? (int.Parse(m.Groups["i"].Value), int.Parse(m.Groups["n"].Value)) : null;
    }

    private static bool IsCompleteGroup(List<HfFile> group)
    {
        var first = ShardInfo(group[0].Path);
        if (first is null) return group.Count == 1;
        var total = first.Value.Total;
        return group.Count == total
               && group.Select((f, i) => ShardInfo(f.Path) is { } s && s.Index == i + 1 && s.Total == total).All(ok => ok);
    }

    private static void ValidateRepo(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo) || !RepoRegex().IsMatch(repo))
            throw new ModelException(L.F("Неверное имя репозитория Hugging Face: «{0}» (ожидается «владелец/название»).", repo));
    }

    private static ModelException StatusError(HttpStatusCode code, string repo, string rev) => code switch
    {
        HttpStatusCode.NotFound => new ModelException(L.F("Репозиторий {0} (ревизия {1}) не найден на Hugging Face.", repo, rev)),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new ModelException(L.F("Доступ к репозиторию {0} ограничен: нужен вход на Hugging Face (переменная окружения HF_TOKEN).", repo)),
        HttpStatusCode.TooManyRequests => new ModelException(L.T("Hugging Face временно ограничил число запросов (429). Повторите через несколько минут.")),
        _ => new ModelException(L.F("Hugging Face вернул ошибку {0} при получении списка файлов {1}.", (int)code, repo)),
    };

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"-(?<i>\d{5})-of-(?<n>\d{5})(?=\.gguf$)", RegexOptions.IgnoreCase)]
    private static partial Regex ShardRegex();

    [GeneratedRegex(@"(?:^|[-_.])(?<tag>(?:UD-)?(?:I?Q\d(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4(?:_MOE)?|TQ\d_\d))$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantTagRegex();

    [GeneratedRegex(@"<(?<url>[^>]+)>\s*;\s*rel=""?(?<rel>[^"";,]+)""?")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepoRegex();
}
