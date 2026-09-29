using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Llama.Tests;

/// <summary>Автоподбор параметров (LlamaAutoTune): поиск на поддельных замерах, сеанс с восстановлением, профили в конфиге.</summary>
public sealed class AutoTuneTests
{
    private static readonly TuneCandidate Base = new("auto", "q8_0", 0, 0, 10, null, null);

    private static readonly TuneLimits Limits = new(40, ["f16", "q8_0"], MultiGpu: false);

    /// <summary>Замер по таблице «ключ набора → секунды типичного запроса» (по умолчанию — как у исходного).</summary>
    private sealed class FakeBench(Func<TuneCandidate, double?> seconds)
    {
        public List<TuneCandidate> Calls { get; } = [];

        public Task<TuneMeasurement> MeasureAsync(TuneCandidate c, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(c);
            if (seconds(c) is not { } s) throw new LlamaServerException("CUDA error: out of memory");
            // Оценка = 4000/pp + 400/tg; при pp = tg·10 обе части равны s/2.
            var tg = 800 / s;
            return Task.FromResult(new TuneMeasurement(tg * 10, tg));
        }
    }

    private static AutoTuneOptions Options(int maxTrials = 30, Func<TimeSpan>? elapsed = null) =>
        new() { MaxTrials = maxTrials, Elapsed = elapsed ?? (() => TimeSpan.Zero) };

    [Fact]
    public void Score_TypicalCodingRequest()
    {
        var o = new AutoTuneOptions();
        Assert.Equal(4000 / 1000d + 400 / 40d, LlamaAutoTune.Score(new TuneMeasurement(1000, 40), o), 6);
    }

    [Fact]
    public async Task Search_CoordinateDescent_PicksBestPerDimension()
    {
        var bench = new FakeBench(c =>
        {
            var s = 10.0;
            if (c.CpuMoeLayers == 9) s -= 1.5;      // на слой меньше на ЦП — быстрее
            if (c.CpuMoeLayers == 8) return null;   // на два — не помещается
            if (c.UBatch == 1024) s -= 1.0;          // больший ub ускоряет промпт
            if (c.FlashAttention == "off") s += 2;
            if (c.CacheType == "f16") s -= 0.1;      // в пределах шума
            return s;
        });
        var dims = LlamaAutoTune.CreateDimensions(Base, Limits);
        var r = await LlamaAutoTune.SearchAsync(Base, dims, bench.MeasureAsync, Options(), ct: TestContext.Current.CancellationToken);

        Assert.NotNull(r.Winner);
        Assert.Equal(9, r.Winner.CpuMoeLayers);
        Assert.Equal(1024, r.Winner.UBatch);
        Assert.Equal(2048, r.Winner.Batch);
        Assert.Equal("auto", r.Winner.FlashAttention);
        Assert.Equal("q8_0", r.Winner.CacheType); // выигрыш f16 меньше порога 3 %
        Assert.Equal(10, r.BaselineSeconds!.Value, 6);
        Assert.Equal(7.5, r.WinnerSeconds!.Value, 6);
        Assert.InRange(r.Gain, 0.24, 0.26);
        // Неудачная проба (нехватка памяти) пропущена, но учтена.
        Assert.Contains(r.Trials, t => t.Candidate.CpuMoeLayers == 8 && t.Error is not null);
        // Победитель замерен дважды, исходный — один раз, повторов прочих наборов нет.
        Assert.True(r.Trials[^1].Confirmation);
        Assert.Equal(r.Trials.Count, bench.Calls.Select(c => c.Key).Distinct().Count() + 1);
        Assert.Equal(Base, bench.Calls[0]);
    }

    [Fact]
    public async Task Search_NoiseBelowThreshold_KeepsBaseline()
    {
        var bench = new FakeBench(c => c.UBatch == 2048 ? 9.8 : 10.0); // 2 % — шум
        var r = await LlamaAutoTune.SearchAsync(Base, LlamaAutoTune.CreateDimensions(Base, Limits), bench.MeasureAsync, Options(),
            ct: TestContext.Current.CancellationToken);
        Assert.Null(r.Winner);
        Assert.Equal(0, r.Gain);
        Assert.DoesNotContain(r.Trials, t => t.Confirmation);
    }

