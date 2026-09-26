using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Llama;
using Offload.Models;

namespace Offload.App.Forms.Pages;

/// <summary>
/// Блок «Слоты и производительность» страницы «Сервер»: размещение «Авто» (то же, что получит llama-server при запуске),
/// предупреждение о видеопамяти, занятой другими программами, рекомендация числа слотов и замер скорости.
/// </summary>
internal sealed class ServerPerformanceSection
{
    private const double GiB = 1024d * 1024 * 1024;

    private readonly IAppShell _shell;
    private readonly Control _host;
    private readonly Func<int, Task> _applyParallel;
    private readonly Func<Func<Task>, string, Control[], Task<bool>> _runBusy;

    private readonly Label _fit = Kit.Wrap("");
    private readonly Label _mode = Kit.Hint("");
    private readonly Label _vramWarning = Kit.Wrap("");
    private readonly Label _slots = Kit.Wrap("");
    private readonly Button _applySlots;
    private readonly Label _bench = Kit.Wrap("");
    private readonly Label _benchDetails = Kit.Hint("");
    private readonly Button _measure;

    private PerformanceSnapshot? _snapshot;
    private int _version;
    private bool _measuring;
    private bool _pageBusy;

    /// <param name="host">Страница: владелец диалогов и признак IsDisposed.</param>
    /// <param name="applyParallel">Подставить число слотов в настройки страницы (и сохранить, если других правок нет).</param>
    /// <param name="runBusy">RunBusyAsync страницы: долгая операция с блокировкой кнопок и показом ошибок.</param>
    public ServerPerformanceSection(IAppShell shell, Control host, Func<int, Task> applyParallel,
        Func<Func<Task>, string, Control[], Task<bool>> runBusy)
    {
        _shell = shell;
        _host = host;
        _applyParallel = applyParallel;
        _runBusy = runBusy;
        _vramWarning.ForeColor = Theme.WarnText;
        _vramWarning.Visible = false;
        _applySlots = Kit.Button(L.T("Применить"), async (_, _) => await ApplySlotsAsync());
        _applySlots.Visible = false;
        _measure = Kit.Button(L.T("Измерить скорость"), async (_, _) => await MeasureAsync(), 150);
    }

    /// <summary>Добавить блок строками в таблицу страницы.</summary>
    public void AddTo(TableLayoutPanel root)
    {
        root.AddRow(Kit.Section(L.T("Слоты и производительность")));
        root.AddRow(_fit);
        root.AddRow(_mode);
        root.AddRow(_vramWarning);
        var slotsRow = Kit.Table(100, 0);
        slotsRow.AddRow(_slots, _applySlots);
        root.AddRow(slotsRow);
        root.AddRow(Kit.Hint(L.T("Слоты — это одновременные запросы: субагенты и несколько IDE не ждут друг друга в очереди. Каждый слот резервирует свой KV-кэш (у гибридных моделей он небольшой), а при одновременной работе скорость генерации делится между запросами.")));
        root.AddRow(Kit.Flow(_measure));
        root.AddRow(_bench);
        root.AddRow(_benchDetails);
        ShowBenchmark(ConfigStore.Current);
    }

    public void UpdateUiState(bool pageBusy)
    {
        _pageBusy = pageBusy;
        var running = _shell.Server.State == ServerState.Running;
        _measure.Enabled = running && !pageBusy && !_measuring;
        _applySlots.Enabled = !pageBusy;
    }

    /// <summary>Пересчитать оценку (в фоне; устаревшие результаты отбрасываются).</summary>
    public async Task RefreshAsync()
    {
        var version = ++_version;
        var cfg = ConfigStore.Current;
        ShowBenchmark(cfg);
        UpdateUiState(_pageBusy);
        try
        {
            var hw = await _shell.Hardware.GetAsync();
            var pid = _shell.Server.State is ServerState.Running or ServerState.Starting ? _shell.Server.ProcessId : null;
            var snapshot = await ServerAutoPlacement.SnapshotAsync(hw, cfg, pid);
            if (version != _version || _host.IsDisposed) return;
            Show(snapshot, cfg.Server);
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Оценка размещения модели: {ex.Message}");
        }
    }

