using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>
/// Переключатель в духе Windows 11 вместо флажка: дорожка с бегунком, текст справа. Остаётся <see cref="CheckBox"/>
/// (Checked, CheckState, события, клавиатура и доступность не меняются); промежуточное состояние — бегунок посередине.
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    private const int TrackWidth = 38;
    private const int TrackHeight = 20;
    private const int Gap = 10;
    private bool _hover;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    private int Px(int logical) => LogicalToDeviceUnits(logical);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = string.IsNullOrEmpty(Text)
            ? Size.Empty
            : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var w = Px(TrackWidth) + (text.Width > 0 ? Px(Gap) + text.Width + Px(2) : 0) + Padding.Horizontal;
        var h = Math.Max(Px(TrackHeight) + Px(4), text.Height + Px(6));
        return new Size(w, h);
    }

    protected override void OnMouseEnter(EventArgs eventargs) { base.OnMouseEnter(eventargs); _hover = true; Invalidate(); }

    protected override void OnMouseLeave(EventArgs eventargs) { base.OnMouseLeave(eventargs); _hover = false; Invalidate(); }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        using var b = new SolidBrush(Draw.EffectiveBack(this));
        pevent.Graphics.FillRectangle(b, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        OnPaintBackground(pevent);
        if (Px(TrackWidth) <= 0 || Px(TrackHeight) <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(Padding.Left + Px(1), (Height - Px(TrackHeight)) / 2, Px(TrackWidth), Px(TrackHeight));
        var radius = track.Height / 2;
        var state = CheckState;
        using (var path = Draw.Rounded(track, radius))
        {
            if (!Enabled)
            {
                using var b = new SolidBrush(state == CheckState.Checked ? Theme.Blend(Theme.Track, Theme.Accent, 0.35) : Theme.Track);
                g.FillPath(b, path);
            }
            else if (state == CheckState.Checked)
            {
                var start = _hover ? Theme.Blend(Theme.Fill, Color.White, 0.08) : Theme.Fill;
                var end = _hover ? Theme.Blend(Theme.FillEnd, Color.White, 0.08) : Theme.FillEnd;
                using var b = new LinearGradientBrush(track, start, end, LinearGradientMode.Horizontal);
                g.FillPath(b, path);
            }
            else
            {
                using var b = new SolidBrush(state == CheckState.Indeterminate ? Theme.AccentLight : Draw.EffectiveBack(this));
                g.FillPath(b, path);
                using var pen = new Pen(_hover ? Theme.TextMuted : Theme.TextFaint, Math.Max(1f, Px(1) * 1.2f));
                g.DrawPath(pen, path);
            }
        }

        var knobD = state == CheckState.Checked || _hover ? Px(14) : Px(12);
        var knobX = state switch
        {
            CheckState.Checked => track.Right - Px(3) - knobD,
            CheckState.Indeterminate => track.X + (track.Width - knobD) / 2,
            _ => track.X + Px(4),
        };
        var knob = new Rectangle(knobX, track.Y + (track.Height - knobD) / 2, knobD, knobD);
        var knobColor = !Enabled ? Theme.TextFaint
            : state == CheckState.Checked ? Theme.OnFill
            : state == CheckState.Indeterminate ? Theme.Accent
            : Theme.TextMuted;
        using (var b = new SolidBrush(knobColor)) g.FillEllipse(b, knob);

        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Accent, Px(2));
            using var fr = Draw.Rounded(Rectangle.Inflate(track, Px(2), Px(2)), radius + Px(2));
            g.DrawPath(ring, fr);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            var tx = track.Right + Px(Gap);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height), Enabled ? ForeColorOrDefault() : Theme.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private Color ForeColorOrDefault() => ForeColor.IsEmpty ? Theme.TextPrimary : ForeColor;
}
