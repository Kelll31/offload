namespace Offload.Core.Hardware;

/// <summary>
/// Размещение модели, рассчитанное под оборудование (режим «Авто»): его передают llama-server явными -c и --n-cpu-moe
/// вместо того, чтобы полагаться только на --fit. Считает Offload.Models (FitCalculator), применяет Offload.Llama.
/// </summary>
/// <param name="ContextPerSlot">Контекст одного слота, токенов (общий пул -c = ContextPerSlot × слоты).</param>
/// <param name="CpuMoeLayers">Сколько слоёв экспертов MoE держать на ЦП (--n-cpu-moe); 0 — все эксперты в видеопамяти.</param>
/// <param name="Reason">Пояснение для журнала (текст оценки FitCalculator).</param>
public sealed record ServerPlacement(int ContextPerSlot, int CpuMoeLayers, string Reason);
