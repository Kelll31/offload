using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Resources;

/// <summary>
/// completion/complete: подсказки значений аргументов промптов (kind у solve, target у review/security, пути у tests/explain)
/// и идентификаторов в шаблонах ресурсов (прогоны, задачи). Пути — только через FileGatherer (те же проверки PathGuard, секреты
/// и .gitignore не показываются), не больше <see cref="MaxValues"/>. Любая ошибка — пустой список, а не ошибка протокола.
/// </summary>
internal static class OffloadCompletions
{
    /// <summary>Спецификация MCP: не больше 100 значений; нам хватает 50.</summary>
    public const int MaxValues = 50;

    internal static readonly string[] SolveKinds = ["feature", "bug", "refactor", "tests", "issue"];
    internal static readonly string[] DiffTargets = ["all", "staged", "unstaged"];

    /// <summary>Сколько ждать перебора файлов: подсказка должна быть мгновенной.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(3);

    public static async ValueTask<CompleteResult> HandleAsync(RequestContext<CompleteRequestParams> rc, SessionState state, CancellationToken ct)
    {
        var p = rc.Params;
        var arg = p?.Argument?.Name ?? "";
        var value = p?.Argument?.Value ?? "";
        IReadOnlyList<string> values = [];
        try
        {
            values = p?.Ref switch
            {
                PromptReference pr => await ForPromptAsync(rc, state, pr.Name, arg, value, ct).ConfigureAwait(false),
                ResourceTemplateReference rr => await ForResourceAsync(rc, state, rr.Uri ?? "", arg, value, ct).ConfigureAwait(false),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Debug("mcp", $"completion {arg}: {ex.Message}");
        }
        return Result(values);
    }

    internal static CompleteResult Result(IReadOnlyList<string> values) => new()
    {
        Completion = new Completion
        {
            Values = [.. values.Take(MaxValues)],
            Total = values.Count > MaxValues ? null : values.Count,
            HasMore = values.Count > MaxValues,
        },
    };

    private static async Task<IReadOnlyList<string>> ForPromptAsync(RequestContext<CompleteRequestParams> rc, SessionState state, string prompt, string arg, string value,
        CancellationToken ct) => (prompt, arg) switch
    {
        (OffloadPrompts.Solve, "kind") => Filter(SolveKinds, value),
        (OffloadPrompts.Review or OffloadPrompts.Security, "target") => Filter(DiffTargets, value),
        (OffloadPrompts.Tests, "path") => await PathsAsync(rc, state, value, ct).ConfigureAwait(false),
        // explain: пути через запятую — дополняется последний.
        (OffloadPrompts.Explain, "paths") => await LastOfListAsync(rc, state, value, ct).ConfigureAwait(false),
        _ => [],
    };

    private static async Task<IReadOnlyList<string>> ForResourceAsync(RequestContext<CompleteRequestParams> rc, SessionState state, string uri, string arg, string value,
        CancellationToken ct)
    {
        if (arg != "id") return [];
        var roots = await Workspace.GetRootsAsync(rc.Server, state, ct).ConfigureAwait(false);
        if (uri == ResourceUris.RunTemplate)
        {
            var ids = new List<string>();
            foreach (var root in roots)
            {
                var dir = Path.Combine(root, ".offload", "runs");
                if (!Directory.Exists(dir)) continue;
                ids.AddRange(Directory.EnumerateFiles(dir, "*.log").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(ResourceUris.IsValidRunId));
            }
            return [.. ids.OrderByDescending(x => x, StringComparer.Ordinal).Where(x => x.StartsWith(value, StringComparison.OrdinalIgnoreCase))];
        }
        if (uri == ResourceUris.JobDiffTemplate)
        {
            return [.. JobStore.List(MaxValues, j => PathGuard.JobVisible(j.Root, roots))
                .Select(j => j.Id).Where(x => x.StartsWith(value, StringComparison.OrdinalIgnoreCase))];
        }
        return [];
    }

    internal static List<string> Filter(IEnumerable<string> options, string value) =>
        [.. options.Where(o => o.StartsWith(value.Trim(), StringComparison.OrdinalIgnoreCase))];

    private static async Task<IReadOnlyList<string>> LastOfListAsync(RequestContext<CompleteRequestParams> rc, SessionState state, string value,
        CancellationToken ct)
    {
        var cut = value.LastIndexOf(',');
        var head = cut < 0 ? "" : value[..(cut + 1)] + " ";
        var last = cut < 0 ? value : value[(cut + 1)..];
        var paths = await PathsAsync(rc, state, last.Trim(), ct).ConfigureAwait(false);
        return [.. paths.Select(x => head.TrimStart() + x)];
    }

    /// <summary>Файлы проекта (относительные пути), начинающиеся с введённого; затем — содержащие его. Через FileGatherer/PathGuard.</summary>
    private static async Task<IReadOnlyList<string>> PathsAsync(RequestContext<CompleteRequestParams> rc, SessionState state, string value,
        CancellationToken ct)
    {
        var roots = await Workspace.GetRootsAsync(rc.Server, state, ct).ConfigureAwait(false);
        return await ListPathsAsync(ConfigStore.Reload(), roots, value, ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<string>> ListPathsAsync(AppConfig cfg, IReadOnlyList<string> roots, string value, CancellationToken ct)
    {
        if (roots.Count == 0) return [];
        var typed = value.Replace('\\', '/').TrimStart('/');
        if (typed.Contains("..", StringComparison.Ordinal) || typed.Contains(':', StringComparison.Ordinal)) return [];
        // Перебираем папку, в которой пользователь уже находится (если она есть), иначе корень.
        var slash = typed.LastIndexOf('/');
        var dir = slash > 0 ? typed[..slash] : "";
        var spec = dir.Length > 0 && Directory.Exists(Path.Combine(roots[0], dir.Replace('/', Path.DirectorySeparatorChar))) ? dir : ".";
        var options = new GatherOptions(Math.Max(4096, cfg.Mcp.MaxFileBytes), Math.Max(16 * 1024, cfg.Mcp.MaxTotalBytes),
            cfg.Mcp.SecretFilePatterns ?? [], MaxFiles: 2000, MaxEntriesVisited: 20_000, RedactSecrets: cfg.Mcp.RedactSecrets);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ListTimeout);
        List<string> files;
        try
        {
            files = await FileGatherer.ListFilesAsync([spec], roots, options, new GatherResult(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
        }
        // Служебная папка логов Offload (.offload/) — не исходники проекта.
        var rel = files.Select(f => PathGuard.Display(f, roots)).Where(r => !r.StartsWith(".offload/", StringComparison.OrdinalIgnoreCase)).ToList();
        var starts = rel.Where(r => r.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase);
        var contains = typed.Length == 0 ? Enumerable.Empty<string>() : rel.Where(r => !r.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
            && r.Contains(typed, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase);
        return [.. starts.Concat(contains).Take(MaxValues + 1)];
    }
}
