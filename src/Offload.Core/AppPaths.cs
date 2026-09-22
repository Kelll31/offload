namespace Offload.Core;

/// <summary>
/// Расположение всех файлов приложения. По умолчанию — %LOCALAPPDATA%\Offload.
/// Переменная окружения OFFLOAD_HOME переопределяет корень (портативный режим и тесты).
/// </summary>
public static class AppPaths
{
    public const string HomeEnvVar = "OFFLOAD_HOME";

    private static string? _dataDirOverride;

    public static string DataDir
    {
        get
        {
            if (_dataDirOverride is not null) return _dataDirOverride;
            var env = Environment.GetEnvironmentVariable(HomeEnvVar);
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Offload");
        }
    }

    /// <summary>Только для тестов: переопределить корень данных в пределах процесса.</summary>
    public static void OverrideDataDir(string? path) => _dataDirOverride = path is null ? null : Path.GetFullPath(path);

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string LlamaDir => Path.Combine(DataDir, "llama.cpp");
    public static string DefaultModelsDir => Path.Combine(DataDir, "models");
    public static string OpenCodeDir => Path.Combine(DataDir, "opencode");
    public static string OpenCodeConfigFile => Path.Combine(OpenCodeDir, "opencode.json");
    public static string DownloadsDir => Path.Combine(DataDir, "downloads");
    public static string UsageFile => Path.Combine(DataDir, "usage.jsonl");
    public static string BackupsDir => Path.Combine(DataDir, "backups");

    /// <summary>Полный путь к исполняемому файлу Offload.exe (текущего процесса).</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Offload.exe");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(LlamaDir);
        Directory.CreateDirectory(OpenCodeDir);
        Directory.CreateDirectory(DownloadsDir);
        Directory.CreateDirectory(BackupsDir);
    }
}
