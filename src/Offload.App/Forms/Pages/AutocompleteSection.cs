using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Util;
using Offload.Integrations;
using Offload.Llama;
using Offload.Models;

namespace Offload.App.Forms.Pages;

/// <summary>
/// Блок «Автодополнение в IDE» страницы «Сервер»: включение, модель (только с FIM), режим «только процессор», состояние сервера,
/// адрес и ключ для копирования, подключение к Continue (файл-блок) и инструкция для llama.vscode. Настройки сохраняются сразу
/// (независимо от кнопки «Применить» страницы), сервер запускает/останавливает <see cref="AutocompleteService"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Controls are added to the page and disposed with it")]
internal sealed class AutocompleteSection
{
    private readonly IAppShell _shell;
    private readonly Control _host;
    private readonly Func<Func<Task>, string, Control[], Task<bool>> _runBusy;

    private readonly CheckBox _enabled = Kit.Check(L.T("Включить автодополнение"));
    private readonly ComboBox _model = Kit.Combo(300);
    private readonly Button _download;
    private readonly CheckBox _cpuOnly = Kit.Check(L.T("Только процессор (не занимать видеопамять основной модели)"));
    private readonly Label _fit = Kit.Wrap("");
    private readonly Label _status = Kit.Wrap("");
    private readonly TextBox _endpoint = Kit.TextBox(readOnly: true);
    private readonly Button _copyUrl;
    private readonly Button _copyKey;
    private readonly CheckBox _continue = Kit.Check(L.T("Прописать модель в Continue"));
    private readonly Label _continueStatus = Kit.Hint("", autoWidth: true);
    private readonly Label _vscodeStatus = Kit.Hint("", autoWidth: true);
    private readonly Button _copyVscode;
    private readonly ProgressPanel _progress = new();

    private readonly List<string?> _modelIds = [];
    private CatalogModel? _suggested;
    private CancellationTokenSource? _downloadCts;
    private bool _filling;
    private bool _pageBusy;
    private int _version;

    /// <param name="host">Страница: владелец диалогов и признак IsDisposed.</param>
    /// <param name="runBusy">RunBusyAsync страницы.</param>
    public AutocompleteSection(IAppShell shell, Control host, Func<Func<Task>, string, Control[], Task<bool>> runBusy)
    {
        _shell = shell;
        _host = host;
        _runBusy = runBusy;
        _download = Kit.Button(L.T("Скачать модель"), async (_, _) => await DownloadAsync(), 150);
        _copyUrl = Kit.Button(L.T("Копировать адрес"), (_, _) => Copy(_shell.Autocomplete.Snapshot().Endpoint?.BaseUrl), 130);
        _copyKey = Kit.Button(L.T("Копировать ключ"), (_, _) => Copy(_shell.Autocomplete.Snapshot().Endpoint?.ApiKey), 130);
        _copyVscode = Kit.Button(L.T("Копировать настройки llama.vscode"), (_, _) =>
        {
            if (_shell.Autocomplete.Snapshot().Endpoint is { } e) Copy(AutocompleteSetup.LlamaVscodeSnippet(e));
        }, 220);
        _endpoint.Width = 260;
        _endpoint.Anchor = AnchorStyles.Left;
        _fit.Visible = false;
        _progress.CancelRequested += (_, _) => _downloadCts?.Cancel();
        _progress.Visible = false;

        _enabled.CheckedChanged += (_, _) => Save(c => c.Autocomplete.Enabled = _enabled.Checked);
        _cpuOnly.CheckedChanged += (_, _) => Save(c => c.Autocomplete.CpuOnly = _cpuOnly.Checked);
        _continue.CheckedChanged += (_, _) => Save(c => c.Autocomplete.ConfigureContinue = _continue.Checked);
        _model.SelectedIndexChanged += async (_, _) => await AssignAsync();
    }

    /// <summary>Добавить блок строками в таблицу страницы.</summary>
    public void AddTo(TableLayoutPanel root)
    {
        root.AddRow(Kit.Section(L.T("Автодополнение в IDE")));
        root.AddRow(Kit.Hint(L.T("Маленькая coder-модель дописывает код прямо в редакторе — как Copilot, но локально. Для неё работает отдельный llama-server: он запускается вместе с Offload, пока автодополнение включено, и не зависит от основного сервера.")));
        root.AddRow(_enabled);
        var grid = Kit.Grid();
        grid.AddField(L.T("Модель:"), Kit.Flow(_model, _download));
        root.AddRow(grid);
        root.AddRow(_progress);
        root.AddRow(_cpuOnly);
        root.AddRow(_fit);
        root.AddRow(_status);
        var endpointRow = Kit.Flow(Kit.Label(L.T("Адрес:")), _endpoint, _copyUrl, _copyKey);
        endpointRow.WrapContents = true;
        root.AddRow(endpointRow);
        var continueRow = Kit.Flow(_continue, _continueStatus);
        continueRow.WrapContents = true;
        root.AddRow(continueRow);
        var vscodeRow = Kit.Flow(Kit.Label("llama.vscode:"), _vscodeStatus, _copyVscode);
        vscodeRow.WrapContents = true;
        root.AddRow(vscodeRow);
        root.AddRow(Kit.Hint(L.T("llama.vscode: установите расширение «llama-vscode» (ggml-org), откройте settings.json (Ctrl+Shift+P → «Preferences: Open User Settings (JSON)») и вставьте скопированные строки — адрес сервера и ключ API. Continue подхватывает модель сам после перезагрузки окна IDE.")));
        Refresh();
    }

