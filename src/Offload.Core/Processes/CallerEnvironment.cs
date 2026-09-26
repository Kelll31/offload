using System.Collections;
using System.Text.RegularExpressions;

namespace Offload.Core.Processes;

/// <summary>
/// Окружение вызывающей сессии IDE для дочерних процессов фоновой задачи. Трей запущен при входе в Windows, и его окружение
/// не совпадает с окружением IDE (PATH, активированный venv/conda, nvm, JAVA_HOME, переменные VsDevCmd, CLAUDE_PROJECT_DIR):
/// проверочная команда в трее не нашла бы npm или, хуже, проверила бы задачу другим python/dotnet. Поэтому MCP-процесс при
/// запуске задачи снимает <see cref="Capture"/> — только переменные из белого списка, без секретов, — а исполнитель (трей)
/// включает снимок на время задачи (<see cref="Use"/>); запуск дочерних процессов накладывает его поверх своего окружения
/// (<see cref="ApplyTo"/>) до собственных принудительных переменных Offload (CI=1 и т.п.), которые важнее.
/// </summary>
public static partial class CallerEnvironment
{
    /// <summary>Переменные, которые переносятся из окружения IDE (сравнение без учёта регистра, как в Windows).</summary>
    public static readonly IReadOnlySet<string> AllowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT",
        "VIRTUAL_ENV", "CONDA_PREFIX", "CONDA_DEFAULT_ENV", "PYTHONPATH",
        "NODE_PATH", "NVM_HOME", "NVM_SYMLINK",
        "JAVA_HOME",
        "GOPATH", "GOROOT",
        "CARGO_HOME", "RUSTUP_HOME",
        "DOTNET_ROOT",
        "VSINSTALLDIR", "VCINSTALLDIR", "WindowsSdkDir", "INCLUDE", "LIB", "LIBPATH",
        "CLAUDE_PROJECT_DIR",
    };

    /// <summary>Префикс настроек npm (npm_config_*) — переносятся все, кроме похожих на секреты.</summary>
    public const string NpmConfigPrefix = "npm_config_";

    /// <summary>Не больше переменных в снимке.</summary>
    public const int MaxEntries = 128;

    /// <summary>Потолок длины значения (предел Windows для одной переменной — 32 767 символов).</summary>
    public const int MaxValueChars = 32_767;

    /// <summary>Потолок суммарной длины снимка (имена + значения): описание задачи ограничено 256 КБ.</summary>
    public const int MaxTotalChars = 96 * 1024;

    private static readonly string[] SecretMarkers = ["TOKEN", "KEY", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH"];

    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> Ambient = new();

    /// <summary>Снимок, действующий в текущем потоке выполнения (задача в трее), или null — окружение процесса как есть.</summary>
    public static IReadOnlyDictionary<string, string>? Current => Ambient.Value;

    /// <summary>
    /// Имя переносится: из белого списка или npm_config_*, и не похоже на секрет (TOKEN/KEY/SECRET/PASSWORD/AUTH/… в имени,
    /// «PAT» отдельным словом — PATH и PYTHONPATH остаются).
    /// </summary>
    public static bool IsAllowedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
            return false;
        if (LooksSecret(name)) return false;
        return AllowedNames.Contains(name) || name.StartsWith(NpmConfigPrefix, StringComparison.OrdinalIgnoreCase) && name.Length > NpmConfigPrefix.Length;
    }

    /// <summary>Имя похоже на секрет (токен, ключ, пароль, авторизация, personal access token).</summary>
    public static bool LooksSecret(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var marker in SecretMarkers)
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var part in name.Split(['_', '-', '.', ':', '/'], StringSplitOptions.RemoveEmptyEntries))
            if (part.Equals("PAT", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Снять переносимые переменные окружения этого процесса (или заданного источника — для тестов).</summary>
    public static Dictionary<string, string> Capture(IDictionary? source = null)
    {
        source ??= System.Environment.GetEnvironmentVariables();
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in source)
            if (e.Key is string k && e.Value is string v) raw[k] = v;
        return Sanitize(raw);
    }

    /// <summary>
    /// Очистить снимок (и при снятии, и у исполнителя — снимок из описания задачи это данные, а не команда): только разрешённые
    /// имена, без NUL, без учётных данных в URL (https://user:pass@…), в пределах лимитов; PATH и белый список — в приоритете.
    /// </summary>
    public static Dictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? env)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (env is null) return result;
        var total = 0;
        // Сначала белый список (PATH и т.п.), затем npm_config_* по алфавиту — лимиты режут наименее важное.
        foreach (var (k, v) in env
                     .OrderBy(p => AllowedNames.Contains(p.Key) ? 0 : 1)
                     .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= MaxEntries) break;
            if (!IsAllowedName(k) || v is null || v.Length > MaxValueChars || v.Contains('\0', StringComparison.Ordinal)) continue;
            if (UrlCredentials().IsMatch(v)) continue;
            if (total + k.Length + v.Length > MaxTotalChars) continue;
            if (result.TryAdd(k, v)) total += k.Length + v.Length;
        }
        return result;
    }

    /// <summary>Включить снимок для текущего потока выполнения (до Dispose). null — окружение процесса как есть.</summary>
    public static IDisposable Use(IReadOnlyDictionary<string, string>? env)
    {
        var previous = Ambient.Value;
        Ambient.Value = env;
        return new Scope(previous);
    }

    /// <summary>Наложить действующий снимок на окружение запускаемого процесса (до собственных переменных вызывающего кода).</summary>
    public static void ApplyTo(IDictionary<string, string?> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Ambient.Value is not { } env) return;
        foreach (var (k, v) in env) target[k] = v;
    }

    [GeneratedRegex(@"[a-z][a-z0-9+.\-]*://[^/\s@]+:[^/\s@]*@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    private sealed class Scope(IReadOnlyDictionary<string, string>? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
