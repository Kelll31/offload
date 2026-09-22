using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Logging;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «Промпт»: дополнительные правила для локальной модели, разрешённые проверочные команды, лимиты MCP и цены.</summary>
internal sealed class PromptPage : PageBase
{
    private readonly ComboBox _preset = Kit.Combo(200);
    private readonly TextBox _prompt = Kit.MultiLine(200);
    private readonly TextBox _allowlist = Kit.MultiLine(150, mono: true);
    private readonly NumericUpDown _maxFileKb = Kit.Number(16, 65536, 512, 100);
    private readonly NumericUpDown _maxResponse = Kit.Number(1000, 500000, 12000, 100, increment: 1000);
    private readonly CheckBox _restrictWrites = Kit.Check("Разрешать запись файлов только внутри рабочей папки IDE");
    private readonly NumericUpDown _priceIn = Kit.Number(0, 1000, 3, 90, decimals: 2, increment: 0.5m);
    private readonly NumericUpDown _priceOut = Kit.Number(0, 1000, 15, 90, decimals: 2, increment: 0.5m);
    private readonly Button _save;
    private readonly Label _saveStatus = Kit.Hint("", autoWidth: true);
    private readonly IReadOnlyList<Presets.Preset> _presets = Presets.All();

    private bool _loading;
    private bool _dirty;

    public PromptPage(IAppShell shell) : base(shell)
    {
        foreach (var p in _presets) _preset.Items.Add(p);
        _save = Kit.Primary("Сохранить", (_, _) => Save());

        var root = Kit.Table();
        root.AddRow(Kit.Section("Дополнительные правила для локальной модели", first: true));
        root.AddRow(Kit.Hint(
            "Этот текст добавляется к системному промпту локальной модели во всех задачах Offload: соглашения проекта, " +
            "стиль кода, язык комментариев. Пишите кратко — длинный промпт занимает контекст модели."));
        var presetRow = Kit.Flow(Kit.Label("Пресет:"), _preset, Kit.Button("Вставить", (_, _) => ApplyPreset()));
        root.AddRow(presetRow);
        root.AddRow(_prompt);

        root.AddRow(Kit.Section("Разрешённые проверочные команды"));
        root.AddRow(Kit.Hint(
            "Команды, которыми локальная модель может проверить свою правку (параметр verify_command: сборка, тесты, линтер). " +
            "По одному шаблону в строке; «*» в конце означает «любые аргументы». Команды с операторами оболочки (&&, |, ;, >) " +
            "отклоняются всегда."));
        root.AddRow(_allowlist);
        root.AddRow(Kit.Flow(Kit.Button("Список по умолчанию", (_, _) => ResetAllowlist(), 150)));

        root.AddRow(Kit.Section("Ограничения MCP"));
        var limits = Kit.Grid();
        limits.AddField("Максимальный размер файла:", _maxFileKb, "КБ — файлы больше не читаются");
        limits.AddField("Максимальная длина ответа:", _maxResponse, "символов — чтобы не расходовать токены IDE");
        root.AddRow(limits);
        root.AddRow(_restrictWrites);

        root.AddRow(Kit.Section("Цены облачной модели (для оценки экономии)"));
        var prices = Kit.Grid();
        prices.AddField("Входные токены:", _priceIn, "$ за 1 млн токенов");
        prices.AddField("Выходные токены:", _priceOut, "$ за 1 млн токенов");
        root.AddRow(prices);
        root.AddRow(Kit.Hint("Используются только для приблизительной оценки на вкладке «Состояние». По умолчанию — цены Claude Sonnet."));

        var saveRow = Kit.Flow(_save, Kit.Button("Отменить изменения", (_, _) => LoadFromConfig(), 150), _saveStatus);
        saveRow.Margin = new Padding(0, 10, 0, 4);
        root.AddRow(saveRow);

        Controls.Add(Kit.Scroll(root));

        _prompt.TextChanged += (_, _) => MarkDirty();
        _allowlist.TextChanged += (_, _) => MarkDirty();
        _maxFileKb.ValueChanged += (_, _) => MarkDirty();
        _maxResponse.ValueChanged += (_, _) => MarkDirty();
        _restrictWrites.CheckedChanged += (_, _) => MarkDirty();
        _priceIn.ValueChanged += (_, _) => MarkDirty();
        _priceOut.ValueChanged += (_, _) => MarkDirty();

        LoadFromConfig();
    }

    public override string Key => Tabs.Prompt;

    public override string Title => "Промпт";

    protected override void OnActivated()
    {
        if (!_dirty) LoadFromConfig();
    }

