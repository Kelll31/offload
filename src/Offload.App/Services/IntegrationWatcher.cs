using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Integrations;

namespace Offload.App.Services;

/// <summary>
/// Политика автовосстановления без таймеров и файлов: когда включено и сколько раз за час можно чинить одну IDE
/// (если IDE упорно сбрасывает запись, не воюем с ней бесконечно).
/// </summary>
internal sealed class AutoRepairPolicy(int maxPerWindow = AutoRepairPolicy.DefaultMaxPerWindow, TimeSpan? window = null)
{
    public const int DefaultMaxPerWindow = 3;

    private readonly TimeSpan _window = window ?? TimeSpan.FromHours(1);
    private readonly Dictionary<string, List<DateTime>> _history = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>
    /// Автовосстановление действует: включено в настройках, это не dev-сборка и не «чужая» копия
    /// (иначе dev-exe или портативная копия переписали бы пути установленной программы).
    /// </summary>
    public static bool Enabled(AppConfig cfg, bool devMode, bool foreignCopy) =>
        cfg.Ui.AutoRepairIntegrations && !devMode && !foreignCopy && cfg.Integrations.Count > 0;

    /// <summary>Можно ли восстановить IDE сейчас (и учесть попытку).</summary>
    public bool TryAcquire(string id, DateTime nowUtc)
    {
        lock (_history)
        {
            if (!_history.TryGetValue(id, out var list)) _history[id] = list = [];
            list.RemoveAll(t => nowUtc - t >= _window);
            if (list.Count >= maxPerWindow) return false;
            list.Add(nowUtc);
            _warned.Remove(id);
            return true;
        }
    }

    /// <summary>Предупредить об исчерпанном лимите один раз (до следующего удачного восстановления).</summary>
    public bool ShouldWarn(string id)
    {
        lock (_history) return _warned.Add(id);
    }
}

/// <summary>
/// Уведомления «подключение требует решения пользователя»: не больше одного на IDE за сеанс Offload
/// (пользователь мог удалить запись намеренно — не напоминаем при каждой записи IDE в свой конфиг).
/// </summary>
internal sealed class AttentionNotices
{
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);

    /// <summary>Причины по IDE, о которых в этом сеансе ещё не сообщали (и отметить их как показанные).</summary>
    public IReadOnlyList<RepairIssue> Take(IEnumerable<RepairIssue> issues)
    {
        lock (_shown) return issues.Where(i => _shown.Add(i.Id)).ToList();
    }
}

/// <summary>
/// Наблюдение за подключениями (ROADMAP 8.1): следит (<see cref="FileSystemWatcher"/>) за конфигами IDE из
/// <c>cfg.Integrations</c>. Сам исправляет только одно — путь к Offload.exe в нашей записи, если exe по старому пути
/// больше нет (программу переместили); остальные ключи записи не трогаются. Если запись удалена (IDE сбросила конфиг или
/// пользователь убрал её сам), «offload» занят другой программой, устарели настройки записи или она указывает на другую
/// копию Offload — ничего не пишет, а один раз за сеанс Offload показывает уведомление по каждой IDE; щелчок открывает
/// раздел «Интеграции», где запись можно восстановить кнопкой.
/// При старте один раз обновляет управляемые тексты (дополнения Claude Code, секции AGENTS.md/GEMINI.md), если версия Offload
/// сменилась, а пользователь их не менял; разрешения Claude Code — только при включённом автовосстановлении.
/// В режиме разработчика и в копии не из папки установки ничего не делает.
/// </summary>
internal sealed class IntegrationWatcher : IDisposable
{
    /// <summary>Пауза после последнего изменения файла: IDE пишет конфиг в несколько приёмов.</summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(15);

    /// <summary>Не чаще одной проверки за этот интервал (разбор больших конфигов при частой записи IDE).</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(20);

    private long _lastCheckTicks;

    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly AutoRepairPolicy _policy = new();
    private readonly AttentionNotices _notices = new();
    private System.Threading.Timer? _timer;
    private IAppShell? _shell;
    private string _signature = "";
    private int _running;
    private bool _started;
    private bool _disposed;

