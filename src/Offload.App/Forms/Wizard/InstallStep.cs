using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Util;
using Offload.Integrations;
using Offload.Llama;
using Offload.Models;
using Offload.OpenCode;

namespace Offload.App.Forms.Wizard;

internal enum StageStatus { Pending, Running, Done, Warning, Skipped, Failed }

/// <summary>Один этап установки со строкой статуса.</summary>
internal sealed class InstallTask(string id, string title, bool optional, Func<InstallTask, IProgress<StepProgress>, CancellationToken, Task<string?>> run)
{
    public string Id { get; } = id;
    public string Title { get; set; } = title;
    /// <summary>Этап можно пропустить при ошибке.</summary>
    public bool Optional { get; } = optional;
    public Func<InstallTask, IProgress<StepProgress>, CancellationToken, Task<string?>> Run { get; } = run;
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public string? Detail { get; set; }

    /// <summary>Этап завершён (успешно, с предупреждением или пропущен).</summary>
    public bool IsComplete => Status is StageStatus.Done or StageStatus.Warning or StageStatus.Skipped;
}

/// <summary>Предупреждение (этап выполнен, но не полностью) — показывается жёлтым, установка продолжается.</summary>
internal sealed class InstallWarning(string message) : Exception(message);

/// <summary>Этап пропущен (не требуется при выбранных параметрах).</summary>
internal sealed class InstallSkipped(string message) : Exception(message);

/// <summary>Шаг 6: последовательная установка с повтором и пропуском этапов.</summary>
internal sealed class InstallStep : WizardStep
{
    private sealed class Line
    {
        public required Label Glyph { get; init; }
        public required Label Title { get; init; }
        public required Label Detail { get; init; }
    }

    private static readonly string[] Ids = ["vcredist", "llama", "gpu", "model", "opencode", "server", "smoke", "opencode-config", "ide"];

