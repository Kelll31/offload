using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Usage;
using Offload.Core.Util;
using Offload.Llama;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «Состояние»: сервер, проверка модели, видеопамять и экономия токенов.</summary>
internal sealed class StatusPage : PageBase
{
    private readonly StatusDot _dot = new(18);
    private readonly Label _state = Kit.Label("", Theme.Semibold(15f));
    private readonly Label _model = Kit.Label("", Theme.Semibold(11f));
    private readonly Label _backend = Kit.Label("");
    private readonly LinkLabel _endpoint;
    private readonly Label _context = Kit.Label("");
    private readonly Label _uptime = Kit.Label("");
    private readonly Label _tools = Kit.Label("");
    private readonly Label _message = Kit.Wrap("");
    private readonly Button _start;
    private readonly Button _stop;
    private readonly Button _restart;
    private readonly Button _check;
    private readonly Label _checkResult = Kit.Wrap("", color: Theme.TextMuted);

    private readonly ProgressBar _vramBar = new() { Minimum = 0, Maximum = 1000, Height = 14, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 12, 6), Style = ProgressBarStyle.Continuous };
    private readonly Label _vramText = Kit.Label("Нет данных");

    private readonly Label[,] _usageCells = new Label[3, 4];
    private readonly ListView _byTool = Kit.List(("Инструмент", 3), ("Вызовов", 1));
    private readonly ListView _byClient = Kit.List(("Клиент", 3), ("Вызовов", 1));
    private readonly Label _failed = Kit.Hint("");

    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _vramTimer;
    private bool _usageLoading;
    private bool _vramLoading;
    private bool _propsLoading;
    private ServerProps? _props;
    private bool _checking;

    public StatusPage(IAppShell shell) : base(shell)
    {
        _endpoint = new LinkLabel
        {
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 4, 8, 4),
            Anchor = AnchorStyles.Left,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Accent,
            BackColor = Color.Transparent,
        };
        _endpoint.LinkClicked += (_, _) => Ui.OpenShell(ConfigStore.Current.Server.BaseUrl);

        _start = Kit.Primary("Запустить", async (_, _) => await StartAsync());
        _stop = Kit.Button("Остановить", async (_, _) => await RunBusyAsync(() => Shell.Server.StopAsync(), "Не удалось остановить сервер"));
        _restart = Kit.Button("Перезапустить", async (_, _) => await RunBusyAsync(async () =>
        {
            if (!await Shell.Server.RestartAsync()) ShowStartError();
        }, "Не удалось перезапустить сервер"));
        _check = Kit.Button("Проверить модель", async (_, _) => await CheckModelAsync());

        var root = Kit.Table();

        // Карточка состояния сервера.
        var card = new CardPanel { ColumnCount = 1 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = Kit.Flow(_dot, _state);
        head.WrapContents = false;
        _dot.Margin = new Padding(0, 9, 10, 4);
        card.AddRow(head);
        _model.Margin = new Padding(0, 0, 0, 8);
        card.AddRow(_model);

        var copyKey = Kit.ActionLink("копировать ключ API", CopyApiKey);

        var grid = Kit.Grid();
        grid.AddField("Сборка llama.cpp:", _backend);
        grid.AddField("Адрес API:", Kit.Flow(_endpoint, copyKey));
        grid.AddField("Контекст:", _context);
        grid.AddField("Время работы:", _uptime);
        grid.AddField("Инструменты:", _tools);
        card.AddRow(grid);
        card.AddRow(_message);
        card.AddRow(Kit.Flow(_start, _stop, _restart, _check));
        card.AddRow(_checkResult);
        _checkResult.Visible = false;
        root.AddRow(card);

        // Видеопамять.
        root.AddRow(Kit.Section("Видеопамять"));
        var vram = Kit.Table(100, 0);
        _vramText.Margin = new Padding(0, 4, 0, 4);
        vram.AddRow(_vramBar, _vramText);
        root.AddRow(vram);
        root.AddRow(Kit.Hint("Показывается для видеокарт NVIDIA (по данным nvidia-smi) и обновляется каждые 3 секунды."));

        // Экономия токенов.
        root.AddRow(Kit.Section("Экономия токенов"));
        root.AddRow(Kit.Hint(
            "Когда Claude Code или другая IDE поручает задачу Offload, файлы читает и текст пишет локальная модель — " +
            "облачной модели достаётся только короткий ответ. «Сэкономлено» — оценка токенов, которые облачной модели не пришлось " +
            "обработать; сумма в долларах — приблизительный расчёт по ценам, указанным на вкладке «Промпт»."));
        root.AddRow(BuildUsageTable());
        root.AddRow(_failed);

        var lists = Kit.Table(50, 50);
        lists.AddRow(Kit.Label("По инструментам", Theme.Semibold(9f)), Kit.Label("По клиентам", Theme.Semibold(9f)));
        _byTool.Margin = new Padding(0, 2, 8, 4);
        _byClient.Margin = new Padding(8, 2, 0, 4);
        lists.AddFixedRow(150, _byTool, _byClient);
        root.AddRow(lists);
        root.AddRow(Kit.Flow(Kit.Button("Сбросить статистику", (_, _) => ResetStats(), 150)));

        Controls.Add(Kit.Scroll(root));

        _refreshTimer = CreateTimer(5000, () => _ = RefreshDynamicAsync());
        _vramTimer = CreateTimer(3000, () => _ = RefreshVramAsync());
        UpdateState();
    }

    public override string Key => Tabs.Status;

    public override string Title => "Состояние";

    private TableLayoutPanel BuildUsageTable()
    {
        var t = Kit.Table(0, 22, 22, 26, 30);
        string[] headers = ["", "Вызовов", "Токенов локально", "Сэкономлено (оценка)", "≈ Экономия, $"];
        var headerCells = headers.Select(h =>
        {
            var l = Kit.Label(h, Theme.Semibold(9f), Theme.TextMuted);
            l.Anchor = AnchorStyles.Left;
            return (Control)l;
        }).ToArray();
        t.AddRow(headerCells);
        string[] rows = ["Сегодня", "За 7 дней", "За всё время"];
        for (var r = 0; r < rows.Length; r++)
        {
            var cells = new Control[5];
            var title = Kit.Label(rows[r], Theme.Semibold(9f));
            title.Margin = new Padding(0, 4, 16, 4);
            cells[0] = title;
            for (var c = 0; c < 4; c++)
            {
                var l = Kit.Label("—");
                _usageCells[r, c] = l;
                cells[c + 1] = l;
            }
            t.AddRow(cells);
        }
        return t;
    }

    protected override void OnActivated()
    {
        UpdateState();
        _refreshTimer.Start();
        _vramTimer.Start();
        _ = RefreshDynamicAsync();
        _ = RefreshVramAsync();
    }

    protected override void OnDeactivated()
    {
        _refreshTimer.Stop();
        _vramTimer.Stop();
    }

    public override void OnServerStateChanged()
    {
        if (Shell.Server.State != ServerState.Running) _props = null;
        UpdateState();
        if (IsActive && Shell.Server.State == ServerState.Running) _ = RefreshPropsAsync();
    }

    public override void OnConfigChanged() => UpdateState();

    protected override void UpdateUiState()
    {
        var s = Shell.Server.State;
        var busy = IsBusy || Shell.Server.IsBusy;
        _start.Enabled = !busy && s is ServerState.Stopped or ServerState.Failed or ServerState.NotConfigured;
        _stop.Enabled = !IsBusy && s is ServerState.Running or ServerState.Starting;
        _restart.Enabled = !busy && s is ServerState.Running or ServerState.Failed;
        _check.Enabled = !_checking && !busy && s is ServerState.Running or ServerState.Stopped;
    }

    private void UpdateState()
    {
        var server = Shell.Server;
        var cfg = ConfigStore.Current;
        var s = server.State;
        _dot.DotColor = Theme.StateColor(s);
        _state.Text = Texts.State(s);
        _model.Text = Texts.ModelName(cfg.ActiveModel()) + ModelSuffix(cfg.ActiveModel());

        var tag = cfg.Llama.InstalledTag;
        _backend.Text = string.IsNullOrWhiteSpace(tag)
            ? "не установлена"
            : $"{Texts.Backend(cfg.Llama.InstalledBackend)} · {tag}";

        _endpoint.Text = cfg.Server.OpenAiBaseUrl;
        _context.Text = ContextText(cfg);
        _uptime.Text = server.Uptime is TimeSpan up ? FileUtil.FormatDuration(up) : "—";
        _tools.Text = _props?.SupportsToolCalls switch
        {
            true => "шаблон чата модели поддерживает вызов инструментов ✓",
            false => "шаблон чата не поддерживает вызов инструментов — агент OpenCode может работать плохо",
            _ => "—",
        };

        var msg = s switch
        {
            ServerState.Failed or ServerState.NotConfigured => server.LastError,
            ServerState.Stopped => server.Notice,
            ServerState.Starting => "Модель загружается в память — это может занять до пары минут.",
            ServerState.Running when _props?.IsSleeping == true => "Модель выгружена после простоя и загрузится при следующем запросе.",
            _ => null,
        };
        _message.Text = msg ?? "";
        _message.Visible = !string.IsNullOrWhiteSpace(msg);
        _message.ForeColor = s is ServerState.Failed ? Theme.ErrorText : s is ServerState.NotConfigured ? Theme.WarnText : Theme.TextMuted;
        UpdateUiState();
    }

    private static string ModelSuffix(InstalledModel? m)
    {
        if (m is null) return "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.Quant)) parts.Add(m.Quant!);
        if (m.SizeBytes > 0) parts.Add(FileUtil.FormatBytes(m.SizeBytes));
        return parts.Count == 0 ? "" : "  ·  " + string.Join(" · ", parts);
    }

    private string ContextText(AppConfig cfg)
    {
        var plan = Shell.Server.State == ServerState.Running ? Shell.Server.Plan : null;
        var parallel = plan?.Parallel ?? cfg.Server.Parallel;
        string ctx;
        if (_props is { ContextPerSlot: > 0 } p) ctx = $"{Ui.Tokens(p.ContextPerSlot)} токенов на запрос";
        else if (plan is { ContextSize: > 0 }) ctx = $"{Ui.Tokens(plan.ContextSize)} токенов";
        else if (cfg.Server.ContextSize > 0) ctx = $"{Ui.Tokens(cfg.Server.ContextSize)} токенов";
        else ctx = "автоматически (по модели и видеопамяти)";
        return parallel > 1 ? $"{ctx}, параллельных запросов: {parallel}" : ctx;
    }

    private async Task RefreshDynamicAsync()
    {
        UpdateState();
        await RefreshUsageAsync();
        if (Shell.Server.State == ServerState.Running) await RefreshPropsAsync();
    }

    private async Task RefreshPropsAsync()
    {
        if (_propsLoading) return;
        _propsLoading = true;
        try
        {
            var cfg = ConfigStore.Current;
            _props = await LlamaClient.FromConfig(cfg).GetPropsAsync();
            if (!IsDisposed) UpdateState();
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"/props: {ex.Message}");
        }
        finally
        {
            _propsLoading = false;
        }
    }

    private async Task RefreshUsageAsync()
    {
        if (_usageLoading) return;
        _usageLoading = true;
        try
        {
            var prices = ConfigStore.Current.Mcp;
            var (periods, all) = await Task.Run(() =>
            {
                var records = UsageLog.ReadAll();
                return (UsageStats.Build(records, prices), UsageLog.Summarize(records));
            });
            if (IsDisposed) return;
            for (var r = 0; r < periods.Count && r < 3; r++)
            {
                var p = periods[r];
                _usageCells[r, 0].Text = Ui.N(p.Summary.Calls);
                _usageCells[r, 1].Text = Ui.N(p.Summary.PromptTokens + p.Summary.CompletionTokens);
                _usageCells[r, 2].Text = Ui.N(p.Summary.EstimatedSavedTokens);
                _usageCells[r, 3].Text = UsageStats.Dollars(p.Dollars);
            }
            _failed.Text = all.Calls == 0
                ? "Обращений пока не было. Подключите Offload к IDE на вкладке «Интеграции» и попросите Claude: «используй offload, чтобы …»."
                : $"Всего: запрос {Ui.N(all.PromptTokens)} + ответ {Ui.N(all.CompletionTokens)} токенов локально, " +
                  $"время работы модели {FileUtil.FormatDuration(all.TotalDuration)}" +
                  (all.Failed > 0 ? $", с ошибкой завершились {Ui.N(all.Failed)} вызовов." : ".");
            Fill(_byTool, all.CallsByTool.ToDictionary(kv => Texts.ToolName(kv.Key), kv => kv.Value));
            Fill(_byClient, all.CallsByClient.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Статистика: {ex.Message}");
        }
        finally
        {
            _usageLoading = false;
        }
    }

    private static void Fill(ListView lv, IReadOnlyDictionary<string, int> data)
    {
        var rows = data.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
        // Не перерисовывать без изменений (иначе список мигает каждые 5 секунд).
        var same = lv.Items.Count == rows.Count && rows.Select((kv, i) =>
            lv.Items[i].Text == kv.Key && lv.Items[i].SubItems[1].Text == Ui.N(kv.Value)).All(x => x);
        if (same) return;
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var (key, value) in rows) lv.Items.Add(new ListViewItem([key, Ui.N(value)]));
        lv.EndUpdate();
    }

    private async Task RefreshVramAsync()
    {
        if (_vramLoading) return;
        _vramLoading = true;
        try
        {
            var mem = await Task.Run(() => HardwareDetector.QueryNvidiaMemoryAsync());
            if (IsDisposed) return;
            if (mem is (long used, long total) && total > 0)
            {
                var f = Math.Clamp((double)used / total, 0, 1);
                _vramBar.Value = (int)Math.Round(f * 1000);
                _vramText.Text = $"{FileUtil.FormatBytes(used)} из {FileUtil.FormatBytes(total)} ({f:P0})";
            }
            else
            {
                _vramBar.Value = 0;
                _vramText.Text = "Нет данных (только для NVIDIA)";
            }
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Видеопамять: {ex.Message}");
        }
        finally
        {
            _vramLoading = false;
        }
    }

    private async Task StartAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (!await Shell.Server.StartAsync()) ShowStartError();
        }, "Не удалось запустить сервер");
    }

    private void ShowStartError()
    {
        var server = Shell.Server;
        if (server.State == ServerState.NotConfigured)
        {
            if (Ui.Confirm(Owner, $"{server.LastError}{Environment.NewLine}{Environment.NewLine}Открыть мастер настройки?"))
                Shell.ShowSetupWizard();
            return;
        }
        Ui.ShowError(Owner, "Сервер llama.cpp не запустился", server.LastError ?? "Подробности — на вкладке «Журнал» (источник llama-server).");
    }

    private async Task CheckModelAsync()
    {
        _checking = true;
        _checkResult.Visible = true;
        _checkResult.ForeColor = Theme.TextMuted;
        _checkResult.Text = "Отправляю тестовый запрос модели…";
        try
        {
            await RunBusyAsync(async () =>
            {
                if (Shell.Server.State != ServerState.Running)
                {
                    _checkResult.Text = "Запускаю сервер…";
                    if (!await Shell.Server.StartAsync())
                    {
                        _checkResult.Text = "";
                        ShowStartError();
                        return;
                    }
                    _checkResult.Text = "Отправляю тестовый запрос модели…";
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                Shell.Server.MarkActivity();
                try
                {
                    var result = await ModelCheck.RunAsync(ConfigStore.Current, cts.Token);
                    _checkResult.ForeColor = Theme.OkText;
                    _checkResult.Text = ModelCheck.Describe(result);
                }
                catch (OperationCanceledException)
                {
                    _checkResult.ForeColor = Theme.ErrorText;
                    _checkResult.Text = "Модель не ответила за 3 минуты.";
                }
                catch (Exception ex)
                {
                    Log.Warn("ui", $"Проверка модели: {ex.Message}");
                    _checkResult.ForeColor = Theme.ErrorText;
                    _checkResult.Text = "Проверка не удалась: " + Ui.FriendlyError(ex);
                }
            }, "Не удалось проверить модель");
        }
        finally
        {
            _checking = false;
            UpdateUiState();
        }
    }

    private void CopyApiKey()
    {
        var key = ConfigStore.Current.Server.ApiKey;
        if (Ui.TrySetClipboard(key))
            Shell.Notify("Ключ API скопирован", "Ключ llama-server скопирован в буфер обмена.", ToolTipIcon.Info, force: true);
        else
            Ui.Warn(Owner, "Не удалось скопировать ключ в буфер обмена.");
    }

    private void ResetStats()
    {
        if (!Ui.Confirm(Owner, "Удалить всю накопленную статистику обращений к локальной модели?", warning: true)) return;
        Ui.RunSafe(Owner, UsageLog.Clear, "Не удалось сбросить статистику");
        _byTool.Items.Clear();
        _byClient.Items.Clear();
        _ = RefreshUsageAsync();
    }
}
