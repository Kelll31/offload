namespace Offload.Core.Config;

/// <summary>Все настройки Offload (%LOCALAPPDATA%\Offload\config.json).</summary>
public sealed class AppConfig
{
    /// <summary>
    /// Версия схемы файла (см. <see cref="ConfigMigrations"/>). По умолчанию 1: файл без поля считается первой версией
    /// и мигрируется; при сохранении ConfigStore всегда записывает текущую версию.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Пройден ли мастер первоначальной настройки.</summary>
    public bool SetupCompleted { get; set; }

    public LlamaSettings Llama { get; set; } = new();
    public ServerSettings Server { get; set; } = new();
    public ModelSettings Models { get; set; } = new();
    public OpenCodeSettings OpenCode { get; set; } = new();
    public McpSettings Mcp { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    /// <summary>Сеть: токен Hugging Face, зеркала, прокси, удалённый каталог моделей.</summary>
    public NetworkSettings Network { get; set; } = new();

    /// <summary>Удалённый сервер основной модели (клиентский режим, ROADMAP §9.2): см. <see cref="RemoteServer"/>.</summary>
    public RemoteServerSettings Remote { get; set; } = new();

    /// <summary>Идентификаторы IDE, в которые Offload зарегистрирован как MCP-сервер.</summary>
    public List<string> Integrations { get; set; } = [];

    /// <summary>
    /// IDE, от подключения которых пользователь отказался (снял галочку в мастере или отключил на странице «Интеграции»):
    /// автоподключение Claude их не трогает, пока пользователь не подключит IDE сам.
    /// </summary>
    public List<string> DeclinedIntegrations { get; set; } = [];

    /// <summary>Автодополнение кода в IDE (FIM) на отдельном llama-server роли fim.</summary>
    public AutocompleteSettings Autocomplete { get; set; } = new();
}

/// <summary>
/// Автодополнение в IDE («локальный Copilot»): маленькая coder-модель с FIM на своём llama-server (роль fim).
/// В отличие от других вспомогательных ролей сервер работает всё время, пока автодополнение включено: IDE обращается к нему
/// напрямую (llama.vscode — /infill, Continue — /completion), без MCP. Модель — <see cref="ModelRoles.Fim"/>,
/// порт — <see cref="ServerSettings.AuxPorts"/>["fim"] (по умолчанию 8012, как у llama.vscode).
/// </summary>
public sealed class AutocompleteSettings
{
    /// <summary>Держать сервер автодополнения запущенным (вместе с треем).</summary>
    public bool Enabled { get; set; }

    /// <summary>Запускать модель автодополнения только на процессоре (-ngl 0), не занимая видеопамять основной модели.</summary>
    public bool CpuOnly { get; set; }

    /// <summary>Прописывать модель автодополнения в Continue (отдельный файл-блок в папке models Continue).</summary>
    public bool ConfigureContinue { get; set; } = true;

    /// <summary>
    /// Ключ API сервера автодополнения — свой, не ключ основного сервера: он лежит открытым текстом в файле Continue
    /// и в settings.json VS Code, а основной ключ открывает основную модель (в режиме «Доступ из сети» — и из сети).
    /// Создаётся ConfigStore при загрузке.
    /// </summary>
    public string? ApiKey { get; set; }
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
    /// <summary>Intel OpenVINO (x64) — только ручной выбор.</summary>
    OpenVino,
    /// <summary>OpenCL для Adreno (Snapdragon X, ARM64) — только ручной выбор.</summary>
    OpenClAdreno,
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

    /// <summary>Закреплённый тег (bNNNNN): установка ставит именно его, более новые сборки не предлагаются. null — последняя.</summary>
    public string? PinnedTag { get; set; }

    /// <summary>
    /// Папка сборки, работавшей до последнего обновления. Задана, пока новая сборка не запустила сервер хотя бы раз:
    /// если первый запуск не удался, выполняется откат на неё.
    /// </summary>
    public string? PreviousInstallDir { get; set; }

    /// <summary>Папка сборки, с которой только что был выполнен автоматический откат (до подтверждения отката удачным запуском).</summary>
    public string? RolledBackFromDir { get; set; }

    /// <summary>Тег сборки, не прошедшей проверку или первый запуск: обновление до неё больше не предлагается.</summary>
    public string? FailedTag { get; set; }
}

public sealed class ServerSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8765;

