using System.ComponentModel;
using Offload.App.Util;
using Offload.Core.Util;

namespace Offload.App.Controls;

/// <summary>
/// Прогресс длительной операции: этап (Stage), полоса (Fraction; null — «бегущая» полоса),
/// подробности (Detail — скорость, осталось времени) и необязательная кнопка «Отмена».
/// </summary>
internal sealed class ProgressPanel : TableLayoutPanel
{
    private readonly Label _stage;
    private readonly Label _detail;
    private readonly ProgressBar _bar;
    private readonly Button? _cancel;

    public ProgressPanel(bool withCancel = true)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Margin = new Padding(0, 6, 0, 6);
        Padding = Padding.Empty;
        BackColor = Color.Transparent;

        _stage = Kit.Wrap("", Theme.Semibold(9f));
        _stage.Margin = new Padding(0, 0, 0, 2);
        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 1000,
            Height = 16,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 8, 4),
        };
        _detail = Kit.Wrap("", Theme.Regular(8.5f), Theme.TextMuted);
        _detail.Margin = new Padding(0, 0, 0, 0);

        this.AddRow(_stage);
        if (withCancel)
        {
            _cancel = Kit.Button("Отмена", (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty), 88);
            _cancel.Anchor = AnchorStyles.Right;
            this.AddRow(_bar, _cancel);
        }
        else
        {
            this.AddRow(_bar);
        }
        this.AddRow(_detail);
        Visible = false;
    }

    /// <summary>Нажата кнопка «Отмена».</summary>
    public event EventHandler? CancelRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CancelEnabled
    {
        get => _cancel?.Enabled ?? false;
        set
        {
            if (_cancel is not null) _cancel.Enabled = value;
        }
    }

    /// <summary>Создать IProgress, вызывающий Report в потоке интерфейса.</summary>
    public IProgress<StepProgress> CreateProgress() => new Progress<StepProgress>(Report);

    public void Start(string stage)
    {
        Visible = true;
        CancelEnabled = true;
        Report(new StepProgress(stage));
    }

    public void Report(StepProgress p)
    {
        if (IsDisposed) return;
        Visible = true;
        _stage.Text = p.Stage;
        _detail.Text = p.Detail ?? "";
        if (p.Fraction is double f)
        {
            if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = (int)Math.Round(Math.Clamp(f, 0, 1) * 1000);
        }
        else if (_bar.Style != ProgressBarStyle.Marquee)
        {
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;
        }
    }

    /// <summary>Показать итог (полоса останавливается).</summary>
    public void Finish(string text, bool success)
    {
        if (IsDisposed) return;
        Visible = true;
        CancelEnabled = false;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = success ? 1000 : 0;
        _stage.Text = text;
        _stage.ForeColor = success ? Theme.OkText : Theme.ErrorText;
        _detail.Text = "";
    }

    public void Reset()
    {
        _stage.ForeColor = Theme.TextPrimary;
        _stage.Text = "";
        _detail.Text = "";
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = 0;
        Visible = false;
    }
}
