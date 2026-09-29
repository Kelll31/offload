using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.Integrations;

/// <summary>Клиент Claude, подключённый автоматически, и результат проверки подключения.</summary>
public sealed record AutoConnectedIde(string Id, string Name, VerifyResult Verify, string? Hint);

/// <summary>Клиент Claude, который подключить не удалось, и причина.</summary>
public sealed record AutoConnectFailure(string Id, string Name, string Message);

/// <summary>Итог одного прогона автоподключения.</summary>
public sealed record AutoConnectReport(IReadOnlyList<AutoConnectedIde> Connected, IReadOnlyList<AutoConnectFailure> Failed)
{
    public static readonly AutoConnectReport Empty = new([], []);

    public bool IsEmpty => Connected.Count == 0 && Failed.Count == 0;

    /// <summary>
    /// Одно объединённое уведомление на весь прогон (а не по одному на IDE): что подключено и проверено, что требует
    /// внимания (причина и что делать), и общая подсказка о перезапуске. Предупреждение — если хоть что-то не прошло.
    /// </summary>
    public (string Title, string Text, bool Warning) Notice()
    {
        var lines = new List<string>();
        var warning = false;
        var okNames = Connected.Where(c => c.Verify.Ok).Select(c => c.Name).ToList();
        if (okNames.Count > 0)
            lines.Add(L.F("Offload подключён и проверен: {0}.", string.Join(", ", okNames)));
        foreach (var c in Connected.Where(c => !c.Verify.Ok && c.Verify.Kind != FailureKind.NotVerifiable))
        {
            warning = true;
            lines.Add(L.F("{0}: запись добавлена, но проверка не прошла — {1}. {2}", c.Name, c.Verify.Summary, c.Verify.Hint).Trim());
        }
        // NotVerifiable для Claude не бывает; на всякий случай — как подключённый без проверки.
        foreach (var c in Connected.Where(c => c.Verify.Kind == FailureKind.NotVerifiable))
            lines.Add(L.F("Offload подключён (без автоматической проверки): {0}.", c.Name));
        foreach (var f in Failed)
        {
            warning = true;
            lines.Add(L.F("{0}: подключить не удалось — {1}", f.Name, f.Message));
        }
        var restart = Connected.Select(c => c.Hint).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();
        if (restart.Count > 0) lines.Add(string.Join(" ", restart));
        var title = warning ? L.T("Подключение к Claude требует внимания") : L.T("Offload подключён к Claude");
        return (title, string.Join(" ", lines), warning);
    }
}

/// <summary>Итог перепроверки: сломавшиеся и восстановившиеся подключения.</summary>
public sealed record RecheckReport(IReadOnlyList<AutoConnectedIde> Broken, IReadOnlyList<AutoConnectedIde> Recovered)
{
    public bool IsEmpty => Broken.Count == 0 && Recovered.Count == 0;

    /// <summary>Уведомление: что сломалось (причина и что делать) и что снова работает.</summary>
    public (string Title, string Text, bool Warning) Notice()
    {
        var lines = new List<string>();
        foreach (var b in Broken)
            lines.Add(L.F("{0}: подключение перестало работать — {1}. {2}", b.Name, b.Verify.Summary, b.Verify.Hint).Trim());
        if (Recovered.Count > 0)
            lines.Add(L.F("Снова работает: {0}.", string.Join(", ", Recovered.Select(r => r.Name))));
        return Broken.Count > 0
            ? (L.T("Подключение Offload сломалось"), string.Join(" ", lines), true)
            : (L.T("Подключение Offload восстановлено"), string.Join(" ", lines), false);
    }
}

/// <summary>
/// Зависимости движка автоподключения. По умолчанию — настоящие: реестр интеграций, <see cref="ConfigStore"/>,
/// <see cref="IntegrationVerifier"/>. Тесты подставляют своё.
/// </summary>
public sealed class AutoConnectHost
{
    public Func<AppConfig> GetConfig { get; init; } = () => ConfigStore.Current;

    public Action<Action<AppConfig>> UpdateConfig { get; init; } = ConfigStore.Update;

    public Func<string, IIdeIntegration?> Find { get; init; } = IntegrationRegistry.Find;

