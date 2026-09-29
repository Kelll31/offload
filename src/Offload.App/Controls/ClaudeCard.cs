using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Integrations;

namespace Offload.App.Controls;

/// <summary>
/// Карточка «Claude» на странице «Состояние»: Claude Code и Claude Desktop — найден ли, подключён ли (автоматически или
/// вручную), итог последней проверки запуском; кнопки «Проверить сейчас», «Подключить» и переход в «Интеграции».
/// Сведения собираются в фоне (чтение конфигов IDE), файлы здесь не меняются.
/// </summary>
internal sealed class ClaudeCard
{
    private readonly IAppShell _shell;
    private readonly List<(StatusDot Dot, Label Name, Label State, Label Detail)> _rows = [];
    private readonly Label _headline = Kit.Label("", Theme.Semibold(11f));
    private readonly ModernButton _check;
    private readonly ModernButton _connect;
    private int _loading;

    public ClaudeCard(IAppShell shell)
    {
        _shell = shell;
        Card = new CardPanel { ColumnCount = 4, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(22, 16, 22, 14) };
        Card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _check = Kit.IconButton(Glyphs.Check, L.T("Проверить сейчас"), async (_, _) => await RunAsync(_shell.CheckConnectionsAsync), 150);
        _connect = Kit.IconButton(Glyphs.Link, L.T("Подключить"), async (_, _) => await RunAsync(_shell.ConnectClaudeNowAsync), 120);
        var open = Kit.Subtle(L.T("Интеграции →"), (_, _) => _shell.ShowMainWindow(Tabs.Integrations), 100);
        var buttons = Kit.Flow(_check, _connect, open);
        buttons.WrapContents = false;
        buttons.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        buttons.Margin = Padding.Empty;
        _headline.Margin = new Padding(0, 6, 8, 8);
        Card.AddRow(_headline, null, null, buttons);
        Card.SetColumnSpan(_headline, 3);

        foreach (var _ in AutoConnectPolicy.Ids)
        {
            var dot = new StatusDot(10) { Margin = new Padding(2, 9, 10, 4) };
            var name = Kit.Label("", Theme.Semibold(9.5f));
            name.Margin = new Padding(0, 5, 16, 4);
            var state = Kit.Label("", Theme.Regular(9f));
            state.Margin = new Padding(0, 5, 16, 4);
            var detail = Kit.Label("", Theme.Regular(8.5f), Theme.TextMuted);
            detail.Margin = new Padding(0, 6, 0, 4);
            Card.AddRow(dot, name, state, detail);
            _rows.Add((dot, name, state, detail));
        }
    }

    public CardPanel Card { get; }

    private async Task RunAsync(Func<Task> action)
    {
        _check.Enabled = _connect.Enabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Карточка Claude: {ex.Message}");
        }
        finally
        {
            if (!Card.IsDisposed) _check.Enabled = _connect.Enabled = true;
            Refresh();
        }
    }

    /// <summary>Перечитать состояние (в фоне; повторный вызов во время чтения пропускается).</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        var cfg = ConfigStore.Current;
        _ = Task.Run(() =>
        {
            try
            {
                var rows = ClaudeOverview.Build(cfg, InstallInfo.McpSpec());
                _shell.PostToUi(() => Apply(rows));
            }
            catch (Exception ex)
            {
                Log.Debug("ui", $"Карточка Claude: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _loading, 0);
            }
        });
    }

    private void Apply(IReadOnlyList<ClaudeRow> rows)
    {
        if (Card.IsDisposed) return;
        for (var i = 0; i < _rows.Count && i < rows.Count; i++)
        {
            var (dot, name, state, detail) = _rows[i];
            var r = rows[i];
            name.Text = r.Name;
            (dot.DotColor, state.ForeColor, state.Text) = r.Kind switch
            {
                ClaudeRowKind.Connected => (Theme.Green, Theme.OkText, r.State?.AutoConnected == true ? L.T("подключён автоматически") : L.T("подключён")),
                ClaudeRowKind.Attention => (Theme.Amber, Theme.WarnText, L.T("требует внимания")),
                ClaudeRowKind.NotConnected => (Theme.Gray, Theme.TextMuted, r.Declined ? L.T("не подключён (вы отказались)") : L.T("не подключён")),
                _ => (Theme.Track, Theme.TextFaint, L.T("не установлен")),
            };
            dot.AccessibleName = $"{r.Name}: {state.Text}";
            detail.Text = r.State?.LastCheckUtc is { } at
                ? L.F("проверка {0}: {1}", Forms.Pages.NotificationsPage.Ago(at), r.State.LastCheckText ?? "—")
                : r.Kind == ClaudeRowKind.NotInstalled ? L.T("подключится сам, когда вы его установите") : "";
        }
        var connected = rows.Count(r => r.Kind == ClaudeRowKind.Connected);
        var attention = rows.Any(r => r.Kind == ClaudeRowKind.Attention);
        _headline.Text = attention ? L.T("Claude: требует внимания")
            : connected > 0 ? L.T("Claude подключён к Offload")
            : rows.Any(r => r.Installed) ? L.T("Claude найден, но не подключён")
            : L.T("Claude не установлен");
        _headline.ForeColor = attention ? Theme.WarnText : Theme.TextPrimary;
        Card.EdgeColor = attention ? Theme.Amber : null;
        Card.Hero = attention || connected > 0;
        _connect.Visible = rows.Any(r => r.Kind == ClaudeRowKind.NotConnected && !r.Declined);
    }
}
