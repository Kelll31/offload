using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
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
    ServerPlacement? Placement);

/// <summary>
/// Размещение «Авто» (FitCalculator → явные -c и --n-cpu-moe) для запуска llama-server и для страницы «Сервер».
/// Одна и та же оценка (<see cref="ServerFit"/>) используется в интерфейсе и при запуске, поэтому показанный
/// контекст совпадает с фактическим.
/// </summary>
internal static class ServerAutoPlacement
{
    // Собственный кэш: провайдер вызывается из ServerController, у которого нет доступа к оболочке окна.
    private static readonly HardwareCache Hardware = new();
    private static readonly Dictionary<string, GgufInfo?> Headers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Провайдер для <c>LlamaServerProcess.PlacementProvider</c>: размещение для запуска в режиме «Авто» или null.
    /// Занятость видеопамяти другими программами учитывается (наш сервер в этот момент не запущен).
    /// </summary>
    public static async Task<ServerPlacement?> ResolveAsync(AppConfig cfg, InstalledModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(model);
        if (!ServerFit.IsAutoPlacement(cfg.Server)) return null;
        var fm = await Task.Run(() => ModelFor(model), ct).ConfigureAwait(false);
        if (fm is null)
        {
            Log.Info("llama", $"Размещение «Авто»: нет данных о модели {model.Id} — решает --fit");
            return null;
        }
        var hw = await Hardware.GetAsync().WaitAsync(ct).ConfigureAwait(false);
        var usage = await VramUsageProbe.QueryAsync(hw, null, ct).ConfigureAwait(false);
        var fit = ServerFit.Evaluate(fm, hw, cfg.Server, usage?.OtherBytes ?? 0);
        var placement = ServerFit.AutoPlacement(fit, cfg.Server);
        if (placement is null) Log.Info("llama", $"Размещение «Авто»: оценка {fit.Level} — решает --fit");
        return placement;
    }

    /// <summary>Оценка для страницы «Сервер»; null — нет активной модели или данных о ней.</summary>
    /// <param name="serverPid">PID запущенного llama-server: его видеопамять не считается занятой другими.</param>
    public static async Task<PerformanceSnapshot?> SnapshotAsync(HardwareInfo hw, AppConfig cfg, int? serverPid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hw);
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.ActiveModel() is not { } model) return null;
        var fm = await Task.Run(() => ModelFor(model), ct).ConfigureAwait(false);
        if (fm is null) return null;
        var usage = await VramUsageProbe.QueryAsync(hw, serverPid, ct).ConfigureAwait(false);
        // Долю нашего сервера не удалось отделить — занятость не учитываем, иначе модель «вытеснит» сама себя.
        var other = usage is { OwnKnown: true } u ? u.OtherBytes : 0;
        return await Task.Run(() =>
        {
            var fit = ServerFit.Evaluate(fm, hw, cfg.Server, other);
            var slots = ServerFit.RecommendSlots(fm, hw, cfg.Server, other);
            return new PerformanceSnapshot(fm, hw, usage, other, fit, slots, ServerFit.AutoPlacement(fit, cfg.Server));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Модель для оценки: каталожная или по заголовку GGUF (заголовки кэшируются по пути файла).</summary>
    public static FitModel? ModelFor(InstalledModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<CatalogModel> catalog;
        try
        {
            catalog = ModelCatalog.All;
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
