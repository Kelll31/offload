using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Hardware;
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
    private readonly Label _gpus = Kit.Wrap("");
    private readonly Button _tune;
    private readonly Button _tuneCancel;
    private readonly Button _tuneReset;
    private readonly Label _tuneStatus = Kit.Wrap("");
    private readonly Label _tuneDetails = Kit.Hint("");

    private PerformanceSnapshot? _snapshot;
    private int _version;
    private bool _measuring;
    private bool _pageBusy;
    private CancellationTokenSource? _tuneCts;
    /// <summary>Итог последнего автоподбора в этом окне (null — показывать сохранённый профиль).</summary>
    private (string Text, Color Color)? _tuneOutcome;

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
        _gpus.Visible = false;
        _tune = Kit.Button(L.T("Автоподбор параметров"), async (_, _) => await TuneAsync(), 170);
        _tuneCancel = Kit.Button(L.T("Отменить"), (_, _) => _tuneCts?.Cancel());
        _tuneCancel.Visible = false;
        _tuneReset = Kit.Button(L.T("Сбросить подбор"), async (_, _) => await ResetTuneAsync(), 130);
        _tuneReset.Visible = false;
    }

    /// <summary>Добавить блок строками в таблицу страницы.</summary>
    public void AddTo(TableLayoutPanel root)
    {
        root.AddRow(Kit.Section(L.T("Слоты и производительность")));
        root.AddRow(_fit);
        root.AddRow(_mode);
        root.AddRow(_gpus);
        root.AddRow(_vramWarning);
        var slotsRow = Kit.Table(100, 0);
        slotsRow.AddRow(_slots, _applySlots);
        root.AddRow(slotsRow);
        root.AddRow(Kit.Hint(L.T("Слоты — это одновременные запросы: субагенты и несколько IDE не ждут друг друга в очереди. Каждый слот резервирует свой KV-кэш (у гибридных моделей он небольшой), а при одновременной работе скорость генерации делится между запросами.")));
        root.AddRow(Kit.Flow(_measure));
        root.AddRow(_bench);
        root.AddRow(_benchDetails);
        var tuneRow = Kit.Flow(_tune, _tuneCancel, _tuneReset);
        tuneRow.Margin = new Padding(0, 8, 0, 0);
        root.AddRow(tuneRow);
        root.AddRow(Kit.Hint(L.T("Автоподбор перебирает --n-cpu-moe, -ub/-b, flash attention, тип KV-кэша, а при нескольких видеокартах и MTP — ещё их режимы. Для каждой пробы сервер перезапускается, всё займёт до 20 минут; контекст не уменьшается. Лучший набор сохраняется для этой модели и этого компьютера и применяется при запуске; флаги из «Доп. аргументов» важнее.")));
        root.AddRow(_tuneStatus);
        root.AddRow(_tuneDetails);
        ShowBenchmark(ConfigStore.Current);
    }

    public void UpdateUiState(bool pageBusy)
    {
        _pageBusy = pageBusy;
        var state = _shell.Server.State;
        var tuning = _tuneCts is not null;
        // Удалённый сервер: замер попал бы под id локальной модели, а автоподбор перезапускает свой процесс — оба недоступны.
        var local = !ConfigStore.Current.IsRemote();
        _measure.Enabled = local && state == ServerState.Running && !pageBusy && !_measuring && !tuning;
        _applySlots.Enabled = !pageBusy;
        _tune.Enabled = local && state is ServerState.Running or ServerState.Stopped or ServerState.Failed && !pageBusy && !_measuring && !tuning;
        _tuneCancel.Visible = tuning;
        _tuneCancel.Enabled = tuning;
        _tuneReset.Enabled = !pageBusy && !tuning;
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
            var running = _shell.Server.State is ServerState.Running or ServerState.Starting;
            var pid = running ? _shell.Server.ProcessId : null;
            var snapshot = await ServerAutoPlacement.SnapshotAsync(hw, cfg, pid, running ? _shell.Server.Plan?.Split : null);
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
            _gpus.Visible = false;
            ShowTune(null);
            return;
        }

        _fit.Text = $"{Texts.FitGlyph(s.Fit.Level)} {s.Fit.Explanation}";
        _fit.ForeColor = Texts.FitColor(s.Fit.Level);
        _mode.Text = ModeText(s, settings);
        _gpus.Visible = s.Split is not null;
        if (s.Split is { } split)
            _gpus.Text = split.IsMulti
                ? L.F("Видеокарты: {0} — модель делится между ними (--tensor-split), основная — {1}.", split.Describe(),
                    GpuSplit.ShortName(split.Devices[Math.Clamp(split.MainIndex, 0, split.Devices.Count - 1)].Description))
                : L.F("Видеокарта: используется только {0} (--device {1}).", split.Describe(), split.DeviceArg);
        ShowTune(s);

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

    // ---------- Автоподбор параметров ----------

    private async Task TuneAsync()
    {
        if (_tuneCts is not null || ConfigStore.Current.ActiveModel() is null) return;
        if (!Ui.Confirm(_host.FindForm(), L.T("Автоподбор несколько раз перезапустит сервер с разными параметрами и замерит скорость — это займёт до 20 минут. Запросы из IDE в это время могут ждать или искажать замер. Начать?")))
            return;

        using var cts = new CancellationTokenSource();
        _tuneCts = cts;
        _tuneOutcome = null;
        _tuneStatus.ForeColor = Theme.TextMuted;
        _tuneStatus.Text = L.T("Подготовка автоподбора…");
        _tuneDetails.Text = "";
        UpdateUiState(_pageBusy);
        AutoTuneOutcome? outcome = null;
        var progress = new Progress<AutoTuneProgress>(p =>
        {
            if (_host.IsDisposed || _tuneCts is null) return;
            _tuneStatus.Text = p.Confirming
                ? L.F("Проба {0} из {1} — повторный замер лучшего набора: {2}", p.Trial, p.MaxTrials, p.Candidate.Describe())
                : L.F("Проба {0} из {1}: {2}", p.Trial, p.MaxTrials, p.Candidate.Describe());
        });
        try
        {
            await _runBusy(async () => outcome = await _shell.Server.AutoTuneAsync(progress, cts.Token),
                L.T("Автоподбор не выполнен"), [_tune, _measure]);
        }
        finally
        {
            _tuneCts = null;
            if (!_host.IsDisposed)
            {
                _tuneOutcome = outcome is null ? null : DescribeOutcome(outcome);
                ShowTune(_snapshot);
                UpdateUiState(_pageBusy);
                _ = RefreshAsync();
            }
        }
    }

    private (string Text, Color Color) DescribeOutcome(AutoTuneOutcome o)
    {
        var r = o.Result;
        (string Text, Color Color) result;
        if (o.Cancelled)
            result = (L.T("Автоподбор отменён — сервер запущен с прежними параметрами."), Theme.TextMuted);
        else if (o.Error is { } error)
            result = (L.F("Автоподбор прерван ошибкой: {0}. Сервер запущен с прежними параметрами.", Ui.FriendlyError(error)), Theme.WarnText);
        else if (r?.BaselineError is { } baseError)
            result = (L.F("Сервер не запустился с текущими параметрами, подбирать не от чего: {0}", baseError), Theme.WarnText);
        else if (o.Applied && r is { Winner: { } winner })
            result = (L.F("Готово: типичный запрос ≈{0} с → ≈{1} с, быстрее на {2} %. Параметры: {3}",
                Sec(r.BaselineSeconds ?? 0), Sec(r.WinnerSeconds ?? 0), Pct(r.Gain), winner.Describe()), Theme.OkText);
        else if (r is { Winner: not null })
            result = (L.T("Лучший набор не запустился повторно — оставлены прежние параметры."), Theme.WarnText);
        else
            result = (L.F("Текущие параметры уже близки к лучшим: ни один вариант не оказался быстрее хотя бы на 3 % ({0}).",
                Ui.Plural(r?.Trials.Count ?? 0, "проба", "пробы", "проб")), Theme.TextPrimary);
        if (!o.Restored)
            result = (result.Text + " " + L.F("Сервер не запустился: {0}", _shell.Server.LastError ?? L.T("подробности в журнале llama-server.")), Theme.ErrorText);
        return result;
    }

    /// <summary>Итог последнего подбора и сохранённый профиль для активной модели на этом компьютере.</summary>
    private void ShowTune(PerformanceSnapshot? s)
    {
        if (_tuneCts is not null) return;
        _tuneReset.Visible = s?.Tuned is not null;
        if (_tuneOutcome is { } outcome)
        {
            _tuneStatus.ForeColor = outcome.Color;
            _tuneStatus.Text = outcome.Text;
        }
        else
        {
            _tuneStatus.Text = "";
        }

        if (s?.Tuned is not { } p)
        {
            _tuneDetails.Text = s is null ? "" : L.T("Для этой модели на этом компьютере автоподбор ещё не выполнялся.");
            return;
        }
        var gain = p.BaselineSeconds > 0 ? 1 - p.TunedSeconds / p.BaselineSeconds : 0;
        _tuneDetails.Text = s.TunedCurrent
            ? L.F("Подобрано {0}: {1} — типичный запрос ≈{2} → ≈{3} с (быстрее на {4} %), применяется при запуске.",
                p.TunedAtUtc.ToLocalTime().ToString("g", L.Culture), LlamaAutoTune.Describe(p), Sec(p.BaselineSeconds), Sec(p.TunedSeconds), Pct(gain))
            : L.T("Подобранные параметры не применяются: настройки сервера изменились после подбора. Повторите автоподбор.");
    }

    private async Task ResetTuneAsync()
    {
        if (_tuneCts is not null || ConfigStore.Current.ActiveModel() is not { } model) return;
        var owner = _host.FindForm();
        string key;
        try
        {
            key = await ServerAutoPlacement.HardwareKeyAsync();
        }
        catch (Exception ex)
        {
            Ui.ShowError(owner, L.T("Не удалось сбросить подобранные параметры"), ex);
            return;
        }
        if (!Ui.RunSafe(owner, () => LlamaAutoTune.RemoveProfile(model.Id, key), L.T("Не удалось сбросить подобранные параметры"))) return;
        _tuneOutcome = null;
        _shell.ConfigChanged();
        if (_shell.Server.State == ServerState.Running
            && Ui.Confirm(owner, L.T("Подобранные параметры сброшены. Перезапустить сервер, чтобы вернуться к обычным параметрам?")))
        {
            await _runBusy(async () =>
            {
                if (!await _shell.Server.RestartAsync())
                    Ui.ShowError(owner, L.T("Сервер не запустился"), _shell.Server.LastError ?? "");
            }, L.T("Не удалось перезапустить сервер"), [_tuneReset]);
        }
        if (!_host.IsDisposed) _ = RefreshAsync();
    }

    private static string Sec(double seconds) => seconds.ToString(seconds >= 10 ? "0" : "0.0", L.Culture);

    private static string Pct(double gain) => Math.Round(Math.Max(0, gain) * 100).ToString("0", L.Culture);

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
