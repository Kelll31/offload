using Offload.Llama;

namespace Offload.App.Services;

/// <summary>Ключи вкладок панели управления.</summary>
internal static class Tabs
{
    public const string Status = "status";
    public const string Models = "models";
    public const string Server = "server";
    public const string Integrations = "integrations";
    public const string OpenCode = "opencode";
    public const string Prompt = "prompt";
    public const string Log = "log";
    public const string About = "about";
}

/// <summary>Операции трей-приложения, доступные окнам (реализует TrayApplicationContext).</summary>
internal interface IAppShell
{
    ServerController Server { get; }

    HardwareCache Hardware { get; }

    /// <summary>Найденное при запуске обновление llama.cpp (null — нет или не проверялось).</summary>
    LlamaUpdateInfo? PendingLlamaUpdate { get; set; }

    /// <summary>Открыть (активировать) панель управления на вкладке (см. <see cref="Tabs"/>).</summary>
    void ShowMainWindow(string? tab = null);

    void ShowSetupWizard();

    /// <summary>Всплывающее уведомление трея. force — показать, даже если уведомления выключены (ответ на действие пользователя).</summary>
    void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, bool force = false);

    /// <summary>Выполнить действие в потоке интерфейса.</summary>
    void PostToUi(Action action);

    /// <summary>Сообщить, что конфигурация изменилась (обновить меню, значок, окна).</summary>
    void ConfigChanged();

    /// <summary>Сменить активную модель и перезапустить сервер, если он работал.</summary>
    Task SwitchModelAsync(string modelId, IWin32Window? owner = null);

    /// <summary>Включить/выключить автозапуск с Windows (реестр + Ui.StartWithWindows).</summary>
    void SetAutostart(bool enabled, IWin32Window? owner = null);

    Task ExitAsync();
}
