namespace Offload.Core;

/// <summary>
/// Имена MCP-инструментов Offload. В Claude Code они видны как mcp__offload__&lt;имя&gt;.
/// </summary>
public static class McpToolNames
{
    /// <summary>Состояние локальной модели, скорость, очередь, экономия.</summary>
    public const string Status = "local_status";

    /// <summary>Вопрос по файлам, которые сервер читает сам (в контекст IDE попадает только ответ).</summary>
    public const string AskFiles = "local_ask_files";

    /// <summary>Сжатое изложение большого лога / вывода сборки или тестов.</summary>
    public const string SummarizeLog = "local_summarize_log";

    /// <summary>Первичное ревью git diff локальной моделью.</summary>
    public const string ReviewDiff = "local_review_diff";

    /// <summary>Сообщение коммита по git diff, прочитанному на стороне сервера.</summary>
    public const string CommitMessage = "local_commit_message";

    /// <summary>Создать новый файл по спецификации — код пишет локальная модель прямо на диск.</summary>
    public const string WriteFile = "local_write_file";

    /// <summary>Механическая правка разрешённых файлов (прямая перезапись или агент OpenCode) с проверкой командой.</summary>
    public const string EditFiles = "local_edit_files";

    /// <summary>Статус / diff / откат задачи записи или правки.</summary>
    public const string Job = "local_job";

    /// <summary>Инструменты только для чтения (безопасно разрешать без подтверждения).</summary>
    public static readonly IReadOnlyList<string> ReadOnly = [Status, AskFiles, SummarizeLog, ReviewDiff, CommitMessage];

    /// <summary>Инструменты, изменяющие файлы.</summary>
    public static readonly IReadOnlyList<string> Writing = [WriteFile, EditFiles, Job];

    public static readonly IReadOnlyList<string> All = [.. ReadOnly, .. Writing];

    /// <summary>Полное имя в Claude Code: mcp__offload__local_ask_files.</summary>
    public static string ClaudeCodeName(string tool) => $"mcp__{AppInfo.McpServerId}__{tool}";
}
