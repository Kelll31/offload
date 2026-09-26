using System.Text.Json.Nodes;

namespace Offload.Integrations.Clients;

/// <summary>
/// Claude Code внутри дистрибутива WSL: запись в ~/.claude.json пользователя Linux (через \\wsl.localhost\&lt;d&gt;\…),
/// команда — тот же Offload.exe через interop (/mnt/c/…/Offload.exe) с аргументами «--mcp --wsl-distro &lt;d&gt;»: по ним
/// MCP-сервер понимает, что пути клиента — Linux-пути этого дистрибутива. Одна интеграция на дистрибутив
/// (Id <c>claude-code-wsl:&lt;d&gt;</c>); в общий список IntegrationRegistry.All не входит — опрос WSL запускает дистрибутив,
/// поэтому такие интеграции перечисляются только по запросу (<see cref="IntegrationRegistry.WslIntegrations"/>).
/// </summary>
internal sealed class WslClaudeCodeIntegration : JsonIntegration
{
    public const string Kind = "claude-code-wsl";

    /// <summary>Аргумент MCP-сервера с именем дистрибутива (совпадает с Offload.Mcp.Infrastructure.WslPaths.DistroArg).</summary>
    public const string DistroArg = "--wsl-distro";

    private WslClaudeCodeIntegration(string distro)
        : base(Kind + ":" + distro, "Claude Code (WSL: " + distro + ")",
            "Перезапустите Claude Code в WSL или выполните команду /mcp в открытой сессии.") // l10n-key
    {
        Distro = distro;
    }

    public string Distro { get; }

    public static WslClaudeCodeIntegration Create(string distro)
    {
        string? Home() => ClientLocations.WslHome(distro);
        string? Config() => Home() is { } h ? ClientLocations.WslPath(distro, h.TrimEnd('/') + "/.claude.json") : null;
        return new WslClaudeCodeIntegration(distro)
        {
            Detect = () => Home() is { } h
                           && (ClientLocations.Dir(ClientLocations.WslPath(distro, h.TrimEnd('/') + "/.claude"))
                               || File.Exists(ClientLocations.WslPath(distro, h.TrimEnd('/') + "/.claude.json"))),
            Targets = () => Config() is { } c ? [c] : [],
            Container = ["mcpServers"],
            Entry = spec => new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = spec.Command,
                ["args"] = Strings(spec.Args),
                ["env"] = EnvObject(spec),
            },
        };
    }

    /// <summary>
    /// Спецификация для WSL: exe по пути interop и аргумент с дистрибутивом. null — exe нельзя запустить из WSL
    /// (сетевой путь).
    /// </summary>
    internal McpServerSpec? ForWsl(McpServerSpec spec)
    {
        if (ClientLocations.WslInteropPath(spec.Command) is not { } command) return null;
        return spec with { Command = command, Args = [.. spec.Args, DistroArg, Distro] };
    }

    public override IntegrationStatus GetStatus(McpServerSpec spec) =>
        ForWsl(spec) is { } wsl ? base.GetStatus(wsl) : IntegrationStatus.Error;

    public override Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default)
    {
        if (ForWsl(spec) is not { } wsl)
            return Task.FromResult(new IntegrationResult(false,
                L.F("{0}: Offload.exe находится на сетевом пути ({1}) — WSL не сможет его запустить.", DisplayName, spec.Command)));
        return base.RegisterAsync(wsl, ct);
    }

    internal override IReadOnlyList<FileProbe> ProbeEntries(McpServerSpec spec) =>
        ForWsl(spec) is { } wsl ? base.ProbeEntries(wsl) : [];

    // base.FileNeeds снова вызвал бы переопределённый ProbeEntries (двойной перевод пути) — считаем здесь.
    internal override IReadOnlyList<RepairNeed> FileNeeds(McpServerSpec spec) =>
        IsClientInstalled() && ForWsl(spec) is { } wsl ? base.ProbeEntries(wsl).Select(p => p.Need(wsl)).ToList() : [];

    /// <summary>Путь внутри WSL (/mnt/c/…) автоматически не чинится: пользователь переподключает дистрибутив сам.</summary>
    internal override Task<IntegrationResult?> RepairPathAsync(McpServerSpec spec, CancellationToken ct = default) =>
        Task.FromResult<IntegrationResult?>(null);
}
