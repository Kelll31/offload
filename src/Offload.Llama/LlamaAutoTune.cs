using System.Diagnostics;
using System.Globalization;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;

namespace Offload.Llama;

/// <summary>Набор параметров одного пробного запуска автоподбора.</summary>
/// <param name="FlashAttention">auto / on / off.</param>
/// <param name="CacheType">Тип KV-кэша (f16, q8_0…).</param>
/// <param name="UBatch">-ub; 0 — по умолчанию llama.cpp.</param>
/// <param name="Batch">-b; 0 — по умолчанию llama.cpp.</param>
/// <param name="CpuMoeLayers">--n-cpu-moe (абсолютное значение); -1 — не подбирается.</param>
/// <param name="SplitMode">--split-mode при нескольких видеокартах; null — одна карта.</param>
/// <param name="Mtp">MTP вкл/выкл; null — модель без MTP или несколько слотов.</param>
public sealed record TuneCandidate(string FlashAttention, string CacheType, int UBatch, int Batch, int CpuMoeLayers, string? SplitMode, bool? Mtp)
{
    /// <summary>Ключ для учёта уже опробованных наборов.</summary>
    public string Key => string.Join("|", FlashAttention, CacheType, UBatch.ToString(CultureInfo.InvariantCulture), Batch.ToString(CultureInfo.InvariantCulture),
        CpuMoeLayers.ToString(CultureInfo.InvariantCulture), SplitMode ?? "-", Mtp?.ToString() ?? "-");

    /// <summary>Флаги для журнала и интерфейса: «-fa on -ctk q8_0 -ub 1024 -b 2048 --n-cpu-moe 8».</summary>
    public string Describe()
    {
        var parts = new List<string> { "-fa " + FlashAttention, "-ctk " + CacheType };
        if (UBatch > 0) parts.Add("-ub " + UBatch.ToString(CultureInfo.InvariantCulture));
        if (Batch > 0) parts.Add("-b " + Batch.ToString(CultureInfo.InvariantCulture));
        if (CpuMoeLayers >= 0) parts.Add("--n-cpu-moe " + CpuMoeLayers.ToString(CultureInfo.InvariantCulture));
        if (SplitMode is not null) parts.Add("-sm " + SplitMode);
        if (Mtp is { } mtp) parts.Add(mtp ? "MTP on" : "MTP off");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Профиль для запуска: --n-cpu-moe сохраняется поправкой к размещению «Авто» (<paramref name="placementCpuMoe"/>).
    /// </summary>
    public TunedProfile ToProfile(int placementCpuMoe) => new()
    {
        FlashAttention = FlashAttention,
        CacheType = CacheType,
        UBatch = UBatch,
        Batch = Batch,
        CpuMoeDelta = CpuMoeLayers >= 0 && placementCpuMoe > 0 ? CpuMoeLayers - placementCpuMoe : 0,
        SplitMode = SplitMode,
        Mtp = Mtp,
    };
}

/// <summary>Замер одного пробного запуска: скорости обработки промпта и генерации, токенов/с.</summary>
public sealed record TuneMeasurement(double PromptTokensPerSecond, double GenerationTokensPerSecond);

/// <summary>Измерение пространства поиска: варианты, отличающиеся от текущего набора по одному параметру.</summary>
public sealed record TuneDimension(string Name, Func<TuneCandidate, IReadOnlyList<TuneCandidate>> Variants);

/// <summary>Какие параметры можно перебирать для данной модели и оборудования.</summary>
/// <param name="MaxCpuMoe">Наибольшее разумное --n-cpu-moe (число слоёв экспертов).</param>
/// <param name="CacheTypes">Допустимые типы KV-кэша (f16 — только если помещается при том же контексте).</param>
/// <param name="MultiGpu">Несколько видеокарт: перебирать --split-mode.</param>
public sealed record TuneLimits(int MaxCpuMoe, IReadOnlyList<string> CacheTypes, bool MultiGpu);

/// <summary>Одна проба: набор, результат (или ошибка) и оценка типичного запроса, секунд.</summary>
public sealed record TuneTrial(TuneCandidate Candidate, TuneMeasurement? Measurement, double? Seconds, string? Error, bool Confirmation);

/// <summary>Ход автоподбора: номер пробы, бюджет, опробуемый набор.</summary>
public sealed record AutoTuneProgress(int Trial, int MaxTrials, TuneCandidate Candidate, bool Confirming);

public sealed class AutoTuneOptions
{
    /// <summary>Наибольшее число пробных запусков (каждый — перезапуск сервера и загрузка модели), включая повтор победителя.</summary>
    public int MaxTrials { get; init; } = 12;

