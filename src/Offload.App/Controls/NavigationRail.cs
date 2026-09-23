using System.ComponentModel;
using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>
/// Боковая навигация в духе Windows 11 (NavigationView): логотип, группы пунктов со значками,
/// индикатор выбранного пункта, блок состояния внизу и кнопка темы. Сворачивается до полосы значков.
/// Рисуется целиком вручную; клавиатура — стрелки, Home/End, Enter/пробел.
/// </summary>
internal sealed class NavigationRail : Control
{
    public sealed record Item(string Key, string Title, string Glyph, string? Group = null);

    private const int ExpandedWidth = 232;
    private const int CollapsedWidth = 52;
    private const int ItemHeight = 38;
    private const int HeaderHeight = 56;
    private const int GroupHeight = 30;
    private const int FooterHeight = 64;

    private readonly List<Item> _items = [];
    private readonly ToolTip _tip = new() { InitialDelay = 400, ShowAlways = true };
    private readonly Image? _logo;
    private int _selected = -1;
    private int _hover = -1;          // индекс пункта; -2 — блок состояния; -3 — кнопка темы; -4 — кнопка сворачивания
    private int _focusIndex = -1;
    private bool _collapsed;
    private string? _tipShownFor;

    private Color _statusColor = Color.Gray;
    private string _statusTitle = "";
    private string _statusDetail = "";

