using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.Core.Update;

/// <summary>Файл релиза Offload на GitHub.</summary>
public sealed record AppReleaseAsset(string Name, string Url, long Size);

/// <summary>Релиз Offload: тег (v1.2.3), версия без «v», дата, страница релиза и файлы.</summary>
public sealed record AppRelease(string Tag, string Version, DateTime PublishedUtc, string? HtmlUrl, IReadOnlyList<AppReleaseAsset> Assets)
{
    public AppReleaseAsset? Asset(string name) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Релизы самого Offload (Kelll31/offload): последний стабильный релиз через GitHub API (releases/latest — без черновиков
/// и предварительных версий), контрольные суммы — из файла SHA256SUMS.txt того же релиза.
/// Ответ кэшируется в app-release-cache.json: автоматическая проверка ходит в сеть не чаще раза в <see cref="CheckInterval"/>,
/// при ошибке сети используется сохранённый ответ. Соглашения (заголовки API, User-Agent) — как у релизов llama.cpp.
/// </summary>
public static partial class AppReleases
{
    public const string Repo = "Kelll31/offload";

    /// <summary>Файл контрольных сумм релиза (формат sha256sum: «hex  имя»).</summary>
    public const string ChecksumsAsset = "SHA256SUMS.txt";

    /// <summary>Портативный exe в релизе.</summary>
    public const string PortableAsset = "Offload.exe";

    /// <summary>Базовый адрес API (тесты подменяют его на локальный сервер).</summary>
    internal static string ApiBase = "https://api.github.com";

    /// <summary>
    /// Базовый адрес загрузок релизов (тесты подменяют его). Ответ API (или зеркала API) не выбирает, откуда качать:
    /// принимаются только адреса «https://github.com/Kelll31/offload/releases/download/…».
    /// </summary>
    internal static string WebBase = "https://github.com";

    /// <summary>Как часто автоматическая проверка обращается к GitHub.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    internal static string CacheFile => Path.Combine(AppPaths.DataDir, "app-release-cache.json");

    /// <summary>Установщик релиза: Offload-Setup-1.2.3.exe.</summary>
    public static string SetupAssetName(string version) => $"Offload-Setup-{version}.exe";

    /// <summary>
    /// Последний релиз (null — релизов нет). force=false — ответ моложе <see cref="CheckInterval"/> берётся из кэша без сети.
    /// Ошибка сети при наличии кэша не выбрасывается: возвращается сохранённый ответ.
    /// </summary>
    public static async Task<AppRelease?> GetLatestAsync(bool force, CancellationToken ct)
    {
        var cache = LoadCache();
        if (!force && cache is not null && DateTime.UtcNow - cache.FetchedAtUtc < CheckInterval && DateTime.UtcNow >= cache.FetchedAtUtc)
        {
            Log.Debug("update", $"Сведения о релизе Offload взяты из кэша ({cache.Release?.Tag ?? "релизов нет"})");
            return cache.Release;
        }

        try
        {
            var release = await FetchLatestAsync(ct).ConfigureAwait(false);
            SaveCache(new ReleaseCacheFile { FetchedAtUtc = DateTime.UtcNow, Release = release });
            return release;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (cache is not null && ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            Log.Warn("update", $"Не удалось проверить обновления Offload ({ex.Message}), используются сведения от {cache.FetchedAtUtc:u}");
            return cache.Release;
        }
    }

    private static async Task<AppRelease?> FetchLatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{Repo}/releases/latest");
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await Http.Api.SendAsync(req, ct).ConfigureAwait(false);
        // 404 — в репозитории ещё нет ни одного опубликованного релиза.
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException(L.T("превышен лимит запросов к GitHub API (60 в час). Повторите попытку позже."), null, resp.StatusCode);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseRelease(json);
    }

    /// <summary>Разбор ответа releases/latest (черновик и предварительная версия не считаются релизом).</summary>
    internal static AppRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (e.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = e.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var version = NormalizeVersion(tag);
        if (tag is null || version is null) return null;

        var published = DateTime.MinValue;
        if (e.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String && pa.TryGetDateTimeOffset(out var dto))
            published = dto.UtcDateTime;
        var html = e.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;

        var assets = new List<AppReleaseAsset>();
        if (e.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                if (!NetworkOptions.IsReleaseAssetUrl(url, Repo, WebBase)) continue;
                if (a.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() != "uploaded") continue;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                assets.Add(new AppReleaseAsset(name, url, size));
            }
        }
        return new AppRelease(tag, version, published, html, assets);
    }

    /// <summary>
    /// «v1.2.3» → «1.2.3»; тег не строгого вида (<see cref="IsValidVersion"/>) → null. Версия попадает в имя файла
    /// установщика и в командную строку cmd, поэтому пути («1.0/../../x»), кавычки и метасимволы cmd («&amp;», «%») отсекаются здесь.
    /// </summary>
    public static string? NormalizeVersion(string? tag)
    {
        var v = tag?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v[0] is 'v' or 'V') v = v[1..];
        return IsValidVersion(v) ? v : null;
    }

