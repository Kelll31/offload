using System.Diagnostics;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Usage;
using Offload.Integrations.Clients;

namespace Offload.Integrations;

public enum DoctorLevel { Ok, Info, Warning, Error }

/// <summary>Одна строка отчёта «доктора интеграций».</summary>
public sealed record DoctorCheck(DoctorLevel Level, string Text);

/// <summary>Отчёт о подключении Offload к одной IDE.</summary>
public sealed record DoctorReport(
    string IntegrationId,
    string DisplayName,
    IReadOnlyList<DoctorCheck> Checks,
    HandshakeResult? Handshake,
    DateTime? LastCallUtc)
{
    /// <summary>Самая серьёзная проблема (подсказки о подводных камнях не в счёт).</summary>
    public DoctorLevel Worst => Checks.Count == 0 ? DoctorLevel.Ok : Checks.Max(c => c.Level);
}

/// <summary>
/// «Доктор интеграций»: для IDE разбирает её конфиг, находит запись Offload, проверяет, что exe существует и какой он версии,
/// и проходит MCP-рукопожатие initialize + tools/list, запуская exe так, как это сделает IDE; добавляет известные подводные
/// камни клиента и время последнего обращения из этой IDE по журналу использования.
/// Конфиг IDE может изменить кто угодно, поэтому запускается только точный путь текущего Offload.exe
/// (<see cref="McpServerSpec.ForCurrentExecutable"/>) или известной установленной копии, и только с аргументами ровно
/// [«--mcp»]. Имя файла Offload.exe (<see cref="Editing.CommandPath.IsOffload"/>) основанием для запуска не является.
/// Сетевые пути (UNC) отклоняются до любого обращения к файловой системе.
/// </summary>
public static class IntegrationDoctor
{
    /// <summary>Сколько ждать ответа сервера (первый запуск single-file exe может задержать антивирус).</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    public static Task<DoctorReport> CheckAsync(
        IIdeIntegration integration,
        IReadOnlyDictionary<string, DateTime>? lastCalls = null,
        CancellationToken ct = default) =>
        CheckAsync(integration, lastCalls, installedCopies: null, ct);

    /// <param name="integration">IDE.</param>
    /// <param name="lastCalls">Последние обращения по идентификатору интеграции.</param>
    /// <param name="installedCopies">
    /// Пути установленных копий Offload (из записи установщика), которые тоже можно запускать для проверки.
    /// Текущий exe разрешён всегда.
    /// </param>
    /// <param name="ct">Отмена.</param>
    public static async Task<DoctorReport> CheckAsync(
        IIdeIntegration integration,
        IReadOnlyDictionary<string, DateTime>? lastCalls,
        IReadOnlyCollection<string>? installedCopies,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(integration);
        var checks = new List<DoctorCheck>();
        HandshakeResult? handshake = null;
        var spec = SafeSpec();
        var lastCall = lastCalls is not null && lastCalls.TryGetValue(integration.Id, out var t) ? t : (DateTime?)null;

        if (!Safe(integration.IsClientInstalled, false))
        {
            checks.Add(new DoctorCheck(DoctorLevel.Info, L.T("IDE не найдена на этом компьютере.")));
            return new DoctorReport(integration.Id, integration.DisplayName, checks, null, lastCall);
        }

        var probes = integration is IntegrationBase b ? Safe(() => b.ProbeEntries(spec), []) : [];
        if (probes.Count == 0)
            checks.Add(new DoctorCheck(DoctorLevel.Info, L.T("Автоматическая проверка недоступна: подключение выполняется вручную в настройках IDE.")));

        foreach (var probe in probes)
        {
            ct.ThrowIfCancellationRequested();
            switch (probe.State)
            {
                case ProbeState.FileMissing:
                    checks.Add(new DoctorCheck(DoctorLevel.Error, L.F("Файл конфигурации не найден: {0}", probe.Path)));
                    continue;
                case ProbeState.Error:
                    checks.Add(new DoctorCheck(DoctorLevel.Error, probe.Error ?? L.F("Не удалось прочитать {0}", probe.Path)));
                    continue;
                case ProbeState.Absent:
                    checks.Add(new DoctorCheck(DoctorLevel.Error, L.F("Файл {0} прочитан, но записи «{1}» в нём нет.", probe.Path, spec.Name)));
                    continue;
                case ProbeState.Foreign:
                    checks.Add(new DoctorCheck(DoctorLevel.Warning,
                        L.F("В файле {0} запись «{1}» указывает на другую программу ({2}) — она не проверяется.", probe.Path, spec.Name, probe.Entry?.Command ?? "—")));
                    continue;
            }

            var entry = probe.Entry!;
            checks.Add(new DoctorCheck(DoctorLevel.Ok, L.F("Конфигурация прочитана, запись Offload найдена ({0}).", probe.Path)));
            if (probe.ToStatus(spec) == IntegrationStatus.Outdated)
            {
                checks.Add(new DoctorCheck(DoctorLevel.Warning, entry.Matches(spec)
                    ? L.T("Настройки записи по умолчанию устарели — нажмите «Обновить путь», чтобы обновить их.")
                    : L.F("Запись указывает не на эту копию Offload ({0}) — нажмите «Обновить путь».", spec.Command)));
            }

            var command = entry.Command ?? "";
            // До любого обращения к файлу: сетевой путь не проверяем и не запускаем (утечка учётных данных NTLM, чужой код).
            if (Editing.CommandPath.IsUnc(command))
            {
                checks.Add(new DoctorCheck(DoctorLevel.Warning,
                    L.F("Запись указывает на сетевой путь ({0}) — Offload не обращается к нему и не запускает его.", command)));
                continue;
            }
            if (!File.Exists(command))
            {
                checks.Add(new DoctorCheck(DoctorLevel.Error, L.F("Программа из записи не найдена: {0}", command)));
                continue;
            }
            if (ExeVersion(command) is { } version)
            {
                var same = string.Equals(version, AppInfo.Version, StringComparison.OrdinalIgnoreCase);
                checks.Add(new DoctorCheck(same ? DoctorLevel.Ok : DoctorLevel.Warning, same
                    ? L.F("Версия Offload в записи: {0}.", version)
                    : L.F("Версия Offload в записи: {0}, а запущена {1}.", version, AppInfo.Version)));
            }

            if (handshake is not null) continue; // одна проверка запуска на IDE (несколько файлов — одна команда)
            if (!MayLaunch(entry, spec, installedCopies))
            {
                checks.Add(new DoctorCheck(DoctorLevel.Warning,
                    L.F("Проверка запуска не выполнялась: команда или аргументы отличаются от Offload ({0} {1}). Запускается только {2} {3}.",
                        command, string.Join(' ', entry.Args), spec.Command, AppInfo.McpArg)));
                continue;
            }
            try
            {
                handshake = await McpHandshake.RunProcessAsync(command, entry.Args, HandshakeTimeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("Integrations", $"{integration.Id}: проверка запуска: {ex.Message}");
                handshake = new HandshakeResult(false, null, null, null, 0, TimeSpan.Zero, ex.Message, 0);
            }
            checks.AddRange(Describe(handshake));
        }

        foreach (var hint in KnownPitfalls(integration.Id)) checks.Add(new DoctorCheck(DoctorLevel.Info, hint));
        checks.Add(new DoctorCheck(DoctorLevel.Info, lastCall is { } lc
            ? L.F("Последнее обращение из этой IDE: {0:g}.", lc.ToLocalTime())
            : L.T("Обращений из этой IDE в журнале использования нет.")));
        return new DoctorReport(integration.Id, integration.DisplayName, checks, handshake, lastCall);
    }

    /// <summary>
    /// Можно ли запустить команду записи для проверки: не сетевой путь, аргументы ровно [«--mcp»], путь — текущий exe
    /// или одна из известных установленных копий. Только сравнение строк, без обращения к файловой системе.
    /// </summary>
    internal static bool MayLaunch(EntryInfo entry, McpServerSpec spec, IReadOnlyCollection<string>? installedCopies)
    {
        if (entry.Command is not { Length: > 0 } command || Editing.CommandPath.IsUnc(command)) return false;
        if (entry.Args.Count != 1 || !string.Equals(entry.Args[0], AppInfo.McpArg, StringComparison.Ordinal)) return false;
        if (!Editing.CommandPath.IsUnc(spec.Command) && Editing.CommandPath.Same(command, spec.Command)) return true;
        return installedCopies is not null &&
               installedCopies.Any(p => !Editing.CommandPath.IsUnc(p) && Editing.CommandPath.Same(command, p));
    }

    /// <summary>Строки отчёта по результату рукопожатия.</summary>
    internal static IEnumerable<DoctorCheck> Describe(HandshakeResult h)
    {
        if (!h.Ok)
        {
            yield return new DoctorCheck(DoctorLevel.Error, L.F("Запуск сервера MCP не удался: {0}", h.Error ?? "—"));
        }
        else
        {
            yield return new DoctorCheck(DoctorLevel.Ok,
                L.F("Сервер MCP отвечает: {0} {1}, инструментов: {2} ({3} мс).", h.ServerName ?? "?", h.ServerVersion ?? "", h.ToolCount, (long)h.Elapsed.TotalMilliseconds));
            if (h.ToolCount == 0)
                yield return new DoctorCheck(DoctorLevel.Error, L.T("Сервер не вернул ни одного инструмента."));
        }
        if (h.NonJsonLines > 0)
            yield return new DoctorCheck(DoctorLevel.Error,
                L.F("В stdout сервера есть строки не в формате JSON-RPC ({0}) — IDE может отключить сервер.", h.NonJsonLines));
    }

    /// <summary>Известные подводные камни клиента (показываются как подсказки).</summary>
    public static IReadOnlyList<string> KnownPitfalls(string integrationId) => integrationId switch
    {
        "vscode" or "vscode-insiders" =>
        [
            L.T("VS Code запускает сервер только после подтверждения доверия: откройте чат Copilot в режиме агента, выполните «MCP: List Servers» и запустите offload."),
            L.T("Инструменты доступны только в режиме агента (Agent), не в режиме Ask/Edit."),
        ],
        "gemini-cli" =>
        [
            L.T("Gemini CLI подключает MCP-серверы только в доверенных папках (trusted folders): проверьте /mcp в папке проекта."),
        ],
        "visual-studio" =>
        [
            L.T("В Visual Studio инструменты MCP по умолчанию выключены: включите их в окне выбора инструментов чата Copilot (нужна версия 17.14 или новее)."),
        ],
        "windsurf" =>
        [
            L.T("Devin Desktop также импортирует серверы из конфигурации Claude Code — если Offload появился дважды, отключите импорт (read_config_from в %APPDATA%\\devin\\config.json)."),
            L.T("После изменения конфигурации нажмите «Обновить» (Refresh) в панели MCP."),
        ],
        "continue" => [L.T("MCP-инструменты в Continue работают только в режиме Agent.")],
        "claude-desktop" => [L.T("Claude Desktop перечитывает конфигурацию только после полного выхода из программы (значок в трее → «Выход»).")],
        _ => [],
    };

    /// <summary>Время последнего обращения по идентификатору интеграции (по полю Client журнала использования).</summary>
    public static IReadOnlyDictionary<string, DateTime> LastCallsByIntegration(IEnumerable<UsageRecord> records)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (ClientNames.ToIntegrationId(r.Client) is not { } id) continue;
            if (!map.TryGetValue(id, out var t) || r.TimestampUtc > t) map[id] = r.TimestampUtc;
        }
        return map;
    }

    /// <summary>Последние обращения за 90 дней (чтение журнала — в фоне; ошибки журнала не мешают проверке).</summary>
    public static IReadOnlyDictionary<string, DateTime> ReadLastCalls()
    {
        try
        {
            return LastCallsByIntegration(UsageLog.ReadAll(DateTime.UtcNow.AddDays(-90)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug("Integrations", $"Журнал использования не прочитан: {ex.Message}");
            return new Dictionary<string, DateTime>();
        }
    }

    private static string? ExeVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var v = info.ProductVersion ?? info.FileVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            var plus = v.IndexOf('+', StringComparison.Ordinal);
            return (plus >= 0 ? v[..plus] : v).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static McpServerSpec SafeSpec()
    {
        try
        {
            return McpServerSpec.ForCurrentExecutable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new McpServerSpec(AppInfo.McpServerId, AppPaths.ExecutablePath, [AppInfo.McpArg], new Dictionary<string, string>());
        }
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.Debug("Integrations", $"Доктор интеграций: {ex.Message}");
            return fallback;
        }
    }
}

