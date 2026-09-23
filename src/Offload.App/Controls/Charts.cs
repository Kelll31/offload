using System.ComponentModel;
using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Базовый элемент с собственной отрисовкой: двойная буферизация, прозрачный фон на карточке.</summary>
internal abstract class PaintedControl : Control
{
    protected PaintedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Margin = Padding.Empty;
        ForeColor = Theme.TextPrimary;
    }

    protected int Px(int logical) => LogicalToDeviceUnits(logical);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var back = BackColor == Color.Transparent ? Parent?.BackColor ?? Theme.Surface : BackColor;
        if (back == Color.Transparent) back = Theme.Surface;
        using var b = new SolidBrush(back);
        e.Graphics.FillRectangle(b, ClientRectangle);
    }
}

/// <summary>
/// Плитка показателя: значок и подпись, крупное значение, пояснение; внизу — полоса заполнения (доля 0…1)
/// или мини-график истории значений.
/// </summary>
internal sealed class StatTile : PaintedControl
{
    private string _caption = "";
    private string _value = "—";
    private string _detail = "";
    private string _glyph = Glyphs.Info;
    private Color? _accent;
    private double? _fraction;
    private IReadOnlyList<double> _history = [];

    public StatTile(string caption, string glyph)
    {
        _caption = caption;
        _glyph = glyph;
        Height = 112;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 12, 12);
        BackColor = Color.Transparent;
    }

    public void Set(string value, string detail, Color? accent = null)
    {
        if (_value == value && _detail == detail && _accent == accent) return;
        _value = value;
        _detail = detail;
        _accent = accent;
        Invalidate();
    }

    /// <summary>Доля для полосы внизу (null — без полосы).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double? Fraction
    {
        get => _fraction;
        set { _fraction = value is null ? null : Math.Clamp(value.Value, 0, 1); Invalidate(); }
    }

    /// <summary>История значений для мини-графика (старые → новые).</summary>
    public void SetHistory(IReadOnlyList<double> values)
    {
        _history = values;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = ClientRectangle;
        Draw.Card(g, r, Px(8));
        var pad = Px(16);
        var accent = _accent ?? Theme.Accent;

        var top = new Rectangle(r.X + pad, r.Y + Px(13), r.Width - 2 * pad, Px(20));
        Draw.Glyph(g, _glyph, 10.5f, new Rectangle(top.X - Px(2), top.Y, Px(20), top.Height), accent);
        Draw.Text(g, _caption, Theme.Regular(9f), new Rectangle(top.X + Px(24), top.Y, top.Width - Px(24), top.Height),
            Theme.TextMuted, TextFormatFlags.VerticalCenter);

        var valueRect = new Rectangle(r.X + pad, top.Bottom + Px(2), r.Width - 2 * pad, Px(32));
        Draw.Text(g, _value, Theme.Semibold(17f), valueRect, Theme.TextPrimary, TextFormatFlags.VerticalCenter);
        var detailRect = new Rectangle(r.X + pad, valueRect.Bottom, r.Width - 2 * pad, Px(18));
        Draw.Text(g, _detail, Theme.Regular(8.5f), detailRect, Theme.TextMuted, TextFormatFlags.VerticalCenter);

        if (_fraction is double f)
        {
            var bar = new Rectangle(r.X + pad, r.Bottom - Px(16), r.Width - 2 * pad, Px(6));
            Draw.Fill(g, bar, Theme.Track, Px(3));
            var w = (int)Math.Round(bar.Width * f);
            if (w > 0) Draw.Fill(g, new Rectangle(bar.X, bar.Y, Math.Max(w, bar.Height), bar.Height), Theme.LoadColor(f), Px(3));
        }
        else if (_history.Count >= 2)
        {
            DrawSpark(g, new Rectangle(r.X + pad, r.Bottom - Px(20), r.Width - 2 * pad, Px(13)), accent);
        }
    }

    private void DrawSpark(Graphics g, Rectangle area, Color color)
    {
        var max = Math.Max(1e-9, _history.Max());
        var n = _history.Count;
        var pts = new PointF[n];
        for (var i = 0; i < n; i++)
        {
            var x = area.X + (float)area.Width * i / (n - 1);
            var y = area.Bottom - (float)(area.Height * Math.Clamp(_history[i] / max, 0, 1));
            pts[i] = new PointF(x, y);
        }
        using (var fill = new GraphicsPath())
        {
            fill.AddLines(pts);
            fill.AddLine(pts[^1], new PointF(area.Right, area.Bottom));
            fill.AddLine(new PointF(area.Right, area.Bottom), new PointF(area.X, area.Bottom));
            fill.CloseFigure();
            using var b = new SolidBrush(Color.FromArgb(40, color));
            g.FillPath(b, fill);
        }
        using var pen = new Pen(color, Px(2)) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }
}