    /// <summary>Ключ API llama-server (генерируется автоматически, защищает от чужих локальных процессов).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Размер контекста на один слот. 0 — определить автоматически по модели и видеопамяти.</summary>
    public int ContextSize { get; set; }

    /// <summary>Наибольшее число параллельных слотов: общий предел для настроек интерфейса и аргументов llama-server.</summary>
    public const int MaxParallel = 8;

    /// <summary>Число параллельных слотов (одновременных запросов), 1..<see cref="MaxParallel"/>.</summary>
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

    /// <summary>Последний замер скорости («Измерить скорость» на странице «Сервер») по идентификатору модели.</summary>
    public Dictionary<string, BenchmarkRecord> LastBenchmark { get; set; } = [];

    /// <summary>Все подходящие видеокарты (--tensor-split по свободной видеопамяти).</summary>
    public const string GpuSelectionAll = "all";

    /// <summary>Только основная (самая большая) видеокарта (--device).</summary>
    public const string GpuSelectionPrimary = "primary";

    /// <summary>
    /// Какие видеокарты использовать, если их несколько: <see cref="GpuSelectionAll"/> или <see cref="GpuSelectionPrimary"/>.
    /// С одной видеокартой не действует.
    /// </summary>
    public string GpuSelection { get; set; } = GpuSelectionAll;

    /// <summary>
    /// Параметры, подобранные автоподбором, по ключу «модель|отпечаток оборудования» (см. LlamaAutoTune.ProfileKey).
    /// </summary>
    public Dictionary<string, TunedProfile> TunedProfiles { get; set; } = [];

    /// <summary>
    /// Порты вспомогательных серверов ролей (ключ — "fast", "embed", "rerank"). Нет записи — порт основного сервера + 1/2/3
    /// (<see cref="ModelRoleConfig.AuxPort"/>); запись появляется, если порт по умолчанию был занят и сервер выбрал другой.
    /// </summary>
    public Dictionary<string, int> AuxPorts { get; set; } = [];

    /// <summary>
    /// «Доступ из сети»: основной llama-server слушает <see cref="LanBindAddress"/> вместо <see cref="Host"/> и принимает
    /// дополнительно сетевой ключ. Действует только вместе с ключом (<see cref="LanServer.IsActive"/>). По умолчанию выключен.
    /// </summary>
    public bool LanAccess { get; set; }

    /// <summary>Адрес прослушивания в режиме «Доступ из сети»: 0.0.0.0 (все интерфейсы) или IPv4-адрес одного интерфейса.</summary>
    public string LanBindAddress { get; set; } = LanServer.AnyAddress;

    /// <summary>Сетевой ключ API (для других компьютеров), зашифрованный DPAPI для текущего пользователя Windows (base64).</summary>
    public string? LanApiKeyProtected { get; set; }

    /// <summary>Адрес основного сервера для клиентов на этом компьютере (с учётом режима «Доступ из сети»).</summary>
    public string BaseUrl => LanServer.ClientBaseUrl(this.ListenHost(), Port);
    public string OpenAiBaseUrl => $"{BaseUrl}/v1";
}

/// <summary>Результат встроенного замера скорости llama-server и параметры, при которых он получен.</summary>
public sealed class BenchmarkRecord
{
    public DateTime MeasuredAtUtc { get; set; }

    /// <summary>Скорость обработки промпта, токенов/с.</summary>
    public double PromptTokensPerSecond { get; set; }

    /// <summary>Скорость генерации, токенов/с.</summary>
    public double GenerationTokensPerSecond { get; set; }

    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    /// <summary>Контекст одного слота при замере.</summary>
    public int ContextSize { get; set; }

    public int Parallel { get; set; }

    /// <summary>--n-cpu-moe при замере (-1 — размещение решал --fit, 0 — выключено).</summary>
    public int CpuMoeLayers { get; set; }

    /// <summary>Тег сборки llama.cpp (b6710…).</summary>
    public string? LlamaTag { get; set; }
}

public sealed class ModelSettings
{
    /// <summary>Папка для GGUF-файлов (модели большие — можно вынести на другой диск).</summary>
    public string? ModelsDir { get; set; }

    /// <summary>Идентификатор активной модели (из каталога или пользовательской).</summary>
    public string? ActiveModelId { get; set; }

    public List<InstalledModel> Installed { get; set; } = [];

