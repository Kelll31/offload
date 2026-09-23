using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.App.Forms.Wizard;

/// <summary>Шаг 2: определение оборудования и выбор сборки llama.cpp.</summary>
internal sealed class HardwareStep : WizardStep
{
    private readonly ProgressBar _spinner = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Height = 14, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
    private readonly Label _detecting = Kit.Label(L.T("Определение оборудования…"));
    private readonly Label _gpus = Kit.Wrap("—");
    private readonly Label _ram = Kit.Label("—");
    private readonly Label _cpu = Kit.Wrap("—");
    private readonly Label _disk = Kit.Label("—");
    private readonly Label _recommendTitle = Kit.Label("", Theme.Semibold(10f));
    private readonly Label _recommendReason = Kit.Wrap("");
    private readonly ComboBox _backend = Kit.Combo(340);
    private readonly List<LlamaBackend> _backendValues = [];
    private readonly LinkLabel _redetect;
    private readonly TableLayoutPanel _progressRow;

    private bool _detected;
    private bool _running;
    private bool _loading;

    public HardwareStep(WizardContext ctx) : base(ctx)
    {
        _redetect = Kit.ActionLink(L.T("Определить заново"), () => _ = DetectAsync(refresh: true));
        _backend.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _backend.SelectedIndex < 0) return;
            State.Backend = _backendValues[_backend.SelectedIndex];
        };

        var root = Kit.Table();
        _progressRow = Kit.Table(0, 100);
        _progressRow.AddRow(_detecting, _spinner);
        root.AddRow(_progressRow);

        var grid = Kit.Grid();
        grid.AddField(L.T("Видеокарты:"), _gpus);
        grid.AddField(L.T("Оперативная память:"), _ram);
        grid.AddField(L.T("Процессор:"), _cpu);
        grid.AddField(L.T("Свободно на диске:"), _disk);
        root.AddRow(grid);
        root.AddRow(Kit.Flow(_redetect));

        root.AddRow(Kit.Section(L.T("Сборка llama.cpp")));
        var card = new CardPanel { ColumnCount = 1 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.AddRow(_recommendTitle);
        card.AddRow(_recommendReason);
        root.AddRow(card);
        var choose = Kit.Grid();
        choose.AddField(L.T("Установить сборку:"), _backend);
        root.AddRow(choose);
        root.AddRow(Kit.Hint(L.T(
            "Обычно лучше оставить рекомендуемую сборку. Vulkan работает почти на любой видеокарте; «Только процессор» — если видеокарты нет или она не поддерживается (медленно).")));
        SetContent(root);
    }

    public override string Title => L.T("Оборудование");

    public override string Heading => L.T("Оборудование компьютера");

    public override string? Subtitle => L.T("От видеокарты зависят сборка llama.cpp и то, какая модель поместится в память.");

    public override bool CanGoNext => _detected && !_running && _backend.SelectedIndex >= 0;

    public override void OnEnter()
    {
        if (!_detected && !_running) _ = DetectAsync(refresh: State.Hardware is null);
    }

    private async Task DetectAsync(bool refresh)
    {
        if (_running) return;
        _running = true;
        _progressRow.Visible = true;
        _detecting.Text = L.T("Определение оборудования…");
        _redetect.Enabled = false;
        RaiseNavigationChanged();
        HardwareInfo? hw = null;
        try
        {
            hw = await Ctx.Shell.Hardware.GetAsync(refresh);
        }
        catch (Exception ex)
        {
            Log.Error("wizard", "Не удалось определить оборудование", ex);
            _gpus.Text = L.F("Не удалось определить: {0}", Ui.FriendlyError(ex));
        }
        finally
        {
            _running = false;
            _redetect.Enabled = true;
        }
        if (IsDisposed) return;
        _progressRow.Visible = false;
        State.Hardware = hw;
        Display(hw);
        _detected = true;
        RaiseNavigationChanged();
    }

    private void Display(HardwareInfo? hw)
    {
        if (hw is not null)
        {
            _gpus.Text = hw.Gpus.Count == 0
                ? L.T("не найдены")
                : string.Join(Environment.NewLine, hw.Gpus.Select(Texts.Gpu));
            var avail = hw.AvailableRamBytes > 0 ? L.F(" (свободно {0})", FileUtil.FormatBytes(hw.AvailableRamBytes)) : "";
            _ram.Text = $"{FileUtil.FormatBytes(hw.TotalRamBytes)}{avail}";
            _cpu.Text = $"{hw.CpuName}, {Ui.Plural(hw.LogicalCores, "поток", "потока", "потоков")}{(hw.CpuHasAvx2 ? ", AVX2" : "")}{(hw.IsArm64 ? ", ARM64" : "")}";
        }
        var free = HardwareDetector.GetFreeDiskBytes(AppPaths.DataDir);
        _disk.Text = free > 0 ? $"{FileUtil.FormatBytes(free)} ({Path.GetPathRoot(AppPaths.DataDir)})" : L.T("неизвестно");

        var rec = hw is null
            ? new Offload.Llama.BackendRecommendation(LlamaBackend.Vulkan, L.T("Оборудование не определено — Vulkan работает на большинстве видеокарт."))
            : Texts.RecommendBackend(hw);
        State.RecommendedBackend = rec.Backend;
        _recommendTitle.Text = L.F("Рекомендуется: {0}", Texts.Backend(rec.Backend));
        _recommendReason.Text = rec.ReasonRu;

        _loading = true;
        try
        {
            _backend.Items.Clear();
            _backendValues.Clear();
            IReadOnlyList<LlamaBackend> list = hw is null
                ? [LlamaBackend.Cuda12, LlamaBackend.Cuda13, LlamaBackend.Vulkan, LlamaBackend.Rocm, LlamaBackend.Sycl, LlamaBackend.Cpu]
                : Texts.AvailableBackends(hw);
            foreach (var b in list)
            {
                _backendValues.Add(b);
                _backend.Items.Add(b == rec.Backend ? L.F("{0} (рекомендуется)", Texts.Backend(b)) : Texts.Backend(b));
            }
            if (!_backendValues.Contains(rec.Backend))
            {
                _backendValues.Insert(0, rec.Backend);
                _backend.Items.Insert(0, L.F("{0} (рекомендуется)", Texts.Backend(rec.Backend)));
            }

            // Повторный запуск мастера: предлагаем уже установленную сборку.
            var cfg = ConfigStore.Current;
            var preferred = cfg.Llama.InstalledBackend != LlamaBackend.Auto ? cfg.Llama.InstalledBackend
                : cfg.Llama.Backend != LlamaBackend.Auto ? cfg.Llama.Backend
                : rec.Backend;
            var idx = _backendValues.IndexOf(preferred);
            if (idx < 0) idx = _backendValues.IndexOf(rec.Backend);
            _backend.SelectedIndex = Math.Max(0, idx);
            State.Backend = _backendValues[_backend.SelectedIndex];
        }
        finally
        {
            _loading = false;
        }
    }
}
