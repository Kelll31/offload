using Offload.App.Controls;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Integrations;
using Offload.Integrations.Clients;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «Интеграции»: подключение Offload как MCP-сервера к IDE и настройка Claude Code.</summary>
internal sealed class IntegrationsPage : PageBase
{
    private const string ClaudeCodeId = "claude-code";

    /// <param name="Integration">IDE.</param>
    /// <param name="Status">Статус записи.</param>
    /// <param name="Installed">IDE найдена (или в ней есть наша запись).</param>
    /// <param name="LastCallUtc">Последнее обращение из IDE.</param>
    /// <param name="Need">Что не так с записью (для текста статуса и кнопки).</param>
    /// <param name="Tracked">Offload подключал эту IDE (она в cfg.Integrations): пропавшая запись — «удалена», а не «не подключена».</param>
    private sealed record Row(IIdeIntegration Integration, IntegrationStatus Status, bool Installed, DateTime? LastCallUtc, RepairNeed Need, bool Tracked)
    {
        /// <summary>Запись Offload пропала из настроек IDE, которую Offload подключал.</summary>
        public bool Removed => Tracked && Status == IntegrationStatus.NotRegistered;
    }

    private readonly ListView _list = Kit.List(
        (L.T("IDE / агент"), 26), (L.T("Статус"), 16), (L.T("Последнее обращение"), 16), (L.T("Файл конфигурации"), 42));
    private readonly Button _register;
    private readonly Button _unregister;
    private readonly Button _registerAll;
    private readonly Button _check;
    private readonly Button _checkAll;
    private readonly Button _openConfig;
    private readonly Button _refresh;
    private readonly Label _status = Kit.Wrap("");
    private readonly TextBox _report = Kit.MultiLine(170, mono: true, readOnly: true);
    private readonly CheckBox _autoRepair = Kit.Check(L.T("Следить за подключениями к IDE"));
    private readonly Label _wslHint = Kit.Hint("");
    private readonly Button _wslDetect;

    private readonly Label _claudeNote = Kit.Hint("");
    private readonly Label _claudeModified = Kit.Wrap("", Theme.Regular(8.5f), Theme.WarnText);
    private readonly List<(GuidanceFile File, CheckBox Box, Label Note)> _clientGuidance = [];
    private readonly CheckBox _guidance = Kit.Check(L.T("Инструкции по делегированию для Claude Code"));
    private readonly CheckBox _strongRule = Kit.Check(L.T("Строгий режим (правило для Claude)"));
    private readonly CheckBox _runnerAgent = Kit.Check(L.T("Субагент offload-runner"));
    private readonly CheckBox _approveRead = Kit.Check(L.T("Разрешить инструменты чтения без подтверждения"));
    private readonly CheckBox _approveWrite = Kit.Check(L.T("Разрешить инструменты записи Offload без подтверждения"));
    private readonly Label _pendingWrite = Kit.Wrap("", Theme.Regular(8.5f), Theme.WarnText);

    private readonly TextBox _command = Kit.TextBox(readOnly: true);
    private readonly TextBox _json = Kit.MultiLine(118, mono: true, readOnly: true);

    private bool _loadingChecks;
    private bool _loadingList;
    private bool _loaded;

