using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Mcp.Infrastructure;

/// <summary>Запись доски очереди GPU: кто ждёт или держит слот. Slots — слоты, за которые борется вызов (null — старая запись).</summary>
internal sealed record GpuQueueEntry(
    string Id,
    int Pid,
    string Tool,
    string? Client,
    GpuPriority Priority,
    string State,
    int Slot,
    DateTime QueuedUtc,
    DateTime? SinceUtc,
    int[]? Slots = null)
{
    public const string Waiting = "waiting";
    public const string Holding = "holding";
}

/// <summary>
/// Межпроцессная «доска» очереди GPU: по файлу на ожидающий/держащий слот вызов в <c>DataDir\gpu-queue</c>.
/// Файл открыт с <see cref="FileOptions.DeleteOnClose"/>: Windows удаляет его при закрытии дескриптора, в том числе при
/// аварийном завершении процесса, — доска не копит мусор. Остатки после сбоя питания отсеиваются по PID.
/// Доска — только для прозрачности и мягкого приоритета; корректность исключительного владения слотом дают мьютексы.
/// </summary>
internal static class GpuQueueBoard
{
    private const int MaxEntryBytes = 4096;

    public static string Dir => Path.Combine(AppPaths.DataDir, "gpu-queue");

    /// <summary>Открытая запись этого процесса; Dispose удаляет файл.</summary>
    internal sealed class Entry : IDisposable
    {
        private readonly object _lock = new();
        private readonly FileStream _fs;
        private GpuQueueEntry _data;

        internal Entry(FileStream fs, GpuQueueEntry data)
        {
            _fs = fs;
            _data = data;
            Write();
        }

        public string Id => _data.Id;

        public void Update(string state, int slot)
        {
            lock (_lock)
            {
                _data = _data with { State = state, Slot = slot, SinceUtc = DateTime.UtcNow };
                Write();
            }
        }

        private void Write()
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_data, Json.Compact));
                _fs.Position = 0;
                _fs.SetLength(0);
                _fs.Write(bytes);
                _fs.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                Log.Debug("mcp", $"Доска очереди GPU: запись не обновлена ({ex.Message})");
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _fs.Dispose(); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Поставить вызов на доску как ожидающий; slots — слоты, за которые он борется. null — доска недоступна
    /// (очередь работает и без неё).
    /// </summary>
    public static Entry? Register(GpuRequest request, int[]? slots = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var pid = Environment.ProcessId;
            var id = $"{pid}-{Guid.NewGuid():N}";
            var fs = new FileStream(Path.Combine(Dir, id + ".json"), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
            var now = DateTime.UtcNow;
            return new Entry(fs, new GpuQueueEntry(id, pid, request.Tool, SanitizeClient(request.Client), request.Priority, GpuQueueEntry.Waiting, -1, now, null, slots));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Debug("mcp", $"Доска очереди GPU недоступна: {ex.Message}");
            return null;
        }
    }

    /// <summary>Текущие записи всех процессов (без исключений).</summary>
    public static List<GpuQueueEntry> ReadSafe()
    {
        try { return Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static List<GpuQueueEntry> Read()
    {
        var list = new List<GpuQueueEntry>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var path in Directory.EnumerateFiles(Dir, "*.json"))
        {
            GpuQueueEntry? e = null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length is <= 0 or > MaxEntryBytes) continue;
                var buf = new byte[fs.Length];
                fs.ReadExactly(buf);
                e = JsonSerializer.Deserialize<GpuQueueEntry>(buf, Json.Compact);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or EndOfStreamException)
            {
                // Файл удаляется или переписывается прямо сейчас — пропускаем.
                continue;
            }
            if (e is null) continue;
            if (!IsAlive(e))
            {
                // Остаток после сбоя питания/перезагрузки (при обычном завершении файл удаляет Windows).
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                continue;
            }
            list.Add(e);
        }
        return list;
    }

    /// <summary>Процесс-владелец жив и запущен раньше, чем появилась запись (иначе PID переиспользован).</summary>
    private static bool IsAlive(GpuQueueEntry e)
    {
        if (e.Pid == Environment.ProcessId) return true;
        try
        {
            using var p = Process.GetProcessById(e.Pid);
            if (p.HasExited) return false;
            try { return p.StartTime.ToUniversalTime() <= e.QueuedUtc.AddSeconds(5); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return true; }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>Наибольшая длина имени клиента на доске и в local_status.</summary>
    internal const int MaxClientChars = 40;

    /// <summary>
    /// Имя клиента MCP (clientInfo.name) присылает любой локальный процесс, а local_status читает облачная модель другой IDE —
    /// канал для внедрения инструкций. Остаются только [A-Za-z0-9 ._-], не длиннее <see cref="MaxClientChars"/>;
    /// пустой результат — null.
    /// </summary>
    internal static string? SanitizeClient(string? s) => Sanitize(s, MaxClientChars);

    private static string? Sanitize(string? s, int max)
    {
        if (s is null) return null;
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var c in s)
        {
            if (sb.Length >= max) break;
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-') sb.Append(c);
        }
        var clean = sb.ToString().Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>Сводка для local_status: держатели слотов (инструмент, клиент, сколько держит) и число ждущих.</summary>
    public static string Describe(IReadOnlyList<GpuQueueEntry> entries, DateTime nowUtc, int maxLines = 8)
    {
        var sb = new StringBuilder();
        var holders = entries.Where(e => e.State == GpuQueueEntry.Holding).OrderBy(e => e.Slot < 0 ? int.MaxValue : e.Slot).ThenBy(e => e.SinceUtc).ToList();
        var waiting = entries.Where(e => e.State == GpuQueueEntry.Waiting).OrderBy(e => e.Priority).ThenBy(e => e.QueuedUtc).ToList();
        foreach (var h in holders.Take(maxLines))
        {
            var where = h.Slot >= 0 ? $"slot {h.Slot}" : "no slot (shares llama-server's own queue)";
            // Записи доски пишут другие процессы (в том числе старые версии) — имена очищаются и при показе.
            var client = SanitizeClient(h.Client);
            sb.Append($"  {where}: {Sanitize(h.Tool, MaxClientChars)}{(client is null ? "" : $" ({client})")}, held {Ago(nowUtc - (h.SinceUtc ?? h.QueuedUtc))}\n");
        }
        if (holders.Count > maxLines) sb.Append($"  … and {holders.Count - maxLines} more\n");
        if (waiting.Count > 0)
        {
            var longest = nowUtc - waiting.Min(w => w.QueuedUtc);
            sb.Append($"  waiting: {waiting.Count} ({string.Join(", ", waiting.Take(4).Select(w => Sanitize(w.Tool, MaxClientChars) + (w.Priority == GpuPriority.Interactive ? "" : $" [{w.Priority.ToString().ToLowerInvariant()}]")))}" +
                      $"{(waiting.Count > 4 ? ", …" : "")}; longest {Ago(longest)})\n");
        }
        return sb.ToString();
    }

    internal static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalSeconds < 90 ? $"{t.TotalSeconds:0} s" : t.TotalMinutes < 90 ? $"{t.TotalMinutes:0} min" : $"{t.TotalHours:0.#} h";
    }
}
