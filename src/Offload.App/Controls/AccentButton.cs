using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Основная (акцентная) кнопка: синий фон, белый текст.</summary>
internal sealed class AccentButton : Button
{
    public AccentButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 1;
        UseVisualStyleBackColor = false;
        ApplyColors();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        ApplyColors();
        base.OnEnabledChanged(e);
    }

    private void ApplyColors()
    {
        if (Enabled)
        {
            BackColor = Theme.Accent;
            ForeColor = Color.White;
            FlatAppearance.BorderColor = Theme.Accent;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 84, 166);
            FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 70, 140);
        }
        else
        {
            BackColor = Color.FromArgb(230, 230, 230);
            ForeColor = Theme.Gray;
            FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
        }
    }
}