    /// <summary>Что прописывать в IDE (у приложения — путь установленной копии).</summary>
    public required Func<McpServerSpec> GetSpec { get; init; }

    public Func<IIdeIntegration, McpServerSpec, CancellationToken, Task<VerifyResult>> Verify { get; init; } =
        (integration, spec, ct) => IntegrationVerifier.VerifyAsync(integration, spec, ct: ct);

    /// <summary>Приостановить автовосстановление на время записи (claude CLI удаляет и добавляет запись в два шага).</summary>
    public Action<TimeSpan> Pause { get; init; } = _ => { };

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>Установить дополнения Claude Code (навык, разрешения чтения) после первого подключения.</summary>
    public Action InstallClaudeCodeExtras { get; init; } = ClaudeCodeAutoExtras.Install;
}

/// <summary>Дополнения Claude Code при автоподключении — как при первом запуске мастера: навык и разрешения на чтение.</summary>
internal static class ClaudeCodeAutoExtras
{
    public static void Install()
    {
        // Файл без метки Offload (чужой навык с тем же именем) InstallGuidance не перезаписывает — только сообщает.
        if (!ClaudeCodeExtras.IsGuidanceInstalled())
        {
            var r = ClaudeCodeExtras.InstallGuidance();
            Log.Write(r.Ok ? LogLevel.Info : LogLevel.Warn, "integrations", "Автоподключение, навык Claude Code: " + r.Message);
        }
        if (!ClaudeCodeExtras.AreToolsPreapproved())
        {
            var r = ClaudeCodeExtras.AllowReadTools();
            Log.Write(r.Ok ? LogLevel.Info : LogLevel.Warn, "integrations", "Автоподключение, разрешения Claude Code: " + r.Message);
        }
    }
}

/// <summary>
/// Автоподключение Claude Code и Claude Desktop (Claude мог быть установлен позже Offload). За один прогон для каждого
/// клиента: установлен → записи «offload» нет → не отказ пользователя → <see cref="IIdeIntegration.RegisterAsync"/>
/// (то есть <c>ConfigFile.Edit</c>: бэкап, атомарная запись) → клиент добавляется в <see cref="AppConfig.Integrations"/>
/// (дальше им занимается автовосстановление) → дополнения Claude Code → проверка запуском (<see cref="IntegrationVerifier"/>).
/// Неудачную регистрацию повторяет не чаще раза в сутки на клиента, чтобы не спамить уведомлениями; неудачную проверку
/// не откатывает. Пишет только через RegisterAsync, чужие записи «offload» не заменяет.
/// </summary>
public sealed class AutoConnectEngine(AutoConnectHost host)
{
    /// <summary>Не чаще одной попытки регистрации в сутки на клиента после ошибки.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(24);

    /// <summary>Пауза автовосстановления вокруг нашей записи.</summary>
    public static readonly TimeSpan PauseDuration = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, DateTime> _failedAt = new(StringComparer.Ordinal);

