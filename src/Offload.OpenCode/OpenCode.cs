namespace Offload.OpenCode;

// Публичный контракт модуля. Реализация:
//   OpenCodeInstaller.cs    — установка standalone-бинарника (OpenCodeReleases.cs — выбор релиза/сборки);
//   OpenCodeConfigWriter.cs — управляемый конфиг, глобальная регистрация, окружение (AgentPrompts.cs — промпты);
//   OpenCodeRunner.cs       — «opencode run» и интерактивный запуск
//                             (RunEvents.cs — разбор JSONL, ChangeTracker.cs — изменённые файлы,
//                              RunQueueLock.cs — очередь запусков, LocalServer.cs — проверка llama-server).

public sealed record OpenCodeInstallResult(string Version, string ExecutablePath);

public sealed record OpenCodeRunOptions(
    /// <summary>Разрешить выполнение команд оболочки (иначе — только чтение/правка файлов).</summary>
    bool AllowShell,
    TimeSpan Timeout,
    /// <summary>Агент OpenCode: «build» (может править файлы) или «plan» (только анализ).</summary>
    string Agent = "build");

public sealed record OpenCodeRunResult(
    bool Success,
    /// <summary>Итоговый ответ агента (последнее сообщение ассистента).</summary>
    string FinalText,
    /// <summary>Изменённые/созданные/удалённые файлы (относительно рабочей папки), со статусом: «M path», «A path», «D path».</summary>
    IReadOnlyList<string> ChangedFiles,
    /// <summary>Вызванные агентом инструменты по порядку (кратко: «edit src/a.cs»).</summary>
    IReadOnlyList<string> ToolCalls,
    string? Error,
    int ExitCode,
    TimeSpan Duration,
    long PromptTokens,
    long CompletionTokens);
