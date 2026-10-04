using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Util;
using Offload.Models;

namespace Offload.App.Forms;

/// <summary>
/// Окно «Найти модель на Hugging Face»: поиск репозиториев GGUF, варианты квантизации с оценкой памяти для этого
/// компьютера (заголовок GGUF читается Range-запросами, без загрузки весов) и выбор кванта для загрузки.
/// Результат — <see cref="Chosen"/> (запись каталога с выбранным квантом по умолчанию); загрузку выполняет вкладка «Модели».
/// </summary>
internal sealed class HfSearchForm : Form
{
    private const int SearchLimit = 40;

    private readonly HardwareInfo? _hw;
    private readonly ServerSettings _server;
    private readonly TextBox _query = new()
    {
        Width = 340,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 3, 8, 3),
        BackColor = Theme.Input,
        ForeColor = Theme.TextPrimary,
    };
    private readonly ComboBox _sort = Kit.Combo(230);
    private readonly Button _searchButton;
    private readonly ListView _results = Kit.List(
        (L.T("Репозиторий"), 42), (L.T("Загрузки"), 11), (L.T("Нравится"), 10), (L.T("Обновлено"), 13), (L.T("Лицензия"), 24));
    private readonly Label _status = Kit.Hint("");
    private readonly Label _repoName = Kit.Label("", Theme.Semibold(11f));
    private readonly Label _repoInfo = Kit.Wrap("");
    private readonly ListView _quants = Kit.List((L.T("Квантизация"), 24), (L.T("Размер"), 16), (L.T("Оценка для этого компьютера"), 60));
    private readonly LinkLabel _openPage;
    private readonly Button _download;
    private readonly Button _close;

    private readonly Dictionary<string, Loaded> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;
    private int _detailsVersion;
    private Loaded? _current;
    private bool _searching;
    private string? _wantedQuant;

    /// <summary>Выбранная модель: квант по умолчанию (первый) — выбранный пользователем.</summary>
    public CatalogModel? Chosen { get; private set; }

    public string? ChosenQuant { get; private set; }

    /// <param name="initialQuery">Начальный запрос: название, ссылка на страницу модели или «owner/name[:квант]».</param>
    public HfSearchForm(HardwareInfo? hw, ServerSettings server, string? initialQuery = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        _hw = hw;
        _server = server;
        SuspendLayout();
        Text = L.T("Найти модель на Hugging Face");
        Icon = AppIcons.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(940, 700);
        MinimumSize = new Size(780, 580);
        BackColor = Theme.Surface;
        ForeColor = Theme.TextPrimary;
        ShowInTaskbar = false;
        MinimizeBox = false;

        _query.PlaceholderText = L.T("Название, ссылка или owner/name, например qwen coder");
        if (!string.IsNullOrWhiteSpace(initialQuery)) _query.Text = initialQuery.Trim();
        _sort.Items.AddRange([L.T("Популярные сейчас"), L.T("По загрузкам"), L.T("Недавно обновлённые"), L.T("По отметкам «нравится»")]);
        _sort.SelectedIndex = 0;
        _sort.SelectedIndexChanged += async (_, _) => await SearchAsync();
        _searchButton = Kit.Primary(L.T("Найти"), async (_, _) => await SearchAsync(), 90);
        _download = Kit.Primary(L.T("Скачать"), (_, _) => Choose());
        _close = Kit.Button(L.T("Закрыть"), (_, _) => Close());
        _openPage = Kit.ActionLink(L.T("Открыть страницу модели"), () =>
        {
            if (_current is { } c) Ui.OpenShell(HfClient.RepoUrl(c.Entry.Repo));
        });

        _results.SelectedIndexChanged += async (_, _) => await LoadSelectedAsync();
        _quants.SelectedIndexChanged += (_, _) => UpdateButtons();
        _quants.DoubleClick += (_, _) => Choose();

        var root = Kit.FillTable();
        root.Padding = new Padding(16, 12, 16, 8);
        root.AddRow(Kit.Flow(_query, _sort, _searchButton));
        root.AddRow(Kit.Hint(L.T("Показываются репозитории с файлами GGUF. Такие модели не проверены мейнтейнером Offload: параметры читаются из заголовка файла, качество ответов и вызова инструментов не гарантируется. Размеры и SHA-256 закрепляются на текущей ревизии репозитория.")));
        root.AddFillRow(_results);
        root.AddRow(_status);

        var card = new CardPanel { ColumnCount = 1, Dock = DockStyle.Fill };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Margin = new Padding(0, 6, 0, 6);
        card.AddRow(_repoName);
        card.AddRow(_repoInfo);
        card.AddFillRow(_quants);
        root.AddFillRow(card);

        var bottom = Kit.Table(100, 0);
        var buttons = Kit.Flow(_download, _close);
        buttons.WrapContents = false;
        buttons.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        bottom.AddRow(_openPage, buttons);
        root.AddRow(bottom);
        Controls.Add(root);

        AcceptButton = _searchButton;
        CancelButton = _close;
        ShowDetails(null);
        Kit.FinishForm(this);
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(this);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _query.Focus();
            await SearchAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Поиск на Hugging Face", ex);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _searchCts?.Cancel();
        _detailsCts?.Cancel();
        base.OnFormClosing(e);
    }

    private HfSort SelectedSort => _sort.SelectedIndex switch
    {
        1 => HfSort.Downloads,
        2 => HfSort.Updated,
        3 => HfSort.Likes,
        _ => HfSort.Trending,
    };

    /// <summary>Поиск (предыдущий незавершённый отменяется).</summary>
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _searchCts = cts;
        _searching = true;
        _status.ForeColor = Theme.TextMuted;
        _status.Text = L.T("Поиск на Hugging Face…");
        UpdateButtons();
        try
        {
            _wantedQuant = null;
            var found = await FindReferenceAsync(cts.Token) ?? await HfQuery.SearchAsync(_query.Text, SelectedSort, SearchLimit, cts.Token);
            if (IsDisposed || cts.IsCancellationRequested) return;
            FillResults(found);
            if (_wantedQuant is not null && _results.Items.Count == 1) _results.Items[0].Selected = true; // ссылка на модель — сразу к вариантам
            _status.Text = found.Count == 0
                ? L.T("Ничего не найдено. Попробуйте другое название (например, без номера версии).")
                : L.F("Найдено: {0}. Выберите репозиторий, чтобы увидеть варианты квантизации.", Ui.N(found.Count));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Заменён новым поиском или окно закрыто.
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            Log.Warn("hf", $"Поиск моделей: {ex.Message}");
            _status.ForeColor = Theme.ErrorText;
            _status.Text = Ui.FriendlyError(ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                _searching = false;
            }
            if (!IsDisposed) UpdateButtons();
        }
    }

    /// <summary>
    /// Если введена ссылка или «owner/name» — репозиторий напрямую (с запомненным квантом); не найден или не ссылка — null
    /// (тогда обычный поиск по названию).
    /// </summary>
    private async Task<IReadOnlyList<HfRepoInfo>?> FindReferenceAsync(CancellationToken ct)
    {
        if (HfQuery.ParseReference(_query.Text) is not { } reference) return null;
        try
        {
            var info = await HfClient.GetRepoInfoAsync(reference.Repo, ct);
            _wantedQuant = reference.Quant ?? "";
            return [info];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("hf", $"Ссылка {reference.Repo} не открылась, обычный поиск: {ex.Message}");
            return null;
        }
    }

    private void FillResults(IReadOnlyList<HfRepoInfo> found)
    {
        _results.BeginUpdate();
        try
        {
            _results.Items.Clear();
            foreach (var r in found)
            {
                var updated = r.LastModified is { } d ? d.ToLocalTime().ToString("d", L.Culture) : "—";
                var license = r.License ?? "—";
                if (r.IsGated) license = L.F("{0} · закрытый", license);
                var item = new ListViewItem([r.Repo, Ui.Short(r.Downloads), Ui.Short(r.Likes), updated, license]) { Tag = r };
                if (r.IsGated) item.ToolTipText = L.T("Закрытый репозиторий: нужен токен Hugging Face и принятые условия модели.");
                _results.Items.Add(item);
            }
        }
        finally
        {
            _results.EndUpdate();
        }
        ShowDetails(null);
    }

    /// <summary>Сведения о выбранном репозитории: ревизия, файлы, заголовок GGUF и оценка вариантов (с кэшем).</summary>
    private async Task LoadSelectedAsync()
    {
        if (_results.SelectedItems.Count == 0 || _results.SelectedItems[0].Tag is not HfRepoInfo repo) return;
        var version = ++_detailsVersion;
        _detailsCts?.Cancel();
        _current = null;

        if (_cache.TryGetValue(repo.Repo, out var cached))
        {
            ShowDetails(cached);
            return;
        }

        using var cts = new CancellationTokenSource();
        _detailsCts = cts;
        _repoName.Text = repo.Repo;
        _repoInfo.ForeColor = Theme.TextMuted;
        _repoInfo.Text = L.T("Чтение списка файлов и заголовка GGUF (веса не скачиваются)…");
        _quants.Items.Clear();
        UpdateButtons();
        try
        {
            await Task.Delay(250, cts.Token); // быстрый перебор списка стрелками не запускает лишние запросы
            var details = await HubImport.LoadDetailsAsync(repo.Repo, cts.Token);
            var hw = _hw;
            var server = _server;
            var loaded = await Task.Run(() =>
            {
                var entry = HubImport.BuildEntry(details);
                var fits = HubImport.Evaluate(entry, hw, server);
                var recommended = HubImport.Recommend(fits);
                if (recommended is not null) entry = HubImport.WithDefaultQuant(entry, recommended);
                return new Loaded(entry, details, fits, recommended);
            }, cts.Token);
            _cache[repo.Repo] = loaded;
            if (IsDisposed || version != _detailsVersion) return;
            ShowDetails(loaded);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Выбран другой репозиторий или окно закрыто.
        }
        catch (Exception ex)
        {
            if (IsDisposed || version != _detailsVersion) return;
            Log.Warn("hf", $"Сведения о {repo.Repo}: {ex.Message}");
            _repoInfo.ForeColor = Theme.ErrorText;
            _repoInfo.Text = Ui.FriendlyError(ex);
        }
        finally
        {
            if (ReferenceEquals(_detailsCts, cts)) _detailsCts = null;
            if (!IsDisposed) UpdateButtons();
        }
    }

    private void ShowDetails(Loaded? loaded)
    {
        _current = loaded;
        _quants.BeginUpdate();
        try
        {
            _quants.Items.Clear();
            if (loaded is null)
            {
                _repoName.Text = L.T("Выберите репозиторий в списке");
                _repoInfo.ForeColor = Theme.TextMuted;
                _repoInfo.Text = "";
                return;
            }

            var e = loaded.Entry;
            var h = loaded.Details.Header;
            _repoName.Text = e.Repo;
            var facts = new List<string>();
            if (!string.IsNullOrWhiteSpace(h.Architecture)) facts.Add(L.F("архитектура {0}", h.Architecture));
            if (e.ParamsB > 0) facts.Add(e.IsMoe && e.ActiveParamsB > 0
                ? L.F("{0:0.#} млрд параметров (активных {1:0.#} млрд, MoE)", e.ParamsB, e.ActiveParamsB)
                : L.F("{0:0.#} млрд параметров", e.ParamsB));
            else if (e.IsMoe) facts.Add("MoE");
            if (h.BlockCount > 0) facts.Add(Ui.Plural(h.BlockCount, "слой", "слоя", "слоёв"));
            if (e.NativeContext > 0) facts.Add(L.F("контекст до {0}", Ui.Tokens(e.NativeContext)));
            facts.Add(string.IsNullOrWhiteSpace(e.License) ? L.T("лицензия не указана") : L.F("лицензия {0}", e.License));
            facts.Add(e.GoodToolCalling ? L.T("шаблон поддерживает инструменты") : L.T("шаблон без поддержки инструментов"));
            if (e.Role == ModelKind.Embed) facts.Add(L.T("модель эмбеддингов"));
            if (e.Role == ModelKind.Rerank) facts.Add(L.T("реранкер"));

            var lines = new List<string>
            {
                string.Join(" · ", facts),
                L.F("Ревизия {0} (закреплена) · не проверено мейнтейнером Offload", e.Revision![..10]),
            };
            var color = Theme.TextMuted;
            if (e.Gated)
            {
                lines.Add(L.T("⚠ Закрытый репозиторий: для загрузки нужен токен Hugging Face («Настройки» → «Сеть») и принятые условия на странице модели."));
                color = Theme.WarnText;
            }
            if (ModelCatalog.All.Any(m => string.Equals(m.Repo, e.Repo, StringComparison.OrdinalIgnoreCase)))
                lines.Add(L.T("Эта модель есть в проверенном каталоге Offload — надёжнее скачать её оттуда."));
            if (_hw is null) lines.Add(L.T("Оборудование не определено — оценка памяти недоступна."));
            else if (loaded.Recommended is null) lines.Add(L.T("Ни один вариант не поместится в память этого компьютера."));
            _repoInfo.ForeColor = color;
            _repoInfo.Text = string.Join(Environment.NewLine, lines);

            foreach (var f in loaded.Fits)
            {
                var recommended = string.Equals(f.Quant, loaded.Recommended, StringComparison.OrdinalIgnoreCase);
                var name = recommended ? $"{f.Quant}   {ModelListBinder.RecommendedMark}" : f.Quant;
                var size = FileUtil.FormatBytes(f.Size);
                if (f.Option.IsSplit) size = $"{size} ({Ui.Plural(f.Option.Files.Count, "часть", "части", "частей")})";
                var fit = f.Fit is { } r ? $"{Texts.FitGlyph(r.Level)} {r.Explanation}" : "—";
                var item = new ListViewItem([name, size, fit]) { Tag = f, UseItemStyleForSubItems = false };
                if (recommended) item.Font = Theme.Semibold(9f);
                if (f.Fit is { } fr) item.SubItems[2].ForeColor = Texts.FitColor(fr.Level);
                item.ToolTipText = f.Fit?.Explanation ?? f.Quant;
                _quants.Items.Add(item);
            }
            var wanted = _wantedQuant;
            _wantedQuant = null;
            var select = _quants.Items.Cast<ListViewItem>()
                             .FirstOrDefault(i => !string.IsNullOrEmpty(wanted) && string.Equals(((HubQuantFit)i.Tag!).Quant, wanted, StringComparison.OrdinalIgnoreCase))
                         ?? _quants.Items.Cast<ListViewItem>()
                             .FirstOrDefault(i => string.Equals(((HubQuantFit)i.Tag!).Quant, e.DefaultQuant, StringComparison.OrdinalIgnoreCase))
                         ?? (_quants.Items.Count > 0 ? _quants.Items[0] : null);
            if (select is not null)
            {
                select.Selected = true;
                select.Focused = true;
                select.EnsureVisible();
            }
        }
        finally
        {
            _quants.EndUpdate();
            UpdateButtons();
        }
    }

    private HubQuantFit? SelectedQuant => _quants.SelectedItems.Count > 0 ? _quants.SelectedItems[0].Tag as HubQuantFit : null;

    private void UpdateButtons()
    {
        _searchButton.Enabled = !_searching;
        _download.Enabled = _current is not null && SelectedQuant is not null;
        _openPage.Enabled = _current is not null;
    }

    /// <summary>Выбрать квант и закрыть окно (загрузку начнёт вкладка «Модели»).</summary>
    private void Choose()
    {
        if (_current is not { } loaded || SelectedQuant is not { } quant) return;
        if (loaded.Entry.Gated && NetworkOptions.HfToken is null &&
            !Ui.Confirm(this, L.T("Репозиторий закрытый, а токен Hugging Face не указан — загрузка, скорее всего, не удастся. Укажите токен в разделе «Настройки» → «Сеть».\n\nВсё равно продолжить?"), warning: true))
            return;
        Chosen = HubImport.WithDefaultQuant(loaded.Entry, quant.Quant);
        ChosenQuant = quant.Quant;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record Loaded(CatalogModel Entry, HubModelDetails Details, IReadOnlyList<HubQuantFit> Fits, string? Recommended);
}
