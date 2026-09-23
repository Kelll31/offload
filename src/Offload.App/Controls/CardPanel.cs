using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Карточка: таблица на светлом фоне со скруглённой рамкой.</summary>
internal sealed class CardPanel : TableLayoutPanel
{
    public CardPanel()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Padding = new Padding(16, 14, 16, 14);
        Margin = new Padding(0, 0, 0, 8);
        BackColor = Theme.Card;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        var radius = LogicalToDeviceUnits(6);
        using var path = RoundedRect(r, radius);
        using var pen = new Pen(Theme.Border, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Фон родителя за скруглёнными углами, затем заливка карточки.
        var parentColor = Parent?.BackColor ?? Theme.Surface;
        if (parentColor == Color.Transparent) parentColor = Theme.Surface;
        using (var b = new SolidBrush(parentColor)) e.Graphics.FillRectangle(b, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var path = RoundedRect(r, LogicalToDeviceUnits(6));
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        if (r.Width <= d || r.Height <= d)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
