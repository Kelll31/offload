using System.ComponentModel;
using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Вид кнопки: обычная (карточка с рамкой), основная (градиент акцента), тихая (без фона до наведения).</summary>
internal enum ButtonKind { Standard, Primary, Subtle }

/// <summary>
/// Кнопка нового оформления: скруглённая, с плавными состояниями наведения и нажатия, кольцом фокуса клавиатуры
/// и необязательным значком слева. Основная кнопка залита градиентом акцента (<see cref="Theme.Fill"/> → <see cref="Theme.FillEnd"/>).
/// Остаётся обычной <see cref="Button"/>: доступность, AcceptButton, клавиатура и DialogResult работают как прежде.
/// </summary>
internal class ModernButton : Button
{
    private bool _hover;
    private bool _pressed;
    private string? _glyph;
    private ButtonKind _kind;

    public ModernButton(ButtonKind kind = ButtonKind.Standard)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        _kind = kind;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        Font = Theme.Semibold(9f);
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Invalidate();
        }
    }

    /// <summary>Значок Segoe Fluent Icons слева от текста (null — без значка).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Parent?.PerformLayout(this, nameof(Glyph));
            Invalidate();
        }
    }

    private int Px(int logical) => LogicalToDeviceUnits(logical);

    private int GlyphWidth => _glyph is null ? 0 : Px(22);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var w = text.Width + GlyphWidth + Padding.Horizontal + Px(24);
        var h = Math.Max(text.Height + Px(14), Px(32));
        return new Size(Math.Max(w, MinimumSize.Width), Math.Max(h, MinimumSize.Height));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        base.OnMouseDown(mevent);
        if (mevent.Button == MouseButtons.Left) _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        using var b = new SolidBrush(Draw.EffectiveBack(this));
        pevent.Graphics.FillRectangle(b, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        OnPaintBackground(pevent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(Px(1), Px(1), Width - Px(2) - 1, Height - Px(2) - 1);
        var radius = Px(Theme.RadiusControl + 2);
        Color text;
        using (var path = Draw.Rounded(r, radius))
        {
            if (!Enabled)
            {
                using var b = new SolidBrush(_kind == ButtonKind.Subtle ? Draw.EffectiveBack(this) : Theme.Track);
                g.FillPath(b, path);
                text = Theme.TextFaint;
            }
            else if (_kind == ButtonKind.Primary)
            {
                var start = _pressed ? Theme.Blend(Theme.Fill, Color.Black, 0.18) : _hover ? Theme.Blend(Theme.Fill, Color.White, 0.10) : Theme.Fill;
                var end = _pressed ? Theme.Blend(Theme.FillEnd, Color.Black, 0.18) : _hover ? Theme.Blend(Theme.FillEnd, Color.White, 0.10) : Theme.FillEnd;
                using var b = new LinearGradientBrush(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)), start, end, LinearGradientMode.Horizontal);
                g.FillPath(b, path);
                // Лёгкий блик сверху — объём без тени.
                using var hl = new Pen(Color.FromArgb(_pressed ? 0 : 40, Color.White), 1f);
                g.DrawLine(hl, r.X + radius, r.Y + 1, r.Right - radius, r.Y + 1);
                text = Theme.OnFill;
            }
            else
            {
                var back = Draw.EffectiveBack(this);
                var fill = _kind == ButtonKind.Subtle
                    ? (_pressed ? Theme.NavSelected : _hover ? Theme.NavHover : back)
                    : (_pressed ? Theme.NavSelected : _hover ? Theme.CardHover : Theme.Blend(back, Theme.Card, 0.6));
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (_kind == ButtonKind.Standard)
                {
                    using var pen = new Pen(_hover ? Theme.Blend(Theme.Border, Theme.Accent, 0.45) : Theme.Border, 1f);
                    g.DrawPath(pen, path);
                }
                text = Theme.TextPrimary;
            }
        }

        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Accent, Px(2));
            using var fr = Draw.Rounded(new Rectangle(r.X, r.Y, r.Width, r.Height), radius);
            g.DrawPath(ring, fr);
        }

        var content = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var total = content.Width + GlyphWidth;
        var x = Math.Max(r.X + Px(8), r.X + (r.Width - total) / 2);
        if (_glyph is not null)
        {
            var gr = new Rectangle(x, r.Y, Px(18), r.Height);
            Draw.Glyph(g, _glyph, 10f, gr, _kind == ButtonKind.Primary || !Enabled ? text : Theme.Accent);
            x += GlyphWidth;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, r.Y, r.Right - x - Px(6), r.Height), text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding
            | TextFormatFlags.EndEllipsis | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
    }
}
