using Offload.Core;
using Offload.Core.Logging;
using Offload.Integrations.Claude;
using Offload.Integrations.Clients;

namespace Offload.Integrations;

/// <summary>Описание stdio MCP-сервера, которое прописывается в IDE.</summary>
public sealed record McpServerSpec(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env)
{
    /// <summary>
    /// При подключении сразу разрешить инструменты записи Offload без подтверждения (клиенты с <c>Approval</c>, см.
    /// <see cref="ToolApprovals"/>). false — запись получает только инструменты чтения, прежнее согласие не снимается.
    /// </summary>
    public bool ApproveWriteTools { get; init; }

    /// <summary>Offload.exe текущего процесса с аргументом --mcp.</summary>
    public static McpServerSpec ForCurrentExecutable()
    {
        var exe = AppPaths.ExecutablePath;
        // Запуск через «dotnet Offload.dll»: в конфиг IDE нужен сам Offload.exe, а не dotnet.exe.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var apphost = Path.Combine(AppContext.BaseDirectory, "Offload.exe");
            if (File.Exists(apphost)) exe = apphost;
        }
        return new McpServerSpec(
            AppInfo.McpServerId,
            Path.GetFullPath(exe),
            [AppInfo.McpArg],
            new Dictionary<string, string>());
    }
}

public enum IntegrationStatus
{
    /// <summary>IDE/агент не найден на компьютере.</summary>
    ClientNotFound,
    /// <summary>IDE найдена, Offload не подключён.</summary>
    NotRegistered,
    /// <summary>Подключён с актуальным путём к exe.</summary>
    Registered,
    /// <summary>Подключён, но путь/аргументы устарели (например, программа перемещена).</summary>
    Outdated,
    /// <summary>Не удалось прочитать конфигурацию.</summary>
    Error,
    /// <summary>
    /// Запись «offload» есть, но указывает на другую программу (например, собственную обёртку пользователя).
    /// Автоматически не трогается никогда; заменить её можно только явным «Подключить».
    /// </summary>
    Foreign,
}

public sealed record IntegrationResult(bool Ok, string Message, string? BackupPath = null);

/// <summary>
/// Что не так с записью Offload в конфиге IDE и можно ли это исправить без участия пользователя.
/// Автоматически исправляется только <see cref="PathMoved"/>; остальное — лишь по кнопке пользователя.
/// </summary>
public enum RepairNeed
{
    /// <summary>Всё в порядке, IDE не найдена или файл не читается (ошибка показывается статусом).</summary>
    None,
    /// <summary>Наша запись указывает на отсутствующий Offload.exe (программу переместили) — обновляется только путь.</summary>
    PathMoved,
    /// <summary>Записи нет: IDE сбросила конфиг или пользователь удалил её сам.</summary>
    Missing,
    /// <summary>Запись наша, путь верный, но значения по умолчанию (таймауты, списки автоодобрения) отличаются от текущих.</summary>
    StaleSettings,
    /// <summary>Запись указывает на другую существующую копию Offload (dev-сборка, портативная) или с другими аргументами.</summary>
    OtherCopy,
    /// <summary>Запись «offload» принадлежит другой программе — не трогаем никогда.</summary>
    Foreign,
}

/// <summary>IDE, запись которой Offload не исправил сам, и причина.</summary>
public sealed record RepairIssue(string Id, RepairNeed Need);

/// <summary>
/// Итог автовосстановления: обновлён путь; не удалось; пропущены из-за ограничения частоты; требуют решения пользователя
/// (запись удалена, чужая, устарели настройки, другая копия — автоматически не исправляются).
/// </summary>
public sealed record RepairReport(
    IReadOnlyList<string> Repaired,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<RepairIssue> NeedsAttention);

/// <summary>Одна IDE или агент, в которую можно прописать MCP-сервер.</summary>
public interface IIdeIntegration
{
    /// <summary>Стабильный идентификатор: claude-code, claude-desktop, cursor, vscode, windsurf, cline, roo-code, zed, gemini-cli, codex, continue, visual-studio…</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Путь к файлу конфигурации (для кнопки «Открыть файл»), если применимо.</summary>
    string? ConfigPath { get; }

    /// <summary>Подсказка пользователю на русском после подключения (например, «Перезапустите Cursor»).</summary>
    string? PostRegisterHint { get; }

    bool IsClientInstalled();

    IntegrationStatus GetStatus(McpServerSpec spec);

    /// <summary>Прописать (или обновить) сервер. Перед изменением файла делается резервная копия.</summary>
    Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default);

    Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default);
}