    public IntegrationsPage(IAppShell shell) : base(shell)
    {
        _register = Kit.Primary(L.T("Подключить"), async (_, _) => await RegisterSelectedAsync());
        _unregister = Kit.Button(L.T("Отключить"), async (_, _) => await UnregisterSelectedAsync());
        _registerAll = Kit.Button(L.T("Подключить все найденные"), async (_, _) => await RegisterAllAsync(), 170);
        _check = Kit.Button(L.T("Проверить"), async (_, _) =>
        {
            if (Selected() is { } row) await CheckAsync([row.Integration]);
        });
        _checkAll = Kit.Button(L.T("Проверить все"), async (_, _) => await CheckAsync(ConnectedIntegrations()), 120);
        _openConfig = Kit.Button(L.T("Открыть файл конфигурации"), (_, _) => OpenConfig(), 170);
        _refresh = Kit.Button(L.T("Обновить список"), async (_, _) => await RefreshAsync());
        _wslDetect = Kit.Button(L.T("Найти в WSL"), async (_, _) => await DetectWslAsync(), 120);
        _list.SelectedIndexChanged += (_, _) => UpdateUiState();
        _list.DoubleClick += async (_, _) =>
        {
            if (Selected() is { Installed: true, Status: not IntegrationStatus.Registered }) await RegisterSelectedAsync();
        };

        var root = Kit.Table();
        root.AddRow(Kit.Section(L.T("IDE и агенты"), first: true));
        root.AddRow(Kit.Hint(
            L.T("Offload подключается к IDE как MCP-сервер: IDE сама запускает «Offload.exe --mcp» и может поручать локальной модели чтение файлов, ревью изменений, сообщения коммитов и простые правки. Перед изменением файла настроек IDE делается резервная копия.")));
        root.AddFixedRow(230, _list);
        root.AddRow(Kit.Flow(_register, _unregister, _registerAll, _check, _checkAll, _openConfig, _refresh));
        root.AddRow(_status);
        // Остановленные дистрибутивы WSL: Offload не запускает их сам (это поднимает виртуальную машину) — только по кнопке.
        _wslHint.Visible = _wslDetect.Visible = false;
        root.AddRow(_wslHint);
        root.AddRow(Kit.Flow(_wslDetect));
        _report.Visible = false;
        root.AddRow(_report);
        root.AddRow(_autoRepair);
        root.AddRow(Indented(Kit.Hint(DevMode.Active
            ? L.T("Если программу переместили, путь к Offload в настройках подключённых IDE обновляется сам. Если запись удалена, изменена или имя «offload» занято другим сервером, Offload ничего не меняет — только сообщает, а восстановить подключение можно здесь. В режиме разработчика не действует.")
            : L.T("Если программу переместили, путь к Offload в настройках подключённых IDE обновляется сам. Если запись удалена, изменена или имя «offload» занято другим сервером, Offload ничего не меняет — только сообщает, а восстановить подключение можно здесь."))));
        root.AddRow(_approveWrite);
        _pendingWrite.Margin = new Padding(20, 0, 0, 6);
        root.AddRow(_pendingWrite);
        root.AddRow(Indented(Kit.Hint(L.F(
            "Действует в {0} — там, где Offload подключён. Инструменты записи создают и правят файлы проекта; каждая правка сохраняется и может быть отменена (local_job). Новые инструменты записи после обновления Offload сами не разрешаются — кроме Gemini CLI: там разрешение (trust) даётся всему серверу сразу.",
            string.Join(", ", Ui.Try(() => ToolApprovals.SupportedNames, [], "ToolApprovals.SupportedNames"))))));

        root.AddRow(Kit.Section("Claude Code"));
        root.AddRow(_claudeNote);
        root.AddRow(_claudeModified);
        root.AddRow(_guidance);
        root.AddRow(Indented(Kit.Hint(L.T("Файлы в ~/.claude объясняют Claude, какие задачи выгодно поручать локальной модели."))));
        root.AddRow(_strongRule);
        root.AddRow(Indented(Kit.Hint(L.T("Правило ~/.claude/rules/offload.md загружается в каждой сессии — Claude делегирует активнее."))));
        root.AddRow(_runnerAgent);
        root.AddRow(Indented(Kit.Hint(L.T("Субагент ~/.claude/agents/offload-runner.md выполняет пакеты механических задач через локальную модель."))));
        root.AddRow(_approveRead);
        root.AddRow(Indented(Kit.Hint(L.T("Инструменты записи для Claude Code включаются общим пунктом в разделе «IDE и агенты»."))));
        root.AddRow(Kit.Hint(L.T("После изменений перезапустите Claude Code.")));

        root.AddRow(Kit.Section(L.T("Инструкции для других клиентов")));
        root.AddRow(Kit.Hint(L.T("Секция в глобальном файле инструкций клиента объясняет модели, когда поручать задачи Offload. Ваш текст в файле не меняется; секцию, которую вы отредактировали, Offload больше не обновляет.")));
        foreach (var g in ClientGuidance.All)
        {
            var box = Kit.Check(L.F("{0}: инструкции по делегированию", g.DisplayName));
            var note = Indented(Kit.Hint(""));
            var file = g;
            box.CheckedChanged += (_, _) => ToggleGuidance(file, box);
            _clientGuidance.Add((g, box, note));
            root.AddRow(box);
            root.AddRow(note);
        }

        root.AddRow(Kit.Section(L.T("Другие клиенты (вручную)")));
        root.AddRow(Kit.Hint(L.T("Если вашей IDE нет в списке, добавьте в её настройки MCP-сервер со следующей командой (транспорт stdio):")));
        var cmdRow = Kit.Table(100, 0);
        cmdRow.AddRow(_command, Kit.Button(L.T("Копировать"), (_, _) => Copy(_command.Text)));
        root.AddRow(cmdRow);
        var jsonRow = Kit.Table(100, 0);
        var copyJson = Kit.Button(L.T("Копировать"), (_, _) => Copy(_json.Text));
        copyJson.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        jsonRow.AddRow(_json, copyJson);
        root.AddRow(jsonRow);

        Controls.Add(Kit.Scroll(root));

        _guidance.CheckedChanged += (_, _) => ToggleExtra(_guidance,
            ClaudeCodeExtras.InstallGuidance, ClaudeCodeExtras.RemoveGuidance, L.T("инструкции по делегированию"));
        _strongRule.CheckedChanged += (_, _) => ToggleExtra(_strongRule,
            ClaudeCodeExtras.InstallStrongRule, ClaudeCodeExtras.RemoveStrongRule, L.T("строгий режим"));
        _runnerAgent.CheckedChanged += (_, _) => ToggleExtra(_runnerAgent,
            ClaudeCodeExtras.InstallRunnerAgent, ClaudeCodeExtras.RemoveRunnerAgent, L.T("субагент offload-runner"));
        _approveRead.CheckedChanged += (_, _) => ToggleClaudeReadApprovals();
        _approveWrite.CheckedChanged += async (_, _) => await ToggleWriteApprovalsAsync();
        _autoRepair.CheckedChanged += (_, _) => ToggleAutoRepair();

        FillManual();
        UpdateUiState();
    }

