using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Заголовок раздела: цветная метка акцента, полужирный текст и тонкая линия до правого края.</summary>
internal sealed class SectionHeader : Control
{
    public SectionHeader(string text, bool first = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        Text = text;
        Font = Theme.Semibold(11f);
        ForeColor = Theme.TextPrimary;
        BackColor = Color.Transparent;
        AutoSize = true;
        TabStop = false;
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        Margin = new Padding(0, first ? 0 : 18, 0, 8);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(s.Width + LogicalToDeviceUnits(14), s.Height + LogicalToDeviceUnits(6));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Parent?.PerformLayout(this, nameof(Text));
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Parent?.PerformLayout(this, nameof(Font));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var size = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var mid = size.Height / 2 + LogicalToDeviceUnits(1);
        var bar = new Rectangle(0, mid - LogicalToDeviceUnits(7), LogicalToDeviceUnits(4), LogicalToDeviceUnits(14));
        Draw.Gradient(g, bar, LogicalToDeviceUnits(2), Theme.Fill, Theme.FillEnd, System.Drawing.Drawing2D.LinearGradientMode.Vertical);
        var x = bar.Right + LogicalToDeviceUnits(9);
        TextRenderer.DrawText(g, Text, Font, new Point(x, 0), ForeColor,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        var lineX = x + size.Width + LogicalToDeviceUnits(12);
        if (lineX < ClientSize.Width)
        {
            using var pen = new Pen(Theme.Border, 1f);
            g.DrawLine(pen, lineX, mid, ClientSize.Width, mid);
        }
    }
}
