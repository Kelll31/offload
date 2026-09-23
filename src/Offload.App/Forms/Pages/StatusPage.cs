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

/// <summary>
/// Вкладка «Состояние» — дашборд: карточка сервера с управлением, плитки показателей (экономия, вызовы, видеопамять),
/// график экономии по дням, распределение по инструментам и клиентам, лента последних вызовов.
/// </summary>
internal sealed class StatusPage : PageBase
{
    private const int HistoryDays = 14;
    private const int FeedSize = 60;

    private readonly StatusDot _dot = new(16);
    private readonly Label _state = Kit.Label("", Theme.Semibold(16f));
    private readonly Label _model = Kit.Label("", Theme.Regular(10.5f), Theme.TextMuted);
    private readonly Label _backend = FactValue();
    private readonly LinkLabel _endpoint;
    private readonly Label _context = FactValue();
    private readonly Label _uptime = FactValue();
    private readonly Label _tools = FactValue();
    private readonly Label _message = Kit.Wrap("");
    private readonly Button _start;
    private readonly Button _stop;
    private readonly Button _restart;
    private readonly Button _check;
    private readonly Label _checkResult = Kit.Wrap("", color: Theme.TextMuted);

    private readonly StatTile _tileToday = new(L.T("Сэкономлено сегодня"), Glyphs.Bolt);
    private readonly StatTile _tileCalls = new(L.T("Вызовов сегодня"), Glyphs.Chat);
    private readonly StatTile _tileTotal = new(L.T("За всё время"), Glyphs.History);
    private readonly StatTile _tileVram = new(L.T("Видеопамять"), Glyphs.Server);
    private readonly BarChart _daily = new() { EmptyText = L.T("Обращений за эти дни не было") };
    private readonly BarList _byTool = new() { EmptyText = L.T("Пока нет вызовов") };
    private readonly BarList _byClient = new() { EmptyText = L.T("Пока нет вызовов") };
    private readonly ListView _feed = Kit.List((L.T("Время"), 11), (L.T("Инструмент"), 22), (L.T("Клиент"), 16), (L.T("Токенов"), 11), (L.T("Сэкономлено"), 12), (L.T("Длительность"), 12), (L.T("Итог"), 9));
    private readonly Label _summary = Kit.Hint("");

    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _vramTimer;
    private bool _usageLoading;
    private bool _vramLoading;
    private bool _propsLoading;
    private ServerProps? _props;
    private bool _checking;
    private string? _usageSignature;

    public StatusPage(IAppShell shell) : base(shell)
    {
        _endpoint = new LinkLabel
        {
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 2, 8, 2),
            Anchor = AnchorStyles.Left,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Accent,
            BackColor = Color.Transparent,
        };
        _endpoint.LinkClicked += (_, _) => Ui.OpenShell(ConfigStore.Current.Server.BaseUrl);

        _start = Kit.Primary(L.T("Запустить"), async (_, _) => await StartAsync());
        _stop = Kit.Button(L.T("Остановить"), async (_, _) => await RunBusyAsync(() => Shell.Server.StopAsync(), L.T("Не удалось остановить сервер")));
        _restart = Kit.Button(L.T("Перезапустить"), async (_, _) => await RunBusyAsync(async () =>
        {
            if (!await Shell.Server.RestartAsync()) ShowStartError();
        }, L.T("Не удалось перезапустить сервер")));
        _check = Kit.Button(L.T("Проверить модель"), async (_, _) => await CheckModelAsync());

        var root = Kit.Table();
        root.AddRow(BuildServerCard());
        root.AddRow(BuildTiles());

        root.AddRow(ChartCard(L.T("Экономия по дням"), L.T("Оценка облачных токенов, которые не пришлось обработать, за последние 14 дней"), _daily, 200));