    public override string Key => Tabs.Integrations;

    public override string Title => L.T("Интеграции");

    public override string Subtitle => L.T("Подключение Offload к IDE и агентам как MCP-сервера");

    public override string Glyph => Glyphs.Integrations;

    public override string? BusyDescription => IsBusy ? L.T("подключение к IDE") : null;

    private static Label Indented(Label hint)
    {
        hint.Margin = new Padding(20, 0, 0, 6);
        return hint;
    }

    protected override async void OnActivated()
    {
        try
        {
            if (!_loaded) _status.Text = L.T("Поиск установленных IDE…");
            LoadClaudeChecks();
            await RefreshAsync();
            _loaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Вкладка «Интеграции»", ex);
        }
    }

    private static McpServerSpec Spec() =>
        Ui.Try(McpServerSpec.ForCurrentExecutable,
            new McpServerSpec(AppInfo.McpServerId, AppPaths.ExecutablePath, [AppInfo.McpArg], new Dictionary<string, string>()),
            "McpServerSpec");

    private Row? Selected() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Row : null;

    protected override void UpdateUiState()
    {
        var row = Selected();
        var busy = IsBusy || _loadingList;
        _register.Enabled = !busy && row is { Installed: true, Status: not IntegrationStatus.Registered };
        _register.Text = row switch
        {
            { Status: IntegrationStatus.Outdated } => L.T("Обновить путь"),
            { Status: IntegrationStatus.Foreign } => L.T("Заменить…"),
            { Removed: true } => L.T("Восстановить"),
            _ => L.T("Подключить"),
        };
        // «Отключить» для удалённой/чужой записи только перестаёт отслеживать IDE (файл не меняется).
        _unregister.Enabled = !busy && row is { Status: IntegrationStatus.Registered or IntegrationStatus.Outdated or IntegrationStatus.Error }
            or { Tracked: true, Status: IntegrationStatus.NotRegistered or IntegrationStatus.Foreign };
        _registerAll.Enabled = !busy && _list.Items.Count > 0;
        _check.Enabled = !busy && row is { Installed: true };
        _checkAll.Enabled = !busy && ConnectedIntegrations().Count > 0;
        _openConfig.Enabled = row?.Integration.ConfigPath is { Length: > 0 };
        _refresh.Enabled = !busy;
    }

