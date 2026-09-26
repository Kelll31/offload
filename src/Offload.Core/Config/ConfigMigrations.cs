using System.Text.Json;
using System.Text.Json.Nodes;

namespace Offload.Core.Config;

/// <summary>
/// Миграции config.json по <see cref="AppConfig.SchemaVersion"/>. Шаг получает сырой JSON (camelCase) версии
/// <see cref="Step.From"/> и приводит его к версии From + 1: переименовать или перенести поле, сменить формат значения.
/// Простое добавление поля миграции не требует — отсутствующее поле получает значение по умолчанию при чтении.
/// </summary>
/// <remarks>
/// Как добавить миграцию: увеличить <see cref="CurrentVersion"/> и дописать в конец <see cref="Steps"/> шаг
/// с From = прежняя версия (тест проверяет, что шаги идут подряд от 1 до текущей версии). Шаг должен быть идемпотентным
/// и не падать на неполном файле (ручная правка). Перед записью мигрированного файла прежний копируется в backups.
/// </remarks>
internal static class ConfigMigrations
{
    /// <summary>Версия схемы, которую пишет эта сборка.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Шаг миграции: From → From + 1.</summary>
    internal readonly record struct Step(int From, Action<JsonObject> Apply);

    /// <summary>Шаги по порядку версий (сейчас пусто: с версии 1 поля только добавлялись).</summary>
    internal static readonly IReadOnlyList<Step> Steps = [];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Привести текст config.json версии <paramref name="fromVersion"/> к <paramref name="toVersion"/>. null — миграция
    /// не нужна (версия не ниже целевой, шагов нет или корень — не объект). Исключения шагов пробрасываются.
    /// </summary>
    internal static string? Migrate(string text, int fromVersion, int toVersion = CurrentVersion, IReadOnlyList<Step>? steps = null)
    {
        steps ??= Steps;
        if (fromVersion >= toVersion) return null;
        if (JsonNode.Parse(text, documentOptions: DocumentOptions) is not JsonObject root) return null;

        var version = Math.Max(fromVersion, 1);
        var applied = false;
        foreach (var step in steps.Where(s => s.From >= version && s.From < toVersion).OrderBy(s => s.From))
        {
            if (step.From != version) break; // пропуск в цепочке — дальше не мигрируем, чтобы не применить шаг не к той схеме
            step.Apply(root);
            version++;
            applied = true;
        }
        if (!applied) return null;

        root["schemaVersion"] = version;
        return root.ToJsonString();
    }
}
