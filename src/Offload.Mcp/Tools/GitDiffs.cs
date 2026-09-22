using System.Text;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

internal sealed record FileDiff(string Path, string Text, int Added, int Removed, bool Binary);

internal sealed class DiffSet
{
    public string RepoRoot { get; init; } = "";
    public string Description { get; init; } = "";
    public List<FileDiff> Files { get; } = [];
    public List<string> Untracked { get; } = [];
    public List<string> Excluded { get; } = [];
    public bool Truncated { get; set; }
    public int Added => Files.Sum(f => f.Added);
    public int Removed => Files.Sum(f => f.Removed);
}

/// <summary>Получение diff через git на стороне сервера, разбиение по файлам, исключение секретов.</summary>
internal static class GitDiffs
{
    private static readonly string[] DiffFlags = ["--no-color", "--no-ext-diff", "--no-textconv", "-U5", "--find-renames"];

    public static string ResolveRepo(ToolContext ctx, string? workingDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(workingDirectory) ? ctx.Roots[0] : PathGuard.Resolve(workingDirectory, ctx.Roots);
        if (!Directory.Exists(dir)) throw new ToolException($"working_directory '{workingDirectory}' does not exist.");
        if (PathGuard.CheckReadResolved(dir, ctx.Roots, ctx.Cfg.Mcp.SecretFilePatterns) is { } why) throw new ToolException($"Refusing to use '{dir}': {why}.");
        if (Git.Executable is null) throw new ToolException("git is not installed or not on PATH.");
        var root = Git.FindWorkTreeRoot(dir) ?? throw new ToolException($"'{dir}' is not inside a git repository.");
        return root;
    }

    public static async Task<DiffSet> CollectAsync(ToolContext ctx, string repo, string target, bool includeUntracked)
    {
        var t = (target ?? "all").Trim();
        var set = new DiffSet { RepoRoot = repo, Description = Describe(t) };
        var outputs = new List<string>();
        switch (t.ToLowerInvariant())
        {
            case "staged":
                outputs.Add(await RunDiff(ctx, repo, ["diff", "--cached", .. DiffFlags], set).ConfigureAwait(false));
                break;
            case "unstaged":
                outputs.Add(await RunDiff(ctx, repo, ["diff", .. DiffFlags], set).ConfigureAwait(false));
                break;
            case "all" or "":
                var head = await Git.RunAsync(repo, ["rev-parse", "--verify", "-q", "HEAD"], ctx.Ct, maxChars: 1000).ConfigureAwait(false);
                if (head.Success) outputs.Add(await RunDiff(ctx, repo, ["diff", "HEAD", .. DiffFlags], set).ConfigureAwait(false));
                else
                {
                    outputs.Add(await RunDiff(ctx, repo, ["diff", "--cached", .. DiffFlags], set).ConfigureAwait(false));
                    outputs.Add(await RunDiff(ctx, repo, ["diff", .. DiffFlags], set).ConfigureAwait(false));
                }
                break;
            default:
                if (!Git.IsSafeRevision(t))
                    throw new ToolException($"target '{t}' is not a valid git ref or range (use e.g. main, HEAD~3, main...HEAD, or all/staged/unstaged).");
                outputs.Add(await RunDiff(ctx, repo, ["diff", .. DiffFlags, t, "--"], set).ConfigureAwait(false));
                break;
        }
        foreach (var o in outputs) Split(o, set, ctx.Cfg.Mcp.SecretFilePatterns);

        if (includeUntracked && t.ToLowerInvariant() is "all" or "" or "unstaged")
        {
            await Git.RunAsync(repo, ["ls-files", "--others", "--exclude-standard"], ctx.Ct, maxChars: 1, onLine: line =>
            {
                if (line.Length == 0) return true;
                var p = Git.Unquote(line);
                if (PathGuard.IsSecretName(Path.GetFileName(p), ctx.Cfg.Mcp.SecretFilePatterns)) set.Excluded.Add(p);
                else set.Untracked.Add(p);
                return set.Untracked.Count < 500;
            }).ConfigureAwait(false);
        }
        return set;
    }

    private static string Describe(string t) => t.ToLowerInvariant() switch
    {
        "staged" => "staged changes",
        "unstaged" => "unstaged changes",
        "all" or "" => "all uncommitted changes vs HEAD",
        _ => $"git diff {t}",
    };

