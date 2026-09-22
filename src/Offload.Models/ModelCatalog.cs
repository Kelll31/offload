using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Offload.Core.Config;

namespace Offload.Models;

/// <summary>Параметры архитектуры для оценки размера KV-кэша.</summary>
public sealed record KvSpec(
    /// <summary>Число слоёв с KV-кэшем (для гибридных DeltaNet-моделей — только слои полного внимания).</summary>
    int Layers,
    int KvHeads,
    int HeadDim,
    /// <summary>Сколько слоёв используют полное внимание (для моделей со скользящим окном); 0 — все.</summary>
    int FullAttentionLayers = 0,
    /// <summary>Размер скользящего окна (SWA) в токенах; 0 — нет.</summary>
    int SlidingWindow = 0,
    /// <summary>Фиксированное состояние на один слот (рекуррентные слои DeltaNet), байт.</summary>
    long RecurrentStateBytes = 0);

/// <summary>Конкретный GGUF-файл одной квантизации.</summary>
public sealed record CatalogFile(
    string Quant,
    /// <summary>Путь внутри репозитория (для разбитых моделей — первый шард).</summary>
    string Path,
    long Size,
    string? Sha256,
    /// <summary>Дополнительные шарды (кроме первого), если модель разбита.</summary>
    IReadOnlyList<string>? ExtraShards = null,
    /// <summary>Раскладка MoE именно этого файла (эксперты разных квантизаций весят по-разному); null — как у модели.</summary>
    MoeSpec? Moe = null,
    /// <summary>В файле есть тензоры типов IQ* (сборки CUDA 13.2 выдают на них бессмыслицу).</summary>
    bool IqTensors = false);

/// <summary>Раскладка MoE для оценки выгрузки экспертов на ЦП.</summary>
public sealed record MoeSpec(int MoeLayers, long ExpertBytesPerLayer, long NonExpertBytes);

