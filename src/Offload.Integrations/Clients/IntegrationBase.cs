using Offload.Core;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Clients;

/// <summary>
/// Команда и аргументы из записи сервера в конфиге клиента. <paramref name="StaleDefaults"/> — в записи остались
/// значения по умолчанию, записанные прежней версией Offload (таймауты, список автоодобрения), — запись «устарела».
/// </summary>
internal sealed record EntryInfo(string? Command, IReadOnlyList<string> Args, bool StaleDefaults = false)
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

internal sealed record FileProbe(
    string Path, ProbeState State, EntryInfo? Entry = null, string? Error = null, FailureKind ErrorKind = FailureKind.ConfigInvalid)
{
    public IntegrationStatus ToStatus(McpServerSpec spec) => State switch
    {
        ProbeState.Error => IntegrationStatus.Error,
        ProbeState.Ours => Entry!.Matches(spec) && !Entry.StaleDefaults ? IntegrationStatus.Registered : IntegrationStatus.Outdated,
        ProbeState.Foreign => IntegrationStatus.Foreign,
        _ => IntegrationStatus.NotRegistered,
    };

    /// <summary>Что с записью в этом файле (см. <see cref="RepairNeed"/>). Не обращается к сетевым путям.</summary>
    public RepairNeed Need(McpServerSpec spec) => State switch
    {
        ProbeState.FileMissing or ProbeState.Absent => RepairNeed.Missing,
        ProbeState.Foreign => RepairNeed.Foreign,
        ProbeState.Ours => IntegrationBase.NeedOf(Entry!, spec),
        _ => RepairNeed.None,
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

    /// <summary>Записи Offload в файлах, куда клиент прописывается (для «доктора интеграций»). Пусто — клиент без файла.</summary>
    internal virtual IReadOnlyList<FileProbe> ProbeEntries(McpServerSpec spec) => [];

    /// <summary>Файлы конфигурации, за которыми следит автовосстановление подключения.</summary>
    internal virtual IReadOnlyList<string> WatchedFiles() => ConfigPath is { Length: > 0 } p ? [p] : [];

    /// <summary>Состояние записи в каждом файле клиента (пусто — клиент не найден или без файла).</summary>
    internal virtual IReadOnlyList<RepairNeed> FileNeeds(McpServerSpec spec) =>
        IsClientInstalled() ? ProbeEntries(spec).Select(p => p.Need(spec)).ToList() : [];

    /// <summary>
    /// Автоматическое исправление: в файлах, где наша запись указывает на отсутствующий Offload.exe (<see cref="RepairNeed.PathMoved"/>),
    /// заменить только путь к exe; прочие ключи записи и другие файлы не трогаются. null — клиент так не умеет (решает пользователь).
    /// </summary>
    internal virtual Task<IntegrationResult?> RepairPathAsync(McpServerSpec spec, CancellationToken ct = default) =>
        Task.FromResult<IntegrationResult?>(null);

    /// <summary>
    /// Состояние нашей записи: путь и аргументы совпадают — всё в порядке (или устарели умолчания); аргументы те же, а exe
    /// по пути из записи отсутствует — программу переместили; иначе запись указывает на другую копию Offload
    /// (dev-сборка, портативная копия, свои аргументы) — её не трогаем.
    /// </summary>
    internal static RepairNeed NeedOf(EntryInfo entry, McpServerSpec spec)
    {
        if (entry.Matches(spec)) return entry.StaleDefaults ? RepairNeed.StaleSettings : RepairNeed.None;
        if (!entry.Args.SequenceEqual(spec.Args, StringComparer.Ordinal)) return RepairNeed.OtherCopy;
        return CommandPath.IsMissingLocalFile(entry.Command) ? RepairNeed.PathMoved : RepairNeed.OtherCopy;
    }

    /// <summary>Самая серьёзная причина из нескольких файлов (чужая запись важнее всего, перемещённый exe — меньше всего).</summary>
    internal static RepairNeed Worst(IEnumerable<RepairNeed> needs)
    {
        var set = needs.ToHashSet();
        foreach (var n in (RepairNeed[])[RepairNeed.Foreign, RepairNeed.OtherCopy, RepairNeed.Missing, RepairNeed.StaleSettings, RepairNeed.PathMoved])
            if (set.Contains(n)) return n;
        return RepairNeed.None;
    }

    protected static string ServerName => AppInfo.McpServerId;

    protected IntegrationResult NotFound() => new(false, L.F("{0} не найден на этом компьютере.", DisplayName));

    protected string MsgRegistered(string path) => L.F("{0}: Offload подключён (файл {1}).", DisplayName, path);

    protected string MsgAlready(string path) => L.F("{0}: Offload уже подключён (файл {1}).", DisplayName, path);

    protected string MsgUnregistered(string path) => L.F("{0}: Offload отключён (файл {1}).", DisplayName, path);

    protected string MsgNotRegistered => L.F("{0}: Offload не был подключён.", DisplayName);

    protected static string MsgParse(string path, string detail) =>
        L.F("Не удалось разобрать файл {0}: {1}. Файл не изменён — исправьте ошибку в нём вручную и повторите.", path, detail);

    protected static string MsgWrite(string path, Exception ex) =>
        L.F("Не удалось записать файл {0}: {1}", path, ex.Message);

    protected static string MsgForeign(string path, string? command) =>
        L.F("В файле {0} запись «{1}» указывает на другую программу ({2}) — она оставлена без изменений.", path, ServerName, command ?? L.T("без команды"));

    protected static string MsgReplacedForeign(string? command) =>
        L.F("Прежняя запись «{0}» ({1}) заменена, резервная копия сохранена.", ServerName, command ?? L.T("без команды"));

    /// <summary>
    /// Несколько файлов → один статус: ошибка важнее всего, затем чужая запись «offload»; частичная регистрация = «требует обновления».
    /// </summary>
    protected static IntegrationStatus Aggregate(IReadOnlyList<IntegrationStatus> statuses)
    {
        if (statuses.Count == 0) return IntegrationStatus.NotRegistered;
        if (statuses.Contains(IntegrationStatus.Error)) return IntegrationStatus.Error;
        if (statuses.Contains(IntegrationStatus.Foreign)) return IntegrationStatus.Foreign;
        if (statuses.All(s => s == IntegrationStatus.Registered)) return IntegrationStatus.Registered;
        if (statuses.Any(s => s is IntegrationStatus.Registered or IntegrationStatus.Outdated)) return IntegrationStatus.Outdated;
        return IntegrationStatus.NotRegistered;
    }

    /// <summary>Причина ошибки чтения/правки файла: занят или недоступен — <see cref="FailureKind.ConfigLocked"/>, иначе не разобран.</summary>
    internal static FailureKind KindOf(Exception ex) =>
        ex is IOException or UnauthorizedAccessException ? FailureKind.ConfigLocked : FailureKind.ConfigInvalid;

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
