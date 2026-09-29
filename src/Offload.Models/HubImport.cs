using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Offload.Core.Config;
using Offload.Core.Hardware;

namespace Offload.Models;

/// <summary>Всё, что нужно знать о репозитории GGUF, чтобы добавить модель: ревизия, варианты квантизации, заголовок.</summary>
/// <param name="Revision">Закреплённая ревизия (commit SHA): список файлов, заголовок и загрузка — только с неё.</param>
/// <param name="HeaderQuant">Квантизация, из файла которой прочитан заголовок (параметры архитектуры у всех одинаковы).</param>
public sealed record HubModelDetails(HfRepoInfo Info, string Revision, IReadOnlyList<HfQuantOption> Quants, GgufInfo Header, string HeaderQuant);

/// <summary>Вариант квантизации с оценкой на этом компьютере (null — оборудование неизвестно).</summary>
public sealed record HubQuantFit(HfQuantOption Option, FitResult? Fit)
{
    public string Quant => Option.Quant;

    public long Size => Option.TotalSize;
}

/// <summary>
/// Добавление модели, найденной на Hugging Face: сведения о репозитории на закреплённой ревизии, заголовок GGUF
/// Range-запросами (без загрузки весов), запись каталога (<see cref="CatalogModel"/>, <c>FromHub = true</c>) и выбор
/// кванта по оценке <see cref="FitCalculator"/>.
/// </summary>
public static partial class HubImport
{
    /// <summary>Префикс идентификаторов моделей с Hugging Face (не пересекается с каталогом).</summary>
    public const string IdPrefix = "hf-";

    /// <summary>Рабочий контекст по умолчанию для чат-моделей (как у своих GGUF-файлов).</summary>
    internal const int DefaultChatContext = 32768;

    /// <summary>Рабочий контекст для эмбеддингов и реранкеров.</summary>
    internal const int DefaultAuxContext = 8192;

    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>
    /// Ревизия (последний коммит), файлы GGUF на ней и заголовок меньшего варианта. Ничего не скачивает целиком.
    /// </summary>
    public static async Task<HubModelDetails> LoadDetailsAsync(string repo, CancellationToken ct = default)
    {
        var info = await HfClient.GetRepoInfoAsync(repo, ct).ConfigureAwait(false);
        // Никогда не «main»: без закреплённой ревизии размеры и SHA-256 могли бы разойтись с файлами при загрузке.
        if (info.Sha is not { Length: 40 } revision)
            throw new ModelException(L.F("Hugging Face не сообщил ревизию репозитория {0} — модель нельзя закрепить.", info.Repo));
        var files = await HfClient.ListFilesAsync(info.Repo, revision, ct).ConfigureAwait(false);
        var quants = HfClient.GroupQuants(files);
        if (quants.Count == 0)
            throw new ModelException(L.F("В репозитории {0} нет файлов GGUF с контрольными суммами SHA-256.", info.Repo));
        var probe = quants[0]; // меньший файл: заголовок тот же, а размер не важен — читается только начало
        var header = await HfClient.ReadGgufHeaderAsync(info.Repo, revision, probe.Files[0], ct).ConfigureAwait(false);
        return new HubModelDetails(info, revision, quants, header, probe.Quant);
    }

    /// <summary>Идентификатор записи: «hf-» + владелец и название репозитория (строчные латиница/цифры/.-_).</summary>
    public static string IdFor(string repo)
    {
        var sb = new StringBuilder(IdPrefix);
        foreach (var ch in (repo ?? "").ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-') sb.Append(ch);
            else if (ch == '/') sb.Append("--");
        }
        var id = sb.ToString();
        return id.Length > 150 ? id[..150] : id;
    }

