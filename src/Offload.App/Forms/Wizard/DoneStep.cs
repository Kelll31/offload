using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Integrations;
using Offload.Llama;

namespace Offload.App.Forms.Wizard;

/// <summary>Шаг 7: итоги и что делать дальше. Отмечает настройку завершённой, если установлены llama.cpp и модель.</summary>
internal sealed class DoneStep : WizardStep
{
    private readonly InstallStep _install;
    private readonly Label _headline = Kit.Label("", Theme.Semibold(11f));
    private readonly TableLayoutPanel _summary = Kit.Table(0, 0, 100);
    private readonly SectionHeader _ideSection = Kit.Section(L.T("Подключено"));
    private readonly TableLayoutPanel _ideTable = Kit.Table(0, 0, 100);
    private readonly Label _restart = Kit.Hint("");
    private readonly List<(Label Glyph, Label Name, Label Detail)> _ideRows = [];
    private readonly Label _next = Kit.Wrap("");
    private readonly Label _hints = Kit.Hint("");
    private readonly CheckBox _openMain = Kit.Check(L.T("Открыть панель управления"), true);
    private readonly OnboardingCard _onboarding;
    private readonly List<(Label Glyph, Label Title, Label Detail)> _rows = [];
    private const int MaxIdeRows = 24;
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
        // Итог подключения к IDE: у каждой — результат проверки запуском (строки заранее, чтобы масштабировались с формой).
        root.AddRow(_ideSection);
        root.AddRow(_ideTable);
        for (var i = 0; i < MaxIdeRows; i++)
        {
            var g = Kit.Label("", Theme.Semibold(10f));
            g.Margin = new Padding(0, 2, 8, 2);
            var n = Kit.Label("", Theme.Semibold(9f));
            n.Margin = new Padding(0, 3, 12, 3);
            var d = Kit.Wrap("", color: Theme.TextMuted);
            d.Margin = new Padding(0, 3, 0, 3);
            _ideTable.AddRow(g, n, d);
            _ideRows.Add((g, n, d));
        }
        root.AddRow(_restart);
        root.AddRow(Kit.Section(L.T("Что дальше")));
        root.AddRow(_next);
        // Проверка «IDE видит Offload»: ожидание первого вызова и тестовый запрос к модели.
        _onboarding = new OnboardingCard(ctx.Shell);
        _onboarding.Card.Margin = new Padding(0, 10, 0, 4);
        _onboarding.Card.Visible = false;
        root.AddRow(_onboarding.Card);
        root.AddRow(_hints);
        root.AddRow(Kit.Spacer(8));
        root.AddRow(_openMain);
        SetContent(root);
    }

    public override string Title => L.T("Готово");

    public override string Heading => _coreReady ? L.T("Готово!") : L.T("Настройка не завершена");

    public override string? Subtitle => _coreReady
        ? L.T("Offload установлен и работает в области уведомлений.")
        : L.T("Основные компоненты не установлены — мастер можно запустить снова из меню значка Offload.");

    public override string NextText => L.T("Готово");

    public override bool CanGoBack => false;

    /// <summary>Открыть панель управления после закрытия мастера.</summary>
    public bool OpenMainWindow => _openMain.Checked;

    /// <summary>Блок «Подключено»: IDE — ✓ проверено (N инструментов, время) либо ⚠ причина и что делать; одна общая подсказка о перезапуске.</summary>
    private void ShowIdeChecks()
    {
        var checks = _install.IdeChecks;
        _ideSection.Visible = _ideTable.Visible = _restart.Visible = checks.Count > 0;
        for (var i = 0; i < _ideRows.Count; i++)
        {
            var (g, n, d) = _ideRows[i];
            var visible = i < checks.Count;
            g.Visible = n.Visible = d.Visible = visible;
            if (!visible) continue;
            var c = checks[i];
            (g.Text, g.ForeColor) = c.Ok ? ("✓", Theme.OkText) : ("⚠", Theme.WarnText);
            n.Text = c.Name;
            d.Text = c.Ok || c.Hint.Length == 0 ? c.Text : $"{c.Text}. {c.Hint}";
        }
        _restart.Text = RestartText(checks.Select(c => c.Id).ToList());
    }

    /// <summary>Общая подсказка: как заставить подключённые программы увидеть Offload (Claude — с конкретными действиями).</summary>
    internal static string RestartText(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return "";
        var parts = new List<string>();
        if (ids.Contains("claude-code")) parts.Add(L.T("Claude Code (команда /mcp в открытой сессии)"));
        if (ids.Contains("claude-desktop")) parts.Add(L.T("Claude Desktop (полный выход через значок в трее и новый запуск)"));
        var others = ids.Where(i => i is not ("claude-code" or "claude-desktop"))
            .Select(i => IntegrationRegistry.Find(i)?.DisplayName ?? i).ToList();
        if (others.Count > 0) parts.Add(string.Join(", ", others));
        return L.F("Чтобы программы увидели Offload, перезапустите: {0}.", string.Join("; ", parts));
    }

    public override void OnEnter()
    {
        var cfg = ConfigStore.Reload();
        var llama = Ui.Try(() => LlamaInstaller.IsInstalled(cfg), false, "IsInstalled");
        var model = cfg.ActiveModel();
        _coreReady = llama && model is not null && File.Exists(model.FilePath);
        if (_coreReady && !cfg.SetupCompleted)
        {
            Ui.RunSafe(Ctx.Form, () => ConfigStore.Update(c => c.SetupCompleted = true), L.T("Не удалось сохранить настройки"));
            Log.Info("wizard", "Первоначальная настройка завершена");
        }
        Ctx.Shell.ConfigChanged();

        var tasks = _install.Tasks;
        var warnings = tasks.Count(t => t.Status is StageStatus.Warning or StageStatus.Failed);
        _headline.Text = !_coreReady ? L.T("Не установлены llama.cpp или модель.")
            : warnings > 0 ? L.T("Установка завершена, но есть предупреждения.")
            : L.T("Всё установлено и проверено.");
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

        ShowIdeChecks();

        var server = Ctx.Shell.Server;
        _next.Text = _coreReady
            ? L.F("1. Перезапустите Claude Code (или другую подключённую IDE), чтобы она увидела сервер Offload.{0}2. Попросите: «используй offload, чтобы …» — например, «используй offload, чтобы найти в проекте, где читается конфиг».{0}3. Состояние модели, экономию токенов и настройки смотрите в панели управления (двойной щелчок по значку Offload рядом с часами).",
                  Environment.NewLine) +
              (server.State != ServerState.Running
                  ? L.F("{0}{0}Сервер сейчас не запущен: {1}", Environment.NewLine, server.LastError ?? Texts.State(server.State))
                  : "")
            : L.T("Откройте мастер ещё раз (меню значка Offload → «Мастер настройки…») и повторите установку. Подробности — в журнале.");
        _hints.Text = _install.HintsText.Count > 0 ? string.Join(Environment.NewLine, _install.HintsText) : "";
        _hints.Visible = _install.HintsText.Count > 0;
        _onboarding.Card.Visible = _coreReady && (_onboarding.Connected || !OnboardingCard.HasUsageHistory());
        RaiseNavigationChanged();
    }
}
