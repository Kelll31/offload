using Offload.Core.Hardware;

namespace Offload.App.Services;

/// <summary>Кэш сведений об оборудовании (определение занимает секунду-две из-за nvidia-smi).</summary>
internal sealed class HardwareCache
{
    private readonly object _lock = new();
    private Task<HardwareInfo>? _task;

    /// <summary>Уже определённое оборудование или null.</summary>
    public HardwareInfo? Current
    {
        get
        {
            lock (_lock) return _task is { IsCompletedSuccessfully: true } t ? t.Result : null;
        }
    }

    public Task<HardwareInfo> GetAsync(bool refresh = false)
    {
        lock (_lock)
        {
            if (refresh || _task is null || _task.IsFaulted || _task.IsCanceled)
                _task = Task.Run(() => HardwareDetector.DetectAsync());
            return _task;
        }
    }
}
