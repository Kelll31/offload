using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Integrations;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «Интеграции»: подключение Offload как MCP-сервера к IDE и настройка Claude Code.</summary>
internal sealed class IntegrationsPage : PageBase
{
    private const string ClaudeCodeId = "claude-code";

    private sealed record Row(IIdeIntegration Integration, IntegrationStatus Status, bool Installed);

    private readonly ListView _list = Kit.List(("IDE / агент", 28), ("Статус", 18), ("Файл конфигурации", 54));
    private readonly Button _register;
    private readonly Button _unregister;
    private readonly Button _registerAll;
    private readonly Button _openConfig;
    private readonly Button _refresh;
    private readonly Label _status = Kit.Wrap("");

    private readonly Label _claudeNote = Kit.Hint("");
    private readonly CheckBox _guidance = Kit.Check("Инструкции по делегированию для Claude Code");
    private readonly CheckBox _strongRule = Kit.Check("Строгий режим (правило для Claude)");
    private readonly CheckBox _runnerAgent = Kit.Check("Субагент offload-runner");
    private readonly CheckBox _approveRead = Kit.Check("Разрешить инструменты чтения без подтверждения");
    private readonly CheckBox _approveWrite = Kit.Check("…и инструменты записи");

    private readonly TextBox _command = Kit.TextBox(readOnly: true);
    private readonly TextBox _json = Kit.MultiLine(118, mono: true, readOnly: true);

    private bool _loadingChecks;
    private bool _loadingList;
    private bool _loaded;

    public IntegrationsPage(IAppShell shell) : base(shell)
    {
        _register = Kit.Primary("Подключить", async (_, _) => await RegisterSelectedAsync());
        _unregister = Kit.Button("Отключить", async (_, _) => await UnregisterSelectedAsync());
        _registerAll = Kit.Button("Подключить все найденные", async (_, _) => await RegisterAllAsync(), 170);
        _openConfig = Kit.Button("Открыть файл конфигурации", (_, _) => OpenConfig(), 170);
        _refresh = Kit.Button("Обновить", async (_, _) => await RefreshAsync());
        _list.SelectedIndexChanged += (_, _) => UpdateUiState();
        _list.DoubleClick += async (_, _) =>
        {
            if (Selected() is { Installed: true, Status: not IntegrationStatus.Registered }) await RegisterSelectedAsync();
        };

        var root = Kit.Table();
        root.AddRow(Kit.Section("IDE и агенты", first: true));
        root.AddRow(Kit.Hint(
            "Offload подключается к IDE как MCP-сервер: IDE сама запускает «Offload.exe --mcp» и может поручать локальной модели " +
            "чтение файлов, ревью изменений, сообщения коммитов и простые правки. Перед изменением файла настроек IDE делается резервная копия."));
        root.AddFixedRow(230, _list);
        root.AddRow(Kit.Flow(_register, _unregister, _registerAll, _openConfig, _refresh));
        root.AddRow(_status);

        root.AddRow(Kit.Section("Claude Code"));
        root.AddRow(_claudeNote);
        root.AddRow(_guidance);
        root.AddRow(Indented(Kit.Hint("Файлы в ~/.claude объясняют Claude, какие задачи выгодно поручать локальной модели.")));
        root.AddRow(_strongRule);
        root.AddRow(Indented(Kit.Hint("Правило ~/.claude/rules/offload.md загружается в каждой сессии — Claude делегирует активнее.")));
        root.AddRow(_runnerAgent);
        root.AddRow(Indented(Kit.Hint("Субагент ~/.claude/agents/offload-runner.md выполняет пакеты механических задач через локальную модель.")));
        root.AddRow(_approveRead);
        _approveWrite.Margin = new Padding(20, 2, 8, 4);
        root.AddRow(_approveWrite);
        root.AddRow(Indented(Kit.Hint("Инструменты записи создают и правят файлы проекта; каждая правка сохраняется и может быть отменена (local_job).")));
        root.AddRow(Kit.Hint("После изменений перезапустите Claude Code."));

        root.AddRow(Kit.Section("Другие клиенты (вручную)"));
        root.AddRow(Kit.Hint("Если вашей IDE нет в списке, добавьте в её настройки MCP-сервер со следующей командой (транспорт stdio):"));
        var cmdRow = Kit.Table(100, 0);
        cmdRow.AddRow(_command, Kit.Button("Копировать", (_, _) => Copy(_command.Text)));
        root.AddRow(cmdRow);
        var jsonRow = Kit.Table(100, 0);
        var copyJson = Kit.Button("Копировать", (_, _) => Copy(_json.Text));
        copyJson.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        jsonRow.AddRow(_json, copyJson);
        root.AddRow(jsonRow);

        Controls.Add(Kit.Scroll(root));

        _guidance.CheckedChanged += (_, _) => ToggleExtra(_guidance,
            ClaudeCodeExtras.InstallGuidance, ClaudeCodeExtras.RemoveGuidance, "инструкции по делегированию");
        _strongRule.CheckedChanged += (_, _) => ToggleExtra(_strongRule,
            ClaudeCodeExtras.InstallStrongRule, ClaudeCodeExtras.RemoveStrongRule, "строгий режим");
        _runnerAgent.CheckedChanged += (_, _) => ToggleExtra(_runnerAgent,
            ClaudeCodeExtras.InstallRunnerAgent, ClaudeCodeExtras.RemoveRunnerAgent, "субагент offload-runner");
        _approveRead.CheckedChanged += (_, _) => ToggleApprovals(fromWrite: false);
        _approveWrite.CheckedChanged += (_, _) => ToggleApprovals(fromWrite: true);

        FillManual();
        UpdateUiState();
    }

