using System.Text.Json.Nodes;
using Offload.Core;
using static Offload.Integrations.Clients.JsonIntegration;

namespace Offload.Integrations.Clients;

/// <summary>Описания всех поддерживаемых клиентов. Пути вычисляются при каждом обращении (важно для песочницы тестов).</summary>
internal static class KnownIntegrations
{
    /// <summary>Таймаут вызова в секундах для Cline/Roo (максимум, который они принимают).</summary>
    public const int ClineTimeoutSec = 3600;

    /// <summary>Zed: таймаут stdio-сервера в секундах.</summary>
    public const int ZedTimeoutSec = 1800;

    /// <summary>Gemini CLI: таймаут в миллисекундах (30 минут).</summary>
    public const int GeminiTimeoutMs = 1_800_000;

    private static string Profile => IntegrationEnvironment.UserProfile;
    private static string AppData => IntegrationEnvironment.AppData;

    private static JsonArray ReadOnlyTools() => Strings(McpToolNames.ReadOnly);

    private static JsonObject Basic(McpServerSpec spec, string? type = null, bool env = false)
    {
        var o = new JsonObject();
        if (type is not null) o["type"] = type;
        o["command"] = spec.Command;
        o["args"] = Strings(spec.Args);
        if (env || spec.Env.Count > 0) o["env"] = EnvObject(spec);
        return o;
    }

    private static JsonObject With(this JsonObject o, string key, JsonNode? value)
    {
        o[key] = value;
        return o;
    }

    public static readonly IReadOnlyList<IIdeIntegration> All = Build();

    private static List<IIdeIntegration> Build() =>
    [
        ClaudeCodeIntegration.Create(),

        new JsonIntegration("claude-desktop", "Claude Desktop",
            "Полностью закройте Claude Desktop (значок в трее → «Выход») и запустите снова.") // l10n-key
        {
            Detect = ClientLocations.ClaudeDesktopInstalled,
            Targets = () => [ClientLocations.ClaudeDesktopConfig],
            KnownFiles = ClientLocations.AllClaudeDesktopConfigs,
            Container = ["mcpServers"],
            Entry = spec => Basic(spec, env: true),
        },

        new JsonIntegration("cursor", "Cursor", "Перезапустите Cursor.") // l10n-key
        {
            Detect = ClientLocations.CursorInstalled,
            Targets = () => [Path.Combine(Profile, ".cursor", "mcp.json")],
            Container = ["mcpServers"],
            Entry = spec => Basic(spec, "stdio"),
        },

        new JsonIntegration("vscode", "VS Code (GitHub Copilot)",
            "Откройте чат Copilot в режиме агента; при первом запуске сервера подтвердите доверие к нему.") // l10n-key
        {
            Detect = () => ClientLocations.Dir(ClientLocations.VsCodeUser(false)),
            Targets = () => [Path.Combine(ClientLocations.VsCodeUser(false), "mcp.json")],
            Container = ["servers"],
            Entry = spec => Basic(spec, "stdio"),
        },

        new JsonIntegration("vscode-insiders", "VS Code Insiders (GitHub Copilot)",
            "Откройте чат Copilot в режиме агента; при первом запуске сервера подтвердите доверие к нему.") // l10n-key
        {
            Detect = () => ClientLocations.Dir(ClientLocations.VsCodeUser(true)),
            Targets = () => [Path.Combine(ClientLocations.VsCodeUser(true), "mcp.json")],
            Container = ["servers"],
            Entry = spec => Basic(spec, "stdio"),
        },

        new JsonIntegration("copilot-cli", "GitHub Copilot CLI",
            "Изменения применяются сразу; проверьте список серверов командой /mcp в Copilot CLI.") // l10n-key
        {
            Detect = () => ClientLocations.Dir(ClientLocations.CopilotHome),
            Targets = () => [Path.Combine(ClientLocations.CopilotHome, "mcp-config.json")],
            Container = ["mcpServers"],
            Entry = spec => Basic(spec, "local").With("tools", Strings(["*"])),
        },

        new JsonIntegration("windsurf", "Windsurf / Devin Desktop",
            "Нажмите «Обновить» (Refresh) в панели MCP или перезапустите Windsurf / Devin Desktop.") // l10n-key
        {
            Detect = () => ClientLocations.WindsurfTargets().Count > 0,
            Targets = ClientLocations.WindsurfTargets,
            Container = ["mcpServers"],
            Entry = spec => Basic(spec),
            ExtraRegisterNote = () =>
                Registry.Find("claude-code")?.GetStatus(McpServerSpec.ForCurrentExecutable()) is IntegrationStatus.Registered or IntegrationStatus.Outdated
                    ? L.T("Devin также импортирует серверы из конфигурации Claude Code — если Offload появится дважды, отключите импорт (read_config_from в %APPDATA%\\devin\\config.json).")
                    : null,
        },

        new JsonIntegration("cline", "Cline", "Cline подхватит изменения автоматически.") // l10n-key
        {
            Detect = ClientLocations.ClineInstalled,
            Targets = () => [ClientLocations.ClineUsesV4() ? ClientLocations.ClineV4Config : ClientLocations.ClineLegacyConfig],
            KnownFiles = () => [ClientLocations.ClineV4Config, ClientLocations.ClineLegacyConfig],
            Container = ["mcpServers"],
            Entry = spec => Basic(spec, "stdio")
                .With("disabled", false)
                .With("timeout", ClineTimeoutSec)
                .With("autoApprove", ReadOnlyTools()),
            DroppedKeys = ["transport"],
        },

        new JsonIntegration("roo-code", "Roo Code", "Roo Code подхватит изменения автоматически.") // l10n-key
        {
            Detect = ClientLocations.RooInstalled,
            Targets = () => [ClientLocations.RooConfig],
            Container = ["mcpServers"],
            Entry = spec => Basic(spec)
                .With("disabled", false)
                .With("timeout", ClineTimeoutSec)
                .With("alwaysAllow", ReadOnlyTools()),
        },

        new JsonIntegration("kilo-code", "Kilo Code", "Перезапустите Kilo Code.") // l10n-key
        {
            Detect = () => ClientLocations.Dir(ClientLocations.KiloDir),
            Targets = ClientLocations.KiloTargets,
            KnownFiles = ClientLocations.KiloAllFiles,
            Container = ["mcp"],
            Shape = EntryShape.CommandArray,
            OwnedKeys = ["type", "command"],
            Entry = spec =>
            {
                var o = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = Strings([spec.Command, .. spec.Args]),
                    ["enabled"] = true,
                };
                if (spec.Env.Count > 0) o["environment"] = EnvObject(spec);
                return o;
            },
        },

