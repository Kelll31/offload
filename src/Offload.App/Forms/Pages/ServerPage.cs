using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Llama;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «Сервер»: параметры llama-server, установка/обновление llama.cpp, VC++ runtime, общие настройки программы.</summary>
internal sealed class ServerPage : PageBase
{
    private static (string Text, int Value)[] ContextOptions =>
    [
        (L.T("Авто (по модели и видеопамяти)"), 0),
        ("8K (8 192)", 8192),
        ("16K (16 384)", 16384),
        ("32K (32 768)", 32768),
        ("64K (65 536)", 65536),
        ("128K (131 072)", 131072),
        ("256K (262 144)", 262144),
    ];

    private static (string Text, string Value)[] FlashOptions =>
    [
        (L.T("Авто"), "auto"),
        (L.T("Включено"), "on"),
        (L.T("Выключено"), "off"),
    ];

    private static (string Text, string Value)[] CacheOptions =>
    [
        (L.T("f16 — без сжатия (больше памяти)"), "f16"),
        (L.T("q8_0 — рекомендуется"), "q8_0"),
        (L.T("q4_0 — экономия памяти"), "q4_0"),
    ];

    private readonly NumericUpDown _port = Kit.Number(1024, 65535, 8765, 100);
    private readonly ComboBox _context = Kit.Combo(250);
    private readonly List<int> _contextValues = [];
    private readonly NumericUpDown _parallel = Kit.Number(1, 4, 1, 70);
    private readonly OptionalNumberBox _gpuLayers = new([(L.T("Авто (все слои)"), -1), (L.T("Своё число"), null)], 0, 999, 99);
    private readonly OptionalNumberBox _cpuMoe = new([(L.T("Авто"), -1), (L.T("Выключено"), 0), (L.T("Своё число"), null)], 1, 999, 8);
    private readonly ComboBox _flash = Kit.Combo(170);
    private readonly ComboBox _cache = Kit.Combo(250);
    private readonly NumericUpDown _threads = Kit.Number(0, 256, 0, 70);
    private readonly NumericUpDown _idle = Kit.Number(0, 1440, 0, 70);
    private readonly TextBox _extra = Kit.TextBox();
    private readonly CheckBox _autoStart = Kit.Check(L.T("Запускать сервер при старте Offload"));
    private readonly CheckBox _mtp = Kit.Check(L.T("Ускорение MTP (экспериментально)"));
    private readonly Label _mtpHint = Kit.Hint("");
    private readonly Button _apply;
    private readonly Label _applyStatus = Kit.Hint("", autoWidth: true);

    private readonly Label _llamaInstalled = Kit.Label("", Theme.Semibold(9f));
    private readonly Label _llamaPath = Kit.Hint("");
    private readonly ComboBox _backend = Kit.Combo(330);
    private readonly List<LlamaBackend> _backendValues = [];
    private readonly Button _checkUpdates;
    private readonly Button _reinstall;
    private readonly Label _updateStatus = Kit.Wrap("");
    private readonly ProgressPanel _llamaProgress = new();

    private readonly Label _vcStatus = Kit.Label("");
    private readonly Button _vcInstall;
    private readonly ProgressPanel _vcProgress = new(withCancel: false);

    private readonly CheckBox _llamaCheckUpdates = Kit.Check(L.T("Проверять обновления llama.cpp при запуске"));

    private bool _loading;
    private bool _dirty;
    private HardwareInfo? _hw;
    private CancellationTokenSource? _installCts;
    private string? _busyText;