    public override string Key => Tabs.Integrations;

    public override string Title => "Интеграции";

    public override string? BusyDescription => IsBusy ? "подключение к IDE" : null;

    private static Control Indented(Label hint)
    {
        hint.Margin = new Padding(20, 0, 0, 6);
        return hint;
    }

    protected override async void OnActivated()
    {
        try
        {
            if (!_loaded) _status.Text = "Поиск установленных IDE…";
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
        _register.Text = row?.Status == IntegrationStatus.Outdated ? "Обновить путь" : "Подключить";
        _unregister.Enabled = !busy && row is { Status: IntegrationStatus.Registered or IntegrationStatus.Outdated or IntegrationStatus.Error };
        _registerAll.Enabled = !busy && _list.Items.Count > 0;
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
            var (rows, error) = await Task.Run<(List<Row>, string?)>(() =>
            {
                try
                {
                    var list = IntegrationRegistry.All.Select(i =>
                    {
                        var installed = Ui.Try(i.IsClientInstalled, false, $"{i.Id}.IsClientInstalled");
                        var status = Ui.Try(() => i.GetStatus(spec), installed ? IntegrationStatus.Error : IntegrationStatus.ClientNotFound, $"{i.Id}.GetStatus");
                        if (!installed && status is IntegrationStatus.NotRegistered) status = IntegrationStatus.ClientNotFound;
                        return new Row(i, status, installed || status is IntegrationStatus.Registered or IntegrationStatus.Outdated);
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
                    Texts.Integration(r.Status),
                    Ui.Try(() => r.Integration.ConfigPath, null, "ConfigPath") ?? "—",
                ]) { Tag = r, UseItemStyleForSubItems = false };
                item.SubItems[1].ForeColor = Texts.IntegrationColor(r.Status);
                if (r.Status == IntegrationStatus.ClientNotFound)
                {
                    item.ForeColor = Theme.Gray;
                    item.SubItems[2].ForeColor = Theme.Gray;
                }
                _list.Items.Add(item);
                if (r.Integration.Id == selectedId) item.Selected = true;
            }
            _list.EndUpdate();
            if (error is not null)
            {
                _status.ForeColor = Theme.ErrorText;
                _status.Text = "Не удалось получить список IDE: " + error;
            }
            else if (_status.Text == "Поиск установленных IDE…")
            {
                var found = rows.Count(r => r.Installed);
                _status.ForeColor = Theme.TextMuted;
                _status.Text = $"Найдено IDE и агентов: {found} из {rows.Count}.";
            }
        }
        finally
        {
            _loadingList = false;
            UpdateUiState();
        }
    }

    private async Task<IntegrationResult?> RegisterAsync(IIdeIntegration integration)
    {
        var spec = Spec();
        var result = await integration.RegisterAsync(spec);
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
        await RunBusyAsync(async () =>
        {
            var r = await RegisterAsync(i);
            if (r is null) return;
            ShowResult(i, r, registered: true);
        }, $"Не удалось подключить {i.DisplayName}");
        Shell.ConfigChanged();
        await RefreshAsync();
        if (i.Id == ClaudeCodeId) LoadClaudeChecks();
    }

    private async Task UnregisterSelectedAsync()
    {
        if (Selected() is not { } row) return;
        var i = row.Integration;
        if (!Ui.Confirm(Owner, $"Отключить Offload от «{i.DisplayName}»?")) return;
        await RunBusyAsync(async () =>
        {
            var r = await i.UnregisterAsync();
            Log.Info("integrations", $"{i.Id}: отключение — {r.Message}");
            if (r.Ok) ConfigStore.Update(c => c.Integrations.Remove(i.Id));
            ShowResult(i, r, registered: false);
        }, $"Не удалось отключить {i.DisplayName}");
        Shell.ConfigChanged();
        await RefreshAsync();
    }

    private async Task RegisterAllAsync()
    {
        var targets = _list.Items.Cast<ListViewItem>().Select(x => (Row)x.Tag!)
            .Where(r => r.Installed && r.Status is not IntegrationStatus.Registered).ToList();
        if (targets.Count == 0)
        {
            Ui.Info(Owner, "Все найденные IDE уже подключены.");
            return;
        }
        var lines = new List<string>();
        var hints = new List<string>();
        var failed = 0;
        await RunBusyAsync(async () =>
        {
            foreach (var t in targets)
            {
                _status.ForeColor = Theme.TextMuted;
                _status.Text = $"Подключение: {t.Integration.DisplayName}…";
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
        }, "Не удалось подключить IDE");
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
        if (!string.IsNullOrWhiteSpace(r.BackupPath)) text += Environment.NewLine + $"Резервная копия: {r.BackupPath}";
        _status.ForeColor = r.Ok ? Theme.OkText : Theme.ErrorText;
        _status.Text = text;
        if (!r.Ok) Ui.ShowError(Owner, registered ? $"Не удалось подключить {i.DisplayName}" : $"Не удалось отключить {i.DisplayName}", r.Message);
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
                ? "Дополнительные настройки для Claude Code помогают ему чаще и правильнее поручать задачи локальной модели."
                : "Claude Code не найден на этом компьютере. Настройки можно включить заранее — они подействуют после установки Claude Code.";
            _guidance.Checked = Ui.Try(ClaudeCodeExtras.IsGuidanceInstalled, false, "IsGuidanceInstalled");
            _strongRule.Checked = Ui.Try(ClaudeCodeExtras.IsStrongRuleInstalled, false, "IsStrongRuleInstalled");
            _runnerAgent.Checked = Ui.Try(ClaudeCodeExtras.IsRunnerAgentInstalled, false, "IsRunnerAgentInstalled");
            var approved = Ui.Try(ClaudeCodeExtras.AreToolsPreapproved, false, "AreToolsPreapproved");
            _approveRead.Checked = approved;
            _approveWrite.Checked = approved && ClaudeSettingsProbe.AreWriteToolsAllowed();
            _approveWrite.Enabled = _approveRead.Checked;
        }
        finally
        {
            _loadingChecks = false;
        }
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
            if (!r.Ok) Ui.ShowError(Owner, $"Claude Code: не удалось изменить «{what}»", r.Message);
        }, $"Claude Code: не удалось изменить «{what}»");
        LoadClaudeChecks();
    }

