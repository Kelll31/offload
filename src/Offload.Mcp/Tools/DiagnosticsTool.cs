using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_diagnostics: ошибки/предупреждения компилятора или линтера по проекту или файлу — из запуска разрешённой команды
/// (или готового лога), в структурированном виде: путь:строка:столбец, код, сообщение, охватывающий символ и строка кода.
/// Плюс кадры стека из кода проекта (исключения, паники). Без модели.
/// </summary>
internal static class DiagnosticsTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? command, string? kind, string? logPath, string[]? paths, string? severity,
        int maxResults, int timeoutSec)
    {
        maxResults = Math.Clamp(maxResults <= 0 ? 40 : maxResults, 1, 300);
        var sev = (severity ?? "error").Trim().ToLowerInvariant();
        if (sev is not ("error" or "warning" or "all")) throw new ToolException("severity must be error, warning or all.");

        IEnumerable<string> lines;
        string source;
        string? outcome = null;
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            var full = ctx.ResolveRead(logPath);
            if (!File.Exists(full)) throw new ToolException($"Log file '{logPath}' not found.");
            lines = ReadTail(full, MaxLogBytes);
            source = ctx.Display(full) + (new FileInfo(full).Length > MaxLogBytes ? " (last 64 MB)" : "");
        }
        else
        {
            var cmd = await VerifyTool.ResolveCommandAsync(ctx, command, string.IsNullOrWhiteSpace(kind) ? "build" : kind).ConfigureAwait(false);
            var run = await VerifyTool.RunLoggedAsync(ctx, cmd, TimeSpan.FromSeconds(Math.Clamp(timeoutSec <= 0 ? 900 : timeoutSec, 10, 3600))).ConfigureAwait(false);
            lines = ReadTail(run.LogPath, MaxLogBytes);
            source = run.Display;
            outcome = run.Result.TimedOut ? $"`{cmd}` TIMED OUT" : $"`{cmd}` exit {run.Result.ExitCode} ({run.Result.Duration.TotalSeconds:0} s)";
        }

        var materialized = lines.ToList();
        var all = DiagnosticParser.Parse(materialized);
        var filtered = all.Where(d => sev == "all" || d.Severity == sev || sev == "warning" && d.Severity == "error").ToList();
        if (paths is { Length: > 0 })
        {
            var filters = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Replace('\\', '/').Trim()).ToList();
            filtered = filtered.Where(d =>
            {
                var p = ShortPath(ctx, d.File);
                return filters.Any(f => Glob.HasWildcards(f) ? Glob.ToRegex(f).IsMatch(p) : p.StartsWith(f, StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith("/" + f, StringComparison.OrdinalIgnoreCase));
            }).ToList();
        }

        var sb = new StringBuilder();
        if (outcome is not null) sb.Append(outcome).Append(" · ");
        sb.Append($"source: {source} · {all.Count(d => d.Severity == "error")} errors, {all.Count(d => d.Severity == "warning")} warnings parsed\n");
        if (filtered.Count > 0)
        {
            var byCode = filtered.Where(d => d.Code is not null).GroupBy(d => d.Code!).OrderByDescending(g => g.Count()).Take(8).ToList();
            if (byCode.Count > 1) sb.Append("by code: ").Append(string.Join(", ", byCode.Select(g => $"{g.Key}×{g.Count()}"))).Append('\n');
            foreach (var g in filtered.Take(maxResults).GroupBy(d => ShortPath(ctx, d.File)))
            {
                var file = TryLoad(ctx, g.First().File);
                sb.Append(g.Key).Append('\n');
                foreach (var d in g.OrderBy(d => d.Line))
                {
                    sb.Append($"  {d.Line}{(d.Col > 0 ? ":" + d.Col : "")} {d.Severity} {d.Code} {Short(d.Message, 240)}");
                    if (file is not null && d.Line >= 1 && d.Line <= file.Lines.Length)
                    {
                        if (Symbols.Enclosing(file.Symbols, d.Line) is { } e) sb.Append($"   [in {e.QualifiedName}]");
                        sb.Append("\n      ").Append(Short(file.Lines[d.Line - 1].Trim(), 160));
                    }
                    sb.Append('\n');
                }
            }
            if (filtered.Count > maxResults) sb.Append($"… {filtered.Count - maxResults} more (raise max_results or filter by paths)\n");
        }
        else
        {
            sb.Append(all.Count == 0 ? "No compiler/linter diagnostics recognized in the output.\n" : $"No {sev} diagnostics match the filter.\n");
        }

        var frames = ProjectFrames(ctx, DiagnosticParser.ParseFrames(materialized), 12);
        if (frames.Count > 0)
        {
            sb.Append("stack frames in project code (innermost first as logged):\n");
            foreach (var f in frames) sb.Append("  ").Append(f).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public const long MaxLogBytes = 64L * 1024 * 1024;

    /// <summary>Строки файла; у очень больших — только конец (диагностика и итоги сборки почти всегда там).</summary>
    internal static IEnumerable<string> ReadTail(string path, long maxBytes)
    {
        var length = new FileInfo(path).Length;
        if (length <= maxBytes) return File.ReadLines(path);
        return Tail();

        IEnumerable<string> Tail()
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(length - maxBytes, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            reader.ReadLine(); // неполная первая строка
            string? line;
            while ((line = reader.ReadLine()) is not null) yield return line;
        }
    }

    /// <summary>Путь из вывода компилятора → путь относительно рабочей папки (если внутри неё).</summary>
    internal static string ShortPath(ToolContext ctx, string file)
    {
        try
        {
            var f = file.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.IsPathFullyQualified(f) ? Path.GetFullPath(f) : Path.GetFullPath(Path.Combine(ctx.Roots[0], f));
            return PathGuard.FindRoot(full, ctx.Roots) is not null ? ctx.Display(full) : file.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return file;
        }
    }

    /// <summary>Файл диагностики/кадра, если он внутри рабочей папки и читается (иначе null — без исключений).</summary>
    internal static SourceFile? TryLoad(ToolContext ctx, string file)
    {
        try
        {
            var f = file.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.IsPathFullyQualified(f) ? Path.GetFullPath(f) : Path.GetFullPath(Path.Combine(ctx.Roots[0], f));
            if (PathGuard.FindRoot(full, ctx.Roots) is null || !File.Exists(full)) return null;
            return CodeIndex.LoadOne(ctx, full);
        }
        catch (Exception ex) when (ex is ToolException or ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>Кадры стека, попадающие в код проекта: «путь:строка in Функция — строка кода». Java-кадры (только имя файла) ищутся по имени.</summary>
    internal static List<string> ProjectFrames(ToolContext ctx, List<StackFrame> frames, int max)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fr in frames)
        {
            var file = TryLoad(ctx, fr.File);
            if (file is null) continue;
            var key = file.Display + ":" + fr.Line;
            if (!seen.Add(key)) continue;
            var sym = fr.Line <= file.Lines.Length ? Symbols.Enclosing(file.Symbols, fr.Line) : null;
            var code = fr.Line >= 1 && fr.Line <= file.Lines.Length ? " — " + Short(file.Lines[fr.Line - 1].Trim(), 140) : "";
            result.Add($"{key}{(sym is not null ? " in " + sym.QualifiedName : fr.Function is not null ? " in " + fr.Function : "")}{code}");
            if (result.Count >= max) break;
        }
        return result;
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
