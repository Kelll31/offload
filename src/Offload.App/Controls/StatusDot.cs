using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Offload.App.Controls;

/// <summary>Цветной круглый индикатор состояния.</summary>
internal sealed class StatusDot : Control
{
    private Color _color = Color.Gray;

    public StatusDot(int diameter = 12)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        Size = new Size(diameter, diameter);
        Margin = new Padding(0, 4, 8, 4);
        Anchor = AnchorStyles.Left;
        TabStop = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DotColor
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var d = Math.Min(ClientSize.Width, ClientSize.Height) - 1;
        if (d <= 0) return;
        var rect = new Rectangle((ClientSize.Width - d) / 2, (ClientSize.Height - d) / 2, d, d);
        using var brush = new SolidBrush(_color);
        e.Graphics.FillEllipse(brush, rect);
        using var pen = new Pen(ControlPaint.Dark(_color, 0.1f), 1f);
        e.Graphics.DrawEllipse(pen, rect);
    }
}
