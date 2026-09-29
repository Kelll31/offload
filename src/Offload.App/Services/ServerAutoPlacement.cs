using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Llama;
using Offload.Models;

namespace Offload.App.Services;

/// <summary>Оценка размещения активной модели: что покажет страница «Сервер» и что получит llama-server при запуске.</summary>
internal sealed record PerformanceSnapshot(
    FitModel Model,
    HardwareInfo Hardware,
    VramUsage? Usage,
    /// <summary>Видеопамять, занятая другими программами и учтённая в оценке (0 — не учитывалась).</summary>
    long OtherVramBytes,
    FitResult Fit,
    SlotAdvice Slots,
    /// <summary>Размещение «Авто», которое будет передано при запуске, или null (решает --fit / значения пользователя).</summary>
    ServerPlacement? Placement)
{
    /// <summary>Разделение между видеокартами (или выбор одной из нескольких) либо null — одна видеокарта.</summary>
    public GpuSplit? Split { get; init; }

    /// <summary>Сохранённый профиль автоподбора для модели и оборудования (в том числе устаревший) или null.</summary>
    public TunedProfile? Tuned { get; init; }

    /// <summary>Профиль подобран при текущих настройках и применяется при запуске.</summary>
    public bool TunedCurrent { get; init; }
}

/// <summary>Всё, что нужно автоподбору: исходное размещение (без профиля), исходный набор, пространство поиска.</summary>
/// <param name="BasePlacement">Размещение для проб (с разделением по видеокартам, без подобранного профиля); null — без размещения.</param>
/// <param name="Previous">Действующий профиль до подбора (его восстанавливают при отмене) или null.</param>
internal sealed record TunePlan(
    ServerPlacement? BasePlacement,
    TuneCandidate Baseline,
    IReadOnlyList<TuneDimension> Dimensions,
    string HardwareKey,
    TunedProfile? Previous);

/// <summary>
/// Размещение «Авто» (FitCalculator → явные -c и --n-cpu-moe), разделение по видеокартам и подобранные параметры
/// для запуска llama-server и для страницы «Сервер». Одна и та же оценка (<see cref="ServerFit"/>) используется
/// в интерфейсе и при запуске, поэтому показанный контекст совпадает с фактическим.
/// </summary>
internal static class ServerAutoPlacement
{
    // Собственный кэш: провайдер вызывается из ServerController, у которого нет доступа к оболочке окна.
    private static readonly HardwareCache Hardware = new();
    private static readonly Dictionary<string, GgufInfo?> Headers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Провайдер для <c>LlamaServerProcess.PlacementProvider</c>: размещение «Авто» (только в этом режиме), разделение по
    /// видеокартам и действующий профиль автоподбора (в любом режиме); null — ничего из этого.
    /// Занятость видеопамяти другими программами учитывается (наш сервер в этот момент не запущен).
    /// </summary>
    public static async Task<ServerPlacement?> ResolveAsync(AppConfig cfg, InstalledModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(model);
        var hw = await Hardware.GetAsync().WaitAsync(ct).ConfigureAwait(false);
        // Перед запуском — свежий опрос свободной памяти карт.
        var split = await SplitAsync(cfg, hw, ownServerRunning: false, refresh: true, ct).ConfigureAwait(false);
        ServerPlacement? placement = null;
        if (ServerFit.IsAutoPlacement(cfg.Server))
        {
            var fm = await Task.Run(() => ModelFor(model), ct).ConfigureAwait(false);
            if (fm is null)
            {
                Log.Info("llama", $"Размещение «Авто»: нет данных о модели {model.Id} — решает --fit");
            }
            else
            {
                // При нескольких картах занятость другими программами уже учтена в бюджете каждой карты.
                var usage = split is { IsMulti: true } ? null : await VramUsageProbe.QueryAsync(hw, null, ct).ConfigureAwait(false);
                var fit = ServerFit.Evaluate(fm, hw, cfg.Server, usage?.OtherBytes ?? 0, split: FitSplit(split));
                placement = ServerFit.AutoPlacement(fit, cfg.Server);
                if (placement is null) Log.Info("llama", $"Размещение «Авто»: оценка {fit.Level} — решает --fit");
            }
        }
        var tuned = LlamaAutoTune.FindProfile(cfg, model.Id, HardwareFingerprint.Of(hw));
        if (split is null && tuned is null) return placement;
        return (placement ?? ServerPlacement.Manual) with { Split = split, Tuned = tuned };
    }