    public ServerPage(IAppShell shell) : base(shell)
    {
        foreach (var (text, _) in FlashOptions) _flash.Items.Add(text);
        foreach (var (text, _) in CacheOptions) _cache.Items.Add(text);
        _extra.Width = 420;
        _extra.Anchor = AnchorStyles.Left;
        _port.ThousandsSeparator = false;
        _extra.PlaceholderText = L.T("например: --top-n-sigma 1.5");

        _apply = Kit.Primary(L.T("Применить"), async (_, _) => await ApplyAsync());
        _checkUpdates = Kit.Button(L.T("Проверить обновления"), async (_, _) => await CheckUpdatesAsync(), 150);
        _reinstall = Kit.Button(L.T("Переустановить / обновить"), async (_, _) => await ReinstallAsync(confirm: true), 170);
        _vcInstall = Kit.Button(L.T("Установить"), async (_, _) => await InstallVcAsync());
        _llamaProgress.CancelRequested += (_, _) => _installCts?.Cancel();

        var root = Kit.Table();

        // Параметры сервера.
        root.AddRow(Kit.Section(L.T("Параметры llama-server"), first: true));
        var grid = Kit.Grid();
        grid.AddField(L.T("Порт:"), _port, L.T("адрес API: http://127.0.0.1:<порт>/v1"));
        grid.AddField(L.T("Контекст:"), _context, L.T("больше контекст — больше видеопамяти"));
        grid.AddField(L.T("Параллельные запросы:"), _parallel, L.T("каждый запрос получает свою долю контекста"));
        grid.AddField(L.T("Слои на GPU:"), _gpuLayers);
        grid.AddField(L.T("MoE-эксперты на ЦП:"), _cpuMoe, L.T("для MoE-моделей, которые не помещаются в видеопамять"));
        grid.AddField("Flash attention:", _flash);
        grid.AddField(L.T("Тип KV-кэша:"), _cache, L.T("q4_0 может ухудшить вызов инструментов"));
        grid.AddField(L.T("Потоки ЦП:"), _threads, L.T("0 — автоматически"));
        grid.AddField(L.T("Выгрузка при простое:"), _idle, L.T("минут; 0 — никогда. Модель загрузится снова при обращении из IDE"));
        grid.AddField(L.T("Доп. аргументы:"), _extra);
        root.AddRow(grid);
        root.AddRow(_autoStart);
        root.AddRow(_mtp);
        _mtpHint.Margin = new Padding(20, 0, 0, 6);
        root.AddRow(_mtpHint);
        var applyRow = Kit.Flow(_apply, Kit.Button(L.T("По умолчанию"), (_, _) => ResetDefaults(), 110), _applyStatus);
        applyRow.Margin = new Padding(0, 8, 0, 4);
        root.AddRow(applyRow);

        // llama.cpp.
        root.AddRow(Kit.Section("llama.cpp"));
        var llamaGrid = Kit.Grid();
        llamaGrid.AddField(L.T("Установлено:"), _llamaInstalled);
        llamaGrid.AddField(L.T("Сборка:"), _backend);
        root.AddRow(llamaGrid);
        root.AddRow(_llamaPath);
        root.AddRow(Kit.Flow(_checkUpdates, _reinstall));
        root.AddRow(_llamaCheckUpdates);
        root.AddRow(_updateStatus);
        root.AddRow(_llamaProgress);

        root.AddRow(Kit.Section("Microsoft Visual C++ Redistributable"));
        root.AddRow(Kit.Hint(L.T("Библиотеки MSVCP140.dll и VCRUNTIME140.dll нужны для работы llama.cpp. Установка запросит права администратора.")));
        root.AddRow(Kit.Flow(_vcStatus, _vcInstall));
        root.AddRow(_vcProgress);

        // Тема, язык, автозапуск и уведомления — в разделе «Настройки».
        Controls.Add(Kit.Scroll(root));

        // Любое изменение параметров сервера — «несохранённые изменения».
        _port.ValueChanged += (_, _) => MarkDirty();
        _context.SelectedIndexChanged += (_, _) => MarkDirty();
        _parallel.ValueChanged += (_, _) => MarkDirty();
        _gpuLayers.ValueChanged += (_, _) => MarkDirty();
        _cpuMoe.ValueChanged += (_, _) => MarkDirty();
        _flash.SelectedIndexChanged += (_, _) => MarkDirty();
        _cache.SelectedIndexChanged += (_, _) => MarkDirty();
        _threads.ValueChanged += (_, _) => MarkDirty();
        _idle.ValueChanged += (_, _) => MarkDirty();
        _extra.TextChanged += (_, _) => MarkDirty();
        _autoStart.CheckedChanged += (_, _) => MarkDirty();
        _mtp.CheckedChanged += (_, _) => MarkDirty();

        _llamaCheckUpdates.CheckedChanged += (_, _) => SaveUi(c => c.Llama.CheckUpdates = _llamaCheckUpdates.Checked);

        LoadSettings(ConfigStore.Current.Server);
        LoadProgramSettings();
        UpdateLlamaInfo();
    }

