using System.Text;
using System.Text.RegularExpressions;
using Offload.Core.Logging;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>Запуск проверочной команды с полным логом в .offload/runs/ проекта.</summary>
internal sealed record LoggedRun(string Command, VerifyResult Result, string LogPath, string Display);

/// <summary>
/// local_verify: запуск разрешённой команды сборки/тестов/линтера/форматирования на стороне сервера (команда — явная или
/// выбранная по карте проекта для kind). Полный вывод — в .offload/runs/*.log проекта, в IDE — только итог: при успехе
/// строки-сводки и число предупреждений (без модели), при ошибке — структурированные ошибки, кадры стека из кода проекта
/// и разбор лога локальной моделью.
/// </summary>
internal static partial class VerifyTool
{
    public const int KeepLogs = 20;

    public static async Task<string> RunAsync(ToolContext ctx, string? command, string? kind, int timeoutSec, string? focus, bool analyze, int maxAnswerTokens)
    {
        var cmd = await ResolveCommandAsync(ctx, command, kind).ConfigureAwait(false);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec <= 0 ? 900 : timeoutSec, 10, 3600));
        var run = await RunLoggedAsync(ctx, cmd, timeout).ConfigureAwait(false);
        var res = run.Result;
        var scan = Scan(run.LogPath, ctx.Ct);

        var sb = new StringBuilder();
        sb.Append(res.TimedOut
            ? $"`{cmd}` → TIMED OUT after {res.Duration.TotalSeconds:0} s"
            : $"`{cmd}` → {(res.Passed ? "PASSED" : "FAILED")} (exit {res.ExitCode}, {res.Duration.TotalSeconds:0} s)");
        sb.Append($" · {scan.Lines} lines · full log: {run.Display}\n");
        if (scan.Summary.Count > 0)
        {
            sb.Append("summary:\n");
            foreach (var l in scan.Summary) sb.Append("  ").Append(l).Append('\n');
        }
        if (scan.Warnings.Count > 0)
        {
            sb.Append($"warnings: {scan.WarningCount} ({scan.Warnings.Count} distinct)");
            if (res.Passed)
            {
                sb.Append(", first:\n");
                foreach (var w in scan.Warnings.Take(5)) sb.Append("  ").Append(w).Append('\n');
            }
            else sb.Append('\n');
        }
        if (res.Passed) return sb.ToString().TrimEnd();

        var diags = DiagnosticParser.Parse(File.ReadLines(run.LogPath)).Where(d => d.Severity == "error").ToList();
        if (diags.Count > 0)
        {
            sb.Append($"errors ({diags.Count} distinct; details: local_diagnostics log_path=\"{run.Display}\"):\n");
            foreach (var d in diags.Take(15))
                sb.Append($"  {DiagnosticsTool.ShortPath(ctx, d.File)}:{d.Line}{(d.Col > 0 ? ":" + d.Col : "")} {d.Code} {Short(d.Message, 220)}\n");
        }
        else if (scan.Errors.Count > 0)
        {
            sb.Append("errors (distinct, in order):\n");
            foreach (var e in scan.Errors) sb.Append("  ").Append(e).Append('\n');
        }
        else
        {
            sb.Append("last output lines:\n");
            foreach (var l in res.Tail.Where(l => l.Trim().Length > 0).TakeLast(15)) sb.Append("  ").Append(Short(l, 300)).Append('\n');
        }
        var frames = DiagnosticsTool.ProjectFrames(ctx, DiagnosticParser.ParseFrames(File.ReadLines(run.LogPath)), 6);
        if (frames.Count > 0)
        {
            sb.Append("stack frames in project code:\n");
            foreach (var f in frames) sb.Append("  ").Append(f).Append('\n');
        }
        if (!analyze) return sb.ToString().TrimEnd();

        try
        {
            var analysis = await SummarizeLogTool.RunAsync(ctx, run.Display,
                string.IsNullOrWhiteSpace(focus) ? "why the command failed: root error, failing tests, fix" : focus, 0, maxAnswerTokens).ConfigureAwait(false);
            sb.Append("\nlocal model analysis:\n").Append(analysis.Trim());
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException)
        {
            sb.Append("\n(local model analysis unavailable: ").Append(ex.Message).Append(')');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Явная команда (проверка белым списком) или выбранная по карте проекта для kind (build/test/lint/format).</summary>
    internal static async Task<string> ResolveCommandAsync(ToolContext ctx, string? command, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(command)) return VerifyCommand.Validate(command, ctx.Cfg.Mcp.VerifyCommandAllowlist);
        var k = string.IsNullOrWhiteSpace(kind) ? "test" : kind.Trim().ToLowerInvariant();
        if (k is not ("build" or "test" or "lint" or "format")) throw new ToolException("kind must be build, test, lint or format (or pass command).");
        ctx.Progress.Report("Detecting the project's commands…");
        var (map, _) = await ProjectMap.BuildAsync(ctx, ctx.Ct).ConfigureAwait(false);
        var pick = map.Commands.FirstOrDefault(c => c.Kind == k && c.Allowed);
        if (pick is null)
        {
            var known = map.Commands.Where(c => c.Kind == k).Select(c => $"\"{c.Command}\"").ToList();
            throw new ToolException(known.Count > 0
                ? $"No allowlisted {k} command: detected {string.Join(", ", known)} but none is in the Offload allowlist. Pass command explicitly or ask the user to allow it (Offload tray app → Settings → MCP)."
                : $"Could not detect a {k} command for this project; pass command explicitly (e.g. \"dotnet test\", \"npm test\").");
        }
        return VerifyCommand.Validate(pick.Command, ctx.Cfg.Mcp.VerifyCommandAllowlist);
    }

    /// <summary>Выполнить проверенную команду в корне проекта, сохранив полный вывод в .offload/runs/.</summary>
    internal static async Task<LoggedRun> RunLoggedAsync(ToolContext ctx, string validatedCommand, TimeSpan timeout, string? workDir = null)
    {
        var root = ctx.Roots[0];
        var offloadDir = Path.Combine(root, ".offload");
        var (_, logPath) = ctx.ResolveWrite(Path.Combine(offloadDir, "runs", $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Slug(validatedCommand)}.log"));
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        EnsureSelfIgnored(offloadDir);
        VerifyResult res;
        await using (var writer = new StreamWriter(logPath, false, new UTF8Encoding(false)))
        {
            var gate = new object();
            res = await VerifyCommand.RunAsync(validatedCommand, workDir ?? root, timeout, ctx.Progress, ctx.Ct, line =>
            {
                lock (gate) writer.WriteLine(line);
            }).ConfigureAwait(false);
        }
        PruneLogs(Path.GetDirectoryName(logPath)!);
        return new LoggedRun(validatedCommand, res, logPath, ctx.Display(logPath));
    }

    internal sealed record ScanResult(long Lines, List<string> Errors, List<string> Warnings, int WarningCount, List<string> Summary);

    /// <summary>Детерминированный разбор лога: уникальные ошибки, предупреждения и строки-сводки (тестов/сборки).</summary>
    internal static ScanResult Scan(string path, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var seenErr = new HashSet<string>(StringComparer.Ordinal);
        var seenWarn = new HashSet<string>(StringComparer.Ordinal);
        var summary = new List<string>();
        long lines = 0;
        var warningCount = 0;
        foreach (var raw in File.ReadLines(path))
        {
            if ((++lines & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // Строка-сводка («Failed! - Failed: 1, Passed: 5») — итог, а не отдельная ошибка.
            if (SummaryPattern().IsMatch(line))
            {
                summary.Add(Short(line, 200));
                if (summary.Count > 8) summary.RemoveAt(0);
                continue;
            }
            if (NoProblemPattern().IsMatch(line)) continue;
            if (ErrorPattern().IsMatch(line))
            {
                if (errors.Count < 20 && seenErr.Add(Normalize(line))) errors.Add(Short(line, 300));
            }
            else if (WarningPattern().IsMatch(line))
            {
                warningCount++;
                if (seenWarn.Add(Normalize(line)) && warnings.Count < 200) warnings.Add(Short(line, 240));
            }
        }
        return new ScanResult(lines, errors, warnings, warningCount, summary);
    }

    /// <summary>MSBuild повторяет ошибки в конце с суффиксом « [проект]» — считаем их одной.</summary>
    private static string Normalize(string line) => ProjectSuffix().Replace(line, "");

    [GeneratedRegex(@"(\berror\b|\bFAILED\b|\bFAIL\b|^\s*Failed\s|\bfailed\b.*\btests?\b|Exception\b|panicked at|Traceback \(most recent|AssertionError|\bassert(ion)?\b.*\bfail)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"\bwarning\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarningPattern();

    /// <summary>«0 Error(s)», «Failed: 0», «errors: 0» — это не ошибки.</summary>
    [GeneratedRegex(@"(\b0\s+(error|warning|failed|failure)|\b(errors?|warnings?|failed|failures?)\s*[:=]\s*0\b|TreatWarningsAsErrors|-warnaserror|--no-warn)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoProblemPattern();

    [GeneratedRegex(@"^(passed!|failed!|total tests|test run (successful|failed)|tests? (run|summary|result)|build (succeeded|failed)|=+ .*\b(passed|failed|error)\b.* =+$|test result:|tests?:\s+\d|test files\s+\d|ok\s+\S+\s+[\d.]+s|\d+ (passing|failing|passed|failed)|ran \d+ tests?|(passed|failed|skipped|total)\s*:\s*\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPattern();

    [GeneratedRegex(@"\s+\[[^\[\]]+\.(cs|vb|fs|vcx)proj(::[^\]]*)?\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectSuffix();

    /// <summary>.offload/.gitignore со «*» — папка не попадает в git и не мешает ревью изменений.</summary>
    internal static void EnsureSelfIgnored(string offloadDir)
    {
        try
        {
            Directory.CreateDirectory(offloadDir);
            var gi = Path.Combine(offloadDir, ".gitignore");
            if (!File.Exists(gi)) File.WriteAllText(gi, "# Offload: служебные файлы (логи проверок)\n*\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("mcp", $".offload/.gitignore не создан: {ex.Message}");
        }
    }

    private static void PruneLogs(string dir)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(dir, "*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepLogs))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("mcp", $"Старые логи проверок не удалены: {ex.Message}");
        }
    }

    internal static string Slug(string cmd)
    {
        var sb = new StringBuilder();
        foreach (var c in cmd.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        return sb.ToString().Trim('-') is { Length: > 0 } s ? s : "run";
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
