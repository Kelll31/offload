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

/// <summary>Вкладка «Модели»: каталог, загрузка, выбор активной модели, свои GGUF-файлы.</summary>
internal sealed class ModelsPage : PageBase
{
    private readonly Label _hardware = Kit.Hint("Определение оборудования…");
    private readonly ListView _list = ModelListBinder.Create(full: true);
    private readonly Label _name = Kit.Label("Выберите модель в списке", Theme.Semibold(11f));
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

    private HardwareInfo? _hw;
    private CancellationTokenSource? _downloadCts;
    private string? _downloadingName;
    private IReadOnlyList<(string Quant, string Text, long Size)> _quantItems = [];

    public ModelsPage(IAppShell shell) : base(shell)
    {
        _download = Kit.Primary("Скачать", async (_, _) => await DownloadAsync());
        _activate = Kit.Button("Сделать активной", async (_, _) => await ActivateAsync());
        _remove = Kit.Button("Удалить", async (_, _) => await RemoveAsync());
        _addCustom = Kit.Button("Добавить свой GGUF…", async (_, _) => await AddCustomAsync(), 150);
        _changeFolder = Kit.Button("Изменить папку…", (_, _) => ChangeFolder(), 120);

        _list.SelectedIndexChanged += (_, _) => ShowDetails();
        _list.DoubleClick += async (_, _) =>
        {
            if (ModelListBinder.Selected(_list) is { IsInstalled: true, IsActive: false }) await ActivateAsync();
        };
        _quant.SelectedIndexChanged += (_, _) => UpdateDisk();
        _progress.CancelRequested += (_, _) => _downloadCts?.Cancel();

        var root = Kit.FillTable();
        root.Padding = new Padding(16, 12, 16, 12);
        root.AddRow(_hardware);
        root.AddFillRow(_list);

        var card = new CardPanel { ColumnCount = 1 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Margin = new Padding(0, 6, 0, 6);
        card.AddRow(_name);
        card.AddRow(_description);
        card.AddRow(_fit);
        var quantRow = Kit.Flow(Kit.Label("Квантизация:"), _quant, _quantHint);
        quantRow.WrapContents = true;
        card.AddRow(quantRow);
        card.AddRow(Kit.Flow(_download, _activate, _remove));
        card.AddRow(_progress);
        root.AddRow(card);

        var folderRow = Kit.Table(100, 0);
        folderRow.AddRow(_folder, Kit.Flow(_addCustom, Kit.Button("Открыть папку моделей", (_, _) => OpenModelsFolder(), 150), _changeFolder));
        root.AddRow(folderRow);
        root.AddRow(_disk);

        Controls.Add(root);
        UpdateFolder();
        ShowDetails();
    }

    public override string Key => Tabs.Models;

    public override string Title => "Модели";

    public override string? BusyDescription => _downloadingName is null ? null : $"загрузка модели «{_downloadingName}»";

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
            if (_hw is null) _hardware.Text = "Не удалось определить оборудование — оценка видеопамяти недоступна.";
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

    public override void OnServerStateChanged() => UpdateUiState();

    /// <summary>Перестроить список (выделение сохраняется).</summary>
    private void Reload()
    {
        var cfg = ConfigStore.Current;
        if (_hw is not null) _hardware.Text = Texts.HardwareSummary(_hw);
        var (rows, error) = ModelRows.Build(cfg, _hw);
        if (error is not null) _hardware.Text = $"Каталог моделей недоступен: {error}";
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
    }

    private void ShowDetails()
    {
        var row = ModelListBinder.Selected(_list);
        if (row is null)
        {
            _name.Text = _list.Items.Count == 0 ? "Список моделей пуст" : "Выберите модель в списке";
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
            if (c.ParamsB > 0) facts.Add(c.IsMoe ? $"{c.ParamsB:0.#} млрд параметров (активных {c.ActiveParamsB:0.#} млрд, MoE)" : $"{c.ParamsB:0.#} млрд параметров");
            if (c.NativeContext > 0) facts.Add($"контекст до {Ui.Tokens(c.NativeContext)}");
            if (!string.IsNullOrWhiteSpace(c.License)) facts.Add($"лицензия {c.License}");
            if (c.GoodToolCalling) facts.Add("надёжно вызывает инструменты");
            if (facts.Count > 0) desc = $"{desc}{Environment.NewLine}{string.Join(" · ", facts)}";
        }
        if (row.Installed is { } inst) desc = $"{desc}{Environment.NewLine}Файл: {inst.FilePath}";
        _description.Text = desc;

        if (row.Fit is { } fit)
        {
            _fit.Text = $"{Texts.FitGlyph(fit.Level)} {fit.Explanation}";
            _fit.ForeColor = Texts.FitColor(fit.Level);
        }
        else
        {
            _fit.Text = _hw is null ? "Оценка появится после определения оборудования." : "";
            _fit.ForeColor = Theme.TextMuted;
        }

        _quant.Items.Clear();
        _quantItems = row.Catalog is { } cat ? ModelListBinder.Quants(cat) : [];
        foreach (var q in _quantItems) _quant.Items.Add(q.Text);
        if (_quantItems.Count > 0)
        {
            var current = row.Installed?.Quant;
            var idx = current is null ? 0 : Math.Max(0, _quantItems.ToList().FindIndex(q => string.Equals(q.Quant, current, StringComparison.OrdinalIgnoreCase)));
            _quant.SelectedIndex = idx;
            _quantHint.Text = "первая — рекомендуемая";
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
        _download.Text = row?.IsInstalled == true ? "Скачать заново" : "Скачать";
        _activate.Enabled = !IsBusy && row is { IsInstalled: true, IsActive: false };
        _remove.Enabled = !IsBusy && row is { IsInstalled: true };
        _quant.Enabled = !downloading && _quant.Items.Count > 1;
        _addCustom.Enabled = !IsBusy;
        _changeFolder.Enabled = !downloading;
    }

    private string? SelectedQuant() =>
        _quant.SelectedIndex >= 0 && _quant.SelectedIndex < _quantItems.Count ? _quantItems[_quant.SelectedIndex].Quant : null;

    private string ModelsDir()
    {
        var cfg = ConfigStore.Current;
        return Ui.Try(() => ModelManager.ModelsDir(cfg), cfg.Models.ModelsDir ?? AppPaths.DefaultModelsDir, "ModelsDir");
    }

    private void UpdateFolder()
    {
        var dir = ModelsDir();
        var free = HardwareDetector.GetFreeDiskBytes(dir);
        _folder.Text = $"Папка моделей: {dir}{Environment.NewLine}Свободно на диске: {(free > 0 ? FileUtil.FormatBytes(free) : "неизвестно")}";
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
            _disk.Text = $"Недостаточно места на диске: нужно {FileUtil.FormatBytes(need)}, свободно {FileUtil.FormatBytes(free)}. " +
                         "Освободите место или выберите другую папку моделей.";
        }
        else
        {
            _disk.ForeColor = Theme.TextMuted;
            _disk.Text = need > 0 ? $"Для загрузки нужно ≈{FileUtil.FormatBytes(need)} свободного места." : "";
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
            !Ui.Confirm(Owner, $"Похоже, модель «{model.DisplayName}» не поместится в память этого компьютера:{Environment.NewLine}{row.Fit.Explanation}{Environment.NewLine}{Environment.NewLine}Всё равно скачать?", warning: true))
            return;

        var quant = SelectedQuant();
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        _downloadingName = model.DisplayName;
        _progress.Reset();
        _progress.Start($"Загрузка «{model.DisplayName}»…");
        InstalledModel? installed = null;
        try
        {
            await RunBusyAsync(async () =>
            {
                try
                {
                    installed = await ModelManager.DownloadAsync(model, quant, _progress.CreateProgress(), cts.Token);
                    _progress.Finish($"Модель «{installed.DisplayName}» установлена.", true);
                    Log.Info("models", $"Модель установлена: {installed.DisplayName} ({installed.FilePath})");
                }
                catch (OperationCanceledException)
                {
                    _progress.Finish("Загрузка отменена. Скачанная часть сохранена — при повторной загрузке она продолжится.", false);
                    throw;
                }
                catch (Exception ex)
                {
                    _progress.Finish("Ошибка загрузки: " + Ui.FriendlyError(ex), false);
                    throw;
                }
            }, "Не удалось скачать модель", _addCustom);
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
        Shell.Notify("Модель скачана", $"«{installed.DisplayName}» готова к работе.");
        var active = ConfigStore.Current.ActiveModel();
        if (active?.Id != installed.Id &&
            Ui.Confirm(Owner, $"Модель «{installed.DisplayName}» установлена. Сделать её активной?"))
            await Shell.SwitchModelAsync(installed.Id, Owner);
        else if (active?.Id == installed.Id && Shell.Server.State is ServerState.Stopped or ServerState.NotConfigured)
            Shell.Server.RefreshConfigured();
        Reload();
    }

    private async Task ActivateAsync()
    {
        var row = ModelListBinder.Selected(_list);
        if (row?.Installed is not { } inst) return;
        await RunBusyAsync(() => Shell.SwitchModelAsync(inst.Id, Owner), "Не удалось сменить модель");
        Reload();
    }

    private async Task RemoveAsync()
    {
        var row = ModelListBinder.Selected(_list);
        if (row?.Installed is not { } inst) return;

        var verification = new TaskDialogVerificationCheckBox("Удалить файлы модели с диска", !inst.IsCustom);
        var yes = new TaskDialogButton("Удалить");
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = Ui.Caption,
            Heading = $"Удалить модель «{inst.DisplayName}»?",
            Text = inst.IsCustom
                ? $"Модель будет убрана из списка Offload.{Environment.NewLine}Файл: {inst.FilePath}{Environment.NewLine}{Environment.NewLine}Это ваш собственный файл — удаляйте его с диска, только если он больше не нужен."
                : $"Модель будет убрана из списка Offload.{Environment.NewLine}Файл: {inst.FilePath} ({FileUtil.FormatBytes(inst.SizeBytes)})",
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
            await Task.Run(() => ModelManager.Remove(inst.Id, deleteFiles));
            Log.Info("models", $"Модель удалена: {inst.DisplayName} (файлы {(deleteFiles ? "удалены" : "оставлены")})");
        }, "Не удалось удалить модель");
        Shell.ConfigChanged();
        Reload();
    }

