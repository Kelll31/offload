using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Offload.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag(Level)}] {Category}: {Message}";

    internal static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };
}

/// <summary>
/// Простой потокобезопасный файловый логгер с ротацией по размеру и кольцевым буфером для UI.
/// В режиме MCP нельзя писать в stdout — логгер пишет только в файл (и опционально в stderr).
/// </summary>
/// <remarks>
/// В один файл (например, mcp.log) могут одновременно писать несколько процессов: по MCP-серверу на каждую IDE.
/// Поэтому дозапись идёт с FileShare.ReadWrite | Delete, а ротация — под именованным мьютексом (общим для всех процессов
/// этого файла) с повторной проверкой размера: иначе два процесса могли бы «провернуть» журнал дважды и потерять записи.
/// </remarks>
public static class Log
{
    /// <summary>Переменная окружения с уровнем журнала (debug, info, warn, error) — важнее настройки в config.json.</summary>
    public const string LevelEnvVar = "OFFLOAD_LOG_LEVEL";

    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RingCapacity = 2000;

    private static readonly object FileLock = new();
    private static readonly ConcurrentQueue<LogEntry> Ring = new();
    private static string? _filePath;
    private static bool _alsoStderr;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>Вызывается для каждой новой записи (из любого потока).</summary>
    public static event Action<LogEntry>? EntryAdded;

    /// <summary>Инициализация: имя файла журнала без расширения (app, mcp, llama-server…). Учитывает OFFLOAD_LOG_LEVEL.</summary>
    public static void Init(string fileBaseName, bool alsoStderr = false)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            _filePath = Path.Combine(AppPaths.LogsDir, fileBaseName + ".log");
        }
        catch
        {
            _filePath = null;
        }
        _alsoStderr = alsoStderr;
        if (ParseLevel(Environment.GetEnvironmentVariable(LevelEnvVar)) is { } level) MinLevel = level;
    }

    /// <summary>
    /// Применить уровень из настроек: подробный журнал (отладочные записи) или обычный.
    /// Переменная окружения <see cref="LevelEnvVar"/>, если задана, важнее настройки.
    /// </summary>
    public static void ApplyLevel(bool verbose) =>
        MinLevel = ResolveLevel(verbose, Environment.GetEnvironmentVariable(LevelEnvVar));

    /// <summary>Итоговый уровень: корректное значение переменной окружения, иначе — по настройке.</summary>
    public static LogLevel ResolveLevel(bool verbose, string? envValue) =>
        ParseLevel(envValue) ?? (verbose ? LogLevel.Debug : LogLevel.Info);

    /// <summary>Разбор уровня: debug/dbg/verbose/trace, info, warn/warning, error/err (без учёта регистра). null — не задан или неизвестен.</summary>
    public static LogLevel? ParseLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "debug" or "dbg" or "verbose" or "trace" or "all" => LogLevel.Debug,
        "info" or "inf" or "information" => LogLevel.Info,
        "warn" or "wrn" or "warning" => LogLevel.Warn,
        "error" or "err" => LogLevel.Error,
        _ => null,
    };

    public static string? CurrentFile => _filePath;

    public static IReadOnlyList<LogEntry> Snapshot() => Ring.ToArray();

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);

    public static void Error(string category, string message, Exception ex) =>
        Write(LogLevel.Error, category, $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    public static void Write(LogLevel level, string category, string message)
    {
        if (level < MinLevel) return;
        var entry = new LogEntry(DateTime.Now, level, category, message);

        Ring.Enqueue(entry);
        while (Ring.Count > RingCapacity && Ring.TryDequeue(out _)) { }

        var line = entry.ToString();
        if (_filePath is { } path)
        {
            lock (FileLock)
            {
                AppendLine(path, line, MaxFileBytes);
            }
        }

        if (_alsoStderr)
        {
            try { Console.Error.WriteLine(line); } catch { }
        }

        try { EntryAdded?.Invoke(entry); } catch { }
    }

    /// <summary>
    /// Дописать строку в файл журнала (с ротацией при превышении размера). Безопасно при нескольких процессах-писателях;
    /// ошибки не выбрасываются — журнал не должен ронять приложение.
    /// </summary>
    internal static void AppendLine(string path, string line, long maxBytes)
    {
        try
        {
            RotateIfNeeded(path, maxBytes);
        }
        catch
        {
            // Ротация не удалась — пишем в текущий файл, попробуем в следующий раз.
        }

        var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = OpenForAppend(path);
                fs.Write(bytes);
                return;
            }
            catch (IOException)
            {
                // Нарушение совместного доступа (файл в этот момент переименовывают или держит другой процесс) — короткая пауза.
                Thread.Sleep(5 * (attempt + 1));
            }
            catch
            {
                return;
            }
        }
    }

    /// <summary>
    /// Открыть файл только на дозапись (FILE_APPEND_DATA без FILE_WRITE_DATA): Windows сама пишет в конец файла,
    /// поэтому строки нескольких процессов не затирают друг друга (обычный FileMode.Append пишет по запомненной позиции).
    /// </summary>
    private static FileStream OpenForAppend(string path)
    {
        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new FileInfo(path).Create(FileMode.Append, System.Security.AccessControl.FileSystemRights.AppendData |
                    System.Security.AccessControl.FileSystemRights.Synchronize, share, 4096, FileOptions.None, null);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PlatformNotSupportedException)
            {
                // Нет поддержки прав доступа — обычная дозапись.
            }
        }
        return new FileStream(path, FileMode.Append, FileAccess.Write, share);
    }

    private static void RotateIfNeeded(string path, long maxBytes)
    {
        if (FileLength(path) < maxBytes) return;

        using var mutex = new Mutex(false, MutexName(path));
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                owned = true; // владелец упал — мьютекс наш
            }
            if (!owned) return;

            // Пока ждали мьютекс, другой процесс мог уже повернуть журнал.
            if (FileLength(path) < maxBytes) return;
            try
            {
                File.Move(path, path + ".1", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл открыт без FileShare.Delete (например, старой версией программы) — попробуем в следующий раз.
            }
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Имя мьютекса ротации: одно на файл журнала (в пределах сеанса пользователя).</summary>
    internal static string MutexName(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..24];
        return @"Local\Offload.LogRotate." + hash;
    }
}
