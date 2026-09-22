using System.ComponentModel;
using Offload.App.Util;

namespace Offload.App.Controls;

/// <summary>
/// Выбор значения: фиксированные варианты («Авто», «Выключено») или своё число.
/// Пример: слои на GPU — «Авто (все)» = -1 либо конкретное число.
/// </summary>
internal sealed class OptionalNumberBox : FlowLayoutPanel
{
    private readonly ComboBox _mode;
    private readonly NumericUpDown _number;
    private readonly List<(string Text, int? Value)> _options;

    /// <param name="options">Варианты; Value = null — «своё число».</param>
    public OptionalNumberBox(IReadOnlyList<(string Text, int? Value)> options, int min, int max, int customDefault)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        Anchor = AnchorStyles.Left;
        BackColor = Color.Transparent;

        _options = options.ToList();
        _mode = Kit.Combo(170);
        foreach (var o in _options) _mode.Items.Add(o.Text);
        _number = Kit.Number(min, max, customDefault, 90);
        _mode.SelectedIndexChanged += (_, _) =>
        {
            _number.Visible = _options[Math.Max(0, _mode.SelectedIndex)].Value is null;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        _number.ValueChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_mode);
        Controls.Add(_number);
        _mode.SelectedIndex = 0;
    }

    public event EventHandler? ValueChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get
        {
            var opt = _options[Math.Max(0, _mode.SelectedIndex)];
            return opt.Value ?? (int)_number.Value;
        }
        set
        {
            var idx = _options.FindIndex(o => o.Value == value);
            if (idx < 0)
            {
                idx = _options.FindIndex(o => o.Value is null);
                if (idx < 0) idx = 0;
                _number.Value = Math.Clamp(value, _number.Minimum, _number.Maximum);
            }
            _mode.SelectedIndex = idx;
            _number.Visible = _options[idx].Value is null;
        }
    }
}
