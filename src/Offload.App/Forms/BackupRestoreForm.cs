using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Integrations;

namespace Offload.App.Forms;

/// <summary>
/// Восстановление конфигурации IDE из резервной копии, сделанной Offload перед правкой: список копий (новые сверху),
/// просмотр в Блокноте и восстановление с подтверждением (текущий файл тоже сохраняется в копию).
/// </summary>
internal sealed class BackupRestoreForm : Form
{
    private readonly IIdeIntegration _integration;
    private readonly ListView _list = Kit.List((L.T("Когда"), 22), (L.T("Файл"), 62), (L.T("Размер"), 16));
    private readonly ModernButton _restore;
    private readonly ModernButton _view;
    private readonly Label _status = Kit.Wrap("", color: Theme.TextMuted);

    public BackupRestoreForm(IIdeIntegration integration)
    {
        _integration = integration;
        Text = L.F("Резервные копии — {0}", integration.DisplayName);
        Icon = AppIcons.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.TextPrimary;
        ClientSize = new Size(760, 440);
        MinimumSize = new Size(560, 360);

        _restore = Kit.Primary(L.T("Восстановить"), (_, _) => Restore(), 130);
        _view = Kit.Button(L.T("Открыть копию"), (_, _) => View(), 130);
        var close = Kit.Button(L.T("Закрыть"), (_, _) => Close(), 100);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => View();

        var root = Kit.FillTable();
        root.Padding = new Padding(20, 16, 20, 14);
        root.AddRow(Kit.Wrap(L.T("Перед каждой правкой настроек IDE Offload сохраняет копию файла. Восстановление вернёт выбранную версию; текущий файл перед этим тоже сохранится в копию."),
            Theme.Regular(9f), Theme.TextMuted));
        root.AddFillRow(_list);
        root.AddRow(_status);
        root.AddRow(Kit.Flow(_restore, _view, close));
        Controls.Add(root);
        CancelButton = close;
        Kit.FinishForm(this);
        Reload();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(this);
    }

    private ConfigBackup? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ConfigBackup : null;

    private void Reload()
    {
        var backups = Ui.Try(() => ConfigBackups.For(_integration), [], "ConfigBackups.For");
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var b in backups)
            _list.Items.Add(new ListViewItem([b.Created.ToString("g", L.Culture), b.ConfigPath, Offload.Core.Util.FileUtil.FormatBytes(b.Size)]) { Tag = b });
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        _status.Text = backups.Count == 0 ? L.T("Резервных копий этой IDE нет: Offload ещё не менял её настройки (или копии старше 30 дней удалены).") : "";
        UpdateButtons();
    }

    private void UpdateButtons() => _restore.Enabled = _view.Enabled = Selected is not null;

    private void View()
    {
        if (Selected is { } b) Ui.OpenInNotepad(b.BackupPath);
    }

    private void Restore()
    {
        if (Selected is not { } b) return;
        if (!Ui.Confirm(this, L.F("Восстановить {0} из копии от {1:g}? Закройте {2}, чтобы программа не перезаписала файл.", b.ConfigPath, b.Created, _integration.DisplayName)))
            return;
        IntegrationWatcher.PauseFor(TimeSpan.FromMinutes(2));
        var r = ConfigBackups.Restore(b);
        if (r.Ok) Reload();
        else Ui.ShowError(this, L.T("Не удалось восстановить файл"), r.Message);
        _status.ForeColor = r.Ok ? Theme.OkText : Theme.ErrorText;
        _status.Text = r.Message + (r.BackupPath is { } p ? Environment.NewLine + L.F("Текущая версия сохранена: {0}", p) : "");
    }
}
