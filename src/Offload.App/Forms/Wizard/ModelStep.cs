using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Util;
using Offload.Models;

namespace Offload.App.Forms.Wizard;

/// <summary>Шаг 3: выбор модели, квантизации и папки для моделей.</summary>
internal sealed class ModelStep : WizardStep
{
    private readonly Label _summary = Kit.Hint("");
    private readonly ListView _list = ModelListBinder.Create(full: false);
    private readonly Label _description = Kit.Wrap("");
    private readonly Label _fit = Kit.Wrap("");
    private readonly ComboBox _quant = Kit.Combo(240);
    private readonly Label _quantCaption = Kit.Label(L.T("Квантизация:"));
    private readonly TextBox _folder = Kit.TextBox(readOnly: true);
    private readonly Label _space = Kit.Wrap("");
    private readonly NumericUpDown _parallel = Kit.Number(1, ServerSettings.MaxParallel, 1, 70);
    private readonly Label _parallelHint = Kit.Hint("");
    private readonly CheckBox _remote = Kit.Check(L.T("Модель работает на другом компьютере (удалённый сервер) — не скачивать"));
    private bool _settingParallel;
    private bool _parallelTouched;
    private IReadOnlyList<(string Quant, string Text, long Size)> _quantItems = [];
    private bool _loaded;
    private bool _catalogFailed;

    public ModelStep(WizardContext ctx) : base(ctx)
    {
        _list.SelectedIndexChanged += (_, _) => OnSelected();
        _quant.SelectedIndexChanged += (_, _) =>
        {
            State.Quant = SelectedQuant();
            UpdateSpace();
            UpdateParallel();
        };
        _parallel.Value = Math.Clamp(ConfigStore.Current.Server.Parallel, 1, ServerSettings.MaxParallel);
        _parallel.ValueChanged += (_, _) =>
        {
            if (!_settingParallel) _parallelTouched = true;
        };
        _remote.CheckedChanged += (_, _) =>
        {
            State.RemoteModel = _remote.Checked;
            _list.Enabled = _quant.Enabled = _parallel.Enabled = !_remote.Checked;
            RaiseNavigationChanged();
        };

        var root = Kit.Table();
        root.AddRow(_summary);
        root.AddFixedRow(220, _list);
        root.AddRow(_description);
        root.AddRow(_fit);
        root.AddRow(Kit.Flow(_quantCaption, _quant));
        root.AddRow(Kit.Flow(Kit.Label(L.T("Параллельные запросы:")), _parallel));
        root.AddRow(_parallelHint);
        root.AddRow(_remote);
        root.AddRow(Kit.Hint(L.T("Для слабого ноутбука: модель остаётся на мощном ПК с Offload (там включите «Доступ из сети»), а адрес и ключ вводятся после мастера в «Настройках» → «Удалённый сервер».")));

        root.AddRow(Kit.Section(L.T("Папка для моделей")));
        var folderRow = Kit.Table(100, 0);
        folderRow.AddRow(_folder, Kit.Button(L.T("Изменить…"), (_, _) => ChooseFolder()));
        root.AddRow(folderRow);
        root.AddRow(_space);
        SetContent(root);
    }

    public override string Title => L.T("Модель");

    public override string Heading => L.T("Выбор модели");

    public override string? Subtitle => L.T("Рекомендуемая модель подобрана под объём видеопамяти и оперативной памяти.");

    public override bool CanGoNext => State.RemoteModel || (State.Model is not null && !Insufficient());

    /// <summary>Число слотов сервера сохраняется при переходе дальше: шаг установки запускает сервер уже с ним.</summary>
    public override bool OnLeave(bool forward)
    {
        var value = (int)_parallel.Value;
        if (forward && value != ConfigStore.Current.Server.Parallel)
            return Ui.RunSafe(Ctx.Form, () => ConfigStore.Update(c => c.Server.Parallel = value), L.T("Не удалось сохранить настройки"));
        return true;
    }

    public override void OnEnter()
    {
        var cfg = ConfigStore.Current;
        _remote.Checked = State.RemoteModel || cfg.IsRemote();
        if (string.IsNullOrWhiteSpace(State.ModelsDir))
            State.ModelsDir = Ui.Try(() => ModelManager.ModelsDir(cfg), cfg.Models.ModelsDir ?? AppPaths.DefaultModelsDir, "ModelsDir");
        _folder.Text = State.ModelsDir;
        LoadRows();
        RaiseNavigationChanged();
    }

    private void LoadRows()
    {
        var cfg = ConfigStore.Current;
        var hw = State.Hardware;
        _summary.Text = hw is null ? L.T("Оборудование не определено — оценка памяти недоступна.") : Texts.HardwareSummary(hw);
        var (rows, error) = ModelRows.Build(cfg, hw, chatOnly: true);
        _catalogFailed = error is not null;
        if (error is not null)
        {
            _summary.Text = L.F("Каталог моделей недоступен: {0}", error);
            _summary.ForeColor = Theme.ErrorText;
        }

        // Первый вход: активная установленная модель, иначе рекомендуемая.
        string? select = State.Model?.Id;
        if (!_loaded)
        {
            select = rows.FirstOrDefault(r => r.IsActive)?.Id
                     ?? rows.FirstOrDefault(r => r.IsRecommended)?.Id
                     ?? rows.FirstOrDefault(r => r.Fit?.Usable == true)?.Id
                     ?? (rows.Count > 0 ? rows[0].Id : null);
            _loaded = true;
        }
        ModelListBinder.Fill(_list, rows, full: false, select);
        OnSelected();
    }