    private async Task RefreshAsync()
    {
        if (_loadingList) return;
        _loadingList = true;
        UpdateUiState();
        try
        {
            var spec = Spec();
            var tracked = ConfigStore.Current.Integrations.ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<string> stoppedWsl = [];
            var (rows, error) = await Task.Run<(List<Row>, string?)>(() =>
            {
                try
                {
                    stoppedWsl = Ui.Try<IReadOnlyList<string>>(WslProbe.Stopped, [], "WslProbe.Stopped");
                    var lastCalls = Ui.Try(IntegrationDoctor.ReadLastCalls, new Dictionary<string, DateTime>(), "ReadLastCalls");
                    // Claude Code в дистрибутивах WSL — отдельные строки (перечисление запускает wsl.exe, поэтому только здесь, в фоне).
                    var wsl = Ui.Try<IReadOnlyList<IIdeIntegration>>(IntegrationRegistry.WslIntegrations, [], "WslIntegrations");
                    var list = IntegrationRegistry.All.Concat(wsl).Select(i =>
                    {
                        var installed = Ui.Try(i.IsClientInstalled, false, $"{i.Id}.IsClientInstalled");
                        var status = Ui.Try(() => i.GetStatus(spec), installed ? IntegrationStatus.Error : IntegrationStatus.ClientNotFound, $"{i.Id}.GetStatus");
                        if (!installed && status is IntegrationStatus.NotRegistered) status = IntegrationStatus.ClientNotFound;
                        var need = installed ? Ui.Try(() => IntegrationRegistry.Assess(i, spec), RepairNeed.None, $"{i.Id}.Assess") : RepairNeed.None;
                        return new Row(i, status, installed || status is IntegrationStatus.Registered or IntegrationStatus.Outdated,
                            lastCalls.TryGetValue(i.Id, out var t) ? t : null, need, tracked.Contains(i.Id));
                    }).ToList();
                    return (list, null);
                }
                catch (Exception ex)
                {
                    Log.Warn("ui", $"Список IDE недоступен: {ex.Message}");
                    return (new List<Row>(), Ui.FriendlyError(ex));
                }
            });
            if (IsDisposed) return;

            var selectedId = Selected()?.Integration.Id;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var r in rows.OrderBy(r => r.Status == IntegrationStatus.ClientNotFound).ThenBy(r => Ui.Try(() => r.Integration.DisplayName, r.Integration.Id, "DisplayName")))
            {
                var item = new ListViewItem([
                    Ui.Try(() => r.Integration.DisplayName, r.Integration.Id, "DisplayName"),
                    StatusText(r),
                    r.LastCallUtc is { } last ? LastCallText(last) : "—",
                    Ui.Try(() => r.Integration.ConfigPath, null, "ConfigPath") ?? "—",
                ]) { Tag = r, UseItemStyleForSubItems = false };
                item.SubItems[1].ForeColor = r.Removed || r.Status == IntegrationStatus.Foreign ? Theme.WarnText : Texts.IntegrationColor(r.Status);
                item.SubItems[2].ForeColor = Theme.TextMuted;
                if (r.Status == IntegrationStatus.ClientNotFound)
                {
                    item.ForeColor = Theme.Gray;
                    item.SubItems[2].ForeColor = Theme.Gray;
                    item.SubItems[3].ForeColor = Theme.Gray;
                }
                _list.Items.Add(item);
                if (r.Integration.Id == selectedId) item.Selected = true;
            }
            _list.EndUpdate();
            _wslHint.Visible = _wslDetect.Visible = stoppedWsl.Count > 0;
            if (stoppedWsl.Count > 0)
                _wslHint.Text = L.F("Дистрибутивы WSL не запущены: {0}. Offload не запускает их сам — нажмите «Найти в WSL», чтобы запустить их и проверить Claude Code внутри.", string.Join(", ", stoppedWsl));
            if (error is not null)
            {
                _status.ForeColor = Theme.ErrorText;
                _status.Text = L.F("Не удалось получить список IDE: {0}", error);
            }
            else if (_status.Text == L.T("Поиск установленных IDE…"))
            {
                var found = rows.Count(r => r.Installed);
                _status.ForeColor = Theme.TextMuted;
                _status.Text = L.F("Найдено IDE и агентов: {0} из {1}.", found, rows.Count);
            }
        }
        finally
        {
            _loadingList = false;
            UpdateUiState();
        }
    }

    private static bool IsWsl(IIdeIntegration i) => i.Id.StartsWith("claude-code-wsl:", StringComparison.Ordinal);

    /// <summary>Запустить остановленные дистрибутивы WSL (действие пользователя) и обновить список.</summary>
    private async Task DetectWslAsync()
    {
        await RunBusyAsync(async () => await Task.Run(WslProbe.Detect), L.T("Не удалось проверить дистрибутивы WSL"));
        await RefreshAsync();
    }

    /// <summary>Статус в списке: уточняет «не подключена» и «требует обновления» причиной (см. <see cref="RepairNeed"/>).</summary>
    private static string StatusText(Row r) => r switch
    {
        { Status: IntegrationStatus.Foreign } => L.T("Другой сервер с именем offload"),
        { Removed: true } => L.T("Запись удалена из настроек IDE"),
        { Status: IntegrationStatus.Outdated, Need: RepairNeed.StaleSettings } => L.T("Устарели настройки записи"),
        { Status: IntegrationStatus.Outdated, Need: RepairNeed.OtherCopy } => L.T("Указывает на другую копию Offload"),
        { Status: IntegrationStatus.Outdated, Need: RepairNeed.Missing } => L.T("Подключена не во всех файлах"),
        _ => Texts.Integration(r.Status),
    };

    private async Task<IntegrationResult?> RegisterAsync(IIdeIntegration integration)
    {
        // Согласие на инструменты записи (общий пункт) сразу переносится в новую запись.
        var spec = Spec() with { ApproveWriteTools = _approveWrite.CheckState == CheckState.Checked };
        IntegrationResult result;
        IntegrationWatcher.PauseFor(TimeSpan.FromMinutes(2));
        try
        {
            result = await integration.RegisterAsync(spec);
        }
        finally
        {
            IntegrationWatcher.PauseFor(IntegrationWatcher.Debounce);
        }
        Log.Info("integrations", $"{integration.Id}: {(result.Ok ? "подключено" : "ошибка")} — {result.Message}");
        if (result.Ok)
        {
            ConfigStore.Update(c =>
            {
                if (!c.Integrations.Contains(integration.Id)) c.Integrations.Add(integration.Id);
            });
        }
        return result;
    }

    private async Task RegisterSelectedAsync()
    {
        if (Selected() is not { } row) return;
        var i = row.Integration;
        if (row.Status == IntegrationStatus.Foreign &&
            !Ui.Confirm(Owner,
                L.F("В настройках «{0}» имя «offload» уже занято другим сервером (не Offload). Заменить его записью Offload? Резервная копия файла будет сохранена.", i.DisplayName),
                warning: true))
            return;
        await RunBusyAsync(async () =>
        {
            using var wslStart = IsWsl(i) ? WslProbe.AllowStart() : null;
            var r = await RegisterAsync(i);
            if (r is null) return;
            ShowResult(i, r, registered: true);
        }, L.F("Не удалось подключить {0}", i.DisplayName));
        Shell.ConfigChanged();
        await RefreshAsync();
        LoadClaudeChecks(); // в т.ч. состояние общего пункта разрешений: у новой записи оно своё
    }

    private async Task UnregisterSelectedAsync()
    {
        if (Selected() is not { } row) return;
        var i = row.Integration;
        if (row is { Tracked: true, Status: IntegrationStatus.NotRegistered or IntegrationStatus.Foreign })
        {
            // Нашей записи в файле нет — только перестаём следить за IDE и напоминать о ней.
            if (!Ui.Confirm(Owner, L.F("Записи Offload в настройках «{0}» нет. Больше не следить за этой IDE?", i.DisplayName))) return;
            if (!Ui.RunSafe(Owner, () => ConfigStore.Update(c => c.Integrations.Remove(i.Id)), L.T("Не удалось сохранить настройку"))) return;
            Log.Info("integrations", $"{i.Id}: больше не отслеживается (записи Offload нет)");
            _status.ForeColor = Theme.OkText;
            _status.Text = L.F("{0}: Offload больше не следит за этой IDE. Файл настроек не изменялся.", i.DisplayName);
            Shell.ConfigChanged();
            await RefreshAsync();
            return;
        }
        if (!Ui.Confirm(Owner, L.F("Отключить Offload от «{0}»?", i.DisplayName))) return;
        await RunBusyAsync(async () =>
        {
            // Пока идёт отключение, автовосстановление не должно вернуть запись.
            IntegrationWatcher.PauseFor(TimeSpan.FromMinutes(2));
            using var wslStart = IsWsl(i) ? WslProbe.AllowStart() : null;
            try
            {
                var r = await i.UnregisterAsync();
                Log.Info("integrations", $"{i.Id}: отключение — {r.Message}");
                if (r.Ok) ConfigStore.Update(c => c.Integrations.Remove(i.Id));
                ShowResult(i, r, registered: false);
            }
            finally
            {
                IntegrationWatcher.PauseFor(IntegrationWatcher.Debounce);
            }
        }, L.F("Не удалось отключить {0}", i.DisplayName));
        Shell.ConfigChanged();
        await RefreshAsync();
        LoadClaudeChecks();
    }

    private async Task RegisterAllAsync()
    {
        var rows = _list.Items.Cast<ListViewItem>().Select(x => (Row)x.Tag!).ToList();
        // Чужой сервер «offload» заменяется только по одному, с подтверждением («Заменить…»).
        var foreign = rows.Where(r => r.Status == IntegrationStatus.Foreign).ToList();
        var targets = rows.Where(r => r.Installed && r.Status is not (IntegrationStatus.Registered or IntegrationStatus.Foreign)).ToList();
        if (targets.Count == 0)
        {
            Ui.Info(Owner, foreign.Count > 0
                ? L.F("Остальные найденные IDE уже подключены. Пропущены (имя «offload» занято другим сервером): {0}.", string.Join(", ", foreign.Select(r => r.Integration.DisplayName)))
                : L.T("Все найденные IDE уже подключены."));
            return;
        }
        var lines = new List<string>();
        foreach (var f in foreign)
            lines.Add(L.F("— {0}: пропущена — имя «offload» занято другим сервером (заменить — кнопкой «Заменить…»).", f.Integration.DisplayName));
        var hints = new List<string>();
        var failed = 0;
        await RunBusyAsync(async () =>
        {
            foreach (var t in targets)
            {
                _status.ForeColor = Theme.TextMuted;
                _status.Text = L.F("Подключение: {0}…", t.Integration.DisplayName);
                try
                {
                    var r = await RegisterAsync(t.Integration);
                    if (r is null) continue;
                    lines.Add($"{(r.Ok ? "✓" : "✗")} {t.Integration.DisplayName}: {r.Message}");
                    if (!r.Ok) failed++;
                    if (r.Ok && Ui.Try(() => t.Integration.PostRegisterHint, null, "PostRegisterHint") is { Length: > 0 } hint) hints.Add(hint);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Error("integrations", $"Подключение {t.Integration.Id}", ex);
                    lines.Add($"✗ {t.Integration.DisplayName}: {Ui.FriendlyError(ex)}");
                }
            }
        }, L.T("Не удалось подключить IDE"));
        _status.ForeColor = failed > 0 ? Theme.ErrorText : Theme.OkText;
        _status.Text = string.Join(Environment.NewLine, lines);
        var text = string.Join(Environment.NewLine, lines);
        if (hints.Count > 0) text += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, hints.Distinct());
        if (failed > 0) Ui.Warn(Owner, text);
        else Ui.Info(Owner, text);
        Shell.ConfigChanged();
        await RefreshAsync();
        LoadClaudeChecks();
    }

    private void ShowResult(IIdeIntegration i, IntegrationResult r, bool registered)
    {
        var hint = registered && r.Ok ? Ui.Try(() => i.PostRegisterHint, null, "PostRegisterHint") : null;
        var text = r.Message;
        if (!string.IsNullOrWhiteSpace(hint)) text += Environment.NewLine + hint;
        if (!string.IsNullOrWhiteSpace(r.BackupPath)) text += Environment.NewLine + L.F("Резервная копия: {0}", r.BackupPath);
        _status.ForeColor = r.Ok ? Theme.OkText : Theme.ErrorText;
        _status.Text = text;
        if (!r.Ok) Ui.ShowError(Owner, registered ? L.F("Не удалось подключить {0}", i.DisplayName) : L.F("Не удалось отключить {0}", i.DisplayName), r.Message);
        else if (!string.IsNullOrWhiteSpace(hint)) Ui.Info(Owner, $"{r.Message}{Environment.NewLine}{Environment.NewLine}{hint}");
    }

    private void OpenConfig()
    {
        var path = Selected()?.Integration.ConfigPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (File.Exists(path)) Ui.OpenInNotepad(path);
        else Ui.SelectInExplorer(path);
    }

    // ---------- Claude Code ----------

    private void LoadClaudeChecks()
    {
        _loadingChecks = true;
        try
        {
            var claude = Ui.Try(() => IntegrationRegistry.Find(ClaudeCodeId), null, "Find(claude-code)");
            var found = claude is not null && Ui.Try(claude.IsClientInstalled, false, "claude.IsClientInstalled");
            _claudeNote.Text = found
                ? L.T("Дополнительные настройки для Claude Code помогают ему чаще и правильнее поручать задачи локальной модели.")
                : L.T("Claude Code не найден на этом компьютере. Настройки можно включить заранее — они подействуют после установки Claude Code.");
            _guidance.Checked = Ui.Try(ClaudeCodeExtras.IsGuidanceInstalled, false, "IsGuidanceInstalled");
            _strongRule.Checked = Ui.Try(ClaudeCodeExtras.IsStrongRuleInstalled, false, "IsStrongRuleInstalled");
            _runnerAgent.Checked = Ui.Try(ClaudeCodeExtras.IsRunnerAgentInstalled, false, "IsRunnerAgentInstalled");
            _approveRead.Checked = Ui.Try(ClaudeCodeExtras.AreToolsPreapproved, false, "AreToolsPreapproved");
            LoadWriteApprovals();
            var modified = Ui.Try(ClaudeCodeExtras.ModifiedFiles, [], "ModifiedFiles");
            _claudeModified.Text = modified.Count > 0
                ? L.F("Изменены вручную и не обновляются автоматически: {0}. Чтобы вернуть версию Offload, выключите и снова включите пункт.", string.Join(", ", modified))
                : "";
            _claudeModified.Visible = modified.Count > 0;
            _autoRepair.Checked = ConfigStore.Current.Ui.AutoRepairIntegrations;
            _autoRepair.Enabled = !DevMode.Active;
            foreach (var (file, box, note) in _clientGuidance)
            {
                var installed = Ui.Try(file.IsClientInstalled, false, $"{file.Id}.IsClientInstalled");
                var state = Ui.Try(file.State, ManagedState.Foreign, $"{file.Id}.State");
                box.Checked = state is ManagedState.Current or ManagedState.Outdated or ManagedState.Modified;
                box.Enabled = installed || box.Checked;
                var path = Ui.Try(() => file.FilePath, "", $"{file.Id}.FilePath");
                note.ForeColor = state == ManagedState.Modified ? Theme.WarnText : Theme.TextMuted;
                note.Text = state switch
                {
                    ManagedState.Modified => L.F("Секция в {0} изменена вручную — Offload её не обновляет.", path),
                    ManagedState.Foreign => L.F("Файл {0} не удалось прочитать (не UTF-8?) — он не изменяется.", path),
                    _ when !installed => L.F("{0} не найден на этом компьютере.", file.DisplayName),
                    _ => L.F("Файл: {0}", path),
                };
            }
        }
        finally
        {
            _loadingChecks = false;
        }
    }

    private void ToggleAutoRepair()
    {
        if (_loadingChecks) return;
        var on = _autoRepair.Checked;
        if (Ui.RunSafe(Owner, () => ConfigStore.Update(c => c.Ui.AutoRepairIntegrations = on), L.T("Не удалось сохранить настройку")))
        {
            Log.Info("integrations", on ? "Автовосстановление подключений включено" : "Автовосстановление подключений выключено");
            Shell.ConfigChanged();
        }
    }

    private void ToggleGuidance(GuidanceFile file, CheckBox box)
    {
        if (_loadingChecks) return;
        var on = box.Checked;
        var what = L.F("инструкции для {0}", file.DisplayName);
        Ui.RunSafe(Owner, () =>
        {
            var r = on ? file.Install() : file.Remove();
            Log.Info("integrations", $"{file.Id}, инструкции по делегированию: {r.Message}");
            _status.ForeColor = r.Ok ? Theme.OkText : Theme.ErrorText;
            _status.Text = string.IsNullOrWhiteSpace(r.BackupPath) ? r.Message : r.Message + Environment.NewLine + L.F("Резервная копия: {0}", r.BackupPath);
            if (!r.Ok) Ui.ShowError(Owner, L.F("Не удалось изменить «{0}»", what), r.Message);
        }, L.F("Не удалось изменить «{0}»", what));
        LoadClaudeChecks();
    }

    // ---------- Проверка подключений ----------

    /// <summary>IDE, к которым Offload подключён (по настройкам и по текущему статусу в списке).</summary>
    private List<IIdeIntegration> ConnectedIntegrations()
    {
        var ids = ConfigStore.Current.Integrations.ToHashSet(StringComparer.Ordinal);
        return _list.Items.Cast<ListViewItem>().Select(x => (Row)x.Tag!)
            .Where(r => ids.Contains(r.Integration.Id) || r.Status is IntegrationStatus.Registered or IntegrationStatus.Outdated or IntegrationStatus.Error)
            .Select(r => r.Integration)
            .ToList();
    }

    private async Task CheckAsync(List<IIdeIntegration> targets)
    {
        if (targets.Count == 0)
        {
            Ui.Info(Owner, L.T("Нет подключённых IDE — сначала подключите Offload к IDE."));
            return;
        }
        var reports = new List<DoctorReport>();
        await RunBusyAsync(async () =>
        {
            var lastCalls = await Task.Run(IntegrationDoctor.ReadLastCalls);
            // Кроме текущего exe доктор может запустить для проверки только установленную копию Offload.
            var installed = await Task.Run<string[]>(() => InstallInfo.Installed?.ExePath is { } exe ? [exe] : []);
            foreach (var t in targets)
            {
                if (IsDisposed) return;
                _status.ForeColor = Theme.TextMuted;
                _status.Text = L.F("Проверка: {0}…", t.DisplayName);
                var integration = t;
                reports.Add(await Task.Run(() => IntegrationDoctor.CheckAsync(integration, lastCalls, installed)));
            }
        }, L.T("Не удалось проверить подключение"));
        if (IsDisposed || reports.Count == 0) return;
        var worst = reports.Max(r => r.Worst);
        _status.ForeColor = worst switch
        {
            DoctorLevel.Error => Theme.ErrorText,
            DoctorLevel.Warning => Theme.WarnText,
            _ => Theme.OkText,
        };
        var failed = reports.Count(r => r.Worst == DoctorLevel.Error);
        _status.Text = failed > 0
            ? L.F("Проверка завершена: проблемы найдены в {0} из {1}.", failed, reports.Count)
            : L.F("Проверка завершена: {0} — без ошибок.", string.Join(", ", reports.Select(r => r.DisplayName)));
        _report.Text = FormatReports(reports);
        _report.Visible = true;
    }

    internal static string FormatReports(IEnumerable<DoctorReport> reports)
    {
        var lines = new List<string>();
        foreach (var r in reports)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(r.DisplayName);
            foreach (var c in r.Checks)
            {
                var mark = c.Level switch
                {
                    DoctorLevel.Ok => "✓",
                    DoctorLevel.Warning => "!",
                    DoctorLevel.Error => "✗",
                    _ => "·",
                };
                lines.Add($"  {mark} {c.Text}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string LastCallText(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("t", L.Culture) : local.ToString("g", L.Culture);
    }

    private void ToggleExtra(CheckBox box, Func<IntegrationResult> install, Func<IntegrationResult> remove, string what)
    {
        if (_loadingChecks) return;
        var on = box.Checked;
        Ui.RunSafe(Owner, () =>
        {
            var r = on ? install() : remove();
            Log.Info("integrations", $"Claude Code, {what}: {r.Message}");
            _status.ForeColor = r.Ok ? Theme.OkText : Theme.ErrorText;
            _status.Text = r.Message;
            if (!r.Ok) Ui.ShowError(Owner, L.F("Claude Code: не удалось изменить «{0}»", what), r.Message);
        }, L.F("Claude Code: не удалось изменить «{0}»", what));
        LoadClaudeChecks();
    }

    /// <summary>
    /// Общий пункт «инструменты записи без подтверждения». Включён — во всех клиентах с разрешениями Offload разрешены все
    /// инструменты записи (и среди них есть хоть один с поимённым списком: одно лишь trust у Gemini, выставленное вручную,
    /// согласием на все клиенты не считается). Выключен — нигде не разрешены. Иначе — промежуточное состояние: щелчок по нему
    /// снимает разрешения везде, повторный — разрешает все. Новые инструменты записи сами не разрешаются: пока разрешены
    /// не все, пункт не включён, и его включение и есть согласие.
    /// </summary>
    private void LoadWriteApprovals()
    {
        var states = Ui.Try(ToolApprovals.Inspect, [], "ToolApprovals.Inspect");
        var full = states.Count(s => s.State == WriteApproval.Full);
        _approveWrite.CheckState =
            full == states.Count && states.Any(s => !s.WholeServer) ? CheckState.Checked
            : states.All(s => s.State == WriteApproval.None) ? CheckState.Unchecked
            : CheckState.Indeterminate;
        var partial = states.Where(s => s.State == WriteApproval.Partial).ToList();
        var denied = states.Where(s => s.State == WriteApproval.None).ToList();
        _pendingWrite.Text = partial.Count > 0
            ? L.F("Новые инструменты записи ещё спрашивают подтверждение: {0} ({1}). Чтобы разрешить и их, включите пункт; чтобы снять разрешения везде, щёлкните по нему.",
                string.Join(", ", partial.SelectMany(s => s.Missing).Distinct()), string.Join(", ", partial.Select(s => s.DisplayName)))
            : _approveWrite.CheckState == CheckState.Indeterminate
                ? L.F("Разрешены только в: {0}. Щелчок по пункту снимет разрешения везде, повторный — разрешит во всех клиентах.",
                    string.Join(", ", states.Where(s => s.State == WriteApproval.Full).Select(s => s.DisplayName)))
                : "";
        _pendingWrite.Visible = _pendingWrite.Text.Length > 0;
    }

    private void ToggleClaudeReadApprovals()
    {
        if (_loadingChecks) return;
        var read = _approveRead.Checked;
        Ui.RunSafe(Owner, () =>
        {
            // Включение только добавляет недостающие инструменты чтения; выключение снимает все разрешения Offload в Claude Code.
            var r = read ? ClaudeCodeExtras.AllowReadTools() : ClaudeCodeExtras.RevokeToolApprovals();
            Log.Info("integrations", $"Claude Code, разрешения: {r.Message}");
            _status.ForeColor = r.Ok ? Theme.OkText : Theme.ErrorText;
            _status.Text = r.Message;
            if (!r.Ok) Ui.ShowError(Owner, L.T("Claude Code: не удалось изменить разрешения"), r.Message);
        }, L.T("Claude Code: не удалось изменить разрешения"));
        LoadClaudeChecks();
    }

    private async Task ToggleWriteApprovalsAsync()
    {
        if (_loadingChecks) return;
        // Из промежуточного состояния щелчок ведёт в «выключено» — это отзыв разрешений везде.
        var allow = _approveWrite.CheckState == CheckState.Checked;
        if (allow &&
            !Ui.Confirm(Owner,
                L.F("{0} смогут без подтверждения поручать Offload создание и правку файлов проекта (каждую правку можно отменить через local_job). В Gemini CLI это доверие всему серверу (trust), включая инструменты будущих версий. Разрешить?",
                    string.Join(", ", Ui.Try(() => ToolApprovals.SupportedNames, [], "ToolApprovals.SupportedNames"))), warning: true))
        {
            LoadClaudeChecks();
            return;
        }
        await RunBusyAsync(async () =>
        {
            var (results, anyClient) = await Task.Run(() =>
            {
                var r = ToolApprovals.SetWriteTools(allow);
                return (r, r.Count > 0 || ToolApprovals.Inspect().Count > 0);
            });
            var failed = results.Where(r => !r.Result.Ok).ToList();
            _status.ForeColor = failed.Count == 0 && anyClient ? Theme.OkText : failed.Count > 0 ? Theme.ErrorText : Theme.TextMuted;
            _status.Text = results.Count > 0 ? string.Join(Environment.NewLine, results.Select(r => r.Result.Message))
                : !anyClient ? L.T("Offload не подключён ни к одному клиенту, где можно разрешить инструменты записи: подключите его к Claude Code, Cline, Roo Code, Kiro или Gemini CLI.")
                : allow ? L.T("Инструменты записи уже разрешены во всех клиентах, где подключён Offload.")
                : L.T("Инструменты записи нигде не разрешены без подтверждения.");
            if (failed.Count > 0)
                Ui.ShowError(Owner, L.T("Не удалось изменить разрешения инструментов записи"), string.Join(Environment.NewLine, failed.Select(r => r.Result.Message)));
        }, L.T("Не удалось изменить разрешения инструментов записи"), _approveWrite);
        LoadClaudeChecks();
    }

    // ---------- Ручное подключение ----------

    private void FillManual()
    {
        var spec = Spec();
        var args = spec.Args.Count > 0 ? spec.Args : [AppInfo.McpArg];
        _command.Text = $"\"{spec.Command}\" {string.Join(' ', args)}";

        var server = new JsonObject
        {
            ["command"] = spec.Command,
            ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
        };
        if (spec.Env.Count > 0)
        {
            var env = new JsonObject();
            foreach (var (k, v) in spec.Env) env[k] = v;
            server["env"] = env;
        }
        var root = new JsonObject { ["mcpServers"] = new JsonObject { [AppInfo.McpServerId] = server } };
        _json.Text = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private void Copy(string text)
    {
        if (Ui.TrySetClipboard(text))
        {
            _status.ForeColor = Theme.OkText;
            _status.Text = L.T("Скопировано в буфер обмена.");
        }
        else
        {
            Ui.Warn(Owner, L.T("Не удалось скопировать в буфер обмена."));
        }
    }
}