/// <summary>Модель из встроенного каталога (catalog.json).</summary>
public sealed record CatalogModel(
    string Id,
    string DisplayName,
    /// <summary>Краткое описание для пользователя на русском.</summary>
    string Description,
    /// <summary>Репозиторий Hugging Face с GGUF-файлами.</summary>
    string Repo,
    /// <summary>Квантизации в порядке предпочтения (первая — по умолчанию).</summary>
    IReadOnlyList<string> Quants,
    /// <summary>Размер квантизации по умолчанию, байт.</summary>
    long ApproxSizeBytes,
    double ParamsB,
    /// <summary>Активных параметров для MoE (млрд), для dense = ParamsB.</summary>
    double ActiveParamsB,
    bool IsMoe,
    int NativeContext,
    int DefaultContext,
    KvSpec Kv,
    bool GoodToolCalling,
    SamplingSettings Sampling,
    string License,
    /// <summary>Минимум видеопамяти (ГБ) для комфортной работы полностью на GPU.</summary>
    int MinVramGb,
    /// <summary>Модель «думающая» по умолчанию (выдаёт рассуждения).</summary>
    bool IsReasoning = false,
    string? ReleaseDate = null,
    IReadOnlyList<string>? Tags = null,
    /// <summary>Закреплённая ревизия (commit SHA) репозитория; null — main.</summary>
    string? Revision = null,
    /// <summary>Точные файлы по квантизациям (размеры и SHA-256 из tree API).</summary>
    IReadOnlyList<CatalogFile>? Files = null,
    MoeSpec? Moe = null,
    /// <summary>Как отключать рассуждения для быстрых разовых задач.</summary>
    ReasoningControl Reasoning = ReasoningControl.None,
    /// <summary>В GGUF встроен MTP-слой (ускорение --spec-type draft-mtp).</summary>
    bool HasMtp = false,
    /// <summary>Минимум ОЗУ (ГБ) при выгрузке экспертов MoE на ЦП; 0 — не требуется.</summary>
    int MinRamGb = 0,
    /// <summary>Архитектура GGUF (qwen35, qwen35moe, qwen3next, gpt-oss, mistral3…).</summary>
    string? Architecture = null,
    /// <summary>Подходит для работы только на процессоре.</summary>
    bool CpuFriendly = false,
    /// <summary>Порядок в списке (меньше — выше).</summary>
    int Priority = 100,
    /// <summary>Всего слоёв (block_count из GGUF, включая MTP) — для оценки -ngl; 0 — неизвестно.</summary>
    int BlockCount = 0)
{
    /// <summary>Квантизация по умолчанию.</summary>
    public string DefaultQuant => Quants.Count > 0 ? Quants[0] : Files is { Count: > 0 } f ? f[0].Quant : "";

    /// <summary>Файл квантизации из каталога (без учёта регистра); null — нет в каталоге.</summary>
    public CatalogFile? FindFile(string? quant)
    {
        if (Files is null) return null;
        quant ??= DefaultQuant;
        return Files.FirstOrDefault(f => string.Equals(f.Quant, quant, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Файл квантизации по умолчанию.</summary>
    public CatalogFile? DefaultFile => FindFile(DefaultQuant);

    /// <summary>Есть ли тег (без учёта регистра).</summary>
    public bool HasTag(string tag) => Tags?.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) == true;
}

/// <summary>Встроенный каталог рекомендованных моделей для программирования.</summary>
public static partial class ModelCatalog
{
    internal const string ResourceName = "Offload.Models.catalog.json";

    // Lazy по умолчанию потокобезопасен (ExecutionAndPublication): каталог разбирается один раз.
    private static readonly Lazy<IReadOnlyList<CatalogModel>> Lazy = new(LoadEmbedded);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Все модели каталога, отсортированные по Priority.</summary>
    public static IReadOnlyList<CatalogModel> All => Lazy.Value;

    public static CatalogModel? Find(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<CatalogModel> LoadEmbedded()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Встроенный каталог моделей не найден в сборке Offload.Models.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Разобрать и проверить JSON каталога ({"models": [...]}). Ошибки — InvalidDataException.</summary>
    public static IReadOnlyList<CatalogModel> Parse(string json)
    {
        CatalogDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Каталог моделей повреждён: {ex.Message}", ex);
        }
        var models = doc?.Models ?? throw new InvalidDataException("Каталог моделей пуст.");
        Validate(models);
        // OrderBy устойчив: при равном Priority сохраняется порядок файла.
        return models.OrderBy(m => m.Priority).ToArray();
    }

    private static void Validate(IReadOnlyList<CatalogModel> models)
    {
        if (models.Count == 0) throw new InvalidDataException("Каталог моделей пуст.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            string Err(string what) => $"Каталог моделей: у модели «{m.Id}» {what}.";
            if (m is null) throw new InvalidDataException("Каталог моделей: пустая запись.");
            if (string.IsNullOrWhiteSpace(m.Id)) throw new InvalidDataException("Каталог моделей: запись без id.");
            if (!ids.Add(m.Id)) throw new InvalidDataException(Err("повторяющийся id"));
            if (string.IsNullOrWhiteSpace(m.DisplayName)) throw new InvalidDataException(Err("нет названия"));
            if (string.IsNullOrWhiteSpace(m.Repo) || m.Repo.Count(c => c == '/') != 1) throw new InvalidDataException(Err("неверный репозиторий"));
            if (m.Revision is not null && !Sha1Regex().IsMatch(m.Revision)) throw new InvalidDataException(Err("неверная ревизия"));
            if (m.Quants is not { Count: > 0 }) throw new InvalidDataException(Err("нет квантизаций"));
            if (m.Files is not { Count: > 0 }) throw new InvalidDataException(Err("нет файлов"));
            if (m.Kv is null || m.Kv.Layers <= 0 || m.Kv.KvHeads <= 0 || m.Kv.HeadDim <= 0) throw new InvalidDataException(Err("неверные параметры KV-кэша"));
            if (m.Sampling is null) throw new InvalidDataException(Err("нет параметров сэмплирования"));
            if (m.NativeContext <= 0 || m.DefaultContext <= 0 || m.DefaultContext > m.NativeContext) throw new InvalidDataException(Err("неверный контекст"));
            if (m.IsMoe && (m.Moe is null || m.Moe.MoeLayers <= 0 || m.Moe.ExpertBytesPerLayer <= 0)) throw new InvalidDataException(Err("нет раскладки MoE"));
            foreach (var f in m.Files)
            {
                if (f is null || string.IsNullOrWhiteSpace(f.Quant) || string.IsNullOrWhiteSpace(f.Path) || f.Size <= 0)
                    throw new InvalidDataException(Err("неполное описание файла"));
                if (!f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || f.Path.Contains("..") || f.Path.StartsWith('/'))
                    throw new InvalidDataException(Err($"недопустимый путь файла {f.Path}"));
                if (f.Sha256 is not null && !Sha256Regex().IsMatch(f.Sha256))
                    throw new InvalidDataException(Err($"неверный SHA-256 файла {f.Path}"));
            }
            if (m.Files.GroupBy(f => f.Quant, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new InvalidDataException(Err("повторяющаяся квантизация"));
            if (m.Quants.FirstOrDefault(q => m.FindFile(q) is null) is { } noFile)
                throw new InvalidDataException(Err($"нет файла для квантизации {noFile}"));
            if (m.FindFile(m.DefaultQuant) is not { } def) throw new InvalidDataException(Err("нет файла квантизации по умолчанию"));
            if (m.ApproxSizeBytes != def.Size) throw new InvalidDataException(Err("размер не совпадает с файлом по умолчанию"));
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha1Regex();

    private sealed record CatalogDocument(IReadOnlyList<CatalogModel>? Models);
}
