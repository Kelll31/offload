using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Светлая палитра меню (вместо «офисного» градиента по умолчанию).</summary>
internal sealed class MenuColors : ProfessionalColorTable
{
    private static readonly Color Back = Color.FromArgb(252, 252, 252);
    private static readonly Color Hover = Theme.AccentLight;
    private static readonly Color HoverBorder = Color.FromArgb(204, 228, 247);

    public MenuColors() => UseSystemColors = false;

    public override Color ToolStripDropDownBackground => Back;
    public override Color ImageMarginGradientBegin => Back;
    public override Color ImageMarginGradientMiddle => Back;
    public override Color ImageMarginGradientEnd => Back;
    public override Color MenuBorder => Color.FromArgb(200, 200, 200);
    public override Color MenuItemBorder => HoverBorder;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
    public override Color SeparatorDark => Color.FromArgb(222, 222, 222);
    public override Color SeparatorLight => Back;
    public override Color CheckBackground => Color.FromArgb(214, 232, 248);
    public override Color CheckSelectedBackground => Color.FromArgb(196, 222, 245);
    public override Color CheckPressedBackground => Color.FromArgb(196, 222, 245);
    public override Color ButtonSelectedBorder => HoverBorder;

    public static ToolStripProfessionalRenderer Renderer() => new(new MenuColors()) { RoundedEdges = false };
}