    /// <summary>Наибольшая длительность поиска (повтор победителя выполняется и после неё).</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Минимальный выигрыш, чтобы сменить набор (защита от шума замера).</summary>
    public double MinImprovement { get; init; } = 0.03;

    /// <summary>Типичный запрос к коду: токенов промпта…</summary>
    public int PromptTokens { get; init; } = 4000;

    /// <summary>…и сгенерированных токенов.</summary>
    public int GenerationTokens { get; init; } = 400;

    /// <summary>Прошедшее время (для тестов); null — секундомер.</summary>
    public Func<TimeSpan>? Elapsed { get; init; }
}

/// <summary>Итог поиска.</summary>
/// <param name="Winner">Лучший набор, подтверждённый повторным замером и быстрее исходного на порог; null — оставить исходный.</param>
/// <param name="WinnerMeasurement">Средние скорости победителя по двум замерам.</param>
/// <param name="BaselineError">Исходный набор не запустился или не измерился — поиск не выполнялся.</param>
public sealed record AutoTuneResult(
    TuneCandidate Baseline,
    double? BaselineSeconds,
    TuneCandidate? Winner,
    double? WinnerSeconds,
    TuneMeasurement? WinnerMeasurement,
    IReadOnlyList<TuneTrial> Trials,
    bool BudgetExhausted,
    string? BaselineError)
{
    /// <summary>На сколько быстрее исходного (0,18 — на 18 %); 0 — без победителя.</summary>
    public double Gain => Winner is not null && BaselineSeconds is > 0 && WinnerSeconds is { } w ? 1 - w / BaselineSeconds.Value : 0;
}

/// <summary>Итог сеанса автоподбора (поиск + восстановление сервера).</summary>
/// <param name="Applied">Победитель сохранён и сервер с ним запущен.</param>
/// <param name="Restored">Сервер в итоге запущен (с победителем или с прежними параметрами).</param>
public sealed record AutoTuneOutcome(AutoTuneResult? Result, bool Applied, bool Cancelled, Exception? Error, bool Restored);

/// <summary>Сервер для автоподбора: пробные запуски и возврат к рабочему состоянию.</summary>
public interface IAutoTuneHost
{
    /// <summary>Перезапустить сервер с набором и замерить скорость. Ошибка (нехватка памяти, таймаут) — исключение.</summary>
    Task<TuneMeasurement> MeasureAsync(TuneCandidate candidate, CancellationToken ct);

    /// <summary>
    /// Завершить подбор: <paramref name="apply"/> не null — сохранить его победителя и запустить сервер с ним; null — вернуть
    /// всё как было до подбора и запустить сервер. true — сервер работает.
    /// </summary>
    Task<bool> RestoreAsync(AutoTuneResult? apply, CancellationToken ct);
}

/// <summary>
/// Автоподбор параметров llama-server (ROADMAP 5.3): покоординатный спуск от текущих настроек — по очереди
/// --n-cpu-moe (MoE с выгрузкой), -ub/-b, flash attention, тип KV-кэша, --split-mode (несколько видеокарт), MTP.
/// Полный перебор не нужен и невозможен: каждая проба — перезапуск сервера с загрузкой модели. Контекст не меняется
/// (скорость не покупается ценой контекста). Оценка — время типичного запроса: промпт/pp + генерация/tg, меньше — лучше.
/// </summary>
public static class LlamaAutoTune
{
    private static readonly (int UBatch, int Batch)[] BatchOptions = [(0, 0), (1024, 2048), (2048, 2048), (256, 2048)];

