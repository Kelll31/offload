using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Integrations;
using Offload.Llama;
using Offload.Models;

namespace Offload.App.Services;

/// <summary>Русские названия состояний и перечислений для интерфейса.</summary>
internal static class Texts
{
    public static string State(ServerState s) => s switch
    {
        ServerState.NotConfigured => "Не настроен",
        ServerState.Stopped => "Остановлен",
        ServerState.Starting => "Запускается…",
        ServerState.Running => "Работает",
        ServerState.Stopping => "Останавливается…",
        ServerState.Failed => "Ошибка",
        _ => s.ToString(),
    };

    public static string Integration(IntegrationStatus s) => s switch
    {
        IntegrationStatus.ClientNotFound => "Не найдена",
        IntegrationStatus.NotRegistered => "Не подключена",
        IntegrationStatus.Registered => "Подключена",
        IntegrationStatus.Outdated => "Требует обновления",
        IntegrationStatus.Error => "Ошибка",
        _ => s.ToString(),
    };

    public static Color IntegrationColor(IntegrationStatus s) => s switch
    {
        IntegrationStatus.Registered => Theme.OkText,
        IntegrationStatus.Outdated => Theme.WarnText,
        IntegrationStatus.Error => Theme.ErrorText,
        IntegrationStatus.ClientNotFound => Theme.Gray,
        _ => Theme.TextPrimary,
    };

    /// <summary>Название сборки llama.cpp; если модуль недоступен — запасной вариант.</summary>
    public static string Backend(LlamaBackend b) =>
        Ui.Try(() => LlamaReleaseResolver.DisplayName(b), FallbackBackendName(b), "DisplayName");

    public static string FallbackBackendName(LlamaBackend b) => b switch
    {
        LlamaBackend.Auto => "Автовыбор",
        LlamaBackend.Cuda12 => "NVIDIA CUDA 12",
        LlamaBackend.Cuda13 => "NVIDIA CUDA 13",
        LlamaBackend.Vulkan => "Vulkan (любая видеокарта)",
        LlamaBackend.Rocm => "AMD ROCm (HIP)",
        LlamaBackend.Sycl => "Intel SYCL",
        LlamaBackend.Cpu => "Только процессор",
        _ => b.ToString(),
    };

    public static string FitGlyph(FitLevel level) => level switch
    {
        FitLevel.FullGpu or FitLevel.MoeOffload => "✓",
        FitLevel.PartialGpu or FitLevel.CpuOnly => "⚠",
        _ => "✗",
    };

    public static Color FitColor(FitLevel level) => level switch
    {
        FitLevel.FullGpu or FitLevel.MoeOffload => Theme.OkText,
        FitLevel.PartialGpu or FitLevel.CpuOnly => Theme.WarnText,
        _ => Theme.ErrorText,
    };

    public static string Gpu(GpuInfo g)
    {
        var mem = g.DedicatedMemoryBytes > 0 ? $"{g.DedicatedMemoryGb:0.0} ГБ" : "память неизвестна";
        var extra = g.IsIntegrated ? ", встроенная" : "";
        var driver = string.IsNullOrWhiteSpace(g.DriverVersion) ? "" : $", драйвер {g.DriverVersion}";
        return $"{g.Name} — {mem}{extra}{driver}";
    }

    public static string HardwareSummary(HardwareInfo hw)
    {
        var gpu = hw.PrimaryGpu is { } g && !g.IsIntegrated
            ? $"{g.Name} ({g.DedicatedMemoryGb:0.#} ГБ)"
            : "дискретная видеокарта не найдена";
        return $"Видеокарта: {gpu} · ОЗУ: {hw.TotalRamGb:0.#} ГБ · ЦП: {hw.LogicalCores} потоков";
    }

    /// <summary>Рекомендация сборки: из модуля llama, при недоступности — простое правило.</summary>
    public static BackendRecommendation RecommendBackend(HardwareInfo hw)
    {
        try
        {
            return LlamaReleaseResolver.Recommend(hw);
        }
        catch
        {
            if (hw.HasNvidia) return new BackendRecommendation(LlamaBackend.Cuda12, "Найдена видеокарта NVIDIA — сборка CUDA работает быстрее всего.");
            if (hw.PrimaryVramBytes > 0) return new BackendRecommendation(LlamaBackend.Vulkan, "Vulkan поддерживается видеокартами AMD и Intel.");
            return new BackendRecommendation(LlamaBackend.Cpu, "Дискретная видеокарта не найдена — модель будет работать на процессоре.");
        }
    }

    public static IReadOnlyList<LlamaBackend> AvailableBackends(HardwareInfo hw)
    {
        try
        {
            var list = LlamaReleaseResolver.AvailableBackends(hw);
            if (list.Count > 0) return list;
        }
        catch
        {
            // Модуль недоступен — полный список.
        }
        return [LlamaBackend.Cuda12, LlamaBackend.Cuda13, LlamaBackend.Vulkan, LlamaBackend.Rocm, LlamaBackend.Sycl, LlamaBackend.Cpu];
    }

    /// <summary>Строка из числа с ограничением длины (для всплывающей подсказки трея и т. п.).</summary>
    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    public static string ModelName(InstalledModel? m) => m is null ? "модель не выбрана" : m.DisplayName;

    /// <summary>Название MCP-инструмента для статистики: «Вопрос по файлам (local_ask_files)».</summary>
    public static string ToolName(string tool)
    {
        var title = tool switch
        {
            McpToolNames.Status => "Состояние",
            McpToolNames.AskFiles => "Вопрос по файлам",
            McpToolNames.SummarizeLog => "Сжатие лога",
            McpToolNames.ReviewDiff => "Ревью diff",
            McpToolNames.CommitMessage => "Сообщение коммита",
            McpToolNames.WriteFile => "Создание файла",
            McpToolNames.EditFiles => "Правка файлов",
            McpToolNames.Job => "Задачи правки",
            _ => null,
        };
        return title is null ? tool : $"{title} ({tool})";
    }
}
