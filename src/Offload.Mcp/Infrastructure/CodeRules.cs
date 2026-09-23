using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

/// <summary>Находка статической проверки.</summary>
internal sealed record RuleHit(string Rule, string Severity, string Message);

/// <summary>
/// Построчные правила: утёкшие секреты (значения маскируются — в IDE не уходят) и опасные API по языкам
/// (выполнение команд, eval, небезопасная десериализация, SQL-конкатенация, отключение TLS и т. п.), асинхронные анти-паттерны.
/// </summary>
internal static partial class CodeRules
{
    private sealed record Rule(string Id, string Severity, Regex Regex, string Message, CodeLang[]? Langs = null);

    /// <summary>Правила, которые по смыслу ищут содержимое строк (SQL, команды оболочки, URL): для остальных совпадение в литерале — не использование API.</summary>
    private static readonly HashSet<string> StringContentRules = new(StringComparer.Ordinal)
    {
        "sql-concat", "shell-exec", "child-process", "insecure-http", "chmod-777", "go-exec", "tls-disabled",
    };

    private const RegexOptions O = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Rule[] SecretRules =
    [
        new("aws-key", "high", new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", O), "AWS access key id"),
        new("private-key", "high", new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY", O), "private key material"),
        new("github-token", "high", new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}\b", O), "GitHub token"),
        new("slack-token", "high", new Regex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}", O), "Slack token"),
        new("google-key", "high", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", O), "Google API key"),
        new("openai-key", "high", new Regex(@"\bsk-(?:proj-|ant-(?:api\d+-)?)?[A-Za-z0-9_\-]{20,}\b", O), "OpenAI/Anthropic-style API key"),
        new("stripe-key", "high", new Regex(@"\b(?:sk|rk)_live_[0-9a-zA-Z]{20,}\b", O), "Stripe live key"),
        new("jwt", "medium", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", O), "JWT token"),
        new("conn-string-password", "high", new Regex(@"(?i)(?:password|pwd)\s*=\s*[^;""'\s{$][^;""']{3,}", O), "password in a connection string"),
        new("hardcoded-secret", "medium", new Regex(@"(?i)\b(?:password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret)\b[""']?\s*[:=]\s*[""'][^""'\s]{6,}[""']", O), "hard-coded credential"),
        new("url-credentials", "high", new Regex(@"\b[a-z][a-z0-9+.-]*://[^/\s:@""']+:[^/\s@""']{3,}@[\w.-]+", O), "credentials inside a URL"),
    ];

    private static readonly CodeLang[] Cs = [CodeLang.CSharp];
    private static readonly CodeLang[] Js = [CodeLang.TypeScript];
    private static readonly CodeLang[] Py = [CodeLang.Python];

    private static readonly Rule[] UnsafeRules =
    [
        // C#
        new("process-start", "medium", new Regex(@"Process\.Start\(|new\s+ProcessStartInfo\(", O), "process execution: check the command/arguments are not built from input", Cs),
        new("shell-exec", "high", new Regex(@"(?i)(cmd(\.exe)?""?\s*,\s*\$?""/c|FileName\s*=\s*""(cmd|powershell|pwsh|bash|sh)(\.exe)?""|UseShellExecute\s*=\s*true)", O), "shell execution", Cs),
        new("sql-concat", "high", new Regex(@"(?i)(new\s+(Sql|Npgsql|MySql|Sqlite|OleDb)Command\(\s*\$?""[^""]*(\{|""\s*\+)|ExecuteSqlRaw(Async)?\(\s*\$""|FromSqlRaw\(\s*\$""|CommandText\s*=\s*\$""|""\s*(SELECT|INSERT|UPDATE|DELETE)\b[^""]*""\s*\+\s*\w)", O), "SQL built from strings (injection risk); use parameters", Cs),
        new("binaryformatter", "high", new Regex(@"\b(BinaryFormatter|NetDataContractSerializer|SoapFormatter|LosFormatter|ObjectStateFormatter)\b", O), "unsafe deserializer", Cs),
        new("typename-handling", "high", new Regex(@"TypeNameHandling\s*=\s*TypeNameHandling\.(All|Auto|Objects|Arrays)", O), "Json.NET TypeNameHandling enables gadget deserialization", Cs),
        new("tls-disabled", "high", new Regex(@"(ServerCertificateCustomValidationCallback|ServerCertificateValidationCallback|RemoteCertificateValidationCallback)\s*=.*=>\s*true|DangerousAcceptAnyServerCertificateValidator", O), "TLS certificate validation disabled", Cs),
        new("weak-hash", "low", new Regex(@"\b(MD5|SHA1)\.(Create|HashData)\(|new\s+(MD5|SHA1)CryptoServiceProvider", O), "weak hash (fine for checksums, not for security)", Cs),
        new("insecure-random", "low", new Regex(@"new\s+Random\(\).*\b(token|password|secret|key|salt|nonce)", O | RegexOptions.IgnoreCase), "System.Random used for a secret value; use RandomNumberGenerator", Cs),
        new("path-combine-input", "medium", new Regex(@"Path\.Combine\([^)]*\b(request|input|query|param|args|body|name)\w*\b", O | RegexOptions.IgnoreCase), "path built from input: check for traversal (..)", Cs),
        new("xml-dtd", "medium", new Regex(@"DtdProcessing\s*=\s*DtdProcessing\.Parse|XmlResolver\s*=\s*new\s+XmlUrlResolver", O), "XML external entities enabled (XXE)", Cs),
        // JS/TS
        new("eval", "high", new Regex(@"(?<![\w.])eval\(|new\s+Function\(", O), "dynamic code execution", Js),
        new("child-process", "high", new Regex(@"\b(exec|execSync)\(\s*[`'""]?[^)]*(\$\{|\+)|child_process[^;]*\bexec\b|shell\s*:\s*true", O), "shell command built from strings", Js),
        new("inner-html", "medium", new Regex(@"\.innerHTML\s*=|dangerouslySetInnerHTML|document\.write\(|v-html=", O), "HTML injection sink (XSS)", Js),
        new("tls-disabled", "high", new Regex(@"rejectUnauthorized\s*:\s*false|NODE_TLS_REJECT_UNAUTHORIZED\s*=\s*['""]?0", O), "TLS certificate validation disabled", Js),
        new("sql-concat", "high", new Regex(@"(?i)\.(query|execute|raw)\(\s*[`'""](SELECT|INSERT|UPDATE|DELETE)[^`'""]*(\$\{|[`'""]\s*\+)", O), "SQL built from strings", Js),
        // Python
        new("eval", "high", new Regex(@"(?<![\w.])(eval|exec)\(", O), "dynamic code execution", Py),
        new("shell-exec", "high", new Regex(@"os\.system\(|os\.popen\(|subprocess\.\w+\([^)]*shell\s*=\s*True", O), "shell command execution", Py),
        new("pickle", "high", new Regex(@"\b(pickle|cPickle|dill|marshal)\.loads?\(|yaml\.load\((?![^)]*Loader\s*=\s*yaml\.SafeLoader)", O), "unsafe deserialization", Py),
        new("tls-disabled", "high", new Regex(@"verify\s*=\s*False|ssl\._create_unverified_context|CERT_NONE", O), "TLS certificate validation disabled", Py),
        new("sql-concat", "high", new Regex(@"(?i)\.execute\(\s*(f[""']|[""'][^""']*[""']\s*%|[""'][^""']*[""']\s*\+|[""'][^""']*[""']\.format\()", O), "SQL built from strings", Py),
        // Общие
        new("insecure-http", "low", new Regex(@"[""']http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]|schemas\.|www\.w3\.org|json-schema\.org)[\w.-]+", O), "plain HTTP URL"),
        new("chmod-777", "medium", new Regex(@"chmod\s+(-R\s+)?777|0o?777\b", O), "world-writable permissions"),
        new("go-tls", "high", new Regex(@"InsecureSkipVerify:\s*true", O), "TLS certificate validation disabled", [CodeLang.Go]),
        new("go-exec", "medium", new Regex(@"exec\.Command\(\s*""(sh|bash|cmd)""", O), "shell execution", [CodeLang.Go]),
        new("rust-unsafe", "low", new Regex(@"\bunsafe\s*\{", O), "unsafe block", [CodeLang.Rust]),
    ];

    private static readonly Rule[] AsyncRules =
    [
        new("sync-over-async", "medium", new Regex(@"\.(Result|Wait\(\))\b|\.GetAwaiter\(\)\.GetResult\(\)", O), "blocking on a task (deadlock/thread-pool starvation risk)", Cs),
        new("async-void", "medium", new Regex(@"\basync\s+void\s+(?!On\w+\()\w+\s*\(", O), "async void outside an event handler: exceptions crash the process", Cs),
        new("thread-sleep-in-async", "low", new Regex(@"Thread\.Sleep\(", O), "Thread.Sleep blocks a thread; in async code use await Task.Delay", Cs),
        new("task-run-fire-forget", "low", new Regex(@"^\s*(_\s*=\s*)?Task\.Run\(", O), "fire-and-forget Task.Run: exceptions are lost unless observed", Cs),
        new("floating-promise", "low", new Regex(@"^\s*(?!return|await|const|let|var|yield)[\w.]+\.(then|catch)\(|^\s*\w+Async\([^)]*\);", O), "promise/async call not awaited", Js),
        new("async-foreach", "medium", new Regex(@"\.forEach\(\s*async\b", O), "async callback in forEach is not awaited", Js),
        new("py-no-await", "low", new Regex(@"^\s*asyncio\.sleep\(|^\s*\w+\.\w+_async\(", O), "coroutine called without await", Py),
        new("py-blocking", "low", new Regex(@"^\s*time\.sleep\(", O), "time.sleep in code that may be async; use asyncio.sleep", Py),
    ];

    public static IEnumerable<RuleHit> Secrets(string line)
    {
        foreach (var r in SecretRules)
        {
            var m = r.Regex.Match(line);
            if (!m.Success || LooksLikePlaceholder(m.Value)) continue;
            yield return new RuleHit(r.Id, r.Severity, $"{r.Message}: {Mask(m.Value)}");
        }
    }

    public static IEnumerable<RuleHit> Unsafe(CodeLang lang, string line) => Match(UnsafeRules, lang, line);

    public static IEnumerable<RuleHit> Async(CodeLang lang, string line) => Match(AsyncRules, lang, line);

    private static IEnumerable<RuleHit> Match(Rule[] rules, CodeLang lang, string line)
    {
        foreach (var r in rules)
        {
            if (r.Langs is not null && !r.Langs.Contains(lang)) continue;
            var m = r.Regex.Match(line);
            if (!m.Success) continue;
            // «new Regex(@"BinaryFormatter")» или строка в тесте — не использование опасного API.
            if (!StringContentRules.Contains(r.Id) && CodeIndex.InStringOrComment(line, m.Index)) continue;
            yield return new RuleHit(r.Id, r.Severity, r.Message);
        }
    }

    /// <summary>Значение секрета: первые 4 символа + звёздочки (сам секрет в ответ не попадает).</summary>
    public static string Mask(string value)
    {
        var v = value.Trim();
        return v.Length <= 6 ? "****" : v[..4] + new string('*', Math.Min(12, v.Length - 4)) + $" ({v.Length} chars)";
    }

    [GeneratedRegex(@"(?i)(example|placeholder|changeme|your[_-]?|xxx|\*\*\*|<[^>]+>|\$\{|\{\{|dummy|sample|test|fake|redacted|process\.env|os\.environ|Environment\.)", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private static bool LooksLikePlaceholder(string value) => Placeholder().IsMatch(value);

    /// <summary>Строка с секретом — не показывать её текст целиком.</summary>
    public static string RedactLine(string line)
    {
        var result = line;
        foreach (var r in SecretRules)
            result = r.Regex.Replace(result, m => LooksLikePlaceholder(m.Value) ? m.Value : Mask(m.Value));
        return result;
    }

    /// <summary>Ветвления для цикломатической сложности (упрощённо, по ключевым словам и операторам).</summary>
    [GeneratedRegex(@"\b(if|else\s+if|elif|for|foreach|while|case|catch|except|when)\b|&&|\|\||\?\?|\?(?![?.\[])|\band\b|\bor\b", RegexOptions.CultureInvariant)]
    public static partial Regex Branch();

    [GeneratedRegex(@"\b(TODO|FIXME|HACK|XXX|BUG|OPTIMIZE|REVIEW|DEPRECATED)\b[:(\s]?", RegexOptions.CultureInvariant)]
    public static partial Regex Todo();

    [GeneratedRegex(@"<auto-generated|@generated|DO NOT EDIT|Code generated .* DO NOT EDIT|This file was generated|autogenerated", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    public static partial Regex GeneratedMarker();
}