    private readonly Dictionary<string, Line> _lines = [];
    private readonly TableLayoutPanel _linesTable = Kit.Table(0, 0, 100);
    private readonly ProgressBar _overall = new() { Minimum = 0, Maximum = 1000, Height = 18, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4), Style = ProgressBarStyle.Continuous };
    private readonly Label _overallText = Kit.Label("", Theme.Semibold(9f));
    private readonly ProgressPanel _progress = new();
    private readonly Label _error = Kit.Wrap("", color: Theme.ErrorText);
    private readonly Button _retry;
    private readonly Button _skip;
    private readonly FlowLayoutPanel _errorButtons;
    private readonly ToolTip _tip = new() { AutoPopDelay = 20000 };

    private List<InstallTask> _tasks = [];
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _finished;
    private int _failedIndex = -1;

    public InstallStep(WizardContext ctx) : base(ctx)
    {
        _retry = Kit.Primary("Повторить", async (_, _) => await RunFromAsync(_failedIndex, retry: true));
        _skip = Kit.Button("Пропустить", async (_, _) => await SkipAsync());
        _progress.CancelRequested += (_, _) => CancelRunning();

        var root = Kit.Table();
        foreach (var id in Ids)
        {
            var glyph = Kit.Label("○", Theme.Semibold(10f), Theme.Gray);
            glyph.Margin = new Padding(0, 3, 8, 3);
            glyph.MinimumSize = new Size(20, 0);
            var title = Kit.Label("", Theme.Semibold(9f));
            title.Margin = new Padding(0, 4, 12, 4);
            var detail = Kit.Label("", color: Theme.TextMuted);
            detail.AutoEllipsis = true;
            detail.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            detail.AutoSize = false;
            detail.Height = 20;
            _linesTable.AddRow(glyph, title, detail);
            _lines[id] = new Line { Glyph = glyph, Title = title, Detail = detail };
        }
        root.AddRow(_linesTable);
        root.AddRow(Kit.Spacer(8));
        var overallRow = Kit.Table(100, 0);
        _overallText.Margin = new Padding(12, 4, 0, 4);
        overallRow.AddRow(_overall, _overallText);
        root.AddRow(overallRow);
        root.AddRow(_progress);
        root.AddRow(_error);
        _errorButtons = Kit.Flow(_retry, _skip);
        root.AddRow(_errorButtons);
        SetContent(root);
        ShowError(null);
        Disposed += (_, _) => _tip.Dispose();
    }

    public override string Title => "Установка";

    public override string Heading => "Установка";

    public override string? Subtitle => _finished ? "Установка завершена." : "Не закрывайте окно — загрузка может занять некоторое время.";

    public override bool CanGoNext => _finished && !_running;

    public override bool CanGoBack => !_running && !_finished;

    public override bool IsRunning => _running;

    /// <summary>Итоги этапов (для шага «Готово»).</summary>
    public IReadOnlyList<InstallTask> Tasks => _tasks;

    /// <summary>Установка завершилась (успешно после всех этапов).</summary>
    public event EventHandler? Completed;

    public override void OnEnter()
    {
        if (_running || _finished) return;
        BuildTasks();
        _ = RunFromAsync(0, retry: false);
    }

    public override void CancelRunning()
    {
        if (_running) _cts?.Cancel();
    }

    // ---------- Выполнение ----------

    private void BuildTasks()
    {
        var s = State;
        var backendName = Texts.Backend(s.Backend);
        var modelName = s.Model?.Name ?? "модель";
        _tasks =
        [
            new("vcredist", "Microsoft Visual C++ Redistributable", true, RunVcAsync),
            new("llama", $"llama.cpp ({backendName})", false, RunLlamaAsync),
            new("gpu", "Проверка видеокарты", true, RunGpuCheckAsync),
            new("model", $"Модель «{modelName}»", false, RunModelAsync),
            new("opencode", "OpenCode", true, RunOpenCodeAsync),
            new("server", "Запуск сервера", true, RunServerAsync),
            new("smoke", "Проверка ответа модели", true, RunSmokeAsync),
            new("opencode-config", "Настройка OpenCode", true, RunOpenCodeConfigAsync),
            new("ide", "Подключение к IDE и автозапуск", true, RunIdeAsync),
        ];
        _finished = false;
        _failedIndex = -1;
        foreach (var t in _tasks) Render(t);
        UpdateOverall();
    }

    private async Task RunFromAsync(int start, bool retry)
    {
        if (_running || start < 0) return;
        _running = true;
        ShowError(null);
        using var cts = new CancellationTokenSource();
        _cts = cts;
        RaiseNavigationChanged();
        try
        {
            for (var i = start; i < _tasks.Count; i++)
            {
                var t = _tasks[i];
                if (t.IsComplete && !(retry && i == start)) continue;
                if (!await RunTaskAsync(t, cts.Token))
                {
                    _failedIndex = i;
                    ShowError(t);
                    return;
                }
            }
            _finished = true;
            _progress.Finish("Все этапы выполнены.", _tasks.All(t => t.Status != StageStatus.Failed));
            Log.Info("wizard", "Установка завершена");
        }
        finally
        {
            _running = false;
            _cts = null;
            UpdateOverall();
            RaiseNavigationChanged();
        }
        if (_finished) Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Выполнить этап. false — ошибка (установка остановлена).</summary>
    private async Task<bool> RunTaskAsync(InstallTask t, CancellationToken ct)
    {
        t.Status = StageStatus.Running;
        t.Detail = null;
        Render(t);
        _progress.Reset();
        _progress.Start(t.Title + "…");
        var progress = new Progress<StepProgress>(p =>
        {
            if (IsDisposed) return;
            _progress.Report(p);
            UpdateOverall(p.Fraction);
        });
        Log.Info("wizard", $"Этап: {t.Title}");
        try
        {
            var detail = await t.Run(t, progress, ct);
            t.Status = StageStatus.Done;
            t.Detail = detail;
        }
        catch (InstallSkipped ex)
        {
            t.Status = StageStatus.Skipped;
            t.Detail = ex.Message;
        }
        catch (InstallWarning ex)
        {
            t.Status = StageStatus.Warning;
            t.Detail = ex.Message;
            Log.Warn("wizard", $"{t.Title}: {ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            t.Status = StageStatus.Failed;
            t.Detail = "Отменено.";
            Log.Info("wizard", $"{t.Title}: отменено пользователем");
        }
        catch (Exception ex)
        {
            t.Status = StageStatus.Failed;
            t.Detail = Ui.FriendlyError(ex);
            Log.Error("wizard", $"Этап «{t.Title}» не выполнен", ex);
        }
        Render(t);
        UpdateOverall();
        return t.Status != StageStatus.Failed;
    }

    private async Task SkipAsync()
    {
        if (_running || _failedIndex < 0 || _failedIndex >= _tasks.Count) return;
        var t = _tasks[_failedIndex];
        if (!t.Optional) return;
        t.Status = StageStatus.Skipped;
        t.Detail = "Пропущено: " + (t.Detail ?? "");
        Render(t);
        var next = _failedIndex + 1;
        _failedIndex = -1;
        await RunFromAsync(next, retry: false);
    }

    // ---------- Отображение ----------

    private void Render(InstallTask t)
    {
        if (!_lines.TryGetValue(t.Id, out var line)) return;
        line.Title.Text = t.Title;
        (line.Glyph.Text, line.Glyph.ForeColor) = t.Status switch
        {
            StageStatus.Running => ("⏳", Theme.Accent),
            StageStatus.Done => ("✓", Theme.OkText),
            StageStatus.Warning => ("⚠", Theme.WarnText),
            StageStatus.Skipped => ("—", Theme.Gray),
            StageStatus.Failed => ("✗", Theme.ErrorText),
            _ => ("○", Theme.Gray),
        };
        line.Title.ForeColor = t.Status == StageStatus.Pending ? Theme.TextMuted : Theme.TextPrimary;
        line.Detail.Text = t.Status == StageStatus.Running ? "выполняется…" : t.Detail ?? "";
        line.Detail.ForeColor = t.Status switch
        {
            StageStatus.Failed => Theme.ErrorText,
            StageStatus.Warning => Theme.WarnText,
            _ => Theme.TextMuted,
        };
        _tip.SetToolTip(line.Detail, t.Detail ?? "");
    }

    private void UpdateOverall(double? currentFraction = null)
    {
        if (_tasks.Count == 0) return;
        var done = _tasks.Count(t => t.IsComplete);
        var running = _tasks.Any(t => t.Status == StageStatus.Running) ? Math.Clamp(currentFraction ?? 0, 0, 1) : 0;
        var f = (done + running) / _tasks.Count;
        _overall.Value = (int)Math.Round(Math.Clamp(f, 0, 1) * 1000);
        _overallText.Text = $"{done} из {_tasks.Count}";
    }

    private void ShowError(InstallTask? t)
    {
        var show = t is not null;
        _error.Visible = show;
        _errorButtons.Visible = show;
        if (t is null) return;
        _error.Text = t.Detail == "Отменено."
            ? $"Установка прервана на этапе «{t.Title}». Нажмите «Повторить», чтобы продолжить."
            : $"Этап «{t.Title}» не выполнен: {t.Detail}";
        _skip.Visible = t.Optional;
        _progress.Finish(t.Detail == "Отменено." ? "Установка прервана." : "Установка остановлена из-за ошибки.", false);
    }

    // ---------- Этапы ----------

    private static async Task<string?> RunVcAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        if (VcRuntime.IsInstalled()) return "уже установлен";
        p.Report(new StepProgress("Установка Microsoft Visual C++ Redistributable…", null, "Windows попросит подтвердить установку"));
        if (!await VcRuntime.InstallAsync(p, ct))
            throw new InvalidOperationException("Установка не завершена — возможно, запрос прав администратора был отклонён. " +
                                                "Без этих библиотек llama.cpp может не запуститься.");
        return "установлен";
    }

    private async Task<string?> RunLlamaAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        var cfg = ConfigStore.Current;
        var backend = State.Backend;
        if (LlamaInstaller.IsInstalled(cfg) && cfg.Llama.InstalledBackend == backend)
        {
            ConfigStore.Update(c => c.Llama.Backend = backend);
            return $"уже установлена ({cfg.Llama.InstalledTag})";
        }
        var r = await LlamaInstaller.InstallAsync(backend, p, ct);
        ConfigStore.Update(c => c.Llama.Backend = backend);
        return $"{r.Tag}, {Texts.Backend(r.Backend)}";
    }

    private async Task<string?> RunGpuCheckAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        if (State.Backend == LlamaBackend.Cpu) throw new InstallSkipped("не требуется для сборки «Только процессор»");
        var devices = await ListGpuDevicesAsync(p, ct);
        if (devices.Count > 0) return devices[0];

        var backend = State.Backend;
        if (backend != LlamaBackend.Vulkan &&
            Ui.Confirm(Ctx.Form,
                $"Сборка llama.cpp «{Texts.Backend(backend)}» не видит видеокарту — модель будет работать только на процессоре (медленно).{Environment.NewLine}{Environment.NewLine}" +
                "Установить сборку Vulkan? Она работает почти с любой видеокартой.", warning: true))
        {
            p.Report(new StepProgress("Установка сборки Vulkan…"));
            var r = await LlamaInstaller.InstallAsync(LlamaBackend.Vulkan, p, ct);
            State.Backend = LlamaBackend.Vulkan;
            ConfigStore.Update(c => c.Llama.Backend = LlamaBackend.Vulkan);
            var llama = _tasks.FirstOrDefault(x => x.Id == "llama");
            if (llama is not null)
            {
                llama.Title = $"llama.cpp ({Texts.Backend(LlamaBackend.Vulkan)})";
                llama.Detail = $"{r.Tag}, {Texts.Backend(r.Backend)}";
                Render(llama);
            }
            devices = await ListGpuDevicesAsync(p, ct);
            if (devices.Count > 0) return devices[0];
        }
        throw new InstallWarning("видеокарта не найдена — модель будет работать на процессоре");
    }

    private static async Task<IReadOnlyList<string>> ListGpuDevicesAsync(IProgress<StepProgress> p, CancellationToken ct)
    {
        p.Report(new StepProgress("Проверка устройств llama.cpp…"));
        var exe = LlamaInstaller.GetServerExePath(ConfigStore.Current)
                  ?? throw new InvalidOperationException("llama-server.exe не найден.");
        IReadOnlyList<string> devices;
        try
        {
            devices = await LlamaDevices.ListAsync(exe, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is LlamaVcRuntimeMissingException) throw new InstallWarning(ex.Message);
            throw new InstallWarning("не удалось получить список устройств: " + Ui.FriendlyError(ex));
        }
        return devices.Where(d => !d.TrimStart().StartsWith("CPU", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<string?> RunModelAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        var row = State.Model ?? throw new InvalidOperationException("Модель не выбрана.");
        var cfg = ConfigStore.Current;
        if (!string.IsNullOrWhiteSpace(State.ModelsDir) &&
            !string.Equals(cfg.Models.ModelsDir, State.ModelsDir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(State.ModelsDir);
            ConfigStore.Update(c => c.Models.ModelsDir = State.ModelsDir);
        }

        InstalledModel installed;
        string detail;
        var existing = row.Installed is { } inst ? ConfigStore.Current.Models.Installed.FirstOrDefault(m => m.Id == inst.Id) : null;
        if (existing is not null && File.Exists(existing.FilePath))
        {
            installed = existing;
            detail = "уже установлена";
        }
        else
        {
            var catalog = row.Catalog ?? throw new InvalidOperationException("Файл модели не найден, а в каталоге её нет — выберите другую модель.");
            installed = await ModelManager.DownloadAsync(catalog, State.Quant, p, ct);
            detail = $"{installed.Quant ?? State.Quant} · {FileUtil.FormatBytes(installed.SizeBytes)}";
        }
        if (ConfigStore.Current.ActiveModel()?.Id != installed.Id) ModelManager.SetActive(installed.Id);
        Ctx.Shell.ConfigChanged();
        return detail;
    }

    private async Task<string?> RunOpenCodeAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        var install = State.InstallOpenCode;
        ConfigStore.Update(c => c.OpenCode.Enabled = install);
        if (!install) throw new InstallSkipped("не выбрано");
        var cfg = ConfigStore.Current;
        if (OpenCodeInstaller.FindExecutable(cfg) is not null)
            return "уже установлен" + (cfg.OpenCode.InstalledVersion is { } v ? $" ({v})" : "");
        var r = await OpenCodeInstaller.InstallAsync(p, ct);
        return $"версия {r.Version}";
    }

    private async Task<string?> RunServerAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        var server = Ctx.Shell.Server;
        p.Report(new StepProgress("Загрузка модели в память…", null, "первый запуск может занять пару минут"));
        if (server.State == ServerState.Running)
        {
            // Модель или сборка могли смениться — перезапускаем с новыми параметрами.
            await server.StopAsync();
        }
        if (await server.StartAsync(true, ct)) return Ctx.Shell.Server.Summary;

        if (server.LastException is LlamaVcRuntimeMissingException vc)
        {
            if (!Ui.Confirm(Ctx.Form, vc.Message + Environment.NewLine + Environment.NewLine + "Установить Visual C++ Redistributable сейчас?"))
                throw new InvalidOperationException(vc.Message);
            p.Report(new StepProgress("Установка Microsoft Visual C++ Redistributable…"));
            if (!await VcRuntime.InstallAsync(p, ct))
                throw new InvalidOperationException("Visual C++ Redistributable не установлен.");
            var vcTask = _tasks.FirstOrDefault(x => x.Id == "vcredist");
            if (vcTask is not null)
            {
                vcTask.Status = StageStatus.Done;
                vcTask.Detail = "установлен";
                Render(vcTask);
            }
            p.Report(new StepProgress("Загрузка модели в память…"));
            if (await server.StartAsync(true, ct)) return server.Summary;
        }
        throw new InvalidOperationException(server.LastError ?? "Сервер не запустился — подробности в журнале llama-server.");
    }

    private async Task<string?> RunSmokeAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        if (Ctx.Shell.Server.State != ServerState.Running) throw new InstallSkipped("сервер не запущен");
        p.Report(new StepProgress("Тестовый запрос к модели…"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var r = await ModelCheck.RunAsync(ConfigStore.Current, timeout.Token);
            Ctx.Shell.Server.MarkActivity();
            return r.TokensPerSecond is double tps ? $"модель ответила, {tps:0.0} ток/с" : "модель ответила";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("модель не ответила за 3 минуты");
        }
    }

    private static Task<string?> RunOpenCodeConfigAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        var cfg = ConfigStore.Reload();
        if (!cfg.OpenCode.Enabled) throw new InstallSkipped("OpenCode не используется");
        if (OpenCodeInstaller.FindExecutable(cfg) is null) throw new InstallSkipped("OpenCode не установлен");
        var path = OpenCodeConfigWriter.WriteManagedConfig(cfg);
        if (cfg.OpenCode.RegisterInGlobalConfig) OpenCodeConfigWriter.RegisterGlobal(cfg);
        return Task.FromResult<string?>(path);
    }

    private async Task<string?> RunIdeAsync(InstallTask t, IProgress<StepProgress> p, CancellationToken ct)
    {
        var s = State;
        var errors = new List<string>();
        var connected = new List<string>();
        var hints = new List<string>();
        var spec = McpServerSpec.ForCurrentExecutable();
        var cfg = ConfigStore.Current;

        // Шаг IDE мог не открываться (например, данные не загрузились) — тогда ничего не меняем в IDE.
        if (s.IdesLoaded)
        {
            foreach (var integration in IntegrationRegistry.All)
            {
                ct.ThrowIfCancellationRequested();
                var id = integration.Id;
                var wanted = s.Ides.Contains(id);
                var was = cfg.Integrations.Contains(id);
                try
                {
                    if (wanted)
                    {
                        p.Report(new StepProgress($"Подключение: {integration.DisplayName}…"));
                        var r = await integration.RegisterAsync(spec, ct);
                        if (r.Ok)
                        {
                            connected.Add(integration.DisplayName);
                            ConfigStore.Update(c =>
                            {
                                if (!c.Integrations.Contains(id)) c.Integrations.Add(id);
                            });
                            if (integration.PostRegisterHint is { Length: > 0 } hint) hints.Add(hint);
                        }
                        else
                        {
                            errors.Add($"{integration.DisplayName}: {r.Message}");
                        }
                    }
                    else if (was)
                    {
                        p.Report(new StepProgress($"Отключение: {integration.DisplayName}…"));
                        var r = await integration.UnregisterAsync(ct);
                        if (r.Ok) ConfigStore.Update(c => c.Integrations.Remove(id));
                        else errors.Add($"{integration.DisplayName}: {r.Message}");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error("wizard", $"Подключение {id}", ex);
                    errors.Add($"{integration.DisplayName}: {Ui.FriendlyError(ex)}");
                }
            }

            if (s.Ides.Contains("claude-code"))
            {
                p.Report(new StepProgress("Настройка Claude Code…"));
                try
                {
                    if (s.ClaudeGuidance && !ClaudeCodeExtras.IsGuidanceInstalled())
                    {
                        var r = ClaudeCodeExtras.InstallGuidance();
                        if (!r.Ok) errors.Add("Инструкции Claude Code: " + r.Message);
                    }
                    if (s.PreapproveReadTools && !ClaudeCodeExtras.AreToolsPreapproved())
                    {
                        var r = ClaudeCodeExtras.PreapproveTools(includeWriteTools: false);
                        if (!r.Ok) errors.Add("Разрешения Claude Code: " + r.Message);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("wizard", "Настройка Claude Code", ex);
                    errors.Add("Claude Code: " + Ui.FriendlyError(ex));
                }
            }
        }

        p.Report(new StepProgress("Автозапуск…"));
        try
        {
            Autostart.Set(s.Autostart);
            ConfigStore.Update(c => c.Ui.StartWithWindows = s.Autostart);
        }
        catch (Exception ex)
        {
            Log.Error("wizard", "Автозапуск", ex);
            errors.Add("Автозапуск: " + Ui.FriendlyError(ex));
        }

        HintsText = hints.Distinct().ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
        return connected.Count == 0 ? "IDE не выбраны" : string.Join(", ", connected);
    }

    /// <summary>Подсказки IDE после подключения («Перезапустите Cursor» и т. п.).</summary>
    public IReadOnlyList<string> HintsText { get; private set; } = [];
}
