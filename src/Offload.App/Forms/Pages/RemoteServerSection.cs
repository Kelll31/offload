using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Security;
using Offload.Llama;

namespace Offload.App.Forms.Pages;

/// <summary>
/// Блоки страницы «Настройки» для работы модели по сети (ROADMAP §9.2):
/// «Доступ из сети» — этот компьютер раздаёт свою основную модель (llama-server слушает адрес сети, сетевой ключ обязателен);
/// «Удалённый сервер» — основная модель работает на другом компьютере, свой основной сервер не запускается.
/// </summary>
internal sealed class RemoteServerSection
{
    private readonly IAppShell _shell;
    private readonly Control _host;
    private readonly Func<Func<Task>, string, Control[], Task<bool>> _runBusy;

    // Доступ из сети (этот компьютер — сервер)
    private readonly CheckBox _lanEnabled = Kit.Check(L.T("Разрешить другим компьютерам сети использовать основную модель этого компьютера"));
    private readonly ComboBox _bind = Kit.Combo(320);
    private readonly TextBox _lanKey = SecretBox(readOnly: true);
    private readonly Button _showLanKey;
    private readonly Button _copyLanKey;
    private readonly Button _newLanKey;
    private readonly Button _copyUrl;
    private readonly Button _applyLan;
    private readonly Label _lanStatus = Kit.Wrap("");
    private readonly Label _lanWarning = Kit.Wrap("");
    private List<string> _bindValues = [];

    // Удалённый сервер (модель на другом компьютере)
    private readonly CheckBox _remoteEnabled = Kit.Check(L.T("Использовать модель на другом компьютере (удалённый сервер)"));
    private readonly TextBox _remoteUrl = Kit.TextBox();
    private readonly TextBox _remoteKey = SecretBox(readOnly: false);
    private readonly Label _remoteKeyStatus = Kit.Hint("");
    private readonly Button _check;
    private readonly Button _saveRemote;
    private readonly Label _remoteStatus = Kit.Wrap("");

    /// <param name="shell">Трей: сервер (перезапуск для применения), уведомление об изменении настроек.</param>
    /// <param name="host">Страница: владелец диалогов и признак IsDisposed.</param>
    /// <param name="runBusy">RunBusyAsync страницы: долгая операция с блокировкой кнопок и показом ошибок.</param>
    public RemoteServerSection(IAppShell shell, Control host, Func<Func<Task>, string, Control[], Task<bool>> runBusy)
    {
        _shell = shell;
        _host = host;
        _runBusy = runBusy;
        _showLanKey = Kit.Button(L.T("Показать"), (_, _) => ToggleLanKey(), 100);
        _copyLanKey = Kit.Button(L.T("Копировать"), (_, _) => CopyLanKey(), 110);
        _newLanKey = Kit.Button(L.T("Новый ключ"), async (_, _) => await NewLanKeyAsync(), 120);
        _copyUrl = Kit.Button(L.T("Копировать адрес"), (_, _) => CopyUrl(), 150);
        _applyLan = Kit.Primary(L.T("Применить"), async (_, _) => await ApplyLanAsync(), 130);
        _check = Kit.Button(L.T("Проверить подключение"), async (_, _) => await CheckRemoteAsync(), 190);
        _saveRemote = Kit.Primary(L.T("Сохранить"), async (_, _) => await SaveRemoteAsync(), 130);
        _lanWarning.ForeColor = Theme.WarnText;
        _lanWarning.Text = L.T("Трафик между компьютерами не шифруется (HTTP): код, который модель читает и пишет, и ключ API передаются открытым текстом. Включайте только в доверенной домашней или офисной сети либо поверх VPN (Tailscale, WireGuard) или туннеля «ssh -L». При первом запуске Windows спросит разрешение брандмауэра для llama-server — разрешите доступ только для частных сетей.");
        _remoteUrl.Anchor = AnchorStyles.Left;
        _remoteUrl.Width = 320;
        _remoteUrl.PlaceholderText = "http://192.168.1.10:8765"; // l10n-ignore — пример адреса
        _lanEnabled.CheckedChanged += (_, _) => _bind.Enabled = _lanEnabled.Checked;
    }

