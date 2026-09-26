using System.Security.Cryptography;
using System.Text.Json;
using Offload.Core.Net;

namespace Offload.Core.Tests;

/// <summary>Загрузка частями в несколько соединений (HttpDownloader): Range-чанки, докачка по карте частей, откат на один поток.</summary>
public sealed class ParallelDownloadTests
{
    private const int Segment = 256 * 1024;

    private static readonly DownloadOptions Parallel = new()
    {
        Connections = 4,
        ParallelThresholdBytes = 1024 * 1024,
        SegmentBytes = Segment,
    };

    private static byte[] RandomData(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "pc-pdl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Parallel_DownloadsRangesConcurrently_AndVerifiesSha()
    {
        var data = RandomData(3 * 1024 * 1024 + 123, 1);
        using var server = new RangeServer(data) { DelayMs = 40 };
        var dir = TempDir();
        var dest = Path.Combine(dir, "f.bin");
        try
        {
            await HttpDownloader.DownloadFileAsync(server.FileUrl, dest, data.Length, Sha(data), null, Parallel,
                TestContext.Current.CancellationToken);

            Assert.Equal(data, await File.ReadAllBytesAsync(dest, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(dest + ".part"), ".part должен быть переименован");
            Assert.False(File.Exists(dest + ".part.chunks"), "карта частей должна быть удалена");
            var ranges = server.Requests.Select(r => r.Range).ToList();
            Assert.Equal((data.Length + Segment - 1) / Segment, ranges.Count);
            Assert.All(ranges, r => Assert.Matches(@"^bytes=\d+-\d+$", r!));
            Assert.True(server.MaxConcurrent >= 2, $"части должны качаться одновременно, было не больше {server.MaxConcurrent}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Parallel_ResumesFromChunkMap_WithoutRedownloadingDoneParts()
    {
        var data = RandomData(2 * 1024 * 1024, 2);
        using var server = new RangeServer(data);
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "f.bin");
        try
        {
            // Прежняя попытка: первые 3 части готовы, четвёртая — наполовину, остальное — мусор.
            var part = RandomData(data.Length, 99);
            var resumeFrom = 3 * Segment + Segment / 2;
            Array.Copy(data, part, resumeFrom);
            await File.WriteAllBytesAsync(dest + ".part", part, TestContext.Current.CancellationToken);
            var segments = Enumerable.Range(0, data.Length / Segment).Select(i => new
            {
                Start = (long)i * Segment,
                Length = (long)Segment,
                Done = i < 3 ? Segment : i == 3 ? Segment / 2 : 0L,
            });
            await File.WriteAllTextAsync(dest + ".part.chunks", JsonSerializer.Serialize(new { Total = (long)data.Length, Segments = segments }),
                TestContext.Current.CancellationToken);

            await HttpDownloader.DownloadFileAsync(server.FileUrl, dest, data.Length, Sha(data), null, Parallel,
                TestContext.Current.CancellationToken);

            Assert.Equal(data, await File.ReadAllBytesAsync(dest, TestContext.Current.CancellationToken));
            var starts = server.Requests.Select(r => long.Parse(r.Range!["bytes=".Length..].Split('-')[0])).ToList();
            Assert.DoesNotContain(starts, s => s < resumeFrom);
            Assert.Contains((long)resumeFrom, starts);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Parallel_ConnectionDrop_RetriesAndCompletes()
    {
        var data = RandomData(2 * 1024 * 1024, 3);
        using var server = new RangeServer(data) { FailOnceAfterBytes = 100 * 1024 };
        var dir = TempDir();
        var dest = Path.Combine(dir, "f.bin");
        try
        {
            await HttpDownloader.DownloadFileAsync(server.FileUrl, dest, data.Length, Sha(data), null, Parallel,
                TestContext.Current.CancellationToken);
            Assert.Equal(data, await File.ReadAllBytesAsync(dest, TestContext.Current.CancellationToken));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Parallel_ServerWithoutRange_FallsBackToSingleStream()
    {
        var data = RandomData(1536 * 1024, 4);
        using var server = new RangeServer(data) { SupportRange = false };
        var dir = TempDir();
        var dest = Path.Combine(dir, "f.bin");
        try
        {
            await HttpDownloader.DownloadFileAsync(server.FileUrl, dest, data.Length, Sha(data), null, Parallel,
                TestContext.Current.CancellationToken);
            Assert.Equal(data, await File.ReadAllBytesAsync(dest, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(dest + ".part.chunks"));
            Assert.Contains(server.Requests, r => r.Range is null);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Parallel_WrongSha_DeletesPartAndChunkMap()
    {
        var data = RandomData(1024 * 1024 + 7, 5);
        using var server = new RangeServer(data);
        var dir = TempDir();
        var dest = Path.Combine(dir, "f.bin");
        try
        {
            await Assert.ThrowsAsync<DownloadException>(() => HttpDownloader.DownloadFileAsync(server.FileUrl, dest, data.Length,
                new string('0', 64), null, Parallel, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(dest));
            Assert.False(File.Exists(dest + ".part"));
            Assert.False(File.Exists(dest + ".part.chunks"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task SmallFile_UsesSingleStream_AndSendsCustomHeaders()
    {
        var data = RandomData(200 * 1024, 6);
        using var server = new RangeServer(data);
        var dir = TempDir();
        var dest = Path.Combine(dir, "f.bin");
        try
        {
            var options = Parallel with { Headers = new Dictionary<string, string> { ["X-Test"] = "offload" } };
            await HttpDownloader.DownloadFileAsync(server.FileUrl, dest, data.Length, Sha(data), null, options,
                TestContext.Current.CancellationToken);
            Assert.Equal(data, await File.ReadAllBytesAsync(dest, TestContext.Current.CancellationToken));
            var request = Assert.Single(server.Requests);
            Assert.Null(request.Range);
            Assert.Equal("offload", request.TestHeader);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Parallel_SendsCustomHeadersWithEveryRange()
    {
        var data = RandomData(1024 * 1024, 7);
        using var server = new RangeServer(data);
        var dir = TempDir();
        var dest = Path.Combine(dir, "f.bin");
        try
        {
            var options = Parallel with { Headers = new Dictionary<string, string> { ["X-Test"] = "chunk" } };
            await HttpDownloader.DownloadFileAsync(server.FileUrl, dest, data.Length, Sha(data), null, options,
                TestContext.Current.CancellationToken);
            Assert.Equal(4, server.Requests.Count);
            Assert.All(server.Requests, r => Assert.Equal("chunk", r.TestHeader));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
