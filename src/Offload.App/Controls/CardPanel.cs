using System.ComponentModel;
using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>
/// Карточка: таблица на фоне <see cref="Theme.Card"/> со скруглением, тонкой рамкой и мягкой тенью.
/// <see cref="Hero"/> — выделенная карточка: лёгкий градиент акцента и цветная полоса слева.
/// </summary>
internal sealed class CardPanel : TableLayoutPanel
{
    private bool _hero;
    private Color? _edge;

    public CardPanel()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Padding = new Padding(18, 16, 18, 18);
        Margin = new Padding(0, 0, 0, 10);
        BackColor = Theme.Card;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>Выделенная карточка (главный блок страницы).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Hero
    {
        get => _hero;
        set
        {
            _hero = value;
            Invalidate();
        }
    }

    /// <summary>Цвет полосы слева у выделенной карточки (null — градиент акцента).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? EdgeColor
    {
        get => _edge;
        set
        {
            _edge = value;
            Invalidate();
        }
    }

    private Rectangle Body => new(0, 0, ClientSize.Width - 1, ClientSize.Height - 4);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Draw.Rounded(Body, LogicalToDeviceUnits(Theme.RadiusCard));
        using var pen = new Pen(_hero ? Theme.Blend(Theme.Border, Theme.Accent, 0.35) : Theme.Border, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Фон родителя за скруглёнными углами и тенью, затем тень и заливка карточки.
        var parentColor = Parent is null ? Theme.Surface : Draw.EffectiveBack(Parent);
        using (var b = new SolidBrush(parentColor)) e.Graphics.FillRectangle(b, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var body = Body;
        var radius = LogicalToDeviceUnits(Theme.RadiusCard);
        Draw.Shadow(e.Graphics, body, radius);
        using var path = Draw.Rounded(body, radius);
        if (_hero)
        {
            using var fill = new LinearGradientBrush(body, Theme.Blend(BackColor, Theme.Accent, Theme.IsDark ? 0.10 : 0.07),
                Theme.Blend(BackColor, Theme.FillEnd, Theme.IsDark ? 0.03 : 0.02), LinearGradientMode.ForwardDiagonal);
            e.Graphics.FillPath(fill, path);
            // Полоса слева внутри скругления.
            var saved = e.Graphics.Clip;
            using var region = new Region(path);
            e.Graphics.SetClip(region, CombineMode.Intersect);
            var edge = new Rectangle(body.X, body.Y, LogicalToDeviceUnits(4), body.Height);
            if (_edge is { } c)
            {
                using var eb = new SolidBrush(c);
                e.Graphics.FillRectangle(eb, edge);
            }
            else
            {
                using var eb = new LinearGradientBrush(new Rectangle(edge.X, edge.Y, edge.Width, Math.Max(1, edge.Height)), Theme.Fill, Theme.FillEnd, LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(eb, edge);
            }
            e.Graphics.Clip = saved;
        }
        else
        {
            using var fill = new SolidBrush(BackColor);
            e.Graphics.FillPath(fill, path);
        }
    }
}
