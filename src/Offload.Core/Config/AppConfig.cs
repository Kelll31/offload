namespace Offload.Core.Config;

/// <summary>Все настройки Offload (%LOCALAPPDATA%\Offload\config.json).</summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Пройден ли мастер первоначальной настройки.</summary>
    public bool SetupCompleted { get; set; }

    public LlamaSettings Llama { get; set; } = new();
    public ServerSettings Server { get; set; } = new();
    public ModelSettings Models { get; set; } = new();
    public OpenCodeSettings OpenCode { get; set; } = new();
    public McpSettings Mcp { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    /// <summary>Идентификаторы IDE, в которые Offload зарегистрирован как MCP-сервер.</summary>
    public List<string> Integrations { get; set; } = [];
}

/// <summary>Сборки llama.cpp для Windows.</summary>
public enum LlamaBackend
{
    /// <summary>Автовыбор по видеокарте.</summary>
    Auto,
    Cuda12,
    Cuda13,
    Vulkan,
    /// <summary>AMD ROCm (HIP).</summary>
    Rocm,
    Sycl,
    Cpu,
}

public sealed class LlamaSettings
{
    public LlamaBackend Backend { get; set; } = LlamaBackend.Auto;

    /// <summary>Фактически установленная сборка (после разрешения Auto).</summary>
    public LlamaBackend InstalledBackend { get; set; } = LlamaBackend.Auto;

    /// <summary>Тег релиза llama.cpp (например, b6710).</summary>
    public string? InstalledTag { get; set; }

    /// <summary>Папка с llama-server.exe.</summary>
    public string? InstallDir { get; set; }

    /// <summary>Проверять обновления llama.cpp при запуске.</summary>
    public bool CheckUpdates { get; set; } = true;
}

public sealed class ServerSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8765;

    /// <summary>Ключ API llama-server (генерируется автоматически, защищает от чужих локальных процессов).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Размер контекста на один слот. 0 — определить автоматически по модели и видеопамяти.</summary>
    public int ContextSize { get; set; }

    /// <summary>Число параллельных слотов (одновременных запросов).</summary>
    public int Parallel { get; set; } = 1;

    /// <summary>Слоёв на GPU: -1 — все (автоматически).</summary>
    public int GpuLayers { get; set; } = -1;

    /// <summary>Выгрузка экспертов MoE на ЦП (--n-cpu-moe). -1 — автоматически, 0 — выключено.</summary>
    public int CpuMoeLayers { get; set; } = -1;

    /// <summary>auto / on / off.</summary>
    public string FlashAttention { get; set; } = "auto";

    /// <summary>Тип KV-кэша: f16, q8_0, q4_0.</summary>
    public string CacheType { get; set; } = "q8_0";

    /// <summary>Потоков ЦП (0 — по умолчанию llama.cpp).</summary>
    public int Threads { get; set; }

    /// <summary>Дополнительные аргументы командной строки llama-server.</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>Запускать сервер автоматически при старте Offload.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Выгружать модель из памяти после простоя (минуты, 0 — никогда). Используется --sleep-idle-seconds.</summary>
    public int IdleUnloadMinutes { get; set; }

    /// <summary>Спекулятивное декодирование MTP (--spec-type draft-mtp) для моделей со встроенным MTP-слоем. Экспериментально.</summary>
    public bool EnableMtp { get; set; }

    public string BaseUrl => $"http://{Host}:{Port}";
    public string OpenAiBaseUrl => $"{BaseUrl}/v1";
}

public sealed class ModelSettings
{
    /// <summary>Папка для GGUF-файлов (модели большие — можно вынести на другой диск).</summary>
    public string? ModelsDir { get; set; }

    /// <summary>Идентификатор активной модели (из каталога или пользовательской).</summary>
    public string? ActiveModelId { get; set; }

    public List<InstalledModel> Installed { get; set; } = [];
}

public sealed class InstalledModel
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Репозиторий Hugging Face (для каталожных моделей).</summary>
    public string? Repo { get; set; }

    /// <summary>Файл(ы) GGUF. Для разбитых моделей — первый шард.</summary>
    public string FilePath { get; set; } = "";

    public long SizeBytes { get; set; }
    public string? Quant { get; set; }

    /// <summary>Родной контекст модели (токенов).</summary>
    public int NativeContext { get; set; }

    /// <summary>Рекомендуемый контекст для работы.</summary>
    public int RecommendedContext { get; set; }

    public bool IsMoe { get; set; }

    /// <summary>Архитектура из GGUF (qwen35, qwen35moe, gpt-oss…).</summary>
    public string? Architecture { get; set; }

    /// <summary>Модель надёжно вызывает инструменты (подходит для агента OpenCode).</summary>
    public bool GoodToolCalling { get; set; }

    /// <summary>Как отключать «размышления» модели для быстрых разовых задач.</summary>
    public ReasoningControl Reasoning { get; set; } = ReasoningControl.None;

    /// <summary>В файле есть MTP-слой (можно включить ServerSettings.EnableMtp).</summary>
    public bool HasMtp { get; set; }

    public SamplingSettings Sampling { get; set; } = new();

    /// <summary>Пользовательская модель, добавленная вручную.</summary>
    public bool IsCustom { get; set; }

    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Способ управления «размышлениями» (thinking) модели.</summary>
