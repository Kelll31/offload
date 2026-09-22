using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Заголовок раздела: полужирный текст и тонкая линия под ним на всю ширину.</summary>
internal sealed class SectionHeader : Control
{
    public SectionHeader(string text, bool first = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        Text = text;
        Font = Theme.Semibold(10.5f);
        ForeColor = Theme.TextPrimary;
        BackColor = Color.Transparent;
        AutoSize = true;
        TabStop = false;
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        Margin = new Padding(0, first ? 0 : 14, 0, 6);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(s.Width + 2, s.Height + LogicalToDeviceUnits(7));
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
        TextRenderer.DrawText(e.Graphics, Text, Font, new Point(0, 0), ForeColor,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        using var pen = new Pen(Theme.Border, 1f);
        var y = ClientSize.Height - 1;
        e.Graphics.DrawLine(pen, 0, y, ClientSize.Width, y);
    }
}