    [Fact]
    public async Task Search_WinnerNotConfirmed_KeepsBaseline()
    {
        var calls = 0;
        // Первый замер ub=1024 случайно быстрый, повторный — как у исходного: среднее ниже порога.
        var bench = new FakeBench(c => c.UBatch == 1024 ? (++calls == 1 ? 9.4 : 10.4) : 10.0);
        var r = await LlamaAutoTune.SearchAsync(Base, LlamaAutoTune.CreateDimensions(Base, Limits), bench.MeasureAsync, Options(),
            ct: TestContext.Current.CancellationToken);
        Assert.Null(r.Winner);
        Assert.Contains(r.Trials, t => t.Confirmation && t.Candidate.UBatch == 1024);
    }

    [Fact]
    public async Task Search_RespectsTrialBudget_ReservesConfirmation()
    {
        var bench = new FakeBench(c => c.CpuMoeLayers == 9 ? 8.0 : 10.0);
        var r = await LlamaAutoTune.SearchAsync(Base, LlamaAutoTune.CreateDimensions(Base, Limits), bench.MeasureAsync, Options(maxTrials: 3),
            ct: TestContext.Current.CancellationToken);
        Assert.True(r.BudgetExhausted);
        Assert.Equal(3, r.Trials.Count); // исходный, одна проба, повтор победителя
        Assert.NotNull(r.Winner);
        Assert.Equal(9, r.Winner.CpuMoeLayers);
    }

    [Fact]
    public async Task Search_RespectsTimeBudget()
    {
        var elapsed = TimeSpan.Zero;
        var bench = new FakeBench(_ =>
        {
            elapsed += TimeSpan.FromMinutes(8);
            return 10.0;
        });
        var r = await LlamaAutoTune.SearchAsync(Base, LlamaAutoTune.CreateDimensions(Base, Limits), bench.MeasureAsync,
            new AutoTuneOptions { MaxTrials = 30, MaxDuration = TimeSpan.FromMinutes(20), Elapsed = () => elapsed },
            ct: TestContext.Current.CancellationToken);
        Assert.True(r.BudgetExhausted);
        Assert.Equal(3, r.Trials.Count); // 0 → 8 → 16 мин: третья проба начата до 20 минут, дальше — нет
    }

