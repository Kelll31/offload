using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Offload.Core.Logging;

namespace Offload.Core.Net;

public enum DownloadStage { Connecting, Downloading, Verifying, Completed }

/// <summary>Прогресс загрузки. TotalBytes может быть неизвестен (null).</summary>
public sealed record DownloadProgress(
    DownloadStage Stage,
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond,
    string FileName)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;

    public TimeSpan? Eta => TotalBytes is > 0 && BytesPerSecond > 1
        ? TimeSpan.FromSeconds((TotalBytes.Value - BytesReceived) / BytesPerSecond)
        : null;
}

public sealed class DownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Параметры загрузки <see cref="HttpDownloader"/>.</summary>
public sealed record DownloadOptions
{
    public static DownloadOptions Default { get; } = new();

    /// <summary>
    /// Дополнительные заголовки каждого запроса (например, Authorization). Токен Hugging Face добавляется и без этого —
    /// общим клиентом <see cref="Http"/> и только для хаба (<see cref="NetworkOptions.AuthorizationFor"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Число параллельных соединений для больших файлов; 1 — всегда одно соединение.</summary>
    public int Connections { get; init; } = 4;

    /// <summary>С какого размера файла (известного заранее) загружать частями в несколько соединений.</summary>
    public long ParallelThresholdBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Размер одной части; 0 — автоматически (от 32 МБ, около 8 частей на соединение).</summary>
    public long SegmentBytes { get; init; }
}

