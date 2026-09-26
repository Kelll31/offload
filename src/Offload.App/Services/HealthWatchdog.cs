using Offload.Llama;

namespace Offload.App.Services;

/// <summary>Решение сторожа по очередной проверке /health.</summary>
internal enum WatchdogVerdict
{
    /// <summary>Проверка не учитывается (сервер не работает, запускается, остановка, модель загружается).</summary>
    Skip,
    /// <summary>Сервер ответил 200 — счётчик сбоев сброшен.</summary>
    Healthy,
    /// <summary>Сервер не ответил, но порог ещё не достигнут.</summary>
    Suspect,
    /// <summary>Порог подряд идущих сбоев достигнут — сервер завис, нужен перезапуск.</summary>
    Restart,
}

/// <summary>
/// Политика сторожа зависания llama-server (без таймеров и сети — только решение по результатам проверок).
/// Перезапуск — после <paramref name="threshold"/> подряд неответов /health, пока процесс жив,
/// и только если сервер хотя бы раз был готов (во время загрузки модели /health отвечает 503).
/// </summary>
/// <remarks>
/// 503 (Loading) после готовности не считается сбоем: так сервер может отвечать, пока заново загружает
/// модель после сна (--sleep-idle-seconds). Сбой — только отсутствие ответа (нет соединения, таймаут).
/// </remarks>
internal sealed class HealthWatchdog(int threshold = HealthWatchdog.DefaultThreshold)
{
    public const int DefaultThreshold = 3;

    /// <summary>Интервал проверок.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждать ответа /health, прежде чем считать проверку неудачной.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly int _threshold = Math.Max(1, threshold);

    /// <summary>Неответов подряд.</summary>
    public int Failures { get; private set; }

    /// <summary>Сервер хотя бы раз ответил «готов» с последнего сброса.</summary>
    public bool WasReady { get; private set; }

    public int Threshold => _threshold;

    /// <summary>Учесть результат проверки.</summary>
    /// <param name="state">Состояние сервера по данным контроллера.</param>
    /// <param name="processAlive">Процесс llama-server жив (падение обрабатывается отдельно).</param>
    /// <param name="inOperation">Идёт запуск/остановка по команде — проверки не учитываются.</param>
    /// <param name="health">Результат /health.</param>
    public WatchdogVerdict Observe(ServerState state, bool processAlive, bool inOperation, HealthState health)
    {
        if (state != ServerState.Running || !processAlive || inOperation)
        {
            Reset();
            return WatchdogVerdict.Skip;
        }
        switch (health)
        {
            case HealthState.Ready:
                WasReady = true;
                Failures = 0;
                return WatchdogVerdict.Healthy;
            case HealthState.Loading:
                return WatchdogVerdict.Skip;
        }
        if (!WasReady) return WatchdogVerdict.Skip;
        Failures++;
        if (Failures < _threshold) return WatchdogVerdict.Suspect;
        Reset();
        return WatchdogVerdict.Restart;
    }

    public void Reset()
    {
        Failures = 0;
        WasReady = false;
    }
}