    public void UpdateUiState(bool pageBusy)
    {
        _pageBusy = pageBusy;
        var downloading = _downloadCts is not null;
        _enabled.Enabled = !downloading;
        _model.Enabled = !pageBusy && _modelIds.Count > 1;
        _download.Enabled = !pageBusy && !downloading;
        var hasEndpoint = _shell.Autocomplete.Snapshot().Endpoint is not null;
        _copyUrl.Enabled = _copyKey.Enabled = _copyVscode.Enabled = hasEndpoint;
    }

    /// <summary>Перечитать настройки, список моделей и состояние (после изменения конфигурации).</summary>
    public void Refresh()
    {
        var cfg = ConfigStore.Current;
        _filling = true;
        try
        {
            _enabled.Checked = cfg.Autocomplete.Enabled;
            _cpuOnly.Checked = cfg.Autocomplete.CpuOnly;
            _continue.Checked = cfg.Autocomplete.ConfigureContinue;
            _modelIds.Clear();
            _modelIds.Add(null);
            _model.BeginUpdate();
            _model.Items.Clear();
            _model.Items.Add(L.T("— не выбрана —"));
            foreach (var m in cfg.Models.Installed.Where(m => ModelRoleConfig.IsCompatible(m, ModelRole.Fim))
                         .OrderBy(Texts.ModelName, StringComparer.CurrentCultureIgnoreCase))
            {
                _modelIds.Add(m.Id);
                _model.Items.Add(string.IsNullOrWhiteSpace(m.Quant) ? Texts.ModelName(m) : $"{Texts.ModelName(m)}  ({m.Quant})");
            }
            var assigned = cfg.RoleModel(ModelRole.Fim)?.Id;
            _model.SelectedIndex = Math.Max(0, _modelIds.FindIndex(id => string.Equals(id, assigned, StringComparison.OrdinalIgnoreCase)));
            _model.EndUpdate();
        }
        finally
        {
            _filling = false;
        }
        var (catalog, _) = ModelRows.Catalog();
        _suggested = AutocompletePolicy.Suggested(catalog, cfg);
        _download.Visible = _suggested is not null;
        if (_suggested is { } s)
            _download.Text = L.F("Скачать {0} ({1})", s.LocalizedDisplayName, FileUtil.FormatBytes(s.ApproxSizeBytes));
        UpdateStatus();
        _ = UpdateFitAsync(cfg);
    }

    /// <summary>Состояние сервера и подключённых IDE (после смены состояния, из потока интерфейса).</summary>
    public void UpdateStatus()
    {
        var st = _shell.Autocomplete.Snapshot();
        _endpoint.Text = st.Endpoint?.BaseUrl ?? "";
        (_status.Text, _status.ForeColor) = StatusText(st);

        var cont = AutocompleteSetup.ContinueStatus(st.Enabled && ConfigStore.Current.Autocomplete.ConfigureContinue ? st.Endpoint : null);
        (_continueStatus.Text, _continueStatus.ForeColor) = cont.State switch
        {
            AutocompleteTargetState.ClientNotFound => (L.T("Continue не найден"), Theme.TextMuted),
            AutocompleteTargetState.Configured => (L.F("прописано: {0}", cont.Path), Theme.OkText),
            AutocompleteTargetState.Outdated => (L.T("адрес обновится после запуска сервера"), Theme.TextMuted),
            AutocompleteTargetState.Foreign => (L.F("файл {0} создан не Offload — не трогаем", cont.Path), Theme.WarnText),
            AutocompleteTargetState.Error => (L.F("ошибка: {0}", cont.Detail), Theme.ErrorText),
            _ => (st.Enabled ? L.T("пропишется после запуска сервера") : L.T("не прописано"), Theme.TextMuted),
        };
        _continue.Enabled = cont.State != AutocompleteTargetState.ClientNotFound;

        (_vscodeStatus.Text, _vscodeStatus.ForeColor) = AutocompleteSetup.LlamaVscodeStatus().State == AutocompleteTargetState.Manual
            ? (L.T("расширение установлено — настройте вручную"), Theme.TextPrimary)
            : (L.T("расширение не найдено"), Theme.TextMuted);
        UpdateUiState(_pageBusy);
    }