    private void ToggleApprovals(bool fromWrite)
    {
        if (_loadingChecks) return;
        if (fromWrite && _approveWrite.Checked &&
            !Ui.Confirm(Owner,
                "Claude Code сможет без подтверждения поручать Offload создание и правку файлов проекта " +
                "(каждую правку можно отменить через local_job). Разрешить?", warning: true))
        {
            LoadClaudeChecks();
            return;
        }
        var read = _approveRead.Checked;
        var write = read && _approveWrite.Checked;
        Ui.RunSafe(Owner, () =>
        {
            IntegrationResult r;
            if (!read)
            {
                r = ClaudeCodeExtras.RevokeToolApprovals();
            }
            else
            {
                // Снимаем прежние разрешения, чтобы выключение «записи» действительно убрало инструменты записи.
                if (!write) ClaudeCodeExtras.RevokeToolApprovals();
                r = ClaudeCodeExtras.PreapproveTools(write);
            }
            Log.Info("integrations", $"Claude Code, разрешения: {r.Message}");
            _status.ForeColor = r.Ok ? Theme.OkText : Theme.ErrorText;
            _status.Text = r.Message;
            if (!r.Ok) Ui.ShowError(Owner, "Claude Code: не удалось изменить разрешения", r.Message);
        }, "Claude Code: не удалось изменить разрешения");
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
            _status.Text = "Скопировано в буфер обмена.";
        }
        else
        {
            Ui.Warn(Owner, "Не удалось скопировать в буфер обмена.");
        }
    }
}
