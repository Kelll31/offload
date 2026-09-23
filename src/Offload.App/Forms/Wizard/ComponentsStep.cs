using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Util;
using Offload.Llama;
using Offload.OpenCode;

namespace Offload.App.Forms.Wizard;

/// <summary>Шаг 4: дополнительные компоненты (OpenCode, Visual C++ Redistributable) и сводка установки.</summary>
internal sealed class ComponentsStep : WizardStep
{
    private readonly CheckBox _openCode = Kit.Check(L.T("Установить OpenCode (рекомендуется)"));
    private readonly Label _openCodeState = Kit.Hint("");
    private readonly Label _vc = Kit.Wrap("");
    private readonly Label _summary = Kit.Wrap("");
    private bool _initialized;

    public ComponentsStep(WizardContext ctx) : base(ctx)
    {
        _openCode.CheckedChanged += (_, _) =>
        {
            State.InstallOpenCode = _openCode.Checked;
            UpdateSummary();
        };

        var root = Kit.Table();
        root.AddRow(Kit.Section("OpenCode", first: true));
        root.AddRow(_openCode);
        var ocHint = Kit.Hint(L.T(
            "OpenCode — агент для программирования. С ним локальная модель может выполнять многошаговые задачи: прочитать нужные файлы, согласованно поправить несколько из них и проверить результат сборкой или тестами. Без OpenCode правки выполняются только прямой перезаписью файлов."));
        ocHint.Margin = new Padding(20, 0, 0, 4);
        root.AddRow(ocHint);
        _openCodeState.Margin = new Padding(20, 0, 0, 6);
        root.AddRow(_openCodeState);

        root.AddRow(Kit.Section("Microsoft Visual C++ Redistributable"));
        root.AddRow(_vc);

        root.AddRow(Kit.Section(L.T("Будет выполнено")));
        root.AddRow(_summary);
        SetContent(root);
    }

    public override string Title => L.T("Компоненты");

    public override string Heading => L.T("Дополнительные компоненты");

    public override string? Subtitle => L.T("Проверьте, что будет установлено.");

    public override void OnEnter()
    {
        var cfg = ConfigStore.Current;
        if (!_initialized)
        {
            _initialized = true;
            State.InstallOpenCode = !cfg.SetupCompleted || cfg.OpenCode.Enabled;
            _openCode.Checked = State.InstallOpenCode;
        }
        var exe = Ui.Try(() => OpenCodeInstaller.FindExecutable(cfg), null, "FindExecutable");
        _openCodeState.Text = exe is null ? ""
            : L.F("Уже установлен{0}: {1}", cfg.OpenCode.InstalledVersion is { } v ? L.F(" (версия {0})", v) : "", exe);

        var vc = Ui.Try<bool?>(() => VcRuntime.IsInstalled(), null, "VcRuntime.IsInstalled");
        (_vc.Text, _vc.ForeColor) = vc switch
        {
            true => (L.T("✓ Установлен — ничего делать не нужно."), Theme.OkText),
            false => (L.T("Не найден — будет установлен автоматически. Windows попросит подтвердить установку (права администратора)."), Theme.WarnText),
            _ => (L.T("Будет проверен во время установки; при необходимости Windows попросит подтвердить установку."), Theme.TextMuted),
        };
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var cfg = ConfigStore.Current;
        var lines = new List<string>();
        var llamaInstalled = Ui.Try(() => LlamaInstaller.IsInstalled(cfg), false, "IsInstalled")
                             && cfg.Llama.InstalledBackend == State.Backend;
        lines.Add(llamaInstalled
            ? L.F("• llama.cpp ({0}) — уже установлена", Texts.Backend(State.Backend))
            : L.F("• llama.cpp — сборка «{0}», последняя версия", Texts.Backend(State.Backend)));
        if (State.Model is { } m)
        {
            var size = m.Catalog is { } c && !m.IsInstalled ? ModelListBinder.RequiredBytes(c, State.Quant) : 0;
            lines.Add(m.IsInstalled
                ? L.F("• Модель «{0}» — уже установлена", m.Name)
                : L.F("• Модель «{0}»{1}{2} → {3}", m.Name, State.Quant is { } q ? $" ({q})" : "",
                    size > 0 ? $", ≈{FileUtil.FormatBytes(size)}" : "", State.ModelsDir));
        }
        lines.Add(State.InstallOpenCode ? "• OpenCode" : L.T("• OpenCode — не устанавливается"));
        lines.Add(L.T("• Запуск сервера и проверка ответа модели"));
        lines.Add(L.T("• Подключение к выбранным IDE (следующий шаг)"));
        _summary.Text = string.Join(Environment.NewLine, lines);
    }
}
