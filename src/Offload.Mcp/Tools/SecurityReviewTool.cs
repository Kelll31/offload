using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_security_review: проверка безопасности только изменённого кода (git diff + новые файлы). Детерминированные правила
/// по добавленным строкам (секреты — маскируются, опасные API, снятые проверки, новые зависимости, чувствительные файлы), затем
/// по желанию — ревью локальной модели с фокусом на безопасность. Файлы-секреты в diff не попадают (исключаются GitDiffs).
/// </summary>
internal static partial class SecurityReviewTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? target, bool useModel, int maxResults)
    {
        maxResults = Math.Clamp(maxResults <= 0 ? 60 : maxResults, 5, 300);
        var repo = GitDiffs.ResolveRepo(ctx, null);
        ctx.Progress.Report("Collecting git diff…");
        var t = string.IsNullOrWhiteSpace(target) ? "all" : target.Trim();
        var set = await GitDiffs.CollectAsync(ctx, repo, t, includeUntracked: true).ConfigureAwait(false);

        var findings = new List<(string Severity, string Where, string Rule, string Message, string Text)>();
        var sensitive = new List<string>();
        foreach (var f in set.Files)
        {
            var lang = Symbols.LangOf(f.Path);
            if (SensitivePath().IsMatch(f.Path)) sensitive.Add(f.Path);
            if (IsManifest(f.Path)) AddDependencyFindings(f, findings);
            foreach (var (line, op, text) in Lines(f.Text))
            {
                var where = $"{f.Path}:{line}";
                if (op == '-')
                {
                    if (RemovedGuard().IsMatch(text) && !CodeIndex.IsCommentLine(text))
                        findings.Add(("medium", where, "guard-removed", "a security/validation check was removed or changed", text.Trim()));
                    continue;
                }
                foreach (var h in CodeRules.Secrets(text)) findings.Add((h.Severity, where, h.Rule, h.Message, CodeRules.RedactLine(text.Trim())));
                if (CodeIndex.IsCommentLine(text)) continue;
                foreach (var h in CodeRules.Unsafe(lang, text)) findings.Add((h.Severity, where, h.Rule, h.Message, text.Trim()));
            }
        }
        // Новые (неотслеживаемые) файлы — целиком, через те же проверки чтения.
        foreach (var u in set.Untracked.Take(200))
        {
            SourceFile? file;
            try { file = CodeIndex.LoadOne(ctx, Path.Combine(set.RepoRoot, u)); }
            catch (ToolException) { continue; }
            if (file is null) continue;
            if (SensitivePath().IsMatch(u)) sensitive.Add(u + " (new)");
            for (var i = 0; i < file.Lines.Length; i++)
            {
                var text = file.Lines[i];
                if (text.Length > 2000) continue;
                foreach (var h in CodeRules.Secrets(text)) findings.Add((h.Severity, $"{u}:{i + 1}", h.Rule, h.Message, CodeRules.RedactLine(text.Trim())));
                if (CodeIndex.IsCommentLine(text)) continue;
                foreach (var h in CodeRules.Unsafe(file.Lang, text)) findings.Add((h.Severity, $"{u}:{i + 1}", h.Rule, h.Message, text.Trim()));
            }
        }

        var sb = new StringBuilder($"security review of {set.Description}: {set.Files.Count} changed file(s) +{set.Added} −{set.Removed}, {set.Untracked.Count} new\n");
        if (set.Excluded.Count > 0) sb.Append($"secret files excluded from the diff (not read): {string.Join(", ", set.Excluded.Take(10))}\n");
        if (sensitive.Count > 0) sb.Append($"security-sensitive files touched: {string.Join(", ", sensitive.Distinct().Take(15))}\n");
        var order = new Dictionary<string, int> { ["critical"] = 0, ["high"] = 1, ["medium"] = 2, ["low"] = 3, ["info"] = 4 };
        if (findings.Count == 0) sb.Append("rule-based checks: no findings\n");
        else
        {
            sb.Append($"rule-based findings ({findings.Count}):\n");
            foreach (var f in findings.OrderBy(f => CodeIndex.IsTestPath(f.Where)).ThenBy(f => order.GetValueOrDefault(f.Severity, 5)).Take(maxResults))
                sb.Append($"[{f.Severity}] {f.Where} {f.Rule}: {f.Message}{(CodeIndex.IsTestPath(f.Where) ? " [test]" : "")}\n    {Short(f.Text, 160)}\n");
            if (findings.Count > maxResults) sb.Append($"… {findings.Count - maxResults} more\n");
        }

        if (useModel && set.Files.Count > 0)
        {
            try
            {
                var review = await ReviewDiffTool.RunAsync(ctx, null, t,
                    "security only: injection (SQL/command/path traversal), authentication/authorization gaps, secrets handling, unsafe deserialization, " +
                    "SSRF, XSS, TLS/crypto misuse, missing input validation, race conditions on security checks", 1200).ConfigureAwait(false);
                sb.Append("\nlocal model security review (verify each finding):\n").Append(review.Trim());
            }
            catch (Exception ex) when (ex is ToolException or ContextExceededException)
            {
                sb.Append("\n(local model review unavailable: ").Append(ex.Message).Append(')');
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Строки diff с номерами новой версии: (номер, операция '+'/'-'/' ', текст).</summary>
    internal static IEnumerable<(int Line, char Op, string Text)> Lines(string diff)
    {
        var newLine = 0;
        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                continue;
            }
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                var m = HunkNew().Match(raw);
                newLine = m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                inHunk = true;
                continue;
            }
            // Заголовки файла («--- a/x», «+++ b/x») бывают только до первого хунка; «+++counter;» внутри хунка — добавленная строка.
            if (!inHunk || raw.Length == 0) continue;
            var op = raw[0];
            if (op == '+') yield return (newLine++, '+', raw[1..]);
            else if (op == '-') yield return (newLine, '-', raw[1..]);
            else if (op == ' ') newLine++;
        }
    }

    private static bool IsManifest(string path) =>
        Path.GetFileName(path) is "package.json" or "requirements.txt" or "pyproject.toml" or "go.mod" or "Cargo.toml" or "pom.xml" or "build.gradle" or "Directory.Packages.props"
        || path.EndsWith("proj", StringComparison.OrdinalIgnoreCase);

    private static void AddDependencyFindings(FileDiff f, List<(string, string, string, string, string)> findings)
    {
        foreach (var (line, op, text) in Lines(f.Text))
        {
            if (op != '+') continue;
            if (DependencyLine().Match(text) is { Success: true } m)
                findings.Add(("info", $"{f.Path}:{line}", "new-dependency", "dependency added/changed: review its source, license and version", text.Trim()));
            else if (text.Contains("\"postinstall\"", StringComparison.Ordinal) || text.Contains("\"preinstall\"", StringComparison.Ordinal))
                findings.Add(("medium", $"{f.Path}:{line}", "install-script", "npm install script runs arbitrary code on install", text.Trim()));
        }
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex HunkNew();

    [GeneratedRegex(@"(?i)(auth|security|permission|password|login|token|crypto|secret|acl|policy|guard|sanitiz|validat|firewall|cors|csrf|session|jwt|oauth|\.github/workflows/|dockerfile)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePath();

    [GeneratedRegex(@"(?i)(\[Authorize|\[ValidateAntiForgeryToken|RequireAuthorization|\bauthorize\b|IsAuthenticated|\bValidate\w*\(|Sanitize|Escape\w*\(|PathGuard|CheckPermission|HasPermission|verify\s*=\s*True|rejectUnauthorized\s*:\s*true|csrf|@login_required|@permission_required|\.IsInside\(|throw new UnauthorizedAccessException)", RegexOptions.CultureInvariant)]
    private static partial Regex RemovedGuard();

    [GeneratedRegex(@"(PackageReference\s+Include=|""[@\w./-]+""\s*:\s*""[\^~>=<]*\d|^\s*[\w.\-\[\]]+\s*(==|>=|~=)|^\s*require\s|^\s*[\w\-]+\s*=\s*[""{]|<artifactId>)", RegexOptions.CultureInvariant)]
    private static partial Regex DependencyLine();

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
