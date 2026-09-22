using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.App.Forms.Wizard;

/// <summary>Выбор пользователя в мастере (заполняется из текущей конфигурации при повторном запуске).</summary>
internal sealed class WizardState
{
    public HardwareInfo? Hardware { get; set; }

    /// <summary>Выбранная сборка llama.cpp (конкретная, не Auto).</summary>
    public LlamaBackend Backend { get; set; } = LlamaBackend.Cpu;

    public LlamaBackend RecommendedBackend { get; set; } = LlamaBackend.Cpu;

    /// <summary>Выбранная модель (каталожная или уже установленная).</summary>
    public ModelRow? Model { get; set; }

    public string? Quant { get; set; }

    public string ModelsDir { get; set; } = "";

    public bool InstallOpenCode { get; set; } = true;

    /// <summary>Идентификаторы IDE, которые нужно подключить.</summary>
    public HashSet<string> Ides { get; } = [];

    /// <summary>Список IDE уже загружен (иначе шаг IDE ещё не открывался и выбор берётся из конфигурации).</summary>
    public bool IdesLoaded { get; set; }

    public bool ClaudeGuidance { get; set; } = true;

    public bool PreapproveReadTools { get; set; } = true;

    public bool Autostart { get; set; } = true;
}

/// <summary>Общее окружение шагов мастера.</summary>
internal sealed class WizardContext(IAppShell shell, WizardState state, Form form)
{
    public IAppShell Shell { get; } = shell;
    public WizardState State { get; } = state;
    public Form Form { get; } = form;
}

/// <summary>Шаг мастера настройки (панель с содержимым).</summary>
internal abstract class WizardStep : UserControl
{
    protected WizardStep(WizardContext ctx)
    {
        Ctx = ctx;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = Theme.Surface;
        AutoScaleMode = AutoScaleMode.Inherit;
        Visible = false;
    }

    protected WizardContext Ctx { get; }

    protected WizardState State => Ctx.State;

    /// <summary>Короткое название для списка шагов слева.</summary>
    public abstract string Title { get; }

    /// <summary>Заголовок над содержимым.</summary>
    public abstract string Heading { get; }

    public virtual string? Subtitle => null;

    public virtual string NextText => "Далее";

    public virtual bool CanGoNext => true;

    public virtual bool CanGoBack => true;

    /// <summary>Выполняется операция, которую нужно прервать перед закрытием мастера.</summary>
    public virtual bool IsRunning => false;

    /// <summary>Изменились CanGoNext/CanGoBack/NextText.</summary>
    public event EventHandler? NavigationChanged;

    protected void RaiseNavigationChanged() => NavigationChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Шаг показан (при каждом входе).</summary>
    public virtual void OnEnter() { }

    /// <summary>Уход со шага. false — остаться (например, ошибка проверки).</summary>
    public virtual bool OnLeave(bool forward) => true;

    /// <summary>Прервать выполняющуюся операцию.</summary>
    public virtual void CancelRunning() { }

    /// <summary>Прокручиваемое содержимое шага.</summary>
    protected void SetContent(TableLayoutPanel content)
    {
        Controls.Add(Kit.Scroll(content, new Padding(24, 8, 24, 8)));
    }
}