    private static (string, Color) StatusText(AutocompleteStatus st)
    {
        if (!st.Enabled) return (L.T("Автодополнение выключено."), Theme.TextMuted);
        if (st.Model is null) return (L.T("Выберите или скачайте модель автодополнения."), Theme.WarnText);
        var port = st.Endpoint is { } e && Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var uri) ? uri.Port : 0;
        return st.State switch
        {
            ServerState.Running => (L.F("Сервер автодополнения работает: {0}, порт {1}.", Texts.ModelName(st.Model), port), Theme.OkText),
            ServerState.Starting => (L.F("Сервер автодополнения загружает модель {0}…", Texts.ModelName(st.Model)), Theme.TextMuted),
            _ when st.Error is { } err => (L.F("Сервер автодополнения не запустился: {0}", err), Theme.ErrorText),
            _ when st.Deferred => (L.T("Сервер автодополнения запустится после основного."), Theme.TextMuted),
            _ => (L.T("Сервер автодополнения запускается…"), Theme.TextMuted),
        };
    }

    private async Task UpdateFitAsync(AppConfig cfg)
    {
        var version = ++_version;
        FimFit verdict;
        RoleFootprint? item = null;
        try
        {
            var hw = await _shell.Hardware.GetAsync();
            var (catalog, _) = ModelRows.Catalog();
            var split = await ServerAutoPlacement.RoleBudgetSplitAsync(cfg, hw);
            var budget = await Task.Run(() => RoleBudget.Evaluate(cfg, hw, catalog, ReadHeader, includeFim: true, split: split));
            verdict = AutocompletePolicy.AssessFit(budget, cfg.Autocomplete.CpuOnly);
            item = budget.Items.FirstOrDefault(i => i.Role == ModelRole.Fim);
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Оценка памяти автодополнения: {ex.Message}");
            verdict = FimFit.Unknown;
        }
        if (_host.IsDisposed || version != _version) return;
        _fit.Visible = verdict != FimFit.Unknown;
        var size = item is null ? "" : FileUtil.FormatBytes(verdict == FimFit.Cpu ? item.RamBytes : item.VramBytes);
        (_fit.Text, _fit.ForeColor) = verdict switch
        {
            FimFit.Gpu => (L.F("Помещается в видеопамять рядом с основной моделью (≈{0}).", size), Theme.TextMuted),
            FimFit.Cpu => (L.F("Работает на процессоре (≈{0} ОЗУ): подсказки появляются медленнее, видеопамять не занята.", size), Theme.TextMuted),
            FimFit.NotBesideMain => (L.T("⚠ Рядом с основной моделью не помещается в видеопамять: часть слоёв уйдёт на процессор или основная модель станет медленнее. Включите «Только процессор» или выберите модель поменьше."), Theme.WarnText),
            FimFit.NoMemory => (L.T("⚠ Не хватает оперативной памяти для модели автодополнения вместе с остальными — выберите модель поменьше или снимите другие роли."), Theme.WarnText),
            _ => ("", Theme.TextMuted),
        };
    }

    private void Save(Action<AppConfig> change)
    {
        if (_filling) return;
        try
        {
            ConfigStore.Update(change);
        }
        catch (Exception ex)
        {
            Ui.ShowError(_host.FindForm(), L.T("Не удалось сохранить настройки"), ex);
            return;
        }
        _shell.ConfigChanged();
    }

    private async Task AssignAsync()
    {
        if (_filling || _model.SelectedIndex < 0 || _model.SelectedIndex >= _modelIds.Count) return;
        var id = _modelIds[_model.SelectedIndex];
        await _runBusy(() => Task.Run(() => ModelManager.AssignRole(ModelRole.Fim, id)), L.T("Не удалось назначить модель"), [_model]);
        _shell.ConfigChanged();
    }

    private async Task DownloadAsync()
    {
        if (_suggested is not { } model) return;
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        _progress.Visible = true;
        _progress.Reset();
        _progress.Start(L.F("Загрузка «{0}»…", model.LocalizedDisplayName));
        UpdateUiState(_pageBusy);
        try
        {
            await _runBusy(async () =>
            {
                try
                {
                    var installed = await ModelManager.DownloadAsync(model, null, _progress.CreateProgress(), cts.Token);
                    _progress.Finish(L.F("Модель «{0}» установлена.", Texts.ModelName(installed)), true);
                    Log.Info("models", $"Модель автодополнения установлена: {installed.DisplayName} ({installed.FilePath})");
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
            }, L.T("Не удалось скачать модель"), [_download]);
        }
        finally
        {
            _downloadCts = null;
            if (!_host.IsDisposed) UpdateUiState(_pageBusy);
        }
        _shell.ConfigChanged();
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!Ui.TrySetClipboard(text)) Ui.Warn(_host.FindForm(), L.T("Не удалось скопировать в буфер обмена — попробуйте ещё раз."));
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
}
