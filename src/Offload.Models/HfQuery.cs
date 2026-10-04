using System.Text.RegularExpressions;

namespace Offload.Models;

/// <summary>Ссылка на репозиторий Hugging Face, введённая вместо названия (с квантом, если он указан).</summary>
/// <param name="Repo">Владелец и название: «owner/name».</param>
/// <param name="Quant">Квантизация из ссылки («:Q4_K_M» или имя файла .gguf); null — не указана.</param>
public sealed record HfReference(string Repo, string? Quant);

/// <summary>
/// Разбор того, что пользователь вводит в строку поиска моделей: ссылка на страницу, «owner/name», «hf.co/owner/name:Q4_K_M»
/// (запись Ollama/llama.cpp), команда «huggingface-cli download …» — и многословный поиск по названию.
/// </summary>
public static partial class HfQuery
{
    /// <summary>
    /// Распознать ссылку или «owner/name». Обычные слова («qwen coder») и пути вида «a/b/c» не считаются ссылкой — null.
    /// </summary>
    public static HfReference? ParseReference(string? input)
    {
        var text = (input ?? "").Trim().Trim('"', '\'');
        if (text.Length == 0 || text.Length > 400) return null;

        // «huggingface-cli download owner/name [file]» и «hf download …».
        var cli = CliRegex().Match(text);
        if (cli.Success)
        {
            var quantFromFile = QuantFromFileName(cli.Groups["file"].Value);
            return Make(cli.Groups["repo"].Value, quantFromFile);
        }

        var url = UrlRegex().Match(text);
        // Страницы датасетов, спейсов, документации и т. п. — не репозиторий модели.
        if (url.Success && NotModelSegments.Contains(url.Groups["repo"].Value.Split('/')[0])) return null;
        if (url.Success)
        {
            var rest = url.Groups["rest"].Value;
            // …/owner/name/resolve/main/file.gguf, …/blob/main/file.gguf — квант из имени файла.
            var file = rest.Contains(".gguf", StringComparison.OrdinalIgnoreCase)
                ? Uri.UnescapeDataString(rest[(rest.LastIndexOf('/') + 1)..])
                : null;
            var tag = url.Groups["tag"].Success ? url.Groups["tag"].Value : null;
            return Make(url.Groups["repo"].Value, tag ?? QuantFromFileName(file));
        }

        var plain = PlainRegex().Match(text);
        return plain.Success
            ? Make(plain.Groups["repo"].Value, plain.Groups["tag"].Success ? plain.Groups["tag"].Value : null)
            : null;
    }

    private static readonly HashSet<string> NotModelSegments = new(StringComparer.OrdinalIgnoreCase)
        { "datasets", "spaces", "docs", "collections", "papers", "blog", "models", "organizations", "settings", "api" };

    private static HfReference? Make(string repo, string? quant)
    {
        if (!RepoRegex().IsMatch(repo)) return null;
        return new HfReference(repo, string.IsNullOrWhiteSpace(quant) ? null : quant.Trim());
    }

    /// <summary>Метка кванта из имени файла: «Qwen3-Q4_K_M.gguf» → «Q4_K_M»; null — метки нет.</summary>
    internal static string? QuantFromFileName(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var m = QuantTagRegex().Match(file);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Слова запроса: разделители «пробел - _ . /» равнозначны, регистр не важен, пустые слова отброшены.
    /// «Qwen3-Coder 30B» → [qwen3, coder, 30b].
    /// </summary>
    public static IReadOnlyList<string> Tokens(string? query) =>
        TokenSplitRegex().Split((query ?? "").ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToArray();

    /// <summary>Содержит ли название репозитория все слова запроса (в любом порядке, без учёта разделителей).</summary>
    public static bool MatchesAll(string repo, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return true;
        var haystack = repo.ToLowerInvariant();
        var compact = TokenSplitRegex().Replace(haystack, "");
        return tokens.All(t => haystack.Contains(t, StringComparison.Ordinal) || compact.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>
    /// Запрос для хаба: хаб ищет подстроку целиком, поэтому «qwen coder» ничего не находит, а «qwen» — слишком много.
    /// Берётся самое длинное слово; остальные потом проверяет <see cref="MatchesAll"/>.
    /// </summary>
    public static string HubQuery(IReadOnlyList<string> tokens) =>
        tokens.Count == 0 ? "" : tokens.OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).First();

    /// <summary>
    /// Поиск по названию с учётом нескольких слов: сначала запрос как есть (хаб ранжирует точное совпадение выше),
    /// если слов несколько и результат пуст или не содержит всех слов — запрос по самому длинному слову
    /// с отбором по остальным. Порядок хаба сохраняется.
    /// </summary>
    public static async Task<IReadOnlyList<HfRepoInfo>> SearchAsync(string query, HfSort sort, int limit, CancellationToken ct = default)
    {
        var tokens = Tokens(query);
        var direct = await HfClient.SearchAsync(query, sort, limit, ct).ConfigureAwait(false);
        if (tokens.Count < 2) return direct;

        var matching = direct.Where(r => MatchesAll(r.Repo, tokens)).ToList();
        if (matching.Count >= Math.Min(limit, 5)) return matching;

        // Берём с запасом: после отбора по остальным словам останется часть.
        var wide = await HfClient.SearchAsync(HubQuery(tokens), sort, Math.Min(100, limit * 3), ct).ConfigureAwait(false);
        var merged = matching.Concat(wide.Where(r => MatchesAll(r.Repo, tokens)))
            .DistinctBy(r => r.Repo, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
        return merged;
    }

    [GeneratedRegex(@"^(?:huggingface-cli|hf)\s+download\s+(?<repo>[\w.\-]+/[\w.\-]+)(?:\s+(?<file>\S+\.gguf))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliRegex();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?:huggingface\.co|hf\.co)/(?<repo>[\w.\-]+/[\w.\-]+)(?::(?<tag>[\w.\-]+))?(?<rest>(?:[/?#].*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(?<repo>[\w.\-]+/[\w.\-]+)(?::(?<tag>[\w.\-]+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainRegex();

    [GeneratedRegex(@"^[\w.\-]{1,96}/[\w.\-]{1,96}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:UD-)?(?:IQ\d(?:_[A-Z]+)*|Q\d(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantTagRegex();

    [GeneratedRegex(@"[\s\-_./]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSplitRegex();
}
