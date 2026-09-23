using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Integrations;

namespace Offload.App.Forms.Wizard;

/// <summary>Шаг 5: подключение к IDE, настройки Claude Code и автозапуск.</summary>
internal sealed class IdeStep : WizardStep
{
    private const string ClaudeCodeId = "claude-code";

    private sealed record Item(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly CheckedListBox _ides = new()
    {
        CheckOnClick = true,
        IntegralHeight = false,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 4, 0, 4),
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly Label _loading = Kit.Hint(L.T("Поиск установленных IDE…"));
    private readonly Label _notFound = Kit.Hint("");
    private readonly CheckBox _guidance = Kit.Check(L.T("Установить инструкции по делегированию для Claude Code"));
    private readonly CheckBox _preapprove = Kit.Check(L.T("Разрешить Claude Code вызывать инструменты чтения Offload без подтверждения"));
    private readonly CheckBox _autostart = Kit.Check(L.T("Запускать Offload вместе с Windows"));

    private bool _loaded;
    private bool _busy;
    private bool _claudeFound;

    public IdeStep(WizardContext ctx) : base(ctx)
    {
        _ides.ItemCheck += (_, e) =>
        {
            if (_busy) return;
            if (_ides.Items[e.Index] is Item it)
            {
                if (e.NewValue == CheckState.Checked) State.Ides.Add(it.Id);
                else State.Ides.Remove(it.Id);
            }
        };
        _guidance.CheckedChanged += (_, _) => State.ClaudeGuidance = _guidance.Checked;
        _preapprove.CheckedChanged += (_, _) => State.PreapproveReadTools = _preapprove.Checked;
        _autostart.CheckedChanged += (_, _) => State.Autostart = _autostart.Checked;

        var root = Kit.Table();
        root.AddRow(Kit.Section(L.T("IDE и агенты"), first: true));
        root.AddRow(Kit.Hint(L.T("Offload будет прописан как MCP-сервер в выбранных программах. Перед изменением их настроек делается резервная копия.")));
        root.AddRow(_loading);
        root.AddFixedRow(170, _ides);
        root.AddRow(_notFound);

        root.AddRow(Kit.Section("Claude Code"));
        root.AddRow(_guidance);
        root.AddRow(Indent(Kit.Hint(L.T("Объясняет Claude, какие задачи выгодно поручать локальной модели, чтобы экономить токены."))));
        root.AddRow(_preapprove);
        root.AddRow(Indent(Kit.Hint(L.T("Только инструменты чтения (вопросы по файлам, ревью, сжатие логов). Инструменты записи можно разрешить позже на вкладке «Интеграции»."))));

        root.AddRow(Kit.Section(L.T("Автозапуск")));
        root.AddRow(_autostart);
        root.AddRow(Indent(Kit.Hint(L.T("Offload стартует в области уведомлений, и IDE сразу может обращаться к локальной модели."))));
        SetContent(root);
    }

    public override string Title => L.T("Подключение к IDE");

    public override string Heading => L.T("Подключение к IDE");

    public override string? Subtitle => L.T("Выберите программы, из которых можно будет поручать задачи локальной модели.");

    public override string NextText => L.T("Установить");

    public override bool CanGoNext => _loaded;

    private static Label Indent(Label l)
    {
        l.Margin = new Padding(20, 0, 0, 6);
        return l;
    }

    public override async void OnEnter()
    {
        if (_loaded || _busy) return;
        _busy = true;
        RaiseNavigationChanged();
        try
        {
            var cfg = ConfigStore.Current;
            var firstRun = !cfg.SetupCompleted;
            var spec = Ui.Try<McpServerSpec?>(() => McpServerSpec.ForCurrentExecutable(), null, "McpServerSpec");
            var (found, missing, error) = await Task.Run<(List<(Item Item, bool Registered)>, List<string>, string?)>(() =>
            {
                var f = new List<(Item Item, bool Registered)>();
                var m = new List<string>();
                try
                {
                    foreach (var i in IntegrationRegistry.All)
                    {
                        var name = Ui.Try(() => i.DisplayName, i.Id, "DisplayName");
                        if (Ui.Try(i.IsClientInstalled, false, $"{i.Id}.IsClientInstalled"))
                        {
                            var status = spec is null ? IntegrationStatus.NotRegistered
                                : Ui.Try(() => i.GetStatus(spec), IntegrationStatus.NotRegistered, $"{i.Id}.GetStatus");
                            f.Add((new Item(i.Id, name), status is IntegrationStatus.Registered or IntegrationStatus.Outdated));
                        }
                        else
                        {
                            m.Add(name);
                        }
                    }
                    return (f, m, null);
                }
                catch (Exception ex)
                {
                    Log.Warn("wizard", $"Список IDE недоступен: {ex.Message}");
                    return (f, m, Ui.FriendlyError(ex));
                }
            });
            if (IsDisposed) return;

            State.Ides.Clear();
            _ides.Items.Clear();
            foreach (var (item, registered) in found)
            {
                // Первый запуск — все найденные; повторный — то, что уже подключено.
                var check = firstRun || registered || cfg.Integrations.Contains(item.Id);
                _ides.Items.Add(item, check);
                if (check) State.Ides.Add(item.Id);
            }
            _loading.Visible = found.Count == 0 || error is not null;
            _loading.Text = error is not null
                ? L.F("Не удалось получить список IDE: {0}", error)
                : found.Count == 0 ? L.T("Поддерживаемые IDE не найдены. Подключить их можно позже на вкладке «Интеграции».") : "";
            _notFound.Text = missing.Count == 0 ? "" : L.F("Не найдены на этом компьютере: {0}.", string.Join(", ", missing));

            _claudeFound = found.Any(f => f.Item.Id == ClaudeCodeId);
            _guidance.Enabled = _claudeFound;
            _preapprove.Enabled = _claudeFound;
            var guidanceInstalled = Ui.Try(ClaudeCodeExtras.IsGuidanceInstalled, false, "IsGuidanceInstalled");
            var approved = Ui.Try(ClaudeCodeExtras.AreToolsPreapproved, false, "AreToolsPreapproved");
            _guidance.Checked = _claudeFound && (firstRun || guidanceInstalled);
            _preapprove.Checked = _claudeFound && (firstRun || approved);
            State.ClaudeGuidance = _guidance.Checked;
            State.PreapproveReadTools = _preapprove.Checked;
            _autostart.Checked = firstRun || Autostart.IsEnabled;
            State.Autostart = _autostart.Checked;
            if (!_claudeFound)
            {
                _guidance.Text = L.T("Установить инструкции по делегированию для Claude Code (Claude Code не найден)");
            }
            State.IdesLoaded = true;
            _loaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("wizard", "Шаг «Подключение к IDE»", ex);
            _loading.Text = L.F("Не удалось получить список IDE: {0}", Ui.FriendlyError(ex));
            _loaded = true;
        }
        finally
        {
            _busy = false;
            RaiseNavigationChanged();
        }
    }
}