    public async Task<AutoConnectReport> RunOnceAsync(CancellationToken ct = default)
    {
        var connected = new List<AutoConnectedIde>();
        var failed = new List<AutoConnectFailure>();
        foreach (var id in AutoConnectPolicy.Candidates(host.GetConfig()))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ConnectAsync(id, connected, failed, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("integrations", $"{id}: автоподключение", ex);
                Fail(id, id, ex.Message, failed);
            }
        }
        return connected.Count == 0 && failed.Count == 0 ? AutoConnectReport.Empty : new AutoConnectReport(connected, failed);
    }

    private async Task ConnectAsync(string id, List<AutoConnectedIde> connected, List<AutoConnectFailure> failed, CancellationToken ct)
    {
        if (host.Find(id) is not { } integration || !integration.IsClientInstalled()) return;
        if (_failedAt.TryGetValue(id, out var when) && host.UtcNow() - when < FailureBackoff) return;

        var spec = host.GetSpec() with { ApproveWriteTools = false };
        if (!AutoConnectPolicy.ShouldConnect(host.GetConfig(), id, integration.GetStatus(spec))) return;

        // Пока пользователь сам правит подключения, не вмешиваемся; статус и настройки перечитываются прямо перед записью.
        host.Pause(PauseDuration);
        if (!AutoConnectPolicy.ShouldConnect(host.GetConfig(), id, integration.GetStatus(spec))) return;

        Log.Info("integrations", $"{id}: найден без подключения Offload — подключаем автоматически");
        var r = await integration.RegisterAsync(spec, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            Fail(id, integration.DisplayName, r.Message, failed);
            return;
        }
        _failedAt.Remove(id);
        host.UpdateConfig(c =>
        {
            if (!c.Integrations.Contains(id)) c.Integrations.Add(id);
            c.Undecline(id);
            c.MarkConnected(id, auto: true);
        });

        if (id == "claude-code")
        {
            try
            {
                host.InstallClaudeCodeExtras();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Warn("integrations", $"Автоподключение, дополнения Claude Code: {ex.Message}");
            }
        }

        VerifyResult verify;
        try
        {
            verify = await host.Verify(integration, spec, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("integrations", $"{id}: проверка подключения: {ex.Message}");
            verify = new VerifyResult(false, FailureKind.StartFailed, ex.Message, 0, TimeSpan.Zero);
        }
        var checkedAt = host.UtcNow();
        host.UpdateConfig(c => c.RecordCheck(id, verify.Ok, verify.Summary, checkedAt));
        Log.Write(verify.Ok ? LogLevel.Info : LogLevel.Warn, "integrations", $"{id}: подключён автоматически, проверка: {(verify.Ok ? "успешно" : verify.Message)}");
        connected.Add(new AutoConnectedIde(id, integration.DisplayName, verify, integration.PostRegisterHint));
    }

    /// <summary>Как часто перепроверять работающие подключения Claude (запуском сервера по записи).</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Перепроверка подключённых клиентов из <see cref="AutoConnectPolicy.Candidates"/>: сервер запускается по записи в IDE,
    /// итог сохраняется в <see cref="AppConfig.IntegrationStates"/>. Без <paramref name="force"/> проверяются только клиенты,
    /// которые не проверялись дольше <see cref="RecheckInterval"/>. Возвращает клиентов, у которых подключение
    /// сломалось (прошлая проверка была успешной или её не было, а эта — нет), — о них стоит сообщить; и тех, кто починился.
    /// Ничего не пишет в конфиги IDE.
    /// </summary>
    public async Task<RecheckReport> RecheckAsync(bool force = false, CancellationToken ct = default)
    {
        var broken = new List<AutoConnectedIde>();
        var recovered = new List<AutoConnectedIde>();
        var cfg = host.GetConfig();
        var now = host.UtcNow();
        foreach (var id in AutoConnectPolicy.Candidates(cfg))
        {
            ct.ThrowIfCancellationRequested();
            if (!cfg.Integrations.Contains(id) || host.Find(id) is not { } integration) continue;
            var state = cfg.IntegrationStates.GetValueOrDefault(id);
            if (!force && state?.LastCheckUtc is { } last && now - last < RecheckInterval) continue;
            try
            {
                if (!integration.IsClientInstalled()) continue;
                var spec = host.GetSpec() with { ApproveWriteTools = false };
                var v = await host.Verify(integration, spec, ct).ConfigureAwait(false);
                if (v.Kind == FailureKind.NotVerifiable) continue;
                var wasOk = state?.LastCheckUtc is null || state.LastCheckOk;
                host.UpdateConfig(c => c.RecordCheck(id, v.Ok, v.Summary, now));
                var item = new AutoConnectedIde(id, integration.DisplayName, v, integration.PostRegisterHint);
                if (!v.Ok && wasOk) broken.Add(item);
                else if (v.Ok && !wasOk) recovered.Add(item);
                Log.Write(v.Ok ? LogLevel.Info : LogLevel.Warn, "integrations", $"{id}: перепроверка подключения — {(v.Ok ? "успешно" : v.Message)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("integrations", $"{id}: перепроверка подключения: {ex.Message}");
            }
        }
        return new RecheckReport(broken, recovered);
    }

    private void Fail(string id, string name, string message, List<AutoConnectFailure> failed)
    {
        _failedAt[id] = host.UtcNow();
        Log.Warn("integrations", $"{id}: автоподключение не удалось: {message}");
        failed.Add(new AutoConnectFailure(id, name, message));
    }
}