    /// <summary>Поле для ключа: скрытые символы, фиксированная ширина (в строке с кнопками).</summary>
    private static TextBox SecretBox(bool readOnly)
    {
        var box = Kit.TextBox(readOnly: readOnly);
        box.UseSystemPasswordChar = true;
        box.Anchor = AnchorStyles.Left;
        box.Width = 300;
        return box;
    }

    /// <summary>Есть несохранённые правки (адрес/ключ удалённого сервера или переключатели не применены).</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            var cfg = ConfigStore.Current;
            var r = cfg.Remote ?? new RemoteServerSettings();
            return _remoteKey.TextLength > 0
                   || _remoteEnabled.Checked != r.Enabled
                   || !string.Equals(NormalizedOrRaw(_remoteUrl.Text), r.Url ?? "", StringComparison.OrdinalIgnoreCase)
                   || _lanEnabled.Checked != cfg.Server.LanAccess
                   || (_lanEnabled.Checked && !string.Equals(SelectedBind(), cfg.Server.LanBindAddress, StringComparison.Ordinal));
        }
    }

    /// <summary>Добавить блоки строками в таблицу страницы.</summary>
    public void AddTo(TableLayoutPanel root)
    {
        root.AddRow(Kit.Section(L.T("Доступ из сети")));
        root.AddRow(Kit.Wrap(L.T("Этот компьютер раздаёт свою основную модель другим компьютерам с Offload (например, слабому ноутбуку). Вспомогательные модели (быстрая, эмбеддинги, реранк) по-прежнему доступны только на этом компьютере.")));
        root.AddRow(_lanEnabled);
        var lan = Kit.Grid();
        lan.AddField(L.T("Адрес сети:"), _bind, L.T("0.0.0.0 — все сети сразу"));
        var keyRow = Kit.Flow(_lanKey, _showLanKey, _copyLanKey, _newLanKey);
        keyRow.WrapContents = false;
        keyRow.Margin = Padding.Empty;
        lan.AddField(L.T("Ключ для сети:"), keyRow);
        root.AddRow(lan);
        root.AddRow(_lanStatus);
        root.AddRow(Kit.Flow(_applyLan, _copyUrl));
        root.AddRow(_lanWarning);

        root.AddRow(Kit.Section(L.T("Удалённый сервер")));
        root.AddRow(Kit.Wrap(L.T("Основная модель работает на другом компьютере — обычно на мощном ПК с Offload, где включён «Доступ из сети». Свой основной сервер llama.cpp на этом компьютере тогда не запускается, модель скачивать не нужно.")));
        root.AddRow(_remoteEnabled);
        var remote = Kit.Grid();
        remote.AddField(L.T("Адрес сервера:"), _remoteUrl, L.T("http://адрес:порт — как показано в «Доступе из сети» на том компьютере"));
        remote.AddField(L.T("Ключ API:"), _remoteKey);
        root.AddRow(remote);
        root.AddRow(_remoteKeyStatus);
        root.AddRow(Kit.Flow(_saveRemote, _check));
        root.AddRow(_remoteStatus);
        Load(force: true);
    }

    /// <summary>
    /// Заполнить поля из настроек (при открытии страницы и после внешних изменений конфигурации). Несохранённые правки
    /// пользователя не затираются (трей сам обновляет config.json — например, id модели удалённого сервера), кроме force.
    /// </summary>
    public void Load(bool force = false)
    {
        var cfg = ConfigStore.Current;
        if (!force && HasUnsavedChanges)
        {
            UpdateLanStatus(cfg);
            UpdateRemoteKeyStatus(cfg);
            return;
        }
        var s = cfg.Server;
        _lanEnabled.Checked = s.LanAccess;
        _bindValues = [LanServer.AnyAddress, .. Ui.Try<IReadOnlyList<string>>(LanServer.LocalIPv4Addresses, [], "LocalIPv4Addresses")];
        var saved = s.LanBindAddress?.Trim() ?? LanServer.AnyAddress;
        if (LanServer.IsValidBindAddress(saved) && !_bindValues.Contains(saved)) _bindValues.Add(saved);
        _bind.Items.Clear();
        foreach (var v in _bindValues)
            _bind.Items.Add(v == LanServer.AnyAddress ? L.T("Все сетевые интерфейсы (0.0.0.0)") : v);
        _bind.SelectedIndex = Math.Max(0, _bindValues.IndexOf(saved));
        _bind.Enabled = _lanEnabled.Checked;
        _lanKey.UseSystemPasswordChar = true;
        _showLanKey.Text = L.T("Показать");
        _lanKey.Text = Ui.Try(() => s.LanApiKey(), null, "LanApiKey") ?? "";
        UpdateLanStatus(cfg);

        var r = cfg.Remote ?? new RemoteServerSettings();
        _remoteEnabled.Checked = r.Enabled;
        _remoteUrl.Text = r.Url ?? "";
        _remoteKey.Clear();
        UpdateRemoteKeyStatus(cfg);
        if (_remoteStatus.Text.Length == 0) UpdateRemoteStatus(cfg);
    }

    /// <summary>Состояние сервера изменилось (подключение к удалённому серверу, задержка, перезапуск со сменой адреса).</summary>
    public void OnServerStateChanged()
    {
        if (_host.IsDisposed) return;
        var cfg = ConfigStore.Current;
        UpdateRemoteStatus(cfg);
        UpdateLanStatus(cfg);
    }

    /// <summary>
    /// Адрес сети, который слушает запущенный основной сервер (--host его процесса), если он не петлевой; null — сервер
    /// не запущен или слушает только этот компьютер. Настройки могли уже поменяться, а процесс — ещё нет.
    /// </summary>
    private string? RunningNetworkHost()
    {
        var server = _shell.Server;
        if (server.State is not (ServerState.Running or ServerState.Starting) || server.ProcessId is null) return null;
        var host = server.Plan?.ListenHost;
        return string.IsNullOrWhiteSpace(host) || LanServer.IsLoopbackHost(host) ? null : host;
    }

    // ── Доступ из сети ────────────────────────────────────────────────────────────

    private string SelectedBind() =>
        _bind.SelectedIndex >= 0 && _bind.SelectedIndex < _bindValues.Count ? _bindValues[_bind.SelectedIndex] : LanServer.AnyAddress;

    private void UpdateLanStatus(AppConfig cfg)
    {
        var s = cfg.Server;
        var hasKey = _lanKey.TextLength > 0;
        _showLanKey.Enabled = _copyLanKey.Enabled = hasKey;
        // Состояние — по запущенному процессу: выключенный в настройках режим действует только после перезапуска сервера.
        var listening = RunningNetworkHost();
        if (listening is not null && LanServer.ListenerOutdated(listening, s))
        {
            _lanStatus.Text = L.F("Сервер всё ещё слушает сеть ({0}) с прежними настройками — перезапустите его, чтобы применить изменения.", listening);
            _lanStatus.ForeColor = Theme.WarnText;
            _copyUrl.Enabled = false;
            return;
        }
        if (!s.LanAccess)
        {
            _lanStatus.Text = L.T("Выключено: основной сервер доступен только программам этого компьютера (127.0.0.1).");
            _lanStatus.ForeColor = Theme.TextMuted;
            _copyUrl.Enabled = false;
            return;
        }
        if (!s.IsActive() || !hasKey)
        {
            _lanStatus.Text = L.T("Не действует: нет сетевого ключа — нажмите «Новый ключ» или «Применить». Без ключа сервер в сеть не открывается.");
            _lanStatus.ForeColor = Theme.WarnText;
            _copyUrl.Enabled = false;
            return;
        }
        var urls = LanServer.ConnectUrls(s);
        _copyUrl.Enabled = urls.Count > 0;
        var text = urls.Count == 0
            ? L.T("Включено, но у компьютера не найдено сетевых адресов IPv4.")
            : L.F("Включено. Адрес для подключения с другого компьютера: {0}", string.Join("  ·  ", urls));
        if (cfg.IsRemote()) text += " " + L.T("Сейчас включён «Удалённый сервер» — свой основной сервер не запускается, поэтому раздавать нечего.");
        _lanStatus.Text = text;
        _lanStatus.ForeColor = Theme.TextPrimary;
    }

    private void ToggleLanKey()
    {
        _lanKey.UseSystemPasswordChar = !_lanKey.UseSystemPasswordChar;
        _showLanKey.Text = _lanKey.UseSystemPasswordChar ? L.T("Показать") : L.T("Скрыть");
    }

    private void CopyLanKey()
    {
        if (_lanKey.TextLength == 0) return;
        if (Ui.TrySetClipboard(_lanKey.Text))
            _shell.Notify(L.T("Ключ скопирован"), L.T("Сетевой ключ скопирован в буфер обмена — вставьте его на другом компьютере в «Настройки» → «Удалённый сервер»."), ToolTipIcon.Info, force: true);
        else
            Ui.Warn(_host.FindForm(), L.T("Не удалось скопировать ключ в буфер обмена."));
    }

    private void CopyUrl()
    {
        var urls = LanServer.ConnectUrls(ConfigStore.Current.Server);
        if (urls.Count == 0) return;
        if (!Ui.TrySetClipboard(urls[0])) Ui.Warn(_host.FindForm(), L.T("Не удалось скопировать адрес в буфер обмена."));
    }

    private async Task NewLanKeyAsync()
    {
        var owner = _host.FindForm();
        if (_lanKey.TextLength > 0 && !Ui.Confirm(owner, L.T("Создать новый сетевой ключ? Компьютеры, подключённые со старым ключом, перестанут получать ответы, пока вы не введёте на них новый."), warning: true))
            return;
        if (!Ui.RunSafe(owner, () =>
            {
                var protectedKey = Dpapi.Protect(LanServer.NewLanApiKey());
                ConfigStore.Update(c => c.Server.LanApiKeyProtected = protectedKey);
            }, L.T("Не удалось сохранить настройку")))
            return;
        Log.Info("settings", "Доступ из сети: создан новый сетевой ключ (DPAPI)");
        Load(force: true);
        // Старый ключ действует, пока работает процесс: перезапуск без вопроса.
        if (ConfigStore.Current.Server.IsActive() || RunningNetworkHost() is not null) await RestartLocalIfRunningAsync(ask: false);
    }

    private async Task ApplyLanAsync()
    {
        var owner = _host.FindForm();
        var enable = _lanEnabled.Checked;
        var bind = SelectedBind();
        var cfg = ConfigStore.Current;
        if (enable && !LanServer.IsValidBindAddress(bind))
        {
            Ui.Warn(owner, L.T("Выберите адрес сети из списка."));
            return;
        }
        if (enable && !cfg.Server.LanAccess
            && !Ui.Confirm(owner, L.T("Открыть основную модель для других компьютеров сети? Запросы защищены ключом, но не шифруются — используйте только доверенную сеть или VPN."), warning: true))
        {
            _lanEnabled.Checked = false;
            return;
        }
        string? newKey = null;
        if (enable && Ui.Try(() => cfg.Server.LanApiKey(), null, "LanApiKey") is null)
        {
            // Ключа нет (или он не расшифровывается) — без ключа режим не включается, создаём новый.
            if (!Ui.RunSafe(owner, () => newKey = Dpapi.Protect(LanServer.NewLanApiKey()), L.T("Не удалось сохранить настройку"))) return;
        }
        var changed = enable != cfg.Server.LanAccess || bind != cfg.Server.LanBindAddress || newKey is not null;
        // Закрыть сеть (выключить режим, сменить адрес) нужно сразу: процесс со старыми настройками слушает сеть до перезапуска.
        var closes = cfg.Server.LanAccess && (!enable || bind != cfg.Server.LanBindAddress);
        if (!Ui.RunSafe(owner, () => ConfigStore.Update(c =>
            {
                c.Server.LanAccess = enable;
                c.Server.LanBindAddress = bind;
                if (newKey is not null) c.Server.LanApiKeyProtected = newKey;
            }), L.T("Не удалось сохранить настройку")))
            return;
        Log.Info("settings", enable ? $"Доступ из сети включён: {bind}" : "Доступ из сети выключен");
        Load(force: true);
        _shell.ConfigChanged();
        if (changed) await RestartLocalIfRunningAsync(ask: !closes && RunningNetworkHost() is null);
    }

    /// <summary>
    /// Свой основной сервер работает — перезапустить, чтобы он начал (или перестал) слушать сеть. Открыть сеть — с вопросом;
    /// закрыть её или сменить ключ (<paramref name="ask"/> = false) — сразу: иначе процесс остаётся в сети со старыми настройками.
    /// </summary>
    private async Task RestartLocalIfRunningAsync(bool ask)
    {
        if (_shell.Server.State is not (ServerState.Running or ServerState.Starting)) return;
        // Клиентский режим: свой сервер не запускается, если только он не остался от прежнего режима и не слушает сеть.
        if (ConfigStore.Current.IsRemote() && RunningNetworkHost() is null) return;
        if (ask && !Ui.Confirm(_host.FindForm(), L.T("Перезапустить сервер сейчас, чтобы применить изменения?"))) return;
        await _runBusy(async () =>
        {
            await _shell.Server.RestartAsync();
        }, L.T("Не удалось перезапустить сервер"), [_applyLan, _newLanKey]);
        if (!_host.IsDisposed) Load(force: true);
    }

    // ── Удалённый сервер ──────────────────────────────────────────────────────────

    private static string NormalizedOrRaw(string text) =>
        RemoteServer.TryNormalizeUrl(text, out var url, out _) ? url : text.Trim();

    private void UpdateRemoteKeyStatus(AppConfig cfg)
    {
        var stored = !string.IsNullOrWhiteSpace(cfg.Remote?.ApiKeyProtected);
        var readable = stored && Ui.Try(() => cfg.RemoteApiKey(), null, "RemoteApiKey") is not null;
        _remoteKeyStatus.Text = !stored
            ? L.T("Ключ не сохранён. Скопируйте его в разделе «Доступ из сети» на компьютере с моделью.")
            : readable
                ? L.T("Ключ сохранён (зашифрован для вашей учётной записи Windows). Оставьте поле пустым, чтобы не менять его.")
                : L.T("Сохранённый ключ не расшифровывается (другая учётная запись Windows) — введите его заново.");
        _remoteKeyStatus.ForeColor = stored && !readable ? Theme.WarnText : Theme.TextMuted;
    }

    private void UpdateRemoteStatus(AppConfig cfg)
    {
        if (!cfg.IsRemote())
        {
            _remoteStatus.Text = "";
            return;
        }
        var server = _shell.Server;
        var ep = cfg.MainEndpoint();
        var latency = server.RemoteLatency is { } l ? L.F(", задержка {0} мс", (int)Math.Round(l.TotalMilliseconds)) : "";
        (_remoteStatus.Text, _remoteStatus.ForeColor) = server.State switch
        {
            ServerState.Running => (L.F("Подключено к {0}: модель «{1}»{2}.", ep.Host, ep.Model, latency), Theme.OkText),
            ServerState.Starting => (L.F("Подключение к {0}…", ep.Host), Theme.TextMuted),
            ServerState.Failed or ServerState.NotConfigured => (server.LastError ?? L.F("Сервер {0} недоступен.", ep.Host), Theme.ErrorText),
            _ => (L.F("Клиентский режим: {0} (подключение не проверялось).", ep.Host), Theme.TextMuted),
        };
    }

    /// <summary>Ключ из поля или сохранённый; null — нет ни того, ни другого (текст ошибки уже показан).</summary>
    private string? KeyForCheck()
    {
        var typed = _remoteKey.Text.Trim();
        var key = typed.Length > 0 ? typed : Ui.Try(() => ConfigStore.Current.RemoteApiKey(), null, "RemoteApiKey") ?? "";
        if (RemoteServer.ValidateApiKey(key) is { } error)
        {
            Ui.Warn(_host.FindForm(), error);
            return null;
        }
        return key;
    }

    private async Task CheckRemoteAsync()
    {
        var owner = _host.FindForm();
        if (!RemoteServer.TryNormalizeUrl(_remoteUrl.Text, out var url, out var urlError))
        {
            Ui.Warn(owner, urlError!);
            return;
        }
        if (KeyForCheck() is not { } key) return;
        RemoteCheckResult? result = null;
        _remoteStatus.Text = L.F("Проверка {0}…", RemoteServer.DisplayHost(url));
        _remoteStatus.ForeColor = Theme.TextMuted;
        await _runBusy(async () => result = await RemoteProbe.CheckAsync(url, key, ConfigStore.Current.Remote?.ModelId),
            L.T("Не удалось проверить подключение"), [_check, _saveRemote]);
        if (_host.IsDisposed || result is null) return;
        ShowCheck(result);
    }

    private void ShowCheck(RemoteCheckResult r)
    {
        var ms = (int)Math.Round(r.Latency.TotalMilliseconds);
        if (r.Error is not null)
        {
            _remoteStatus.Text = r.Error;
            _remoteStatus.ForeColor = Theme.ErrorText;
            return;
        }
        var ctx = r.ContextPerRequest is int c ? Ui.Tokens(c) : L.T("неизвестен");
        var models = r.Models.Count > 1 ? " " + L.F("Доступные модели: {0}.", string.Join(", ", r.Models.Take(5))) : "";
        var key = r.KeyAccepted is null ? " " + L.T("Проверить ключ не удалось: сервер не отдаёт /props.") : "";
        (_remoteStatus.Text, _remoteStatus.ForeColor) = r.State == HealthState.Loading
            ? (L.F("Сервер отвечает (задержка {0} мс), но ещё загружает модель.", ms) + key, Theme.WarnText)
            : (L.F("Подключение работает: модель «{0}», контекст {1}, задержка {2} мс.", r.ModelId, ctx, ms) + models + key, Theme.OkText);
    }

    private async Task SaveRemoteAsync()
    {
        var owner = _host.FindForm();
        var enable = _remoteEnabled.Checked;
        var url = "";
        if (enable || _remoteUrl.Text.Trim().Length > 0)
        {
            if (!RemoteServer.TryNormalizeUrl(_remoteUrl.Text, out url, out var urlError))
            {
                Ui.Warn(owner, urlError!);
                return;
            }
        }
        var typed = _remoteKey.Text.Trim();
        if (typed.Length > 0 && RemoteServer.ValidateApiKey(typed) is { } keyError)
        {
            Ui.Warn(owner, keyError);
            return;
        }
        var cfg = ConfigStore.Current;
        if (enable && typed.Length == 0 && Ui.Try(() => cfg.RemoteApiKey(), null, "RemoteApiKey") is null)
        {
            Ui.Warn(owner, RemoteServer.ValidateApiKey("")!);
            return;
        }
        string? keyProtected = null;
        if (typed.Length > 0 && !Ui.RunSafe(owner, () => keyProtected = Dpapi.Protect(typed), L.T("Не удалось сохранить настройку"))) return;

        var old = cfg.Remote ?? new RemoteServerSettings();
        var urlChanged = !string.Equals(old.Url ?? "", url, StringComparison.OrdinalIgnoreCase);
        var reconnect = old.Enabled != enable || (enable && (urlChanged || keyProtected is not null));
        if (!Ui.RunSafe(owner, () => ConfigStore.Update(c =>
            {
                var r = c.Remote ??= new RemoteServerSettings();
                r.Enabled = enable;
                r.Url = url;
                if (keyProtected is not null) r.ApiKeyProtected = keyProtected;
                if (urlChanged)
                {
                    r.ModelId = null;
                    r.ContextSize = 0;
                }
                // Клиентскому режиму не нужны llama.cpp и модель: мастер для него можно не проходить.
                if (enable) c.SetupCompleted = true;
            }), L.T("Не удалось сохранить настройку")))
            return;
        _remoteKey.Clear();
        Log.Info("settings", enable ? $"Удалённый сервер включён: {RemoteServer.DisplayHost(url)}" : "Удалённый сервер выключен");
        _shell.ConfigChanged();
        if (reconnect)
        {
            // В клиентском режиме «запуск» — проверка удалённого сервера; при выключении — возврат к своему серверу.
            _remoteStatus.Text = "";
            await _runBusy(async () =>
            {
                await _shell.Server.RestartAsync();
            }, L.T("Не удалось перезапустить сервер"), [_saveRemote, _check]);
        }
        if (!_host.IsDisposed)
        {
            Load(force: true);
            UpdateRemoteStatus(ConfigStore.Current);
        }
    }
}