    /// <summary>
    /// Роли моделей (ROADMAP §5.2). Роль quality — всегда активная модель (<see cref="ActiveModelId"/>, основной llama-server);
    /// остальные роли необязательны и работают на своих вспомогательных серверах, которые трей запускает по первому запросу.
    /// </summary>
    public ModelRoles Roles { get; set; } = new();
}

/// <summary>
/// Назначение модели (поле role каталога): chat — чат и код, embed — эмбеддинги (векторы текста), rerank — реранкер.
/// Модели embed/rerank не могут быть активной (основной) моделью.
/// </summary>
public enum ModelKind
{
    Chat,
    Embed,
    Rerank,
    /// <summary>Coder-модель с FIM (fill-in-the-middle) для автодополнения в IDE; активной быть не может.</summary>
    Fim,
}

/// <summary>Роль модели в Offload: какой сервер обслуживает запрос.</summary>
public enum ModelRole
{
    /// <summary>Основная модель (активная, основной llama-server): агент, запись кода, ревью.</summary>
    Quality,
    /// <summary>Быстрая модель для коротких интерактивных задач (commit_message, summarize_log, find_context).</summary>
    Fast,
    /// <summary>Модель эмбеддингов (llama-server --embeddings).</summary>
    Embed,
    /// <summary>Реранкер (llama-server --reranking).</summary>
    Rerank,
    /// <summary>Автодополнение в IDE (FIM): сервер работает постоянно, пока автодополнение включено.</summary>
    Fim,
}

/// <summary>Назначение моделей вспомогательным ролям (идентификаторы установленных моделей; null — роль не используется).</summary>
public sealed class ModelRoles
{
    /// <summary>Быстрая модель; null — короткие задачи выполняет основная модель.</summary>
    public string? Fast { get; set; }

    /// <summary>Модель эмбеддингов; null — не используется.</summary>
    public string? Embed { get; set; }

    /// <summary>Реранкер; null — не используется.</summary>
    public string? Rerank { get; set; }

    /// <summary>Модель автодополнения (FIM); null — не назначена.</summary>
    public string? Fim { get; set; }
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

    /// <summary>Назначение модели (из каталога): чат, эмбеддинги или реранк. Пользовательские модели — Chat.</summary>
    public ModelKind Kind { get; set; } = ModelKind.Chat;

    /// <summary>Пулинг эмбеддингов (--pooling: mean, last, cls); null — как задано в GGUF.</summary>
    public string? Pooling { get; set; }

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

