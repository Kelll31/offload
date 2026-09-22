using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

/// <summary>
/// Загрузка больших файлов с докачкой (HTTP Range), повторами и проверкой SHA-256.
/// Данные пишутся в "<dest>.part", по завершении файл атомарно переименовывается.
/// </summary>
public static class HttpDownloader
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxAttempts = 8;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    public static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        long? expectedSize = null,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
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
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var total = await DownloadAttemptAsync(url, partPath, expectedSize, fileName, progress, ct);
                var actual = new FileInfo(partPath).Length;
                if (total is > 0 && actual != total)
                    throw new IOException($"Размер файла не совпадает: получено {actual}, ожидалось {total}.");

                if (expectedSha256 is not null &&
                    !await VerifyShaAsync(partPath, expectedSha256, actual, fileName, progress, ct))
                {
                    File.Delete(partPath);
                    throw new DownloadException($"Контрольная сумма SHA-256 файла {fileName} не совпадает. Файл удалён, повторите загрузку.");
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
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                    break;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                Log.Warn("download", $"Попытка {attempt}/{MaxAttempts} загрузки {fileName} не удалась: {ex.Message}. Повтор через {delay.TotalSeconds:0} с.");
                await Task.Delay(delay, ct);
            }
        }

        throw new DownloadException($"Не удалось загрузить {fileName}: {lastError?.Message}", lastError);
    }

    /// <returns>Полный размер файла, если сервер его сообщил.</returns>
    private static async Task<long?> DownloadAttemptAsync(
        string url, string partPath, long? expectedSize, string fileName,
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

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await Http.Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Файл уже полностью скачан, либо на сервере изменился — начинаем заново.
            var len = response.Content.Headers.ContentRange?.Length;
            if (len is not null && len == existing) return len;
            File.Delete(partPath);
            throw new IOException("Сервер отклонил докачку, загрузка начнётся заново.");
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
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ReadTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Сервер перестал отвечать при загрузке.");
            }
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
