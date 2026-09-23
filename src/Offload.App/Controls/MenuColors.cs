using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Палитра меню трея под тему (вместо «офисного» градиента по умолчанию).</summary>
internal sealed class MenuColors : ProfessionalColorTable
{
    private static Color Back => Theme.IsDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(252, 252, 252);
    private static Color Hover => Theme.IsDark ? Color.FromArgb(58, 58, 58) : Theme.AccentLight;
    private static Color HoverBorder => Theme.IsDark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(204, 228, 247);
    private static Color Check => Theme.IsDark ? Color.FromArgb(38, 58, 72) : Color.FromArgb(214, 232, 248);

    public MenuColors() => UseSystemColors = false;

    public override Color ToolStripDropDownBackground => Back;
    public override Color ImageMarginGradientBegin => Back;
    public override Color ImageMarginGradientMiddle => Back;
    public override Color ImageMarginGradientEnd => Back;
    public override Color MenuBorder => Theme.IsDark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(200, 200, 200);
    public override Color MenuItemBorder => HoverBorder;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Back;
    public override Color CheckBackground => Check;
    public override Color CheckSelectedBackground => Check;
    public override Color CheckPressedBackground => Check;
    public override Color ButtonSelectedBorder => HoverBorder;

    /// <summary>Рендерер меню: палитра темы и цвет текста пунктов (в тёмной теме — светлый).</summary>
    public static ToolStripProfessionalRenderer Renderer() => new ThemedRenderer();

    private sealed class ThemedRenderer : ToolStripProfessionalRenderer
    {
        public ThemedRenderer() : base(new MenuColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.TextMuted;
            base.OnRenderArrow(e);
        }
    }
}