    /// <summary>
    /// Канал версий OpenCode: "stable" — проверенная версия с зашитыми SHA-256 (OpenCodeReleases.Pinned),
    /// "latest" — последний релиз GitHub (контрольная сумма из API). null — не выбран явно (конфиг прежних версий):
    /// действующий канал определяет OpenCodeChannels.Effective — "latest", если установлена версия новее проверенной
    /// (чтобы «Обновить» не откатило её), иначе "stable"; при первой установке/обновлении он записывается сюда.
    /// </summary>
    public string? Channel { get; set; }
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
        // Проверки форматирования/линтеры без записи файлов (local_verify kind=lint/format).
        "dotnet format --verify-no-changes*", "cargo fmt --check*", "npx prettier --check*", "npx eslint*",
    ];

    /// <summary>Файлы, которые сервер никогда не читает и не изменяет (секреты).</summary>
    public List<string> SecretFilePatterns { get; set; } =
    [
        ".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12", "*.keystore", "*.jks", "id_rsa*", "id_ed25519*", "id_ecdsa*",
        "secrets.*", "*.secret", "credentials*", ".npmrc", ".pypirc", ".netrc", "*.kdbx",
    ];

    /// <summary>Сколько дней хранить снимки задач правки (для отката).</summary>
    public int JobRetentionDays { get; set; } = 7;

    /// <summary>
    /// Маскировать значения секретов по содержимому (строки подключения, ключи API, токены, приватные ключи) в тексте,
    /// который читает локальная модель, и в ответах инструментов, показывающих код (search_code, symbols, find_context, ask_files).
    /// </summary>
    public bool RedactSecrets { get; set; } = true;

    /// <summary>
    /// Запретить инструментам записи менять файлы сборки (*.props, *.targets, *.csproj, package.json, Makefile, conftest.py,
    /// setup.py, build.rs, *.gradle …) без явного allow_build_files=true в вызове: правка сборки + local_verify = выполнение кода.
    /// </summary>
    public bool ProtectBuildFiles { get; set; }

    /// <summary>
    /// Токен Bearer для MCP по Streamable HTTP (<c>Offload.exe --mcp-http</c>, только 127.0.0.1). Пусто — HTTP-режим ещё
    /// не использовался: MCP-процесс конфиг не пишет, поэтому токен создаёт трей по IPC-команде ensure-mcp-http-token.
    /// </summary>
    public string HttpToken { get; set; } = "";

    /// <summary>
    /// Порт MCP по HTTP на 127.0.0.1. 0 — ещё не назначен. Случайный для каждого пользователя (создаётся трей вместе с токеном):
    /// фиксированный общий порт другой локальный пользователь или процесс в WSL2 (зеркальная сеть) мог бы занять заранее
    /// и получить токен от клиента.
    /// </summary>
    public int HttpPort { get; set; }

    /// <summary>
    /// Дополнительные значения заголовка Host (и Origin), которые HTTP-режим принимает кроме 127.0.0.1:&lt;порт&gt; и
    /// localhost:&lt;порт&gt;: проброс порта (ssh -L → «localhost:1234»), devcontainer («host.docker.internal:&lt;порт&gt;»).
    /// Точное совпадение «хост:порт» без учёта регистра. Сервер по-прежнему слушает только 127.0.0.1, токен обязателен.
    /// </summary>
    public List<string> HttpAllowedHosts { get; set; } = [];

    /// <summary>
    /// Предлагать пользователю «разрешить один раз» (elicitation) проверочную команду не из белого списка (только той же программы,
    /// что уже разрешена). false — не предлагать никогда. По HTTP одноразовое разрешение выключено всегда.
    /// </summary>
    public bool AllowOneTimeVerify { get; set; } = true;

    /// <summary>
    /// Кэш результатов модели для инструментов только для чтения (ask_files, review_diff, summarize_log, commit_message,
    /// шаги модели find_context): тот же вопрос по неизменённым входам — ответ без повторной генерации. Хранится в папке данных
    /// Offload (cache/work), по базе на рабочую папку; секреты маскируются. Работает только при <see cref="RedactSecrets"/>.
    /// </summary>
    public bool WorkCache { get; set; } = true;

    /// <summary>Предел размера кэша результатов на одну рабочую папку, МБ (старые по последнему использованию удаляются).</summary>
    public int WorkCacheMaxMb { get; set; } = 50;

    /// <summary>Сколько дней хранить результат в кэше (после — считается устаревшим, даже если входы не менялись).</summary>
    public int WorkCacheTtlDays { get; set; } = 14;

    /// <summary>
    /// Автоматическая память проекта: уроки локального агента (проверка прошла после исправлений), «грабли» проверок
    /// (FAILED → PASSED с понятной причиной); подсказки из памяти добавляются в задания агенту. Записи помечаются тегом auto.
    /// </summary>
    public bool AutoMemory { get; set; } = true;

    /// <summary>Прежний фиксированный порт HTTP-режима: новым конфигам не назначается.</summary>
    public const int LegacyHttpPort = 39217;

    /// <summary>Диапазон случайного порта: ниже динамических портов Windows (49152+), выше распространённых служебных.</summary>
    public const int HttpPortMin = 20000;

    public const int HttpPortMax = 47999;

    /// <summary>Новый случайный токен для <see cref="HttpToken"/> (256 бит).</summary>
    public static string NewHttpToken() =>
        "oft-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Новый случайный порт для <see cref="HttpPort"/> (никогда не <see cref="LegacyHttpPort"/>). <paramref name="isFree"/> —
    /// проверка, что порт сейчас можно занять (трей пробует несколько кандидатов); без неё — первый случайный.
    /// </summary>
    public static int NewHttpPort(Func<int, bool>? isFree = null)
    {
        var port = 0;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            port = System.Security.Cryptography.RandomNumberGenerator.GetInt32(HttpPortMin, HttpPortMax + 1);
            if (port == LegacyHttpPort) continue;
            if (isFree is null || isFree(port)) return port;
        }
        return port == LegacyHttpPort ? LegacyHttpPort + 1 : port;
    }

    /// <summary>Порт задан и допустим.</summary>
    public static bool IsValidHttpPort(int port) => port is >= 1 and <= 65535;

    /// <summary>
    /// Создать недостающие учётные данные HTTP-режима: токен и случайный порт (трей по IPC ensure-mcp-http-token).
    /// true — что-то изменилось и конфиг нужно сохранить.
    /// </summary>
    public bool EnsureHttpCredentials(Func<int, bool>? isFree = null)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(HttpToken))
        {
            HttpToken = NewHttpToken();
            changed = true;
        }
        if (!IsValidHttpPort(HttpPort))
        {
            HttpPort = NewHttpPort(isFree);
            changed = true;
        }
        return changed;
    }

    /// <summary>Заменить токен HTTP-режима новым (IPC rotate-mcp-http-token); порт сохраняется, недостающий — назначается.</summary>
    public void RotateHttpToken(Func<int, bool>? isFree = null)
    {
        HttpToken = NewHttpToken();
        if (!IsValidHttpPort(HttpPort)) HttpPort = NewHttpPort(isFree);
    }
}

