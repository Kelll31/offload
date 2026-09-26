using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Llama;

namespace Offload.App.Services;

/// <summary>Что делать с сервером роли по запросу из IDE.</summary>
internal enum AuxAction
{
    /// <summary>Роли не назначена модель — запускать нечего.</summary>
    NotAssigned,
    /// <summary>Сервер уже работает с назначенной моделью.</summary>
    UseRunning,
    /// <summary>Запустить (не запущен).</summary>
    Start,
    /// <summary>Работает с другой моделью (назначение сменилось) — перезапустить.</summary>
    Restart,
    /// <summary>Недавний запуск этой же модели не удался — не повторять до конца паузы, IDE перейдёт на основную модель.</summary>
    CoolingDown,
}

/// <summary>Состояние сервера роли для интерфейса.</summary>
internal sealed record AuxServerStatus(ModelRole Role, ServerState State, string? ModelId, string? BaseUrl, string? Error);

/// <summary>Итог запроса запуска сервера роли (для ответа IPC).</summary>
internal sealed record AuxStartResult(bool Ok, string? Message, string? BaseUrl, bool NotAssigned = false);

/// <summary>Чистая логика жизненного цикла серверов ролей (тесты — ServerControllerPolicyTests).</summary>
internal static class AuxServerPolicy
{
    /// <summary>Пауза после неудачного запуска: пока она идёт, запросы IDE сразу уходят на основную модель.</summary>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(2);

    /// <param name="assignedId">Модель, назначенная роли сейчас (null — не назначена).</param>
    /// <param name="runningId">Модель запущенного (или запускаемого) процесса.</param>
    /// <param name="failedId">Модель, запуск которой последним не удался.</param>
    public static AuxAction Decide(string? assignedId, string? runningId, ServerState state, string? failedId, DateTime? failedAtUtc, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(assignedId)) return AuxAction.NotAssigned;
        var same = string.Equals(assignedId, runningId, StringComparison.OrdinalIgnoreCase);
        if (state == ServerState.Running) return same ? AuxAction.UseRunning : AuxAction.Restart;
        if (state is ServerState.Starting or ServerState.Stopping && !same) return AuxAction.Restart;
        if (failedAtUtc is DateTime t && nowUtc - t < FailureCooldown && string.Equals(assignedId, failedId, StringComparison.OrdinalIgnoreCase))
            return AuxAction.CoolingDown;
        return AuxAction.Start;
    }

    /// <summary>Роли, чьи серверы работают не с той моделью, что назначена сейчас (или роль снята), — их нужно остановить.</summary>
    public static IReadOnlyList<ModelRole> ToStop(AppConfig cfg, IReadOnlyDictionary<ModelRole, string?> running)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(running);
        return [.. running
            .Where(r => r.Key != ModelRole.Quality && r.Value is not null
                        && !string.Equals(cfg.RoleModel(r.Key)?.Id, r.Value, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Key)
            .OrderBy(r => r)];
    }
}

/// <summary>
/// Вспомогательные llama-server ролей fast / embed / rerank (ROADMAP §5.2, §6.3): свой процесс на свою роль и порт,
/// запуск по первому запросу из IDE (IPC start-server с ролью), остановка вместе с основным сервером или при смене назначения.
/// Автоперезапуска нет: упавший сервер поднимется при следующем запросе; неудачный запуск повторяется не раньше
/// <see cref="AuxServerPolicy.FailureCooldown"/>, а IDE тем временем работает с основной моделью.
/// </summary>
internal sealed class AuxServers : IDisposable
{
    private sealed class Slot(ModelRole role)
    {
        public ModelRole Role { get; } = role;
        public LlamaServerProcess Process { get; } = new() { Role = role };
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? ModelId;
        public string? Error;
        public string? FailedModelId;
        public DateTime? FailedAtUtc;
    }

    private readonly Dictionary<ModelRole, Slot> _slots = ModelRoleConfig.Auxiliary.ToDictionary(r => r, r => new Slot(r));
    private readonly object _lock = new();
    private bool _disposed;

    public AuxServers()
    {
        foreach (var slot in _slots.Values) slot.Process.StateChanged += _ => RaiseChanged();
    }

    /// <summary>Смена состояния любого сервера роли (из фонового потока).</summary>
    public event Action? Changed;

    /// <summary>Состояние серверов ролей (для страницы «Модели»).</summary>
    public IReadOnlyList<AuxServerStatus> Snapshot()
    {
        var list = new List<AuxServerStatus>();
        foreach (var slot in _slots.Values)
        {
            var state = Ui.Try(() => slot.Process.State, ServerState.Stopped, "aux.State");
            string? model, error;
            lock (_lock)
            {
                model = slot.ModelId;
                error = slot.Error;
            }
            var url = state is ServerState.Running or ServerState.Starting ? Ui.Try(() => slot.Process.CreateRunningClient()?.BaseUrl, null, "aux.Url") : null;
            list.Add(new AuxServerStatus(slot.Role, state, model, url, error ?? Ui.Try(() => slot.Process.LastError, null, "aux.LastError")));
        }
        return list;
    }