    private void OnSelected()
    {
        var row = ModelListBinder.Selected(_list);
        State.Model = row;
        if (row is null)
        {
            _description.Text = _catalogFailed ? L.T("Без каталога моделей продолжить нельзя. Проверьте подключение к интернету и откройте мастер позже.") : L.T("Выберите модель в списке.");
            _fit.Text = "";
            _quant.Items.Clear();
            _quant.Visible = _quantCaption.Visible = false;
            UpdateSpace();
            return;
        }
        _description.Text = row.Description + (row.IsInstalled ? L.F("{0}Уже установлена — повторная загрузка не нужна.", Environment.NewLine) : "");
        if (row.Fit is { } fit)
        {
            _fit.Text = $"{Texts.FitGlyph(fit.Level)} {fit.Explanation}";
            _fit.ForeColor = Texts.FitColor(fit.Level);
        }
        else
        {
            _fit.Text = "";
        }

        var prevQuant = State.Quant;
        _quantItems = [];
        _quant.Items.Clear();
        _quantItems = row.Catalog is { } c && !row.IsInstalled ? ModelListBinder.Quants(c) : [];
        foreach (var q in _quantItems) _quant.Items.Add(q.Text);
        _quant.Visible = _quantCaption.Visible = _quantItems.Count > 0;
        if (_quantItems.Count > 0)
        {
            var idx = prevQuant is { } prev ? _quantItems.ToList().FindIndex(q => q.Quant == prev) : -1;
            _quant.SelectedIndex = Math.Max(0, idx);
        }
        State.Quant = SelectedQuant() ?? row.Installed?.Quant;
        UpdateSpace();
        UpdateParallel();
    }

    /// <summary>
    /// Рекомендация числа параллельных слотов для выбранной модели (FitCalculator): значение по умолчанию,
    /// пока пользователь не изменил его сам, и пояснение компромисса.
    /// </summary>
    private void UpdateParallel()
    {
        var row = State.Model;
        var hw = State.Hardware;
        var fm = row is null || hw is null ? null
            : row.Catalog is { } c && !row.IsInstalled ? new FitModel(c, ModelListBinder.RequiredBytes(c, SelectedQuant()), c.DefaultContext)
            : row.Installed is { } inst ? Ui.Try(() => ServerAutoPlacement.ModelFor(inst), null, "ServerAutoPlacement.ModelFor")
            : null;
        var advice = fm is null ? null : Ui.Try<SlotAdvice?>(() => ServerFit.RecommendSlots(fm, hw!, ConfigStore.Current.Server), null, "ServerFit.RecommendSlots");
        if (advice is null)
        {
            _parallelHint.Text = L.T("Сколько запросов модель обрабатывает одновременно. Больше слотов — меньше ожидания в очереди, но больше видеопамяти.");
            return;
        }

        if (!_parallelTouched)
        {
            _settingParallel = true;
            try
            {
                _parallel.Value = advice.Recommended;
            }
            finally
            {
                _settingParallel = false;
            }
        }
        var chosen = advice.Chosen;
        _parallelHint.Text = advice.Recommended > 1
            ? L.F("Рекомендуется {0} × {1} (видеопамять ≈{2} из {3} ГБ): субагенты и несколько IDE не ждут друг друга, но скорость генерации при одновременной работе делится.",
                Ui.Plural(advice.Recommended, "слот", "слота", "слотов"), Ui.Tokens(advice.ContextPerSlot),
                Gb(chosen.Fit.EstimatedVramBytes), Gb(hw!.PrimaryVramBytes))
            : L.T("Рекомендуется 1 слот: для параллельных запросов этой модели не хватит видеопамяти без замедления.");
    }

    private static string Gb(long bytes) => Math.Round(bytes / (1024d * 1024 * 1024), 1).ToString("0.#", L.Culture);

    private string? SelectedQuant() =>
        _quant.SelectedIndex >= 0 && _quant.SelectedIndex < _quantItems.Count ? _quantItems[_quant.SelectedIndex].Quant : null;

    private long Required()
    {
        var row = State.Model;
        if (row is null || row.IsInstalled || row.Catalog is null) return 0;
        return ModelListBinder.RequiredBytes(row.Catalog, SelectedQuant());
    }

    private bool Insufficient()
    {
        var need = Required();
        if (need <= 0) return false;
        var free = HardwareDetector.GetFreeDiskBytes(State.ModelsDir);
        return free > 0 && need + ModelListBinder.DiskReserveBytes > free;
    }

    private void UpdateSpace()
    {
        var need = Required();
        var free = HardwareDetector.GetFreeDiskBytes(State.ModelsDir);
        var freeText = free > 0 ? FileUtil.FormatBytes(free) : L.T("неизвестно");
        if (State.Model is null)
        {
            _space.Text = L.F("Свободно на диске: {0}", freeText);
            _space.ForeColor = Theme.TextMuted;
        }
        else if (need <= 0)
        {
            _space.Text = L.F("Загрузка не требуется. Свободно на диске: {0}", freeText);
            _space.ForeColor = Theme.TextMuted;
        }
        else if (Insufficient())
        {
            _space.Text = L.F("Недостаточно места: нужно {0}, свободно {1}. Выберите папку на другом диске или модель поменьше.",
                FileUtil.FormatBytes(need), freeText);
            _space.ForeColor = Theme.ErrorText;
        }
        else
        {
            _space.Text = L.F("Нужно: {0} · Свободно: {1}", FileUtil.FormatBytes(need), freeText);
            _space.ForeColor = Theme.OkText;
        }
        RaiseNavigationChanged();
    }

    private void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = L.T("Папка для хранения моделей (нужно много свободного места)"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(State.ModelsDir) ? State.ModelsDir : AppPaths.DataDir,
        };
        if (dlg.ShowDialog(Ctx.Form) != DialogResult.OK) return;
        State.ModelsDir = dlg.SelectedPath;
        _folder.Text = State.ModelsDir;
        UpdateSpace();
    }
}
