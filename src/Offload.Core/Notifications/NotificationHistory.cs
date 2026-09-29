namespace Offload.Core.Notifications;

/// <summary>Важность уведомления (соответствует значку всплывающего окна).</summary>
public enum NotificationLevel { Info, Warning, Error }

/// <summary>Одно уведомление в истории: когда, что, насколько важно и какой раздел открыть по щелчку.</summary>
/// <param name="Shown">Было ли показано всплывающее окно (false — уведомления выключены или «тихие часы»).</param>
public sealed record NotificationEntry(long Id, DateTime TimeUtc, string Title, string Text, NotificationLevel Level, string? Tab, bool Shown);

/// <summary>
/// Центр уведомлений: последние уведомления Offload за сеанс (в том числе не показанные из-за «тихих часов» или выключенных
/// уведомлений) и число непрочитанных. Потокобезопасен; событие <see cref="Changed"/> поднимается в потоке вызывающего.
/// </summary>
public sealed class NotificationHistory(int capacity = NotificationHistory.DefaultCapacity)
{
    public const int DefaultCapacity = 100;

    private readonly object _gate = new();
    private readonly LinkedList<NotificationEntry> _items = new();
    private long _nextId;
    private long _readUpTo;

    /// <summary>Изменился список или число непрочитанных.</summary>
    public event Action? Changed;

    /// <summary>Записать уведомление (одинаковое подряд в течение минуты не дублируется).</summary>
    public NotificationEntry Add(string title, string text, NotificationLevel level, string? tab, bool shown, DateTime utcNow)
    {
        NotificationEntry entry;
        lock (_gate)
        {
            if (_items.First?.Value is { } last && last.Title == title && last.Text == text && utcNow - last.TimeUtc < TimeSpan.FromMinutes(1))
                return last;
            entry = new NotificationEntry(++_nextId, utcNow, title, text, level, tab, shown);
            _items.AddFirst(entry);
            while (_items.Count > Math.Max(1, capacity)) _items.RemoveLast();
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Уведомления, новые сверху.</summary>
    public IReadOnlyList<NotificationEntry> Items
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    /// <summary>Непрочитанные: появились после последнего <see cref="MarkAllRead"/>.</summary>
    public int UnreadCount
    {
        get
        {
            lock (_gate) return _items.Count(i => i.Id > _readUpTo);
        }
    }

    public bool IsUnread(NotificationEntry entry)
    {
        lock (_gate) return entry.Id > _readUpTo;
    }

    public void MarkAllRead()
    {
        lock (_gate)
        {
            if (_items.First is null || _readUpTo >= _items.First.Value.Id) return;
            _readUpTo = _items.First.Value.Id;
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return;
            _readUpTo = _nextId;
            _items.Clear();
        }
        Changed?.Invoke();
    }
}

/// <summary>«Тихие часы»: в заданный промежуток суток всплывающие уведомления не показываются (история их сохраняет).</summary>
public static class QuietHours
{
    /// <summary>Формат времени в настройках: «ЧЧ:ММ».</summary>
    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return TimeSpan.TryParseExact(text.Trim(), [@"hh\:mm", @"h\:mm"], System.Globalization.CultureInfo.InvariantCulture, out time)
               && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    /// <summary>
    /// Попадает ли момент в тихий промежуток [from, to). Промежуток может переходить через полночь (22:00–08:00);
    /// одинаковые границы — промежутка нет. Неразборчивые значения — не тихо.
    /// </summary>
    public static bool IsQuiet(DateTime localNow, string? from, string? to)
    {
        if (!TryParse(from, out var f) || !TryParse(to, out var t) || f == t) return false;
        var now = localNow.TimeOfDay;
        return f < t ? now >= f && now < t : now >= f || now < t;
    }
}