    /// <summary>Оценка для страницы «Сервер»; null — нет активной модели или данных о ней.</summary>
    /// <param name="serverPid">PID запущенного llama-server: его видеопамять не считается занятой другими.</param>
    /// <param name="runningSplit">Разделение, с которым запущен сервер (показывается как есть), или null — рассчитать.</param>
    public static async Task<PerformanceSnapshot?> SnapshotAsync(HardwareInfo hw, AppConfig cfg, int? serverPid, GpuSplit? runningSplit = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hw);
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.ActiveModel() is not { } model) return null;
        var fm = await Task.Run(() => ModelFor(model), ct).ConfigureAwait(false);
        if (fm is null) return null;
        var usage = await VramUsageProbe.QueryAsync(hw, serverPid, ct).ConfigureAwait(false);
        // Долю нашего сервера не удалось отделить — занятость не учитываем, иначе модель «вытеснит» сама себя.
        var other = usage is { OwnKnown: true } u ? u.OtherBytes : 0;
        var split = runningSplit ?? await SplitAsync(cfg, hw, ownServerRunning: serverPid is not null, refresh: false, ct).ConfigureAwait(false);
        var profile = LlamaAutoTune.GetProfile(cfg, model.Id, HardwareFingerprint.Of(hw));
        return await Task.Run(() =>
        {
            var fitSplit = FitSplit(split);
            var fit = ServerFit.Evaluate(fm, hw, cfg.Server, other, split: fitSplit);
            var slots = ServerFit.RecommendSlots(fm, hw, cfg.Server, other, split: fitSplit);
            return new PerformanceSnapshot(fm, hw, usage, other, fit, slots, ServerFit.AutoPlacement(fit, cfg.Server))
            {
                Split = split,
                Tuned = profile,
                TunedCurrent = profile is not null && LlamaAutoTune.IsCurrent(profile, cfg.Server),
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Разделение по видеокартам: только если дискретных карт несколько (с одной — ничего не меняется и сборка не
    /// опрашивается). Устройства — от самой llama.cpp (--list-devices), порядок WMI не используется.
    /// </summary>
    /// <param name="ownServerRunning">Наш сервер работает: свободная память карт занижена им самим — не вычитать занятость.</param>
    /// <param name="refresh">Опросить сборку заново (свежая свободная память), иначе — кэш.</param>
    public static async Task<GpuSplit?> SplitAsync(AppConfig cfg, HardwareInfo hw, bool ownServerRunning, bool refresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(hw);
        if (hw.Gpus.Count(g => !g.IsIntegrated) < 2) return null;
        string? exe;
        try
        {
            exe = LlamaInstaller.GetServerExePath(cfg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug("llama", $"Видеокарты: сборка llama.cpp недоступна: {ex.Message}");
            return null;
        }
        if (exe is null) return null;
        try
        {
            var devices = await LlamaDevices.QueryAsync(exe, refresh, ct).ConfigureAwait(false);
            return MultiGpuPlanner.Plan(devices, hw, cfg.Server.GpuSelection, ignoreOtherUsage: ownServerRunning);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("llama", $"Видеокарты: список устройств llama.cpp не получен, решает llama.cpp: {ex.Message}");
            return null;
        }
    }

    /// <summary>Разделение для оценки памяти: только при нескольких картах (одна выбранная — как прежде, по основной карте).</summary>
    private static GpuSplit? FitSplit(GpuSplit? split) => split is { IsMulti: true } ? split : null;

    /// <summary>
    /// Разделение основной модели для оценки памяти ролей (<see cref="RoleBudget"/>): кэш списка устройств, без вычета занятости
    /// другими программами (бюджет ролей её тоже не учитывает); null — одна карта, клиентский режим или разделения нет.
    /// Ошибки не выбрасываются: без разделения оценка идёт по основной карте, как раньше.
    /// </summary>
    public static async Task<GpuSplit?> RoleBudgetSplitAsync(AppConfig cfg, HardwareInfo hw, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(hw);
        if (cfg.IsRemote()) return null;
        try
        {
            return FitSplit(await SplitAsync(cfg, hw, ownServerRunning: true, refresh: false, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug("llama", $"Видеокарты для оценки ролей: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Подготовка автоподбора (сервер уже остановлен): размещение как при обычном запуске, но без профиля; исходный набор —
    /// действующие параметры (с прежним профилем); f16-кэш перебирается, только если помещается с тем же контекстом.
    /// </summary>
    public static async Task<TunePlan> PrepareTuneAsync(AppConfig cfg, InstalledModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(model);
        var hw = await Hardware.GetAsync().WaitAsync(ct).ConfigureAwait(false);
        var hwKey = HardwareFingerprint.Of(hw);
        var placement = await ResolveAsync(cfg, model, ct).ConfigureAwait(false);
        var previous = placement?.Tuned;
        var basePlacement = placement is null ? null : placement with { Tuned = null };
        if (basePlacement is { ContextPerSlot: <= 0, Split: null }) basePlacement = null;
        var baseline = LlamaAutoTune.Baseline(cfg.Server, model, placement);

        var fm = await Task.Run(() => ModelFor(model), ct).ConfigureAwait(false);
        var cacheTypes = new List<string> { baseline.CacheType };
        var maxCpuMoe = Math.Max(0, baseline.CpuMoeLayers) + 2;
        if (fm is not null)
        {
            maxCpuMoe = fm.Model.Moe is { MoeLayers: > 0 } moe ? moe.MoeLayers : Math.Max(maxCpuMoe, fm.Model.BlockCount);
            var split = FitSplit(placement?.Split);
            var s = cfg.Server;
            var usage = split is null ? await VramUsageProbe.QueryAsync(hw, null, ct).ConfigureAwait(false) : null;
            var other = usage?.OtherBytes ?? 0;
            var current = ServerFit.Evaluate(fm, hw, s, other, split: split);
            foreach (var type in new[] { "f16", "q8_0" })
            {
                var alt = ServerFit.Evaluate(fm, hw, new ServerSettings { ContextSize = s.ContextSize, Parallel = s.Parallel, CpuMoeLayers = s.CpuMoeLayers, CacheType = type },
                    other, split: split);
                // Скорость не покупается ценой контекста или выгрузки на ЦП.
                if (alt.Usable && alt.ContextSize >= current.ContextSize && alt.Level <= current.Level
                    && (alt.Level != FitLevel.MoeOffload || alt.CpuMoeLayers <= current.CpuMoeLayers))
                    cacheTypes.Add(type);
            }
        }
        var limits = new TuneLimits(maxCpuMoe, cacheTypes.Distinct().ToList(), placement?.Split is { IsMulti: true });
        return new TunePlan(basePlacement, baseline, LlamaAutoTune.CreateDimensions(baseline, limits), hwKey, previous);
    }

    /// <summary>Отпечаток оборудования (для профиля автоподбора).</summary>
    public static async Task<string> HardwareKeyAsync(CancellationToken ct = default) =>
        HardwareFingerprint.Of(await Hardware.GetAsync().WaitAsync(ct).ConfigureAwait(false));

    /// <summary>Модель для оценки: каталожная или по заголовку GGUF (заголовки кэшируются по пути файла).</summary>
    public static FitModel? ModelFor(InstalledModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<CatalogModel> catalog;
        try
        {
            catalog = ModelCatalog.Available;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log.Warn("models", $"Каталог моделей недоступен: {ex.Message}");
            catalog = [];
        }
        return ServerFit.ModelFor(model, catalog, ReadHeader);
    }

    private static GgufInfo? ReadHeader(string path)
    {
        lock (Headers)
        {
            if (Headers.TryGetValue(path, out var cached)) return cached;
        }
        GgufInfo? info = null;
        try
        {
            if (File.Exists(path)) info = GgufReader.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Debug("models", $"Заголовок GGUF не прочитан ({path}): {ex.Message}");
        }
        lock (Headers) Headers[path] = info;
        return info;
    }
}