public enum ReasoningControl
{
    /// <summary>Модель не рассуждает (или управление не требуется).</summary>
    None,
    /// <summary>Qwen3.5+/Ornith: chat_template_kwargs {"enable_thinking": false}.</summary>
    EnableThinkingKwarg,
    /// <summary>gpt-oss: reasoning_effort = low/medium/high.</summary>
    ReasoningEffort,
}

public sealed class SamplingSettings
{
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.8;
    public int TopK { get; set; } = 20;
    public double MinP { get; set; }
    public double RepeatPenalty { get; set; } = 1.0;
    public double PresencePenalty { get; set; }
}

public sealed class OpenCodeSettings
{
    /// <summary>Устанавливать и использовать OpenCode для агентных задач.</summary>
    public bool Enabled { get; set; } = true;

    public string? ExecutablePath { get; set; }
    public string? InstalledVersion { get; set; }

    /// <summary>Разрешить локальному агенту выполнять команды оболочки (bash). По умолчанию — нет.</summary>
    public bool AllowShellCommands { get; set; }

    /// <summary>Максимальное время выполнения одной агентной задачи, секунд.</summary>
    public int TaskTimeoutSeconds { get; set; } = 900;

    /// <summary>Также прописать провайдера Offload в глобальный конфиг OpenCode пользователя.</summary>
    public bool RegisterInGlobalConfig { get; set; }
}

public sealed class McpSettings
{
    /// <summary>Максимальный объём текста одного файла, читаемого сервером, байт.</summary>
    public int MaxFileBytes { get; set; } = 512 * 1024;

    /// <summary>Максимальный суммарный объём файлов в одном запросе, байт.</summary>
    public int MaxTotalBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Максимальная длина ответа инструмента (символов), чтобы не расходовать токены IDE.</summary>
    public int MaxResponseChars { get; set; } = 12000;

    /// <summary>Разрешить инструментам записи изменять файлы только внутри рабочей папки IDE.</summary>
    public bool RestrictWritesToWorkspace { get; set; } = true;

    /// <summary>Сколько секунд ждать запуска сервера при первом обращении из IDE.</summary>
    public int ServerStartTimeoutSeconds { get; set; } = 180;

    /// <summary>Дополнительный системный промпт для локальной модели (например, правила Delphi VCL).</summary>
    public string ExtraSystemPrompt { get; set; } = "";

    /// <summary>Стоимость 1 млн входных токенов облачной модели, $ (для оценки экономии).</summary>
    public double CloudInputPricePerMTok { get; set; } = 3.0;

    /// <summary>Стоимость 1 млн выходных токенов облачной модели, $.</summary>
    public double CloudOutputPricePerMTok { get; set; } = 15.0;

    /// <summary>
    /// Разрешённые проверочные команды (verify_command) — шаблоны с * в конце.
    /// Команды с операторами оболочки (&amp;&amp;, |, ;, &gt;, …) отклоняются всегда.
    /// </summary>
    public List<string> VerifyCommandAllowlist { get; set; } =
    [
        "dotnet build*", "dotnet test*", "msbuild *", "npm test*", "npm run test*", "npm run build*", "npm run lint*",
        "pnpm test*", "pnpm run *", "yarn test*", "yarn run *", "npx tsc*", "npx vitest*", "npx jest*",
        "pytest*", "python -m pytest*", "python -m unittest*", "ruff *", "mypy *",
        "cargo build*", "cargo test*", "cargo check*", "cargo clippy*", "go build*", "go test*", "go vet*",
        "mvn test*", "mvn -q test*", "gradle test*", "gradlew test*", "gradlew.bat test*", "make test*", "make",
        "dcc32 *", "dcc64 *", "msbuild.exe *", "cmake --build *", "ctest*",
    ];

    /// <summary>Файлы, которые сервер никогда не читает и не изменяет (секреты).</summary>
    public List<string> SecretFilePatterns { get; set; } =
    [
        ".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12", "*.keystore", "*.jks", "id_rsa*", "id_ed25519*", "id_ecdsa*",
        "secrets.*", "*.secret", "credentials*", ".npmrc", ".pypirc", ".netrc", "*.kdbx",
    ];

    /// <summary>Сколько дней хранить снимки задач правки (для отката).</summary>
    public int JobRetentionDays { get; set; } = 7;
}

public sealed class UiSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
}
