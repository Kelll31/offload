using Offload.Core.Config;

namespace Offload.Core.Hardware;

/// <summary>
/// Размещение модели, рассчитанное под оборудование (режим «Авто»): его передают llama-server явными -c и --n-cpu-moe
/// вместо того, чтобы полагаться только на --fit. Считает Offload.Models (FitCalculator), применяет Offload.Llama.
/// </summary>
/// <param name="ContextPerSlot">
/// Контекст одного слота, токенов (общий пул -c = ContextPerSlot × слоты). 0 — размещения «Авто» нет (контекст и выгрузку
/// задал пользователь или оценки нет), а запись несёт только <see cref="Split"/> и/или <see cref="Tuned"/>.
/// </param>
/// <param name="CpuMoeLayers">Сколько слоёв экспертов MoE держать на ЦП (--n-cpu-moe); 0 — все эксперты в видеопамяти.</param>
/// <param name="Reason">Пояснение для журнала (текст оценки FitCalculator).</param>
public sealed record ServerPlacement(int ContextPerSlot, int CpuMoeLayers, string Reason)
{
    /// <summary>Без размещения «Авто»: только разделение по видеокартам и/или подобранные параметры.</summary>
    public static ServerPlacement Manual { get; } = new(0, -1, "");

    /// <summary>Разделение модели между видеокартами (--device, --tensor-split, --main-gpu) или null — как раньше.</summary>
    public GpuSplit? Split { get; init; }

    /// <summary>Подобранные автоподбором параметры (действующие для текущих настроек) или null.</summary>
    public TunedProfile? Tuned { get; init; }
}