/// <summary>Все поддерживаемые IDE.</summary>
public static class IntegrationRegistry
{
    /// <summary>
    /// Стабильный порядок: claude-code, claude-desktop, cursor, vscode, vscode-insiders, copilot-cli, windsurf, cline,
    /// roo-code, kilo-code, codex, gemini-cli, zed, visual-studio, junie, jetbrains-ai, continue, kiro, trae, qoder.
    /// </summary>
    public static IReadOnlyList<IIdeIntegration> All => KnownIntegrations.All;

    public static IIdeIntegration? Find(string id) => All.FirstOrDefault(i => i.Id == id);

    /// <summary>
    /// Claude Code в каждом установленном дистрибутиве WSL (Id <c>claude-code-wsl:&lt;дистрибутив&gt;</c>). Не входят в
    /// <see cref="All"/>: перечисление запускает wsl.exe, а проверка статуса — сам дистрибутив, поэтому вызывать только
    /// по действию пользователя и не из потока интерфейса.
    /// </summary>
    public static IReadOnlyList<IIdeIntegration> WslIntegrations() =>
        ClientLocations.WslDistros().Select(d => (IIdeIntegration)WslClaudeCodeIntegration.Create(d)).ToList();

    /// <summary>
    /// Обновить пути во всех IDE, где наша запись указывает на отсутствующий Offload.exe (программу переместили;
    /// вызывается при старте трея). Меняется только путь к exe: записи, указывающие на другую существующую копию
    /// Offload, чужие записи, устаревшие умолчания и удалённые записи не трогаются. Возвращает список обновлённых IDE.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RefreshOutdatedAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        var updated = new List<string>();
        foreach (var integration in All)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (integration is not IntegrationBase b || !b.FileNeeds(spec).Contains(RepairNeed.PathMoved)) continue;
                var r = await b.RepairPathAsync(spec, ct);
                if (r is null) continue;
                if (r.Ok) updated.Add(integration.Id);
                else Log.Warn("Integrations", $"{integration.Id}: не удалось обновить путь: {r.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Integrations", $"{integration.Id}: ошибка обновления", ex);
            }
        }
        if (updated.Count > 0) Log.Info("Integrations", "Обновлён путь к Offload: " + string.Join(", ", updated));
        return updated;
    }

    /// <summary>
    /// Отключить Offload от всех IDE (при удалении программы). Кроме подключённых (Registered/Outdated),
    /// проверяются и устаревшие расположения (старый путь Cline, второй файл Kilo, конфиг удалённой IDE):
    /// UnregisterAsync удаляет только записи, указывающие на Offload, чужие и неразборчивые файлы не трогает.
    /// </summary>
    public static async Task UnregisterAllAsync(CancellationToken ct = default)
    {
        var spec = McpServerSpec.ForCurrentExecutable();
        IReadOnlyList<IIdeIntegration> wsl;
        try
        {
            wsl = WslIntegrations();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("Integrations", $"Дистрибутивы WSL не перечислены: {ex.Message}");
            wsl = [];
        }
        foreach (var integration in All.Concat(wsl))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (integration is JetBrainsAiIntegration) continue;
                var status = integration.GetStatus(spec);
                if (status == IntegrationStatus.Error) continue;
                if (status is not (IntegrationStatus.Registered or IntegrationStatus.Outdated) && !HasStaleEntry(integration)) continue;
                var r = await integration.UnregisterAsync(ct);
                Log.Write(r.Ok ? LogLevel.Info : LogLevel.Warn, "Integrations", $"{integration.Id}: {r.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Integrations", $"{integration.Id}: ошибка отключения", ex);
            }
        }
    }

    /// <summary>Файлы конфигурации клиента, изменение которых может снять нашу запись (для наблюдения за ними).</summary>
    public static IReadOnlyList<string> ConfigFiles(IIdeIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(integration);
        try
        {
            return integration is IntegrationBase b ? b.WatchedFiles() : integration.ConfigPath is { Length: > 0 } p ? [p] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Debug("Integrations", $"{integration.Id}: файлы конфигурации: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Что с записью Offload в IDE (для страницы «Интеграции» и уведомлений). Несколько файлов → самая серьёзная причина:
    /// чужая запись, другая копия, удалённая запись, устаревшие настройки, перемещённый exe.
    /// </summary>
    public static RepairNeed Assess(IIdeIntegration integration, McpServerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(integration);
        return integration is IntegrationBase b ? IntegrationBase.Worst(b.FileNeeds(spec)) : RepairNeed.None;
    }

    /// <summary>
    /// Автовосстановление для перечисленных IDE, где Offload был подключён. Автоматически исправляется только одно:
    /// наша запись указывает на отсутствующий Offload.exe (<see cref="RepairNeed.PathMoved"/>) — в ней меняется лишь путь,
    /// остальные ключи не трогаются. Удалённые записи (IDE сбросила конфиг или пользователь убрал их сам), чужие записи
    /// «offload», устаревшие умолчания, укороченные пользователем списки инструментов и записи на другую копию Offload
    /// не исправляются — они возвращаются в <see cref="RepairReport.NeedsAttention"/>, чтобы показать уведомление.
    /// Решение «можно ли чинить сейчас» — <paramref name="allow"/> (ограничение частоты).
    /// </summary>
    public static async Task<RepairReport> RepairAsync(
        IEnumerable<string> ids, McpServerSpec spec, Func<string, bool>? allow = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var repaired = new List<string>();
        var failed = new List<string>();
        var skipped = new List<string>();
        var attention = new List<RepairIssue>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (Find(id) is not IntegrationBase integration) continue;
            try
            {
                var needs = integration.FileNeeds(spec);
                if (needs.Contains(RepairNeed.PathMoved))
                {
                    if (allow is not null && !allow(id))
                    {
                        skipped.Add(id);
                    }
                    else if (await integration.RepairPathAsync(spec, ct) is { } r)
                    {
                        if (r.Ok)
                        {
                            repaired.Add(id);
                            Log.Info("Integrations", $"{id}: путь к Offload обновлён: {r.Message}");
                        }
                        else
                        {
                            failed.Add(id);
                            Log.Warn("Integrations", $"{id}: не удалось обновить путь к Offload: {r.Message}");
                        }
                        needs = integration.FileNeeds(spec);
                    }
                }
                // Пропуск по лимиту и неудача уже учтены (Skipped/Failed); остальное — на решение пользователя.
                var pathHandled = skipped.Contains(id) || failed.Contains(id);
                var rest = IntegrationBase.Worst(pathHandled ? needs.Where(n => n != RepairNeed.PathMoved) : needs);
                if (rest != RepairNeed.None) attention.Add(new RepairIssue(id, rest));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(id);
                Log.Error("Integrations", $"{id}: ошибка восстановления подключения", ex);
            }
        }
        return new RepairReport(repaired, failed, skipped, attention);
    }

    /// <summary>Наша запись в каком-либо из известных файлов клиента (в т.ч. устаревших расположений).</summary>
    private static bool HasStaleEntry(IIdeIntegration integration) =>
        integration is JsonIntegration json && json.HasOurEntryAnywhere();
}

/// <summary>
/// Дополнительная настройка Claude Code: инструкции по делегированию (субагент/навык/CLAUDE.md)
/// и разрешения на вызов инструментов без подтверждения.
/// </summary>
public static class ClaudeCodeExtras
{
    /// <summary>
    /// Один наш файл в ~/.claude (с меткой x-offload: managed). Все такие файлы перечислены в <see cref="ManagedFiles"/>:
    /// по этому списку <see cref="RemoveAll"/> снимает их при удалении программы, поэтому новое дополнение-файл
    /// заводится только здесь — забыть его при удалении нельзя.
    /// </summary>
    /// <param name="ShippedHashes">
    /// Хэши (<see cref="Editing.ManagedContent.Hash"/>) текстов, которые прежние версии Offload записывали без хэша в метке:
    /// только файл, совпадающий с одним из них, считается «устаревшим нашим» и обновляется; прочие — изменёнными пользователем.
    /// </param>
    internal sealed record ManagedFile(
        Func<string> Path, Func<string> Content, Func<string> What, bool DeleteEmptyDir, IReadOnlyCollection<string> ShippedHashes)
    {
        public bool IsInstalled() => ClaudeExtrasImpl.IsManagedFile(Path());

        public ManagedState State() => ClaudeExtrasImpl.StateOf(Path(), Content(), ShippedHashes);

        public IntegrationResult Install() => ClaudeExtrasImpl.InstallManaged(Path(), Content(), What());

        public IntegrationResult Remove() => ClaudeExtrasImpl.RemoveManaged(Path(), What(), DeleteEmptyDir);
    }

    private static readonly ManagedFile Skill =
        new(() => ClaudeExtrasImpl.SkillFile, ClaudeTexts.Skill, () => L.T("навык /offload"), DeleteEmptyDir: true, ClaudeTexts.SkillShippedHashes);

    private static readonly ManagedFile StrongRule =
        new(() => ClaudeExtrasImpl.RuleFile, ClaudeTexts.StrongRule, () => L.T("правило «строгого режима»"), DeleteEmptyDir: false,
            ClaudeTexts.StrongRuleShippedHashes);

    private static readonly ManagedFile RunnerAgent =
        new(() => ClaudeExtrasImpl.AgentFile, ClaudeTexts.RunnerAgent, () => L.T("субагент offload-runner"), DeleteEmptyDir: false,
            ClaudeTexts.RunnerAgentShippedHashes);

    /// <summary>Все файлы-дополнения Offload в ~/.claude.</summary>
    internal static IReadOnlyList<ManagedFile> ManagedFiles { get; } = [Skill, StrongRule, RunnerAgent];

    /// <summary>
    /// Снять все дополнения Claude Code (при удалении программы): каждый наш файл из <see cref="ManagedFiles"/>
    /// и наши разрешения инструментов. Файлы без метки Offload не трогаются. Результат — по одному на дополнение;
    /// ошибка одного не мешает снять остальные.
    /// </summary>
    public static IReadOnlyList<IntegrationResult> RemoveAll()
    {
        var results = new List<IntegrationResult>();
        foreach (var file in ManagedFiles) results.Add(Guarded(file.Remove));
        results.Add(Guarded(RevokeToolApprovals));
        return results;
    }

    /// <summary>
    /// Обновить дополнения после смены версии Offload: файлы в состоянии <see cref="ManagedState.Outdated"/> переписываются
    /// (изменённые пользователем — <see cref="ManagedState.Modified"/> — не трогаются). С <paramref name="includeApprovals"/>
    /// в разрешения добавляются новые инструменты только для чтения (см. <see cref="ClaudeExtrasImpl.RefreshApprovals"/>;
    /// инструменты записи — только по явному согласию на странице «Интеграции»). Возвращает только то, что изменилось (или не удалось).
    /// </summary>
    public static IReadOnlyList<IntegrationResult> RefreshManaged(bool includeApprovals = true)
    {
        var results = new List<IntegrationResult>();
        foreach (var file in ManagedFiles)
        {
            try
            {
                if (file.State() == ManagedState.Outdated) results.Add(file.Install());
            }
            catch (Exception ex)
            {
                results.Add(new IntegrationResult(false, L.F("Не удалось обновить дополнение Claude Code: {0}", ex.Message)));
            }
        }
        if (!includeApprovals) return results;
        var approvals = Guarded(() => ClaudeExtrasImpl.RefreshApprovals() ?? new IntegrationResult(true, ""));
        if (approvals.Message.Length > 0) results.Add(approvals);
        return results;
    }

    /// <summary>Файлы-дополнения, изменённые пользователем вручную (автоматически не обновляются): их названия.</summary>
    public static IReadOnlyList<string> ModifiedFiles() =>
        ManagedFiles.Where(f => f.State() == ManagedState.Modified).Select(f => f.What()).ToList();

    private static IntegrationResult Guarded(Func<IntegrationResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            return new IntegrationResult(false, L.F("Не удалось снять дополнение Claude Code: {0}", ex.Message));
        }
    }

    /// <summary>Установлены ли инструкции по делегированию для Claude Code.</summary>
    public static bool IsGuidanceInstalled() => Skill.IsInstalled();

    /// <summary>Установить инструкции (файлы в ~/.claude), объясняющие Claude, когда делегировать задачи Offload.</summary>
    public static IntegrationResult InstallGuidance() => Skill.Install();

    public static IntegrationResult RemoveGuidance() => Skill.Remove();

    /// <summary>Разрешены ли инструменты Offload без подтверждения (~/.claude/settings.json permissions.allow).</summary>
    public static bool AreToolsPreapproved() => ClaudeExtrasImpl.AllPresent(McpToolNames.ReadOnly);

    /// <summary>Разрешены ли без подтверждения и инструменты записи (все имена из McpToolNames.Writing).</summary>
    public static bool AreWriteToolsPreapproved() => ClaudeExtrasImpl.AllPresent(McpToolNames.Writing);

    /// <param name="includeWriteTools">Также разрешить инструменты, изменяющие файлы (false — убрать их, если были).</param>
    public static IntegrationResult PreapproveTools(bool includeWriteTools) => ClaudeExtrasImpl.Preapprove(includeWriteTools);

    public static IntegrationResult RevokeToolApprovals() => ClaudeExtrasImpl.Revoke();

    /// <summary>Разрешить без подтверждения недостающие инструменты чтения, не трогая уже выданные разрешения на запись.</summary>
    public static IntegrationResult AllowReadTools() => ClaudeExtrasImpl.AllowReadTools();

    /// <summary>«Строгий режим»: правило ~/.claude/rules/offload.md — Claude активнее делегирует (загружается в каждой сессии).</summary>
    public static bool IsStrongRuleInstalled() => StrongRule.IsInstalled();

    public static IntegrationResult InstallStrongRule() => StrongRule.Install();

    public static IntegrationResult RemoveStrongRule() => StrongRule.Remove();

    /// <summary>Субагент ~/.claude/agents/offload-runner.md (Haiku) для пакетных механических задач через локальную модель.</summary>
    public static bool IsRunnerAgentInstalled() => RunnerAgent.IsInstalled();

    public static IntegrationResult InstallRunnerAgent() => RunnerAgent.Install();

    public static IntegrationResult RemoveRunnerAgent() => RunnerAgent.Remove();
}
