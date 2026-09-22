using Offload.Core;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Clients;

/// <summary>Команда и аргументы из записи сервера в конфиге клиента.</summary>
internal sealed record EntryInfo(string? Command, IReadOnlyList<string> Args)
{
    public bool Matches(McpServerSpec spec) =>
        CommandPath.Same(Command, spec.Command) && Args.SequenceEqual(spec.Args, StringComparer.Ordinal);
}

internal enum ProbeState
{
    /// <summary>Файла конфигурации нет.</summary>
    FileMissing,
    /// <summary>Файл есть, записи offload нет.</summary>
    Absent,
    /// <summary>Запись offload указывает на Offload.</summary>
    Ours,
    /// <summary>Запись offload есть, но указывает на другую программу (не трогаем при отключении).</summary>
    Foreign,
    /// <summary>Файл не удалось прочитать/разобрать.</summary>
    Error,
}

internal sealed record FileProbe(string Path, ProbeState State, EntryInfo? Entry = null, string? Error = null)
{
    public IntegrationStatus ToStatus(McpServerSpec spec) => State switch
    {
        ProbeState.Error => IntegrationStatus.Error,
        ProbeState.Ours => Entry!.Matches(spec) ? IntegrationStatus.Registered : IntegrationStatus.Outdated,
        _ => IntegrationStatus.NotRegistered,
    };
}

/// <summary>Общие тексты сообщений и сведение статусов нескольких файлов.</summary>
internal abstract class IntegrationBase : IIdeIntegration
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string? ConfigPath { get; }
    public abstract string? PostRegisterHint { get; }
    public abstract bool IsClientInstalled();
    public abstract IntegrationStatus GetStatus(McpServerSpec spec);
    public abstract Task<IntegrationResult> RegisterAsync(McpServerSpec spec, CancellationToken ct = default);
    public abstract Task<IntegrationResult> UnregisterAsync(CancellationToken ct = default);

    public override string ToString() => Id;

    protected static string ServerName => AppInfo.McpServerId;

    protected IntegrationResult NotFound() => new(false, $"{DisplayName} не найден на этом компьютере.");

    protected string MsgRegistered(string path) => $"{DisplayName}: Offload подключён (файл {path}).";

    protected string MsgAlready(string path) => $"{DisplayName}: Offload уже подключён (файл {path}).";

    protected string MsgUnregistered(string path) => $"{DisplayName}: Offload отключён (файл {path}).";

    protected string MsgNotRegistered => $"{DisplayName}: Offload не был подключён.";

    protected static string MsgParse(string path, string detail) =>
        $"Не удалось разобрать файл {path}: {detail}. Файл не изменён — исправьте ошибку в нём вручную и повторите.";

    protected static string MsgWrite(string path, Exception ex) =>
        $"Не удалось записать файл {path}: {ex.Message}";

    protected static string MsgForeign(string path, string? command) =>
        $"В файле {path} запись «{ServerName}» указывает на другую программу ({command ?? "без команды"}) — она оставлена без изменений.";

    protected static string MsgReplacedForeign(string? command) =>
        $"Прежняя запись «{ServerName}» ({command ?? "без команды"}) заменена, резервная копия сохранена.";

    /// <summary>Несколько файлов → один статус: ошибка важнее всего, частичная регистрация = «требует обновления».</summary>
    protected static IntegrationStatus Aggregate(IReadOnlyList<IntegrationStatus> statuses)
    {
        if (statuses.Count == 0) return IntegrationStatus.NotRegistered;
        if (statuses.Contains(IntegrationStatus.Error)) return IntegrationStatus.Error;
        if (statuses.All(s => s == IntegrationStatus.Registered)) return IntegrationStatus.Registered;
        if (statuses.Any(s => s is IntegrationStatus.Registered or IntegrationStatus.Outdated)) return IntegrationStatus.Outdated;
        return IntegrationStatus.NotRegistered;
    }

    /// <summary>Текст ошибки для исключений правки файлов.</summary>
    protected static string? DescribeFailure(string path, Exception ex) => ex switch
    {
        JsoncParseException p => MsgParse(path, p.Message),
        ConfigReadException r => MsgParse(path, r.Message),
        TomlPatchException t => MsgParse(path, t.Message),
        JsoncEditException e => MsgParse(path, e.Message),
        IOException or UnauthorizedAccessException => MsgWrite(path, ex),
        _ => null,
    };
}
