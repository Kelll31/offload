using Offload.Core;

namespace Offload.App.Services;

/// <summary>
/// Режим разработчика: exe запущен из папки сборки (<c>bin\Debug</c>, <c>bin\Release</c>) или задана переменная
/// <c>OFFLOAD_DEV=1</c>. Такая копия не перенастраивает на себя автозапуск Windows и пути к Offload в конфигах IDE:
/// иначе один запуск dev-сборки «перехватывает» реальные подключения пользователя.
/// </summary>
internal static class DevMode
{
    public const string EnvVar = "OFFLOAD_DEV";

    private static readonly Lazy<bool> Cached = new(() => Detect(Environment.GetEnvironmentVariable(EnvVar), AppPaths.ExecutablePath));

    public static bool Active => Cached.Value;

    internal static bool Detect(string? env, string? exePath)
    {
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim() is not ("0" or "false" or "no");
        if (string.IsNullOrEmpty(exePath)) return false;
        var p = exePath.Replace('/', '\\');
        return p.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
               || p.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }
}
