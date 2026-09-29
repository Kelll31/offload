using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Notifications;

namespace Offload.App.Forms.Pages;

/// <summary>
/// «Уведомления» — центр уведомлений: всё, что Offload сообщал за сеанс, в том числе не показанное из-за «тихих часов»
/// или выключенных уведомлений. Щелчок по «Открыть» ведёт в раздел, к которому относится уведомление. Открытие страницы
/// отмечает всё прочитанным (счётчик в навигации гаснет).
/// </summary>
internal sealed class NotificationsPage : PageBase
{
    private const int MaxCards = 60;
    private readonly TableLayoutPanel _list = Kit.Table();
    private readonly Label _empty;
    private readonly Label _summary = Kit.Hint("");
    private bool _dirty = true;

    public NotificationsPage(IAppShell shell) : base(shell)
    {
        _empty = Kit.Wrap(L.T("Уведомлений пока нет. Здесь появится всё, что Offload сообщает: подключение Claude, обновления, ошибки сервера."),
            Theme.Regular(9.5f), Theme.TextMuted);
        var root = Kit.Table();
        var clear = Kit.IconButton(Glyphs.Delete, L.T("Очистить"), (_, _) => Shell.Notifications.Clear(), 110);
        var settings = Kit.Subtle(L.T("Тихие часы…"), (_, _) => Shell.ShowMainWindow(Tabs.Settings), 110);
        var bar = Kit.Flow(clear, settings);
        bar.Margin = new Padding(0, 0, 0, 8);
        root.AddRow(bar);
        _summary.Margin = new Padding(2, 0, 0, 8);
        root.AddRow(_summary);
        root.AddRow(_empty);
        root.AddRow(_list);
        Controls.Add(Kit.Scroll(root, new Padding(16, 4, 28, 16)));
        Shell.Notifications.Changed += OnChanged;
    }

    public override string Key => Tabs.Notifications;

    public override string Title => L.T("Уведомления");

    public override string Subtitle => L.T("Всё, что Offload сообщал за этот сеанс");

    public override string Glyph => Glyphs.Bell;

    private void OnChanged()
    {
        _dirty = true;
        if (IsActive) Shell.PostToUi(Rebuild);
    }

    protected override void OnActivated()
    {
        Rebuild();
        Shell.Notifications.MarkAllRead();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Shell.Notifications.Changed -= OnChanged;
        base.Dispose(disposing);
    }

    private void Rebuild()
    {
        if (IsDisposed || !_dirty) return;
        _dirty = false;
        var items = Shell.Notifications.Items;
        _list.SuspendLayout();
        // Копия списка: Dispose убирает элемент из Controls, перебор самой коллекции пропускал бы карточки.
        var old = _list.Controls.Cast<Control>().ToList();
        _list.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _list.RowStyles.Clear();
        _list.RowCount = 0;
        foreach (var n in items.Take(MaxCards)) _list.AddRow(Card(n, Shell.Notifications.IsUnread(n)));
        _list.ResumeLayout(true);
        _empty.Visible = items.Count == 0;
        var hidden = items.Count(i => !i.Shown);
        _summary.Visible = items.Count > 0;
        _summary.Text = L.F("Всего: {0}", items.Count) + (hidden > 0 ? L.F(" · не показаны всплывающим окном: {0} (уведомления выключены или «тихие часы»)", hidden) : "");
        if (IsActive) Shell.Notifications.MarkAllRead();
    }

    private CardPanel Card(NotificationEntry n, bool unread)
    {
        var card = new CardPanel { ColumnCount = 3, Hero = true, Padding = new Padding(22, 12, 16, 14), Margin = new Padding(0, 0, 0, 8) };
        card.EdgeColor = n.Level switch
        {
            NotificationLevel.Error => Theme.Red,
            NotificationLevel.Warning => Theme.Amber,
            _ => unread ? null : Theme.Border,
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var glyph = new Label
        {
            Text = n.Level switch { NotificationLevel.Error => Glyphs.Cancel, NotificationLevel.Warning => Glyphs.Warning, _ => Glyphs.Info },
            Font = Theme.Icons(12f),
            ForeColor = n.Level switch { NotificationLevel.Error => Theme.ErrorText, NotificationLevel.Warning => Theme.WarnText, _ => Theme.Accent },
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 3, 12, 0),
        };
        var title = Kit.Label(n.Title, Theme.Semibold(10f));
        title.Margin = new Padding(0, 2, 8, 2);
        var time = Kit.Label(Ago(n.TimeUtc) + (n.Shown ? "" : " · " + L.T("без всплывающего окна")), Theme.Regular(8.5f), Theme.TextFaint);
        time.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        card.AddRow(glyph, title, time);
        if (n.Text != n.Title)
        {
            var text = Kit.Wrap(n.Text, Theme.Regular(9f), Theme.TextMuted);
            text.Margin = new Padding(0, 2, 0, 2);
            card.AddRow(null, text);
        }
        if (n.Tab is { } tab)
        {
            var open = Kit.ActionLink(L.T("Открыть раздел →"), () => Shell.ShowMainWindow(tab));
            open.Margin = new Padding(0, 4, 0, 0);
            card.AddRow(null, open);
        }
        return card;
    }

    /// <summary>«только что», «5 мин назад», «вчера в 14:02»…</summary>
    internal static string Ago(DateTime utc, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var d = now - utc;
        if (d < TimeSpan.FromMinutes(1)) return L.T("только что");
        if (d < TimeSpan.FromHours(1)) return L.F("{0} назад", Ui.Plural((long)d.TotalMinutes, "минуту", "минуты", "минут"));
        var local = utc.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? L.F("сегодня в {0:t}", local) : local.ToString("g", L.Culture);
    }
}
