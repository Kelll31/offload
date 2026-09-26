using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models;

/// <summary>Сколько памяти займёт сервер одной роли (оценка FitCalculator).</summary>
public sealed record RoleFootprint(ModelRole Role, InstalledModel Model, FitResult Fit)
{
    /// <summary>Видеопамять сервера роли, байт.</summary>
    public long VramBytes => Fit.EstimatedVramBytes;

    /// <summary>Оперативная память сервера роли (выгруженные слои/эксперты), байт.</summary>
    public long RamBytes => Fit.EstimatedRamBytes;
}

/// <summary>
/// Суммарная оценка памяти для всех назначенных ролей. Fits = false — серверы ролей вместе не помещаются в видеопамять
/// (или в ОЗУ без видеокарты): вспомогательный сервер загрузится медленнее, частично на ЦП, или не загрузится вовсе.
/// </summary>
/// <param name="Unknown">Роли, для которых оценки нет (пользовательская модель без заголовка GGUF и т.п.).</param>
public sealed record RoleBudgetResult(
    IReadOnlyList<RoleFootprint> Items,
    IReadOnlyList<ModelRole> Unknown,
    long TotalVramBytes,
    long TotalRamBytes,
    long VramBudgetBytes,
    long RamBudgetBytes)
{
    /// <summary>Есть ли хоть одна вспомогательная роль в оценке (иначе предупреждать не о чем).</summary>
    public bool HasAuxiliary => Items.Any(i => i.Role != ModelRole.Quality);

    /// <summary>Всё помещается: в видеопамять (если она есть), и суммарно — в ОЗУ.</summary>
    public bool Fits => (VramBudgetBytes <= 0 || TotalVramBytes <= VramBudgetBytes) && TotalRamBytes <= RamBudgetBytes;
}

/// <summary>
/// Бюджет памяти ролей моделей (ROADMAP §5.2): основной сервер — оценка при настройках сервера (как на странице «Сервер»),
/// вспомогательные — один слот с контекстом роли (<see cref="ModelRoleConfig.AuxContext"/>). Только оценка: запуск не блокирует.
/// </summary>
public static class RoleBudget
{
    public static RoleBudgetResult Evaluate(AppConfig cfg, HardwareInfo hw, IReadOnlyList<CatalogModel> catalog,
        Func<string, GgufInfo?>? readHeader = null, long otherVramBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(hw);
        ArgumentNullException.ThrowIfNull(catalog);
        var items = new List<RoleFootprint>();
        var unknown = new List<ModelRole>();
        var cache = string.IsNullOrWhiteSpace(cfg.Server.CacheType) ? "f16" : cfg.Server.CacheType;

        foreach (var role in ModelRoleConfig.All)
        {
            if (cfg.RoleModel(role) is not { } model) continue;
            if (role == ModelRole.Fast && string.Equals(model.Id, cfg.ActiveModel()?.Id, StringComparison.OrdinalIgnoreCase)) continue;
            var fm = ServerFit.ModelFor(model, catalog, readHeader);
            if (fm is null)
            {
                unknown.Add(role);
                continue;
            }
            var fitModel = fm.Model;
            var roleCache = cache;
            if (role is ModelRole.Embed or ModelRole.Rerank)
            {
                // Пользовательская модель, назначенная эмбеддингам/реранку, оценивается как модель этой роли
                // (без KV-кэша у энкодеров); кэш серверов этих ролей — f16 (AuxServerArgs не задаёт -ctk).
                fitModel = fitModel with { Role = ModelRoleConfig.KindFor(role), Architecture = fitModel.Architecture ?? model.Architecture };
                roleCache = "f16";
            }
            var fit = role == ModelRole.Quality
                ? ServerFit.Evaluate(fm, hw, cfg.Server, otherVramBytes)
                : FitCalculator.Evaluate(fitModel, fm.WeightsBytes, hw, ModelRoleConfig.AuxContext(role, model), roleCache, 1,
                    otherVramBytes: otherVramBytes);
            items.Add(new RoleFootprint(role, model, fit));
        }

        var vram = hw.PrimaryVramBytes >= FitCalculator.MinUsableVramBytes ? hw.PrimaryVramBytes : 0;
        var vramBudget = vram > 0 ? FitCalculator.VramBudget(vram, otherVramBytes) : 0;
        var ramBudget = Math.Max(0, hw.TotalRamBytes - FitCalculator.RamReserveBytes(hw));
        return new RoleBudgetResult(items, unknown,
            items.Sum(i => i.VramBytes), items.Sum(i => i.RamBytes), vramBudget, ramBudget);
    }
}