/// <summary>Сетевые настройки (ROADMAP §9.3).</summary>
public sealed class NetworkSettings
{
    /// <summary>Токен Hugging Face, зашифрованный DPAPI для текущего пользователя Windows (base64). Пусто — без токена.</summary>
    public string? HfTokenProtected { get; set; }

    /// <summary>Передавать токен и зеркалу Hugging Face (по умолчанию токен уходит только на huggingface.co).</summary>
    public bool SendHfTokenToMirror { get; set; }

    /// <summary>Зеркало Hugging Face вместо https://huggingface.co (переменная окружения HF_ENDPOINT важнее). Пусто — официальный хаб.</summary>
    public string? HfMirror { get; set; }

    /// <summary>Зеркало GitHub API вместо https://api.github.com (релизы llama.cpp, OpenCode, Offload). Пусто — без зеркала.</summary>
    public string? GitHubApiMirror { get; set; }

    /// <summary>Зеркало github.com (загрузки файлов релизов): адрес, к которому дописывается путь. Пусто — без зеркала.</summary>
    public string? GitHubMirror { get; set; }

    /// <summary>Прокси: "system" (как в Windows), "none" (напрямую) или "custom" (<see cref="ProxyUrl"/>).</summary>
    public string ProxyMode { get; set; } = "system";

    /// <summary>Адрес своего прокси (http://, https://, socks5://) для ProxyMode = custom.</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>Обновлять каталог моделей из репозитория Offload (подпись Ed25519, не чаще раза в сутки).</summary>
    public bool RemoteCatalog { get; set; } = true;

    /// <summary>Адрес удалённого каталога (рядом должен лежать «адрес.sig»); пусто — репозиторий Offload на GitHub.</summary>
    public string? CatalogUrl { get; set; }
}

public sealed class UiSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Проверять обновления Offload на GitHub при запуске (не чаще раза в 12 часов).</summary>
    public bool CheckAppUpdates { get; set; } = true;

    /// <summary>Тема интерфейса: "system" (как в Windows), "light" или "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Боковая панель навигации свёрнута до значков.</summary>
    public bool NavCollapsed { get; set; }

    /// <summary>Цветовая схема: default, teal, green, purple, orange, pink, midnight, contrast или custom (свой акцент).</summary>
    public string ThemePreset { get; set; } = "default";

    /// <summary>Свой акцентный цвет «#RRGGBB» (для ThemePreset = custom).</summary>
    public string? AccentColor { get; set; }

    /// <summary>Язык интерфейса: ru, en или system (по языку Windows).</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Подробный журнал: писать и отладочные записи (DBG). Переменная OFFLOAD_LOG_LEVEL важнее.</summary>
    public bool VerboseLog { get; set; }

    /// <summary>Положение и размер окна панели управления (null — по центру экрана, размер по умолчанию).</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>Последний открытый раздел панели управления (ключ вкладки).</summary>
    public string? LastTab { get; set; }

    /// <summary>Версия Offload при прошлом запуске трея: запуск более новой версии — «Что нового» (раз на версию).</summary>
    public string? LastRunVersion { get; set; }

    /// <summary>
    /// Следить за конфигами подключённых IDE и возвращать запись Offload, если IDE её сбросила (с уведомлением).
    /// Не действует в режиме разработчика и в копии, запущенной не из папки установки.
    /// </summary>
    public bool AutoRepairIntegrations { get; set; } = true;
}

/// <summary>
/// Сохранённое положение окна: левый верхний угол — в пикселях экрана, размер — в логических пикселях (96 DPI),
/// чтобы на мониторе с другим масштабом окно не стало больше или меньше.
/// </summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}