    public override void OnConfigChanged()
    {
        if (!_dirty) LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            var m = ConfigStore.Current.Mcp;
            _prompt.Text = Normalize(m.ExtraSystemPrompt);
            _allowlist.Text = string.Join(Environment.NewLine, m.VerifyCommandAllowlist ?? []);
            _maxFileKb.Value = Math.Clamp(m.MaxFileBytes / 1024, (int)_maxFileKb.Minimum, (int)_maxFileKb.Maximum);
            _maxResponse.Value = Math.Clamp(m.MaxResponseChars, (int)_maxResponse.Minimum, (int)_maxResponse.Maximum);
            _restrictWrites.Checked = m.RestrictWritesToWorkspace;
            _priceIn.Value = Math.Clamp((decimal)m.CloudInputPricePerMTok, _priceIn.Minimum, _priceIn.Maximum);
            _priceOut.Value = Math.Clamp((decimal)m.CloudOutputPricePerMTok, _priceOut.Minimum, _priceOut.Maximum);
            _preset.SelectedIndex = DetectPreset();
        }
        finally
        {
            _loading = false;
        }
        _dirty = false;
        _saveStatus.Text = "";
    }

    private static string Normalize(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    /// <summary>Какой пресет совпадает с текущим текстом (0 — «Нет»).</summary>
    private int DetectPreset()
    {
        var text = _prompt.Text.Trim();
        for (var i = 1; i < _presets.Count; i++)
        {
            var body = Ui.Try(() => Presets.Load(_presets[i]), null, "Presets.Load");
            if (body is not null && text.Contains(body.Trim(), StringComparison.Ordinal)) return i;
        }
        return 0;
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        _saveStatus.ForeColor = Theme.WarnText;
        _saveStatus.Text = "Есть несохранённые изменения";
    }

    private void ApplyPreset()
    {
        if (_preset.SelectedItem is not Presets.Preset p) return;
        if (p.ResourceName is null)
        {
            if (_prompt.TextLength > 0 && Ui.Confirm(Owner, "Очистить дополнительные правила?")) _prompt.Clear();
            return;
        }
        var body = Ui.Try(() => Presets.Load(p), null, "Presets.Load");
        if (string.IsNullOrWhiteSpace(body))
        {
            Ui.Warn(Owner, $"Пресет «{p.Title}» не найден в этой сборке программы.");
            return;
        }
        if (_prompt.Text.Contains(body, StringComparison.Ordinal))
        {
            Ui.Info(Owner, $"Пресет «{p.Title}» уже добавлен.");
            return;
        }
        if (_prompt.Text.Trim().Length == 0)
        {
            _prompt.Text = body;
            return;
        }
        var answer = Ui.Show(Owner,
            $"Заменить текущие правила пресетом «{p.Title}»?{Environment.NewLine}{Environment.NewLine}" +
            "«Да» — заменить, «Нет» — добавить в конец, «Отмена» — ничего не менять.",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) _prompt.Text = body;
        else if (answer == DialogResult.No) _prompt.Text = _prompt.Text.TrimEnd() + Environment.NewLine + Environment.NewLine + body;
    }

    private void ResetAllowlist()
    {
        _allowlist.Text = string.Join(Environment.NewLine, new McpSettings().VerifyCommandAllowlist);
    }

    private static List<string> ParseAllowlist(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly string[] ShellOperators = ["&&", "||", "|", ";", ">", "<", "`", "$("];

    private void Save()
    {
        var allow = ParseAllowlist(_allowlist.Text);
        var bad = allow.Where(a => ShellOperators.Any(op => a.Contains(op, StringComparison.Ordinal))).ToList();
        if (bad.Count > 0)
        {
            Ui.Warn(Owner, "Шаблоны с операторами оболочки не допускаются — такие команды всё равно будут отклонены:" +
                           Environment.NewLine + string.Join(Environment.NewLine, bad.Select(b => "• " + b)));
            return;
        }
        var ok = Ui.RunSafe(Owner, () => ConfigStore.Update(c =>
        {
            var m = c.Mcp;
            m.ExtraSystemPrompt = _prompt.Text.Replace("\r\n", "\n").Trim();
            m.VerifyCommandAllowlist = allow;
            m.MaxFileBytes = (int)_maxFileKb.Value * 1024;
            // Суммарный лимит должен быть не меньше лимита одного файла.
            if (m.MaxTotalBytes < m.MaxFileBytes) m.MaxTotalBytes = m.MaxFileBytes;
            m.MaxResponseChars = (int)_maxResponse.Value;
            m.RestrictWritesToWorkspace = _restrictWrites.Checked;
            m.CloudInputPricePerMTok = (double)_priceIn.Value;
            m.CloudOutputPricePerMTok = (double)_priceOut.Value;
        }), "Не удалось сохранить настройки");
        if (!ok) return;
        Log.Info("ui", "Настройки промпта и MCP сохранены");
        _dirty = false;
        _saveStatus.ForeColor = Theme.OkText;
        _saveStatus.Text = "Сохранено. Новые правила применяются к следующим запросам из IDE.";
        Shell.ConfigChanged();
        LoadFromConfig();
        _saveStatus.ForeColor = Theme.OkText;
        _saveStatus.Text = "Сохранено. Новые правила применяются к следующим запросам из IDE.";
    }
}
