using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Models;

namespace Offload.App.Services;

/// <summary>Строка списка моделей: модель каталога и/или установленная модель.</summary>
internal sealed record ModelRow(
    CatalogModel? Catalog,
    InstalledModel? Installed,
    bool IsActive,
    bool IsRecommended,
    FitResult? Fit)
{
    public string Id => Installed?.Id ?? Catalog?.Id ?? "";

    public string Name => Catalog?.LocalizedDisplayName ?? (Installed is null ? Id : Texts.ModelName(Installed));

    public bool IsInstalled => Installed is not null;

    public long SizeBytes => Installed is { SizeBytes: > 0 } i ? i.SizeBytes : Catalog?.ApproxSizeBytes ?? 0;

    public int DefaultContext => Catalog?.DefaultContext ?? Installed?.RecommendedContext ?? 0;

    public int NativeContext => Catalog?.NativeContext ?? Installed?.NativeContext ?? 0;

    public bool GoodToolCalling => Catalog?.GoodToolCalling ?? Installed?.GoodToolCalling ?? false;

    public string Description =>
        Catalog?.LocalizedDescription
        ?? (Installed is { IsCustom: true } ? L.F("Пользовательская модель: {0}", Installed.FilePath) : Installed?.FilePath ?? "");

    public string StatusText => IsActive ? L.T("Активна") : IsInstalled ? L.T("Установлена") : "—";

    public string ContextText
    {
        get
        {
            if (DefaultContext <= 0 && NativeContext <= 0) return "—";
            if (NativeContext > 0 && DefaultContext > 0 && NativeContext != DefaultContext)
                return $"{Ui.Tokens(DefaultContext)} / {Ui.Tokens(NativeContext)}";
            return Ui.Tokens(Math.Max(DefaultContext, NativeContext));
        }
    }

    public string FitText => Fit is null ? "—" : $"{Texts.FitGlyph(Fit.Level)} {Fit.Explanation}";
}

internal static class ModelRows
{
    private static readonly Dictionary<string, KvSpec?> GgufKvCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Каталог (может быть недоступен — тогда пустой список и текст ошибки).</summary>
    public static (IReadOnlyList<CatalogModel> Models, string? Error) Catalog()
    {
        try
        {
            return (ModelCatalog.All, null);
        }
        catch (Exception ex)
        {
            Log.Warn("models", $"Каталог моделей недоступен: {ex.Message}");
            return ([], Ui.FriendlyError(ex));
        }
    }

    /// <summary>Каталожная модель, соответствующая установленной (по идентификатору или репозиторию).</summary>
    public static bool Matches(CatalogModel c, InstalledModel i) =>
        string.Equals(i.Id, c.Id, StringComparison.OrdinalIgnoreCase)
        || i.Id.StartsWith(c.Id + ":", StringComparison.OrdinalIgnoreCase);

    public static CatalogModel? Recommended(HardwareInfo? hw) =>
        hw is null ? null : Ui.Try<CatalogModel?>(() => FitCalculator.Recommend(hw), null, "FitCalculator.Recommend");

    public static (IReadOnlyList<ModelRow> Rows, string? Error) Build(AppConfig cfg, HardwareInfo? hw, bool includeCustom = true)
    {
        var (catalog, error) = Catalog();
        var recommended = Recommended(hw);
        var active = cfg.ActiveModel();
        var rows = new List<ModelRow>();
        var used = new HashSet<InstalledModel>();

        foreach (var c in catalog)
        {
            var inst = cfg.Models.Installed.FirstOrDefault(i => Matches(c, i) && !used.Contains(i));
            if (inst is not null) used.Add(inst);
            rows.Add(new ModelRow(c, inst,
                inst is not null && active is not null && inst.Id == active.Id,
                recommended is not null && recommended.Id == c.Id,
                Evaluate(c, inst?.SizeBytes > 0 ? inst.SizeBytes : c.ApproxSizeBytes, hw, cfg)));
        }

        if (includeCustom)
        {
            foreach (var i in cfg.Models.Installed.Where(i => !used.Contains(i)))
            {
                rows.Add(new ModelRow(null, i, active is not null && i.Id == active.Id, false, EvaluateCustom(i, hw, cfg)));
            }
        }
        return (rows, error);
    }

    private static FitResult? Evaluate(CatalogModel model, long weights, HardwareInfo? hw, AppConfig cfg)
    {
        if (hw is null) return null;
        return Ui.Try<FitResult?>(
            () => FitCalculator.Evaluate(model, weights, hw, 0, cfg.Server.CacheType, cfg.Server.Parallel),
            null, $"FitCalculator.Evaluate({model.Id})");
    }

    /// <summary>Оценка для пользовательской модели: параметры KV-кэша берутся из заголовка GGUF.</summary>
    private static FitResult? EvaluateCustom(InstalledModel m, HardwareInfo? hw, AppConfig cfg)
    {
        if (hw is null || !File.Exists(m.FilePath)) return null;
        KvSpec? kv;
        lock (GgufKvCache)
        {
            if (!GgufKvCache.TryGetValue(m.FilePath, out kv))
            {
                kv = Ui.Try<KvSpec?>(() =>
                {
                    // ToKvSpec учитывает гибридные модели (KV-кэш только у слоёв полного внимания).
                    return GgufReader.Read(m.FilePath).ToKvSpec();
                }, null, "GgufReader.Read");
                GgufKvCache[m.FilePath] = kv;
            }
        }
        if (kv is null) return null;
        var ctx = m.RecommendedContext > 0 ? m.RecommendedContext : Math.Min(32768, Math.Max(4096, m.NativeContext));
        var synthetic = new CatalogModel(
            m.Id, m.DisplayName, "", m.Repo ?? "", [m.Quant ?? ""], m.SizeBytes, 0, 0, m.IsMoe,
            m.NativeContext > 0 ? m.NativeContext : ctx, ctx, kv, m.GoodToolCalling, m.Sampling, "", 0);
        return Evaluate(synthetic, m.SizeBytes, hw, cfg);
    }
}