        var split = Kit.Table(50, 50);
        var toolsCard = ChartCard(L.T("По инструментам"), L.T("Число вызовов"), _byTool, 210);
        var clientsCard = ChartCard(L.T("По клиентам"), L.T("Какие IDE обращались к Offload"), _byClient, 210);
        toolsCard.Dock = clientsCard.Dock = DockStyle.Fill;
        toolsCard.Margin = new Padding(0, 0, 6, 12);
        clientsCard.Margin = new Padding(6, 0, 0, 12);
        split.AddRow(toolsCard, clientsCard);
        root.AddRow(split);

        _feed.Margin = new Padding(0, 4, 0, 0);
        root.AddRow(ChartCard(L.T("Последние вызовы"), L.T("Самые свежие обращения IDE к локальной модели"), _feed, 250));

        _summary.Margin = new Padding(2, 0, 0, 6);
        root.AddRow(_summary);
        root.AddRow(Kit.Hint(L.T(
            "Когда Claude Code или другая IDE поручает задачу Offload, файлы читает и текст пишет локальная модель — облачной модели достаётся только короткий ответ. «Сэкономлено» — оценка токенов, которые облачной модели не пришлось обработать; сумма в долларах — приблизительный расчёт по ценам, указанным на вкладке «Промпт».")));
        root.AddRow(Kit.Flow(Kit.Button(L.T("Сбросить статистику"), (_, _) => ResetStats(), 150)));

        Controls.Add(Kit.Scroll(root, new Padding(16, 4, 28, 16)));

