using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Core.Util;
using Offload.Integrations;

namespace Offload.OpenCode;

/// <summary>
/// Генерация конфигурации OpenCode, указывающей на локальный llama-server
/// (провайдер «offload» через @ai-sdk/openai-compatible).
/// </summary>
public static class OpenCodeConfigWriter
{
    public const string ProviderId = "offload";

    /// <summary>Ключ модели у провайдера: стабильный, не зависит от выбранного GGUF (выбор модели в TUI не «ломается»).</summary>
    internal const string ModelKey = "local-coder";

    /// <summary>Имя модели в запросах к llama-server (= --alias). Не содержит «gpt»/«claude» — OpenCode не подменит инструменты.</summary>
    internal const string ServerModelAlias = "offload";

    internal const string SchemaUrl = "https://opencode.ai/config.json";
    internal const string EditAgent = "offload";
    internal const string ReadOnlyAgent = "offload-readonly";
    internal const string ApiKeyEnvVar = "OFFLOAD_API_KEY";

    /// <summary>Метка процессов, запущенных из OpenCode под Offload (защита от рекурсивного local_agent).</summary>
    internal const string NestedEnvVar = "OFFLOAD_OPENCODE";

    internal const int DefaultContext = 32768;
    internal const int EditAgentSteps = 40;
    internal const int ReadOnlyAgentSteps = 25;

    /// <summary>Опасные команды, запрещённые даже при разрешённой оболочке (последнее совпавшее правило побеждает).</summary>
    internal static readonly string[] DangerousCommands =
    [
        "git push*", "git commit*", "git reset --hard*", "git clean*", "git checkout -- *",
        "rm -rf*", "rm -fr*", "rm -r *", "*Remove-Item*-Recurse*", "*rmdir /s*", "*rd /s*", "*del /s*",
        "format *", "*Format-Volume*", "diskpart*", "shutdown*", "*Stop-Computer*", "*Restart-Computer*", "reg delete*",
    ];

    /// <summary>Инструменты, убираемые из списка (deny с шаблоном «*»): экономия контекста и безопасность.</summary>
    private static readonly string[] DeniedTools =
        ["webfetch", "websearch", "task", "todowrite", "skill", "question", "doom_loop"];

    private static readonly object WriteLock = new();

    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string CacheHome => Path.Combine(AppPaths.OpenCodeDir, "cache");

    private static readonly string[] IsolatedDirNames = ["config", "cache", "data", "state", "data-tui", "state-tui"];

    /// <summary>Изолированные папки XDG (config/cache общие; data/state у TUI свои — параллельные экземпляры на одной базе зависают).</summary>
    internal static IEnumerable<string> IsolatedDirs =>
        IsolatedDirNames.Select(n => Path.Combine(AppPaths.OpenCodeDir, n));

    /// <summary>Идентификатор модели для --model: «offload/&lt;alias&gt;».</summary>
    public static string ModelRef(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return $"{ProviderId}/{ModelKey}";
    }

    /// <summary>Контекст llama-server на слот: настройка сервера → рекомендованный контекст модели → 32768.</summary>
    internal static int EffectiveContext(AppConfig cfg)
    {
        // Клиентский режим: контекст удалённого сервера по последней проверке трея (обычно его заменяет фактический из /props).
        if (cfg.IsRemote()) return cfg.Remote.ContextSize > 0 ? cfg.Remote.ContextSize : DefaultContext;
        if (cfg.Server.ContextSize > 0) return cfg.Server.ContextSize;
        if (cfg.ActiveModel() is { RecommendedContext: > 0 } m) return m.RecommendedContext;
        return DefaultContext;
    }

    /// <summary>limit.output: умеренный (иначе OpenCode просит 32000 и сжатие контекста срабатывает слишком поздно).</summary>
    internal static int OutputLimit(int context) => Math.Clamp(context / 4, 512, 8192);

    /// <summary>Порог обрезки вывода инструментов (bash/grep), байт: примерно четверть контекста.</summary>
    internal static int ToolOutputBytes(int context) => Math.Clamp(context, 8192, 51200);

    /// <summary>
    /// Записать %LOCALAPPDATA%\Offload\opencode\opencode.json (провайдер, модель по умолчанию,
    /// разрешения: edit=allow, bash=allow/deny по настройке, webfetch=deny; autoupdate/share выключены).
    /// Возвращает путь к файлу.
    /// </summary>
    public static string WriteManagedConfig(AppConfig cfg) => WriteManagedConfig(cfg, null);

