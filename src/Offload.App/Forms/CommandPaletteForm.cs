using System.Drawing.Drawing2D;
using Offload.App.Controls;
using Offload.App.Util;
using Offload.Core.Util;

namespace Offload.App.Forms;

/// <summary>Команда палитры: заголовок, группа, значок, ключевые слова для поиска, сочетание клавиш и действие.</summary>
internal sealed record PaletteCommand(string Title, string Group, string Glyph, string Keywords, string? Shortcut, Action Run);

/// <summary>
/// Палитра команд (Ctrl+K): строка поиска и список разделов и действий с нечётким поиском (<see cref="FuzzyMatch"/>).
/// Стрелки выбирают, Enter выполняет, Esc или щелчок мимо окна закрывают. Выполнение — после закрытия палитры.
/// </summary>
internal sealed class CommandPaletteForm : Form
{
    private const int MaxVisible = 9;
    private readonly IReadOnlyList<PaletteCommand> _all;
    private readonly TextBox _query = new()
    {
        BorderStyle = BorderStyle.None,
        Font = Theme.Regular(13f),
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
    };
    private readonly ListBox _list = new()
    {
        DrawMode = DrawMode.OwnerDrawFixed,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
    };
    private readonly Label _empty = Kit.Label("", Theme.Regular(9.5f), Theme.TextMuted);
    private PaletteCommand? _chosen;

    public CommandPaletteForm(IReadOnlyList<PaletteCommand> commands)
    {
        _all = commands;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Theme.Card;
        ForeColor = Theme.TextPrimary;
        ClientSize = new Size(640, 470);
        Padding = new Padding(1);
        Text = L.T("Палитра команд");
        AccessibleName = Text;

        _query.BackColor = Theme.Card;
        _query.ForeColor = Theme.TextPrimary;
        _query.PlaceholderText = L.T("Раздел или действие: «сервер», «claude», «тема»…");
        _query.AccessibleName = L.T("Поиск команды");
        _query.TextChanged += (_, _) => Filter();

        _list.BackColor = Theme.Card;
        _list.ForeColor = Theme.TextPrimary;
        _list.ItemHeight = 46;
        _list.AccessibleName = L.T("Команды");
        _list.DrawItem += DrawItem;
        _list.MouseClick += (_, _) => Choose();
        _list.MouseMove += (_, e) =>
        {
            var i = _list.IndexFromPoint(e.Location);
            if (i >= 0 && i != _list.SelectedIndex) _list.SelectedIndex = i;
        };

        var search = new TableLayoutPanel { Dock = DockStyle.Top, Height = 58, ColumnCount = 2, BackColor = Theme.Card, Padding = new Padding(18, 16, 18, 8) };
        search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var icon = new Label { Text = Glyphs.Search, Font = Theme.Icons(13f), ForeColor = Theme.Accent, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Card };
        search.Controls.Add(icon, 0, 0);
        search.Controls.Add(_query, 1, 0);

        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border };
        _empty.Dock = DockStyle.Top;
        _empty.Padding = new Padding(20, 14, 0, 0);
        _empty.Visible = false;
        var footer = Kit.Label(L.T("↑↓ — выбрать · Enter — выполнить · Esc — закрыть"), Theme.Regular(8.5f), Theme.TextFaint);
        footer.Dock = DockStyle.Bottom;
        footer.AutoSize = false;
        footer.Height = 30;
        footer.TextAlign = ContentAlignment.MiddleCenter;
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 0), BackColor = Theme.Card };
        body.Controls.Add(_list);
        body.Controls.Add(_empty);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(line);
        Controls.Add(search);
        Kit.FinishForm(this);
        Filter();
    }

    /// <summary>Выбранная команда (выполняется владельцем после закрытия).</summary>
    public PaletteCommand? Chosen => _chosen;

    /// <summary>Показать над окном владельца (по центру, ближе к верху) и вернуть выбранную команду.</summary>
    public static PaletteCommand? Pick(Form owner, IReadOnlyList<PaletteCommand> commands)
    {
        using var f = new CommandPaletteForm(commands);
        var b = owner.Bounds;
        f.Location = new Point(b.X + (b.Width - f.Width) / 2, b.Y + Math.Max(40, b.Height / 7));
        f.ShowDialog(owner);
        return f.Chosen;
    }

    private void Filter()
    {
        var q = _query.Text;
        var items = FuzzyMatch.Rank(q, _all, c => [c.Title, c.Keywords, c.Group]);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var c in items) _list.Items.Add(c);
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _empty.Text = L.F("Ничего не найдено по запросу «{0}»", q.Trim());
        _empty.Visible = _list.Items.Count == 0;
        _list.Visible = _list.Items.Count > 0;
    }

    private void Choose()
    {
        if (_list.SelectedItem is not PaletteCommand c) return;
        _chosen = c;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                e.Handled = true;
                break;
            case Keys.Enter:
                Choose();
                e.Handled = e.SuppressKeyPress = true;
                break;
            case Keys.Down when _list.Items.Count > 0:
                _list.SelectedIndex = (_list.SelectedIndex + 1) % _list.Items.Count;
                e.Handled = true;
                break;
            case Keys.Up when _list.Items.Count > 0:
                _list.SelectedIndex = (_list.SelectedIndex - 1 + _list.Items.Count) % _list.Items.Count;
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _query.Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && DialogResult == DialogResult.None) Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Blend(Theme.Border, Theme.Accent, 0.4));
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not PaletteCommand c) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(Theme.Card)) g.FillRectangle(back, e.Bounds);
        var px = (int v) => _list.LogicalToDeviceUnits(v);
        var r = new Rectangle(e.Bounds.X + px(2), e.Bounds.Y + px(2), e.Bounds.Width - px(4), e.Bounds.Height - px(4));
        if (selected) Draw.Fill(g, r, Theme.NavSelected, px(8));
        var chip = new Rectangle(r.X + px(10), r.Y + (r.Height - px(30)) / 2, px(30), px(30));
        if (selected) Draw.Gradient(g, chip, px(8), Theme.Fill, Theme.FillEnd);
        else Draw.Fill(g, chip, Theme.AccentLight, px(8));
        Draw.Glyph(g, c.Glyph, 10.5f, chip, selected ? Theme.OnFill : Theme.Accent);

        var right = r.Right - px(12);
        if (c.Shortcut is { Length: > 0 } sc)
        {
            var font = Theme.Semibold(8f);
            var size = TextRenderer.MeasureText(sc, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            var key = new Rectangle(right - size.Width - px(12), r.Y + (r.Height - px(22)) / 2, size.Width + px(12), px(22));
            Draw.Fill(g, key, Theme.Surface, px(5));
            using (var pen = new Pen(Theme.Border))
            using (var path = Draw.Rounded(new Rectangle(key.X, key.Y, key.Width - 1, key.Height - 1), px(5)))
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, sc, font, key, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            right = key.X - px(10);
        }
        var groupFont = Theme.Regular(8.5f);
        var gs = TextRenderer.MeasureText(c.Group, groupFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        Draw.Text(g, c.Group, groupFont, new Rectangle(right - gs.Width, r.Y, gs.Width, r.Height), Theme.TextFaint, TextFormatFlags.VerticalCenter);
        right -= gs.Width + px(12);
        var tx = chip.Right + px(12);
        Draw.Text(g, c.Title, selected ? Theme.Semibold(10f) : Theme.Regular(10f), new Rectangle(tx, r.Y, Math.Max(0, right - tx), r.Height),
            Theme.TextPrimary, TextFormatFlags.VerticalCenter);
    }
}
