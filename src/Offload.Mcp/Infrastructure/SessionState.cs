namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Ошибка с понятным IDE текстом (английский, с подсказкой, что делать). Возвращается как isError-результат.
/// </summary>
internal sealed class ToolException(string message) : Exception(message);

/// <summary>
/// Состояние одного MCP-процесса (одна stdio-сессия IDE): корни рабочей области, экономия за сессию,
/// последняя измеренная скорость. Регистрируется в DI как singleton.
/// </summary>
public sealed class SessionState
{
    private readonly object _lock = new();
    private IReadOnlyList<string>? _clientRoots;
    private bool _rootsUnavailable;
    private long _savedTokens;
    private int _modelCalls;

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Последняя скорость генерации (ток/с) в этом процессе.</summary>
    public double? LastGenerationTps { get; set; }

    /// <summary>Сериализует запуск сервера внутри процесса (параллельные read-only вызовы).</summary>
    internal SemaphoreSlim EnsureLock { get; } = new(1, 1);

    /// <summary>
    /// Для тестов: заменить запуск трея (Process.Start Offload.exe --background). Возвращает true, если запущен.
    /// </summary>
    internal Func<bool>? TrayLauncherOverride { get; set; }

    public long SavedTokens => Interlocked.Read(ref _savedTokens);
    public int ModelCalls => Volatile.Read(ref _modelCalls);

    internal void RecordCall(long savedTokens)
    {
        Interlocked.Add(ref _savedTokens, Math.Max(0, savedTokens));
        Interlocked.Increment(ref _modelCalls);
    }

    /// <summary>Клиент прислал notifications/roots/list_changed — корни будут запрошены заново.</summary>
    public void InvalidateRoots()
    {
        lock (_lock)
        {
            _clientRoots = null;
            _rootsUnavailable = false;
        }
    }

    internal bool TryGetCachedRoots(out IReadOnlyList<string>? roots, out bool unavailable)
    {
        lock (_lock)
        {
            roots = _clientRoots;
            unavailable = _rootsUnavailable;
            return roots is not null || unavailable;
        }
    }

    internal void SetClientRoots(IReadOnlyList<string>? roots)
    {
        lock (_lock)
        {
            if (roots is { Count: > 0 })
            {
                _clientRoots = roots;
                _rootsUnavailable = false;
            }
            else
            {
                _clientRoots = null;
                _rootsUnavailable = true;
            }
        }
    }
}
