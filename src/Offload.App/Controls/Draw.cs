using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Общие приёмы отрисовки собственных элементов (скругления, карточки, текст).</summary>
internal static class Draw
{
    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var d = radius * 2;
        if (radius <= 0 || d <= 0)
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

    /// <summary>Карточка: мягкая тень, заливка Theme.Card и тонкая рамка.</summary>
    public static void Card(Graphics g, Rectangle r, int radius, Color? fill = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 3);
        Shadow(g, body, radius);
        using var path = Rounded(body, radius);
        using (var b = new SolidBrush(fill ?? Theme.Card)) g.FillPath(b, path);
        using var pen = new Pen(Theme.Border);
        g.DrawPath(pen, path);
    }

    /// <summary>Мягкая тень под прямоугольником (два слоя со сдвигом вниз, внутри границ элемента).</summary>
    public static void Shadow(Graphics g, Rectangle body, int radius)
    {
        var shadow = Theme.Shadow;
        for (var i = 2; i >= 1; i--)
        {
            using var sp = Rounded(new Rectangle(body.X, body.Y + i, body.Width, body.Height), radius);
            using var sb = new SolidBrush(Color.FromArgb(shadow.A / i, shadow));
            g.FillPath(sb, sp);
        }
    }

    /// <summary>Градиентная заливка акцента (значки, «герой»-карточки).</summary>
    public static void Gradient(Graphics g, Rectangle r, int radius, Color from, Color to, LinearGradientMode mode = LinearGradientMode.ForwardDiagonal)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new LinearGradientBrush(r, from, to, mode);
        using var path = Rounded(r, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        g.FillPath(b, path);
    }

    /// <summary>Фактический цвет фона под элементом (первый непрозрачный предок).</summary>
    public static Color EffectiveBack(Control c)
    {
        for (var p = (Control?)c; p is not null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Theme.Surface;
    }

    public static void Text(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags = TextFormatFlags.Left) =>
        TextRenderer.DrawText(g, text, font, r, color,
            flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

    public static void Glyph(Graphics g, string glyph, float size, Rectangle r, Color color) =>
        TextRenderer.DrawText(g, glyph, Theme.Icons(size), r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
}
