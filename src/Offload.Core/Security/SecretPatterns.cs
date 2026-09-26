using System.Text.RegularExpressions;

namespace Offload.Core.Security;

/// <summary>
/// Общие регулярные выражения токенов с узнаваемым форматом (ключи облаков, токены GitHub/Slack/npm/Hugging Face, JWT …).
/// Используются маскированием секретов в MCP (<c>SecretRedactor</c>, <c>code_scan secrets</c>) и пакетом диагностики
/// приложения — чтобы набор распознаваемых токенов был один на всё приложение.
/// </summary>
public static class SecretPatterns
{
    private const RegexOptions O = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>AWS access key id (AKIA… постоянный, ASIA… временный).</summary>
    public static readonly Regex AwsKey = new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", O);

    /// <summary>GitHub: ghp_/gho_/ghu_/ghs_/ghr_ и fine-grained github_pat_.</summary>
    public static readonly Regex GitHubToken = new(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}\b", O);

    /// <summary>Slack xoxb-/xoxp-/… .</summary>
    public static readonly Regex SlackToken = new(@"\bxox[abposr]-[A-Za-z0-9-]{10,}", O);

    /// <summary>Google API key (AIza…).</summary>
    public static readonly Regex GoogleKey = new(@"\bAIza[0-9A-Za-z_\-]{35}\b", O);

    /// <summary>OpenAI/Anthropic: sk-…, sk-proj-…, sk-ant-api03-….</summary>
    public static readonly Regex OpenAiKey = new(@"\bsk-(?:proj-|ant-(?:api\d+-)?)?[A-Za-z0-9_\-]{20,}\b", O);

    /// <summary>Stripe live secret/restricted key (sk_live_/rk_live_).</summary>
    public static readonly Regex StripeKey = new(@"\b(?:sk|rk)_live_[0-9a-zA-Z]{20,}\b", O);

    /// <summary>JWT: три части base64url, первые две — JSON (eyJ…).</summary>
    public static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", O);

    /// <summary>npm access token (npm_ + 36 символов).</summary>
    public static readonly Regex NpmToken = new(@"\bnpm_[A-Za-z0-9]{36}\b", O);

    /// <summary>Hugging Face user access token (hf_ + 34 символа).</summary>
    public static readonly Regex HuggingFaceToken = new(@"\bhf_[A-Za-z0-9]{30,}\b", O);

    /// <summary>Токены, которые заменяются целиком (вид → выражение).</summary>
    public static IReadOnlyList<(string Kind, Regex Regex)> WholeTokens { get; } =
    [
        ("aws-key", AwsKey), ("github-token", GitHubToken), ("slack-token", SlackToken), ("google-key", GoogleKey),
        ("openai-key", OpenAiKey), ("stripe-key", StripeKey), ("npm-token", NpmToken), ("hf-token", HuggingFaceToken), ("jwt", Jwt),
    ];

    /// <summary>
    /// Нестрогий вариант по префиксам (короче настоящих токенов): для журналов и диагностики, где ложное срабатывание
    /// безвредно, а обрезанный или нестандартный токен всё равно нужно скрыть.
    /// </summary>
    public static readonly Regex LooseTokenPrefixes = new(
        @"\b(?:sk-[A-Za-z0-9_\-]{8,}|(?:sk|rk)_live_[A-Za-z0-9]{8,}|hf_[A-Za-z0-9]{8,}|gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}" +
        @"|xox[abposr]-[A-Za-z0-9\-]{8,}|(?:AKIA|ASIA)[A-Z0-9]{12,}|AIza[0-9A-Za-z_\-]{20,}|npm_[A-Za-z0-9]{20,}" +
        @"|eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{4,})", O);

    /// <summary>Значение похоже на токен (строгие форматы или нестрогие префиксы).</summary>
    public static bool LooksLikeToken(string value) =>
        LooseTokenPrefixes.IsMatch(value) || WholeTokens.Any(t => t.Regex.IsMatch(value));

    /// <summary>Заменить все похожие на токены фрагменты текста на <paramref name="mask"/> (строгие и нестрогие шаблоны).</summary>
    public static string MaskTokens(string text, string mask)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (_, regex) in WholeTokens) text = regex.Replace(text, mask);
        return LooseTokenPrefixes.Replace(text, mask);
    }
}