    /// <param name="contextOverride">Фактический контекст слота, полученный от работающего llama-server (/props).</param>
    public static string WriteManagedConfig(AppConfig cfg, int? contextOverride)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var path = AppPaths.OpenCodeConfigFile;
        var json = BuildManagedConfig(cfg, contextOverride).ToJsonString(WriteOptions);
        lock (WriteLock)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == json) return path;
            }
            catch (IOException)
            {
                // Читается другим процессом — просто перезапишем.
            }
            FileUtil.WriteAllTextAtomic(path, json);
        }
        Log.Debug("opencode", $"Конфигурация OpenCode обновлена: {path}");
        return path;
    }

    internal static JsonObject BuildManagedConfig(AppConfig cfg, int? contextOverride = null)
    {
        var context = contextOverride is > 0 ? contextOverride.Value : EffectiveContext(cfg);
        var modelRef = ModelRef(cfg);
        return new JsonObject
        {
            ["$schema"] = SchemaUrl,
            ["autoupdate"] = false,
            ["share"] = "disabled",
            ["snapshot"] = false,
            ["enabled_providers"] = new JsonArray(ProviderId),
            ["model"] = modelRef,
            ["small_model"] = modelRef,
            ["default_agent"] = EditAgent,
            ["provider"] = new JsonObject
            {
                // Ключ — из переменной окружения: в файл он не попадает.
                [ProviderId] = BuildProvider(cfg, context, "{env:" + ApiKeyEnvVar + "}"),
            },
            ["agent"] = new JsonObject
            {
                ["title"] = new JsonObject { ["disable"] = true },
                [EditAgent] = BuildEditAgent(cfg, cfg.OpenCode.AllowShellCommands),
                [ReadOnlyAgent] = BuildReadOnlyAgent(cfg),
            },
            ["tool_output"] = new JsonObject
            {
                ["max_lines"] = 1000,
                ["max_bytes"] = ToolOutputBytes(context),
            },
            ["experimental"] = new JsonObject
            {
                // Отклонённое «ask» не обрывает работу агента (правила у нас явные allow/deny — это страховка).
                ["continue_loop_on_deny"] = true,
            },
        };
    }

    /// <summary>
    /// Модель основного сервера для сэмплинга и отключения размышлений: активная локальная модель; в клиентском режиме — null
    /// (на удалённом сервере другая модель, её умолчания задаёт тот сервер).
    /// </summary>
    internal static InstalledModel? MainModel(AppConfig cfg) => cfg.IsRemote() ? null : cfg.ActiveModel();

    internal static JsonObject BuildProvider(AppConfig cfg, int context, string apiKeyValue)
    {
        var model = MainModel(cfg);
        var ep = cfg.MainEndpoint();
        var name = ep.IsRemote ? $"{ep.Model} @ {ep.Host} (Offload)"
            : model is { DisplayName.Length: > 0 } ? $"{model.DisplayName} (Offload)" : "Локальная модель (Offload)"; // l10n-ignore: имя провайдера в конфиге OpenCode
        return new JsonObject
        {
            ["npm"] = "@ai-sdk/openai-compatible",
            ["name"] = "Offload (llama.cpp)",
            ["options"] = new JsonObject
            {
                ["baseURL"] = ep.OpenAiBaseUrl,
                ["apiKey"] = apiKeyValue,
                // Локальная обработка длинного контекста бывает медленной — таймауты щедрые.
                ["timeout"] = 900000,
                ["headerTimeout"] = 900000,
                ["chunkTimeout"] = 300000,
            },
            ["models"] = new JsonObject
            {
                [ModelKey] = new JsonObject
                {
                    ["id"] = ep.IsRemote ? ep.Model : ServerModelAlias,
                    ["name"] = name,
                    ["tool_call"] = true,
                    ["reasoning"] = false,
                    ["attachment"] = false,
                    // Иначе OpenCode не передаёт температуру агента.
                    ["temperature"] = true,
                    ["limit"] = new JsonObject
                    {
                        ["context"] = context,
                        ["output"] = OutputLimit(context),
                    },
                    ["options"] = BuildModelOptions(model),
                },
            },
        };
    }

    /// <summary>Параметры, которые @ai-sdk/openai-compatible добавляет прямо в тело /v1/chat/completions.</summary>
    internal static JsonObject BuildModelOptions(InstalledModel? model)
    {
        var s = model?.Sampling ?? new SamplingSettings();
        var o = new JsonObject
        {
            ["top_k"] = s.TopK,
            ["min_p"] = s.MinP,
            ["repeat_penalty"] = s.RepeatPenalty,
        };
        if (s.PresencePenalty != 0) o["presence_penalty"] = s.PresencePenalty;
        switch (model?.Reasoning)
        {
            case ReasoningControl.EnableThinkingKwarg:
                // Размышления в агентном цикле медленные и ломают шаблоны (issue #27920) — отключаем.
                o["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
                break;
            case ReasoningControl.ReasoningEffort:
                o["reasoning_effort"] = "low";
                break;
        }
        return o;
    }

    internal static JsonObject BuildEditAgent(AppConfig cfg, bool allowShell)
    {
        var s = MainModel(cfg)?.Sampling ?? new SamplingSettings();
        return new JsonObject
        {
            ["description"] = "Offload: локальная модель выполняет задачу в проекте (чтение и правка файлов).", // l10n-ignore: конфиг OpenCode
            ["mode"] = "primary",
            ["model"] = ModelRef(cfg),
            ["temperature"] = s.Temperature,
            ["top_p"] = s.TopP,
            ["steps"] = EditAgentSteps,
            ["prompt"] = AgentPrompts.Edit(allowShell, cfg.Mcp.ExtraSystemPrompt),
            ["permission"] = BuildPermissions(cfg, allowEdit: true, allowShell),
        };
    }

    internal static JsonObject BuildReadOnlyAgent(AppConfig cfg)
    {
        var s = MainModel(cfg)?.Sampling ?? new SamplingSettings();
        return new JsonObject
        {
            ["description"] = "Offload: локальная модель анализирует проект (только чтение).", // l10n-ignore: конфиг OpenCode
            ["mode"] = "primary",
            ["model"] = ModelRef(cfg),
            ["temperature"] = s.Temperature,
            ["top_p"] = s.TopP,
            ["steps"] = ReadOnlyAgentSteps,
            ["prompt"] = AgentPrompts.ReadOnly(cfg.Mcp.ExtraSystemPrompt),
            ["permission"] = BuildPermissions(cfg, allowEdit: false, allowShell: false),
        };
    }

    /// <summary>
    /// Только явные allow/deny: в «opencode run» правило «ask» отклоняется автоматически и останавливает агента.
    /// Первым идёт «*»: allow (перекрывает умолчания OpenCode с «ask»), дальше — запреты.
    /// </summary>
    internal static JsonObject BuildPermissions(AppConfig cfg, bool allowEdit, bool allowShell)
    {
        var p = new JsonObject
        {
            ["*"] = "allow",
            ["read"] = SecretRules(cfg),
            ["edit"] = allowEdit ? EditRules(cfg) : "deny",
            ["bash"] = BashRules(allowShell),
            ["external_directory"] = "deny",
        };
        foreach (var tool in DeniedTools) p[tool] = "deny";
        return p;
    }

    internal static JsonNode BashRules(bool allowShell)
    {
        if (!allowShell) return JsonValue.Create("deny");
        var rules = new JsonObject { ["*"] = "allow" };
        foreach (var pattern in DangerousCommands) rules[pattern] = "deny";
        return rules;
    }

    /// <summary>Служебные файлы, которые агент не правит: git (подмена .git в песочнице), конфигурация самого OpenCode.</summary>
    private static readonly string[] ProtectedEditPatterns =
        [".git", ".git/*", "opencode.json", "opencode.jsonc", ".opencode/*", "node_modules/*", ".venv/*"];

    internal static JsonObject EditRules(AppConfig cfg)
    {
        var rules = SecretRules(cfg);
        foreach (var pattern in ProtectedEditPatterns)
        {
            rules[pattern] = "deny";
            rules["*/" + pattern] = "deny";
        }
        return rules;
    }

    /// <summary>Файлы-секреты из настроек MCP: шаблоны сравниваются с путём относительно корня проекта.</summary>
    internal static JsonObject SecretRules(AppConfig cfg)
    {
        var rules = new JsonObject { ["*"] = "allow" };
        foreach (var raw in cfg.Mcp.SecretFilePatterns ?? [])
        {
            var pattern = raw?.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(pattern)) continue;
            rules[pattern] = "deny";
            if (!pattern.StartsWith('*')) rules["*/" + pattern] = "deny";
        }
        return rules;
    }

    /// <summary>
    /// Переопределение агента на один запуск (OPENCODE_CONFIG_CONTENT загружается после OPENCODE_CONFIG
    /// и сливается с ним): разрешение оболочки из параметров задачи вместо настройки.
    /// OPENCODE_PERMISSION здесь не годится — правила агента применяются после глобальных.
    /// </summary>
    internal static string ShellOverrideContent(AppConfig cfg, bool allowShell) =>
        new JsonObject
        {
            ["$schema"] = SchemaUrl,
            ["agent"] = new JsonObject
            {
                [EditAgent] = new JsonObject
                {
                    ["prompt"] = AgentPrompts.Edit(allowShell, cfg.Mcp.ExtraSystemPrompt),
                    ["permission"] = new JsonObject { ["bash"] = BashRules(allowShell) },
                },
            },
        }.ToJsonString();

    /// <summary>
    /// Переопределение агента правки на один запуск: разрешение оболочки (null — как в настройках) и/или температура
    /// (null — из настроек модели, ограничивается 0…1.5). null — переопределять нечего.
    /// </summary>
    internal static string? RunOverrideContent(AppConfig cfg, bool? allowShell, double? temperature)
    {
        if (allowShell is null && temperature is null) return null;
        var agent = new JsonObject();
        if (allowShell is { } shell)
        {
            agent["prompt"] = AgentPrompts.Edit(shell, cfg.Mcp.ExtraSystemPrompt);
            agent["permission"] = new JsonObject { ["bash"] = BashRules(shell) };
        }
        if (temperature is { } t) agent["temperature"] = Math.Round(Math.Clamp(t, 0, 1.5), 2);
        return new JsonObject
        {
            ["$schema"] = SchemaUrl,
            ["agent"] = new JsonObject { [EditAgent] = agent },
        }.ToJsonString();
    }

    /// <summary>Переменные окружения для запуска opencode с управляемой конфигурацией.</summary>
    public static IReadOnlyDictionary<string, string?> Environment(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return BuildEnvironment(cfg, interactive: false);
    }

    /// <summary>
    /// Полная изоляция от собственной установки OpenCode пользователя (его провайдеры, плагины, MCP-серверы —
    /// среди которых может быть и сам Offload): все папки XDG — внутри AppPaths.OpenCodeDir.
    /// Значение null — удалить унаследованную переменную.
    /// </summary>
    /// <param name="cfg">null — без ключа API (для «opencode --version»).</param>
    /// <param name="interactive">TUI: отдельные data/state (одновременные экземпляры на одной базе зависают, issue #29395).</param>
    internal static Dictionary<string, string?> BuildEnvironment(AppConfig? cfg, bool interactive)
    {
        var dir = AppPaths.OpenCodeDir;
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENCODE_CONFIG"] = AppPaths.OpenCodeConfigFile,
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "1",
            ["OPENCODE_DISABLE_MODELS_FETCH"] = "1",
            ["OPENCODE_DISABLE_SHARE"] = "1",
            ["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "1",
            ["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "1",
            ["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1",
            // CLAUDE.md и навыки Claude Code не нужны маленькой модели (AGENTS.md проекта загружается).
            ["OPENCODE_DISABLE_CLAUDE_CODE"] = "1",
            ["OPENCODE_PURE"] = "1",
            // opencode.json и .opencode/ из проекта (или созданные агентом) не должны подключать MCP-серверы, плагины и провайдеров.
            ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1",
            ["XDG_CONFIG_HOME"] = Path.Combine(dir, "config"),
            ["XDG_CACHE_HOME"] = CacheHome,
            ["XDG_DATA_HOME"] = Path.Combine(dir, interactive ? "data-tui" : "data"),
            ["XDG_STATE_HOME"] = Path.Combine(dir, interactive ? "state-tui" : "state"),
            [NestedEnvVar] = "1",
            // Системный прокси не должен перехватывать запросы к 127.0.0.1 (и к удалённому серверу в локальной сети/VPN).
            ["NO_PROXY"] = NoProxy(cfg is not null && cfg.IsRemote() ? RemoteHostName(cfg) : null),
            // Унаследованные настройки OpenCode пользователя нарушили бы изоляцию.
            ["OPENCODE_CONFIG_DIR"] = null,
            ["OPENCODE_CONFIG_CONTENT"] = null,
            ["OPENCODE_PERMISSION"] = null,
            ["OPENCODE_DB"] = null,
            ["OPENCODE_TEST_HOME"] = null,
            ["OPENCODE_EXPERIMENTAL"] = null,
            ["OPENCODE_CLIENT"] = null,
        };
        // Ключ основного сервера: свой локальный или ключ удалённого сервера (клиентский режим) — только в окружении процесса.
        if (cfg is not null) env[ApiKeyEnvVar] = cfg.MainEndpoint().ApiKey;
        return env;
    }

    /// <summary>Имя хоста удалённого сервера (без порта и скобок IPv6) для NO_PROXY.</summary>
    private static string? RemoteHostName(AppConfig cfg) =>
        Uri.TryCreate(cfg.MainEndpoint().BaseUrl, UriKind.Absolute, out var u) ? u.IdnHost : null;

    internal static string NoProxy(string? extraHost = null)
    {
        var local = "127.0.0.1,localhost,::1";
        if (!string.IsNullOrWhiteSpace(extraHost) && !extraHost.Contains(',')) local += "," + extraHost;
        var existing = System.Environment.GetEnvironmentVariable("NO_PROXY");
        if (string.IsNullOrWhiteSpace(existing)) return local;
        return existing.Contains("127.0.0.1", StringComparison.Ordinal) && (extraHost is null || existing.Contains(extraHost, StringComparison.OrdinalIgnoreCase))
            ? existing
            : local + "," + existing;
    }

    // ---- Глобальный конфиг пользователя (~/.config/opencode) ----

    /// <summary>
    /// Добавить провайдера Offload в глобальный конфиг собственного OpenCode пользователя. Правка точечная
    /// (<see cref="OpenCodeGlobalConfig"/>: комментарии и чужие ключи сохраняются, резервная копия, атомарная запись,
    /// путь — от корней IntegrationEnvironment). Агенты и модель по умолчанию пользователя не трогаются.
    /// <para>
    /// Ключ API записывается значением, а не ссылкой «{env:OFFLOAD_API_KEY}», как в изолированном конфиге: там
    /// переменную задаёт сам Offload при запуске opencode (<see cref="BuildEnvironment"/>), а собственный OpenCode
    /// пользователь запускает из своей консоли или IDE, где этой переменной нет — провайдер получил бы пустой ключ
    /// и llama-server отвечал бы 401. Ключ защищает только локальный сервер на 127.0.0.1 и уже хранится открытым
    /// текстом в config.json Offload в том же профиле.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Файл не разобран или не записан (текст — для пользователя).</exception>
    /// <param name="cfg">Настройки.</param>
    /// <param name="contextOverride">Фактический контекст слота запущенного сервера (режим «Авто» может его уменьшить).</param>
    public static void RegisterGlobal(AppConfig cfg, int? contextOverride = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var context = contextOverride is > 0 ? contextOverride.Value : EffectiveContext(cfg);
        // Ключ удалённого сервера (клиентский режим) открывает доступ к чужому компьютеру по сети — в открытый файл пользователя
        // он не пишется: провайдер ссылается на переменную окружения, которую пользователь задаёт сам.
        var key = cfg.IsRemote() ? "{env:" + ApiKeyEnvVar + "}" : cfg.Server.ApiKey;
        var r = OpenCodeGlobalConfig.RegisterProvider(ProviderId, BuildProvider(cfg, context, key), SchemaUrl);
        if (!r.Ok) throw new InvalidOperationException(r.Message);
    }

    /// <summary>Убрать провайдера Offload (и нашу модель по умолчанию, если пользователь её выбрал) из глобального конфига OpenCode.</summary>
    /// <exception cref="InvalidOperationException">Какой-то из файлов не удалось записать.</exception>
    public static void UnregisterGlobal()
    {
        var r = OpenCodeGlobalConfig.UnregisterProvider(ProviderId);
        if (!r.Ok) throw new InvalidOperationException(r.Message);
    }
}
