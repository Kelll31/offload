using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.Llama;

/// <summary>
/// Получение релизов ggml-org/llama.cpp.
/// С августа 2026 «latest» на GitHub — семантическая версия только с исходниками и файлом nightly-tag.txt;
/// бинарные сборки лежат в ночных релизах bNNNNN (prerelease=true). Стабильный путь:
/// releases/latest/download/nightly-tag.txt (без квоты API) → releases/tags/{tag}.
/// Запасной: releases?per_page=10 → первый тег ^b\d+$ с нужной сборкой.
/// Последний удачный ответ кэшируется в llama-release-cache.json (TTL 1 ч; при ошибке сети — без срока).
/// </summary>
internal static class GitHubReleases
{
    /// <summary>Базовые адреса (тесты подменяют их на локальный сервер).</summary>
    internal static string ApiBase = "https://api.github.com";
    internal static string WebBase = "https://github.com";

    internal static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    internal static string CacheFile => Path.Combine(AppPaths.DataDir, "llama-release-cache.json");

    private static readonly Regex BuildTag = new(@"^b\d+$", RegexOptions.CultureInvariant);

    /// <summary>Номер сборки из тега bNNNNN (иначе -1).</summary>
    public static int BuildNumber(string? tag) =>
        tag is not null && tag.Length > 1 && (tag[0] == 'b' || tag[0] == 'B')
        && int.TryParse(tag.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;

    public static bool IsBuildTag(string? tag) => tag is not null && BuildTag.IsMatch(tag);

    /// <summary>Есть ли в релизе хоть одна сборка для Windows (для «общего» запроса без конкретного бэкенда).</summary>
    public static bool HasWindowsBuild(LlamaRelease r) =>
        r.Assets.Any(a => a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)
                          && a.Name.Contains("-bin-win-", StringComparison.OrdinalIgnoreCase)
                          && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public static async Task<LlamaRelease> GetLatestAsync(Func<LlamaRelease, bool> accept, string what, CancellationToken ct)
    {
        var cache = ReleaseCache.Load();
        if (cache is not null && DateTime.UtcNow - cache.FetchedAtUtc < CacheTtl && cache.Pick(accept) is { } fresh)
        {
            Log.Debug("llama", $"Релиз llama.cpp {fresh.Tag} взят из кэша");
            return fresh;
        }

        Exception? error = null;
        string? stableTag = null;
        var fetched = new List<LlamaRelease>();

        try
        {
            stableTag = await GetStableTagAsync(ct);
            if (stableTag is not null)
            {
                var rel = await GetReleaseByTagAsync(stableTag, ct);
                fetched.Add(rel);
                if (accept(rel))
                {
                    Save(stableTag, fetched, cache);
                    Log.Info("llama", $"Актуальный релиз llama.cpp: {rel.Tag} (стабильный)");
                    return rel;
                }
                Log.Info("llama", $"В стабильном релизе {rel.Tag} нет сборки «{what}», ищем в последних ночных");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex;
            Log.Warn("llama", $"Не удалось получить стабильный тег llama.cpp: {ex.Message}");
        }

        try
        {
            var list = await GetRecentReleasesAsync(10, ct);
            fetched.AddRange(list);
            Save(stableTag, fetched, cache);
            var pick = list.FirstOrDefault(accept);
            if (pick is not null)
            {
                Log.Info("llama", $"Актуальный релиз llama.cpp: {pick.Tag} (ночной)");
                return pick;
            }
            error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (error is null || ex is GitHubRateLimitException) error = ex;
            Log.Warn("llama", $"Не удалось получить список релизов llama.cpp: {ex.Message}");
        }

        if (cache?.Pick(accept) is { } stale)
        {
            Log.Warn("llama", $"Используются сохранённые сведения о релизе {stale.Tag} от {cache.FetchedAtUtc:u}");
            return stale;
        }

        if (error is null)
            throw new LlamaBuildNotFoundException($"В последних релизах llama.cpp нет сборки «{what}» для Windows.");
        throw new InvalidOperationException($"Не удалось получить сведения о релизах llama.cpp: {Describe(error)}", error);
    }

    private static string Describe(Exception ex) => ex switch
    {
        GitHubRateLimitException => ex.Message,
        HttpRequestException { StatusCode: null } => "нет подключения к GitHub. Проверьте интернет и повторите попытку.",
        HttpRequestException h => $"GitHub ответил ошибкой {(int)h.StatusCode!}.",
        TaskCanceledException => "GitHub не ответил вовремя. Повторите попытку позже.",
        _ => ex.Message,
    };

    /// <summary>Тег ночной сборки, на которую указывает последний стабильный релиз (nightly-tag.txt).</summary>
    internal static async Task<string?> GetStableTagAsync(CancellationToken ct)
    {
        var url = $"{WebBase}/{LlamaReleaseResolver.Repo}/releases/latest/download/nightly-tag.txt";
        using var resp = await Http.Api.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var text = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        if (IsBuildTag(text)) return text;
        Log.Warn("llama", $"nightly-tag.txt содержит неожиданное значение: «{(text.Length > 40 ? text[..40] : text)}»");
        return null;
    }

    internal static async Task<LlamaRelease> GetReleaseByTagAsync(string tag, CancellationToken ct)
    {
        var json = await GetApiAsync($"{ApiBase}/repos/{LlamaReleaseResolver.Repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct);
        using var doc = JsonDocument.Parse(json);
        return ParseRelease(doc.RootElement);
    }

    /// <summary>Последние релизы с тегами bNNNNN (новые первыми), без черновиков.</summary>
    internal static async Task<List<LlamaRelease>> GetRecentReleasesAsync(int count, CancellationToken ct)
    {
        var json = await GetApiAsync($"{ApiBase}/repos/{LlamaReleaseResolver.Repo}/releases?per_page={count}", ct);
        return ParseReleaseList(json);
    }

    internal static List<LlamaRelease> ParseReleaseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<LlamaRelease>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (!e.TryGetProperty("tag_name", out var t) || !IsBuildTag(t.GetString())) continue;
            list.Add(ParseRelease(e));
        }
        return list;
    }

    private static async Task<string> GetApiAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await Http.Api.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && (resp.StatusCode == HttpStatusCode.TooManyRequests
                || resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0"))
        {
            var reset = resp.Headers.TryGetValues("X-RateLimit-Reset", out var r)
                        && long.TryParse(r.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime()
                : (DateTimeOffset?)null;
            throw new GitHubRateLimitException(reset is null
                ? "превышен лимит запросов к GitHub API (60 в час). Повторите попытку позже."
                : $"превышен лимит запросов к GitHub API (60 в час). Повторите попытку после {reset:HH:mm}.");
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    internal static LlamaRelease ParseRelease(JsonElement e)
    {
        var tag = e.GetProperty("tag_name").GetString() ?? "";
        var published = DateTime.MinValue;
        foreach (var name in new[] { "published_at", "created_at" })
        {
            if (e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var dto))
            {
                published = dto.UtcDateTime;
                break;
            }
        }

        var assets = new List<LlamaAsset>();
        if (e.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                // Файл ещё загружается в релиз (state=starter) — пропускаем.
                if (a.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() != "uploaded") continue;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                assets.Add(new LlamaAsset(name, url, size, ParseDigest(a.TryGetProperty("digest", out var dg) ? dg.GetString() : null)));
            }
        }
        return new LlamaRelease(tag, published, assets);
    }

    /// <summary>"sha256:ABC…" → "abc…" (64 hex); прочие алгоритмы и мусор → null.</summary>
    internal static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static void Save(string? stableTag, List<LlamaRelease> fetched, ReleaseCache? previous)
    {
        if (fetched.Count == 0) return;
        var releases = fetched
            .GroupBy(r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => BuildNumber(r.Tag))
            .Take(12)
            .ToList();
        var cache = new ReleaseCache
        {
            FetchedAtUtc = DateTime.UtcNow,
            StableTag = stableTag ?? previous?.StableTag,
            Releases = releases,
        };
        cache.Save();
    }
}

internal sealed class GitHubRateLimitException(string message) : Exception(message);

/// <summary>Сеть доступна, но ни в одном из последних релизов нет нужной сборки.</summary>
internal sealed class LlamaBuildNotFoundException(string message) : InvalidOperationException(message);

/// <summary>Кэш последнего удачного ответа GitHub (защита от лимита 60 запросов/ч и работы без сети).</summary>
internal sealed class ReleaseCache
{
    public DateTime FetchedAtUtc { get; set; }
    public string? StableTag { get; set; }
    public List<LlamaRelease> Releases { get; set; } = [];

    /// <summary>Стабильный релиз, если подходит; иначе самый новый подходящий.</summary>
    public LlamaRelease? Pick(Func<LlamaRelease, bool> accept)
    {
        var stable = Releases.FirstOrDefault(r => string.Equals(r.Tag, StableTag, StringComparison.OrdinalIgnoreCase));
        if (stable is not null && accept(stable)) return stable;
        return Releases.OrderByDescending(r => GitHubReleases.BuildNumber(r.Tag)).FirstOrDefault(accept);
    }

    public static ReleaseCache? Load()
    {
        try
        {
            var path = GitHubReleases.CacheFile;
            if (!File.Exists(path)) return null;
            var c = JsonSerializer.Deserialize<ReleaseCache>(File.ReadAllText(path), Json.Options);
            if (c?.Releases is null) return null;
            c.Releases = c.Releases.Where(r => r?.Assets is not null && !string.IsNullOrEmpty(r.Tag)).ToList();
            return c;
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Кэш релизов не прочитан: {ex.Message}");
            return null;
        }
    }

    public void Save()
    {
        try
        {
            FileUtil.WriteAllTextAtomic(GitHubReleases.CacheFile, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (Exception ex)
        {
            Log.Debug("llama", $"Кэш релизов не сохранён: {ex.Message}");
        }
    }
}

/// <summary>Выбор архивов релиза по регулярным выражениям (подписи версий меняются: cuda-13.3 → 13.4, hip-radeon → rocm-10.0).</summary>
internal static class AssetSelector
{
    public static LlamaBuildSelection? Select(LlamaRelease release, LlamaBackend backend, bool arm64)
    {
        ArgumentNullException.ThrowIfNull(release);
        var arch = arm64 ? "arm64" : "x64";
        switch (backend)
        {
            case LlamaBackend.Cpu:
                return Simple(release, backend, $@"^llama-b\d+-bin-win-cpu-{arch}\.zip$");
            case LlamaBackend.Vulkan:
                return Simple(release, backend, $@"^llama-b\d+-bin-win-vulkan-{arch}\.zip$");
            case LlamaBackend.Sycl:
                return Simple(release, backend, $@"^llama-b\d+-bin-win-sycl-{arch}\.zip$");
            case LlamaBackend.Rocm:
            {
                // hip-radeon — старое название той же сборки (до ~b10900), версия считается нулевой.
                var main = Best(release, $@"^llama-b\d+-bin-win-(?:rocm-(?<v>\d+(?:\.\d+)*)|hip-radeon)-{arch}\.zip$");
                return main is null ? null : new LlamaBuildSelection(backend, release, main.Value.Asset, null);
            }
            case LlamaBackend.Cuda12:
            case LlamaBackend.Cuda13:
            {
                var major = backend == LlamaBackend.Cuda12 ? "12" : "13";
                var main = Best(release, $@"^llama-b\d+-bin-win-cuda-(?<v>{major}\.\d+)-{arch}\.zip$");
                if (main is null) return null;
                var runtimes = Matches(release, $@"^cudart-llama-bin-win-cuda-(?<v>{major}\.\d+)-{arch}\.zip$");
                // Runtime той же версии; иначе — более новый той же major-версии (cudart64_NN.dll обратно совместим),
                // но не старее сборки: в старом cudart может не быть нужных функций.
                var rt = runtimes.FirstOrDefault(x => x.Version == main.Value.Version).Asset
                         ?? runtimes.Where(x => x.Version > main.Value.Version).OrderBy(x => x.Version).FirstOrDefault().Asset;
                // Без cudart сборка CUDA не загрузит ggml-cuda.dll — считаем, что сборки нет.
                return rt is null ? null : new LlamaBuildSelection(backend, release, main.Value.Asset, rt);
            }
            default:
                throw new ArgumentException("Сборку «Автоматически» нужно сначала разрешить через Recommend().", nameof(backend));
        }
    }

    private static LlamaBuildSelection? Simple(LlamaRelease release, LlamaBackend backend, string pattern)
    {
        var m = Best(release, pattern);
        return m is null ? null : new LlamaBuildSelection(backend, release, m.Value.Asset, null);
    }

    private static (LlamaAsset Asset, Version Version)? Best(LlamaRelease release, string pattern)
    {
        var list = Matches(release, pattern);
        return list.Count == 0 ? null : list.OrderByDescending(x => x.Version).First();
    }

    private static List<(LlamaAsset Asset, Version Version)> Matches(LlamaRelease release, string pattern)
    {
        var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var list = new List<(LlamaAsset, Version)>();
        foreach (var a in release.Assets)
        {
            var m = rx.Match(a.Name);
            if (!m.Success) continue;
            list.Add((a, ParseVersion(m.Groups["v"].Success ? m.Groups["v"].Value : null)));
        }
        return list;
    }

    private static Version ParseVersion(string? s)
    {
        if (string.IsNullOrEmpty(s)) return new Version(0, 0);
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out var v) ? v : new Version(0, 0);
    }

    /// <summary>Версия CUDA из имени архива (например, «13.4»), иначе null.</summary>
    public static string? CudaVersionOf(LlamaAsset? asset)
    {
        if (asset is null) return null;
        var m = Regex.Match(asset.Name, @"-cuda-(\d+\.\d+)-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }
}
