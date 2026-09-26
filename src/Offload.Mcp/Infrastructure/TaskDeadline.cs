using System.Diagnostics;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Срок задачи правки (timeout_minutes): каждому следующему шагу — проверке, раунду исправлений — достаётся
/// остаток срока, а не весь срок заново.
/// </summary>
internal sealed class TaskDeadline
{
    /// <summary>Минимум на проверочную команду, даже если срок почти исчерпан (иначе сборка гарантированно не успеет).</summary>
    public static readonly TimeSpan MinVerifyTime = TimeSpan.FromSeconds(60);

    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Func<TimeSpan> _elapsed;

    public TaskDeadline(TimeSpan total, Func<TimeSpan>? elapsed = null)
    {
        Total = total;
        _elapsed = elapsed ?? (() => Stopwatch.GetElapsedTime(_started));
    }

    public TimeSpan Total { get; }

    /// <summary>Остаток срока (≤ 0 — срок исчерпан).</summary>
    public TimeSpan Remaining => Total - _elapsed();

    public bool Exhausted => Remaining <= TimeSpan.Zero;

    /// <summary>Таймаут очередного шага: остаток срока, но не меньше minimum; null — срок уже исчерпан.</summary>
    public TimeSpan? ForStep(TimeSpan minimum)
    {
        var left = Remaining;
        if (left <= TimeSpan.Zero) return null;
        return left < minimum ? minimum : left;
    }
}
