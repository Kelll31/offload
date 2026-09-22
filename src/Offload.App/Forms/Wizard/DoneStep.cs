using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Llama;

namespace Offload.App.Forms.Wizard;

/// <summary>Шаг 7: итоги и что делать дальше. Отмечает настройку завершённой, если установлены llama.cpp и модель.</summary>
internal sealed class DoneStep : WizardStep
{
    private readonly InstallStep _install;
    private readonly Label _headline = Kit.Label("", Theme.Semibold(11f));
    private readonly TableLayoutPanel _summary = Kit.Table(0, 0, 100);
    private readonly Label _next = Kit.Wrap("");
    private readonly Label _hints = Kit.Hint("");
    private readonly CheckBox _openMain = Kit.Check("Открыть панель управления", true);
    private readonly List<(Label Glyph, Label Title, Label Detail)> _rows = [];
    private bool _coreReady;

    public DoneStep(WizardContext ctx, InstallStep install) : base(ctx)
    {
        _install = install;
        var root = Kit.Table();
        root.AddRow(_headline);
        root.AddRow(_summary);
        // Строки создаются заранее (по числу этапов), чтобы они масштабировались вместе с формой.
        for (var i = 0; i < 12; i++)
        {
            var g = Kit.Label("", Theme.Semibold(10f));
            g.Margin = new Padding(0, 2, 8, 2);
            var t = Kit.Label("", Theme.Semibold(9f));
            t.Margin = new Padding(0, 3, 12, 3);
            var d = Kit.Wrap("", color: Theme.TextMuted);
            d.Margin = new Padding(0, 3, 0, 3);
            _summary.AddRow(g, t, d);
            _rows.Add((g, t, d));
        }
        root.AddRow(Kit.Section("Что дальше"));
        root.AddRow(_next);
        root.AddRow(_hints);
        root.AddRow(Kit.Spacer(8));
        root.AddRow(_openMain);
        SetContent(root);
    }

    public override string Title => "Готово";

    public override string Heading => _coreReady ? "Готово!" : "Настройка не завершена";

    public override string? Subtitle => _coreReady
        ? "Offload установлен и работает в области уведомлений."
        : "Основные компоненты не установлены — мастер можно запустить снова из меню значка Offload.";

    public override string NextText => "Готово";

    public override bool CanGoBack => false;

    /// <summary>Открыть панель управления после закрытия мастера.</summary>
    public bool OpenMainWindow => _openMain.Checked;

    public override void OnEnter()
    {
        var cfg = ConfigStore.Reload();
        var llama = Ui.Try(() => LlamaInstaller.IsInstalled(cfg), false, "IsInstalled");
        var model = cfg.ActiveModel();
        _coreReady = llama && model is not null && File.Exists(model.FilePath);
        if (_coreReady && !cfg.SetupCompleted)
        {
            Ui.RunSafe(Ctx.Form, () => ConfigStore.Update(c => c.SetupCompleted = true), "Не удалось сохранить настройки");
            Log.Info("wizard", "Первоначальная настройка завершена");
        }
        Ctx.Shell.ConfigChanged();

        var tasks = _install.Tasks;
        var warnings = tasks.Count(t => t.Status is StageStatus.Warning or StageStatus.Failed);
        _headline.Text = !_coreReady ? "Не установлены llama.cpp или модель."
            : warnings > 0 ? "Установка завершена, но есть предупреждения."
            : "Всё установлено и проверено.";
        _headline.ForeColor = !_coreReady ? Theme.ErrorText : warnings > 0 ? Theme.WarnText : Theme.OkText;

        for (var i = 0; i < _rows.Count; i++)
        {
            var (g, t, d) = _rows[i];
            var visible = i < tasks.Count;
            g.Visible = t.Visible = d.Visible = visible;
            if (!visible) continue;
            var task = tasks[i];
            (g.Text, g.ForeColor) = task.Status switch
            {
                StageStatus.Done => ("✓", Theme.OkText),
                StageStatus.Warning => ("⚠", Theme.WarnText),
                StageStatus.Failed => ("✗", Theme.ErrorText),
                _ => ("—", Theme.Gray),
            };
            t.Text = task.Title;
            d.Text = task.Detail ?? "";
        }

        var server = Ctx.Shell.Server;
        _next.Text = _coreReady
            ? "1. Перезапустите Claude Code (или другую подключённую IDE), чтобы она увидела сервер Offload." + Environment.NewLine +
              "2. Попросите: «используй offload, чтобы …» — например, «используй offload, чтобы найти в проекте, где читается конфиг»." + Environment.NewLine +
              "3. Состояние модели, экономию токенов и настройки смотрите в панели управления (двойной щелчок по значку Offload рядом с часами)." +
              (server.State != ServerState.Running ? Environment.NewLine + Environment.NewLine + "Сервер сейчас не запущен: " + (server.LastError ?? Texts.State(server.State)) : "")
            : "Откройте мастер ещё раз (меню значка Offload → «Мастер настройки…») и повторите установку. Подробности — в журнале.";
        _hints.Text = _install.HintsText.Count > 0 ? string.Join(Environment.NewLine, _install.HintsText) : "";
        _hints.Visible = _install.HintsText.Count > 0;
        RaiseNavigationChanged();
    }
}
