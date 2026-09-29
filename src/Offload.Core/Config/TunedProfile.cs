namespace Offload.Core.Config;

/// <summary>
/// Параметры llama-server, подобранные автоподбором («Автоподбор параметров» на странице «Сервер») для пары
/// «модель × оборудование». Применяются при обычных запусках, пока настройки сервера совпадают с теми, при которых
/// шёл подбор (<see cref="SettingsSignature"/>); флаги из доп. аргументов пользователя важнее подобранных.
/// </summary>
public sealed class TunedProfile
{
    /// <summary>Flash attention: auto / on / off; null — как в настройках.</summary>
    public string? FlashAttention { get; set; }

    /// <summary>Тип KV-кэша (f16, q8_0…); null — как в настройках.</summary>
    public string? CacheType { get; set; }

    /// <summary>-ub (физический размер пакета); 0 — по умолчанию llama.cpp.</summary>
    public int UBatch { get; set; }

    /// <summary>-b (логический размер пакета); 0 — по умолчанию llama.cpp.</summary>
    public int Batch { get; set; }

    /// <summary>
    /// Поправка к --n-cpu-moe из размещения «Авто» (−1 — на один слой экспертов больше в видеопамяти). Хранится поправка,
    /// а не число: если другие программы займут видеопамять, размещение сдвинется, и поправка останется осмысленной.
    /// </summary>
    public int CpuMoeDelta { get; set; }

    /// <summary>--split-mode при нескольких видеокартах (layer / row); null — по умолчанию.</summary>
    public string? SplitMode { get; set; }

    /// <summary>MTP-спекуляция вкл/выкл; null — как в настройках.</summary>
    public bool? Mtp { get; set; }

    /// <summary>Отпечаток настроек сервера при подборе: изменились настройки — профиль не применяется.</summary>
    public string SettingsSignature { get; set; } = "";

    public DateTime TunedAtUtc { get; set; }

    /// <summary>Оценка типичного запроса до подбора, секунд.</summary>
    public double BaselineSeconds { get; set; }

    /// <summary>Оценка типичного запроса с подобранными параметрами, секунд.</summary>
    public double TunedSeconds { get; set; }

    /// <summary>Скорость обработки промпта с подобранными параметрами, токенов/с.</summary>
    public double PromptTokensPerSecond { get; set; }

    /// <summary>Скорость генерации с подобранными параметрами, токенов/с.</summary>
    public double GenerationTokensPerSecond { get; set; }

    /// <summary>Сколько пробных запусков было сделано.</summary>
    public int Trials { get; set; }

    /// <summary>Тег сборки llama.cpp при подборе.</summary>
    public string? LlamaTag { get; set; }
}
