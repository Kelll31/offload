using System.Globalization;
using System.Text;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;

namespace Offload.Llama;

/// <summary>Построение аргументов командной строки llama-server.</summary>
public static class LlamaServerArgs
{
    /// <summary>
    /// Псевдоним модели в API (--alias): стабильное имя, которое используют OpenCode и MCP,
    /// не зависящее от имени файла. Например, «offload».
    /// </summary>
    public const string DefaultAlias = "offload";

    /// <summary>Контекст по умолчанию, если ни настройки, ни модель его не задают.</summary>
    public const int FallbackContext = 32768;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] CacheTypes = ["f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1"];

    /// <summary>Флаги, удалённые из llama-server (v0.4.1, #28334): с ними сервер не запускается.</summary>
    private static readonly HashSet<string> RemovedFlags = new(StringComparer.Ordinal)
    {
        "--no-mmap", "--mmap", "--mlock", "--direct-io", "--no-direct-io",
    };

    /// <summary>Флаги, которыми управляет Offload (адрес, ключ, модель, псевдоним) — из доп. аргументов не принимаются.</summary>
    private static readonly HashSet<string> ManagedFlags = new(StringComparer.Ordinal)
    {
        "-m", "--model", "--host", "--port", "--api-key", "--api-key-file", "-a", "--alias",
    };

    /// <summary>
    /// Собрать план запуска: -m, --host, --port, --alias, -ngl, -c, -np, --jinja,
    /// flash attention, тип KV-кэша, --n-cpu-moe, потоки, дополнительные аргументы пользователя.
    /// Контекст 0 в настройках → размещение «Авто» (если передано), иначе рекомендованный контекст модели.
    /// </summary>
    /// <param name="placement">
    /// Размещение, рассчитанное под оборудование (FitCalculator). Применяется только в режиме «Авто»
    /// (ContextSize = 0 и CpuMoeLayers = -1): явные -c и --n-cpu-moe вместо решения --fit. null — как раньше.
    /// Разделение по видеокартам (<see cref="ServerPlacement.Split"/>) и подобранные параметры (<see cref="ServerPlacement.Tuned"/>)
    /// применяются в любом режиме; одноимённые флаги из доп. аргументов пользователя важнее.
    /// </param>
    /// <remarks>Ключ API передаётся переменной окружения процесса (<see cref="BuildEnvironment"/>), не аргументом.</remarks>
    public static ServerLaunchPlan Build(AppConfig cfg, InstalledModel model, string serverExePath, ServerPlacement? placement = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExePath);
        if (string.IsNullOrWhiteSpace(model.FilePath))
            throw new ArgumentException(L.T("У модели не указан файл GGUF."), nameof(model));

        var s = cfg.Server ?? new ServerSettings();
        var parallel = Math.Clamp(s.Parallel, 1, ServerSettings.MaxParallel);
        var auto = placement is { ContextPerSlot: > 0 } && s.ContextSize <= 0 && s.CpuMoeLayers < 0 ? placement : null;
        var ctx = auto is not null ? ResolveContext(auto.ContextPerSlot, model) : ResolveContext(s.ContextSize, model);
        var total = (int)Math.Min(int.MaxValue, (long)ctx * parallel);
        var args = new List<string>();
        // Подобранные параметры и разделение по видеокартам уступают флагам из доп. аргументов пользователя.
        var tuned = placement?.Tuned;
        var extra = SanitizeExtra(SplitArgs(s.ExtraArgs ?? ""));

        args.AddRange(["-m", Path.GetFullPath(model.FilePath)]);
        // «Доступ из сети» (LanServer.IsActive): адрес сети вместо петлевого — только вместе с сетевым ключом.
        args.AddRange(["--host", s.ListenHost()]);
        // Порт всегда явно: значение по умолчанию llama-server скоро сменится (8080 → 9931).
        args.AddRange(["--port", s.Port.ToString(Inv)]);
        // Ключ — не в командной строке (её видит любая программа пользователя), а в окружении процесса: BuildEnvironment.
        // Не --api-key-file: llama-server не открывает файлы по не-ASCII путям (профиль «C:\Users\Иван»).
        args.AddRange(["--alias", DefaultAlias]);

        // -c явно, чтобы --fit не уменьшал контекст; -np явно (авто = 4 слота).
        args.AddRange(["-c", total.ToString(Inv)]);
        args.AddRange(["-np", parallel.ToString(Inv)]);
        // При явном -np общий KV-кэш выключен и слот получает c/np — включаем общий пул.
        // (LlamaClient.FromConfig учитывает это через UsesUnifiedKv: /props тогда сообщает весь буфер.)
        if (parallel > 1) args.Add("-kvu");

        // -fa всегда со значением: «голый» -fa съедает следующий аргумент.
        var fa = NormalizeFlashAttention(tuned?.FlashAttention ?? s.FlashAttention);
        args.AddRange(["-fa", fa]);
        if (NormalizeCacheType(tuned?.CacheType ?? s.CacheType) is { } cache)
        {
            args.AddRange(["-ctk", cache]);
            // Квантованный V-кэш требует flash attention.
            if (fa != "off" || !IsQuantized(cache)) args.AddRange(["-ctv", cache]);
        }
        if (tuned is { UBatch: > 0 } && !HasFlag(extra, UBatchFlags)) args.AddRange(["-ub", tuned.UBatch.ToString(Inv)]);
        if (tuned is { Batch: > 0 } && !HasFlag(extra, BatchFlags)) args.AddRange(["-b", tuned.Batch.ToString(Inv)]);

        // -1 — не передаём -ngl: слои по видеопамяти распределяет --fit.
        if (s.GpuLayers >= 0) args.AddRange(["-ngl", s.GpuLayers.ToString(Inv)]);

        var split = AddGpuSplit(args, placement?.Split, tuned, extra);

        int cpuMoe;
        if (auto is not null)
        {
            // «Авто» с оценкой: выгрузка экспертов по расчёту (0 — все эксперты в видеопамяти, флаг не нужен);
            // автоподбор мог сдвинуть её на несколько слоёв.
            cpuMoe = auto.CpuMoeLayers > 0 && (model.IsMoe || model.IsCustom) ? Math.Max(0, auto.CpuMoeLayers + (tuned?.CpuMoeDelta ?? 0)) : 0;
            if (cpuMoe > 0) args.AddRange(["--n-cpu-moe", cpuMoe.ToString(Inv)]);
        }
        else if (s.CpuMoeLayers > 0 && (model.IsMoe || model.IsCustom))
        {
            args.AddRange(["--n-cpu-moe", s.CpuMoeLayers.ToString(Inv)]);
            cpuMoe = s.CpuMoeLayers;
        }
        else
        {
            cpuMoe = s.CpuMoeLayers < 0 ? -1 : 0;
        }

        if (s.Threads > 0) args.AddRange(["-t", s.Threads.ToString(Inv)]);
        if (s.IdleUnloadMinutes > 0) args.AddRange(["--sleep-idle-seconds", (s.IdleUnloadMinutes * 60L).ToString(Inv)]);

        args.Add("--jinja");
        args.Add("--no-webui");
        args.AddRange(["--log-colors", "off"]);

        // Сэмплинг по умолчанию — рекомендации модели (запросы могут переопределить).
        var sp = model.Sampling ?? new SamplingSettings();
        AddNumber(args, "--temp", sp.Temperature);
        AddNumber(args, "--top-p", sp.TopP);
        args.AddRange(["--top-k", Math.Max(0, sp.TopK).ToString(Inv)]);
        AddNumber(args, "--min-p", sp.MinP);
        AddNumber(args, "--repeat-penalty", sp.RepeatPenalty);
        if (sp.PresencePenalty > 0) AddNumber(args, "--presence-penalty", sp.PresencePenalty);

        // MTP-спекуляция: только для моделей со встроенным MTP-слоем и одного слота.
        if ((tuned?.Mtp ?? s.EnableMtp) && model.HasMtp && parallel == 1)
            args.AddRange(["--spec-type", "draft-mtp", "--spec-draft-n-max", "2"]);

        args.AddRange(extra);

        return new ServerLaunchPlan(Path.GetFullPath(serverExePath), args, ctx, parallel, cpuMoe, DefaultAlias, auto, split, tuned);
    }

    private static readonly string[] UBatchFlags = ["-ub", "--ubatch-size"];
    private static readonly string[] BatchFlags = ["-b", "--batch-size"];
    private static readonly string[] DeviceFlags = ["-dev", "--device"];
    private static readonly string[] TensorSplitFlags = ["-ts", "--tensor-split"];
    private static readonly string[] MainGpuFlags = ["-mg", "--main-gpu"];
    private static readonly string[] SplitModeFlags = ["-sm", "--split-mode"];

    /// <summary>
    /// Видеокарты: --device (выбранные устройства llama.cpp), при нескольких — --tensor-split, --main-gpu, --split-mode.
    /// Пользователь задал --device сам — разделение Offload не передаётся целиком (иначе число долей не совпадёт с числом
    /// устройств); остальные флаги пропускаются по одному. Возвращает применённое разделение или null.
    /// </summary>
    private static GpuSplit? AddGpuSplit(List<string> args, GpuSplit? split, TunedProfile? tuned, IReadOnlyList<string> extra)
    {
        if (split is not { Devices.Count: > 0 } || HasFlag(extra, DeviceFlags)) return null;
        args.AddRange(["--device", split.DeviceArg]);
        if (!split.IsMulti) return split;
        if (!HasFlag(extra, SplitModeFlags)) args.AddRange(["-sm", NormalizeSplitMode(tuned?.SplitMode ?? split.SplitMode)]);
        if (!HasFlag(extra, TensorSplitFlags)) args.AddRange(["-ts", split.TensorSplitArg]);
        if (!HasFlag(extra, MainGpuFlags)) args.AddRange(["-mg", Math.Clamp(split.MainIndex, 0, split.Devices.Count - 1).ToString(Inv)]);
        return split;
    }

    internal static string NormalizeSplitMode(string? value) =>
        string.Equals(value?.Trim(), GpuSplit.RowMode, StringComparison.OrdinalIgnoreCase) ? GpuSplit.RowMode : GpuSplit.LayerMode;

    /// <summary>Флаг (в любом из написаний, также «--флаг=значение») есть среди аргументов.</summary>
    internal static bool HasFlag(IReadOnlyList<string> args, IReadOnlyCollection<string> names) =>
        args.Any(a => names.Contains(a.Split('=', 2)[0], StringComparer.Ordinal));

    /// <summary>Переменная окружения llama-server с ключом API (аналог --api-key, см. «llama-server --help»).</summary>
    public const string ApiKeyEnvVar = "LLAMA_API_KEY";

    /// <summary>
    /// Переменные окружения процесса llama-server. Значение null — убрать унаследованную переменную
    /// (иначе чужой LLAMA_API_KEY из окружения пользователя подменил бы ключ или включил его без ведома Offload).
    /// </summary>
    /// <param name="cfg">Настройки.</param>
    /// <param name="main">
    /// Основной сервер: в режиме «Доступ из сети» — локальный и сетевой ключи через запятую (<see cref="LanServer.ApiKeysForServer"/>;
    /// нерасшифровываемый сетевой ключ — InvalidOperationException). Вспомогательные серверы (127.0.0.1) — ключ роли
    /// (<see cref="AuxApiKey"/>).
    /// </param>
    /// <param name="role">Роль вспомогательного сервера (для main = false).</param>
    public static IReadOnlyDictionary<string, string?> BuildEnvironment(AppConfig cfg, bool main = true, ModelRole role = ModelRole.Fast)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var key = main && cfg.Server is { } server ? LanServer.ApiKeysForServer(server) : AuxApiKey(cfg, role);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ApiKeyEnvVar] = string.IsNullOrEmpty(key) ? null : key,
        };
    }

    /// <summary>
    /// Ключ API вспомогательного сервера роли: у автодополнения — свой (<see cref="AutocompleteSettings.ApiKey"/>: он попадает
    /// в файлы IDE открытым текстом), у остальных ролей — локальный ключ основного сервера. Ключа автодополнения нет —
    /// InvalidOperationException: запускать сервер без ключа или с ключом основного сервера нельзя.
    /// </summary>
    public static string? AuxApiKey(AppConfig cfg, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (role != ModelRole.Fim) return cfg.Server?.ApiKey?.Trim();
        var key = cfg.Autocomplete?.ApiKey?.Trim();
        if (string.IsNullOrEmpty(key) || string.Equals(key, cfg.Server?.ApiKey?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(L.T("Не задан отдельный ключ API сервера автодополнения — перезапустите Offload, он будет создан."));
        return key;
    }

    /// <summary>
    /// Слоты сервера делят общий KV-кэш: Build добавляет -kvu при нескольких слотах,
    /// если пользователь не выключил его доп. аргументом (-no-kvu / --no-kv-unified).
    /// </summary>
    public static bool UsesUnifiedKv(ServerSettings? s)
    {
        if (s is null || Math.Clamp(s.Parallel, 1, ServerSettings.MaxParallel) <= 1) return false;
        var extra = SplitArgs(s.ExtraArgs ?? "");
        return !extra.Any(a => a is "-no-kvu" or "--no-kv-unified");
    }

    /// <summary>Контекст одного слота: настройки → рекомендация модели → 32768; не больше родного контекста.</summary>
    internal static int ResolveContext(int configured, InstalledModel model)
    {
        var ctx = configured > 0 ? configured : model.RecommendedContext > 0 ? model.RecommendedContext : FallbackContext;
        ctx = Math.Max(ctx, 512);
        if (model.NativeContext > 0) ctx = Math.Min(ctx, model.NativeContext);
        return ctx;
    }

    internal static string NormalizeFlashAttention(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" or "enabled" => "on",
        "off" or "false" or "0" or "no" or "disabled" => "off",
        _ => "auto",
    };

    internal static string? NormalizeCacheType(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return CacheTypes.Contains(v) ? v : null;
    }

    private static bool IsQuantized(string cacheType) => cacheType is not ("f32" or "f16" or "bf16");

    private static void AddNumber(List<string> args, string flag, double value)
    {
        if (double.IsFinite(value)) args.AddRange([flag, value.ToString("0.######", Inv)]);
    }

    /// <summary>Убрать удалённые и управляемые Offload флаги, дописать значение к «голому» -fa.</summary>
    internal static IReadOnlyList<string> SanitizeExtra(IReadOnlyList<string> extra)
    {
        var result = new List<string>(extra.Count);
        for (var i = 0; i < extra.Count; i++)
        {
            var a = extra[i];
            var flag = a.Split('=', 2)[0];
            if (RemovedFlags.Contains(flag))
            {
                Log.Warn("llama", $"Дополнительный аргумент {a} удалён из llama.cpp и пропущен");
                continue;
            }
            if (ManagedFlags.Contains(flag))
            {
                Log.Warn("llama", $"Дополнительный аргумент {flag} пропущен: его задаёт Offload");
                if (!a.Contains('=') && i + 1 < extra.Count && !extra[i + 1].StartsWith('-')) i++;
                continue;
            }
            result.Add(a);
            if (a is "-fa" or "--flash-attn" && (i + 1 >= extra.Count || extra[i + 1].StartsWith('-')))
                result.Add("on");
        }
        return result;
    }

    /// <summary>Разбор строки дополнительных аргументов с учётом кавычек.</summary>
    /// <remarks>
    /// Разделители — пробельные символы. "…" и '…' группируют (кавычки убираются); внутри "…" \" — буквальная кавычка;
    /// '…' берётся как есть (удобно для JSON: --chat-template-kwargs '{"enable_thinking":false}'). "" — пустой аргумент.
    /// </remarks>
    public static IReadOnlyList<string> SplitArgs(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;
        var sb = new StringBuilder();
        var inToken = false;
        var i = 0;
        var s = commandLine;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    inToken = false;
                }
                i++;
                continue;
            }
            inToken = true;
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    sb.Append(s[i++]);
                }
                i++; // закрывающая кавычка (или конец строки)
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'') sb.Append(s[i++]);
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        if (inToken) result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// Есть незакрытая кавычка: <see cref="SplitArgs"/> молча доберёт аргумент до конца строки,
    /// и llama-server получит не то, что задумано. Правила кавычек те же, что в <see cref="SplitArgs"/>.
    /// </summary>
    public static bool HasUnclosedQuote(string? commandLine)
    {
        var s = commandLine ?? "";
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i++];
            if (c is not ('"' or '\'')) continue;
            while (i < s.Length && s[i] != c)
                i += c == '"' && s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '"' ? 2 : 1;
            if (i >= s.Length) return true;
            i++; // закрывающая кавычка
        }
        return false;
    }

    /// <summary>Командная строка для журнала: ключ API скрыт.</summary>
    internal static string Describe(ServerLaunchPlan plan)
    {
        var sb = new StringBuilder(Quote(plan.ExePath));
        for (var i = 0; i < plan.Arguments.Count; i++)
        {
            var a = plan.Arguments[i];
            var masked = i > 0 && plan.Arguments[i - 1] == "--api-key" ? "***" : a;
            sb.Append(' ').Append(Quote(masked));
        }
        return sb.ToString();
    }

    private static string Quote(string a) => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;
}