    public override string Key => Tabs.Server;

    public override string Title => L.T("Сервер");

    public override string Subtitle => L.T("Параметры llama-server, сборка llama.cpp и поведение программы");

    public override string Glyph => Glyphs.Server;

    public override string? BusyDescription => _busyText;

    protected override async void OnActivated()
    {
        try
        {
            if (!_dirty) LoadSettings(ConfigStore.Current.Server);
            LoadProgramSettings();
            UpdateLlamaInfo();
            if (Shell.PendingLlamaUpdate is { UpdateAvailable: true } upd) ShowUpdate(upd);
            if (_hw is null)
            {
                try { _hw = await Shell.Hardware.GetAsync(); }
                catch (Exception ex) { Log.Warn("ui", $"Оборудование не определено: {ex.Message}"); }
                if (!IsDisposed) FillBackends();
            }
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Вкладка «Сервер»", ex);
        }
    }

    public override void OnConfigChanged()
    {
        if (!_dirty && !IsBusy) LoadSettings(ConfigStore.Current.Server);
        LoadProgramSettings();
        UpdateLlamaInfo();
    }

    public override void OnServerStateChanged() => UpdateUiState();

    protected override void UpdateUiState()
    {
        var installing = _installCts is not null;
        _reinstall.Enabled = !IsBusy && _backend.SelectedIndex >= 0;
        _checkUpdates.Enabled = !IsBusy;
        _backend.Enabled = !installing && _backendValues.Count > 0;
        _vcInstall.Enabled = !IsBusy;
        _apply.Enabled = !installing;
    }

    // ---------- Параметры сервера ----------

    private void LoadSettings(ServerSettings s)
    {
        _loading = true;
        try
        {
            _port.Value = Math.Clamp(s.Port, (int)_port.Minimum, (int)_port.Maximum);

            _context.Items.Clear();
            _contextValues.Clear();
            foreach (var (text, value) in ContextOptions)
            {
                _context.Items.Add(text);
                _contextValues.Add(value);
            }
            if (!_contextValues.Contains(s.ContextSize) && s.ContextSize > 0)
            {
                _context.Items.Add(L.F("{0} ({1}) — своё значение", Ui.Tokens(s.ContextSize), Ui.N(s.ContextSize)));
                _contextValues.Add(s.ContextSize);
            }
            _context.SelectedIndex = Math.Max(0, _contextValues.IndexOf(Math.Max(0, s.ContextSize)));

            _parallel.Value = Math.Clamp(s.Parallel, 1, 4);
            _gpuLayers.Value = s.GpuLayers < 0 ? -1 : s.GpuLayers;
            _cpuMoe.Value = s.CpuMoeLayers < 0 ? -1 : s.CpuMoeLayers;
            _flash.SelectedIndex = Math.Max(0, Array.FindIndex(FlashOptions, o => string.Equals(o.Value, s.FlashAttention, StringComparison.OrdinalIgnoreCase)));
            var cacheIdx = Array.FindIndex(CacheOptions, o => string.Equals(o.Value, s.CacheType, StringComparison.OrdinalIgnoreCase));
            _cache.SelectedIndex = cacheIdx < 0 ? 1 : cacheIdx;
            _threads.Value = Math.Clamp(s.Threads, 0, 256);
            _idle.Value = Math.Clamp(s.IdleUnloadMinutes, 0, 1440);
            _extra.Text = s.ExtraArgs ?? "";
            _autoStart.Checked = s.AutoStart;
            _mtp.Checked = s.EnableMtp;
            UpdateMtpHint();
        }
        finally
        {
            _loading = false;
        }
        _dirty = false;
        _applyStatus.Text = "";
    }

