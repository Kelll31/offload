using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Общие приёмы отрисовки собственных элементов (скругления, карточки, текст).</summary>
internal static class Draw
{
    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (r.Width <= d || r.Height <= d || radius <= 0)
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

    public static void Fill(Graphics g, Rectangle r, Color color, int radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(color);
        using var path = Rounded(r, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        g.FillPath(b, path);
    }

    /// <summary>Карточка: заливка Theme.Card и тонкая рамка.</summary>
    public static void Card(Graphics g, Rectangle r, int radius, Color? fill = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rr = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        using var path = Rounded(rr, radius);
        using (var b = new SolidBrush(fill ?? Theme.Card)) g.FillPath(b, path);
        using var pen = new Pen(Theme.Border);
        g.DrawPath(pen, path);
    }

    public static void Text(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags = TextFormatFlags.Left) =>
        TextRenderer.DrawText(g, text, font, r, color,
            flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

    public static void Glyph(Graphics g, string glyph, float size, Rectangle r, Color color) =>
        TextRenderer.DrawText(g, glyph, Theme.Icons(size), r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
}
