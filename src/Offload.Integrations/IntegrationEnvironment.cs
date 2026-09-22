namespace Offload.Integrations;

/// <summary>
/// Корни, от которых строятся ВСЕ пути к конфигурациям IDE. По умолчанию — реальные папки пользователя;
/// в тестах подменяются песочницей (переменные OFFLOAD_TEST_* или <see cref="Override"/>),
/// чтобы разработка и тесты никогда не трогали настоящие конфиги.
/// В режиме песочницы переменные окружения (CODEX_HOME, CLAUDE_CONFIG_DIR, CLINE_* …) берутся только
/// из словаря песочницы, а внешние CLI по умолчанию не запускаются.
/// </summary>
internal static class IntegrationEnvironment
{
    public const string UserProfileVar = "OFFLOAD_TEST_USERPROFILE";
    public const string AppDataVar = "OFFLOAD_TEST_APPDATA";
    public const string LocalAppDataVar = "OFFLOAD_TEST_LOCALAPPDATA";

    /// <summary>Переменные, перенаправляющие конфиги клиентов. В песочнице дочерним CLI они не передаются из реального окружения.</summary>
    internal static readonly string[] RedirectVariables =
    [
        "CLAUDE_CONFIG_DIR", "CODEX_HOME", "COPILOT_HOME", "CLINE_MCP_SETTINGS_PATH", "CLINE_DATA_DIR", "CLINE_DIR",
        "GEMINI_CLI_HOME", "XDG_CONFIG_HOME", "JUNIE_HOME", "KILO_CONFIG_DIR",
    ];

    internal sealed class Sandbox
    {
        public required string UserProfile { get; init; }
        public required string AppData { get; init; }
        public required string LocalAppData { get; init; }
        public required string ProgramFiles { get; init; }
        public required string ProgramFilesX86 { get; init; }
        public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();
        public bool AllowCli { get; init; }
        public string? ClaudeCliPath { get; init; }
    }

    private static readonly AsyncLocal<Sandbox?> LocalSandbox = new();

    /// <summary>Активная песочница: явная (Override) или из переменных OFFLOAD_TEST_*.</summary>
    internal static Sandbox? Current => LocalSandbox.Value ?? FromVariables(Environment.GetEnvironmentVariable);

    public static bool IsSandboxed => Current is not null;

    public static string UserProfile =>
        Current?.UserProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string AppData =>
        Current?.AppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string LocalAppData =>
        Current?.LocalAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string ProgramFiles =>
        Current?.ProgramFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static string ProgramFilesX86 =>
        Current?.ProgramFilesX86 ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <summary>Переменная окружения (в песочнице — только из её словаря). Пустая строка считается отсутствием.</summary>
    public static string? GetVariable(string name)
    {
        var sb = Current;
        var v = sb is not null
            ? sb.Variables.GetValueOrDefault(name)
            : Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim().Trim('"');
    }

    /// <summary>Можно ли запускать внешние CLI (claude, qoder). В песочнице — только если явно разрешено.</summary>
    public static bool CliAllowed => Current?.AllowCli ?? true;

    /// <summary>Путь к claude.exe, заданный песочницей (живые проверки), иначе null.</summary>
    public static string? ClaudeCliOverride => Current?.ClaudeCliPath;

    /// <summary>
    /// Окружение для дочерних CLI: null — наследовать (обычный режим); в песочнице все домашние папки
    /// и переменные-перенаправления указывают внутрь песочницы.
    /// </summary>
    public static IDictionary<string, string?>? ChildEnvironment()
    {
        var sb = Current;
        if (sb is null) return null;
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = sb.UserProfile,
            ["HOME"] = sb.UserProfile,
            ["APPDATA"] = sb.AppData,
            ["LOCALAPPDATA"] = sb.LocalAppData,
        };
        foreach (var name in RedirectVariables) env[name] = null;
        foreach (var (k, v) in sb.Variables) env[k] = v;
        return env;
    }

    /// <summary>
    /// Только для тестов: подменить все корни на папку <paramref name="root"/> в текущем асинхронном контексте.
    /// Если разрешены CLI, CLAUDE_CONFIG_DIR всегда указывает в песочницу.
    /// </summary>
    public static IDisposable Override(
        string root,
        IReadOnlyDictionary<string, string>? variables = null,
        bool allowCli = false,
        string? claudeCliPath = null)
    {
        var full = Path.GetFullPath(root);
        var vars = new Dictionary<string, string>(variables ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (allowCli && !vars.ContainsKey("CLAUDE_CONFIG_DIR"))
            vars["CLAUDE_CONFIG_DIR"] = Path.Combine(full, ".claude-config");
        var sandbox = new Sandbox
        {
            UserProfile = full,
            AppData = Path.Combine(full, "AppData", "Roaming"),
            LocalAppData = Path.Combine(full, "AppData", "Local"),
            ProgramFiles = Path.Combine(full, "ProgramFiles"),
            ProgramFilesX86 = Path.Combine(full, "ProgramFilesX86"),
            Variables = vars,
            AllowCli = allowCli,
            ClaudeCliPath = claudeCliPath,
        };
        var previous = LocalSandbox.Value;
        LocalSandbox.Value = sandbox;
        return new Restore(previous);
    }

    /// <summary>Песочница из переменных OFFLOAD_TEST_* (null, если ни одна не задана).</summary>
    internal static Sandbox? FromVariables(Func<string, string?> getEnv)
    {
        var profile = NonEmpty(getEnv(UserProfileVar));
        var appData = NonEmpty(getEnv(AppDataVar));
        var local = NonEmpty(getEnv(LocalAppDataVar));
        if (profile is null && appData is null && local is null) return null;

        // Задана хотя бы одна — остальные выводим из неё, но никогда не из реального профиля.
        var baseDir = Path.GetFullPath(profile ?? Path.GetDirectoryName(Path.GetFullPath(appData ?? local!))!);
        return new Sandbox
        {
            UserProfile = baseDir,
            AppData = Path.GetFullPath(appData ?? Path.Combine(baseDir, "AppData", "Roaming")),
            LocalAppData = Path.GetFullPath(local ?? Path.Combine(baseDir, "AppData", "Local")),
            ProgramFiles = Path.Combine(baseDir, "ProgramFiles"),
            ProgramFilesX86 = Path.Combine(baseDir, "ProgramFilesX86"),
        };
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class Restore(Sandbox? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            LocalSandbox.Value = previous;
        }
    }
}
