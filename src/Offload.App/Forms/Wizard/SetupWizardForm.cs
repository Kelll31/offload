using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.App.Forms.Wizard;

/// <summary>«Мастер настройки Offload»: шаги слева, содержимое справа, кнопки Назад/Далее/Отмена внизу.</summary>
internal sealed class SetupWizardForm : Form
{
    private readonly IAppShell _shell;
    private readonly List<WizardStep> _steps;
    private readonly List<Label> _stepLabels = [];
    private readonly Panel _host;
    private readonly Label _heading = Kit.Label("", Theme.Semibold(14f));
    private readonly Label _subtitle = Kit.Wrap("", color: Theme.TextMuted);
    private readonly Button _back;
    private readonly Button _next;
    private readonly Button _cancel;
    private readonly DoneStep _done;
    private int _index = -1;
    private bool _closeWhenStopped;
    private bool _finishing;
    private bool _reopening;

    /// <summary>Положение окна для мастера, открываемого заново после смены языка (одноразово).</summary>
    private static (Rectangle Bounds, FormWindowState State)? _reopenPlacement;

    public SetupWizardForm(IAppShell shell)
    {
        _shell = shell;
        SuspendLayout();
        Text = L.T("Мастер настройки Offload");
        Icon = AppIcons.AppIcon;
        StartPosition = _reopenPlacement is null ? FormStartPosition.CenterScreen : FormStartPosition.Manual;
        ClientSize = new Size(920, 660);
        MinimumSize = new Size(820, 600);
        BackColor = Theme.Card;
        ForeColor = Theme.TextPrimary;
        ShowInTaskbar = true;
        MaximizeBox = true;

        var ctx = new WizardContext(shell, new WizardState(), this);
        var install = new InstallStep(ctx);
        _done = new DoneStep(ctx, install);
        _steps =
        [
            new WelcomeStep(ctx),
            new HardwareStep(ctx),
            new ModelStep(ctx),
            new ComponentsStep(ctx),
            new IdeStep(ctx),
            install,
            _done,
        ];
        install.Completed += (_, _) =>
        {
            // После успешной установки сразу переходим к итогам.
            if (_index == _steps.IndexOf(install)) Go(_index + 1);
        };

        _back = Kit.Button(L.T("< Назад"), (_, _) => Go(_index - 1));
        _next = Kit.Primary(L.T("Далее >"), (_, _) => Next());
        _cancel = Kit.Button(L.T("Отмена"), (_, _) => Close());
        _back.Margin = new Padding(8, 0, 0, 0);
        _next.Margin = new Padding(8, 0, 0, 0);
        _cancel.Margin = new Padding(16, 0, 0, 0);

        // Боковая панель со списком шагов.
        var sidebar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 20, 12, 20),
            Margin = Padding.Empty,
            ColumnCount = 1,
        };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var logo = new PictureBox
        {
            Image = AppIcons.Logo,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(40, 40),
            Margin = new Padding(0, 0, 0, 6),
            BackColor = Color.Transparent,
        };
        sidebar.AddRow(logo);
        var product = Kit.Label("Offload", Theme.Semibold(12f));
        product.Margin = new Padding(0, 0, 0, 18);
        sidebar.AddRow(product);
        for (var i = 0; i < _steps.Count; i++)
        {
            var l = Kit.Label($"{i + 1}.  {_steps[i].Title}");
            l.Margin = new Padding(0, 5, 0, 5);
            _stepLabels.Add(l);
            sidebar.AddRow(l);
        }
        sidebar.AddFillRow(new Panel { BackColor = Color.Transparent, Margin = Padding.Empty });

        // Заголовок, содержимое шага, кнопки.
        var header = Kit.Table();
        header.Padding = new Padding(24, 18, 24, 6);
        header.AddRow(_heading);
        _subtitle.Margin = new Padding(0, 2, 0, 0);
        header.AddRow(_subtitle);

        _host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Theme.Card };
        foreach (var s in _steps)
        {
            s.NavigationChanged += (_, _) =>
            {
                if (_index >= 0 && _steps[_index] == s) UpdateNavigation();
            };
            _host.Controls.Add(s);
        }

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(24, 12, 24, 14),
            Margin = Padding.Empty,
            BackColor = Theme.Surface,
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Theme.Card };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.AddRow(header);
        content.AddFillRow(_host);
        content.AddRow(Kit.Separator());
        content.AddRow(buttons);
        foreach (Control c in content.Controls) c.Margin = Padding.Empty;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(sidebar, 0, 0);
        root.Controls.Add(content, 1, 0);
        Controls.Add(root);

        AcceptButton = _next;
        CancelButton = _cancel;
        Kit.FinishForm(this);
        ResumeLayout(false);
        PerformLayout();
    }

    private WizardStep? Current => _index >= 0 && _index < _steps.Count ? _steps[_index] : null;

    /// <summary>Идёт установка (для подтверждения выхода из программы).</summary>
    public bool IsInstalling => _steps.Any(s => s.IsRunning);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_reopenPlacement is { } placement)
        {
            _reopenPlacement = null;
            Bounds = placement.Bounds;
            WindowState = placement.State;
        }
    }

    /// <summary>
    /// Открыть мастер заново (после смены языка): закрыть без вопросов, перевести главное окно и меню трея
    /// и показать новый мастер на том же месте. Во время установки не выполняется.
    /// </summary>
    public void Reopen(bool applyAppearance = true)
    {
        if (IsInstalling || _reopening || IsDisposed) return;
        _reopening = true;
        _reopenPlacement = (WindowState == FormWindowState.Normal ? Bounds : RestoreBounds,
            WindowState == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal);
        var shell = _shell;
        // Отложенно: обработчик выбора в списке ещё выполняется, а закрытие освобождает его элемент.
        shell.PostToUi(() =>
        {
            Close();
            try
            {
                if (applyAppearance) shell.ApplyAppearance();
            }
            finally
            {
                shell.ShowSetupWizard();
            }
        });
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_index < 0) Go(0);
    }

    private void Next()
    {
        if (Current is null) return;
        if (_index == _steps.Count - 1)
        {
            Finish();
            return;
        }
        Go(_index + 1);
    }

    private void Go(int index)
    {
        if (index < 0 || index >= _steps.Count || index == _index) return;
        var forward = index > _index;
        if (Current is { } cur)
        {
            if (!cur.OnLeave(forward)) return;
            cur.Visible = false;
        }
        _index = index;
        var step = _steps[index];
        step.Visible = true;
        step.BringToFront();
        try
        {
            step.OnEnter();
        }
        catch (Exception ex)
        {
            Log.Error("wizard", $"Шаг «{step.Title}»", ex);
            Ui.ShowError(this, L.F("Ошибка на шаге «{0}»", step.Title), ex);
        }
        UpdateNavigation();
        // Фокус на «Далее», чтобы Enter переходил дальше.
        if (_next.Enabled) _next.Focus();
    }

    private void UpdateNavigation()
    {
        var step = Current;
        if (step is null) return;
        _heading.Text = step.Heading;
        _subtitle.Text = step.Subtitle ?? "";
        _subtitle.Visible = !string.IsNullOrEmpty(step.Subtitle);
        _back.Enabled = _index > 0 && step.CanGoBack;
        _back.Visible = step != _done;
        _next.Enabled = step.CanGoNext;
        _next.Text = step == _done ? L.T("Готово") : step.NextText == L.T("Далее") ? L.T("Далее >") : step.NextText;
        _cancel.Visible = step != _done;

        for (var i = 0; i < _stepLabels.Count; i++)
        {
            var l = _stepLabels[i];
            var title = _steps[i].Title;
            if (i == _index)
            {
                l.Text = $"{i + 1}.  {title}";
                l.Font = Theme.Semibold(9f);
                l.ForeColor = Theme.Accent;
            }
            else if (i < _index)
            {
                l.Text = $"✓  {title}";
                l.Font = Theme.Regular(9f);
                l.ForeColor = Theme.TextPrimary;
            }
            else
            {
                l.Text = $"{i + 1}.  {title}";
                l.Font = Theme.Regular(9f);
                l.ForeColor = Theme.TextMuted;
            }
        }
    }

    private void Finish()
    {
        _finishing = true;
        var open = _done.OpenMainWindow;
        Close();
        if (open) _shell.ShowMainWindow(Tabs.Status);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_finishing || _reopening || e.CloseReason is CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            if (IsInstalling) foreach (var s in _steps) s.CancelRunning();
            base.OnFormClosing(e);
            return;
        }

        if (IsInstalling)
        {
            e.Cancel = true;
            if (_closeWhenStopped) return;
            if (!Ui.Confirm(this, L.T("Прервать установку? Уже скачанные файлы сохранятся, и установку можно будет продолжить позже."), warning: true)) return;
            _closeWhenStopped = true;
            foreach (var s in _steps) s.CancelRunning();
            _ = CloseWhenStoppedAsync();
            return;
        }

        if (Current != _done && !_closeWhenStopped &&
            !Ui.Confirm(this, L.T("Прервать настройку? Мастер можно открыть снова из меню значка Offload в области уведомлений.")))
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    private async Task CloseWhenStoppedAsync()
    {
        for (var i = 0; i < 600 && IsInstalling; i++) await Task.Delay(100);
        if (!IsDisposed) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (!ConfigStore.Current.SetupCompleted && !_reopening)
            _shell.Notify(L.T("Настройка Offload не завершена"), L.T("Продолжить можно из меню значка: «Мастер настройки…»."), ToolTipIcon.Warning);
        _shell.ConfigChanged();
    }
}
