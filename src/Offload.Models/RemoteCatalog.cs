using System.Globalization;
using System.Text;
using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Security;
using Offload.Core.Util;

namespace Offload.Models;

public enum CatalogUpdateStatus
{
    /// <summary>Удалённый каталог выключен в настройках или в сборке нет открытого ключа.</summary>
    Disabled,
    /// <summary>Проверка была меньше суток назад.</summary>
    NotDue,
    /// <summary>Удалённый каталог не новее действующего.</summary>
    UpToDate,
    /// <summary>Загружен и применён более новый каталог.</summary>
    Updated,
    /// <summary>Нет связи, неверная подпись или каталог не прошёл проверку — действует прежний каталог.</summary>
    Failed,
}

/// <summary>Итог проверки удалённого каталога; Version — версия действующего каталога после проверки.</summary>
public sealed record CatalogUpdateResult(CatalogUpdateStatus Status, long Version, string? Error = null);

/// <summary>Состояние последней проверки (catalog\state.json в папке данных).</summary>
public sealed record CatalogCheckState(DateTime LastCheckUtc, long RemoteVersion, string? LastError);

/// <summary>
/// Удалённый каталог моделей (ROADMAP §9.3, решение §14): catalog.json и catalog.json.sig (base64 подписи Ed25519
/// точных байтов файла) по адресу из настроек, по умолчанию — <see cref="DefaultUrl"/>. Подпись проверяется открытым
/// ключом, зашитым в сборку; затем — <see cref="ModelCatalog.ParseDocument"/> (те же проверки, что у встроенного).
/// Применяется только каталог с версией выше действующей (без отката на старый), хранится в папке данных
/// (catalog\catalog.signed.json — подпись и байты каталога одним файлом, одной атомарной заменой) и при следующих
/// запусках снова проверяется по подписи. Защита от повтора старого подписанного каталога: наибольшая принятая
/// версия хранится отдельно (catalog\max-version) и переживает порчу или удаление кэша, а необязательное подписанное
/// поле «expires» ограничивает срок действия каталога (и после полной очистки папки данных). Проверка — не чаще раза
/// в сутки; без сети или при любой ошибке действует прежний (встроенный) каталог.
/// </summary>
public static class RemoteCatalog
{
    /// <summary>catalog/catalog.json в ветке main репозитория Offload (публикация — catalog/README.md).</summary>
    public const string DefaultUrl = "https://raw.githubusercontent.com/Kelll31/offload/main/catalog/catalog.json";

    /// <summary>
    /// ЗАГЛУШКА: открытый ключ Ed25519 мейнтейнера — 32 байта в base64 (вывод <c>scripts/sign-catalog.ps1 -PublicKey</c>).
    /// Пока строка пуста, удалённый каталог отключён: проверять подпись нечем. Закрытый ключ в репозиторий не попадает.
    /// </summary>
    internal const string PublicKeyBase64 = ""; // TODO(мейнтейнер): вставить открытый ключ каталога

    /// <summary>Только для тестов: свой открытый ключ вместо зашитого.</summary>
    internal static byte[]? PublicKeyOverride { get; set; }

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private const int MaxCatalogBytes = 8 * 1024 * 1024;
    private const int MaxSignatureBytes = 4 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static string CacheDir => Path.Combine(AppPaths.DataDir, "catalog");

    /// <summary>Кэш: {"signature": base64 подписи, "catalog": base64 точных байтов каталога} — один файл, одна замена.</summary>
    internal static string CacheFile => Path.Combine(CacheDir, "catalog.signed.json");

    /// <summary>Прежний формат кэша: каталог и подпись двумя файлами — читается, если нового файла нет.</summary>
    internal static string LegacyCacheFile => Path.Combine(CacheDir, "catalog.json");

    internal static string LegacySignatureFile => Path.Combine(CacheDir, "catalog.json.sig");

    /// <summary>Наибольшая когда-либо принятая версия удалённого каталога (десятичное число).</summary>
    internal static string MaxVersionFile => Path.Combine(CacheDir, "max-version");

    internal static string StateFile => Path.Combine(CacheDir, "state.json");

    /// <summary>В сборку зашит открытый ключ — удалённый каталог может работать.</summary>
    public static bool IsAvailable => PublicKey is not null;

    /// <summary>Адрес каталога: из настроек или <see cref="DefaultUrl"/>.</summary>
    public static string Url => NetworkOptions.NormalizeBase(Settings.CatalogUrl) ?? DefaultUrl;

