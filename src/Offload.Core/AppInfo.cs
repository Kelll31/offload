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

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string UserAgent => $"{Name}/{Version} (+{RepositoryUrl})";
}
