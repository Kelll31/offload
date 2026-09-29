using System.Text;
using System.Text.RegularExpressions;
using Offload.Core.Security;

namespace Offload.Mcp.Infrastructure;

/// <summary>Шаблон секрета для поиска (code_scan secrets, отказ local_memory хранить секреты).</summary>
internal sealed record SecretRule(string Id, string Severity, Regex Regex, string Message);

/// <summary>
/// Секреты по содержимому: общие регулярные выражения (их же использует <see cref="CodeRules"/>) и маскирование значений
/// в тексте, который уходит в локальную модель (<see cref="FileGatherer"/>) и в ответах инструментов, показывающих код.
/// Значение заменяется на «redacted:вид» в угловых кавычках, ключ остаётся — модель видит, что настройка есть, но не видит её значение.
/// Правила консервативны к коду: имя параметра <c>password</c> без литерала, сравнения (<c>==</c>), шаблоны
/// (<c>${VAR}</c>, <c>{{x}}</c>, <c>&lt;token&gt;</c>) и заглушки (example, changeme, ***) не маскируются.
/// Число строк и их окончания сохраняются: ссылки path:line остаются верными.
/// </summary>
internal static partial class SecretRedactor
{
    private const RegexOptions O = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex PrivateKeyHeader = new(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY", O);

    /// <summary>
    /// Правила поиска секретов (построчно, для отчётов). Токены с узнаваемым форматом — общие с пакетом диагностики
    /// приложения (<see cref="SecretPatterns"/>). Значения в отчётах маскирует <see cref="CodeRules.Mask"/>.
    /// </summary>
    public static readonly SecretRule[] Rules =
    [
        new("aws-key", "high", SecretPatterns.AwsKey, "AWS access key id"),
        new("private-key", "high", PrivateKeyHeader, "private key material"),
        new("github-token", "high", SecretPatterns.GitHubToken, "GitHub token"),
        new("slack-token", "high", SecretPatterns.SlackToken, "Slack token"),
        new("google-key", "high", SecretPatterns.GoogleKey, "Google API key"),
        new("openai-key", "high", SecretPatterns.OpenAiKey, "OpenAI/Anthropic-style API key"),
        new("stripe-key", "high", SecretPatterns.StripeKey, "Stripe live key"),
        new("npm-token", "high", SecretPatterns.NpmToken, "npm access token"),
        new("hf-token", "high", SecretPatterns.HuggingFaceToken, "Hugging Face access token"),
        new("jwt", "medium", SecretPatterns.Jwt, "JWT token"),
        new("offload-key", "high", SecretPatterns.OffloadLocalKey, "Offload llama-server API key"),
        new("offload-lan-key", "high", SecretPatterns.OffloadLanKey, "Offload network access key"),
        new("conn-string-password", "high", new Regex(@"(?i)(?:password|pwd)\s*=\s*[^;""'\s{$][^;""']{3,}", O), "password in a connection string"),
        new("hardcoded-secret", "medium", new Regex(@"(?i)\b(?:password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret)\b[""']?\s*[:=]\s*[""'][^""'\s]{6,}[""']", O), "hard-coded credential"),
        new("url-credentials", "high", new Regex(@"\b[a-z][a-z0-9+.-]*://[^/\s:@""']+:[^/\s@""']{3,}@[\w.-]+", O), "credentials inside a URL"),
    ];

    /// <summary>
    /// Обёртка инструмента, возвращающего фрагменты файлов (search_code, symbols, find_context, ask_files): ответ маскируется
    /// перед отправкой в IDE, если включён Mcp.RedactSecrets. Это вторая линия защиты: основная — маскирование при чтении
    /// (<see cref="CodeIndex"/> и <see cref="FileGatherer"/> маскируют с учётом пути, поэтому правила конфигов и поиск regex
    /// работают уже по замаскированному тексту). Здесь путь неизвестен — правила «ключ: значение» без кавычек не применяются.
    /// </summary>
    public static Func<ToolContext, Task<string>> RedactingOutput(Func<ToolContext, Task<string>> body) =>
        async ctx =>
        {
            var text = await body(ctx).ConfigureAwait(false);
            return ctx.Cfg.Mcp.RedactSecrets ? Redact(text) : text;
        };

    /// <summary>Замена значения секрета.</summary>
    public static string Marker(string kind) => $"«redacted:{kind}»";

    /// <summary>Заглушка, шаблон или уже замаскированное значение — не секрет.</summary>
    public static bool LooksLikePlaceholder(string value) => Placeholder().IsMatch(value) || value.Contains("«redacted:", StringComparison.Ordinal);

    [GeneratedRegex(@"(?i)(example|placeholder|changeme|your[_-]?|xxx|\*\*\*|<[^>]+>|\$\{|\{\{|dummy|sample|test|fake|redacted|process\.env|os\.environ|Environment\.)", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>Замаскировать значения секретов в тексте. path — для правил, зависящих от формата файла (YAML, .properties, .ini …).</summary>
    public static string Redact(string text, string? path = null) => Redact(text, path, out _);

    /// <summary>Замаскировать значения секретов; count — число замен.</summary>
    public static string Redact(string text, string? path, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(text)) return text;
        var n = 0;
        var result = RedactPrivateKeys(text, path, ref n);
        result = EscapedPrivateKey().Replace(result, m =>
        {
            n++;
            return m.Groups["h"].Value + "\\n" + Marker("private-key") + "\\n" + m.Groups["e"].Value;
        });

        result = UrlCredentials().Replace(result, m => ReplaceGroup(m, "v", "url-credentials", ref n));
        var input = result;
        result = ConnStringPassword().Replace(input, m => IsConnectionStringContext(input, m) ? ReplaceGroup(m, "v", "conn-string-password", ref n) : m.Value);
        result = QuotedKeyValue().Replace(result, m => IsSecretAssignment(m.Groups["name"].Value, m.Groups["v"].Value) ? ReplaceGroup(m, "v", "hardcoded-secret", ref n) : m.Value);
        result = XmlKeyValue().Replace(result, m => IsSecretAssignment(m.Groups["name"].Value, m.Groups["v"].Value) ? ReplaceGroup(m, "v", "hardcoded-secret", ref n) : m.Value);
        result = XmlElement().Replace(result, m => IsSecretAssignment(m.Groups["name"].Value, m.Groups["v"].Value) ? ReplaceGroup(m, "v", "hardcoded-secret", ref n) : m.Value);
        result = NpmrcAuth().Replace(result, m => LooksLikePlaceholder(m.Groups["v"].Value) ? m.Value : ReplaceGroup(m, "v", "npmrc-auth", ref n));
        if (IsConfigFile(path))
            result = ConfigKeyValue().Replace(result, m => IsSecretAssignment(m.Groups["name"].Value, m.Groups["v"].Value, minLength: 4) ? ReplaceGroup(m, "v", "config-secret", ref n) : m.Value);

        foreach (var (kind, regex) in SecretPatterns.WholeTokens)
        {
            result = regex.Replace(result, m =>
            {
                if (LooksLikePlaceholder(m.Value)) return m.Value;
                n++;
                return Marker(kind);
            });
        }
        count = n;
        return result;
    }

    private static string ReplaceGroup(Match m, string group, string kind, ref int n)
    {
        var g = m.Groups[group];
        if (!g.Success || g.Length == 0 || LooksLikePlaceholder(g.Value)) return m.Value;
        n++;
        var start = g.Index - m.Index;
        return string.Concat(m.Value.AsSpan(0, start), Marker(kind), m.Value.AsSpan(start + g.Length));
    }

    /// <summary>
    /// Многострочный блок ключа (PEM/OpenSSH/PGP): строка BEGIN и END остаются, тело (base64 и заголовки) заменяется маркером
    /// на первой строке и пустыми строками дальше — номера строк не сдвигаются. Литерал «-----BEGIN PRIVATE KEY» в коде
    /// (без base64 за ним) не трогается. Тело ищется и вперёд от BEGIN, и назад от END (фрагмент без строки BEGIN — контекст
    /// поиска), а в файлах ключей (*.pem, *.key …) — любой блок base64 со строкой от 40 символов. Префикс номера строки
    /// во фрагментах ответа («  12: », «   13- », «14| ») сохраняется.
    /// </summary>
    private static string RedactPrivateKeys(string text, string? path, ref int n)
    {
        var keyFile = IsKeyFile(path);
        if (!keyFile && !text.Contains("PRIVATE KEY", StringComparison.Ordinal)) return text;
        var lines = new List<(int Start, int End, int Next)>();
        for (var i = 0; i < text.Length;)
        {
            var end = LineEnd(text, i);
            var next = NextLineStart(text, end);
            lines.Add((i, end, next));
            if (next <= i) break;
            i = next;
        }
        var body = new int[lines.Count]; // 0 — нет; 1 — тело блока; 2 — первая строка блока (маркер)
        string Content(int k) => text[lines[k].Start..lines[k].End];

        for (var k = 0; k < lines.Count; k++)
        {
            var line = Content(k);
            if (PemHeaderLine().IsMatch(line))
            {
                // Вперёд: строки base64 и заголовки «Proc-Type: …» до строки END или до первой посторонней строки.
                var (last, longest, closed) = (k, 0, false);
                var j = k + 1;
                for (; j < lines.Count; j++)
                {
                    var c = StripSnippetPrefix(Content(j));
                    if (c.StartsWith("-----END ", StringComparison.Ordinal)) { closed = true; break; }
                    if (c.Length > 0 && !IsPemBodyLine(c)) break;
                    longest = Math.Max(longest, c.Length);
                    last = j;
                }
                // Без строки END (файл обрезан) — только если есть строка, похожая на base64 ключа, а не «else» после литерала.
                if (last > k && (closed || longest >= 40)) Mark(body, k + 1, last);
                k = Math.Max(k, last);
            }
            else if (PemFooterLine().IsMatch(line))
            {
                // Назад от END: тело без строки BEGIN (фрагмент ответа начинается с середины ключа).
                var (first, longest) = (k, 0);
                for (var j = k - 1; j >= 0 && body[j] == 0; j--)
                {
                    var c = StripSnippetPrefix(Content(j));
                    if (c.Length == 0 || !IsPemBodyLine(c)) break;
                    longest = Math.Max(longest, c.Length);
                    first = j;
                }
                if (first < k && longest >= 40) Mark(body, first, k - 1);
            }
        }
        if (keyFile)
        {
            // Файл ключа: любой непрерывный блок base64, в котором есть строка от 40 символов.
            for (var k = 0; k < lines.Count; k++)
            {
                if (body[k] != 0 || !IsBase64Line(StripSnippetPrefix(Content(k)))) continue;
                var j = k;
                var longest = 0;
                while (j < lines.Count && body[j] == 0 && IsBase64Line(StripSnippetPrefix(Content(j))))
                {
                    longest = Math.Max(longest, StripSnippetPrefix(Content(j)).Length);
                    j++;
                }
                if (longest >= 40) Mark(body, k, j - 1);
                k = j - 1;
            }
        }
        if (!body.Any(b => b != 0)) return text;

        var sb = new StringBuilder(text.Length);
        for (var k = 0; k < lines.Count; k++)
        {
            var (start, end, next) = lines[k];
            if (body[k] == 0)
            {
                sb.Append(text, start, next - start);
                continue;
            }
            var line = text[start..end];
            var prefixLength = line.Length - StripSnippetPrefix(line).Length;
            sb.Append(line, 0, prefixLength);
            if (body[k] == 2)
            {
                sb.Append(Marker("private-key"));
                n++;
            }
            sb.Append(text, end, next - end);
        }
        return sb.ToString();
    }

    /// <summary>Отметить строки from..to как тело ключа (первая — под маркер).</summary>
    private static void Mark(int[] body, int from, int to)
    {
        for (var i = from; i <= to; i++) body[i] = i == from ? 2 : 1;
    }

    /// <summary>Строка — только base64 (без пробелов), без префикса номера строки.</summary>
    private static bool IsBase64Line(ReadOnlySpan<char> line) => line.Length > 0 && line.IndexOfAnyExcept(Base64Chars) < 0;

    /// <summary>Без отступа и префикса номера строки из фрагментов ответа («  12: », «   13- », «14| »).</summary>
    private static ReadOnlySpan<char> StripSnippetPrefix(ReadOnlySpan<char> line)
    {
        var t = line.Trim();
        var digits = 0;
        while (digits < t.Length && char.IsAsciiDigit(t[digits])) digits++;
        if (digits > 0 && digits < t.Length && t[digits] is ':' or '|' or '-') t = t[(digits + 1)..].TrimStart();
        return t;
    }

    /// <summary>Файл ключа или сертификата по имени (*.pem, *.key, id_rsa …): блоки base64 маскируются и без строк BEGIN/END.</summary>
    internal static bool IsKeyFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".pem" or ".key" or ".p8" or ".ppk" or ".asc" or ".gpg"
               || name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase) || name.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("id_ecdsa", StringComparison.OrdinalIgnoreCase) || name.StartsWith("id_dsa", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPemBodyLine(ReadOnlySpan<char> line)
    {
        var colon = line.IndexOf(':');
        if (colon > 0 && line[..colon].IndexOfAnyExcept(HeaderChars) < 0) return true;
        return line.IndexOfAnyExcept(Base64Chars) < 0;
    }

    private static readonly System.Buffers.SearchValues<char> Base64Chars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");

    private static readonly System.Buffers.SearchValues<char> HeaderChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-");

    private static int LineEnd(string text, int from)
    {
        var i = text.IndexOfAny(['\r', '\n'], from);
        return i < 0 ? text.Length : i;
    }

    private static int NextLineStart(string text, int lineEnd)
    {
        if (lineEnd >= text.Length) return text.Length;
        if (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n') return lineEnd + 2;
        return lineEnd + 1;
    }

    /// <summary>Password=… маскируется только в строке подключения: рядом есть Server=/Host=/Database=/User Id=… (а не «password = GetPassword();»).</summary>
    private static bool IsConnectionStringContext(string text, Match m)
    {
        var value = m.Groups["v"].Value;
        if (CallLike().IsMatch(value)) return false;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
        var lineEnd = LineEnd(text, m.Index + m.Length);
        var line = text.AsSpan(lineStart, lineEnd - lineStart).ToString();
        return ConnStringKey().IsMatch(line);
    }

    /// <summary>Ключ похож на секрет, а значение — на настоящее (не заглушка, не имя переменной окружения, не тип).</summary>
    internal static bool IsSecretAssignment(string name, string value, int minLength = 6)
    {
        var v = value.Trim();
        if (v.Length < minLength || LooksLikePlaceholder(v)) return false;
        if (v[0] is '$' or '%' or '{' or '<' or '@' || v.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) return false;
        if (EnvVarName().IsMatch(v) || TypeWords.Contains(v)) return false;
        if (v.All(c => c == '*' || c == 'x' || c == 'X' || c == '.' || c == '-' || c == '_')) return false;
        // Подпись на естественном языке («Пароль», «Password») — строка локализации, а не значение.
        if (v.All(char.IsLetter) && (char.IsUpper(v[0]) && v.Skip(1).All(char.IsLower) || v.Any(c => c > 0x7F))) return false;
        var strength = SecretKeyStrength(name);
        if (strength == 0) return false;
        // Общие имена (token, key, auth) — только для значений, похожих на случайные строки.
        return strength == 2 || v.Length >= 16 && v.Any(char.IsAsciiDigit) && v.Any(char.IsAsciiLetter) && !v.Contains(' ');
    }

    private static readonly HashSet<string> TypeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "number", "boolean", "bool", "null", "undefined", "required", "optional", "none", "true", "false", "object",
        "secret", "password", "string?", "SecureString", "str", "bytes", "text", "hidden",
    };

    private static readonly string[] StrongKeyWords =
    [
        "password", "passwd", "passphrase", "secret", "apikey", "accesskey", "accesstoken", "authtoken", "refreshtoken", "bearertoken",
        "privatekey", "clientsecret", "credential", "signingkey", "encryptionkey", "sessiontoken", "sastoken", "accountkey", "masterkey",
    ];

    private static readonly string[] WeakKeyWords = ["token", "key", "auth", "pwd", "pass", "sig", "signature"];

    private static readonly string[] NonSecretSuffixes =
    [
        "name", "file", "path", "hint", "label", "field", "placeholder", "type", "length", "policy", "pattern", "regex", "url", "uri",
        "id", "header", "prompt", "mode", "enabled", "required", "hash", "provider", "store", "version", "format", "prefix", "env",
        "var", "variable", "setting", "settings", "description", "message", "text", "title", "error", "errors", "count", "size",
        "expiry", "expiration", "expires", "lifetime", "timeout", "ttl", "location", "source", "kind", "column", "property", "event",
        "input", "form", "dialog", "strength", "rules", "attempts", "changed", "reset", "set", "algorithm", "alg", "usage", "vault",
    ];

    /// <summary>0 — не секрет; 1 — общее имя (token/key: нужно случайное значение); 2 — явно секрет (password/secret/apiKey…).</summary>
    internal static int SecretKeyStrength(string name)
    {
        var last = name.Trim('"', '\'', ' ');
        var cut = last.LastIndexOfAny(['.', ':', '/']);
        if (cut >= 0) last = last[(cut + 1)..];
        var norm = new string(last.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (norm.Length == 0) return 0;
        foreach (var s in NonSecretSuffixes)
        {
            if (norm.EndsWith(s, StringComparison.Ordinal) && norm.Length > s.Length) return 0;
        }
        if (StrongKeyWords.Any(w => norm.Contains(w, StringComparison.Ordinal))) return 2;
        if (WeakKeyWords.Any(w => norm.Equals(w, StringComparison.Ordinal) || norm.EndsWith(w, StringComparison.Ordinal))) return 1;
        return 0;
    }

    /// <summary>Файлы конфигурации «ключ: значение» без кавычек (YAML, .properties, .ini, .toml, .env …).</summary>
    internal static bool IsConfigFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".yml" or ".yaml" or ".properties" or ".ini" or ".cfg" or ".conf" or ".toml" or ".env" or ".npmrc" or ".pypirc" or ".netrc"
               || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
               || name.Equals(".pypirc", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^[ \t]*[^\r\n]*?-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----[^\r\n]*", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex PemHeaderLine();

    [GeneratedRegex(@"-----END (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----", RegexOptions.CultureInvariant)]
    private static partial Regex PemFooterLine();

    /// <summary>Ключ в JSON-строке с экранированными переводами строк (service account Google и т. п.).</summary>
    [GeneratedRegex(@"(?<h>-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----)(?:\\[rn]|[A-Za-z0-9+/= ])+?(?<e>-----END (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----)", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedPrivateKey();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^/\s:@""'«]+:(?<v>[^/\s@""'«]{3,})@(?=[\w.-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)(?<![\w])(?:password|pwd)[ \t]*=(?!=)[ \t]*(?<v>[^;""'`\s{$%<(=][^;""'`\r\n]*?)(?=[ \t]*(?:;|""|'|`|\r?$))", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ConnStringPassword();

    [GeneratedRegex(@"(?i)\b(?:server|data\s+source|host|hostname|database|initial\s+catalog|user\s*id|uid|username|user|port|address|addr|network\s+address|trusted_connection|integrated\s+security|sslmode|ssl\s+mode|encrypt|provider|dsn|driver|accountname|accountkey|endpoint|defaultendpointsprotocol)[ \t]*=(?!=)", RegexOptions.CultureInvariant)]
    private static partial Regex ConnStringKey();

    [GeneratedRegex(@"^[\w.]+\(.*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex CallLike();

    /// <summary>«key": "value" / key = 'value' / 'key' =&gt; "value" — значение без пробелов, 6–400 символов.</summary>
    [GeneratedRegex(@"(?<![\w$])(?<q0>[""']?)(?<name>[A-Za-z_$][\w.$-]{0,80})\k<q0>[ \t]*(?:=>|:=|:|=)[ \t]*(?<q>[""'])(?<v>[^""'\s]{4,400})\k<q>", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedKeyValue();

    /// <summary>web.config/app.config: &lt;add key="ApiKey" value="…" /&gt;.</summary>
    [GeneratedRegex(@"(?i)\bkey\s*=\s*""(?<name>[^""]{1,80})""\s+value\s*=\s*""(?<v>[^""\s]{4,400})""", RegexOptions.CultureInvariant)]
    private static partial Regex XmlKeyValue();

    /// <summary>&lt;password&gt;…&lt;/password&gt; (settings.xml Maven и подобные).</summary>
    [GeneratedRegex(@"<(?<name>[A-Za-z_][\w.-]{0,60})>(?<v>[^<\s]{4,400})</\k<name>>", RegexOptions.CultureInvariant)]
    private static partial Regex XmlElement();

    /// <summary>.npmrc: //registry/:_authToken=…, _auth=…, _password=….</summary>
    [GeneratedRegex(@"(?im)(?:^|[\s:])_(?:authToken|auth|password)[ \t]*=[ \t]*(?<v>[^\s""'$][^\s]*)", RegexOptions.CultureInvariant)]
    private static partial Regex NpmrcAuth();

    /// <summary>Конфиги без кавычек: «password: hunter2», «db.password=hunter2» (комментарии # не входят в значение).</summary>
    [GeneratedRegex(@"^[ \t]*(?:-[ \t]+)?(?<name>[A-Za-z_][\w.\-]{0,80})[ \t]*[:=][ \t]*(?<v>[^\s""'#;{$%<!&*|>\[][^\r\n#]*?)[ \t]*(?=\r?$|[ \t]+#)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ConfigKeyValue();

    [GeneratedRegex(@"^[A-Z][A-Z0-9]*_[A-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvVarName();
}