    /// <summary>Последняя проверка; null — ещё не было.</summary>
    public static CatalogCheckState? LastCheck
    {
        get
        {
            try
            {
                return File.Exists(StateFile) ? JsonSerializer.Deserialize<CatalogCheckState>(File.ReadAllText(StateFile), Json.Options) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    private static NetworkSettings Settings => ConfigStore.Current.Network ?? new NetworkSettings();

    private static byte[]? PublicKey
    {
        get
        {
            if (PublicKeyOverride is { Length: Ed25519.PublicKeySize } k) return k;
            if (string.IsNullOrWhiteSpace(PublicKeyBase64)) return null;
            try
            {
                var bytes = Convert.FromBase64String(PublicKeyBase64);
                return bytes.Length == Ed25519.PublicKeySize ? bytes : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>Проверить обновление каталога, если с прошлой проверки прошло больше суток. Сетевые ошибки не выбрасываются.</summary>
    public static Task<CatalogUpdateResult> RefreshIfDueAsync(CancellationToken ct = default) => RefreshAsync(force: false, ct);

    /// <summary>
    /// Загрузить каталог и подпись, проверить и применить, если версия выше действующей. Любая ошибка (нет сети,
    /// неверная подпись, каталог не прошёл проверку) — <see cref="CatalogUpdateStatus.Failed"/>, прежний каталог остаётся.
    /// </summary>
    public static async Task<CatalogUpdateResult> RefreshAsync(bool force, CancellationToken ct = default)
    {
        if (PublicKey is not { } key || !Settings.RemoteCatalog)
            return new CatalogUpdateResult(CatalogUpdateStatus.Disabled, ModelCatalog.Current.Version);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var last = LastCheck;
            var now = DateTime.UtcNow;
            if (!force && last is not null && last.LastCheckUtc <= now && now - last.LastCheckUtc < CheckInterval)
                return new CatalogUpdateResult(CatalogUpdateStatus.NotDue, ModelCatalog.Current.Version);

            var url = Url;
            try
            {
                if (NetworkOptions.ValidateMirror(url) is { } badUrl) throw new InvalidDataException(badUrl);
                var json = await FetchAsync(url, MaxCatalogBytes, ct).ConfigureAwait(false);
                var signature = Encoding.ASCII.GetString(await FetchAsync(url + ".sig", MaxSignatureBytes, ct).ConfigureAwait(false));
                var snapshot = VerifyAndParse(json, signature, key);

                CheckNotExpired(snapshot, now);
                var current = ModelCatalog.Current.Version;
                var maxAccepted = MaxAcceptedVersion;
                if (snapshot.Version < maxAccepted && snapshot.Version > current)
                {
                    // Кэш потерян или испорчен, а сервер (или подменённый адрес) отдаёт старый подписанный каталог.
                    throw new InvalidDataException(L.F("Удалённый каталог моделей версии {0} старше уже принятого ранее ({1}) — откат не принимается.",
                        snapshot.Version, maxAccepted));
                }
                if (snapshot.Version <= current)
                {
                    if (snapshot.Version < current)
                        Log.Warn("catalog", $"Удалённый каталог версии {snapshot.Version} старше действующего ({current}) — не применяется.");
                    SaveState(new CatalogCheckState(now, snapshot.Version, null));
                    return new CatalogUpdateResult(CatalogUpdateStatus.UpToDate, current);
                }

                SaveCache(json, signature);
                SaveMaxVersion(snapshot.Version);
                ModelCatalog.ApplyRemote(snapshot);
                SaveState(new CatalogCheckState(now, snapshot.Version, null));
                Log.Info("catalog", $"Каталог моделей обновлён: версия {snapshot.Version} ({snapshot.Models.Count} моделей) из {url}");
                return new CatalogUpdateResult(CatalogUpdateStatus.Updated, snapshot.Version);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                           or UnauthorizedAccessException or OperationCanceledException)
            {
                var message = ex is OperationCanceledException ? L.T("Сервер каталога не ответил вовремя.") : ex.Message;
                Log.Warn("catalog", $"Проверка удалённого каталога ({url}) не удалась: {message}");
                SaveState(new CatalogCheckState(now, last?.RemoteVersion ?? 0, message));
                return new CatalogUpdateResult(CatalogUpdateStatus.Failed, ModelCatalog.Current.Version, message);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Каталог из кэша в папке данных, заново проверенный по подписи; null — нет кэша, удалённый каталог выключен
    /// или кэш не прошёл проверку (тогда действует встроенный).
    /// </summary>
    internal static CatalogSnapshot? LoadCached()
    {
        if (PublicKey is not { } key) return null;
        try
        {
            // Сначала файлы: без кэша настройки не читаются.
            byte[] json;
            string signature;
            if (File.Exists(CacheFile))
            {
                if (!Settings.RemoteCatalog) return null;
                (json, signature) = ReadEnvelope(File.ReadAllBytes(CacheFile));
            }
            else if (File.Exists(LegacyCacheFile) && File.Exists(LegacySignatureFile))
            {
                if (!Settings.RemoteCatalog) return null;
                json = File.ReadAllBytes(LegacyCacheFile);
                signature = File.ReadAllText(LegacySignatureFile);
            }
            else
            {
                return null;
            }
            var snapshot = VerifyAndParse(json, signature, key);
            CheckNotExpired(snapshot, DateTime.UtcNow);
            var maxAccepted = MaxAcceptedVersion;
            if (snapshot.Version < maxAccepted)
                throw new InvalidDataException(L.F("Удалённый каталог моделей версии {0} старше уже принятого ранее ({1}) — откат не принимается.",
                    snapshot.Version, maxAccepted));
            // Кэш прежнего формата (до файла max-version) тоже задаёт нижнюю границу версии.
            if (snapshot.Version > maxAccepted) SaveMaxVersion(snapshot.Version);
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("catalog", $"Сохранённый удалённый каталог не принят ({ex.Message}) — действует встроенный.");
            return null;
        }
    }

    /// <summary>Наибольшая принятая версия удалённого каталога; 0 — ещё не было (или файл повреждён).</summary>
    internal static long MaxAcceptedVersion
    {
        get
        {
            try
            {
                return File.Exists(MaxVersionFile)
                       && long.TryParse(File.ReadAllText(MaxVersionFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)
                    ? v
                    : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>Каталог с полем «expires» в прошлом не принимается (ни при загрузке, ни из кэша). Ошибка — InvalidDataException.</summary>
    internal static void CheckNotExpired(CatalogSnapshot snapshot, DateTime nowUtc)
    {
        if (snapshot.Expires is { } expires && expires.UtcDateTime <= nowUtc)
            throw new InvalidDataException(L.F("Срок действия удалённого каталога моделей истёк {0} — он не применяется.",
                expires.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    /// <summary>Проверить подпись точных байтов каталога и разобрать его. Ошибка — InvalidDataException.</summary>
    internal static CatalogSnapshot VerifyAndParse(byte[] json, string signatureBase64, byte[] publicKey)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(L.T("Подпись удалённого каталога моделей повреждена (ожидался base64)."), ex);
        }
        if (!Ed25519.Verify(publicKey, json, signature))
            throw new InvalidDataException(L.T("Подпись удалённого каталога моделей не прошла проверку."));

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(json);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(L.T("Удалённый каталог моделей — не текст UTF-8."), ex);
        }
        return ModelCatalog.ParseDocument(text.TrimStart('﻿'));
    }

    private static async Task<byte[]> FetchAsync(string url, int maxBytes, CancellationToken ct)
    {
        using var response = await Http.Api.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException(L.F("Файл {0} слишком большой для каталога моделей.", url));
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException(L.F("Файл {0} слишком большой для каталога моделей.", url));
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Подпись и каталог — одним файлом одной атомарной заменой: параллельный читатель (трей и MCP-процесс) не увидит
    /// новую подпись со старым каталогом. Прежняя пара файлов удаляется, чтобы не читаться вместо нового.
    /// </summary>
    private static void SaveCache(byte[] json, string signature)
    {
        Directory.CreateDirectory(CacheDir);
        WriteAtomic(CacheFile, JsonSerializer.SerializeToUtf8Bytes(new CacheEnvelope(1, signature.Trim(), Convert.ToBase64String(json)), Json.Options));
        foreach (var legacy in (string[])[LegacyCacheFile, LegacySignatureFile])
        {
            try
            {
                File.Delete(legacy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug("catalog", $"Прежний файл кэша каталога не удалён: {ex.Message}");
            }
        }
    }

    /// <summary>Разобрать файл кэша: байты каталога и подпись. Ошибка — InvalidDataException.</summary>
    internal static (byte[] Json, string Signature) ReadEnvelope(byte[] data)
    {
        CacheEnvelope? e;
        byte[] json;
        try
        {
            e = JsonSerializer.Deserialize<CacheEnvelope>(data, Json.Options);
            json = Convert.FromBase64String(e?.Catalog ?? "");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new InvalidDataException(L.T("Сохранённый удалённый каталог моделей повреждён."), ex);
        }
        if (e?.Signature is not { Length: > 0 } signature || json.Length == 0)
            throw new InvalidDataException(L.T("Сохранённый удалённый каталог моделей повреждён."));
        return (json, signature);
    }

    /// <summary>Запомнить наибольшую принятую версию (не уменьшается).</summary>
    private static void SaveMaxVersion(long version)
    {
        if (version <= MaxAcceptedVersion) return;
        try
        {
            WriteAtomic(MaxVersionFile, Encoding.ASCII.GetBytes(version.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("catalog", $"Не удалось сохранить версию принятого каталога: {ex.Message}");
        }
    }

    /// <summary>Файл кэша: формат, подпись (base64) и точные байты каталога (base64).</summary>
    private sealed record CacheEnvelope(int Format, string? Signature, string? Catalog);

    private static void SaveState(CatalogCheckState state)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            WriteAtomic(StateFile, JsonSerializer.SerializeToUtf8Bytes(state, Json.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("catalog", $"Не удалось сохранить состояние проверки каталога: {ex.Message}");
        }
    }

    /// <summary>Запись через временный файл с уникальным именем (трей и MCP-процесс могут писать одновременно).</summary>
    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