    /// <summary>Запустить наблюдение (повторный вызов ничего не делает).</summary>
    public void Start(IAppShell shell)
    {
        if (DevMode.Active || InstallInfo.Foreign is not null)
        {
            Log.Info("integrations", "Автовосстановление подключений не действует (режим разработчика или копия не из папки установки)");
            return;
        }
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _shell = shell;
            _timer = new System.Threading.Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        }
        ConfigStore.Saved += OnConfig;
        ConfigStore.ExternallyChanged += OnConfig;
        Rewatch(ConfigStore.Current);
        // Разрешения в ~/.claude/settings.json — только если пользователь не выключил автовосстановление.
        var approvals = Enabled(ConfigStore.Current);
        _ = Task.Run(() => RefreshManagedTexts(approvals));
        Schedule(FirstCheck);
    }

    private void OnConfig(AppConfig cfg)
    {
        Rewatch(cfg);
        // Включили автовосстановление или добавили IDE — проверим сразу.
        if (Enabled(cfg)) Schedule(Debounce);
    }

    private static bool Enabled(AppConfig cfg) =>
        AutoRepairPolicy.Enabled(cfg, DevMode.Active, InstallInfo.Foreign is not null);

    /// <summary>Пересоздать наблюдателей, если изменился набор файлов (или выключили автовосстановление).</summary>
    private void Rewatch(AppConfig cfg)
    {
        var files = Enabled(cfg)
            ? cfg.Integrations
                .Select(IntegrationRegistry.Find)
                .Where(i => i is not null)
                .SelectMany(i => IntegrationRegistry.ConfigFiles(i!))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        var signature = string.Join("|", files);
        lock (_gate)
        {
            if (_disposed || signature == _signature) return;
            _signature = signature;
            DisposeWatchers();
            foreach (var group in files.GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(group.Key)) continue;
                var names = group.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var w = new FileSystemWatcher(group.Key)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    };
                    FileSystemEventHandler changed = (_, e) => { if (names.Contains(e.Name ?? "")) Schedule(Debounce); };
                    w.Changed += changed;
                    w.Created += changed;
                    w.Deleted += changed;
                    w.Renamed += (_, e) =>
                    {
                        if (names.Contains(e.Name ?? "") || names.Contains(e.OldName ?? "")) Schedule(Debounce);
                    };
                    w.Error += (_, e) => Log.Debug("integrations", $"Наблюдение за {group.Key}: {e.GetException().Message}");
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    Log.Debug("integrations", $"Не удалось следить за {group.Key}: {ex.Message}");
                }
            }
            if (files.Count > 0) Log.Debug("integrations", $"Наблюдение за конфигами IDE: {files.Count} файл(ов)");
        }
    }

    private void Schedule(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private static long _pausedUntilTicks;

    /// <summary>
    /// Приостановить проверки: пользователь сам подключает/отключает IDE (claude CLI удаляет и добавляет запись в два шага —
    /// промежуточное состояние не должно «чиниться»). Повторный вызов переносит срок.
    /// </summary>
    public static void PauseFor(TimeSpan duration) =>
        Interlocked.Exchange(ref _pausedUntilTicks, (DateTime.UtcNow + duration).Ticks);

    /// <summary>Сколько ещё действует пауза <see cref="PauseFor"/> (ноль — проверки не приостановлены).</summary>
    public static TimeSpan PausedRemaining()
    {
        var left = new DateTime(Interlocked.Read(ref _pausedUntilTicks), DateTimeKind.Utc) - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void OnTimer() => _ = CheckAsync();

    private async Task CheckAsync()
    {
        var paused = PausedRemaining();
        if (paused > TimeSpan.Zero)
        {
            Schedule(paused + Debounce);
            return;
        }
        // ~/.claude.json Claude Code переписывает постоянно — проверяем не чаще раза в MinInterval.
        var sinceLast = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastCheckTicks), DateTimeKind.Utc);
        if (sinceLast < MinInterval)
        {
            Schedule(MinInterval - sinceLast);
            return;
        }
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            Schedule(Debounce);
            return;
        }
        Interlocked.Exchange(ref _lastCheckTicks, DateTime.UtcNow.Ticks);
        try
        {
            var cfg = ConfigStore.Current;
            if (!Enabled(cfg)) return;
            var report = await IntegrationRegistry.RepairAsync(
                cfg.Integrations.ToList(), InstallInfo.McpSpec(), id => _policy.TryAcquire(id, DateTime.UtcNow)).ConfigureAwait(false);
            Report(report);
        }
        catch (Exception ex)
        {
            Log.Error("integrations", "Автовосстановление подключений", ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void Report(RepairReport report)
    {
        var shell = _shell;
        if (shell is null) return;
        if (report.Repaired.Count > 0)
        {
            shell.Notify(L.T("Путь к Offload в IDE обновлён"),
                L.F("Запись Offload указывала на программу, которой больше нет, — путь исправлен: {0}. Перезапустите эти IDE.", Names(report.Repaired)),
                tab: Tabs.Integrations);
            shell.PostToUi(shell.ConfigChanged);
        }
        var warn = report.Skipped.Where(_policy.ShouldWarn).ToList();
        if (warn.Count > 0)
        {
            Log.Warn("integrations", "IDE снова и снова возвращает старый путь к Offload — обновление приостановлено на час: " + string.Join(", ", warn));
            shell.Notify(L.T("IDE снова меняет подключение Offload"),
                L.F("Путь к Offload в настройках снова устарел: {0}. Обновление приостановлено на час — проверьте настройки IDE (раздел «Интеграции» → «Проверить»).", Names(warn)),
                ToolTipIcon.Warning, tab: Tabs.Integrations);
        }
        var fresh = _notices.Take(report.NeedsAttention);
        if (fresh.Count > 0)
        {
            Log.Info("integrations", "Подключения требуют решения пользователя: " + string.Join(", ", fresh.Select(i => $"{i.Id} ({i.Need})")));
            shell.Notify(L.T("Подключение к IDE изменено"), AttentionText(fresh), ToolTipIcon.Warning, tab: Tabs.Integrations);
            shell.PostToUi(shell.ConfigChanged);
        }
    }

    /// <summary>Текст уведомления: что случилось в каждой IDE; щелчок открывает «Интеграции», где запись восстанавливается кнопкой.</summary>
    internal static string AttentionText(IReadOnlyList<RepairIssue> issues)
    {
        var parts = issues.Select(i => NeedText(i.Need, IntegrationRegistry.Find(i.Id)?.DisplayName ?? i.Id));
        return L.F("{0}. Offload не меняет эти настройки сам — щёлкните, чтобы открыть «Интеграции»: там подключение можно восстановить или отключить IDE в Offload.",
            string.Join("; ", parts));
    }

    private static string NeedText(RepairNeed need, string name) => need switch
    {
        RepairNeed.Missing => L.F("{0}: запись Offload удалена из настроек", name),
        RepairNeed.Foreign => L.F("{0}: имя «offload» занято другим сервером", name),
        RepairNeed.StaleSettings => L.F("{0}: настройки записи Offload устарели", name),
        RepairNeed.OtherCopy => L.F("{0}: запись указывает на другую копию Offload", name),
        RepairNeed.PathMoved => L.F("{0}: путь к Offload устарел и не обновлён автоматически", name),
        _ => name,
    };

    private static string Names(IEnumerable<string> ids) =>
        string.Join(", ", ids.Select(id => IntegrationRegistry.Find(id)?.DisplayName ?? id));

    /// <summary>Обновить управляемые тексты после смены версии Offload (изменённые пользователем не трогаются).</summary>
    private static void RefreshManagedTexts(bool includeApprovals)
    {
        try
        {
            foreach (var r in ClaudeCodeExtras.RefreshManaged(includeApprovals).Concat(ClientGuidance.RefreshAll()))
                Log.Write(r.Ok ? LogLevel.Info : LogLevel.Warn, "integrations", "Обновление управляемых файлов: " + r.Message);
        }
        catch (Exception ex)
        {
            Log.Warn("integrations", $"Обновление управляемых файлов: {ex.Message}");
        }
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Папку могли удалить.
            }
        }
        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeWatchers();
            _timer?.Dispose();
            _timer = null;
        }
        ConfigStore.Saved -= OnConfig;
        ConfigStore.ExternallyChanged -= OnConfig;
    }
}
