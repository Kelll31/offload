using Offload.Core;
using Offload.Core.Config;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Выбор роли модели для вызова инструмента (ROADMAP §5.2). Короткие интерактивные задачи — сообщение коммита, сводка журнала,
/// расширение запроса и реранк в find_context — идут на быструю модель (роль fast), если она назначена; всё остальное
/// (ask_files, ревью, запись кода, агент) — на основную (quality). Чистые функции — без обращения к серверу.
/// </summary>
internal static class ModelRouting
{
    /// <summary>Инструменты, которые быстрая модель выполняет не хуже основной (короткий ввод или простой формат ответа).</summary>
    private static readonly HashSet<string> FastTools = new(StringComparer.Ordinal)
    {
        McpToolNames.CommitMessage,
        McpToolNames.SummarizeLog,
        McpToolNames.FindContext,
    };

    /// <summary>Роль, которую предпочёл бы вызов без учёта настроек: fast — только интерактивные короткие задачи.</summary>
    public static ModelRole PreferredRole(string tool, GpuPriority priority) =>
        priority == GpuPriority.Interactive && FastTools.Contains(tool) ? ModelRole.Fast : ModelRole.Quality;

    /// <summary>
    /// Роль для вызова при текущих настройках: fast — если вызов её предпочитает и быстрая модель назначена, установлена
    /// и отличается от активной; иначе quality. <paramref name="priority"/> null — приоритет инструмента по умолчанию.
    /// </summary>
    public static ModelRole Resolve(AppConfig cfg, string tool, GpuPriority? priority = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (PreferredRole(tool, priority ?? GpuQueue.PriorityFor(tool)) != ModelRole.Fast) return ModelRole.Quality;
        var fast = cfg.RoleModel(ModelRole.Fast);
        return fast is not null && !string.Equals(fast.Id, cfg.ActiveModel()?.Id, StringComparison.OrdinalIgnoreCase)
            ? ModelRole.Fast
            : ModelRole.Quality;
    }
}