    /// <summary>Оценка типичного запроса, секунд.</summary>
    public static double Score(TuneMeasurement m, AutoTuneOptions options) =>
        options.PromptTokens / m.PromptTokensPerSecond + options.GenerationTokens / m.GenerationTokensPerSecond;

    /// <summary>Исходный набор: действующие параметры запуска (настройки, размещение и уже подобранный профиль).</summary>
    public static TuneCandidate Baseline(ServerSettings s, InstalledModel model, ServerPlacement? placement)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(model);
        var t = placement?.Tuned;
        var parallel = Math.Clamp(s.Parallel, 1, ServerSettings.MaxParallel);
        var autoMoe = placement is { ContextPerSlot: > 0, CpuMoeLayers: > 0 } && s.ContextSize <= 0 && s.CpuMoeLayers < 0 && (model.IsMoe || model.IsCustom)
            ? Math.Max(0, placement.CpuMoeLayers + (t?.CpuMoeDelta ?? 0))
            : -1;
        return new TuneCandidate(
            LlamaServerArgs.NormalizeFlashAttention(t?.FlashAttention ?? s.FlashAttention),
            LlamaServerArgs.NormalizeCacheType(t?.CacheType ?? s.CacheType) ?? "f16",
            Math.Max(0, t?.UBatch ?? 0),
            Math.Max(0, t?.Batch ?? 0),
            autoMoe,
            placement?.Split is { IsMulti: true } split ? LlamaServerArgs.NormalizeSplitMode(t?.SplitMode ?? split.SplitMode) : null,
            model.HasMtp && parallel == 1 ? t?.Mtp ?? s.EnableMtp : null);
    }

