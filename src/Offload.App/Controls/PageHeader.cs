using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>
/// Шапка страницы: значок раздела в градиентной плашке, крупный заголовок, подзаголовок и необязательные «таблетки»
/// справа (например, состояние сервера). Рисуется целиком; высота задаётся строкой таблицы.
/// </summary>
internal sealed class PageHeader : Control
{
    private string _title = "";
    private string _subtitle = "";
    private string _glyph = Glyphs.Info;
    private IReadOnlyList<(string Text, Color Color)> _pills = [];

    public PageHeader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Theme.Surface;
        Dock = DockStyle.Top;
        Height = 86;
        AccessibleRole = AccessibleRole.StaticText;
    }

    public void Set(string title, string? subtitle, string glyph)
    {
        _title = title;
        _subtitle = subtitle ?? "";
        _glyph = glyph;
        AccessibleName = string.IsNullOrEmpty(_subtitle) ? title : $"{title}. {_subtitle}";
        Invalidate();
    }

    /// <summary>«Таблетки» справа от заголовка: текст и цвет точки.</summary>
    public void SetPills(IReadOnlyList<(string Text, Color Color)> pills)
    {
        if (pills.SequenceEqual(_pills)) return;
        _pills = pills;
        Invalidate();
    }

    private int Px(int v) => LogicalToDeviceUnits(v);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var left = Px(28);
        var chip = new Rectangle(left, Px(20), Px(46), Px(46));
        Draw.Shadow(g, chip, Px(14));
        Draw.Gradient(g, chip, Px(14), Theme.Fill, Theme.FillEnd);
        Draw.Glyph(g, _glyph, 16f, chip, Theme.OnFill);

        var x = chip.Right + Px(16);
        var titleFont = Theme.Semibold(19f);
        var titleSize = TextRenderer.MeasureText(_title, titleFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var titleRect = new Rectangle(x, chip.Y - Px(2), Math.Max(0, Width - x - Px(28)), titleSize.Height);
        Draw.Text(g, _title, titleFont, titleRect, Theme.TextPrimary);
        if (!string.IsNullOrEmpty(_subtitle))
            Draw.Text(g, _subtitle, Theme.Regular(9.5f), new Rectangle(x, titleRect.Bottom + Px(2), Math.Max(0, Width - x - Px(28)), Px(20)), Theme.TextMuted);

        // Таблетки — справа по одной линии с заголовком.
        var px = Width - Px(28);
        foreach (var (text, color) in _pills.Reverse())
        {
            var font = Theme.Semibold(8.5f);
            var ts = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            var w = ts.Width + Px(30);
            var pill = new Rectangle(px - w, chip.Y + Px(4), w, Px(26));
            if (pill.X < titleRect.X + titleSize.Width + Px(16)) break;
            Draw.Fill(g, pill, Theme.Card, pill.Height / 2);
            using (var pen = new Pen(Theme.Border))
            using (var path = Draw.Rounded(new Rectangle(pill.X, pill.Y, pill.Width - 1, pill.Height - 1), pill.Height / 2))
                g.DrawPath(pen, path);
            var dot = new Rectangle(pill.X + Px(11), pill.Y + (pill.Height - Px(8)) / 2, Px(8), Px(8));
            using (var b = new SolidBrush(color)) g.FillEllipse(b, dot);
            Draw.Text(g, text, font, new Rectangle(dot.Right + Px(7), pill.Y, pill.Right - dot.Right - Px(10), pill.Height), Theme.TextPrimary,
                TextFormatFlags.VerticalCenter);
            px = pill.X - Px(8);
        }
    }
}