    /// <summary>
    /// Запись каталога для репозитория. <paramref name="defaultQuant"/> — квант по умолчанию (первый в списке, по нему
    /// оценивается модель в общем списке); null — Q4_K_M или похожий средний вариант. Остальные — по размеру, от большего.
    /// </summary>
    public static CatalogModel BuildEntry(HubModelDetails details, string? defaultQuant = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        var h = details.Header;
        var info = details.Info;
        var options = details.Quants;
        var first = options.FirstOrDefault(o => string.Equals(o.Quant, defaultQuant, StringComparison.OrdinalIgnoreCase)) ?? DefaultOption(options);

        var role = info.PipelineTag switch
        {
            "feature-extraction" or "sentence-similarity" => ModelKind.Embed,
            "text-ranking" => ModelKind.Rerank,
            _ => ModelKind.Chat,
        };
        var native = h.ContextLength > 0 ? h.ContextLength : 4096;
        var working = Math.Min(native, role == ModelKind.Chat ? DefaultChatContext : DefaultAuxContext);
        var (paramsB, activeB) = ParseSizeLabel(h.SizeLabel);
        // У MoE без метки «-A…» число активных параметров неизвестно (0 — FitCalculator оценит буфер по размеру файла).
        if (!h.IsMoe && activeB <= 0) activeB = paramsB;

        var files = options.Select(o => new CatalogFile(o.Quant, o.Files[0].Path, o.Files[0].Size, o.Files[0].Sha256,
            Parts: o.Files.Count > 1 ? o.Files.Skip(1).ToArray() : null)).ToList();
        var quants = new List<string> { first.Quant };
        quants.AddRange(options.Where(o => o != first).OrderByDescending(o => o.TotalSize).Select(o => o.Quant));

        var license = string.IsNullOrWhiteSpace(info.License) ? null : info.License.Trim();
        var template = h.ChatTemplate ?? "";
        return new CatalogModel(
            Id: IdFor(info.Repo),
            DisplayName: DisplayNameFor(info.Repo),
            Description: DescriptionRu(info.Repo, license, info.IsGated),
            Repo: info.Repo,
            Quants: quants,
            ApproxSizeBytes: first.TotalSize,
            ParamsB: paramsB,
            ActiveParamsB: activeB,
            IsMoe: h.IsMoe,
            NativeContext: native,
            DefaultContext: working,
            Kv: h.ToKvSpec(),
            GoodToolCalling: role == ModelKind.Chat && ModelManager.ToolCallingFrom(h),
            Sampling: new SamplingSettings(),
            License: license ?? "",
            MinVramGb: (int)Math.Ceiling((first.TotalSize + GiB) / (double)GiB),
            IsReasoning: template.Contains("<think>", StringComparison.Ordinal) || template.Contains("enable_thinking", StringComparison.Ordinal),
            ReleaseDate: info.LastModified?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Tags: info.IsGated ? ["huggingface", "gated"] : ["huggingface"],
            Revision: details.Revision,
            Files: files,
            Reasoning: ModelManager.ReasoningFrom(h),
            HasMtp: h.NextNPredictLayers > 0,
            Architecture: h.Architecture,
            Priority: 1000,
            BlockCount: h.BlockCount,
            DescriptionEn: DescriptionEn(info.Repo, license, info.IsGated),
            Role: role,
            FromHub: true,
            Gated: info.IsGated);
    }

