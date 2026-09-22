using System.Collections.Concurrent;
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
public static class Log
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RingCapacity = 2000;

    private static readonly object FileLock = new();
    private static readonly ConcurrentQueue<LogEntry> Ring = new();
    private static string? _filePath;
    private static bool _alsoStderr;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>Вызывается для каждой новой записи (из любого потока).</summary>
    public static event Action<LogEntry>? EntryAdded;

    /// <summary>Инициализация: имя файла журнала без расширения (app, mcp, llama-server…).</summary>
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
    }

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
        if (_filePath is not null)
        {
            lock (FileLock)
            {
                try
                {
                    RotateIfNeeded(_filePath);
                    using var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                    sw.WriteLine(line);
                }
                catch
                {
                    // Журнал не должен ронять приложение.
                }
            }
        }

        if (_alsoStderr)
        {
            try { Console.Error.WriteLine(line); } catch { }
        }

        try { EntryAdded?.Invoke(entry); } catch { }
    }

    private static void RotateIfNeeded(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < MaxFileBytes) return;
        var old = path + ".1";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }
        catch
        {
            // Файл может быть открыт другим процессом — попробуем в следующий раз.
        }
    }
}