    private void Show(PerformanceSnapshot? s, ServerSettings settings)
    {
        _snapshot = s;
        if (s is null)
        {
            _fit.Text = L.T("Оценка недоступна: не выбрана модель или нет данных о её архитектуре.");
            _fit.ForeColor = Theme.TextMuted;
            _mode.Text = "";
            _vramWarning.Visible = false;
            _slots.Text = "";
            _applySlots.Visible = false;
            return;
        }

        _fit.Text = $"{Texts.FitGlyph(s.Fit.Level)} {s.Fit.Explanation}";
        _fit.ForeColor = Texts.FitColor(s.Fit.Level);
        _mode.Text = ModeText(s, settings);

        _vramWarning.Visible = s.Usage is { OthersSignificant: true };
        if (_vramWarning.Visible)
            _vramWarning.Text = L.F("⚠ Другие программы занимают ≈{0} ГБ видеопамяти — модели доступно меньше, оценка это учитывает. Закройте их (игры, браузер с аппаратным ускорением, другие модели), чтобы модель не выгружалась в общую память.",
                Gb(s.Usage!.OtherBytes));

        var advice = s.Slots;
        var chosen = advice.Chosen;
        var total = s.Hardware.PrimaryVramBytes;
        var current = Math.Clamp(settings.Parallel, 1, ServerSettings.MaxParallel);
        var slotsText = advice.Recommended > 1
            ? L.F("Рекомендуется: {0} × {1} — видеопамять ≈{2}/{3} ГБ.", Ui.Plural(advice.Recommended, "слот", "слота", "слотов"),
                Ui.Tokens(advice.ContextPerSlot), Gb(chosen.Fit.EstimatedVramBytes + s.OtherVramBytes), Gb(total))
            : s.Fit.Level is FitLevel.FullGpu or FitLevel.MoeOffload
                ? L.F("Рекомендуется 1 слот: второй слот с контекстом {0} не помещается в видеопамять без замедления.", Ui.Tokens(advice.ContextPerSlot))
                : L.T("Рекомендуется 1 слот: модель работает не целиком на видеокарте, параллельные запросы её замедлят.");
        _slots.Text = slotsText + " " + L.F("Сейчас: {0}.", Ui.Plural(current, "слот", "слота", "слотов"));
        _applySlots.Visible = advice.Recommended != current;
    }

    private static string ModeText(PerformanceSnapshot s, ServerSettings settings)
    {
        if (s.Placement is { } p)
        {
            var moe = p.CpuMoeLayers > 0
                ? "--n-cpu-moe " + p.CpuMoeLayers.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : L.T("без выгрузки экспертов");
            return L.F("Режим «Авто»: при запуске llama-server получит контекст {0} на слот (-c {1}), {2}.",
                Ui.Tokens(p.ContextPerSlot), Ui.N((long)p.ContextPerSlot * Math.Clamp(settings.Parallel, 1, ServerSettings.MaxParallel)), moe);
        }
        return ServerFit.IsAutoPlacement(settings)
            ? L.T("Режим «Авто»: подходящего размещения не нашлось — его выберет сам llama-server (--fit).")
            : L.T("Контекст или выгрузка MoE заданы вручную — они передаются llama-server как есть.");
    }

    private async Task ApplySlotsAsync()
    {
        if (_snapshot is not { } s) return;
        await _applyParallel(s.Slots.Recommended);
    }

    // ---------- Замер скорости ----------

    private async Task MeasureAsync()
    {
        var cfg = ConfigStore.Current;
        if (cfg.ActiveModel() is not { } model || _shell.Server.State != ServerState.Running) return;
        _measuring = true;
        _bench.ForeColor = Theme.TextMuted;
        _bench.Text = L.T("Идёт замер скорости…");
        _benchDetails.Text = "";
        UpdateUiState(_pageBusy);
        try
        {
            var ok = await _runBusy(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                _shell.Server.MarkActivity();
                await LlamaBenchmark.MeasureAndSaveAsync(LlamaClient.FromConfig(cfg), model.Id, _shell.Server.Plan, cts.Token);
            }, L.T("Не удалось измерить скорость"), [_measure]);
            if (!ok) Log.Info("ui", "Замер скорости не выполнен");
        }
        finally
        {
            _measuring = false;
            if (!_host.IsDisposed)
            {
                ShowBenchmark(ConfigStore.Current);
                UpdateUiState(_pageBusy);
            }
        }
    }

    private void ShowBenchmark(AppConfig cfg)
    {
        if (_measuring) return;
        var model = cfg.ActiveModel();
        var last = LlamaBenchmark.Last(cfg, model?.Id);
        if (last is null)
        {
            _bench.ForeColor = Theme.TextMuted;
            _bench.Text = _shell.Server.State == ServerState.Running
                ? L.T("Для этой модели замеров ещё не было. Короткий фиксированный запрос к запущенному серверу займёт от 10 секунд до минуты.")
                : L.T("Запустите сервер, чтобы измерить скорость модели.");
            _benchDetails.Text = "";
            return;
        }
        _bench.ForeColor = Theme.TextPrimary;
        _bench.Text = L.F("Обработка промпта: {0} ток/с · генерация: {1} ток/с", Tps(last.PromptTokensPerSecond), Tps(last.GenerationTokensPerSecond));
        _benchDetails.Text = L.F("Замер {0}: контекст {1} × {2}, --n-cpu-moe {3}, llama.cpp {4}. Запросы из IDE во время замера занижают результат.",
            last.MeasuredAtUtc.ToLocalTime().ToString("g", L.Culture),
            last.ContextSize > 0 ? Ui.Tokens(last.ContextSize) : "—",
            Ui.Plural(Math.Max(1, last.Parallel), "слот", "слота", "слотов"),
            last.CpuMoeLayers < 0 ? L.T("авто") : last.CpuMoeLayers.ToString(L.Culture),
            string.IsNullOrWhiteSpace(last.LlamaTag) ? "—" : last.LlamaTag);
    }

    private static string Tps(double value) => value.ToString(value >= 100 ? "#,0" : "0.#", L.Culture);

    private static string Gb(long bytes) => Math.Round(bytes / GiB, 1).ToString("0.#", L.Culture);
}
