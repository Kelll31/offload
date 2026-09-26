using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.OpenCode;

internal sealed record OpenCodeAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>Релиз OpenCode. Version = null — версия неизвестна (GitHub недоступен, ссылка releases/latest).</summary>
internal sealed record OpenCodeRelease(string? Tag, string? Version, List<OpenCodeAsset> Assets, bool FromApi);

/// <summary>
/// Сведения о релизах anomalyco/opencode. Основной путь — GitHub API releases/latest (sha256 в поле digest),
/// ответ кэшируется в opencode-release-cache.json на 1 ч. При лимите API (60 запросов/ч) — тег из
/// перенаправления github.com/…/releases/latest и прямые ссылки без контрольной суммы.
/// </summary>
internal static class OpenCodeReleases
{
    internal const string Repo = "anomalyco/opencode";

    internal const string AssetX64 = "opencode-windows-x64.zip";
    internal const string AssetX64Baseline = "opencode-windows-x64-baseline.zip";
    internal const string AssetArm64 = "opencode-windows-arm64.zip";

    /// <summary>Базовые адреса (тесты подменяют их на локальный сервер).</summary>
    internal static string ApiBase = "https://api.github.com";
    internal static string WebBase = "https://github.com";

    internal static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    internal static string CacheFile => Path.Combine(AppPaths.DataDir, "opencode-release-cache.json");

    public static async Task<OpenCodeRelease> GetLatestAsync(CancellationToken ct)
    {
        var cache = ReleaseCacheFile.Load();
        if (cache?.Release is { } cached && DateTime.UtcNow - cache.FetchedAtUtc < CacheTtl)
        {
            Log.Debug("opencode", $"Релиз OpenCode {cached.Tag} взят из кэша");
            return cached;
        }

        Exception apiError;
        try
        {
            var json = await GetApiAsync($"{ApiBase}/repos/{Repo}/releases/latest", ct);
            var release = ParseRelease(json);
            new ReleaseCacheFile { FetchedAtUtc = DateTime.UtcNow, Release = release }.Save();
            Log.Info("opencode", $"Последний релиз OpenCode: {release.Tag}");
            return release;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            apiError = ex;
            Log.Warn("opencode", $"GitHub API (релизы OpenCode) недоступен: {ex.Message}");
        }

        if (cache?.Release is { } stale)
        {
            Log.Warn("opencode", $"Используются сохранённые сведения о релизе OpenCode {stale.Tag} от {cache.FetchedAtUtc:u}");
            return stale;
        }

        try
        {
            var tag = await GetLatestTagViaRedirectAsync(ct);
            if (tag is not null)
            {
                Log.Info("opencode", $"Последний релиз OpenCode (по перенаправлению): {tag}");
                return FallbackRelease(tag);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("opencode", $"Не удалось определить последний тег OpenCode: {ex.Message}");
        }

        // Лимит API при живой сети — пробуем «плавающую» ссылку releases/latest/download.
        if (apiError is OpenCodeRateLimitException) return FallbackRelease(null);
        throw new InvalidOperationException(L.F("Не удалось получить сведения о релизах OpenCode: {0}", Describe(apiError)), apiError);
    }

    /// <summary>
    /// Проверенная версия OpenCode (канал «stable»): с ней Offload протестирован (формат событий <c>run --format json</c>,
    /// <c>experimental.continue_loop_on_deny</c>, переменные <c>OPENCODE_*</c>). Размеры и SHA-256 зашиты, как у ripgrep, —
    /// установка не зависит от GitHub API и его лимита и не примет подменённый файл.
    /// </summary>
    /// <remarks>
    /// Источник значений: поля <c>size</c> и <c>digest</c> ответа GitHub API releases/latest с тегом v1.18.32 (сохранённый
    /// Offload кэш opencode-release-cache.json от 2026-09-23; эта версия установлена и проверена в работе агентных инструментов).
    /// Обновляя версию: взять size/digest трёх файлов opencode-windows-*.zip нового релиза из
    /// <c>https://api.github.com/repos/anomalyco/opencode/releases/tags/vX.Y.Z</c>, прогнать RunEventsContractTests
    /// на записанном выводе новой версии и только потом менять <see cref="PinnedVersion"/>.
    /// </remarks>
    internal const string PinnedVersion = "1.18.32";

    internal static readonly IReadOnlyDictionary<string, (long Size, string Sha256)> PinnedAssets =
        new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [AssetX64] = (62101772, "1483c72d5adced825590a0ecf8cc18b3e87e535960a125dbf539d33bce135d0f"),
            [AssetX64Baseline] = (62101771, "cd852831bd094c2df2eb379eb98bed7a63db7f823a7caf277c732cdac33cbdb6"),
            [AssetArm64] = (60591906, "5c1c21e85b694ac3fedccff22f934484c29273d5b5780eff006960304108e124"),
        };

    /// <summary>Проверенный релиз: прямые ссылки на тег <see cref="PinnedVersion"/> с зашитыми размерами и SHA-256.</summary>
    internal static OpenCodeRelease Pinned()
    {
        var tag = "v" + PinnedVersion;
        var baseUrl = $"{WebBase}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/";
        var assets = PinnedAssets.Select(a => new OpenCodeAsset(a.Key, baseUrl + a.Key, a.Value.Size, a.Value.Sha256)).ToList();
        return new OpenCodeRelease(tag, PinnedVersion, assets, FromApi: false);
    }