    /// <summary>Та же запись с другим квантом по умолчанию (первым в списке).</summary>
    public static CatalogModel WithDefaultQuant(CatalogModel entry, string quant)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.FindFile(quant) is not { } file) throw new ModelException(L.F("У модели {0} нет квантизации {1}.", entry.LocalizedDisplayName, quant));
        var quants = new List<string> { file.Quant };
        quants.AddRange(entry.Quants.Where(q => !string.Equals(q, file.Quant, StringComparison.OrdinalIgnoreCase)));
        return entry with
        {
            Quants = quants,
            ApproxSizeBytes = file.TotalSize,
            MinVramGb = (int)Math.Ceiling((file.TotalSize + GiB) / (double)GiB),
        };
    }

    /// <summary>Оценка каждого варианта при текущих настройках сервера (как в списке моделей и в режиме «Авто»).</summary>
    public static IReadOnlyList<HubQuantFit> Evaluate(CatalogModel entry, HardwareInfo? hw, ServerSettings server)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(server);
        return (entry.Files ?? [])
            .Select(f => new HubQuantFit(
                new HfQuantOption(f.Quant, [new HfFile(f.Path, f.Size, f.Sha256), .. f.Parts ?? []]),
                hw is null ? null : ServerFit.Evaluate(new FitModel(entry, f.TotalSize, entry.DefaultContext), hw, server)))
            .OrderByDescending(x => x.Size)
            .ToList();
    }

    /// <summary>
    /// Рекомендуемый квант: наибольший из тех, что работают на лучшем достижимом уровне размещения (целиком в видеопамяти →
    /// MoE с выгрузкой экспертов → частично на GPU → процессор) с рабочим контекстом не меньше 16K. Разумные кванты
    /// (3–8 бит) предпочтительнее: 2-битные теряют качество, F16/BF16 почти не лучше Q8_0, но вдвое больше.
    /// null — ни один вариант не поместится (или оборудование неизвестно).
    /// </summary>
    public static string? Recommend(IReadOnlyList<HubQuantFit> fits)
    {
        ArgumentNullException.ThrowIfNull(fits);
        var usable = fits.Where(f => f.Fit is { Usable: true }).ToList();
        if (usable.Count == 0) return null;
        var sane = usable.Where(f => f.Fit!.ContextSize >= FitCalculator.MinAgentContext).ToList();
        if (sane.Count > 0) usable = sane;
        var reasonable = usable.Where(f => QuantBits(f.Quant) is >= 3 and <= 8).ToList();
        if (reasonable.Count > 0) usable = reasonable;
        var best = usable.Min(f => f.Fit!.Level);
        return usable.Where(f => f.Fit!.Level == best).OrderByDescending(f => f.Size).First().Quant;
    }

    /// <summary>Примерная разрядность кванта по метке: Q4_K_M → 4, IQ3_XXS → 3, BF16 → 16; неизвестно — 4.</summary>
    internal static int QuantBits(string quant)
    {
        var q = (quant ?? "").ToUpperInvariant();
        if (q.Contains("F32", StringComparison.Ordinal)) return 32;
        if (q.Contains("F16", StringComparison.Ordinal)) return 16;
        if (q.Contains("MXFP4", StringComparison.Ordinal)) return 4;
        var m = BitsRegex().Match(q);
        return m.Success ? m.Groups["b"].Value[0] - '0' : 4;
    }

    /// <summary>Квант по умолчанию без оценки оборудования: Q4_K_M, иначе крупнейший 4-битный, иначе средний.</summary>
    private static HfQuantOption DefaultOption(IReadOnlyList<HfQuantOption> options)
    {
        if (options.Count == 0) throw new ModelException(L.T("В репозитории нет подходящих файлов GGUF."));
        return options.FirstOrDefault(o => string.Equals(o.Quant, "Q4_K_M", StringComparison.OrdinalIgnoreCase))
               ?? options.Where(o => QuantBits(o.Quant) == 4).MaxBy(o => o.TotalSize)
               ?? options[options.Count / 2];
    }

    /// <summary>«1.5B» → (1.5, 1.5); «30B-A3B» → (30, 3); «8x7B» → (56, 0); «500M» → (0.5, 0.5). Нет данных — (0, 0).</summary>
    internal static (double Params, double Active) ParseSizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return (0, 0);
        var m = SizeLabelRegex().Match(label.Trim());
        if (!m.Success) return (0, 0);
        static double Value(string num, string unit) =>
            double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture) * (unit.ToUpperInvariant() switch { "T" => 1000, "M" => 0.001, "K" => 0.000001, _ => 1 });
        var total = Value(m.Groups["n"].Value, m.Groups["u"].Value);
        if (m.Groups["x"].Success) total *= int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture);
        var active = m.Groups["a"].Success ? Value(m.Groups["a"].Value, m.Groups["au"].Value) : m.Groups["x"].Success ? 0 : total;
        return (Math.Round(total, 3), Math.Round(active, 3));
    }

    /// <summary>«bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF» → «Qwen2.5-Coder-1.5B-Instruct (bartowski)».</summary>
    internal static string DisplayNameFor(string repo)
    {
        var slash = repo.IndexOf('/');
        var owner = slash > 0 ? repo[..slash] : "";
        var name = slash > 0 ? repo[(slash + 1)..] : repo;
        name = GgufSuffixRegex().Replace(name, "");
        if (name.Length == 0) name = repo;
        return owner.Length > 0 ? $"{name} ({owner})" : name;
    }

    // Описания хранятся в записи на обоих языках (как в каталоге: description / descriptionEn).
    private static string DescriptionRu(string repo, string? license, bool gated)
    {
        var text = $"Модель найдена на Hugging Face ({repo}) и не проверена мейнтейнером Offload: параметры взяты из заголовка GGUF, качество ответов и вызова инструментов не гарантируется."; // l10n-ignore: данные записи, английский вариант — DescriptionEn
        if (gated) text += " Репозиторий закрытый: для загрузки нужен токен Hugging Face и принятые условия модели."; // l10n-ignore: данные записи
        return text + $" Лицензия: {license ?? "не указана в карточке модели"}."; // l10n-ignore: данные записи
    }

    private static string DescriptionEn(string repo, string? license, bool gated)
    {
        var text = $"Model found on Hugging Face ({repo}) and not verified by the Offload maintainer: parameters come from the GGUF header, answer and tool-calling quality is not guaranteed.";
        if (gated) text += " The repository is gated: downloading requires a Hugging Face token and accepted model terms.";
        return text + $" License: {license ?? "not specified in the model card"}.";
    }

    [GeneratedRegex(@"(?:^|[^A-Z])I?Q(?<b>\d)|TQ(?<b>\d)")]
    private static partial Regex BitsRegex();

    [GeneratedRegex(@"^(?:(?<x>\d+)x)?(?<n>\d+(?:\.\d+)?)(?<u>[TBMK])(?:-A(?<a>\d+(?:\.\d+)?)(?<au>[TBMK]))?", RegexOptions.IgnoreCase)]
    private static partial Regex SizeLabelRegex();

    [GeneratedRegex(@"[-_.]?gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex GgufSuffixRegex();
}
