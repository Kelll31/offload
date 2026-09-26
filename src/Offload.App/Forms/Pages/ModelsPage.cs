using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Util;
using Offload.Llama;
using Offload.Models;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «Модели»: каталог, загрузка, выбор активной модели, роли моделей, свои GGUF-файлы.</summary>
internal sealed class ModelsPage : PageBase
{
    private readonly Label _hardware = Kit.Hint(L.T("Определение оборудования…"));
    private readonly ListView _list = ModelListBinder.Create(full: true);
    private readonly Label _name = Kit.Label(L.T("Выберите модель в списке"), Theme.Semibold(11f));
    private readonly Label _description = Kit.Wrap("");
    private readonly Label _fit = Kit.Wrap("");
    private readonly ComboBox _quant = Kit.Combo(240);
    private readonly Label _quantHint = Kit.Hint("", autoWidth: true);
    private readonly Button _download;
    private readonly Button _activate;
    private readonly Button _remove;
    private readonly ProgressPanel _progress = new();
    private readonly Label _folder = Kit.Wrap("");
    private readonly Label _disk = Kit.Wrap("");
    private readonly Button _addCustom;
    private readonly Button _changeFolder;
    private readonly TextBox _search = new()
    {
        Width = 220,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 3, 8, 3),
        PlaceholderText = L.T("Поиск модели…"),
        BackColor = Theme.Input,
        ForeColor = Theme.TextPrimary,
    };
    private readonly ComboBox _filter = Kit.Combo(230);
    private readonly Label _shown = Kit.Label("", Theme.Regular(8.5f), Theme.TextMuted);

    // Роли моделей (ROADMAP §5.2): основная — активная модель, остальные — вспомогательные серверы.
    private readonly Label _qualityModel = Kit.Label("");
    private readonly Label _qualityStatus = Kit.Label("", Theme.Regular(8.5f), Theme.TextMuted);
    private readonly Dictionary<ModelRole, ComboBox> _roleCombos = new()
    {
        [ModelRole.Fast] = Kit.Combo(260),
        [ModelRole.Embed] = Kit.Combo(260),
        [ModelRole.Rerank] = Kit.Combo(260),
    };
    private readonly Dictionary<ModelRole, Label> _roleStatus = new()
    {
        [ModelRole.Fast] = Kit.Label("", Theme.Regular(8.5f), Theme.TextMuted),
        [ModelRole.Embed] = Kit.Label("", Theme.Regular(8.5f), Theme.TextMuted),
        [ModelRole.Rerank] = Kit.Label("", Theme.Regular(8.5f), Theme.TextMuted),
    };
    private readonly Dictionary<ModelRole, List<string?>> _roleItems = [];
    private readonly Label _roleBudget = Kit.Wrap("");
    private bool _fillingRoles;
    private int _budgetVersion;

    private HardwareInfo? _hw;
    private CancellationTokenSource? _downloadCts;
    private string? _downloadingName;
    private IReadOnlyList<(string Quant, string Text, long Size)> _quantItems = [];

    public ModelsPage(IAppShell shell) : base(shell)
    {
        _download = Kit.Primary(L.T("Скачать"), async (_, _) => await DownloadAsync());
        _activate = Kit.Button(L.T("Сделать активной"), async (_, _) => await ActivateAsync());
        _remove = Kit.Button(L.T("Удалить"), async (_, _) => await RemoveAsync());
        _addCustom = Kit.Button(L.T("Добавить свой GGUF…"), async (_, _) => await AddCustomAsync(), 150);
        _changeFolder = Kit.Button(L.T("Изменить папку…"), (_, _) => ChangeFolder(), 120);

        _list.SelectedIndexChanged += (_, _) => ShowDetails();
        _list.DoubleClick += async (_, _) =>
        {
            if (ModelListBinder.Selected(_list) is { IsInstalled: true, IsActive: false } row && CanBeActive(row)) await ActivateAsync();
        };
        _quant.SelectedIndexChanged += (_, _) => UpdateDisk();
        _progress.CancelRequested += (_, _) => _downloadCts?.Cancel();
        _filter.Items.AddRange([L.T("Все модели"), L.T("Помещаются в видеопамять"), L.T("Скачанные"), L.T("Для агента (вызов инструментов)")]);
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => Reload();
        _search.TextChanged += (_, _) => Reload();

        var root = Kit.FillTable();
        root.Padding = new Padding(16, 4, 28, 12);
        root.AddRow(_hardware);
        var filters = Kit.Flow(_search, _filter, _shown);
        _shown.Margin = new Padding(4, 7, 0, 0);
        root.AddRow(filters);
        root.AddFillRow(_list);

        var card = new CardPanel { ColumnCount = 1 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Margin = new Padding(0, 6, 0, 6);
        card.AddRow(_name);
        card.AddRow(_description);
        card.AddRow(_fit);
        var quantRow = Kit.Flow(Kit.Label(L.T("Квантизация:")), _quant, _quantHint);
        quantRow.WrapContents = true;
        card.AddRow(quantRow);
        card.AddRow(Kit.Flow(_download, _activate, _remove));
        card.AddRow(_progress);
        root.AddRow(card);

        card.AddRow(_disk);

        root.AddRow(BuildRolesCard());

        var folderRow = Kit.Table(100, 0);
        var folderButtons = Kit.Flow(_addCustom, Kit.Button(L.T("Открыть папку"), (_, _) => OpenModelsFolder(), 110), _changeFolder);
        folderButtons.WrapContents = false;
        folderButtons.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _changeFolder.Margin = new Padding(0, 2, 0, 2);
        _folder.Font = Theme.Regular(8.5f);
        _folder.ForeColor = Theme.TextMuted;
        folderRow.AddRow(_folder, folderButtons);
        root.AddRow(folderRow);

        Controls.Add(root);
        UpdateFolder();
        ShowDetails();
    }

    /// <summary>Карточка «Роли моделей»: основная (активная), быстрая, эмбеддинги, реранк; состояние серверов и бюджет видеопамяти.</summary>
    private CardPanel BuildRolesCard()
    {
        var card = new CardPanel { ColumnCount = 1 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Margin = new Padding(0, 0, 0, 6);
        card.AddRow(Kit.Label(L.T("Роли моделей"), Theme.Semibold(10f)));
        card.AddRow(Kit.Hint(L.T("Основная модель — активная. Быстрая берёт короткие задачи IDE (сообщения коммитов, сводки журналов, поиск контекста), эмбеддинги и реранк нужны для поиска по коду. Вспомогательные серверы запускаются при первом запросе и занимают видеопамять вместе с основным.")));

        var grid = Kit.Table(0, 0, 100);
        grid.Margin = new Padding(0, 4, 0, 0);
        grid.AddRow(Kit.Label(L.T("Основная (качество):")), _qualityModel, _qualityStatus);
        grid.AddRow(Kit.Label(L.T("Быстрая:")), _roleCombos[ModelRole.Fast], _roleStatus[ModelRole.Fast]);
        grid.AddRow(Kit.Label(L.T("Эмбеддинги:")), _roleCombos[ModelRole.Embed], _roleStatus[ModelRole.Embed]);
        grid.AddRow(Kit.Label(L.T("Реранк:")), _roleCombos[ModelRole.Rerank], _roleStatus[ModelRole.Rerank]);
        foreach (var (role, combo) in _roleCombos)
            combo.SelectedIndexChanged += async (_, _) => await AssignRoleAsync(role);
        _qualityModel.Margin = new Padding(0, 6, 8, 3);
        foreach (var l in _roleStatus.Values.Append(_qualityStatus)) l.Margin = new Padding(0, 6, 0, 0);
        card.AddRow(grid);
        _roleBudget.Visible = false;
        card.AddRow(_roleBudget);
        return card;
    }

    public override string Key => Tabs.Models;

    public override string Title => L.T("Модели");

    public override string Subtitle => L.T("Каталог моделей: что поместится в видеопамять, загрузка и выбор активной");

    public override string Glyph => Glyphs.Models;

    public override string? BusyDescription => _downloadingName is null ? null : L.F("загрузка модели «{0}»", _downloadingName);

    protected override async void OnActivated()
    {
        try
        {
            if (_hw is null)
            {
                Reload();
                try
                {
                    _hw = await Shell.Hardware.GetAsync();
                }
                catch (Exception ex)
                {
                    Log.Warn("ui", $"Оборудование не определено: {ex.Message}");
                }
            }
            if (IsDisposed) return;
            Reload();
            if (_hw is null) _hardware.Text = L.T("Не удалось определить оборудование — оценка видеопамяти недоступна.");
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Вкладка «Модели»", ex);
        }
    }

    public override void OnConfigChanged()
    {
        if (IsActive) Reload();
        else UpdateFolder();
    }

    public override void OnServerStateChanged()
    {
        UpdateUiState();
        UpdateRoleStatus();
    }

    /// <summary>Перестроить список (выделение сохраняется).</summary>
    private void Reload()
    {
        var cfg = ConfigStore.Current;
        if (_hw is not null) _hardware.Text = Texts.HardwareSummary(_hw);
        var (all, error) = ModelRows.Build(cfg, _hw);
        if (error is not null) _hardware.Text = L.F("Каталог моделей недоступен: {0}", error);
        var rows = all.Where(PassesFilter).ToList();
        _shown.Text = rows.Count == all.Count ? Ui.Plural(all.Count, "модель", "модели", "моделей") : L.F("показано {0} из {1}", Ui.N(rows.Count), Ui.N(all.Count));
        ModelListBinder.Fill(_list, rows, full: true);
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
        {
            var preferred = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((ModelRow)i.Tag!).IsActive)
                            ?? _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((ModelRow)i.Tag!).IsRecommended)
                            ?? _list.Items[0];
            preferred.Selected = true;
            preferred.EnsureVisible();
        }
        UpdateFolder();
        ShowDetails();
        FillRoles(cfg);
    }

    /// <summary>Списки моделей для ролей (подходящие установленные модели) и выбранные значения — без срабатывания назначения.</summary>
    private void FillRoles(AppConfig cfg)
    {
        _fillingRoles = true;
        try
        {
            var active = cfg.ActiveModel();
            _qualityModel.Text = Texts.ModelName(active);
            foreach (var (role, combo) in _roleCombos)
            {
                var ids = new List<string?> { null };
                var names = new List<string> { L.T("— не использовать —") };
                foreach (var m in cfg.Models.Installed
                             .Where(m => ModelRoleConfig.IsCompatible(m, role) && !(role == ModelRole.Fast && m.Id == active?.Id))
                             .OrderBy(Texts.ModelName, StringComparer.CurrentCultureIgnoreCase))
                {
                    ids.Add(m.Id);
                    names.Add(string.IsNullOrWhiteSpace(m.Quant) ? Texts.ModelName(m) : $"{Texts.ModelName(m)}  ({m.Quant})");
                }
                _roleItems[role] = ids;
                combo.BeginUpdate();
                combo.Items.Clear();
                foreach (var n in names) combo.Items.Add(n);
                var assigned = cfg.RoleModel(role)?.Id;
                combo.SelectedIndex = Math.Max(0, ids.FindIndex(id => string.Equals(id, assigned, StringComparison.OrdinalIgnoreCase)));
                combo.EndUpdate();
                combo.Enabled = !IsBusy && ids.Count > 1;
            }
        }
        finally
        {
            _fillingRoles = false;
        }
        UpdateRoleStatus();
        _ = UpdateRoleBudgetAsync(cfg);
    }

    /// <summary>Состояние основного сервера и серверов ролей.</summary>
    private void UpdateRoleStatus()
    {
        _qualityStatus.Text = Texts.State(Shell.Server.State);
        var cfg = ConfigStore.Current;
        var snapshot = Shell.Server.Aux.Snapshot();
        foreach (var (role, label) in _roleStatus)
        {
            var status = snapshot.FirstOrDefault(x => x.Role == role);
            var (text, color) = RoleStatusText(cfg.RoleModel(role) is not null, status);
            label.Text = text;
            label.ForeColor = color;
        }
    }

    private static (string Text, Color Color) RoleStatusText(bool assigned, AuxServerStatus? status)
    {
        if (!assigned) return ("", Theme.TextMuted);
        switch (status?.State)
        {
            case ServerState.Running:
                var port = Uri.TryCreate(status.BaseUrl, UriKind.Absolute, out var uri) ? uri.Port : 0;
                return (port > 0 ? L.F("работает, порт {0}", port) : L.T("работает"), Theme.OkText);
            case ServerState.Starting:
                return (L.T("загружается…"), Theme.TextMuted);
            case ServerState.Failed:
                return (L.F("ошибка: {0}", status.Error ?? L.T("подробности — в журнале")), Theme.ErrorText);
        }
        return status?.Error is { } error
            ? (L.F("не запустился: {0}", error), Theme.ErrorText)
            : (L.T("запустится по первому запросу"), Theme.TextMuted);
    }

    /// <summary>Хватит ли видеопамяти на все назначенные роли вместе (оценка FitCalculator, только предупреждение).</summary>
    private async Task UpdateRoleBudgetAsync(AppConfig cfg)
    {
        var version = ++_budgetVersion;
        var hw = _hw;
        if (hw is null)
        {
            _roleBudget.Visible = false;
            return;
        }
        RoleBudgetResult? budget;
        try
        {
            var (catalog, _) = ModelRows.Catalog();
            budget = await Task.Run(() => RoleBudget.Evaluate(cfg, hw, catalog, ReadHeader));
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Оценка памяти ролей: {ex.Message}");
            budget = null;
        }
        if (IsDisposed || version != _budgetVersion) return;
        if (budget is not { HasAuxiliary: true })
        {
            _roleBudget.Visible = false;
            return;
        }
        _roleBudget.Visible = true;
        if (budget.Fits)
        {
            _roleBudget.ForeColor = Theme.TextMuted;
            _roleBudget.Text = budget.VramBudgetBytes > 0
                ? L.F("Все роли вместе: ≈{0} видеопамяти из {1} доступных.", FileUtil.FormatBytes(budget.TotalVramBytes), FileUtil.FormatBytes(budget.VramBudgetBytes))
                : L.F("Все роли вместе: ≈{0} памяти из {1} доступных.", FileUtil.FormatBytes(budget.TotalRamBytes), FileUtil.FormatBytes(budget.RamBudgetBytes));
        }
        else
        {
            _roleBudget.ForeColor = Theme.WarnText;
            _roleBudget.Text = budget.VramBudgetBytes > 0 && budget.TotalVramBytes > budget.VramBudgetBytes
                ? L.F("⚠ Модели всех ролей вместе не помещаются в видеопамять: нужно ≈{0}, доступно ≈{1}. Вспомогательные серверы будут работать медленнее (часть слоёв на процессоре) или не запустятся — выберите модели поменьше или снимите роль.",
                    FileUtil.FormatBytes(budget.TotalVramBytes), FileUtil.FormatBytes(budget.VramBudgetBytes))
                : L.F("⚠ Модели всех ролей вместе не помещаются в память: нужно ≈{0} ОЗУ, доступно ≈{1}. Выберите модели поменьше или снимите роль.",
                    FileUtil.FormatBytes(budget.TotalRamBytes), FileUtil.FormatBytes(budget.RamBudgetBytes));
        }
    }

    private static GgufInfo? ReadHeader(string path)
    {
        try
        {
            return File.Exists(path) ? GgufReader.Read(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Назначить роли модель из списка: сохранить, остановить сервер роли со старой моделью.</summary>
    private async Task AssignRoleAsync(ModelRole role)
    {
        if (_fillingRoles || !_roleItems.TryGetValue(role, out var ids)) return;
        var index = _roleCombos[role].SelectedIndex;
        if (index < 0 || index >= ids.Count) return;
        var id = ids[index];
        if (string.Equals(id, ConfigStore.Current.RoleModel(role)?.Id, StringComparison.OrdinalIgnoreCase)) return;
        await RunBusyAsync(async () =>
        {
            await Task.Run(() => ModelManager.AssignRole(role, id));
            await Shell.Server.Aux.SyncWithConfigAsync();
        }, L.T("Не удалось назначить модель роли"));
        Shell.ConfigChanged();
        if (!IsActive) Reload();
    }

    private void ShowDetails()
    {
        var row = ModelListBinder.Selected(_list);
        if (row is null)
        {
            _name.Text = _list.Items.Count == 0 ? L.T("Список моделей пуст") : L.T("Выберите модель в списке");
            _description.Text = "";
            _fit.Text = "";
            _quant.Items.Clear();
            _quantHint.Text = "";
            UpdateUiState();
            UpdateDisk();
            return;
        }

        _name.Text = row.Name + (row.IsRecommended ? "   " + ModelListBinder.RecommendedMark : "");
        var desc = row.Description;
        if (row.Catalog is { } c)
        {
            var facts = new List<string>();
            if (c.ParamsB > 0) facts.Add(c.IsMoe ? L.F("{0:0.#} млрд параметров (активных {1:0.#} млрд, MoE)", c.ParamsB, c.ActiveParamsB) : L.F("{0:0.#} млрд параметров", c.ParamsB));
            if (c.NativeContext > 0) facts.Add(L.F("контекст до {0}", Ui.Tokens(c.NativeContext)));
            if (!string.IsNullOrWhiteSpace(c.License)) facts.Add(L.F("лицензия {0}", c.License));
            if (c.GoodToolCalling) facts.Add(L.T("надёжно вызывает инструменты"));
            if (c.Role == ModelKind.Embed) facts.Add(L.T("модель эмбеддингов — назначается роли «Эмбеддинги», активной быть не может"));
            if (c.Role == ModelKind.Rerank) facts.Add(L.T("реранкер — назначается роли «Реранк», активной быть не может"));
            if (facts.Count > 0) desc = $"{desc}{Environment.NewLine}{string.Join(" · ", facts)}";
        }
        if (row.Installed is { } inst) desc = L.F("{0}{1}Файл: {2}", desc, Environment.NewLine, inst.FilePath);
        _description.Text = desc;

        if (row.Fit is { } fit)
        {
            _fit.Text = $"{Texts.FitGlyph(fit.Level)} {fit.Explanation}";
            _fit.ForeColor = Texts.FitColor(fit.Level);
        }
        else
        {
            _fit.Text = _hw is null ? L.T("Оценка появится после определения оборудования.") : "";
            _fit.ForeColor = Theme.TextMuted;
        }
        if (row.Catalog is { } needs && ModelManager.LlamaBuildWarning(needs, ConfigStore.Current) is { } buildWarning)
        {
            _fit.Text = _fit.Text.Length > 0 ? $"{_fit.Text}{Environment.NewLine}⚠ {buildWarning}" : $"⚠ {buildWarning}";
            if (_fit.ForeColor != Theme.ErrorText) _fit.ForeColor = Theme.WarnText;
        }

        _quant.Items.Clear();
        _quantItems = row.Catalog is { } cat ? ModelListBinder.Quants(cat) : [];
        foreach (var q in _quantItems) _quant.Items.Add(q.Text);
        if (_quantItems.Count > 0)
        {
            var current = row.Installed?.Quant;
            var idx = current is null ? 0 : Math.Max(0, _quantItems.ToList().FindIndex(q => string.Equals(q.Quant, current, StringComparison.OrdinalIgnoreCase)));
            _quant.SelectedIndex = idx;
            _quantHint.Text = L.T("первая — рекомендуемая");
        }
        else
        {
            _quantHint.Text = row.Installed?.Quant is { } q ? q : "";
        }
        UpdateUiState();
        UpdateDisk();
    }

    protected override void UpdateUiState()
    {
        var row = ModelListBinder.Selected(_list);
        var downloading = _downloadCts is not null;
        _download.Enabled = !IsBusy && row?.Catalog is not null && !DiskInsufficient(row);
        _download.Text = row?.IsInstalled == true ? L.T("Скачать заново") : L.T("Скачать");
        _activate.Enabled = !IsBusy && row is { IsInstalled: true, IsActive: false } && CanBeActive(row);
        foreach (var (role, combo) in _roleCombos)
            combo.Enabled = !IsBusy && _roleItems.TryGetValue(role, out var ids) && ids.Count > 1;
        _remove.Enabled = !IsBusy && row is { IsInstalled: true };
        _quant.Enabled = !downloading && _quant.Items.Count > 1;
        _addCustom.Enabled = !IsBusy;
        _changeFolder.Enabled = !downloading;
    }

    /// <summary>Чат-модель: её можно сделать активной (модели эмбеддингов и реранкеры — только роли).</summary>
    private static bool CanBeActive(ModelRow row) => row.IsChat;

    private string? SelectedQuant() =>
        _quant.SelectedIndex >= 0 && _quant.SelectedIndex < _quantItems.Count ? _quantItems[_quant.SelectedIndex].Quant : null;

    private static string ModelsDir() => ModelsFolder.Current();

    private bool PassesFilter(ModelRow r)
    {
        var q = _search.Text.Trim();
        if (q.Length > 0 && !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !r.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            && !(r.Catalog?.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            && !(r.Catalog?.LocalizedDescription.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;
        return _filter.SelectedIndex switch
        {
            1 => r.Fit is { Level: FitLevel.FullGpu or FitLevel.MoeOffload },
            2 => r.IsInstalled,
            3 => r.GoodToolCalling,
            _ => true,
        };
    }

    private void UpdateFolder()
    {
        var dir = ModelsDir();
        var free = HardwareDetector.GetFreeDiskBytes(dir);
        _folder.Text = L.F("Папка моделей: {0}  ·  свободно на диске: {1}", dir, free > 0 ? FileUtil.FormatBytes(free) : L.T("неизвестно"));
    }

    private bool DiskInsufficient(ModelRow row)
    {
        if (row.Catalog is null) return false;
        var need = ModelListBinder.RequiredBytes(row.Catalog, SelectedQuant());
        var free = HardwareDetector.GetFreeDiskBytes(ModelsDir());
        return need > 0 && free > 0 && need + ModelListBinder.DiskReserveBytes > free;
    }

    private void UpdateDisk()
    {
        var row = ModelListBinder.Selected(_list);
        if (row?.Catalog is null || row.IsInstalled)
        {
            _disk.Text = "";
            _disk.Visible = false;
            UpdateUiState();
            return;
        }
        var need = ModelListBinder.RequiredBytes(row.Catalog, SelectedQuant());
        var free = HardwareDetector.GetFreeDiskBytes(ModelsDir());
        _disk.Visible = true;
        if (DiskInsufficient(row))
        {
            _disk.ForeColor = Theme.ErrorText;
            _disk.Text = L.F("Недостаточно места на диске: нужно {0}, свободно {1}. Освободите место или выберите другую папку моделей.",
                FileUtil.FormatBytes(need), FileUtil.FormatBytes(free));
        }
        else
        {
            _disk.ForeColor = Theme.TextMuted;
            _disk.Text = need > 0 ? L.F("Для загрузки нужно ≈{0} свободного места.", FileUtil.FormatBytes(need)) : "";
        }
        UpdateUiState();
    }

    private async Task DownloadAsync()
    {
        var row = ModelListBinder.Selected(_list);
        if (row?.Catalog is not { } model || _downloadCts is not null) return;
        if (DiskInsufficient(row))
        {
            UpdateDisk();
            Ui.Warn(Owner, _disk.Text);
            return;
        }
        if (row.Fit is { Level: FitLevel.TooLarge } &&
            !Ui.Confirm(Owner, L.F("Похоже, модель «{0}» не поместится в память этого компьютера:{1}{2}{1}{1}Всё равно скачать?", model.LocalizedDisplayName, Environment.NewLine, row.Fit.Explanation), warning: true))
            return;

        var quant = SelectedQuant();
        // Файл уже установленной версии этой модели: после загрузки другого кванта он может остаться ненужным.
        var previousFile = ConfigStore.Current.Models.Installed
            .FirstOrDefault(m => string.Equals(m.Id, model.Id, StringComparison.OrdinalIgnoreCase))?.FilePath;
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        _downloadingName = model.LocalizedDisplayName;
        _progress.Reset();
        _progress.Start(L.F("Загрузка «{0}»…", model.LocalizedDisplayName));
        InstalledModel? installed = null;
        try
        {
            await RunBusyAsync(async () =>
            {
                try
                {
                    installed = await ModelManager.DownloadAsync(model, quant, _progress.CreateProgress(), cts.Token);
                    _progress.Finish(L.F("Модель «{0}» установлена.", Texts.ModelName(installed)), true);
                    Log.Info("models", $"Модель установлена: {installed.DisplayName} ({installed.FilePath})");
                }
                catch (OperationCanceledException)
                {
                    _progress.Finish(L.T("Загрузка отменена. Скачанная часть сохранена — при повторной загрузке она продолжится."), false);
                    throw;
                }
                catch (Exception ex)
                {
                    _progress.Finish(L.F("Ошибка загрузки: {0}", Ui.FriendlyError(ex)), false);
                    throw;
                }
            }, L.T("Не удалось скачать модель"), _addCustom);
        }
        finally
        {
            _downloadCts = null;
            _downloadingName = null;
            UpdateUiState();
        }

        Shell.ConfigChanged();
        Reload();
        if (installed is null) return;
        Shell.Notify(L.T("Модель скачана"), L.F("«{0}» готова к работе.", Texts.ModelName(installed)), tab: Tabs.Models);
        await OfferDeleteOldFilesAsync(installed, previousFile);
        if (installed.Kind != ModelKind.Chat)
        {
            // Модель эмбеддингов/реранкер не бывает активной: если роль была свободна, она назначена автоматически.
            Reload();
            return;
        }
        var active = ConfigStore.Current.ActiveModel();
        if (active?.Id != installed.Id &&
            Ui.Confirm(Owner, L.F("Модель «{0}» установлена. Сделать её активной?", Texts.ModelName(installed))))
            await Shell.SwitchModelAsync(installed.Id, Owner);
        else if (active?.Id == installed.Id && Shell.Server.State is ServerState.Stopped or ServerState.NotConfigured)
            Shell.Server.RefreshConfigured();
        Reload();
    }

    /// <summary>
    /// После загрузки другого кванта той же модели прежний файл больше не используется — предложить удалить его
    /// (сами никогда не удаляем: файлы моделей большие, пользователь мог держать их намеренно).
    /// </summary>
    private async Task OfferDeleteOldFilesAsync(InstalledModel installed, string? previousFile)
    {
        if (string.IsNullOrWhiteSpace(previousFile)) return;
        IReadOnlyList<string> orphans;
        long size;
        try
        {
            (orphans, size) = await Task.Run(() =>
            {
                var files = ModelManager.OrphanFiles(previousFile);
                return (files, files.Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0L));
            });
        }
        catch (Exception ex)
        {
            Log.Debug("models", $"Поиск файлов прежней версии: {ex.Message}");
            return;
        }
        if (orphans.Count == 0 || IsDisposed) return;

        // Сервер держит открытым старый файл, если эта модель активна и запущена: перед удалением — перезапуск с новым.
        var serverHolds = ConfigStore.Current.ActiveModel()?.Id == installed.Id &&
                          Shell.Server.State is ServerState.Running or ServerState.Starting;
        var text = L.F("Старый файл этой модели больше не используется:{0}{1}{0}{0}Освободится {2}.",
            Environment.NewLine, string.Join(Environment.NewLine, orphans), FileUtil.FormatBytes(size));
        if (serverHolds)
            text += Environment.NewLine + Environment.NewLine + L.T("Сервер сейчас работает со старым файлом — перед удалением он будет перезапущен с новым.");

        var yes = new TaskDialogButton(L.T("Удалить"));
        var keep = new TaskDialogButton(L.T("Оставить"));
        var page = new TaskDialogPage
        {
            Caption = Ui.Caption,
            Heading = L.F("Удалить прежнюю версию «{0}»?", Texts.ModelName(installed)),
            Text = text,
            Icon = TaskDialogIcon.Information,
            Buttons = { yes, keep },
            DefaultButton = keep,
            AllowCancel = true,
        };
        var owner = Owner;
        var result = owner is null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page);
        if (result != yes) return;

        await RunBusyAsync(async () =>
        {
            if (serverHolds && !await Shell.Server.RestartAsync() && Shell.Server.State is ServerState.Running or ServerState.Starting)
                await Shell.Server.StopAsync();
            var deleted = await Task.Run(() => ModelManager.DeleteOrphanFiles(previousFile));
            Log.Info("models", $"Удалены файлы прежней версии {installed.Id}: {string.Join(", ", deleted)}");
        }, L.T("Не удалось удалить файлы прежней версии модели"));
    }

    private async Task ActivateAsync()
    {
        var row = ModelListBinder.Selected(_list);
        if (row?.Installed is not { } inst || !CanBeActive(row)) return;
        // Модели новой архитектуры нужна свежая llama.cpp (minLlamaBuild каталога).
        if (ModelManager.LlamaBuildWarning(inst.Id, ConfigStore.Current) is { } warning &&
            !Ui.Confirm(Owner, L.F("{0}{1}{1}Всё равно сделать модель активной?", warning, Environment.NewLine), warning: true))
            return;
        await RunBusyAsync(() => Shell.SwitchModelAsync(inst.Id, Owner), L.T("Не удалось сменить модель"));
        Reload();
    }

    private async Task RemoveAsync()
    {
        var row = ModelListBinder.Selected(_list);
        if (row?.Installed is not { } inst) return;

        var verification = new TaskDialogVerificationCheckBox(L.T("Удалить файлы модели с диска"), !inst.IsCustom);
        var yes = new TaskDialogButton(L.T("Удалить"));
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = Ui.Caption,
            Heading = L.F("Удалить модель «{0}»?", Texts.ModelName(inst)),
            Text = inst.IsCustom
                ? L.F("Модель будет убрана из списка Offload.{0}Файл: {1}{0}{0}Это ваш собственный файл — удаляйте его с диска, только если он больше не нужен.", Environment.NewLine, inst.FilePath)
                : L.F("Модель будет убрана из списка Offload.{0}Файл: {1} ({2})", Environment.NewLine, inst.FilePath, FileUtil.FormatBytes(inst.SizeBytes)),
            Icon = TaskDialogIcon.Warning,
            Verification = verification,
            Buttons = { yes, cancel },
            DefaultButton = cancel,
            AllowCancel = true,
        };
        var owner = Owner;
        var result = owner is null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page);
        if (result != yes) return;
        var deleteFiles = verification.Checked;

        await RunBusyAsync(async () =>
        {
            var cfg = ConfigStore.Current;
            var isActive = cfg.ActiveModel()?.Id == inst.Id;
            if (isActive && Shell.Server.State is ServerState.Running or ServerState.Starting)
                await Shell.Server.StopAsync();
            // Сервер роли держит файл модели открытым — остановить до удаления.
            foreach (var role in ModelRoleConfig.Auxiliary.Where(r => string.Equals(cfg.RoleModel(r)?.Id, inst.Id, StringComparison.OrdinalIgnoreCase)))
                await Shell.Server.Aux.StopAsync(role);
            await Task.Run(() => ModelManager.Remove(inst.Id, deleteFiles));
            Log.Info("models", $"Модель удалена: {inst.DisplayName} (файлы {(deleteFiles ? "удалены" : "оставлены")})");
        }, L.T("Не удалось удалить модель"));
        Shell.ConfigChanged();
        Reload();
    }

    private async Task AddCustomAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = L.T("Выберите файл модели GGUF"),
            Filter = L.T("Модели GGUF (*.gguf)|*.gguf|Все файлы (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(ModelsDir()) ? ModelsDir() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        var path = dlg.FileName;
        InstalledModel? added = null;
        await RunBusyAsync(async () =>
        {
            added = await Task.Run(() => ModelManager.AddCustom(path));
            Log.Info("models", $"Добавлена своя модель: {added.DisplayName} ({path})");
        }, L.T("Не удалось добавить модель"));
        Shell.ConfigChanged();
        Reload();
        if (added is null) return;
        if (ConfigStore.Current.ActiveModel()?.Id != added.Id &&
            Ui.Confirm(Owner, L.F("Модель «{0}» добавлена. Сделать её активной?", added.DisplayName)))
        {
            await Shell.SwitchModelAsync(added.Id, Owner);
            Reload();
        }
    }

    private static void OpenModelsFolder() => Ui.OpenFolder(ModelsDir());

    private void ChangeFolder()
    {
        if (!ModelsFolder.Change(Owner, Shell)) return;
        UpdateFolder();
        UpdateDisk();
    }
}
