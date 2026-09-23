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

    /// <summary>Задача программирования для локального агента OpenCode в git-песочнице с последующим слиянием в проект.</summary>
    public const string AgentTask = "local_agent_task";

    /// <summary>Запуск разрешённой команды сборки/тестов: полный лог в файл, в IDE — только итог и разбор ошибок.</summary>
    public const string Verify = "local_verify";

    /// <summary>Список / статус / diff / слияние / откат задач записи, правки и агента.</summary>
    public const string Job = "local_job";

    /// <summary>Контекст под задачу: сервер сам находит и ранжирует файлы/символы и укладывает их в бюджет токенов.</summary>
    public const string FindContext = "local_find_context";

    /// <summary>Поиск по тексту/regex/слову/имени файла с короткими сниппетами.</summary>
    public const string SearchCode = "local_search_code";

    /// <summary>Навигация по символам: outline, определение, ссылки, реализации, граф вызовов, тесты, API, срез тела.</summary>
    public const string Symbols = "local_symbols";

    /// <summary>Карта проекта: проекты, языки, точки входа, команды, маршруты, конфигурация, CI, правила репозитория.</summary>
    public const string ProjectMap = "local_project_map";

    /// <summary>Структурированные ошибки компилятора/линтера и кадры стека с привязкой к символам.</summary>
    public const string Diagnostics = "local_diagnostics";

    /// <summary>Атомарное применение unified diff со снимком, проверкой и автооткатом.</summary>
    public const string ApplyPatch = "local_apply_patch";

    /// <summary>Рефакторинг: безопасное переименование символа по всем ссылкам; извлечение/перенос — агентом в песочнице.</summary>
    public const string Refactor = "local_refactor";

    /// <summary>Анализ влияния изменения: затронутые символы, вызывающие, тесты, проекты; запуск только связанных тестов.</summary>
    public const string Impact = "local_impact";

    /// <summary>Статические проверки кода: TODO, секреты, опасные API, мёртвый код, дубли, сложность, горячие точки.</summary>
    public const string CodeScan = "local_code_scan";

    /// <summary>Проверка безопасности изменённого кода (diff) без чтения секретов.</summary>
    public const string SecurityReview = "local_security_review";

    /// <summary>История git: файл/символ, сводка blame, связанные коммиты, changelog.</summary>
    public const string GitHistory = "local_git_history";

    /// <summary>Зависимости: список, граф, устаревшие, уязвимые, лицензии.</summary>
    public const string Dependencies = "local_dependency_check";

    /// <summary>Память проекта: факты и решения между сессиями.</summary>
    public const string Memory = "local_memory";

    /// <summary>Полный цикл задачи локально: контекст → правки в песочнице → проверка → ревью → доказательства результата.</summary>
    public const string Solve = "local_solve";

    /// <summary>Инструменты только для чтения (безопасно разрешать без подтверждения).</summary>
    public static readonly IReadOnlyList<string> ReadOnly =
    [
        Status, AskFiles, SummarizeLog, ReviewDiff, CommitMessage, FindContext, SearchCode, Symbols, ProjectMap, CodeScan, SecurityReview, GitHistory,
    ];

    /// <summary>Инструменты, изменяющие файлы.</summary>
    public static readonly IReadOnlyList<string> Writing =
    [
        WriteFile, EditFiles, AgentTask, Solve, ApplyPatch, Refactor, Verify, Diagnostics, Impact, Dependencies, Memory, Job,
    ];

    public static readonly IReadOnlyList<string> All = [.. ReadOnly, .. Writing];

    /// <summary>Полное имя в Claude Code: mcp__offload__local_ask_files.</summary>
    public static string ClaudeCodeName(string tool) => $"mcp__{AppInfo.McpServerId}__{tool}";
}
