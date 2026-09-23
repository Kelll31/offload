using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>Основная (акцентная) кнопка: фон цвета акцента, контрастный текст.</summary>
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
            ForeColor = Theme.OnAccent;
            FlatAppearance.BorderColor = Theme.Accent;
            FlatAppearance.MouseOverBackColor = Theme.AccentHover;
            FlatAppearance.MouseDownBackColor = Theme.AccentPressed;
        }
        else
        {
            BackColor = Theme.Track;
            ForeColor = Theme.TextFaint;
            FlatAppearance.BorderColor = Theme.Border;
        }
    }
}
