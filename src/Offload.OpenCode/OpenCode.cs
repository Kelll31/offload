using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.OpenCode;

// Публичный контракт модуля. Реализация:
//   OpenCodeInstaller.cs    — установка standalone-бинарника (OpenCodeReleases.cs — выбор релиза/сборки);
//   OpenCodeConfigWriter.cs — управляемый конфиг, глобальная регистрация, окружение (AgentPrompts.cs — промпты);
//   OpenCodeRunner.cs       — «opencode run» и интерактивный запуск
//                             (RunEvents.cs — разбор JSONL, ChangeTracker.cs — изменённые файлы,
//                              RunQueueLock.cs — очередь запусков, LocalServer.cs — проверка llama-server).

public sealed record OpenCodeInstallResult(string Version, string ExecutablePath);

/// <summary>Сохранённая при обновлении предыдущая версия OpenCode (для отката).</summary>
public sealed record OpenCodePrevious(string Version, string ExecutablePath);

/// <summary>Каналы версий OpenCode (значения <c>OpenCodeSettings.Channel</c>).</summary>
public static class OpenCodeChannels
{
    /// <summary>Проверенная версия с зашитыми SHA-256 (по умолчанию).</summary>
    public const string Stable = "stable";

    /// <summary>Последний релиз GitHub (контрольная сумма — из GitHub API).</summary>
    public const string Latest = "latest";

    /// <summary>Неизвестное или пустое значение — «stable».</summary>
    public static string Normalize(string? channel) =>
        string.Equals(channel?.Trim(), Latest, StringComparison.OrdinalIgnoreCase) ? Latest : Stable;

    /// <summary>
    /// Действующий канал: выбранный явно; если не выбран (конфиг прежних версий) — «latest», когда установлена версия
    /// новее проверенной (иначе «Обновить» откатило бы её), и «stable» в остальных случаях.
    /// </summary>
    public static string Effective(OpenCodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(settings.Channel)) return Normalize(settings.Channel);
        return IsNewer(settings.InstalledVersion, OpenCodeReleases.PinnedVersion) ? Latest : Stable;
    }

    /// <summary>Записать действующий канал в настройки, если он не выбран явно. Возвращает действующий канал.</summary>
    public static string Persist()
    {
        var channel = Effective(ConfigStore.Current.OpenCode);
        if (string.IsNullOrWhiteSpace(ConfigStore.Current.OpenCode.Channel))
        {
            ConfigStore.Update(c =>
            {
                if (string.IsNullOrWhiteSpace(c.OpenCode.Channel)) c.OpenCode.Channel = Effective(c.OpenCode);
            });
            Log.Info("opencode", $"Канал версий OpenCode не был выбран — записан «{channel}» (установлена {ConfigStore.Current.OpenCode.InstalledVersion ?? "—"})");
        }
        return channel;
    }

    /// <summary>Установка <paramref name="target"/> вместо <paramref name="installed"/> была бы откатом на более старую версию.</summary>
    public static bool IsDowngrade(string? installed, string? target) => IsNewer(installed, target);

    /// <summary><paramref name="a"/> новее <paramref name="b"/> (версии вида 1.2.3, «v» и суффиксы -beta/+build не учитываются).</summary>
    internal static bool IsNewer(string? a, string? b) =>
        ParseVersion(a) is { } x && ParseVersion(b) is { } y && x > y;

    private static Version? ParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var core = v.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }
}

public sealed record OpenCodeRunOptions(
    /// <summary>Разрешить выполнение команд оболочки (иначе — только чтение/правка файлов).</summary>
    bool AllowShell,
    TimeSpan Timeout,
    /// <summary>Агент OpenCode: «build» (может править файлы) или «plan» (только анализ).</summary>
    string Agent = "build")
{
    /// <summary>
    /// Отдельный журнал этого запуска (например, jobs\&lt;id&gt;\opencode.log у задачи MCP); дописывается, если уже есть
    /// (раунды исправлений одной задачи). null — только общий logs\opencode-last-run.log, который пишется всегда.
    /// </summary>
    public string? LogPath { get; init; }
}

public sealed record OpenCodeRunResult(
    bool Success,
    /// <summary>Итоговый ответ агента (последнее сообщение ассистента).</summary>
    string FinalText,
    /// <summary>Изменённые/созданные/удалённые файлы (относительно рабочей папки), со статусом: «M path», «A path», «D path».</summary>
    IReadOnlyList<string> ChangedFiles,
    /// <summary>Вызванные агентом инструменты по порядку (кратко: «edit src/a.cs»).</summary>
    IReadOnlyList<string> ToolCalls,
    string? Error,
    int ExitCode,
    TimeSpan Duration,
    long PromptTokens,
    long CompletionTokens);
