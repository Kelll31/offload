namespace Offload.Core.Usage;

/// <summary>
/// Инкрементальное чтение статистики для периодически обновляемых экранов: архивы прошлых месяцев читаются один раз
/// (пока не изменятся их размер или время записи), из текущего usage.jsonl — только дописанные с прошлого раза байты.
/// Стоимость <see cref="Refresh"/> — O(новых записей), а не O(всей истории).
/// </summary>
/// <remarks>
/// Подмена текущего файла (ротация в архив, сброс статистики, запись другим процессом «с нуля») распознаётся по уменьшению
/// размера или по изменившимся первым байтам — тогда текущий файл перечитывается целиком. Потокобезопасен.
/// </remarks>
public sealed class UsageReader
{
    private const int PrefixBytes = 64;

    private readonly object _lock = new();
    private readonly Dictionary<string, ArchiveState> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UsageRecord> _current = [];
    private string? _dataDir;
    private long _offset;
    private byte[] _prefix = [];
    private IReadOnlyList<UsageRecord> _snapshot = [];

    private sealed record ArchiveState(long Length, DateTime WriteTimeUtc, IReadOnlyList<UsageRecord> Records);

    /// <summary>Номер версии данных: увеличивается при каждом изменении набора записей.</summary>
    public long Version { get; private set; }

    /// <summary>Все записи на момент последнего <see cref="Refresh"/> (архивы по порядку, затем текущий месяц).</summary>
    public IReadOnlyList<UsageRecord> Records
    {
        get { lock (_lock) return _snapshot; }
    }

    /// <summary>Сбросить кэш (например, после «Сбросить статистику»): следующий Refresh прочитает всё заново.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _archives.Clear();
            ResetCurrent();
            _dataDir = null;
        }
    }

    /// <summary>Дочитать изменения. true — набор записей изменился (и <see cref="Version"/> увеличен).</summary>
    public bool Refresh()
    {
        lock (_lock)
        {
            var changed = false;
            var dataDir = AppPaths.DataDir;
            if (!string.Equals(dataDir, _dataDir, StringComparison.OrdinalIgnoreCase))
            {
                // Другая папка данных (тесты, OFFLOAD_HOME) — всё с нуля.
                _archives.Clear();
                ResetCurrent();
                _dataDir = dataDir;
                changed = true;
            }

            changed |= RefreshArchives();
            changed |= RefreshCurrent();

            if (changed)
            {
                var all = new List<UsageRecord>(_archives.Values.Sum(a => a.Records.Count) + _current.Count);
                foreach (var archive in UsageLog.ArchiveFiles())
                {
                    if (_archives.TryGetValue(archive, out var state)) all.AddRange(state.Records);
                }
                all.AddRange(_current);
                _snapshot = all;
                Version++;
            }
            return changed;
        }
    }

    private bool RefreshArchives()
    {
        var changed = false;
        var files = UsageLog.ArchiveFiles();
        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _archives.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _archives.Remove(gone);
            changed = true;
        }
        foreach (var file in files)
        {
            long length;
            DateTime written;
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists) continue;
                length = fi.Length;
                written = fi.LastWriteTimeUtc;
            }
            catch
            {
                continue;
            }
            if (_archives.TryGetValue(file, out var known) && known.Length == length && known.WriteTimeUtc == written) continue;
            var records = new List<UsageRecord>();
            UsageLog.ReadFile(file, 0, records, null, out _);
            _archives[file] = new ArchiveState(length, written, records);
            changed = true;
        }
        return changed;
    }

    private bool RefreshCurrent()
    {
        var path = AppPaths.UsageFile;
        long length;
        try
        {
            var fi = new FileInfo(path);
            length = fi.Exists ? fi.Length : -1;
        }
        catch
        {
            return false;
        }

        if (length < 0)
        {
            // Файла нет (ротация или сброс) — текущий месяц пуст.
            if (_offset == 0 && _current.Count == 0) return false;
            ResetCurrent();
            return true;
        }

        var changed = false;
        if (length < _offset || (_offset > 0 && !PrefixMatches(path)))
        {
            ResetCurrent();
            changed = true;
        }
        if (length == _offset) return changed;

        var before = _current.Count;
        if (!UsageLog.ReadFile(path, _offset, _current, null, out var consumed)) return changed;
        if (_offset == 0 && consumed > 0) _prefix = ReadPrefix(path, (int)Math.Min(PrefixBytes, consumed));
        _offset = consumed;
        return changed || _current.Count != before;
    }

    private void ResetCurrent()
    {
        _current.Clear();
        _offset = 0;
        _prefix = [];
    }

    private bool PrefixMatches(string path)
    {
        if (_prefix.Length == 0) return true;
        var now = ReadPrefix(path, _prefix.Length);
        return now.AsSpan().SequenceEqual(_prefix);
    }

    private static byte[] ReadPrefix(string path, int count)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[count];
            var total = 0;
            int n;
            while (total < count && (n = fs.Read(buf, total, count - total)) > 0) total += n;
            return total == count ? buf : buf[..total];
        }
        catch
        {
            return [];
        }
    }
}
