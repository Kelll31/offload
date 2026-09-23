using System.Text.RegularExpressions;

namespace Offload.Mcp.Infrastructure;

/// <summary>Диагностика компилятора/линтера. Line/Col — с 1 (0 — неизвестно).</summary>
internal sealed record Diagnostic(string File, int Line, int Col, string Severity, string? Code, string Message);

/// <summary>Кадр стека исключения/паники, указывающий на файл и строку.</summary>
internal sealed record StackFrame(string File, int Line, string? Function, int LogLine);

/// <summary>
/// Разбор вывода сборки/линтеров/тестов: MSBuild/csc/tsc (file(l,c): error CODE: msg), gcc/clang/go/mypy/ruff/eslint-unix
/// (file:l:c: severity: msg), tsc --pretty (file:l:c - error TS…), rustc/cargo (error[E…]: msg + --> file:l:c), eslint stylish
/// (заголовок-файл + «l:c error msg rule»); кадры стека .NET/Python/Node/Java/Go/Rust.
/// </summary>
internal static partial class DiagnosticParser
{
    [GeneratedRegex(@"^\s*(?<file>(?:[A-Za-z]:)?[^():*?""<>|\s][^():*?""<>|]*?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>fatal error|error|warning|info|hidden|message)\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<msg>.*?)(?:\s+\[[^\[\]]+\])?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MsBuildStyle();

    [GeneratedRegex(@"^\s*(?<file>(?:[A-Za-z]:)?[^:*?""<>|\s][^:*?""<>|]*?):(?<line>\d+):(?<col>\d+)\s+-\s+(?<sev>error|warning|info)\s+(?<code>[A-Z]+\d+)?:?\s*(?<msg>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TscPretty();

    [GeneratedRegex(@"^\s*(?<file>(?:[A-Za-z]:)?[^:*?""<>|\s][^:*?""<>|]*?\.[A-Za-z0-9]{1,6}):(?<line>\d+)(?::(?<col>\d+))?:?\s*(?:(?<sev>fatal error|error|warning|note|info|E|W|F|C)(?:\[(?<code2>[\w-]+)\])?:)?\s*(?<code>[A-Z]{1,4}\d{2,5})?\s*(?<msg>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ColonStyle();

    [GeneratedRegex(@"^(?<sev>error|warning)(?:\[(?<code>E\d+|[\w:]+)\])?:\s*(?<msg>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RustHead();

    [GeneratedRegex(@"^\s*-->\s*(?<file>.+?):(?<line>\d+):(?<col>\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RustLoc();

    [GeneratedRegex(@"^\s+(?<line>\d+):(?<col>\d+)\s+(?<sev>error|warning)\s+(?<msg>.+?)\s{2,}(?<code>[\w@/\-]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EslintStylishItem();

    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|/|\./)?[\w.\-\\/ ]+\.(?:[jt]sx?|mjs|cjs|vue)$", RegexOptions.CultureInvariant)]
    private static partial Regex EslintStylishFile();

    public static List<Diagnostic> Parse(IEnumerable<string> lines)
    {
        var list = new List<Diagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? rustSev = null, rustCode = null, rustMsg = null;
        string? stylishFile = null;

        void Add(Diagnostic d)
        {
            var file = d.File.Trim().Trim('"');
            if (file.Length == 0 || file.Length > 400 || file.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            var sev = NormalizeSeverity(d.Severity);
            var nd = d with { File = file, Severity = sev, Message = d.Message.Trim() };
            if (seen.Add($"{nd.File}|{nd.Line}|{nd.Col}|{nd.Code}|{nd.Message}".ToLowerInvariant())) list.Add(nd);
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.Length > 3000) continue;

            if (RustLoc().Match(line) is { Success: true } loc && rustSev is not null)
            {
                Add(new Diagnostic(loc.Groups["file"].Value, Int(loc, "line"), Int(loc, "col"), rustSev, rustCode, rustMsg ?? ""));
                rustSev = null;
                continue;
            }
            if (RustHead().Match(line) is { Success: true } rh && !line.Contains("aborting due to", StringComparison.Ordinal)
                && !line.StartsWith("error: could not compile", StringComparison.Ordinal))
            {
                (rustSev, rustCode, rustMsg) = (rh.Groups["sev"].Value, rh.Groups["code"].Success ? rh.Groups["code"].Value : null, rh.Groups["msg"].Value);
                continue;
            }
            if (MsBuildStyle().Match(line) is { Success: true } mb)
            {
                Add(new Diagnostic(mb.Groups["file"].Value, Int(mb, "line"), Int(mb, "col"), mb.Groups["sev"].Value, Opt(mb, "code"), mb.Groups["msg"].Value));
                continue;
            }
            if (TscPretty().Match(line) is { Success: true } tp)
            {
                Add(new Diagnostic(tp.Groups["file"].Value, Int(tp, "line"), Int(tp, "col"), tp.Groups["sev"].Value, Opt(tp, "code"), tp.Groups["msg"].Value));
                continue;
            }
            if (EslintStylishFile().IsMatch(line.Trim()) && !line.StartsWith(' '))
            {
                stylishFile = line.Trim();
                continue;
            }
            if (stylishFile is not null && EslintStylishItem().Match(line) is { Success: true } es)
            {
                Add(new Diagnostic(stylishFile, Int(es, "line"), Int(es, "col"), es.Groups["sev"].Value, es.Groups["code"].Value, es.Groups["msg"].Value));
                continue;
            }
            // go build/vet пишут «./main.go:5:2: undefined: foo» — без слова severity: это ошибка.
            if (ColonStyle().Match(line) is { Success: true } cs
                && (cs.Groups["sev"].Success || cs.Groups["code"].Success || cs.Groups["col"].Success && cs.Groups["file"].Value.EndsWith(".go", StringComparison.OrdinalIgnoreCase)))
            {
                var code = Opt(cs, "code") ?? Opt(cs, "code2");
                var sev = cs.Groups["sev"].Success ? cs.Groups["sev"].Value : cs.Groups["code"].Success ? "warning" : "error";
                // «at x.js:12:5» и прочие строки стека — не диагностики.
                if (line.TrimStart().StartsWith("at ", StringComparison.Ordinal)) continue;
                Add(new Diagnostic(cs.Groups["file"].Value, Int(cs, "line"), Int(cs, "col"), sev, code, cs.Groups["msg"].Value));
            }
        }
        return list;
    }

    private static string NormalizeSeverity(string s) => s.ToLowerInvariant() switch
    {
        "fatal error" or "e" or "f" => "error",
        "w" or "c" => "warning",
        "note" or "hidden" or "message" => "info",
        var x => x,
    };

    private static int Int(Match m, string g) => m.Groups[g].Success && int.TryParse(m.Groups[g].Value, out var v) ? v : 0;

    private static string? Opt(Match m, string g) => m.Groups[g].Success && m.Groups[g].Value.Length > 0 ? m.Groups[g].Value : null;

    // ───────────────────────── стек ─────────────────────────

    [GeneratedRegex(@"^\s*at\s+(?<fn>[^\s(]+(?:\[[^\]]*\])?)\(.*?\)\s+in\s+(?<file>.+?):line\s+(?<line>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DotNetFrame();

    [GeneratedRegex(@"^\s*File\s+""(?<file>[^""]+)"",\s+line\s+(?<line>\d+)(?:,\s+in\s+(?<fn>\S+))?", RegexOptions.CultureInvariant)]
    private static partial Regex PythonFrame();

    [GeneratedRegex(@"^\s*at\s+(?:(?<fn>[^\s(]+)\s+\()?(?:file:///)?(?<file>(?:[A-Za-z]:)?[^():]+):(?<line>\d+):\d+\)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeFrame();

    [GeneratedRegex(@"^\s*at\s+(?<fn>[\w.$<>]+)\((?<file>[\w$.]+\.(?:java|kt|scala|groovy)):(?<line>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex JavaFrame();

    [GeneratedRegex(@"^\s+(?<file>(?:[A-Za-z]:)?[^\s:]+\.go):(?<line>\d+)(?:\s+\+0x[0-9a-f]+)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex GoFrame();

    [GeneratedRegex(@"^\s*(?:\d+:\s+\S+\s*)?at\s+(?<file>\.?[^\s:]+\.rs):(?<line>\d+):\d+", RegexOptions.CultureInvariant)]
    private static partial Regex RustFrame();

    public static List<StackFrame> ParseFrames(IEnumerable<string> lines, int max = 400)
    {
        var frames = new List<StackFrame>();
        var n = 0;
        foreach (var raw in lines)
        {
            n++;
            if (raw.Length > 3000) continue;
            Match m;
            if ((m = DotNetFrame().Match(raw)).Success || (m = PythonFrame().Match(raw)).Success || (m = JavaFrame().Match(raw)).Success
                || (m = NodeFrame().Match(raw)).Success || (m = GoFrame().Match(raw)).Success || (m = RustFrame().Match(raw)).Success)
            {
                frames.Add(new StackFrame(m.Groups["file"].Value.Trim(), int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    m.Groups["fn"].Success && m.Groups["fn"].Value.Length > 0 ? m.Groups["fn"].Value : null, n));
                if (frames.Count >= max) break;
            }
        }
        return frames;
    }
}