    private static async Task<string> RunDiff(ToolContext ctx, string repo, string[] args, DiffSet set)
    {
        var res = await Git.RunAsync(repo, args, ctx.Ct, maxChars: 8_000_000, timeout: TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        if (res.TimedOut) throw new ToolException("git diff timed out.");
        if (res.ExitCode != 0)
        {
            var err = res.StdErr.Trim();
            if (err.Length > 400) err = err[..400];
            throw new ToolException($"git failed (exit {res.ExitCode}): {err}");
        }
        if (res.OutputTruncated) set.Truncated = true;
        return res.StdOut;
    }

    /// <summary>Разбить вывод git diff на файлы; секретные файлы отбрасываются (их содержимое не уходит модели).</summary>
    internal static void Split(string output, DiffSet set, IEnumerable<string>? secretPatterns)
    {
        if (string.IsNullOrEmpty(output)) return;
        var lines = output.Split('\n');
        var start = -1;
        void Flush(int end)
        {
            if (start < 0) return;
            var chunk = lines[start..end];
            var path = PathFromHeader(chunk);
            var text = string.Join('\n', chunk);
            if (PathGuard.IsSecretName(Path.GetFileName(path), secretPatterns) || path.Split('/').Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            {
                set.Excluded.Add(path);
                return;
            }
            int added = 0, removed = 0;
            var inHunk = false;
            foreach (var l in chunk)
            {
                if (l.StartsWith("@@", StringComparison.Ordinal)) { inHunk = true; continue; }
                if (!inHunk) continue;
                if (l.StartsWith('+')) added++;
                else if (l.StartsWith('-')) removed++;
            }
            var binary = chunk.Any(l => l.StartsWith("Binary files ", StringComparison.Ordinal));
            var existing = set.Files.FindIndex(f => f.Path == path);
            var fd = new FileDiff(path, text, added, removed, binary);
            if (existing >= 0) set.Files[existing] = fd with { Text = set.Files[existing].Text + "\n" + text, Added = set.Files[existing].Added + added, Removed = set.Files[existing].Removed + removed };
            else set.Files.Add(fd);
        }
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush(i);
                start = i;
            }
        }
        Flush(lines.Length);
    }

    private static string PathFromHeader(string[] chunk)
    {
        foreach (var l in chunk)
        {
            if (l.StartsWith("@@", StringComparison.Ordinal)) break;
            if (l.StartsWith("+++ ", StringComparison.Ordinal) && !l.StartsWith("+++ /dev/null", StringComparison.Ordinal))
                return StripPrefix(Git.Unquote(l[4..].TrimEnd('\t')), "b/");
        }
        foreach (var l in chunk)
        {
            if (l.StartsWith("--- ", StringComparison.Ordinal) && !l.StartsWith("--- /dev/null", StringComparison.Ordinal))
                return StripPrefix(Git.Unquote(l[4..].TrimEnd('\t')), "a/");
            if (l.StartsWith("rename to ", StringComparison.Ordinal)) return l[10..];
        }
        // «diff --git a/x b/x» (двоичные, только режим).
        var h = chunk[0]["diff --git ".Length..];
        var idx = h.LastIndexOf(" b/", StringComparison.Ordinal);
        return idx >= 0 ? Git.Unquote(h[(idx + 3)..]) : h;
    }

    private static string StripPrefix(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : s;

    /// <summary>
    /// Разметка diff номерами строк нового файла («  42|+ code», «    |- old»), чтобы модель ссылалась на path:line.
    /// </summary>
    internal static string Annotate(FileDiff fd)
    {
        var sb = new StringBuilder();
        sb.Append("=== ").Append(fd.Path).Append($" (+{fd.Added} -{fd.Removed}) ===\n");
        if (fd.Binary)
        {
            sb.Append("(binary file changed)\n");
            return sb.ToString();
        }
        var newLine = 0;
        var inHunk = false;
        foreach (var l in fd.Text.Split('\n'))
        {
            if (l.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                var plus = l.IndexOf('+');
                if (plus > 0)
                {
                    var num = new string(l[(plus + 1)..].TakeWhile(char.IsDigit).ToArray());
                    newLine = int.TryParse(num, out var nl) ? nl : 0;
                }
                sb.Append(l).Append('\n');
                continue;
            }
            if (!inHunk) continue;
            if (l.StartsWith('+')) sb.Append($"{newLine,6}|+ ").Append(l[1..]).Append('\n');
            else if (l.StartsWith('-')) sb.Append("      |- ").Append(l[1..]).Append('\n');
            else if (l.StartsWith(' ')) sb.Append($"{newLine,6}|  ").Append(l[1..]).Append('\n');
            else continue;
            if (!l.StartsWith('-')) newLine++;
        }
        return sb.ToString();
    }

    /// <summary>Разрезать размеченный diff на куски не больше budget токенов (по ханкам, затем по строкам).</summary>
    internal static List<string> SplitAnnotated(string annotated, int budget)
    {
        if (Tokens.Estimate(annotated) <= budget) return [annotated];
        var lines = annotated.Split('\n');
        var header = lines[0];
        var parts = new List<string>();
        var sb = new StringBuilder(header + " (continued)\n");
        var used = Tokens.Estimate(header);
        foreach (var l in lines.Skip(1))
        {
            var t = Tokens.Estimate(l) + 1;
            var hunkStart = l.StartsWith("@@", StringComparison.Ordinal);
            if (used + t > budget || (hunkStart && used > budget * 3 / 4))
            {
                parts.Add(sb.ToString());
                sb = new StringBuilder(header + " (continued)\n");
                used = Tokens.Estimate(header);
            }
            sb.Append(l).Append('\n');
            used += t;
        }
        if (sb.Length > header.Length + 14) parts.Add(sb.ToString());
        if (parts.Count > 0) parts[0] = parts[0].Replace(header + " (continued)\n", header + "\n");
        return parts;
    }
}