        _refreshTimer = CreateTimer(5000, () => _ = RefreshDynamicAsync());
        _vramTimer = CreateTimer(3000, () => _ = RefreshVramAsync());
        UpdateState();
    }

    public override string Key => Tabs.Status;

    public override string Title => L.T("Состояние");

    public override string Subtitle => L.T("Сервер, видеопамять и сколько облачных токенов сэкономлено");

    public override string Glyph => Glyphs.Status;

    // ---------- Построение ----------

    private static Label FactValue()
    {
        var l = Kit.Label("—", Theme.Regular(9.5f), Theme.TextPrimary);
        l.Margin = new Padding(0, 2, 8, 2);
        return l;
    }

    private static Control Fact(string caption, Control value)
    {
        var t = Kit.Table();
        t.Dock = DockStyle.Fill;
        t.Margin = new Padding(0, 6, 16, 6);
        var c = Kit.Label(caption, Theme.Regular(8.5f), Theme.TextMuted);
        c.Margin = new Padding(0, 0, 0, 1);
        t.AddRow(c);
        t.AddRow(value);
        return t;
    }

    private CardPanel BuildServerCard()
    {
        var card = new CardPanel { ColumnCount = 2, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(20, 16, 20, 14) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var head = Kit.Flow(_dot, _state);
        head.WrapContents = false;
        head.Margin = new Padding(0);
        _dot.Margin = new Padding(0, 10, 10, 4);
        _state.Margin = new Padding(0, 2, 8, 0);
        var buttons = Kit.Flow(_start, _stop, _restart, _check);
        buttons.WrapContents = false;
        buttons.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        buttons.Margin = new Padding(0, 4, 0, 0);
        _check.Margin = new Padding(0, 2, 0, 2);
        card.AddRow(head, buttons);

        _model.Margin = new Padding(28, 0, 0, 8);
        card.AddRow(_model, null);

        var copyKey = Kit.ActionLink(L.T("копировать"), CopyApiKey);
        copyKey.Margin = new Padding(0, 2, 8, 2);
        var facts = Kit.Table(34, 33, 33);
        facts.Margin = new Padding(28, 0, 0, 0);
        facts.AddRow(Fact(L.T("Сборка llama.cpp"), _backend), Fact(L.T("Адрес API"), _endpoint), Fact(L.T("Ключ API"), copyKey));
        facts.AddRow(Fact(L.T("Контекст"), _context), Fact(L.T("Время работы"), _uptime), Fact(L.T("Вызов инструментов"), _tools));
        card.AddRow(facts);

        _message.Margin = new Padding(28, 6, 0, 0);
        card.AddRow(_message);
        _checkResult.Margin = new Padding(28, 6, 0, 0);
        card.AddRow(_checkResult);
        _checkResult.Visible = false;
        return card;
    }

    private TableLayoutPanel BuildTiles()
    {
        var t = Kit.Table(25, 25, 25, 25);
        _tileVram.Margin = new Padding(0, 0, 0, 12);
        t.AddFixedRow(124, _tileToday, _tileCalls, _tileTotal, _tileVram);
        return t;
    }

    private static CardPanel ChartCard(string title, string hint, Control body, int height)
    {
        var card = new CardPanel { ColumnCount = 1, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(20, 14, 20, 12) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = Kit.Label(title, Theme.Semibold(10.5f));
        head.Margin = new Padding(0, 0, 0, 0);
        card.AddRow(head);
        var sub = Kit.Label(hint, Theme.Regular(8.5f), Theme.TextMuted);
        sub.Margin = new Padding(0, 1, 0, 8);
        card.AddRow(sub);
        body.Dock = DockStyle.Fill;
        card.AddFixedRow(height, body);
        return card;
    }

    // ---------- Жизненный цикл ----------

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
            ? L.T("не установлена")
            : $"{Texts.Backend(cfg.Llama.InstalledBackend)} · {tag}";

        _endpoint.Text = cfg.Server.OpenAiBaseUrl;
        _context.Text = ContextText(cfg);
        _uptime.Text = server.Uptime is TimeSpan up ? FileUtil.FormatDuration(up) : "—";
        _tools.Text = _props?.SupportsToolCalls switch
        {
            true => L.T("поддерживается ✓"),
            false => L.T("не поддерживается шаблоном"),
            _ => "—",
        };
        _tools.ForeColor = _props?.SupportsToolCalls == false ? Theme.WarnText : Theme.TextPrimary;

        var msg = s switch
        {
            ServerState.Failed or ServerState.NotConfigured => server.LastError,
            ServerState.Stopped => server.Notice,
            ServerState.Starting => L.T("Модель загружается в память — это может занять до пары минут."),
            ServerState.Running when _props?.IsSleeping == true => L.T("Модель выгружена после простоя и загрузится при следующем запросе."),
            ServerState.Running when _props?.SupportsToolCalls == false => L.T("Шаблон чата модели не поддерживает вызов инструментов — агент OpenCode может работать плохо."),
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
        if (_props is { ContextPerSlot: > 0 } p) ctx = L.F("{0} на запрос", Ui.Tokens(p.ContextPerSlot));
        else if (plan is { ContextSize: > 0 }) ctx = L.F("{0} токенов", Ui.Tokens(plan.ContextSize));
        else if (cfg.Server.ContextSize > 0) ctx = L.F("{0} токенов", Ui.Tokens(cfg.Server.ContextSize));
        else ctx = L.T("автоматически");
        return parallel > 1 ? $"{ctx} × {parallel}" : ctx;
    }

    // ---------- Данные ----------

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

    private sealed record UsageView(
        IReadOnlyList<UsageStats.Period> Periods,
        UsageSummary All,
        IReadOnlyList<BarChart.Bar> Daily,
        IReadOnlyList<double> CallsPerDay,
        IReadOnlyList<double> Cumulative,
        IReadOnlyList<UsageRecord> Recent,
        string Signature);

    private async Task RefreshUsageAsync()
    {
        if (_usageLoading) return;
        _usageLoading = true;
        try
        {
            var prices = ConfigStore.Current.Mcp;
            var view = await Task.Run(() => BuildUsageView(UsageLog.ReadAll(), prices));
            if (IsDisposed) return;
            if (view.Signature == _usageSignature) return; // без изменений — не перерисовывать
            _usageSignature = view.Signature;
            ApplyUsage(view);
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

    private static UsageView BuildUsageView(IReadOnlyList<UsageRecord> records, McpSettings prices)
    {
        var culture = L.Culture;
        var today = DateTime.Now.Date;
        var byDay = records.GroupBy(r => r.TimestampUtc.ToLocalTime().Date).ToDictionary(g => g.Key, g => g.ToList());
        var daily = new List<BarChart.Bar>();
        var calls = new List<double>();
        for (var d = today.AddDays(-(HistoryDays - 1)); d <= today; d = d.AddDays(1))
        {
            var list = byDay.TryGetValue(d, out var l) ? l : [];
            var s = UsageLog.Summarize(list);
            var dollars = UsageStats.EstimateDollars(s, prices);
            var label = d == today ? L.T("сегодня") : d.ToString("d MMM", culture).TrimEnd('.');
            var tip = L.F("{0}: {1} токенов · {2} · вызовов: {3}", d.ToString("d MMMM", culture), Ui.N(s.EstimatedSavedTokens), UsageStats.Dollars(dollars), Ui.N(s.Calls));
            daily.Add(new BarChart.Bar(label, s.EstimatedSavedTokens, tip, d == today));
            calls.Add(s.Calls);
        }

        var cumulative = new List<double>();
        double acc = 0;
        foreach (var day in byDay.Keys.OrderBy(d => d).TakeLast(30))
        {
            acc += byDay[day].Sum(r => Math.Max(0, r.EstimatedSavedTokens));
            cumulative.Add(acc);
        }

        var recent = records.OrderByDescending(r => r.TimestampUtc).Take(FeedSize).ToList();
        var last = records.Count == 0 ? "" : records[^1].TimestampUtc.Ticks.ToString(culture);
        return new UsageView(UsageStats.Build(records, prices), UsageLog.Summarize(records), daily, calls, cumulative, recent,
            $"{records.Count}|{last}|{today:yyyyMMdd}");
    }

    private void ApplyUsage(UsageView v)
    {
        var today = v.Periods[0];
        var week = v.Periods[1];
        var total = v.Periods[2];

        _tileToday.Set(UsageStats.Dollars(today.Dollars), L.F("{0} токенов", Ui.N(today.Summary.EstimatedSavedTokens)));
        _tileCalls.Set(Ui.N(today.Summary.Calls),
            L.F("за 7 дней: {0}", Ui.N(week.Summary.Calls)) + (week.Summary.Failed > 0 ? L.F(" · ошибок: {0}", Ui.N(week.Summary.Failed)) : ""));
        _tileCalls.SetHistory(v.CallsPerDay);
        _tileTotal.Set(UsageStats.Dollars(total.Dollars), L.F("{0} токенов", Ui.Short(total.Summary.EstimatedSavedTokens)));
        _tileTotal.SetHistory(v.Cumulative);

        _daily.SetData(v.Daily);
        _byTool.SetData(v.All.CallsByTool.Select(kv => new BarList.Row(ShortToolName(kv.Key), kv.Value, Ui.N(kv.Value))));
        _byClient.SetData(v.All.CallsByClient.Select(kv => new BarList.Row(kv.Key, kv.Value, Ui.N(kv.Value))));
        FillFeed(v.Recent);

        var all = v.All;
        _summary.Text = all.Calls == 0
            ? L.T("Обращений пока не было. Подключите Offload к IDE в разделе «Интеграции» и попросите Claude: «используй offload, чтобы …».")
            : L.F("Всего обработано локально: запрос {0} + ответ {1} токенов, время работы модели {2}",
                  Ui.N(all.PromptTokens), Ui.N(all.CompletionTokens), FileUtil.FormatDuration(all.TotalDuration)) +
              (all.Failed > 0 ? L.F(", с ошибкой завершились {0} вызовов.", Ui.N(all.Failed)) : ".");
    }

    private static string ShortToolName(string tool)
    {
        var name = Texts.ToolName(tool);
        var i = name.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? name[..i] : name;
    }

    private void FillFeed(IReadOnlyList<UsageRecord> recent)
    {
        var today = DateTime.Now.Date;
        _feed.BeginUpdate();
        _feed.Items.Clear();
        foreach (var r in recent)
        {
            var t = r.TimestampUtc.ToLocalTime();
            var item = new ListViewItem(t.Date == today ? t.ToString("HH:mm:ss") : t.ToString("dd.MM HH:mm"))
            {
                UseItemStyleForSubItems = false,
                ToolTipText = r.Model is null ? null : L.F("Модель: {0}", r.Model),
            };
            item.SubItems.Add(ShortToolName(r.Tool));
            item.SubItems.Add(r.Client ?? "—");
            item.SubItems.Add(Ui.Short(r.PromptTokens + r.CompletionTokens));
            item.SubItems.Add(r.EstimatedSavedTokens > 0 ? Ui.Short(r.EstimatedSavedTokens) : "—");
            item.SubItems.Add(Duration(r.DurationMs));
            var result = item.SubItems.Add(r.Ok ? L.T("✓ готово") : L.T("ошибка"));
            result.ForeColor = r.Ok ? Theme.OkText : Theme.ErrorText;
            _feed.Items.Add(item);
        }
        _feed.EndUpdate();
    }

    private static string Duration(long ms) =>
        ms < 1000 ? L.F("{0} мс", ms)
        : ms < 60_000 ? L.F("{0:0.#} с", ms / 1000d)
        : FileUtil.FormatDuration(TimeSpan.FromMilliseconds(ms));

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
                _tileVram.Fraction = f;
                _tileVram.Set(FileUtil.FormatBytes(used), L.F("из {0} · {1:P0}", FileUtil.FormatBytes(total), f), Theme.LoadColor(f));
            }
            else
            {
                _tileVram.Fraction = null;
                _tileVram.Set(L.T("нет данных"), L.T("только для видеокарт NVIDIA"), Theme.Gray);
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

    // ---------- Действия ----------

    private async Task StartAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (!await Shell.Server.StartAsync()) ShowStartError();
        }, L.T("Не удалось запустить сервер"));
    }

    private void ShowStartError()
    {
        var server = Shell.Server;
        if (server.State == ServerState.NotConfigured)
        {
            if (Ui.Confirm(Owner, L.F("{0}{1}{1}Открыть мастер настройки?", server.LastError, Environment.NewLine)))
                Shell.ShowSetupWizard();
            return;
        }
        Ui.ShowError(Owner, L.T("Сервер llama.cpp не запустился"), server.LastError ?? L.T("Подробности — в разделе «Журнал» (источник llama-server)."));
    }

    private async Task CheckModelAsync()
    {
        _checking = true;
        _checkResult.Visible = true;
        _checkResult.ForeColor = Theme.TextMuted;
        _checkResult.Text = L.T("Отправляю тестовый запрос модели…");
        try
        {
            await RunBusyAsync(async () =>
            {
                if (Shell.Server.State != ServerState.Running)
                {
                    _checkResult.Text = L.T("Запускаю сервер…");
                    if (!await Shell.Server.StartAsync())
                    {
                        _checkResult.Text = "";
                        ShowStartError();
                        return;
                    }
                    _checkResult.Text = L.T("Отправляю тестовый запрос модели…");
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
                    _checkResult.Text = L.T("Модель не ответила за 3 минуты.");
                }
                catch (Exception ex)
                {
                    Log.Warn("ui", $"Проверка модели: {ex.Message}");
                    _checkResult.ForeColor = Theme.ErrorText;
                    _checkResult.Text = L.F("Проверка не удалась: {0}", Ui.FriendlyError(ex));
                }
            }, L.T("Не удалось проверить модель"));
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
            Shell.Notify(L.T("Ключ API скопирован"), L.T("Ключ llama-server скопирован в буфер обмена."), ToolTipIcon.Info, force: true);
        else
            Ui.Warn(Owner, L.T("Не удалось скопировать ключ в буфер обмена."));
    }

    private void ResetStats()
    {
        if (!Ui.Confirm(Owner, L.T("Удалить всю накопленную статистику обращений к локальной модели?"), warning: true)) return;
        Ui.RunSafe(Owner, UsageLog.Clear, L.T("Не удалось сбросить статистику"));
        _usageSignature = null;
        _ = RefreshUsageAsync();
    }
}
