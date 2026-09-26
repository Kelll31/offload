using Offload.Core.Logging;

namespace Offload.Core.Net;

/// <summary>
/// Политика загрузки исполняемых компонентов (llama.cpp, OpenCode): без известной SHA-256 файл не ставится.
/// Контрольная сумма приходит из GitHub API (поле digest); если API недоступен — установку лучше повторить позже,
/// чем запустить непроверенный exe. Осознанный обход — переменная окружения <see cref="AllowUnverifiedEnvVar"/>=1.
/// </summary>
public static class DownloadPolicy
{
    public const string AllowUnverifiedEnvVar = "OFFLOAD_ALLOW_UNVERIFIED";

    public static bool AllowUnverified =>
        Environment.GetEnvironmentVariable(AllowUnverifiedEnvVar)?.Trim() is "1" or "true" or "yes";

    /// <summary>Проверить, что для файла известна контрольная сумма; иначе — исключение с понятным текстом.</summary>
    public static void RequireChecksum(string? sha256, string fileName, string component)
    {
        if (!string.IsNullOrWhiteSpace(sha256)) return;
        if (AllowUnverified)
        {
            Log.Warn("download", $"{fileName}: контрольная сумма неизвестна, установка без проверки ({AllowUnverifiedEnvVar}=1)");
            return;
        }
        throw new DownloadException(L.F("Не удалось получить контрольную сумму {0} ({1}) — GitHub API недоступен или исчерпан лимит запросов. Повторите установку позже. Непроверенные файлы не устанавливаются.", fileName, component));
    }
}
