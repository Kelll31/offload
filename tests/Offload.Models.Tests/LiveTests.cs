using System.Net.Http.Headers;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.Models.Tests;

/// <summary>
/// Проверки против настоящего Hugging Face. Запускаются только при OFFLOAD_LIVE=1;
/// папка результатов — OFFLOAD_LIVE_DIR (там же остаётся скачанная маленькая модель и live-report.txt).
/// </summary>
[Collection("AppPaths")]
public class LiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("OFFLOAD_LIVE") == "1";

    private static string LiveDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("OFFLOAD_LIVE_DIR");
            dir = string.IsNullOrWhiteSpace(dir) ? Path.Combine(Path.GetTempPath(), "pc-live-models") : dir;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static void Report(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss} {line}";
        File.AppendAllText(Path.Combine(LiveDir, "live-report.txt"), text + Environment.NewLine);
        TestContext.Current.SendDiagnosticMessage(text);
    }

    [Fact]
    public async Task Live_CatalogMatchesHfTree()
    {
        Assert.SkipUnless(Enabled, "OFFLOAD_LIVE != 1");
        var ct = TestContext.Current.CancellationToken;
        var mismatches = new List<string>();
        Report("=== Сверка каталога с HF tree API ===");
        foreach (var group in ModelCatalog.All.GroupBy(m => (m.Repo, m.Revision)))
        {
            var files = await HfClient.ListFilesAsync(group.Key.Repo, group.Key.Revision, ct);
            var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            foreach (var m in group)
            foreach (var f in m.Files!)
            {
                if (!byPath.TryGetValue(f.Path, out var live))
                {
                    mismatches.Add($"{m.Id}: нет файла {f.Path}");
                    continue;
                }
                var ok = live.Size == f.Size && string.Equals(live.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!ok) mismatches.Add($"{m.Id}: {f.Path} каталог {f.Size}/{f.Sha256} ≠ HF {live.Size}/{live.Sha256}");
                Report($"{(ok ? "OK " : "BAD")} {m.Id,-24} {f.Quant,-11} {f.Path} {f.Size} {f.Sha256?[..12]}");
            }
        }
        Report(mismatches.Count == 0 ? "Расхождений нет." : "РАСХОЖДЕНИЯ:\n" + string.Join("\n", mismatches));
        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task Live_ResolveNonCatalogQuant_ViaTree()
    {
        Assert.SkipUnless(Enabled, "OFFLOAD_LIVE != 1");
        var m = ModelCatalog.Find("qwen3.5-2b-q8")!;
        var r = await HfClient.ResolveAsync(m, "Q6_K", TestContext.Current.CancellationToken);
        Report($"Resolve {m.Repo} Q6_K → {string.Join(", ", r.Files.Select(f => $"{f.Path} {f.Size} {f.Sha256}"))}");
        Assert.Equal("Qwen3.5-2B-Q6_K.gguf", r.Files.Single().Path);
        Assert.Matches("^[0-9a-f]{64}$", r.Files.Single().Sha256!);
    }

    [Fact]
    public async Task Live_RangeSurvivesHfRedirect()
    {
        Assert.SkipUnless(Enabled, "OFFLOAD_LIVE != 1");
        var m = ModelCatalog.Find("qwen3.5-2b-q8")!;
        var f = m.DefaultFile!;
        using var req = new HttpRequestMessage(HttpMethod.Get, HfClient.DownloadUrl(m.Repo, f.Path, m.Revision));
        req.Headers.Range = new RangeHeaderValue(1000, 1999);
        using var resp = await Http.Download.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Report($"Range 1000-1999 через 302: статус {(int)resp.StatusCode}, Content-Range {resp.Content.Headers.ContentRange}, " +
               $"получено {body.Length} байт, итоговый хост {resp.RequestMessage?.RequestUri?.Host}");
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal(1000, body.Length);
        Assert.Equal(f.Size, resp.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task Live_DownloadSmallModel_WithResume()
    {
        Assert.SkipUnless(Enabled, "OFFLOAD_LIVE != 1");
        var ct = TestContext.Current.CancellationToken;
        var model = ModelCatalog.All.Where(x => x.ApproxSizeBytes <= 2_100_000_000L).MinBy(x => x.ApproxSizeBytes)!;
        var home = Path.Combine(LiveDir, "home");
        Directory.CreateDirectory(home);
        AppPaths.OverrideDataDir(home);
        try
        {
            ConfigStore.Reload();
            ConfigStore.Update(c => c.Models.ModelsDir = LiveDir);
            var dest = Path.Combine(LiveDir, model.DefaultFile!.Path);
            Report($"=== Загрузка {model.Id} ({model.DefaultQuant}, {model.ApproxSizeBytes} байт) → {dest}");

            if (!File.Exists(dest))
            {
                // 1. Прерываем загрузку примерно на 25 %.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var cancelAt = model.ApproxSizeBytes / 4;
                var raw = new InlineProgress(p =>
                {
                    if (p.Fraction is { } fr && fr * model.ApproxSizeBytes >= cancelAt) cts.Cancel();
                });
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelManager.DownloadAsync(model, null, raw, cts.Token));
                var partLen = new FileInfo(dest + ".part").Length;
                Report($"Отмена: .part = {partLen} байт");
                Assert.True(partLen > 0);

                // 2. Повторный запуск должен продолжить с размера .part (206), а не начать заново.
                long firstReported = -1;
                string? firstDetail = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resume = new InlineProgress(p =>
                {
                    if (firstReported < 0 && p.Stage.StartsWith("Загрузка модели") && p.Fraction is { } fr)
                    {
                        firstReported = (long)(fr * model.ApproxSizeBytes);
                        firstDetail = p.Detail;
                    }
                });
                var installed = await ModelManager.DownloadAsync(model, null, resume, ct);
                Report($"Докачка: первый отчёт ≈{firstReported} байт ({firstDetail}); .part был {partLen}; время {sw.Elapsed.TotalSeconds:0} с");
                Assert.True(firstReported >= partLen - 1024, $"загрузка началась заново: {firstReported} < {partLen}");
                Assert.Equal(dest, installed.FilePath);
            }
            else
            {
                Report("Файл уже скачан ранее — проверка SHA-256 и регистрация.");
                await ModelManager.DownloadAsync(model, null, null, ct);
            }

            var sha = await HttpDownloader.ComputeSha256Async(dest, null, ct);
            Report($"SHA-256 {sha} (ожидалось {model.DefaultFile.Sha256}) размер {new FileInfo(dest).Length}");
            Assert.Equal(model.DefaultFile.Sha256, sha);

            var info = GgufReader.Read(dest);
            var kv = info.ToKvSpec();
            Report($"GGUF v{info.Version}: arch={info.Architecture} name={info.Name} ctx={info.ContextLength} blocks={info.BlockCount} " +
                   $"heads={info.HeadCount} kv_heads={info.HeadCountKv} key_len={info.KeyLength} emb={info.EmbeddingLength} " +
                   $"fai={info.FullAttentionInterval} nextn={info.NextNPredictLayers} moe={info.IsMoe} tensors={info.TensorCount} " +
                   $"template={info.ChatTemplate?.Length} симв. (enable_thinking: {info.ChatTemplate?.Contains("enable_thinking")}) " +
                   $"→ KvSpec {kv.Layers}×{kv.KvHeads}×{kv.HeadDim} = {FitCalculator.KvBytesPerToken(kv, "f16")} Б/токен");
            Assert.Equal(model.Architecture, info.Architecture);
            Assert.Equal(model.Kv.Layers, kv.Layers);
            Assert.Equal(FitCalculator.KvBytesPerToken(model.Kv, "f16"), FitCalculator.KvBytesPerToken(kv, "f16"));

            var cfg = ConfigStore.Reload();
            var entry = Assert.Single(cfg.Models.Installed, e => e.Id == model.Id);
            Report($"Зарегистрирована: {entry.Id} {entry.FilePath} {entry.SizeBytes} активна={cfg.Models.ActiveModelId == entry.Id}");
        }
        finally
        {
            AppPaths.OverrideDataDir(null);
        }
    }

    private sealed class InlineProgress(Action<StepProgress> a) : IProgress<StepProgress>
    {
        public void Report(StepProgress value) => a(value);
    }
}