/// <summary>
/// Сопоставление имени клиента из MCP initialize (clientInfo.name, пишется в журнал использования) с идентификатором
/// интеграции. Имена у IDE не стандартизованы — сопоставление приблизительное, по фрагментам; порядок правил важен
/// (Cursor называет себя cursor-vscode, Roo Code — форк Cline).
/// </summary>
public static class ClientNames
{
    private static readonly (string Id, string[] Any, string[] All)[] Rules =
    [
        ("vscode-insiders", ["insiders"], []),
        ("claude-code", ["claude-code", "claude code"], []),
        ("claude-desktop", ["claude-ai", "claude desktop", "claude-desktop"], []),
        ("cursor", ["cursor"], []),
        ("copilot-cli", ["copilot"], ["cli"]),
        ("windsurf", ["windsurf", "devin", "codeium", "cascade"], []),
        ("roo-code", ["roo"], []),
        ("kilo-code", ["kilo"], []),
        ("cline", ["cline"], []),
        ("codex", ["codex"], []),
        ("gemini-cli", ["gemini"], []),
        ("zed", ["zed"], []),
        ("vscode", ["visual studio code", "vscode", "vs code"], []),
        ("visual-studio", ["visual studio", "visualstudio"], []),
        ("junie", ["junie"], []),
        ("jetbrains-ai", ["jetbrains", "intellij", "pycharm", "rider", "webstorm", "goland"], []),
        ("continue", ["continue"], []),
        ("kiro", ["kiro"], []),
        ("trae", ["trae"], []),
        ("qoder", ["qoder"], []),
    ];

    /// <summary>Идентификатор интеграции по clientInfo.name; null — неизвестный клиент (в том числе проверки самого Offload).</summary>
    public static string? ToIntegrationId(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName) || clientName == UsageLog.UnknownClient) return null;
        var n = clientName.Trim().ToLowerInvariant();
        if (n.StartsWith("offload", StringComparison.Ordinal)) return null;
        foreach (var (id, any, all) in Rules)
        {
            if (any.Any(a => n.Contains(a, StringComparison.Ordinal)) && all.All(a => n.Contains(a, StringComparison.Ordinal))) return id;
        }
        return null;
    }
}