/// <summary>Столбчатая диаграмма по категориям (дни): подписи оси X, сетка по Y, подсказка при наведении.</summary>
internal sealed class BarChart : PaintedControl
{
    public sealed record Bar(string Label, double Value, string Tooltip, bool Highlight = false);

    private IReadOnlyList<Bar> _bars = [];
    private int _hover = -1;
    private Func<double, string> _format = v => Ui.Short((long)Math.Round(v));

    public BarChart()
    {
        Height = 190;
        Dock = DockStyle.Fill;
        BackColor = Color.Transparent;
    }

    public string EmptyText { get; set; } = L.T("Нет данных");

    public void SetData(IReadOnlyList<Bar> bars, Func<double, string>? format = null)
    {
        _bars = bars;
        if (format is not null) _format = format;
        _hover = -1;
        Invalidate();
    }

    private Rectangle Plot => new(Px(52), Px(26), Math.Max(1, Width - Px(60)), Math.Max(1, Height - Px(26) - Px(26)));

    private Rectangle BarRect(int i, Rectangle plot, double max)
    {
        var slot = (float)plot.Width / Math.Max(1, _bars.Count);
        var bw = Math.Max(Px(3), (int)(slot * 0.62f));
        var x = plot.X + (int)(slot * i + (slot - bw) / 2);
        var h = max <= 0 ? 0 : (int)Math.Round(plot.Height * Math.Clamp(_bars[i].Value / max, 0, 1));
        if (_bars[i].Value > 0) h = Math.Max(h, Px(2));
        return new Rectangle(x, plot.Bottom - h, bw, h);
    }