    /// <summary>Версия релиза вида 1.2, 1.2.3, 1.2.3.4 с необязательным суффиксом «-rc1» / «-beta.2» (только ASCII-цифры, буквы и точки).</summary>
    public static bool IsValidVersion(string? version) => version is not null && VersionRegex().IsMatch(version);

    [GeneratedRegex(@"\A[0-9]+(\.[0-9]+){1,3}(-[0-9A-Za-z.]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    /// <summary>
    /// Сравнение версий вида 1.2.3[-суффикс][+сборка]: числовые части по порядку (до четырёх), при равенстве версия
    /// с суффиксом (1.2.3-beta) младше версии без него. &lt;0 — a старше b.
    /// </summary>
    public static int CompareVersions(string? a, string? b)
    {
        static (int[] Parts, string? Pre) Parse(string? v)
        {
            var s = (v ?? "0").Trim().TrimStart('v', 'V').Split('+')[0];
            var dash = s.IndexOf('-', StringComparison.Ordinal);
            var pre = dash >= 0 ? s[(dash + 1)..] : null;
            var core = dash >= 0 ? s[..dash] : s;
            var parts = core.Split('.')
                .Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .Concat([0, 0, 0, 0]).Take(4).ToArray();
            return (parts, string.IsNullOrEmpty(pre) ? null : pre);
        }

        var (x, xp) = Parse(a);
        var (y, yp) = Parse(b);
        for (var i = 0; i < 4; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        if (xp is null && yp is null) return 0;
        if (xp is null) return 1;
        if (yp is null) return -1;
        return string.Compare(xp, yp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Разбор SHA256SUMS.txt: строки «hex  имя» или «hex *имя» (формат sha256sum). Имя без пути; некорректные строки пропускаются.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length < 66 || line.StartsWith('#')) continue;
            var hex = line[..64];
            if (!hex.All(Uri.IsHexDigit) || !char.IsWhiteSpace(line[64])) continue;
            var name = line[65..].Trim().TrimStart('*').Trim();
            if (name.Length == 0) continue;
            name = name.Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            map[name] = hex.ToLowerInvariant();
        }
        return map;
    }

    /// <summary>
    /// SHA-256 файла релиза из SHA256SUMS.txt. Нет файла сумм или строки для файла — обновление не ставится
    /// (кроме осознанного обхода <see cref="DownloadPolicy.AllowUnverifiedEnvVar"/>=1, тогда null).
    /// </summary>
    public static async Task<string?> GetSha256Async(AppRelease release, string assetName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        string? sha = null;
        if (release.Asset(ChecksumsAsset) is { } sums)
        {
            using var resp = await Http.Api.GetAsync(sums.Url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            ParseChecksums(text).TryGetValue(assetName, out sha);
        }
        if (sha is null && !DownloadPolicy.AllowUnverified)
            throw new DownloadException(L.F("В релизе {0} нет контрольной суммы файла {1} ({2}) — обновление не установлено: непроверенные файлы не устанавливаются.",
                release.Tag, assetName, ChecksumsAsset));
        DownloadPolicy.RequireChecksum(sha, assetName, AppInfo.Name);
        return sha;
    }

    // ---------- Кэш ----------

    internal sealed class ReleaseCacheFile
    {
        public DateTime FetchedAtUtc { get; set; }
        public AppRelease? Release { get; set; }
    }

    private static ReleaseCacheFile? LoadCache()
    {
        try
        {
            var path = CacheFile;
            if (!File.Exists(path)) return null;
            var c = JsonSerializer.Deserialize<ReleaseCacheFile>(File.ReadAllText(path), Json.Options);
            // Испорченный или подменённый кэш (тег/версия не строгого вида, версия не из тега) не используется — ответ берётся из сети заново.
            if (c?.Release is { } r && (r.Assets is null || NormalizeVersion(r.Tag) is not { } v || !string.Equals(v, r.Version, StringComparison.Ordinal)))
                return null;
            return c;
        }
        catch (Exception ex)
        {
            Log.Debug("update", $"Кэш релиза Offload не прочитан: {ex.Message}");
            return null;
        }
    }

    private static void SaveCache(ReleaseCacheFile cache)
    {
        try
        {
            FileUtil.WriteAllTextAtomic(CacheFile, JsonSerializer.Serialize(cache, Json.Options));
        }
        catch (Exception ex)
        {
            Log.Debug("update", $"Кэш релиза Offload не сохранён: {ex.Message}");
        }
    }
}
