using Offload.Core.Config;

namespace Offload.Integrations;

/// <summary>Строка карточки «Claude» на странице «Состояние».</summary>
/// <param name="Id">claude-code или claude-desktop.</param>
/// <param name="Installed">Клиент найден на компьютере.</param>
/// <param name="Status">Статус записи Offload в его настройках.</param>
/// <param name="State">Как подключён и итог последней проверки (null — сведений нет).</param>
/// <param name="Declined">Пользователь отказался от подключения.</param>
public sealed record ClaudeRow(string Id, string Name, bool Installed, IntegrationStatus Status, IntegrationState? State, bool Declined)
{
    /// <summary>Сводное состояние для значка: всё хорошо, требует внимания, не подключён, не установлен.</summary>
    public ClaudeRowKind Kind => !Installed ? ClaudeRowKind.NotInstalled
        : Status is IntegrationStatus.Foreign or IntegrationStatus.Error or IntegrationStatus.Outdated ? ClaudeRowKind.Attention
        : Status == IntegrationStatus.Registered && State is { LastCheckUtc: not null, LastCheckOk: false } ? ClaudeRowKind.Attention
        : Status == IntegrationStatus.Registered ? ClaudeRowKind.Connected
        : ClaudeRowKind.NotConnected;
}

public enum ClaudeRowKind { Connected, Attention, NotConnected, NotInstalled }

/// <summary>Сведения о подключении Claude Code и Claude Desktop для карточки на странице «Состояние» (без записи в файлы).</summary>
public static class ClaudeOverview
{
    public static IReadOnlyList<ClaudeRow> Build(AppConfig cfg, McpServerSpec spec, Func<string, IIdeIntegration?>? find = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(spec);
        find ??= IntegrationRegistry.Find;
        var rows = new List<ClaudeRow>();
        foreach (var id in AutoConnectPolicy.Ids)
        {
            if (find(id) is not { } i) continue;
            bool installed;
            IntegrationStatus status;
            try
            {
                installed = i.IsClientInstalled();
                status = installed ? i.GetStatus(spec) : IntegrationStatus.ClientNotFound;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                installed = true;
                status = IntegrationStatus.Error;
            }
            rows.Add(new ClaudeRow(id, i.DisplayName, installed, status, cfg.IntegrationStates.GetValueOrDefault(id),
                cfg.DeclinedIntegrations.Contains(id)));
        }
        return rows;
    }
}
