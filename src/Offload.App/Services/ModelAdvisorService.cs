using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Models;

namespace Offload.App.Services;

/// <summary>Тексты карточки «Подобрано для вашего компьютера» и признаки расхождения с текущими настройками.</summary>
/// <param name="ModelDiffers">Подобранная модель заметно лучше текущей активной (или активной нет / она не помещается).</param>
/// <param name="ParamsDiffer">Число слотов или тип KV-кэша в настройках отличаются от подобранных.</param>
internal sealed record AdviceText(string Headline, string Params, string? Fast, string? Alternatives, bool ModelDiffers, bool ParamsDiffer, bool BestInstalled, bool BestIsActive);

/// <summary>Автоматический подбор модели и параметров под оборудование (<see cref="HardwareAdvisor"/>) в терминах приложения.</summary>
internal static class ModelAdvisorService
{
    /// <summary>Во сколько раз подобранная модель должна быть лучше активной, чтобы предлагать смену (иначе — не беспокоим).</summary>
    internal const double WorthSwitchingRatio = 1.25;

    public static HardwareAdvice Compute(HardwareInfo hw, AppConfig cfg) =>
        HardwareAdvisor.Advise(hw, ModelCatalog.ChatModels, cfg.Server);

    /// <summary>Расхождения совета с текущими настройками и готовые строки для интерфейса. null — советовать нечего (нет подходящих моделей).</summary>
    public static AdviceText? Describe(HardwareAdvice advice, HardwareInfo hw, AppConfig cfg)
    {
        if (advice.Best is not { } best || advice.Params is not { } param) return null;
        var catalog = ModelCatalog.ChatModels;
        var active = cfg.ActiveModel();
        var bestIsActive = active is not null && string.Equals(active.Id, best.Model.Id, StringComparison.OrdinalIgnoreCase);
        var installed = cfg.Models.Installed.Any(m => string.Equals(m.Id, best.Model.Id, StringComparison.OrdinalIgnoreCase));

        var modelDiffers = !bestIsActive && ModelDiffersFrom(active, best, hw, catalog);
        var paramsDiffer = cfg.Server.Parallel != param.Parallel || !string.Equals(cfg.Server.CacheType, param.CacheType, StringComparison.OrdinalIgnoreCase);

        var head = L.F("Лучшая модель для этого компьютера: «{0}», квант {1} — {2}.", best.Model.LocalizedDisplayName, best.Quant, best.Why);
        if (bestIsActive) head += " " + L.T("Она уже активна.");
        else if (installed) head += " " + L.T("Она уже скачана.");

        var paramText = L.F("Параметры сервера: {0}.", param.Summary);
        paramText += paramsDiffer
            ? " " + L.F("Сейчас: слотов {0}, KV-кэш {1}.", cfg.Server.Parallel, cfg.Server.CacheType)
            : " " + L.T("Совпадают с текущими настройками.");
        if (!ServerFit.IsAutoPlacement(cfg.Server))
            paramText += " " + L.T("Контекст и выгрузка экспертов заданы вручную — режим «Авто» их не подбирает.");

        var fast = advice.Fast is { } f
            ? L.F("Быстрая модель для коротких задач (роль «Быстрая»): «{0}», {1}.", f.Model.LocalizedDisplayName, f.Why)
            : null;
        var alt = advice.Alternatives.Count > 0
            ? L.F("Запасные варианты: {0}.", string.Join(", ", advice.Alternatives.Select(p => $"«{p.Model.LocalizedDisplayName}» ({p.Quant})")))
            : null;
        return new AdviceText(head, paramText, fast, alt, modelDiffers, paramsDiffer, installed, bestIsActive);
    }

    /// <summary>
    /// Стоит ли предлагать смену: активной нет; её нет в каталоге (своя модель — выбор пользователя, не трогаем); она не помещается;
    /// либо подобранная по оценке заметно лучше (<see cref="WorthSwitchingRatio"/>).
    /// </summary>
    internal static bool ModelDiffersFrom(InstalledModel? active, ModelPick best, HardwareInfo hw, IReadOnlyList<CatalogModel> catalog)
    {
        if (active is null) return true;
        var current = catalog.FirstOrDefault(m => string.Equals(m.Id, active.Id, StringComparison.OrdinalIgnoreCase));
        if (current is null) return false;
        var pick = HardwareAdvisor.PickFor(current, hw, catalog);
        return pick is null || best.Score > pick.Score * WorthSwitchingRatio;
    }

    /// <summary>Заголовок и текст уведомления о смене оборудования / первом подборе.</summary>
    public static (string Title, string Text) Notice(AdviceText text, HardwareAdvice advice, bool hardwareChanged)
    {
        var best = advice.Best!;
        var title = hardwareChanged ? L.T("Оборудование изменилось — модель подобрана заново") : L.T("Offload подобрал модель под этот компьютер");
        var body = L.F("Лучше всего подойдёт «{0}» ({1}): {2}. Откройте «Модели», чтобы скачать или выбрать её.", best.Model.LocalizedDisplayName, best.Quant, best.Why);
        return (title, body);
    }
}