    [Fact]
    public async Task Search_BaselineFails_NoSearch()
    {
        var bench = new FakeBench(c => c == Base ? null : 5.0);
        var r = await LlamaAutoTune.SearchAsync(Base, LlamaAutoTune.CreateDimensions(Base, Limits), bench.MeasureAsync, Options(),
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(r.BaselineError);
        Assert.Null(r.Winner);
        Assert.Single(bench.Calls);
    }

    [Fact]
    public void Dimensions_FollowModelAndHardware()
    {
        // Плотная модель на одной карте без MTP: --n-cpu-moe, --split-mode и MTP не перебираются.
        var dense = Base with { CpuMoeLayers = -1 };
        var names = LlamaAutoTune.CreateDimensions(dense, Limits).Select(d => d.Name).ToList();
        Assert.Equal(["batch", "flash-attn", "cache"], names);

        var multi = Base with { SplitMode = "layer", Mtp = false };
        var all = LlamaAutoTune.CreateDimensions(multi, Limits with { MultiGpu = true });
        Assert.Equal(["n-cpu-moe", "batch", "flash-attn", "cache", "split-mode", "mtp"], all.Select(d => d.Name).ToList());
        Assert.Equal("row", Assert.Single(all.Single(d => d.Name == "split-mode").Variants(multi)).SplitMode);
        Assert.True(Assert.Single(all.Single(d => d.Name == "mtp").Variants(multi)).Mtp);

        // --n-cpu-moe: соседние значения, не ниже 0; -ub не больше -b.
        var moe = all.Single(d => d.Name == "n-cpu-moe");
        Assert.Equal([9, 11, 8], moe.Variants(multi).Select(c => c.CpuMoeLayers).ToArray());
        Assert.Equal([0, 2], moe.Variants(multi with { CpuMoeLayers = 1 }).Select(c => c.CpuMoeLayers).ToArray());
        Assert.All(all.Single(d => d.Name == "batch").Variants(multi), c => Assert.True(c.UBatch <= c.Batch));

        // Квантованный кэш требует flash attention: при выключенном тип кэша не перебирается.
        var cache = all.Single(d => d.Name == "cache");
        Assert.Equal("f16", Assert.Single(cache.Variants(multi)).CacheType);
        Assert.Empty(cache.Variants(multi with { FlashAttention = "off" }));
        // f16 не помещается (нет в списке) — перебирать нечего.
        Assert.Empty(LlamaAutoTune.CreateDimensions(Base, Limits with { CacheTypes = ["q8_0"] }).Single(d => d.Name == "cache").Variants(Base));
    }

    [Fact]
    public void Baseline_FromSettingsPlacementAndPreviousProfile()
    {
        var model = new InstalledModel { Id = "m", FilePath = @"C:\m.gguf", IsMoe = true, HasMtp = true };
        var s = new ServerSettings { FlashAttention = "on", CacheType = "q8_0", EnableMtp = true };
        var placement = new ServerPlacement(32768, 12, "") { Split = TwoCards(), Tuned = new TunedProfile { UBatch = 1024, Batch = 2048, CpuMoeDelta = -2, SplitMode = "row" } };
        var b = LlamaAutoTune.Baseline(s, model, placement);
        Assert.Equal(new TuneCandidate("on", "q8_0", 1024, 2048, 10, "row", true), b);

        // Контекст задан вручную — --n-cpu-moe не подбирается; несколько слотов — MTP недоступен.
        var manual = LlamaAutoTune.Baseline(new ServerSettings { ContextSize = 16384, Parallel = 2 }, model, placement);
        Assert.Equal(-1, manual.CpuMoeLayers);
        Assert.Null(manual.Mtp);
        Assert.Equal(new TuneCandidate("auto", "f16", 0, 0, -1, null, null),
            LlamaAutoTune.Baseline(new ServerSettings { CacheType = "bad" }, new InstalledModel { Id = "d" }, null));
    }

    [Fact]
    public void ToProfile_StoresCpuMoeAsDelta()
    {
        var p = (Base with { CpuMoeLayers = 8, UBatch = 1024, Batch = 2048 }).ToProfile(placementCpuMoe: 10);
        Assert.Equal(-2, p.CpuMoeDelta);
        Assert.Equal(1024, p.UBatch);
        Assert.Equal("auto", p.FlashAttention);
        Assert.Equal(0, (Base with { CpuMoeLayers = -1 }).ToProfile(10).CpuMoeDelta);
        Assert.Equal("-fa auto -ctk q8_0 -ub 1024 -b 2048 --n-cpu-moe -2", LlamaAutoTune.Describe(p));
    }

    private sealed class FakeHost(FakeBench bench, params bool[] restoreResults) : IAutoTuneHost
    {
        private int _restores;
        public List<AutoTuneResult?> Restores { get; } = [];

        public Task<TuneMeasurement> MeasureAsync(TuneCandidate candidate, CancellationToken ct) => bench.MeasureAsync(candidate, ct);

        public Task<bool> RestoreAsync(AutoTuneResult? apply, CancellationToken ct)
        {
            Restores.Add(apply);
            var ok = _restores < restoreResults.Length ? restoreResults[_restores] : true;
            _restores++;
            return Task.FromResult(ok);
        }
    }

    [Fact]
    public async Task Session_Cancel_RestoresBaseline()
    {
        using var cts = new CancellationTokenSource();
        var bench = new FakeBench(c =>
        {
            if (c != Base) cts.Cancel(); // отмена посреди подбора
            return 10.0;
        });
        var host = new FakeHost(bench);
        var outcome = await LlamaAutoTune.RunAsync(host, Base, LlamaAutoTune.CreateDimensions(Base, Limits), Options(), ct: cts.Token);
        Assert.True(outcome.Cancelled);
        Assert.False(outcome.Applied);
        Assert.True(outcome.Restored);
        Assert.Null(Assert.Single(host.Restores)); // возврат к прежним параметрам
    }

    [Fact]
    public async Task Session_WinnerFailsToStart_FallsBackToBaseline()
    {
        var bench = new FakeBench(c => c.UBatch == 1024 ? 8.0 : 10.0);
        var host = new FakeHost(bench, false, true);
        var outcome = await LlamaAutoTune.RunAsync(host, Base, LlamaAutoTune.CreateDimensions(Base, Limits), Options(),
            ct: TestContext.Current.CancellationToken);
        Assert.False(outcome.Applied);
        Assert.True(outcome.Restored);
        Assert.Equal(2, host.Restores.Count);
        Assert.NotNull(host.Restores[0]!.Winner);
        Assert.Null(host.Restores[1]);
    }

    [Fact]
    public async Task Session_Success_AppliesWinner()
    {
        var bench = new FakeBench(c => c.UBatch == 1024 ? 8.0 : 10.0);
        var host = new FakeHost(bench);
        var outcome = await LlamaAutoTune.RunAsync(host, Base, LlamaAutoTune.CreateDimensions(Base, Limits), Options(),
            ct: TestContext.Current.CancellationToken);
        Assert.True(outcome.Applied);
        Assert.Null(outcome.Error);
        Assert.Equal(1024, Assert.Single(host.Restores)!.Winner!.UBatch);
    }

    [Fact]
    public async Task Session_MeasureCrash_IsSkipped_NotFatal()
    {
        var bench = new FakeBench(c => c.FlashAttention == "off" ? throw new InvalidOperationException("boom") : 10.0);
        var host = new FakeHost(bench);
        var outcome = await LlamaAutoTune.RunAsync(host, Base, LlamaAutoTune.CreateDimensions(Base, Limits), Options(),
            ct: TestContext.Current.CancellationToken);
        Assert.Null(outcome.Error);
        Assert.False(outcome.Applied);
        Assert.Contains(outcome.Result!.Trials, t => t.Error == "boom");
    }

    [Fact]
    public void SettingsSignature_ChangesWithRelevantSettings()
    {
        var s = new ServerSettings();
        var sig = LlamaAutoTune.SettingsSignature(s);
        Assert.Equal(sig, LlamaAutoTune.SettingsSignature(new ServerSettings { Port = 9999, AutoStart = false, IdleUnloadMinutes = 5 }));
        Assert.NotEqual(sig, LlamaAutoTune.SettingsSignature(new ServerSettings { CacheType = "f16" }));
        Assert.NotEqual(sig, LlamaAutoTune.SettingsSignature(new ServerSettings { ExtraArgs = "-ub 512" }));
        Assert.NotEqual(sig, LlamaAutoTune.SettingsSignature(new ServerSettings { Parallel = 2 }));
        Assert.NotEqual(sig, LlamaAutoTune.SettingsSignature(new ServerSettings { GpuSelection = ServerSettings.GpuSelectionPrimary }));
    }

    internal static GpuSplit TwoCards() => new(
    [
        new GpuSplitDevice("CUDA0", "NVIDIA GeForce RTX 3090", 24L << 30, 18L << 30),
        new GpuSplitDevice("CUDA1", "NVIDIA GeForce RTX 4090", 24L << 30, 22L << 30),
    ], 1);
}

/// <summary>Профили автоподбора в config.json.</summary>
[Collection("AppPaths")]
public sealed class AutoTuneProfileTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Profile_RoundTrip_AndStaleWhenSettingsChange()
    {
        var baseline = new TuneCandidate("auto", "q8_0", 0, 0, 10, null, null);
        var winner = baseline with { UBatch = 1024, Batch = 2048, CpuMoeLayers = 9 };
        var result = new AutoTuneResult(baseline, 10, winner, 8, new TuneMeasurement(900, 45), [], false, null);
        var profile = LlamaAutoTune.ProfileFrom(result, 10, ConfigStore.Current.Server, "b9000", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        LlamaAutoTune.SaveProfile("qwen", "abc123", profile);

        var cfg = ConfigStore.Reload();
        var loaded = LlamaAutoTune.FindProfile(cfg, "qwen", "abc123");
        Assert.NotNull(loaded);
        Assert.Equal(1024, loaded.UBatch);
        Assert.Equal(2048, loaded.Batch);
        Assert.Equal(-1, loaded.CpuMoeDelta);
        Assert.Equal(10, loaded.BaselineSeconds);
        Assert.Equal(8, loaded.TunedSeconds);
        Assert.Equal(900, loaded.PromptTokensPerSecond);
        Assert.Equal("b9000", loaded.LlamaTag);
        Assert.Contains("qwen|abc123", cfg.Server.TunedProfiles.Keys);
        // Другое оборудование или модель — профиля нет.
        Assert.Null(LlamaAutoTune.FindProfile(cfg, "qwen", "other"));
        Assert.Null(LlamaAutoTune.FindProfile(cfg, "other", "abc123"));

        // Пользователь поменял настройки — профиль устарел и не применяется, но виден.
        ConfigStore.Update(c => c.Server.CacheType = "f16");
        cfg = ConfigStore.Reload();
        Assert.Null(LlamaAutoTune.FindProfile(cfg, "qwen", "abc123"));
        Assert.NotNull(LlamaAutoTune.GetProfile(cfg, "qwen", "abc123"));

        Assert.True(LlamaAutoTune.RemoveProfile("qwen", "abc123"));
        Assert.False(LlamaAutoTune.RemoveProfile("qwen", "abc123"));
        Assert.Null(LlamaAutoTune.GetProfile(ConfigStore.Reload(), "qwen", "abc123"));
    }
}