    private static double NiceMax(double max)
    {
        if (max <= 0) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * exp >= max) return m * exp;
        return 10 * exp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = Plot;
        if (_bars.Count == 0 || _bars.All(b => b.Value <= 0))
        {
            Draw.Text(g, EmptyText, Theme.Regular(9f), ClientRectangle, Theme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var max = NiceMax(_bars.Max(b => b.Value));
        using (var grid = new Pen(Theme.Border) { DashStyle = DashStyle.Dot })
        {
            for (var k = 0; k <= 4; k++)
            {
                var y = plot.Bottom - plot.Height * k / 4;
                g.DrawLine(grid, plot.X, y, plot.Right, y);
                Draw.Text(g, _format(max * k / 4), Theme.Regular(8f), new Rectangle(0, y - Px(8), plot.X - Px(8), Px(16)),
                    Theme.TextFaint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        var slot = (float)plot.Width / _bars.Count;
        var labelEvery = Math.Max(1, (int)Math.Ceiling(Px(34) / Math.Max(1f, slot)));
        for (var i = 0; i < _bars.Count; i++)
        {
            var br = BarRect(i, plot, max);
            var color = _bars[i].Highlight ? Theme.Accent : Theme.Blend(Theme.Accent, Theme.Card, 0.35);
            if (i == _hover) color = Theme.AccentHover;
            if (br.Height > 0) Draw.Fill(g, br, color, Px(3));
            if (i % labelEvery == 0 || i == _bars.Count - 1)
            {
                var lr = new Rectangle((int)(plot.X + slot * i) - Px(10), plot.Bottom + Px(5), (int)slot + Px(20), Px(16));
                Draw.Text(g, _bars[i].Label, Theme.Regular(8f), lr, _bars[i].Highlight ? Theme.TextPrimary : Theme.TextFaint,
                    TextFormatFlags.HorizontalCenter);
            }
        }

        if (_hover >= 0 && _hover < _bars.Count)
        {
            var text = _bars[_hover].Tooltip;
            var font = Theme.Regular(8.5f);
            var size = TextRenderer.MeasureText(text, font);
            var br = BarRect(_hover, plot, max);
            var tip = new Rectangle(0, 0, size.Width + Px(16), size.Height + Px(8));
            tip.X = Math.Clamp(br.X + br.Width / 2 - tip.Width / 2, 0, Math.Max(0, Width - tip.Width));
            tip.Y = 0;
            Draw.Fill(g, tip, Theme.TooltipBack, Px(4));
            Draw.Text(g, text, font, tip, Theme.TooltipText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = Plot;
        var idx = -1;
        if (_bars.Count > 0 && e.X >= plot.X && e.X < plot.Right && e.Y >= 0 && e.Y <= plot.Bottom + Px(20))
            idx = Math.Clamp((int)((e.X - plot.X) / ((float)plot.Width / _bars.Count)), 0, _bars.Count - 1);
        if (idx != _hover)
        {
            _hover = idx;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }
}

/// <summary>Горизонтальные полосы «подпись — полоса — значение», по убыванию; лишние строки сворачиваются в «Прочие».</summary>
internal sealed class BarList : PaintedControl
{
    public sealed record Row(string Label, double Value, string ValueText);

    private IReadOnlyList<Row> _rows = [];

    public BarList()
    {
        Height = 200;
        Dock = DockStyle.Fill;
        BackColor = Color.Transparent;
    }

    public string EmptyText { get; set; } = L.T("Нет данных");

    public void SetData(IEnumerable<Row> rows)
    {
        _rows = rows.OrderByDescending(r => r.Value).ThenBy(r => r.Label).ToList();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_rows.Count == 0)
        {
            Draw.Text(g, EmptyText, Theme.Regular(9f), ClientRectangle, Theme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var rowH = Px(30);
        var fit = Math.Max(2, ClientSize.Height / rowH);
        var rows = _rows.ToList();
        if (rows.Count > fit)
        {
            var rest = rows.Skip(fit - 1).ToList();
            rows = rows.Take(fit - 1).ToList();
            var sum = rest.Sum(r => r.Value);
            rows.Add(new Row(L.F("Прочие ({0})", rest.Count), sum, Ui.N((long)sum)));
        }
        var max = Math.Max(1e-9, rows.Max(r => r.Value));
        var labelW = Math.Min(Px(200), (int)(Width * 0.42));
        var valueW = Px(64);
        for (var i = 0; i < rows.Count; i++)
        {
            var y = i * rowH;
            var row = rows[i];
            Draw.Text(g, row.Label, Theme.Regular(9f), new Rectangle(0, y, labelW - Px(8), rowH), Theme.TextPrimary,
                TextFormatFlags.VerticalCenter);
            var barArea = new Rectangle(labelW, y + (rowH - Px(10)) / 2, Math.Max(1, Width - labelW - valueW - Px(8)), Px(10));
            Draw.Fill(g, barArea, Theme.Track, Px(5));
            var w = (int)Math.Round(barArea.Width * row.Value / max);
            if (w > 0) Draw.Fill(g, new Rectangle(barArea.X, barArea.Y, Math.Max(w, barArea.Height), barArea.Height), Theme.Series(i), Px(5));
            Draw.Text(g, row.ValueText, Theme.Semibold(9f), new Rectangle(Width - valueW, y, valueW, rowH), Theme.TextPrimary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }
}