    /// <summary>Релиз без API: прямые ссылки на файлы (без размера и контрольной суммы).</summary>
    internal static OpenCodeRelease FallbackRelease(string? tag)
    {
        var baseUrl = tag is null
            ? $"{WebBase}/{Repo}/releases/latest/download/"
            : $"{WebBase}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/";
        var assets = new[] { AssetX64, AssetX64Baseline, AssetArm64 }
            .Select(n => new OpenCodeAsset(n, baseUrl + n, 0, null))
            .ToList();
        return new OpenCodeRelease(tag, VersionFromTag(tag), assets, FromApi: false);
    }

    /// <summary>
    /// Предпочтительные сборки: ARM64 — родная, затем x64-baseline под эмуляцией;
    /// x64 без AVX2 — только baseline (обычная сборка упадёт с «недопустимой инструкцией»).
    /// </summary>
    internal static string[] PreferredAssets(Architecture arch, bool avx2) => arch switch
    {
        Architecture.Arm64 => [AssetArm64, AssetX64Baseline, AssetX64],
        Architecture.X64 => avx2 ? [AssetX64, AssetX64Baseline] : [AssetX64Baseline],
        _ => [],
    };

    public static OpenCodeAsset? SelectAsset(OpenCodeRelease release, Architecture arch, bool avx2)
    {
        foreach (var name in PreferredAssets(arch, avx2))
        {
            var a = release.Assets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (a is not null) return a;
        }
        return null;
    }

    internal static string? VersionFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        return t.StartsWith('v') || t.StartsWith('V') ? t[1..] : t;
    }

    internal static OpenCodeRelease ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        var tag = e.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException(L.T("В ответе GitHub нет tag_name."));

        var assets = new List<OpenCodeAsset>();
        if (e.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                // Файл ещё загружается в релиз — пропускаем.
                if (a.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() != "uploaded") continue;
                if (!IsTrustedAssetUrl(url))
                {
                    // Адрес и digest приходят из одного ответа: файл с чужого хоста или репозитория с «подходящей» суммой не берём.
                    Log.Warn("opencode", $"Файл релиза {name} отброшен: адрес не ведёт на github.com/{Repo}/releases/download/");
                    continue;
                }
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                assets.Add(new OpenCodeAsset(name, url, size, ParseDigest(digest)));
            }
        }
        return new OpenCodeRelease(tag, VersionFromTag(tag), assets, FromApi: true);
    }

    /// <summary>
    /// Адрес файла релиза — только https://github.com/anomalyco/opencode/releases/download/… (или тот же путь от
    /// <see cref="WebBase"/> в тестах): зеркало API не выбирает, откуда качать; зеркало загрузок подставится позже.
    /// </summary>
    internal static bool IsTrustedAssetUrl(string? url) => NetworkOptions.IsReleaseAssetUrl(url, Repo, WebBase);

    /// <summary>"sha256:ABC…" → "abc…" (64 hex); прочие алгоритмы и мусор → null.</summary>
    internal static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>github.com/…/releases/latest перенаправляет на …/releases/tag/vX.Y.Z — без квоты API.</summary>
    internal static async Task<string?> GetLatestTagViaRedirectAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, $"{WebBase}/{Repo}/releases/latest");
        using var resp = await Http.Api.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return TagFromReleaseUrl(resp.RequestMessage?.RequestUri);
    }

    internal static string? TagFromReleaseUrl(Uri? uri)
    {
        if (uri is null) return null;
        var m = Regex.Match(uri.AbsolutePath, @"/releases/tag/(?<t>[^/]+)/?$", RegexOptions.CultureInvariant);
        return m.Success ? Uri.UnescapeDataString(m.Groups["t"].Value) : null;
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
            throw new OpenCodeRateLimitException(reset is null
                ? L.T("превышен лимит запросов к GitHub API (60 в час). Повторите попытку позже.")
                : L.F("превышен лимит запросов к GitHub API (60 в час). Повторите попытку после {0:HH:mm}.", reset));
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    internal static string Describe(Exception ex) => ex switch
    {
        OpenCodeRateLimitException => ex.Message,
        HttpRequestException { StatusCode: null } => L.T("нет подключения к GitHub. Проверьте интернет и повторите попытку."),
        HttpRequestException h => L.F("GitHub ответил ошибкой {0}.", (int)h.StatusCode!),
        TaskCanceledException => L.T("GitHub не ответил вовремя. Повторите попытку позже."),
        _ => ex.Message,
    };
}

internal sealed class OpenCodeRateLimitException(string message) : Exception(message);

/// <summary>Кэш последнего удачного ответа GitHub API.</summary>
internal sealed class ReleaseCacheFile
{
    public DateTime FetchedAtUtc { get; set; }
    public OpenCodeRelease? Release { get; set; }

    public static ReleaseCacheFile? Load()
    {
        try
        {
            var path = OpenCodeReleases.CacheFile;
            if (!File.Exists(path)) return null;
            var c = JsonSerializer.Deserialize<ReleaseCacheFile>(File.ReadAllText(path), Json.Options);
            if (c?.Release?.Assets is null || string.IsNullOrEmpty(c.Release.Tag)) return null;
            // Кэш мог быть записан до проверки адресов — такой кэш не используется.
            if (!c.Release.Assets.All(a => OpenCodeReleases.IsTrustedAssetUrl(a.Url))) return null;
            return c;
        }
        catch (Exception ex)
        {
            Log.Debug("opencode", $"Кэш релизов OpenCode не прочитан: {ex.Message}");
            return null;
        }
    }

    public void Save()
    {
        try
        {
            FileUtil.WriteAllTextAtomic(OpenCodeReleases.CacheFile, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (Exception ex)
        {
            Log.Debug("opencode", $"Кэш релизов OpenCode не сохранён: {ex.Message}");
        }
    }
}
