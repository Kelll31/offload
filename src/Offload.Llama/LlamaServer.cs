using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Llama;

public enum ServerState
{
    /// <summary>Не установлен llama.cpp или нет модели.</summary>
    NotConfigured,
    Stopped,
    /// <summary>Процесс запущен, модель загружается (/health отвечает 503).</summary>
    Starting,
    Running,
    Stopping,
    /// <summary>Процесс упал или не смог запуститься (см. LastError).</summary>
    Failed,
}

/// <summary>Параметры запуска, вычисленные для конкретной модели и оборудования.</summary>
/// <param name="ContextSize">Контекст одного слота (токенов). Общий пул KV (-c) = ContextSize × Parallel.</param>
/// <param name="CpuMoeLayers">Переданное --n-cpu-moe; -1 — размещение решает --fit, 0 — выключено.</param>
/// <param name="Placement">Применённое размещение «Авто» (оценка FitCalculator) или null — контекст из настроек/модели.</param>
/// <param name="Split">Применённое разделение между видеокартами (или выбор одной карты) или null.</param>
/// <param name="Tuned">Применённые подобранные параметры (автоподбор) или null.</param>
public sealed record ServerLaunchPlan(
    string ExePath,
    IReadOnlyList<string> Arguments,
    int ContextSize,
    int Parallel,
    int CpuMoeLayers,
    string ModelAlias,
    ServerPlacement? Placement = null,
    GpuSplit? Split = null,
    TunedProfile? Tuned = null)
{
    /// <summary>Адрес прослушивания (--host) запущенного процесса или null, если его нет в аргументах.</summary>
    public string? ListenHost
    {
        get
        {
            for (var i = 0; i + 1 < Arguments.Count; i++)
                if (Arguments[i] == "--host") return Arguments[i + 1];
            return null;
        }
    }
}