/// <summary>
/// Загрузка больших файлов с докачкой (HTTP Range), повторами и проверкой SHA-256.
/// Данные пишутся в "&lt;dest&gt;.part", по завершении файл атомарно переименовывается. Большие файлы известного размера
/// загружаются частями в несколько соединений (Range-запросы); какие части готовы — в "&lt;dest&gt;.part.chunks",
/// поэтому докачка работает и в этом режиме. Сервер без поддержки Range — обычная загрузка одним потоком.
/// </summary>
public static class HttpDownloader
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxAttempts = 8;
    private const long MinSegmentBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MapSaveInterval = TimeSpan.FromSeconds(2);

    public static Task DownloadFileAsync(
        string url,
        string destinationPath,
        long? expectedSize = null,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default) =>
        DownloadFileAsync(url, destinationPath, expectedSize, expectedSha256, progress, DownloadOptions.Default, ct);

    public static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        long? expectedSize,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress,
        DownloadOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var fileName = Path.GetFileName(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        if (File.Exists(destinationPath) && expectedSize is > 0 && new FileInfo(destinationPath).Length == expectedSize)
        {
            if (expectedSha256 is null || await VerifyShaAsync(destinationPath, expectedSha256, expectedSize, fileName, progress, ct))
            {
                progress?.Report(new DownloadProgress(DownloadStage.Completed, expectedSize.Value, expectedSize, 0, fileName));
                return;
            }
        }

        var partPath = destinationPath + ".part";
        var mapPath = partPath + ".chunks";
        var parallel = options.Connections > 1 && expectedSize is { } size && size >= Math.Max(1, options.ParallelThresholdBytes);
        // Начатая одним потоком загрузка (прежняя версия или сервер без Range) продолжается одним потоком — не с нуля.
        if (parallel && File.Exists(partPath) && !File.Exists(mapPath)) parallel = false;
        if (!parallel) DiscardChunkState(partPath, mapPath);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long? total;
                if (parallel && await ParallelAttemptAsync(url, partPath, mapPath, expectedSize!.Value, options, fileName, progress, ct))
                {
                    total = expectedSize;
                }
                else
                {
                    if (parallel)
                    {
                        // Сервер не поддерживает Range: дальше — одним потоком, с начала.
                        parallel = false;
                        DiscardChunkState(partPath, mapPath);
                        Log.Info("download", $"{fileName}: сервер не поддерживает загрузку частями — одно соединение.");
                    }
                    total = await DownloadAttemptAsync(url, partPath, expectedSize, fileName, options, progress, ct);
                }

                var actual = new FileInfo(partPath).Length;
                if (total is > 0 && actual != total)
                    throw new IOException(L.F("Размер файла не совпадает: получено {0}, ожидалось {1}.", actual, total));

                if (expectedSha256 is not null &&
                    !await VerifyShaAsync(partPath, expectedSha256, actual, fileName, progress, ct))
                {
                    TryDelete(partPath);
                    TryDelete(mapPath);
                    throw new DownloadException(L.F("Контрольная сумма SHA-256 файла {0} не совпадает. Файл удалён, повторите загрузку.", fileName));
                }

                File.Move(partPath, destinationPath, overwrite: true);
                progress?.Report(new DownloadProgress(DownloadStage.Completed, actual, actual, 0, fileName));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
            {
                lastError = ex;
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } denied &&
                    NetworkOptions.IsHfUrl(url))
                    throw new DownloadException(L.F("Нет доступа к файлу {0} на Hugging Face (ошибка {1}). Для закрытых моделей укажите токен в разделе «Настройки» → «Сеть» и примите условия модели на её странице.", fileName, (int)denied.StatusCode!.Value), ex);
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                    break;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                Log.Warn("download", $"Попытка {attempt}/{MaxAttempts} загрузки {fileName} не удалась: {ex.Message}. Повтор через {delay.TotalSeconds:0} с.");
                await Task.Delay(delay, ct);
            }
        }

        throw new DownloadException(L.F("Не удалось загрузить {0}: {1}", fileName, lastError?.Message), lastError);
    }

    private static HttpRequestMessage NewRequest(string url, DownloadOptions options)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (options.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }

    /// <summary>Удалить незаконченную загрузку частями (и сам .part — он заполнен не подряд).</summary>
    private static void DiscardChunkState(string partPath, string mapPath)
    {
        if (!File.Exists(mapPath)) return;
        TryDelete(partPath);
        TryDelete(mapPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("download", $"Не удалось удалить {path}: {ex.Message}");
        }
    }

    // ── Одно соединение ─────────────────────────────────────────────────────────────

    /// <returns>Полный размер файла, если сервер его сообщил.</returns>
    private static async Task<long?> DownloadAttemptAsync(
        string url, string partPath, long? expectedSize, string fileName, DownloadOptions options,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (expectedSize is > 0 && existing > expectedSize)
        {
            File.Delete(partPath);
            existing = 0;
        }
        if (expectedSize is > 0 && existing == expectedSize)
            return expectedSize;

        progress?.Report(new DownloadProgress(DownloadStage.Connecting, existing, expectedSize, 0, fileName));

        using var request = NewRequest(url, options);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await Http.Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Файл уже полностью скачан, либо на сервере изменился — начинаем заново.
            var len = response.Content.Headers.ContentRange?.Length;
            if (len is not null && len == existing) return len;
            File.Delete(partPath);
            throw new IOException(L.T("Сервер отклонил докачку, загрузка начнётся заново."));
        }

        response.EnsureSuccessStatusCode();

        long? total;
        FileMode mode;
        if (response.StatusCode == HttpStatusCode.PartialContent && existing > 0)
        {
            total = response.Content.Headers.ContentRange?.Length ?? expectedSize;
            mode = FileMode.Append;
        }
        else
        {
            existing = 0;
            total = response.Content.Headers.ContentLength ?? expectedSize;
            mode = FileMode.Create;
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(partPath, mode, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        var received = existing;
        var sw = Stopwatch.StartNew();
        long bytesAtLastTick = received;
        var lastTick = TimeSpan.Zero;
        double speed = 0;

        while (true)
        {
            var read = await ReadWithTimeoutAsync(source, buffer, ct);
            if (read == 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            var now = sw.Elapsed;
            if ((now - lastTick).TotalMilliseconds >= 250)
            {
                var instant = (received - bytesAtLastTick) / (now - lastTick).TotalSeconds;
                speed = speed <= 0 ? instant : speed * 0.8 + instant * 0.2;
                bytesAtLastTick = received;
                lastTick = now;
                progress?.Report(new DownloadProgress(DownloadStage.Downloading, received, total, speed, fileName));
            }
        }

        await target.FlushAsync(ct);
        progress?.Report(new DownloadProgress(DownloadStage.Downloading, received, total, speed, fileName));
        return total;
    }

    private static async Task<int> ReadWithTimeoutAsync(Stream source, Memory<byte> buffer, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(ReadTimeout);
        try
        {
            return await source.ReadAsync(buffer, readCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(L.T("Сервер перестал отвечать при загрузке."));
        }
    }

    // ── Несколько соединений ───────────────────────────────────────────────────────

    /// <summary>Сервер ответил на Range-запрос всем файлом (200) — загрузка частями невозможна.</summary>
    private sealed class RangeNotSupportedException : Exception;

    /// <summary>Часть файла: [Start, Start + Length), загружено Done байт от начала части.</summary>
    private sealed class Segment(long start, long length, long done)
    {
        private long _done = done;

        public long Start { get; } = start;
        public long Length { get; } = length;
        public long Done => Interlocked.Read(ref _done);
        public void Advance(long bytes) => Interlocked.Add(ref _done, bytes);
    }

    private sealed record SegmentState(long Start, long Length, long Done);

    private sealed record ChunkMap(long Total, List<SegmentState> Segments);

    /// <returns>true — файл собран в .part; false — сервер не поддерживает Range.</returns>
    private static async Task<bool> ParallelAttemptAsync(
        string url, string partPath, string mapPath, long total, DownloadOptions options, string fileName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var segments = LoadMap(mapPath, partPath, total);
        if (segments is null)
        {
            // Новая загрузка частями (прежний .part одним потоком не продолжить: части пишутся не подряд).
            segments = Split(total, options);
            await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                fs.SetLength(total);
            SaveMap(mapPath, total, segments);
        }

        long Received() => segments.Sum(s => s.Done);
        var pending = new ConcurrentQueue<Segment>(segments.Where(s => s.Done < s.Length));
        if (pending.IsEmpty)
        {
            TryDelete(mapPath);
            return true;
        }

        progress?.Report(new DownloadProgress(DownloadStage.Connecting, Received(), total, 0, fileName));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, Math.Min(options.Connections, pending.Count))
            .Select(_ => Task.Run(async () =>
            {
                while (pending.TryDequeue(out var segment))
                    await DownloadSegmentAsync(url, partPath, total, segment, options, cts.Token);
            }, cts.Token))
            .ToList();
        var all = Task.WhenAll(workers);

        var sw = Stopwatch.StartNew();
        var lastTick = TimeSpan.Zero;
        var lastSave = TimeSpan.Zero;
        long bytesAtLastTick = Received();
        double speed = 0;
        try
        {
            while (!all.IsCompleted)
            {
                await Task.WhenAny(all, Task.Delay(250, CancellationToken.None));
                // Первая ошибка любой части останавливает остальные: повтор — во внешнем цикле, с сохранённой карты.
                if (!cts.IsCancellationRequested && workers.Any(w => w.IsFaulted)) await cts.CancelAsync();

                var now = sw.Elapsed;
                var received = Received();
                if ((now - lastTick).TotalMilliseconds >= 250)
                {
                    var instant = (received - bytesAtLastTick) / Math.Max(0.001, (now - lastTick).TotalSeconds);
                    speed = speed <= 0 ? instant : speed * 0.8 + instant * 0.2;
                    bytesAtLastTick = received;
                    lastTick = now;
                    progress?.Report(new DownloadProgress(DownloadStage.Downloading, received, total, speed, fileName));
                }
                if (now - lastSave >= MapSaveInterval)
                {
                    SaveMap(mapPath, total, segments);
                    lastSave = now;
                }
            }
            await all;
        }
        catch (Exception)
        {
            await cts.CancelAsync();
            try { await all; } catch (Exception) { /* причина — ниже */ }
            SaveMap(mapPath, total, segments);
            ct.ThrowIfCancellationRequested();
            var errors = workers.Where(w => w.IsFaulted).SelectMany(w => w.Exception!.InnerExceptions).ToList();
            if (errors.OfType<RangeNotSupportedException>().Any()) return false;
            // Настоящая причина важнее отмены соседних частей.
            var cause = errors.FirstOrDefault(e => e is not OperationCanceledException) ?? errors.FirstOrDefault();
            if (cause is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cause);
            throw;
        }

        if (segments.Any(s => s.Done < s.Length))
        {
            SaveMap(mapPath, total, segments);
            throw new IOException(L.T("Загрузка частями завершилась не полностью, продолжаю."));
        }
        progress?.Report(new DownloadProgress(DownloadStage.Downloading, total, total, speed, fileName));
        TryDelete(mapPath);
        return true;
    }

    private static List<Segment> Split(long total, DownloadOptions options)
    {
        var size = options.SegmentBytes > 0
            ? options.SegmentBytes
            : Math.Max(MinSegmentBytes, (total + options.Connections * 8 - 1) / (options.Connections * 8));
        var list = new List<Segment>();
        for (long start = 0; start < total; start += size)
            list.Add(new Segment(start, Math.Min(size, total - start), 0));
        return list;
    }

    private static async Task DownloadSegmentAsync(
        string url, string partPath, long total, Segment segment, DownloadOptions options, CancellationToken ct)
    {
        var from = segment.Start + segment.Done;
        var to = segment.Start + segment.Length - 1;
        if (from > to) return;

        using var request = NewRequest(url, options);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await Http.Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.OK) throw new RangeNotSupportedException();
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new IOException(L.T("Сервер отклонил запрос части файла."));
        response.EnsureSuccessStatusCode();

        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != from || (range.Length is { } len && len != total))
            throw new IOException(L.T("Сервер вернул не ту часть файла (файл на сервере изменился?)."));

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        // Без буфера FileStream: «готово» в карте частей отмечается только после записи в файл.
        await using var target = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1, useAsync: true);
        target.Seek(from, SeekOrigin.Begin);

        var buffer = new byte[BufferSize];
        var remaining = to - from + 1;
        while (remaining > 0)
        {
            // Копим до 1 МБ и пишем разом: меньше системных вызовов при тех же гарантиях.
            var filled = 0;
            var limit = (int)Math.Min(buffer.Length, remaining);
            while (filled < limit)
            {
                var read = await ReadWithTimeoutAsync(source, buffer.AsMemory(filled, limit - filled), ct);
                if (read == 0) break;
                filled += read;
            }
            if (filled == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, filled), ct);
            segment.Advance(filled);
            remaining -= filled;
            if (filled < limit) break;
        }
        if (remaining > 0) throw new IOException(L.T("Соединение оборвалось при загрузке части файла."));
    }

    /// <summary>Карта частей прежней попытки; null — нет, повреждена или не соответствует файлу .part.</summary>
    private static List<Segment>? LoadMap(string mapPath, string partPath, long total)
    {
        try
        {
            if (!File.Exists(mapPath) || !File.Exists(partPath) || new FileInfo(partPath).Length != total) return null;
            var map = JsonSerializer.Deserialize<ChunkMap>(File.ReadAllText(mapPath));
            if (map is null || map.Total != total || map.Segments is not { Count: > 0 }) return null;
            long expectedStart = 0;
            foreach (var s in map.Segments)
            {
                if (s.Start != expectedStart || s.Length <= 0 || s.Done < 0 || s.Done > s.Length) return null;
                expectedStart += s.Length;
            }
            return expectedStart == total ? map.Segments.Select(s => new Segment(s.Start, s.Length, s.Done)).ToList() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("download", $"Карта частей {mapPath} не читается ({ex.Message}) — загрузка начнётся заново.");
            return null;
        }
    }

    private static void SaveMap(string mapPath, long total, List<Segment> segments)
    {
        try
        {
            var map = new ChunkMap(total, segments.Select(s => new SegmentState(s.Start, s.Length, s.Done)).ToList());
            var tmp = mapPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map));
            File.Move(tmp, mapPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не страшно: при докачке части загрузятся повторно.
            Log.Warn("download", $"Не удалось сохранить карту частей {mapPath}: {ex.Message}");
        }
    }

    // ── Проверка ───────────────────────────────────────────────────────────────────

    public static async Task<bool> VerifyShaAsync(
        string path, string expectedSha256, long? size, string fileName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new DownloadProgress(DownloadStage.Verifying, 0, size, 0, fileName));
        var actual = await ComputeSha256Async(path, p =>
            progress?.Report(new DownloadProgress(DownloadStage.Verifying, p, size, 0, fileName)), ct);
        var ok = string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!ok) Log.Warn("download", $"SHA-256 {fileName}: ожидалось {expectedSha256}, получено {actual}");
        return ok;
    }

    public static async Task<string> ComputeSha256Async(string path, Action<long>? onProgress = null, CancellationToken ct = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        long done = 0;
        var sw = Stopwatch.StartNew();
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            done += read;
            if (sw.ElapsedMilliseconds >= 250)
            {
                onProgress?.Invoke(done);
                sw.Restart();
            }
        }
        onProgress?.Invoke(done);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