    /// <summary>Запустить сервер роли (если нужно) и дождаться готовности. Вызывается из IPC (фоновый поток).</summary>
    public async Task<AuxStartResult> EnsureRunningAsync(ModelRole role, CancellationToken ct = default)
    {
        if (!_slots.TryGetValue(role, out var slot)) return new AuxStartResult(false, L.T("Неизвестная роль модели."), null);
        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new AuxStartResult(false, L.T("Offload завершает работу."), null);
            var cfg = ConfigStore.Reload();
            var assigned = cfg.RoleModel(role);
            string? runningId, failedId;
            DateTime? failedAt;
            lock (_lock)
            {
                runningId = slot.ModelId;
                failedId = slot.FailedModelId;
                failedAt = slot.FailedAtUtc;
            }
            var state = Ui.Try(() => slot.Process.State, ServerState.Stopped, "aux.State");
            var action = AuxServerPolicy.Decide(assigned?.Id, runningId, state, failedId, failedAt, DateTime.UtcNow);
            switch (action)
            {
                case AuxAction.NotAssigned:
                    return new AuxStartResult(false, L.T("Для этой роли не назначена модель."), null, NotAssigned: true);
                case AuxAction.UseRunning:
                    return new AuxStartResult(true, null, slot.Process.CreateRunningClient()?.BaseUrl);
                case AuxAction.CoolingDown:
                    string? err;
                    lock (_lock) err = slot.Error;
                    return new AuxStartResult(false, err ?? L.T("Сервер роли недавно не запустился."), null);
                case AuxAction.Restart:
                    Log.Info("server", $"Сервер роли {role.Key()}: модель сменилась ({runningId} → {assigned!.Id}), перезапуск");
                    await StopSlotAsync(slot).ConfigureAwait(false);
                    break;
            }

            Log.Info("server", $"Запуск сервера роли {role.Key()}: {assigned!.Id}");
            lock (_lock)
            {
                slot.ModelId = assigned.Id;
                slot.Error = null;
            }
            try
            {
                await slot.Process.StartAsync(cfg, null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await StopSlotAsync(slot).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("server", $"Сервер роли {role.Key()} не запустился", ex);
                await StopSlotAsync(slot).ConfigureAwait(false);
                var error = Ui.FriendlyError(ex);
                lock (_lock)
                {
                    slot.Error = error;
                    slot.FailedModelId = assigned.Id;
                    slot.FailedAtUtc = DateTime.UtcNow;
                }
                RaiseChanged();
                return new AuxStartResult(false, error, null);
            }
            lock (_lock)
            {
                slot.FailedModelId = null;
                slot.FailedAtUtc = null;
            }
            RaiseChanged();
            return new AuxStartResult(true, null, slot.Process.CreateRunningClient()?.BaseUrl);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Остановить серверы, чья модель больше не назначена роли (после смены назначений на странице «Модели»).</summary>
    public async Task SyncWithConfigAsync()
    {
        var cfg = ConfigStore.Current;
        Dictionary<ModelRole, string?> running;
        lock (_lock)
        {
            running = _slots.Values.ToDictionary(s => s.Role, s => IsAlive(s) ? s.ModelId : null);
        }
        foreach (var role in AuxServerPolicy.ToStop(cfg, running)) await StopAsync(role).ConfigureAwait(false);
    }

    public async Task StopAsync(ModelRole role)
    {
        if (!_slots.TryGetValue(role, out var slot)) return;
        await slot.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopSlotAsync(slot).ConfigureAwait(false);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Остановить все серверы ролей (вместе с основным сервером).</summary>
    public async Task StopAllAsync()
    {
        foreach (var role in _slots.Keys) await StopAsync(role).ConfigureAwait(false);
    }

    private static bool IsAlive(Slot s) =>
        Ui.Try(() => s.Process.State, ServerState.Stopped, "aux.State") is ServerState.Running or ServerState.Starting;

    private async Task StopSlotAsync(Slot slot)
    {
        var state = Ui.Try(() => slot.Process.State, ServerState.Stopped, "aux.State");
        if (state is ServerState.Running or ServerState.Starting or ServerState.Stopping)
        {
            Log.Info("server", $"Остановка сервера роли {slot.Role.Key()}");
            try
            {
                await slot.Process.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("server", $"Ошибка остановки сервера роли {slot.Role.Key()}: {ex.Message}");
            }
        }
        lock (_lock) slot.ModelId = null;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn("server", $"Обработчик состояния серверов ролей: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots.Values)
        {
            try { slot.Process.Dispose(); } catch (Exception ex) { Log.Warn("server", $"Освобождение сервера роли {slot.Role.Key()}: {ex.Message}"); }
        }
    }
}
