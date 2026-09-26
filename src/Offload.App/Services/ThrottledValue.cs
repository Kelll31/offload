namespace Offload.App.Services;

/// <summary>
/// Значение, которое дорого получать (запуск nvidia-smi и т. п.): не чаще одного раза за <c>ttl</c>, одновременные
/// запросы ждут один и тот же вызов. Результат кэшируется и переживает пересоздание окна.
/// </summary>
internal sealed class ThrottledValue<T>(TimeSpan ttl, Func<DateTime>? clock = null)
{
    private readonly object _lock = new();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private Task<T>? _inFlight;
    private (T Value, DateTime At)? _last;

    public TimeSpan Ttl { get; } = ttl;

    /// <summary>Последнее значение, если оно моложе ttl.</summary>
    public bool TryGetFresh(out T value)
    {
        lock (_lock)
        {
            if (_last is { } last && _clock() - last.At < Ttl)
            {
                value = last.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    /// <summary>Свежее значение из кэша или результат нового вызова <paramref name="fetch"/> (в фоновом потоке).</summary>
    public Task<T> GetAsync(Func<Task<T>> fetch)
    {
        lock (_lock)
        {
            if (_last is { } last && _clock() - last.At < Ttl) return Task.FromResult(last.Value);
            if (_inFlight is { IsCompleted: false } running) return running;
            _inFlight = FetchAsync(fetch);
            return _inFlight;
        }
    }

    private async Task<T> FetchAsync(Func<Task<T>> fetch)
    {
        var value = await Task.Run(fetch).ConfigureAwait(false);
        lock (_lock) _last = (value, _clock());
        return value;
    }
}