    private void UpdateMtpHint()
    {
        var model = ConfigStore.Current.ActiveModel();
        _mtpHint.Text = L.F("Спекулятивное декодирование встроенным MTP-слоем модели ускоряет генерацию. {0}",
                        model is null ? L.T("Активная модель не выбрана.")
                            : model.HasMtp ? L.F("Активная модель «{0}» поддерживает MTP.", Texts.ModelName(model))
                            : L.F("Активная модель «{0}» не содержит MTP-слоя — настройка не подействует.", Texts.ModelName(model)));
    }

    public override bool HasUnsavedChanges => _dirty;

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        _applyStatus.ForeColor = Theme.WarnText;
        _applyStatus.Text = L.T("Есть несохранённые изменения");
    }

    private void ResetDefaults()
    {
        var current = ConfigStore.Current.Server;
        var defaults = new ServerSettings { Host = current.Host, ApiKey = current.ApiKey };
        LoadSettings(defaults);
        MarkDirty();
    }

    private async Task ApplyAsync()
    {
        var extra = _extra.Text.Trim();
        if (extra.Length > 0)
        {
            try
            {
                LlamaServerArgs.SplitArgs(extra);
            }
            catch (NotImplementedException)
            {
                // Проверка разбора недоступна в этой сборке — сохраняем как есть.
            }
            catch (Exception ex)
            {
                Ui.ShowError(Owner, L.T("Некорректные дополнительные аргументы"), ex);
                return;
            }
        }

        var saved = Ui.RunSafe(Owner, () => ConfigStore.Update(c =>
        {
            var s = c.Server;
            s.Port = (int)_port.Value;
            s.ContextSize = _context.SelectedIndex >= 0 ? _contextValues[_context.SelectedIndex] : 0;
            s.Parallel = (int)_parallel.Value;
            s.GpuLayers = _gpuLayers.Value;
            s.CpuMoeLayers = _cpuMoe.Value;
            s.FlashAttention = FlashOptions[Math.Max(0, _flash.SelectedIndex)].Value;
            s.CacheType = CacheOptions[Math.Max(0, _cache.SelectedIndex)].Value;
            s.Threads = (int)_threads.Value;
            s.IdleUnloadMinutes = (int)_idle.Value;
            s.ExtraArgs = extra;
            s.AutoStart = _autoStart.Checked;
            s.EnableMtp = _mtp.Checked;
        }), L.T("Не удалось сохранить настройки"));
        if (!saved) return;

        Log.Info("ui", "Параметры сервера сохранены");
        _dirty = false;
        _applyStatus.ForeColor = Theme.OkText;
        _applyStatus.Text = L.T("Сохранено");
        Shell.ConfigChanged();

        if (Shell.Server.State is ServerState.Running or ServerState.Starting &&
            Ui.Confirm(Owner, L.T("Настройки сохранены. Перезапустить сервер, чтобы они вступили в силу?")))
        {
            await RunBusyAsync(async () =>
            {
                if (!await Shell.Server.RestartAsync())
                    Ui.ShowError(Owner, L.T("Сервер не запустился с новыми настройками"), Shell.Server.LastError ?? "");
            }, L.T("Не удалось перезапустить сервер"), _apply);
        }
    }

    // ---------- llama.cpp ----------

    private void UpdateLlamaInfo()
    {
        var cfg = ConfigStore.Current;
        var installed = Ui.Try(() => LlamaInstaller.IsInstalled(cfg), false, "IsInstalled");
        if (installed && !string.IsNullOrWhiteSpace(cfg.Llama.InstalledTag))
        {
            _llamaInstalled.Text = $"{cfg.Llama.InstalledTag} · {Texts.Backend(cfg.Llama.InstalledBackend)}";
            _llamaInstalled.ForeColor = Theme.TextPrimary;
            var exe = Ui.Try(() => LlamaInstaller.GetServerExePath(cfg), null, "GetServerExePath");
            _llamaPath.Text = exe ?? cfg.Llama.InstallDir ?? "";
        }
        else
        {
            _llamaInstalled.Text = L.T("не установлен");
            _llamaInstalled.ForeColor = Theme.WarnText;
            _llamaPath.Text = L.T("Выберите сборку и нажмите «Переустановить / обновить» или запустите мастер настройки.");
        }

        var vc = Ui.Try<bool?>(() => VcRuntime.IsInstalled(), null, "VcRuntime.IsInstalled");
        _vcStatus.Text = vc switch
        {
            true => L.T("✓ Установлен"),
            false => L.T("✗ Не установлен"),
            _ => L.T("Состояние неизвестно"),
        };
        _vcStatus.ForeColor = vc == true ? Theme.OkText : vc == false ? Theme.ErrorText : Theme.TextMuted;
        _vcInstall.Text = vc == true ? L.T("Переустановить") : L.T("Установить");
        UpdateUiState();
    }

    private void FillBackends()
    {
        var cfg = ConfigStore.Current;
        _backend.Items.Clear();
        _backendValues.Clear();
        IReadOnlyList<LlamaBackend> list = _hw is null
            ? [LlamaBackend.Cuda12, LlamaBackend.Cuda13, LlamaBackend.Vulkan, LlamaBackend.Rocm, LlamaBackend.Sycl, LlamaBackend.Cpu]
            : Texts.AvailableBackends(_hw);
        var recommended = _hw is null ? (LlamaBackend?)null : Texts.RecommendBackend(_hw).Backend;
        foreach (var b in list)
        {
            _backendValues.Add(b);
            _backend.Items.Add(b == recommended ? L.F("{0} (рекомендуется)", Texts.Backend(b)) : Texts.Backend(b));
        }
        var preferred = cfg.Llama.InstalledBackend != LlamaBackend.Auto ? cfg.Llama.InstalledBackend
            : cfg.Llama.Backend != LlamaBackend.Auto ? cfg.Llama.Backend
            : recommended ?? LlamaBackend.Cpu;
        var idx = _backendValues.IndexOf(preferred);
        if (idx < 0 && recommended is { } r) idx = _backendValues.IndexOf(r);
        _backend.SelectedIndex = idx >= 0 ? idx : _backendValues.Count > 0 ? 0 : -1;
        UpdateUiState();
    }

    private void ShowUpdate(LlamaUpdateInfo info)
    {
        if (info.UpdateAvailable)
        {
            _updateStatus.ForeColor = Theme.WarnText;
            _updateStatus.Text = L.F("Доступна новая версия llama.cpp: {0} (установлена {1}). Нажмите «Переустановить / обновить».", info.LatestTag, info.InstalledTag ?? "—");
        }
        else
        {
            _updateStatus.ForeColor = Theme.OkText;
            _updateStatus.Text = L.F("Установлена последняя версия ({0}).", info.LatestTag);
        }
    }

    private async Task CheckUpdatesAsync()
    {
        _updateStatus.ForeColor = Theme.TextMuted;
        _updateStatus.Text = L.T("Проверка обновлений…");
        var ok = await RunBusyAsync(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var info = await LlamaInstaller.CheckUpdateAsync(ConfigStore.Current, cts.Token);
            Shell.PendingLlamaUpdate = info.UpdateAvailable ? info : null;
            ShowUpdate(info);
        }, L.T("Не удалось проверить обновления llama.cpp"));
        if (!ok && _updateStatus.Text == L.T("Проверка обновлений…")) _updateStatus.Text = "";
    }

    /// <summary>Обновить llama.cpp (пункт меню трея «Обновить llama.cpp»).</summary>
    public async Task RunUpdateAsync()
    {
        if (IsBusy) return;
        if (_backendValues.Count == 0)
        {
            try { _hw ??= await Shell.Hardware.GetAsync(); }
            catch (Exception ex) { Log.Warn("ui", $"Оборудование не определено: {ex.Message}"); }
            FillBackends();
        }
        var installed = ConfigStore.Current.Llama.InstalledBackend;
        var idx = _backendValues.IndexOf(installed);
        if (idx >= 0) _backend.SelectedIndex = idx;
        await ReinstallAsync(confirm: true);
    }

    private async Task ReinstallAsync(bool confirm)
    {
        if (_backend.SelectedIndex < 0 || _backend.SelectedIndex >= _backendValues.Count || IsBusy) return;
        var backend = _backendValues[_backend.SelectedIndex];
        var name = Texts.Backend(backend);
        var server = Shell.Server;
        var wasRunning = server.State is ServerState.Running or ServerState.Starting;
        if (confirm && !Ui.Confirm(Owner,
                L.F("Скачать и установить последнюю версию llama.cpp (сборка «{0}»)?", name) +
                (wasRunning ? L.F("{0}{0}Сервер будет остановлен на время установки и запущен снова.", Environment.NewLine) : "")))
            return;

        using var cts = new CancellationTokenSource();
        _installCts = cts;
        _busyText = L.T("установка llama.cpp");
        _llamaProgress.Reset();
        _llamaProgress.Start(L.T("Подготовка установки llama.cpp…"));
        try
        {
            await RunBusyAsync(async () =>
            {
                try
                {
                    if (wasRunning) await server.StopAsync();
                    var result = await LlamaInstaller.InstallAsync(backend, _llamaProgress.CreateProgress(), cts.Token);
                    ConfigStore.Update(c => c.Llama.Backend = backend);
                    Shell.PendingLlamaUpdate = null;
                    _llamaProgress.Finish(L.F("Установлена llama.cpp {0} ({1}).", result.Tag, Texts.Backend(result.Backend)), true);
                    _updateStatus.Text = "";
                    Log.Info("llama", $"llama.cpp {result.Tag} ({result.Backend}) установлена в {result.InstallDir}");
                }
                catch (OperationCanceledException)
                {
                    _llamaProgress.Finish(L.T("Установка отменена."), false);
                    throw;
                }
                catch (LlamaVcRuntimeMissingException ex)
                {
                    _llamaProgress.Finish(ex.Message, false);
                    if (Ui.Confirm(Owner, L.F("{0}{1}{1}Установить Visual C++ Redistributable сейчас?", ex.Message, Environment.NewLine)))
                        await VcRuntime.InstallAsync(_vcProgress.CreateProgress(), cts.Token);
                    return;
                }
                catch (Exception ex)
                {
                    _llamaProgress.Finish(L.F("Ошибка установки: {0}", Ui.FriendlyError(ex)), false);
                    throw;
                }
            }, L.T("Не удалось установить llama.cpp"));
        }
        finally
        {
            _installCts = null;
            _busyText = null;
        }

        Shell.ConfigChanged();
        UpdateLlamaInfo();
        if (wasRunning || server.State == ServerState.Failed)
        {
            await RunBusyAsync(async () =>
            {
                if (!await server.StartAsync())
                    Ui.ShowError(Owner, L.T("Сервер не запустился после установки llama.cpp"), server.LastError ?? "");
            }, L.T("Не удалось запустить сервер"));
        }
    }

    private async Task InstallVcAsync()
    {
        _vcProgress.Reset();
        _vcProgress.Start(L.T("Загрузка Visual C++ Redistributable…"));
        _busyText = L.T("установка Visual C++ Redistributable");
        try
        {
            await RunBusyAsync(async () =>
            {
                var ok = await VcRuntime.InstallAsync(_vcProgress.CreateProgress());
                if (ok)
                {
                    _vcProgress.Finish(L.T("Visual C++ Redistributable установлен."), true);
                }
                else
                {
                    _vcProgress.Finish(L.T("Установка не завершена."), false);
                    Ui.Warn(Owner, L.T("Visual C++ Redistributable не установлен. Возможно, запрос прав администратора был отклонён или установщик завершился с ошибкой."));
                }
            }, L.T("Не удалось установить Visual C++ Redistributable"));
        }
        finally
        {
            _busyText = null;
            if (!IsDisposed) UpdateLlamaInfo();
        }
    }

    // ---------- Программа ----------

    private void LoadProgramSettings()
    {
        _loading = true;
        try
        {
            _llamaCheckUpdates.Checked = ConfigStore.Current.Llama.CheckUpdates;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveUi(Action<AppConfig> mutate)
    {
        if (_loading) return;
        Ui.RunSafe(Owner, () => ConfigStore.Update(mutate), L.T("Не удалось сохранить настройку"));
    }
}