    private async Task AddCustomAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файл модели GGUF",
            Filter = "Модели GGUF (*.gguf)|*.gguf|Все файлы (*.*)|*.*",
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
        }, "Не удалось добавить модель");
        Shell.ConfigChanged();
        Reload();
        if (added is null) return;
        if (ConfigStore.Current.ActiveModel()?.Id != added.Id &&
            Ui.Confirm(Owner, $"Модель «{added.DisplayName}» добавлена. Сделать её активной?"))
        {
            await Shell.SwitchModelAsync(added.Id, Owner);
            Reload();
        }
    }

    private void OpenModelsFolder() => Ui.OpenFolder(ModelsDir());

    private void ChangeFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Папка для хранения моделей (нужно много свободного места)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = ModelsDir(),
        };
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        var path = dlg.SelectedPath;
        if (string.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(ModelsDir()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        if (!Ui.RunSafe(Owner, () =>
            {
                Directory.CreateDirectory(path);
                ConfigStore.Update(c => c.Models.ModelsDir = path);
            }, "Не удалось сменить папку моделей")) return;
        Log.Info("models", $"Папка моделей: {path}");
        Ui.Info(Owner,
            $"Новые модели будут сохраняться в папку:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}" +
            "Уже скачанные модели не перемещаются: они остаются на прежнем месте и продолжают работать.");
        Shell.ConfigChanged();
        UpdateFolder();
        UpdateDisk();
    }
}
