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
        ServerState.NotConfigured => L.T("Не настроен"),
        ServerState.Stopped => L.T("Остановлен"),
        ServerState.Starting => L.T("Запускается…"),
        ServerState.Running => L.T("Работает"),
        ServerState.Stopping => L.T("Останавливается…"),
        ServerState.Failed => L.T("Ошибка"),
        _ => s.ToString(),
    };

    public static string Integration(IntegrationStatus s) => s switch
    {
        IntegrationStatus.ClientNotFound => L.T("Не найдена"),
        IntegrationStatus.NotRegistered => L.T("Не подключена"),
        IntegrationStatus.Registered => L.T("Подключена"),
        IntegrationStatus.Outdated => L.T("Требует обновления"),
        IntegrationStatus.Error => L.T("Ошибка"),
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
        LlamaBackend.Auto => L.T("Автовыбор"),
        LlamaBackend.Cuda12 => "NVIDIA CUDA 12",
        LlamaBackend.Cuda13 => "NVIDIA CUDA 13",
        LlamaBackend.Vulkan => L.T("Vulkan (любая видеокарта)"),
        LlamaBackend.Rocm => "AMD ROCm (HIP)",
        LlamaBackend.Sycl => "Intel SYCL",
        LlamaBackend.Cpu => L.T("Только процессор"),
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
        var mem = g.DedicatedMemoryBytes > 0 ? L.F("{0:0.0} ГБ", g.DedicatedMemoryGb) : L.T("память неизвестна");
        var extra = g.IsIntegrated ? L.T(", встроенная") : "";
        var driver = string.IsNullOrWhiteSpace(g.DriverVersion) ? "" : L.F(", драйвер {0}", g.DriverVersion);
        return $"{g.Name} — {mem}{extra}{driver}";
    }

    public static string HardwareSummary(HardwareInfo hw)
    {
        var gpu = hw.PrimaryGpu is { } g && !g.IsIntegrated
            ? L.F("{0} ({1:0.#} ГБ)", g.Name, g.DedicatedMemoryGb)
            : L.T("дискретная видеокарта не найдена");
        return L.F("Видеокарта: {0} · ОЗУ: {1:0.#} ГБ · ЦП: {2} потоков", gpu, hw.TotalRamGb, hw.LogicalCores);
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
            if (hw.HasNvidia) return new BackendRecommendation(LlamaBackend.Cuda12, L.T("Найдена видеокарта NVIDIA — сборка CUDA работает быстрее всего."));
            if (hw.PrimaryVramBytes > 0) return new BackendRecommendation(LlamaBackend.Vulkan, L.T("Vulkan поддерживается видеокартами AMD и Intel."));
            return new BackendRecommendation(LlamaBackend.Cpu, L.T("Дискретная видеокарта не найдена — модель будет работать на процессоре."));
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

    public static string ModelName(InstalledModel? m) => m is null ? L.T("модель не выбрана") : Offload.Models.ModelCatalog.NameOf(m);

    /// <summary>Название MCP-инструмента для статистики: «Вопрос по файлам (local_ask_files)».</summary>
    public static string ToolName(string tool)
    {
        var title = tool switch
        {
            McpToolNames.Status => L.T("Состояние"),
            McpToolNames.AskFiles => L.T("Вопрос по файлам"),
            McpToolNames.SummarizeLog => L.T("Сжатие лога"),
            McpToolNames.ReviewDiff => L.T("Ревью diff"),
            McpToolNames.CommitMessage => L.T("Сообщение коммита"),
            McpToolNames.WriteFile => L.T("Создание файла"),
            McpToolNames.EditFiles => L.T("Правка файлов"),
            McpToolNames.AgentTask => L.T("Задача агенту"),
            McpToolNames.Verify => L.T("Сборка и тесты"),
            McpToolNames.Job => L.T("Задачи правки"),
            McpToolNames.FindContext => L.T("Контекст под задачу"),
            McpToolNames.SearchCode => L.T("Поиск по коду"),
            McpToolNames.Symbols => L.T("Символы и граф вызовов"),
            McpToolNames.ProjectMap => L.T("Карта проекта"),
            McpToolNames.Diagnostics => L.T("Диагностика сборки"),
            McpToolNames.ApplyPatch => L.T("Применение патча"),
            McpToolNames.Refactor => L.T("Рефакторинг"),
            McpToolNames.Impact => L.T("Влияние изменений"),
            McpToolNames.CodeScan => L.T("Статический анализ"),
            McpToolNames.SecurityReview => L.T("Проверка безопасности"),
            McpToolNames.GitHistory => L.T("История git"),
            McpToolNames.Dependencies => L.T("Зависимости"),
            McpToolNames.Memory => L.T("Память проекта"),
            McpToolNames.Solve => L.T("Решение задачи"),
            _ => null,
        };
        return title is null ? tool : $"{title} ({tool})";
    }
}