    /// <summary>Измерения поиска в порядке ожидаемого влияния на скорость.</summary>
    public static IReadOnlyList<TuneDimension> CreateDimensions(TuneCandidate baseline, TuneLimits limits)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(limits);
        var dims = new List<TuneDimension>();
        if (baseline.CpuMoeLayers > 0)
        {
            // Оценка FitCalculator с запасом: на слой-два меньше может поместиться; если карта переполнена — на слой больше.
            dims.Add(new TuneDimension("n-cpu-moe", c =>
                new[] { c.CpuMoeLayers - 1, c.CpuMoeLayers + 1, c.CpuMoeLayers - 2 }
                    .Where(v => v >= 0 && v <= Math.Max(limits.MaxCpuMoe, c.CpuMoeLayers) && v != c.CpuMoeLayers)
                    .Select(v => c with { CpuMoeLayers = v }).ToList()));
        }
        dims.Add(new TuneDimension("batch", c =>
            BatchOptions.Where(b => (b.UBatch, b.Batch) != (c.UBatch, c.Batch))
                .Select(b => c with { UBatch = b.UBatch, Batch = b.Batch }).ToList()));
        dims.Add(new TuneDimension("flash-attn", c => [c with { FlashAttention = c.FlashAttention == "off" ? "on" : "off" }]));
        // Квантованный V-кэш требует flash attention: при выключенном тип кэша не перебирается.
        dims.Add(new TuneDimension("cache", c => c.FlashAttention == "off"
            ? []
            : limits.CacheTypes.Select(x => LlamaServerArgs.NormalizeCacheType(x)).OfType<string>().Distinct()
                .Where(x => x != c.CacheType).Select(x => c with { CacheType = x }).ToList()));
        if (limits.MultiGpu && baseline.SplitMode is not null)
            dims.Add(new TuneDimension("split-mode", c => [c with { SplitMode = c.SplitMode == GpuSplit.RowMode ? GpuSplit.LayerMode : GpuSplit.RowMode }]));
        if (baseline.Mtp is not null)
            dims.Add(new TuneDimension("mtp", c => c.Mtp is { } on ? [c with { Mtp = !on }] : []));
        return dims;
    }

    /// <summary>
    /// Поиск: замер исходного набора, затем по каждому измерению — все варианты от текущего лучшего; лучший вариант
    /// принимается, если он быстрее хотя бы на <see cref="AutoTuneOptions.MinImprovement"/>. Неудачные пробы пропускаются.
    /// Итоговый победитель замеряется ещё раз, и решение принимается по среднему двух замеров.
    /// </summary>
    /// <exception cref="OperationCanceledException">Отмена через <paramref name="ct"/>.</exception>
    public static async Task<AutoTuneResult> SearchAsync(TuneCandidate baseline, IReadOnlyList<TuneDimension> dimensions,
        Func<TuneCandidate, CancellationToken, Task<TuneMeasurement>> measure, AutoTuneOptions? options = null,
        IProgress<AutoTuneProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(measure);
        options ??= new AutoTuneOptions();
        var sw = Stopwatch.StartNew();
        var elapsed = options.Elapsed ?? (() => sw.Elapsed);
        var maxTrials = Math.Max(1, options.MaxTrials);
        var trials = new List<TuneTrial>();
        var tried = new Dictionary<string, (double? Seconds, TuneMeasurement? M)>(StringComparer.Ordinal);

        async Task<(double? Seconds, TuneMeasurement? M)> TryAsync(TuneCandidate c, bool confirming)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new AutoTuneProgress(trials.Count + 1, maxTrials, c, confirming));
            try
            {
                var m = await measure(c, ct).ConfigureAwait(false);
                if (!(m.PromptTokensPerSecond > 0) || !(m.GenerationTokensPerSecond > 0) || !double.IsFinite(m.PromptTokensPerSecond) || !double.IsFinite(m.GenerationTokensPerSecond))
                    throw new InvalidOperationException("no timings"); // l10n-ignore: внутренняя причина, в журнал
                var seconds = Score(m, options);
                trials.Add(new TuneTrial(c, m, seconds, null, confirming));
                Log.Info("llama", $"Автоподбор, проба {trials.Count}: {c.Describe()} — промпт {m.PromptTokensPerSecond:0.#} ток/с, генерация {m.GenerationTokensPerSecond:0.#} ток/с, запрос ≈{seconds:0.00} с");
                return (seconds, m);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                trials.Add(new TuneTrial(c, null, null, ex.Message, confirming));
                Log.Info("llama", $"Автоподбор, проба {trials.Count}: {c.Describe()} — не удалась: {ex.Message}");
                return (null, null);
            }
        }

        var (baseSeconds, baseM) = await TryAsync(baseline, false).ConfigureAwait(false);
        if (baseSeconds is not { } baseScore)
            return new AutoTuneResult(baseline, null, null, null, null, trials, false, trials[^1].Error);
        tried[baseline.Key] = (baseScore, baseM);

        var best = baseline;
        var bestScore = baseScore;
        var bestM = baseM;
        var exhausted = false;
        foreach (var dim in dimensions)
        {
            TuneCandidate? dimBest = null;
            var dimScore = double.MaxValue;
            TuneMeasurement? dimM = null;
            foreach (var v in dim.Variants(best))
            {
                if (tried.ContainsKey(v.Key)) continue;
                // Одна проба всегда остаётся на повтор победителя.
                if (trials.Count + 1 >= maxTrials || elapsed() >= options.MaxDuration)
                {
                    exhausted = true;
                    break;
                }
                var r = await TryAsync(v, false).ConfigureAwait(false);
                tried[v.Key] = r;
                if (r.Seconds is { } s && s < dimScore)
                {
                    dimBest = v;
                    dimScore = s;
                    dimM = r.M;
                }
            }
            if (dimBest is not null && dimScore <= bestScore * (1 - options.MinImprovement))
            {
                Log.Info("llama", $"Автоподбор ({dim.Name}): {best.Describe()} → {dimBest.Describe()}, ≈{bestScore:0.00} → {dimScore:0.00} с");
                best = dimBest;
                bestScore = dimScore;
                bestM = dimM;
            }
            if (exhausted) break;
        }

        if (best.Key == baseline.Key)
            return new AutoTuneResult(baseline, baseScore, null, null, null, trials, exhausted, null);

        // Повтор победителя: один удачный замер мог оказаться случайностью.
        var (again, againM) = await TryAsync(best, true).ConfigureAwait(false);
        if (again is not { } second || againM is null || bestM is null)
            return new AutoTuneResult(baseline, baseScore, null, null, null, trials, exhausted, null);
        var mean = (bestScore + second) / 2;
        if (mean > baseScore * (1 - options.MinImprovement))
        {
            Log.Info("llama", $"Автоподбор: повторный замер {best.Describe()} ≈{second:0.00} с — выигрыш в пределах шума, параметры не меняются");
            return new AutoTuneResult(baseline, baseScore, null, null, null, trials, exhausted, null);
        }
        var avg = new TuneMeasurement((bestM.PromptTokensPerSecond + againM.PromptTokensPerSecond) / 2,
            (bestM.GenerationTokensPerSecond + againM.GenerationTokensPerSecond) / 2);
        return new AutoTuneResult(baseline, baseScore, best, mean, avg, trials, exhausted, null);
    }

    /// <summary>
    /// Сеанс: поиск, затем всегда — рабочий сервер. Победитель не запустился — возврат к прежним параметрам;
    /// отмена или ошибка — тоже прежние параметры.
    /// </summary>
    public static async Task<AutoTuneOutcome> RunAsync(IAutoTuneHost host, TuneCandidate baseline, IReadOnlyList<TuneDimension> dimensions,
        AutoTuneOptions? options = null, IProgress<AutoTuneProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        AutoTuneResult? result = null;
        Exception? error = null;
        var cancelled = false;
        try
        {
            result = await SearchAsync(baseline, dimensions, host.MeasureAsync, options, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            Log.Info("llama", "Автоподбор отменён — возврат к прежним параметрам");
        }
        catch (Exception ex)
        {
            error = ex;
            Log.Error("llama", "Автоподбор прерван ошибкой — возврат к прежним параметрам", ex);
        }

        var apply = result is { Winner: not null } ? result : null;
        var restored = await SafeRestoreAsync(host, apply).ConfigureAwait(false);
        if (!restored && apply is not null)
        {
            Log.Warn("llama", "Сервер не запустился с подобранными параметрами — возврат к прежним");
            apply = null;
            restored = await SafeRestoreAsync(host, null).ConfigureAwait(false);
        }
        return new AutoTuneOutcome(result, apply is not null, cancelled, error, restored);
    }

    private static async Task<bool> SafeRestoreAsync(IAutoTuneHost host, AutoTuneResult? apply)
    {
        try
        {
            return await host.RestoreAsync(apply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("llama", $"Автоподбор: запуск сервера после подбора: {ex.Message}");
            return false;
        }
    }

    // ---------- Профили в конфиге ----------

    /// <summary>Ключ профиля: «модель|отпечаток оборудования».</summary>
    public static string ProfileKey(string modelId, string hardwareFingerprint) => $"{modelId}|{hardwareFingerprint}";

    /// <summary>
    /// Отпечаток настроек, влияющих на запуск: профиль, подобранный при других настройках, не применяется
    /// (пользователь поменял параметры — его выбор важнее).
    /// </summary>
    public static string SettingsSignature(ServerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return string.Join("|",
            s.ContextSize.ToString(CultureInfo.InvariantCulture),
            Math.Clamp(s.Parallel, 1, ServerSettings.MaxParallel).ToString(CultureInfo.InvariantCulture),
            s.GpuLayers.ToString(CultureInfo.InvariantCulture),
            s.CpuMoeLayers.ToString(CultureInfo.InvariantCulture),
            LlamaServerArgs.NormalizeFlashAttention(s.FlashAttention),
            LlamaServerArgs.NormalizeCacheType(s.CacheType) ?? "-",
            s.EnableMtp ? "mtp" : "-",
            (s.GpuSelection ?? "").Trim().ToLowerInvariant(),
            (s.ExtraArgs ?? "").Trim());
    }

    /// <summary>Сохранённый профиль (в том числе устаревший) или null.</summary>
    public static TunedProfile? GetProfile(AppConfig cfg, string? modelId, string hardwareFingerprint)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return modelId is not null && cfg.Server.TunedProfiles is { } all && all.TryGetValue(ProfileKey(modelId, hardwareFingerprint), out var p) ? p : null;
    }

    /// <summary>Профиль, действующий при текущих настройках, или null.</summary>
    public static TunedProfile? FindProfile(AppConfig cfg, string? modelId, string hardwareFingerprint) =>
        GetProfile(cfg, modelId, hardwareFingerprint) is { } p && IsCurrent(p, cfg.Server) ? p : null;

    /// <summary>Профиль подобран при тех же настройках сервера.</summary>
    public static bool IsCurrent(TunedProfile p, ServerSettings s) =>
        string.Equals(p.SettingsSignature, SettingsSignature(s), StringComparison.Ordinal);

    public static void SaveProfile(string modelId, string hardwareFingerprint, TunedProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(profile);
        ConfigStore.Update(c => (c.Server.TunedProfiles ??= [])[ProfileKey(modelId, hardwareFingerprint)] = profile);
    }

    /// <summary>Удалить профиль. true — он был.</summary>
    public static bool RemoveProfile(string modelId, string hardwareFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var removed = false;
        ConfigStore.Update(c => removed = c.Server.TunedProfiles?.Remove(ProfileKey(modelId, hardwareFingerprint)) == true);
        return removed;
    }

    /// <summary>Профиль из победителя поиска (с метаданными замера).</summary>
    public static TunedProfile ProfileFrom(AutoTuneResult result, int placementCpuMoe, ServerSettings s, string? llamaTag, DateTime tunedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        var winner = result.Winner ?? throw new InvalidOperationException("no winner"); // l10n-ignore: ошибка программиста
        var p = winner.ToProfile(placementCpuMoe);
        p.SettingsSignature = SettingsSignature(s);
        p.TunedAtUtc = tunedAtUtc;
        p.BaselineSeconds = Math.Round(result.BaselineSeconds ?? 0, 2);
        p.TunedSeconds = Math.Round(result.WinnerSeconds ?? 0, 2);
        p.PromptTokensPerSecond = Math.Round(result.WinnerMeasurement?.PromptTokensPerSecond ?? 0, 1);
        p.GenerationTokensPerSecond = Math.Round(result.WinnerMeasurement?.GenerationTokensPerSecond ?? 0, 1);
        p.Trials = result.Trials.Count;
        p.LlamaTag = llamaTag;
        return p;
    }

    /// <summary>Флаги профиля для журнала и интерфейса: «-fa on -ub 1024 -b 2048 --n-cpu-moe −1».</summary>
    public static string Describe(TunedProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.FlashAttention)) parts.Add("-fa " + p.FlashAttention);
        if (!string.IsNullOrWhiteSpace(p.CacheType)) parts.Add("-ctk " + p.CacheType);
        if (p.UBatch > 0) parts.Add("-ub " + p.UBatch.ToString(CultureInfo.InvariantCulture));
        if (p.Batch > 0) parts.Add("-b " + p.Batch.ToString(CultureInfo.InvariantCulture));
        if (p.CpuMoeDelta != 0) parts.Add("--n-cpu-moe " + p.CpuMoeDelta.ToString("+0;-0", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(p.SplitMode)) parts.Add("-sm " + p.SplitMode);
        if (p.Mtp is { } mtp) parts.Add(mtp ? "MTP on" : "MTP off");
        return string.Join(" ", parts);
    }
}
