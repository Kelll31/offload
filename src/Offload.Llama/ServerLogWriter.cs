using System.Text;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Llama;

/// <summary>Журнал llama-server.log с ротацией (~10 МБ → .1). Ошибки записи не мешают работе сервера.</summary>
internal sealed class ServerLogWriter : IDisposable
{
    /// <summary>Порог ротации журнала.</summary>
    internal const long MaxLogBytes = 10L * 1024 * 1024;

    private static readonly object FileLock = new();
    private readonly string _path;
    private StreamWriter? _writer;
    private long _written;
    private long _limit;

    private ServerLogWriter(string path) => _path = path;

    public static ServerLogWriter? TryOpen(string path)
    {
        try
        {
            var w = new ServerLogWriter(path);
            lock (FileLock) w.Open();
            return w;
        }
        catch (Exception ex)
        {
            Log.Warn("llama", $"Журнал llama-server недоступен: {ex.Message}");
            return null;
        }
    }

    private void Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        try
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length >= MaxLogBytes)
            {
                var old = _path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(_path, old);
            }
        }
        catch
        {
            // Файл занят — ротация в следующий раз.
        }
        var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs, FileUtil.Utf8NoBom) { AutoFlush = true };
        _written = fs.Length;
        // Если ротация не удалась, не пытаемся на каждой строке.
        _limit = Math.Max(MaxLogBytes, _written + 1024 * 1024);
    }

    public void WriteLine(string line)
    {
        lock (FileLock)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine(line);
                _written += Encoding.UTF8.GetByteCount(line) + 2;
                if (_written >= _limit)
                {
                    _writer.Dispose();
                    _writer = null;
                    Open();
                }
            }
            catch
            {
                // Журнал не должен ронять сервер.
            }
        }
    }

    public void Dispose()
    {
        lock (FileLock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