        new CodexIntegration(),

        new JsonIntegration("gemini-cli", "Gemini CLI",
            "Перезапустите Gemini CLI. MCP-серверы подключаются только в доверенных папках (trusted folders).") // l10n-key
        {
            Detect = () => ClientLocations.Dir(Path.Combine(Profile, ".gemini")),
            Targets = () => [Path.Combine(Profile, ".gemini", "settings.json")],
            Container = ["mcpServers"],
            Entry = spec => Basic(spec)
                .With("timeout", GeminiTimeoutMs)
                .With("trust", false),
        },

        new JsonIntegration("zed", "Zed", "Zed применит настройки автоматически; если сервер не появился — перезапустите Zed.") // l10n-key
        {
            Detect = ClientLocations.ZedInstalled,
            Targets = () => [Path.Combine(AppData, "Zed", "settings.json")],
            Container = ["context_servers"],
            Entry = spec => Basic(spec, env: true).With("timeout", ZedTimeoutSec),
        },

        new JsonIntegration("visual-studio", "Visual Studio 2022/2026",
            "Инструменты по умолчанию выключены — включите их в окне Tools (выбор инструментов) чата Copilot. Нужна версия 17.14 или новее.") // l10n-key
        {
            Detect = ClientLocations.VisualStudioInstalled,
            // Никогда не пишем сюда "mcpServers": этот же файл Claude Code читает как проектный .mcp.json.
            Targets = () => [Path.Combine(Profile, ".mcp.json")],
            Container = ["servers"],
            Entry = spec => Basic(spec, "stdio"),
        },

        new JsonIntegration("junie", "JetBrains Junie", "Перезапустите IDE JetBrains (или сессию Junie CLI).") // l10n-key
        {
            Detect = () => ClientLocations.Dir(Path.Combine(Profile, ".junie")),
            Targets = () => [Path.Combine(Profile, ".junie", "mcp", "mcp.json")],
            Container = ["mcpServers"],
            Entry = spec => Basic(spec, env: true),
        },

        new JetBrainsAiIntegration(),

        new ContinueIntegration(),

        new JsonIntegration("kiro", "Kiro", "Kiro применит изменения сразу после сохранения файла.") // l10n-key
        {
            Detect = () => ClientLocations.Dir(Path.Combine(Profile, ".kiro")),
            Targets = () => [Path.Combine(Profile, ".kiro", "settings", "mcp.json")],
            Container = ["mcpServers"],
            Entry = spec => Basic(spec)
                .With("disabled", false)
                .With("autoApprove", ReadOnlyTools()),
        },

        new JsonIntegration("trae", "Trae", "Перезапустите Trae.") // l10n-key
        {
            Detect = () => ClientLocations.TraeTargets().Count > 0,
            Targets = ClientLocations.TraeTargets,
            Container = ["mcpServers"],
            Entry = spec => Basic(spec),
        },

        QoderIntegration.Create(),
    ];

    /// <summary>Поиск без обращения к IntegrationRegistry (избегаем циклической инициализации).</summary>
    private static class Registry
    {
        public static IIdeIntegration? Find(string id) => All.FirstOrDefault(i => i.Id == id);
    }
}
