using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;

namespace Offload.App.Forms;

/// <summary>Справка по сочетаниям клавиш (F1 в панели управления): клавиши нарисованы «клавишами», рядом — действие.</summary>
internal sealed class ShortcutsForm : Form
{
    public ShortcutsForm()
    {
        Text = L.T("Сочетания клавиш");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Surface;
        ForeColor = Theme.TextPrimary;
        Icon = AppIcons.AppIcon;
        ClientSize = new Size(560, 470);
        KeyPreview = true;

        var card = new CardPanel { ColumnCount = 2 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (keys, what) in Items())
        {
            var caps = new KeyCaps(keys) { Margin = new Padding(0, 5, 18, 5) };
            var label = Kit.Label(what, Theme.Regular(9.5f));
            label.Margin = new Padding(0, 9, 0, 5);
            card.AddRow(caps, label);
        }
        var root = Kit.Table();
        root.AddRow(Kit.Section(L.T("Панель управления"), first: true));
        root.AddRow(card);
        var close = Kit.Primary(L.T("Закрыть"), (_, _) => Close());
        close.Anchor = AnchorStyles.Right;
        root.AddRow(close);
        Controls.Add(Kit.Scroll(root, new Padding(22, 18, 22, 16)));
        AcceptButton = close;
        CancelButton = close;
        Kit.FinishForm(this);
    }

    internal static IReadOnlyList<(string[] Keys, string What)> Items() =>
    [
        (["Ctrl", "K"], L.T("Палитра команд: найти раздел или действие")),
        (["Ctrl", "1…9"], L.T("Открыть раздел по номеру")),
        (["Ctrl", "Tab"], L.T("Следующий раздел (с Shift — предыдущий)")),
        (["Ctrl", "Shift", "N"], L.T("Центр уведомлений")),
        (["F1"], L.T("Эта справка")),
        (["Ctrl", "Alt", "O"], L.T("Открыть Offload из любой программы (глобально)")),
        (["Esc"], L.T("Закрыть палитру или диалог")),
    ];

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.F1 or Keys.Escape) Close();
        base.OnKeyDown(e);
    }
}

/// <summary>Сочетание клавиш, нарисованное клавишами: [Ctrl] + [K].</summary>
internal sealed class KeyCaps : Control
{
    private readonly string[] _keys;

    public KeyCaps(string[] keys)
    {
        _keys = keys;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = string.Join(" + ", keys);
        Font = Theme.Semibold(8.5f);
        Size = GetPreferredSize(Size.Empty);
    }

    private int Px(int v) => LogicalToDeviceUnits(v);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var w = 0;
        foreach (var k in _keys) w += KeyWidth(k) + Px(18);
        return new Size(Math.Max(1, w - Px(18) + Px(2)), Px(28));
    }

    private int KeyWidth(string k) =>
        Math.Max(Px(28), TextRenderer.MeasureText(k, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Px(16));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(Draw.EffectiveBack(this))) g.FillRectangle(b, ClientRectangle);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var x = 0;
        for (var i = 0; i < _keys.Length; i++)
        {
            var w = KeyWidth(_keys[i]);
            var key = new Rectangle(x, 0, w, Height - Px(3));
            // Клавиша: «объём» — полоса снизу темнее.
            Draw.Fill(g, new Rectangle(key.X, key.Y + Px(2), key.Width, key.Height), Theme.Border, Px(6));
            Draw.Fill(g, key, Theme.Surface, Px(6));
            using (var pen = new Pen(Theme.Border))
            using (var path = Draw.Rounded(new Rectangle(key.X, key.Y, key.Width - 1, key.Height - 1), Px(6)))
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, _keys[i], Font, key, Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += w;
            if (i < _keys.Length - 1)
            {
                TextRenderer.DrawText(g, "+", Theme.Regular(9f), new Rectangle(x, 0, Px(18), key.Height), Theme.TextFaint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += Px(18);
            }
        }
    }
}
