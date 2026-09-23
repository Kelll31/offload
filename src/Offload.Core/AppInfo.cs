using System.Reflection;

namespace Offload.Core;

public static class AppInfo
{
    public const string Name = "Offload";
    public const string DisplayName = "Offload";

    /// <summary>Идентификатор MCP-сервера в конфигурациях IDE (mcp__offload__*).</summary>
    public const string McpServerId = "offload";

    /// <summary>Аргумент командной строки, переводящий exe в режим stdio MCP-сервера.</summary>
    public const string McpArg = "--mcp";

    public const string RepositoryUrl = "https://github.com/Kelll31/offload";

    /// <summary>
    /// AppId установщика Inno Setup (installer/Offload.iss). По нему программа находит установленную копию;
    /// менять нельзя — иначе новая версия поставится второй программой, а не обновлением.
    /// </summary>
    public const string InstallerAppId = "6C1B7E2A-4F0D-4B8E-9C35-2B1F4E7A9D10";

    /// <summary>Ключ удаления программы: в HKCU при установке «для себя», в HKLM — «для всех пользователей».</summary>
    public const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + InstallerAppId + "}_is1";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string UserAgent => $"{Name}/{Version} (+{RepositoryUrl})";
}