    public NavigationRail(Image? logo)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        _logo = logo;
        Dock = DockStyle.Left;
        Width = ExpandedWidth;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Regular(9.5f);
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = L.T("Разделы");
    }

    /// <summary>Выбран пункт (пользователем или программно).</summary>
    public event EventHandler<string>? SelectedChanged;

    /// <summary>Щелчок по блоку состояния внизу.</summary>
    public event EventHandler? StatusClicked;

    /// <summary>Щелчок по кнопке темы.</summary>
    public event EventHandler? ThemeClicked;

    /// <summary>Панель свёрнута/развёрнута пользователем.</summary>
    public event EventHandler? CollapsedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            // До создания окна — логическая ширина (форма масштабирует её сама при AutoScaleMode.Dpi), после — в пикселях устройства.
            var w = value ? CollapsedWidth : ExpandedWidth;
            Width = IsHandleCreated ? LogicalToDeviceUnits(w) : w;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedKey
    {
        get => _selected >= 0 ? _items[_selected].Key : null;
        set
        {
            var i = _items.FindIndex(x => string.Equals(x.Key, value, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i == _selected) return;
            _selected = i;
            _focusIndex = i;
            Invalidate();
            SelectedChanged?.Invoke(this, _items[i].Key);
        }
    }

    public void SetItems(IEnumerable<Item> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = _items.Count > 0 ? 0 : -1;
        Invalidate();
    }

    /// <summary>Состояние в нижнем блоке: цвет точки, заголовок и подробность (модель).</summary>
    public void SetStatus(Color color, string title, string detail)
    {
        if (_statusColor == color && _statusTitle == title && _statusDetail == detail) return;
        _statusColor = color;
        _statusTitle = title;
        _statusDetail = detail;
        Invalidate();
    }

    // ---- Раскладка ----

    private int Px(int logical) => LogicalToDeviceUnits(logical);

    private IEnumerable<(int Index, Rectangle Rect, string? GroupTitle, Rectangle GroupRect)> LayoutItems()
    {
        var y = Px(HeaderHeight);
        string? group = null;
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            Rectangle groupRect = Rectangle.Empty;
            string? groupTitle = null;
            if (it.Group is not null && it.Group != group)
            {
                group = it.Group;
                groupTitle = it.Group;
                var gh = Px(_collapsed ? 12 : GroupHeight);
                groupRect = new Rectangle(0, y, Width, gh);
                y += gh;
            }
            var r = new Rectangle(Px(6), y, Width - Px(12), Px(ItemHeight) - Px(2));
            yield return (i, r, groupTitle, groupRect);
            y += Px(ItemHeight);
        }
    }

    private Rectangle StatusRect => new(Px(6), Height - Px(FooterHeight) + Px(6), Width - Px(12) - (_collapsed ? 0 : Px(40)), Px(FooterHeight) - Px(14));

    private Rectangle ThemeRect => _collapsed
        ? new Rectangle(Px(6), Height - Px(FooterHeight) - Px(40), Width - Px(12), Px(36))
        : new Rectangle(Width - Px(6) - Px(36), Height - Px(FooterHeight) + Px(6) + (Px(FooterHeight) - Px(14) - Px(36)) / 2, Px(36), Px(36));

    private Rectangle ToggleRect => new(Px(6), Px(10), Px(36), Px(36));

    private int HitTest(Point p)
    {
        if (ToggleRect.Contains(p)) return -4;
        if (ThemeRect.Contains(p)) return -3;
        if (StatusRect.Contains(p)) return -2;
        foreach (var (i, r, _, _) in LayoutItems())
            if (r.Contains(p)) return i;
        return -1;
    }

    // ---- Отрисовка ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        // Кнопка сворачивания и логотип.
        DrawButtonBack(g, ToggleRect, _hover == -4);
        DrawGlyph(g, Glyphs.Menu, ToggleRect, Theme.TextPrimary, 11f);
        if (!_collapsed)
        {
            var x = ToggleRect.Right + Px(8);
            if (_logo is not null)
            {
                var size = Px(22);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_logo, new Rectangle(x, ToggleRect.Y + (ToggleRect.Height - size) / 2, size, size));
                x += size + Px(8);
            }
            TextRenderer.DrawText(g, "Offload", Theme.Semibold(11.5f), new Rectangle(x, ToggleRect.Y, Width - x, ToggleRect.Height),
                Theme.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        foreach (var (i, r, groupTitle, groupRect) in LayoutItems())
        {
            if (groupTitle is not null)
            {
                if (_collapsed)
                {
                    using var pen = new Pen(Theme.Border);
                    var gy = groupRect.Y + groupRect.Height / 2;
                    g.DrawLine(pen, Px(14), gy, Width - Px(14), gy);
                }
                else
                {
                    var gr = new Rectangle(Px(18), groupRect.Y + Px(8), groupRect.Width - Px(24), groupRect.Height - Px(8));
                    TextRenderer.DrawText(g, groupTitle, Theme.Semibold(8.5f), gr, Theme.TextFaint,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
            }
            DrawItem(g, _items[i], r, i == _selected, i == _hover, Focused && ShowFocusCues && i == _focusIndex);
        }

        DrawFooter(g);
    }

    private void DrawItem(Graphics g, Item it, Rectangle r, bool selected, bool hover, bool focused)
    {
        if (selected || hover)
        {
            using var b = new SolidBrush(selected ? Theme.NavSelected : Theme.NavHover);
            using var path = Rounded(r, Px(5));
            g.FillPath(b, path);
        }
        if (selected)
        {
            var pill = new Rectangle(r.X, r.Y + (r.Height - Px(16)) / 2, Px(3), Px(16));
            using var b = new SolidBrush(Theme.Accent);
            using var path = Rounded(pill, Px(2));
            g.FillPath(b, path);
        }
        if (focused)
        {
            using var pen = new Pen(Theme.TextPrimary, Px(1)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, Rectangle.Inflate(r, -1, -1));
        }

        var iconRect = new Rectangle(r.X + Px(8), r.Y, Px(24), r.Height);
        DrawGlyph(g, it.Glyph, iconRect, selected ? Theme.Accent : Theme.TextPrimary, 11.5f);
        if (!_collapsed)
        {
            var tr = new Rectangle(iconRect.Right + Px(10), r.Y, r.Right - iconRect.Right - Px(12), r.Height);
            TextRenderer.DrawText(g, it.Title, selected ? Theme.Semibold(9.5f) : Font, tr, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    private void DrawFooter(Graphics g)
    {
        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, Px(12), Height - Px(FooterHeight), Width - Px(12), Height - Px(FooterHeight));

        var sr = StatusRect;
        if (_hover == -2)
        {
            using var b = new SolidBrush(Theme.NavHover);
            using var path = Rounded(sr, Px(5));
            g.FillPath(b, path);
        }
        var dot = Px(10);
        var dotRect = new Rectangle(sr.X + Px(15), sr.Y + (sr.Height - dot) / 2, dot, dot);
        using (var b = new SolidBrush(_statusColor)) g.FillEllipse(b, dotRect);
        if (!_collapsed)
        {
            var tx = dotRect.Right + Px(12);
            var half = sr.Height / 2;
            TextRenderer.DrawText(g, _statusTitle, Theme.Semibold(9f), new Rectangle(tx, sr.Y + Px(4), sr.Right - tx - Px(4), half - Px(2)),
                Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _statusDetail, Theme.Regular(8.5f), new Rectangle(tx, sr.Y + half + Px(1), sr.Right - tx - Px(4), half - Px(4)),
                Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        var th = ThemeRect;
        DrawButtonBack(g, th, _hover == -3);
        DrawGlyph(g, Theme.IsDark ? Glyphs.Moon : Glyphs.Sun, th, Theme.TextPrimary, 11f);
    }

    private void DrawButtonBack(Graphics g, Rectangle r, bool hover)
    {
        if (!hover) return;
        using var b = new SolidBrush(Theme.NavHover);
        using var path = Rounded(r, Px(5));
        g.FillPath(b, path);
    }

    private static void DrawGlyph(Graphics g, string glyph, Rectangle r, Color color, float size) =>
        TextRenderer.DrawText(g, glyph, Theme.Icons(size), r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

    private static GraphicsPath Rounded(Rectangle r, int radius) => Draw.Rounded(r, radius);

    // ---- Мышь и клавиатура ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTest(e.Location);
        if (h != _hover)
        {
            _hover = h;
            Cursor = h == -1 ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
        var tip = h switch
        {
            >= 0 when _collapsed => _items[h].Title,
            -2 => _collapsed ? $"{_statusTitle}\n{_statusDetail}" : null,
            -3 => Theme.Mode switch
            {
                Theme.ModeLight => L.T("Тема: светлая (щелчок — тёмная)"),
                Theme.ModeDark => L.T("Тема: тёмная (щелчок — как в Windows)"),
                _ => L.T("Тема: как в Windows (щелчок — светлая)"),
            },
            -4 => _collapsed ? L.T("Развернуть панель") : L.T("Свернуть панель"),
            _ => null,
        };
        if (tip != _tipShownFor)
        {
            _tipShownFor = tip;
            if (tip is null) _tip.Hide(this);
            else _tip.Show(tip, this, e.X + Px(16), e.Y + Px(12), 3000);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tipShownFor = null;
        _tip.Hide(this);
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        switch (HitTest(e.Location))
        {
            case -4:
                Collapsed = !Collapsed;
                CollapsedChanged?.Invoke(this, EventArgs.Empty);
                break;
            case -3:
                ThemeClicked?.Invoke(this, EventArgs.Empty);
                break;
            case -2:
                StatusClicked?.Invoke(this, EventArgs.Empty);
                break;
            case >= 0 and var i:
                SelectedKey = _items[i].Key;
                break;
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        if (_focusIndex < 0) _focusIndex = Math.Max(0, _selected);
        switch (e.KeyCode)
        {
            case Keys.Up: _focusIndex = (_focusIndex - 1 + _items.Count) % _items.Count; break;
            case Keys.Down: _focusIndex = (_focusIndex + 1) % _items.Count; break;
            case Keys.Home: _focusIndex = 0; break;
            case Keys.End: _focusIndex = _items.Count - 1; break;
            case Keys.Enter or Keys.Space: SelectedKey = _items[_focusIndex].Key; break;
            default: return;
        }
        e.Handled = true;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Коды значков Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
internal static class Glyphs
{
    public const string Menu = "";
    public const string Status = "";
    public const string Models = "";
    public const string Server = "";
    public const string Integrations = "";
    public const string Code = "";
    public const string Prompt = "";
    public const string Log = "";
    public const string Info = "";
    public const string Sun = "";
    public const string Moon = "";
    public const string Play = "";
    public const string Stop = "";
    public const string Refresh = "";
    public const string Copy = "";
    public const string Download = "";
    public const string Folder = "";
    public const string Check = "";
    public const string Cancel = "";
    public const string Warning = "";
    public const string History = "";
    public const string Bolt = "";
    public const string Speed = "";
    public const string Chat = "";
    public const string Settings = "";
    public const string Search = "";
    public const string Filter = "";
    public const string Delete = "";
    public const string Link = "";
    public const string Wand = "";
}
