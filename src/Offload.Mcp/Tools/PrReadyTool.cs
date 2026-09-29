using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;
using static Offload.Mcp.Infrastructure.TextUtil;

namespace Offload.Mcp.Tools;

/// <summary>Запуск проверки в local_pr_ready.</summary>
internal sealed record PrCheckRun
{
    [JsonPropertyName("scope"), Description("related (tests related to the diff) | full (project test command) | build")]
    public required string Scope { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("status"), Description("passed | failed | timed_out")]
    public required string Status { get; init; }

    [JsonPropertyName("exit_code")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("duration_sec")]
    public required double DurationSec { get; init; }

    [JsonPropertyName("log"), Description("Full log, relative to the project root.")]
    public required string Log { get; init; }

    [JsonPropertyName("errors"), Description("Structured errors (path:line code message) or error lines of a failed run.")]
    public required IReadOnlyList<string> Errors { get; init; }
}

/// <summary>Итог local_pr_ready.</summary>
internal sealed record PrReadyOutput
{
    [JsonPropertyName("verdict"), Description("READY | READY WITH WARNINGS | NOT READY")]
    public required string Verdict { get; init; }

    [JsonPropertyName("blockers")]
    public required IReadOnlyList<string> Blockers { get; init; }

    [JsonPropertyName("warnings")]
    public required IReadOnlyList<string> Warnings { get; init; }

    [JsonPropertyName("branch")]
    public required string Branch { get; init; }

    [JsonPropertyName("base")]
    public required string Base { get; init; }

    [JsonPropertyName("merge_base"), Description("Merge-base commit (short hash).")]
    public required string MergeBase { get; init; }

    [JsonPropertyName("ahead")]
    public required int Ahead { get; init; }

    [JsonPropertyName("behind")]
    public required int Behind { get; init; }

    [JsonPropertyName("files_changed")]
    public required int FilesChanged { get; init; }

    [JsonPropertyName("lines_added")]
    public required int LinesAdded { get; init; }

    [JsonPropertyName("lines_removed")]
    public required int LinesRemoved { get; init; }

    [JsonPropertyName("uncommitted_files"), Description("Uncommitted/untracked files in the working tree (not part of the PR).")]
    public required int UncommittedFiles { get; init; }

    [JsonPropertyName("conflict_check"), Description("clean | conflicts | skipped")]
    public required string ConflictCheck { get; init; }

    [JsonPropertyName("conflicts"), Description("Files that conflict with the base.")]
    public required IReadOnlyList<string> Conflicts { get; init; }

    [JsonPropertyName("checks"), Description("Build/test runs (only with run_tests=true).")]
    public required IReadOnlyList<PrCheckRun> Checks { get; init; }

    [JsonPropertyName("security"), Description("Rule-based security findings on the branch diff.")]
    public required IReadOnlyList<string> Security { get; init; }

    [JsonPropertyName("hygiene"), Description("Added TODO/FIXME, debug leftovers, conflict markers, large or binary files.")]
    public required IReadOnlyList<string> Hygiene { get; init; }

    [JsonPropertyName("review"), Description("Local-model review findings (a draft: verify before acting).")]
    public required IReadOnlyList<string> Review { get; init; }

    [JsonPropertyName("pr_title")]
    public string? PrTitle { get; init; }

    [JsonPropertyName("pr_body")]
    public string? PrBody { get; init; }
}

/// <summary>
/// local_pr_ready: полная проверка текущей ветки перед PR одним вызовом — сводка ветки относительно базы (merge-base),
/// конфликты слияния без касания рабочего дерева (git merge-tree --write-tree), сборка и тесты (сначала связанные с diff,
/// затем полный прогон — только разрешённые команды), правила безопасности (секреты — блокер), гигиена добавленных строк,
/// ревью локальной моделью и черновик PR. Вердикт и блокеры — первыми, подробности — после. Файлы проекта не меняет.
/// </summary>
internal static partial class PrReadyTool
{
    public const string Ready = "READY";
    public const string ReadyWithWarnings = "READY WITH WARNINGS";
    public const string NotReady = "NOT READY";

    /// <summary>Порог «большого» файла, добавленного или изменённого в ветке.</summary>
    public const long LargeFileBytes = 1024 * 1024;

    public static async Task<string> RunAsync(ToolContext ctx, string? baseRef, bool runTests, bool useModel, bool prText, int maxMinutes)
    {
        var deadline = new TaskDeadline(TimeSpan.FromMinutes(Math.Clamp(maxMinutes <= 0 ? 20 : maxMinutes, 2, 120)));
        var repo = GitDiffs.ResolveRepo(ctx, null);
        var blockers = new List<string>();
        var warnings = new List<string>();
        var details = new StringBuilder();

        // 1. База, merge-base, сводка ветки.
        ctx.Progress.Report("Resolving the base branch…");
        var baseName = await ResolveBaseAsync(ctx, repo, baseRef).ConfigureAwait(false);
        var branch = (await GitText(ctx, repo, ["rev-parse", "--abbrev-ref", "HEAD"]).ConfigureAwait(false)).Trim();
        var mergeBase = (await GitText(ctx, repo, ["merge-base", baseName, "HEAD"], $"'{baseName}' and HEAD have no common history.").ConfigureAwait(false)).Trim();
        var mergeBaseShort = mergeBase[..Math.Min(10, mergeBase.Length)];
        var counts = (await GitText(ctx, repo, ["rev-list", "--left-right", "--count", baseName + "...HEAD"]).ConfigureAwait(false)).Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        var behind = counts.Length > 0 ? int.Parse(counts[0], CultureInfo.InvariantCulture) : 0;
        var ahead = counts.Length > 1 ? int.Parse(counts[1], CultureInfo.InvariantCulture) : 0;
        var range = baseName + "...HEAD";
        var set = await GitDiffs.CollectAsync(ctx, repo, range, includeUntracked: false).ConfigureAwait(false);
        ctx.Stats.AddScanned(set.Files.Sum(f => (long)f.Text.Length), set.Files.Count);
        // Драйверы слияния и фильтры из конфигурации репозитория git запустил бы сам (merge-tree, status): в чужом репозитории
        // это выполнение произвольных команд. Такие шаги пропускаются; сравнение деревьев выше команд не запускает.
        var commandConfig = await Git.RepoCommandConfigAsync(repo, ctx.Ct).ConfigureAwait(false);
        var configNote = commandConfig.Count == 0 ? null
            : $"the repository's git config defines commands git would run ({string.Join(", ", commandConfig.Take(5))}); review .git/config";
        if (configNote is not null) warnings.Add("merge-conflict and working-tree checks were skipped: " + configNote);
        var uncommitted = configNote is null ? await UncommittedCountAsync(ctx, repo).ConfigureAwait(false) : 0;

        if (ahead == 0) blockers.Add($"the branch has no commits ahead of {baseName}: nothing to open a PR with");
        if (uncommitted > 0) warnings.Add($"{uncommitted} uncommitted/untracked file(s) in the working tree are NOT part of the PR (commit or stash them)");
        if (behind > 0) warnings.Add($"the branch is {behind} commit(s) behind {baseName}: consider rebasing/merging before the PR");
        details.Append($"branch: {branch} → {baseName} (merge-base {mergeBaseShort}) · {ahead} ahead, {behind} behind · ")
            .Append($"{ToolHelpers.Plural(set.Files.Count + set.Excluded.Count, "file")} +{set.Added} −{set.Removed}")
            .Append(uncommitted > 0 ? $" · {uncommitted} uncommitted" : "").Append('\n');
        if (set.Truncated) warnings.Add("the branch diff is too large and was truncated: checks cover only its beginning");

        // 2. Конфликты с базой без касания рабочего дерева.
        ctx.Progress.Report("Checking merge conflicts with the base…");
        var (conflictCheck, conflicts, conflictNote) = configNote is null
            ? await ConflictsAsync(ctx, repo, baseName).ConfigureAwait(false)
            : ("skipped", [], "git config defines merge drivers/filters");
        if (conflictCheck == "conflicts") blockers.Add($"merge conflicts with {baseName}: {string.Join(", ", conflicts.Take(10))}{(conflicts.Count > 10 ? ", …" : "")}");
        else if (conflictCheck == "skipped" && configNote is null) warnings.Add("merge conflicts were not checked: " + conflictNote);
        details.Append("merge conflicts: ").Append(conflictCheck switch
        {
            "clean" => "none (git merge-tree)",
            "conflicts" => string.Join(", ", conflicts.Take(20)),
            _ => "not checked (" + conflictNote + ")",
        }).Append('\n');

        // 3. Сборка и тесты.
        var checks = new List<PrCheckRun>();
        if (runTests && ahead > 0)
        {
            details.Append("checks:\n");
            await RunChecksAsync(ctx, range, deadline, checks, blockers, warnings, details).ConfigureAwait(false);
        }
        else
        {
            details.Append(runTests ? "checks: skipped (no commits ahead)\n" : "checks: skipped (run_tests=false; no project code was run)\n");
        }

        // 4. Правила безопасности: секреты — блокер, прочее high/critical — предупреждения.
        var (findings, sensitive) = SecurityReviewTool.RuleFindings(ctx, set);
        var security = new List<string>();
        foreach (var f in findings.OrderBy(f => f.Secret ? 0 : 1).ThenBy(f => SeverityRank(f.Severity)).Take(30))
        {
            var isTest = CodeIndex.IsTestPath(f.Where);
            security.Add($"[{f.Severity}] {f.Where} {f.Rule}: {f.Message}{(isTest ? " [test]" : "")} — {Short(f.Text, 120)}");
            if (f.Secret && !isTest) blockers.Add($"possible secret added at {f.Where} ({f.Rule}); remove it and rotate the credential");
            else if (f.Secret) warnings.Add($"secret-looking value in a test file at {f.Where} ({f.Rule}): make sure it is fake");
            else if (f.Severity is "critical" or "high") warnings.Add($"risky API at {f.Where}: {f.Message}");
        }
        if (set.Excluded.Count > 0) warnings.Add($"secret-pattern files changed in the branch (not read): {string.Join(", ", set.Excluded.Take(5))}");
        details.Append("security (rules): ").Append(security.Count == 0 ? "no findings" : $"{findings.Count} finding(s)").Append('\n');
        foreach (var s in security.Take(12)) details.Append("  ").Append(s).Append('\n');
        if (sensitive.Count > 0) details.Append("  security-sensitive files touched: ").Append(string.Join(", ", sensitive.Distinct().Take(10))).Append('\n');

        // 5. Гигиена добавленных строк и файлов.
        var hygiene = Hygiene(set, blockers, warnings);
        hygiene.AddRange(await LargeFilesAsync(ctx, repo, set, warnings).ConfigureAwait(false));
        details.Append("hygiene: ").Append(hygiene.Count == 0 ? "clean" : $"{hygiene.Count} item(s)").Append('\n');
        foreach (var h in hygiene.Take(20)) details.Append("  ").Append(h).Append('\n');
        if (hygiene.Count > 20) details.Append($"  … {hygiene.Count - 20} more\n");

        // 6. Ревью локальной моделью (с фокусом и на безопасность) — находки critical/high только предупреждения.
        var review = new List<string>();
        if (useModel && set.Files.Count > 0)
        {
            try
            {
                var text = await ReviewDiffTool.RunAsync(ctx, null, range,
                    "bugs and security (injection, authz, secrets, unsafe deserialization, SSRF, crypto misuse, missing validation)", 1200).ConfigureAwait(false);
                review.AddRange(text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('[')).Take(15));
                foreach (var r in review.Where(r => r.StartsWith("[critical]", StringComparison.OrdinalIgnoreCase) || r.StartsWith("[high]", StringComparison.OrdinalIgnoreCase)).Take(5))
                    warnings.Add("review (local model, verify): " + Short(r, 220));
                details.Append("review (local model, draft): ").Append(review.Count == 0 ? "no significant issues" : $"{review.Count} finding(s)").Append('\n');
                foreach (var r in review) details.Append("  ").Append(Short(r, 240)).Append('\n');
            }
            catch (Exception ex) when (ex is ToolException or ContextExceededException)
            {
                details.Append("review (local model): unavailable — ").Append(Short(ex.Message, 200)).Append('\n');
            }
        }
        else
        {
            details.Append(useModel ? "review: nothing to review\n" : "review: skipped (use_model=false)\n");
        }

        // 7. Черновик PR.
        string? prTitle = null, prBody = null;
        if (prText && useModel && set.Files.Count > 0)
        {
            try
            {
                ctx.Progress.Report("Writing the PR description…");
                var model = await ctx.GetModelAsync().ConfigureAwait(false);
                var draft = StripAttribution(await CommitMessageTool.DescribeAsync(ctx, model, set, "pr", "en").ConfigureAwait(false));
                (prTitle, prBody) = SplitTitle(draft);
                details.Append("\nPR draft (local model, edit before use):\n").Append(draft.Trim()).Append('\n');
            }
            catch (Exception ex) when (ex is ToolException or ContextExceededException)
            {
                details.Append("PR draft: unavailable — ").Append(Short(ex.Message, 200)).Append('\n');
            }
        }

        var verdict = blockers.Count > 0 ? NotReady : warnings.Count > 0 ? ReadyWithWarnings : Ready;
        ctx.Structured = new PrReadyOutput
        {
            Verdict = verdict,
            Blockers = blockers,
            Warnings = warnings,
            Branch = branch,
            Base = baseName,
            MergeBase = mergeBaseShort,
            Ahead = ahead,
            Behind = behind,
            FilesChanged = set.Files.Count + set.Excluded.Count,
            LinesAdded = set.Added,
            LinesRemoved = set.Removed,
            UncommittedFiles = uncommitted,
            ConflictCheck = conflictCheck,
            Conflicts = conflicts,
            Checks = checks,
            Security = security,
            Hygiene = hygiene,
            Review = review,
            PrTitle = prTitle,
            PrBody = prBody,
        };

        var sb = new StringBuilder($"PR readiness: {verdict} · {branch} → {baseName}\n");
        if (blockers.Count > 0)
        {
            sb.Append($"blockers ({blockers.Count}):\n");
            foreach (var b in blockers) sb.Append("  - ").Append(b).Append('\n');
        }
        if (warnings.Count > 0)
        {
            sb.Append($"warnings ({warnings.Count}):\n");
            foreach (var w in warnings.Take(20)) sb.Append("  - ").Append(w).Append('\n');
            if (warnings.Count > 20) sb.Append($"  … {warnings.Count - 20} more\n");
        }
        sb.Append("\ndetails:\n").Append(details);
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────── git ─────────────────────────

    /// <summary>База: явная (ветка/тег/коммит, не диапазон) или origin/HEAD → main → master (локальные, затем origin/).</summary>
    internal static async Task<string> ResolveBaseAsync(ToolContext ctx, string repo, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var b = requested.Trim();
            if (!Git.IsSafeRevision(b) || b.Contains("..", StringComparison.Ordinal))
                throw new ToolException("base must be a branch, tag or commit (e.g. main, origin/main), not a range.");
            if (!await CommitExistsAsync(ctx, repo, b).ConfigureAwait(false))
                throw new ToolException($"base '{b}' is not a known branch or commit; fetch it or pass another one (e.g. origin/main).");
            return b;
        }
        var candidates = new List<string>();
        var originHead = await Git.RunAsync(repo, ["symbolic-ref", "-q", "--short", "refs/remotes/origin/HEAD"], ctx.Ct, maxChars: 1000).ConfigureAwait(false);
        if (originHead.Success && originHead.StdOut.Trim().Length > 0) candidates.Add(originHead.StdOut.Trim());
        candidates.AddRange(["main", "master", "origin/main", "origin/master"]);
        foreach (var c in candidates.Distinct(StringComparer.Ordinal))
            if (Git.IsSafeRevision(c) && await CommitExistsAsync(ctx, repo, c).ConfigureAwait(false)) return c;
        throw new ToolException("Could not detect the base branch (no origin/HEAD, main or master); pass base, e.g. base=\"develop\".");
    }

    private static async Task<bool> CommitExistsAsync(ToolContext ctx, string repo, string rev) =>
        (await Git.RunAsync(repo, ["rev-parse", "--verify", "-q", rev + "^{commit}"], ctx.Ct, maxChars: 1000).ConfigureAwait(false)).Success;

    private static async Task<string> GitText(ToolContext ctx, string repo, List<string> args, string? error = null)
    {
        var r = await Git.RunAsync(repo, args, ctx.Ct, maxChars: 100_000).ConfigureAwait(false);
        if (!r.Success) throw new ToolException(error ?? $"git {args[0]} failed: {GitSandbox.FirstLines(r.StdErr, 3)}");
        return r.StdOut;
    }

    private static async Task<int> UncommittedCountAsync(ToolContext ctx, string repo)
    {
        var r = await Git.RunAsync(repo, ["status", "--porcelain", "--untracked-files=normal"], ctx.Ct, maxChars: 1_000_000).ConfigureAwait(false);
        return r.Success ? r.StdOut.Split('\n').Count(l => l.Trim().Length > 0) : 0;
    }

    /// <summary>
    /// Конфликты слияния базы с HEAD через git merge-tree --write-tree (git ≥ 2.38): рабочее дерево, индекс и ссылки не
    /// меняются (пишутся только объекты). Старый git — проверка пропускается с пояснением.
    /// </summary>
    internal static async Task<(string Check, List<string> Files, string? Note)> ConflictsAsync(ToolContext ctx, string repo, string baseName)
    {
        var version = await Git.RunAsync(repo, ["version"], ctx.Ct, maxChars: 1000).ConfigureAwait(false);
        var m = GitVersion().Match(version.StdOut);
        if (!m.Success) return ("skipped", [], "could not read the git version");
        var (major, minor) = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        if (major < 2 || major == 2 && minor < 38) return ("skipped", [], $"git {major}.{minor} is older than 2.38 (no merge-tree --write-tree)");
        var r = await Git.RunAsync(repo, ["merge-tree", "--write-tree", "--name-only", "--no-messages", baseName, "HEAD"], ctx.Ct,
            maxChars: 1_000_000, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (r.TimedOut) return ("skipped", [], "git merge-tree timed out");
        if (r.ExitCode == 0) return ("clean", [], null);
        if (r.ExitCode != 1) return ("skipped", [], "git merge-tree failed: " + GitSandbox.FirstLines(r.StdErr, 2));
        // Первая строка — OID дерева, дальше — конфликтующие файлы.
        var files = r.StdOut.Split('\n').Skip(1).Select(l => Git.Unquote(l.Trim())).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return ("conflicts", files.Count > 0 ? files : ["(files not reported)"], null);
    }

    [GeneratedRegex(@"(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex GitVersion();

    // ───────────────────────── сборка и тесты ─────────────────────────

    /// <summary>Сначала — тесты, связанные с diff (local_impact); если они прошли и есть время — полная команда тестов (или сборки).</summary>
    private static async Task RunChecksAsync(ToolContext ctx, string range, TaskDeadline deadline, List<PrCheckRun> checks,
        List<string> blockers, List<string> warnings, StringBuilder details)
    {
        var perRun = (int)Math.Clamp(deadline.Remaining.TotalSeconds, 60, 900);
        try
        {
            ctx.Progress.Report("Running tests related to the branch diff…");
            await ImpactTool.RunAsync(ctx, range, null, runTests: true, 40, perRun).ConfigureAwait(false);
            if (ctx.Structured is ImpactOutput impact)
            {
                foreach (var t in impact.TestRuns)
                {
                    checks.Add(new PrCheckRun
                    {
                        Scope = "related", Command = t.Command, Status = t.Status, ExitCode = t.ExitCode, DurationSec = t.DurationSec, Log = t.Log,
                        Errors = t.Errors,
                    });
                }
            }
        }
        catch (Exception ex) when (ex is ToolException or ContextExceededException)
        {
            details.Append("  related tests: not run — ").Append(Short(ex.Message, 200)).Append('\n');
        }
        ctx.Structured = null;

        var relatedFailed = checks.Any(c => c.Status == "failed");
        if (relatedFailed)
        {
            details.Append("  full test run skipped: related tests failed\n");
        }
        else if (deadline.Remaining < TaskDeadline.MinVerifyTime)
        {
            details.Append("  full test run skipped: max_minutes budget used up\n");
            warnings.Add("the full test suite was not run (time budget); run local_verify kind=test");
        }
        else
        {
            string? cmd = null;
            var scope = "full";
            foreach (var kind in new[] { "test", "build" })
            {
                try
                {
                    cmd = await VerifyTool.ResolveCommandAsync(ctx, null, kind).ConfigureAwait(false);
                    scope = kind == "test" ? "full" : "build";
                    break;
                }
                catch (ToolException ex)
                {
                    details.Append($"  {kind}: ").Append(Short(ex.Message, 200)).Append('\n');
                }
            }
            if (cmd is not null)
            {
                ctx.Progress.Report($"Running `{cmd}`…");
                var run = await VerifyTool.RunLoggedAsync(ctx, cmd, deadline.ForStep(TaskDeadline.MinVerifyTime) ?? TaskDeadline.MinVerifyTime).ConfigureAwait(false);
                VerifyTool.LinkRun(ctx, run);
                checks.Add(new PrCheckRun
                {
                    Scope = scope,
                    Command = cmd,
                    Status = run.Result.Passed ? "passed" : run.Result.TimedOut ? "timed_out" : "failed",
                    ExitCode = run.Result.ExitCode,
                    DurationSec = Math.Round(run.Result.Duration.TotalSeconds, 1),
                    Log = run.Display,
                    Errors = run.Result.Passed ? [] : ErrorsOf(ctx, run),
                });
            }
        }

        foreach (var c in checks)
        {
            details.Append($"  {c.Scope} `{c.Command}` → {c.Status.ToUpperInvariant()} (exit {c.ExitCode}, {c.DurationSec:0} s) · log: {c.Log}\n");
            foreach (var e in c.Errors.Take(8)) details.Append("      ").Append(Short(e, 240)).Append('\n');
            if (c.Status == "failed")
                blockers.Add($"{c.Scope} {(c.Scope == "build" ? "build" : "tests")} failed: `{c.Command}`{(c.Errors.Count > 0 ? " — " + Short(c.Errors[0], 200) : "")}");
            else if (c.Status == "timed_out")
                warnings.Add($"`{c.Command}` timed out: the result is unknown");
        }
        if (checks.Count == 0) warnings.Add("no build/test command was run (none detected or allowlisted): the branch is unverified");
    }

    /// <summary>Ошибки упавшего прогона: структурированные (путь:строка код сообщение), иначе строки-ошибки лога.</summary>
    internal static List<string> ErrorsOf(ToolContext ctx, LoggedRun run)
    {
        var diags = DiagnosticParser.Parse(File.ReadLines(run.LogPath)).Where(d => d.Severity == "error").Take(10).ToList();
        if (diags.Count > 0)
            return [.. diags.Select(d => $"{DiagnosticsTool.ShortPath(ctx, d.File)}:{d.Line} {d.Code} {Short(d.Message, 200)}".Replace("  ", " "))];
        var scan = VerifyTool.Scan(run.LogPath, ctx.Ct);
        return scan.Errors.Count > 0 ? [.. scan.Errors.Take(10)] : [.. run.Result.Tail.Where(l => l.Trim().Length > 0).TakeLast(6).Select(l => Short(l, 240))];
    }

    // ───────────────────────── гигиена ─────────────────────────

    /// <summary>Добавленные строки: маркеры конфликта (блокер), TODO/FIXME/HACK и отладочные остатки (предупреждения), новые двоичные файлы.</summary>
    internal static List<string> Hygiene(DiffSet set, List<string> blockers, List<string> warnings)
    {
        var items = new List<string>();
        int todo = 0, debug = 0;
        foreach (var f in set.Files)
        {
            var isNew = f.Text.Contains("\nnew file mode", StringComparison.Ordinal) || f.Text.StartsWith("new file mode", StringComparison.Ordinal);
            if (f.Binary)
            {
                if (isNew)
                {
                    items.Add($"{f.Path}: binary file added");
                    warnings.Add($"binary file added: {f.Path}");
                }
                continue;
            }
            var lang = Symbols.LangOf(f.Path);
            var isTest = CodeIndex.IsTestPath(f.Path);
            foreach (var (line, op, text) in SecurityReviewTool.Lines(f.Text))
            {
                if (op != '+') continue;
                var where = $"{f.Path}:{line}";
                if (ConflictMarker().IsMatch(text))
                {
                    items.Add($"{where}: merge conflict marker");
                    // В тестовых данных маркеры бывают намеренно (тесты инструментов слияния) — там только предупреждение.
                    if (isTest) warnings.Add($"merge conflict marker in a test file at {where}: make sure it is intended");
                    else blockers.Add($"merge conflict marker left in {where}");
                    continue;
                }
                if (TodoTag().Match(text) is { Success: true } tm)
                {
                    todo++;
                    items.Add($"{where}: {tm.Groups["tag"].Value} added — {Short(CodeRules.RedactLine(text.Trim()), 120)}");
                    continue;
                }
                if (!isTest && !CodeIndex.IsCommentLine(text) && DebugLeftover(lang, f.Path, text) is { } what)
                {
                    debug++;
                    items.Add($"{where}: {what} — {Short(CodeRules.RedactLine(text.Trim()), 120)}");
                }
            }
        }
        if (todo > 0) warnings.Add($"{todo} TODO/FIXME/HACK comment(s) added");
        if (debug > 0) warnings.Add($"{debug} debug leftover(s) added (console output, debugger statements)");
        return items;
    }

    /// <summary>
    /// Отладочные остатки по языку — консервативно: только явные debugger/breakpoint и вывод в консоль там, где он почти
    /// наверняка не часть программы (не Program.cs/main/cli/scripts).
    /// </summary>
    internal static string? DebugLeftover(CodeLang lang, string path, string text)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var p = "/" + path.Replace('\\', '/').ToLowerInvariant();
        var entry = name is "program.cs" or "main.py" or "__main__.py" or "cli.py" or "main.go" or "main.rs"
                    || p.Contains("/scripts/", StringComparison.Ordinal) || p.Contains("/tools/", StringComparison.Ordinal) || p.Contains("/bin/", StringComparison.Ordinal);
        return lang switch
        {
            CodeLang.CSharp when CsDebugger().IsMatch(text) => "Debugger.Break/Launch",
            CodeLang.CSharp when !entry && CsConsole().IsMatch(text) => "console output (C#)",
            CodeLang.TypeScript when JsDebugger().IsMatch(text) => "debugger statement",
            CodeLang.TypeScript when !entry && JsConsole().IsMatch(text) => "console.log",
            CodeLang.Python when PyDebugger().IsMatch(text) => "breakpoint/pdb",
            CodeLang.Python when !entry && PyPrint().IsMatch(text) => "print()",
            CodeLang.Rust when RustDbg().IsMatch(text) => "dbg!",
            CodeLang.Java or CodeLang.Kotlin when JavaPrint().IsMatch(text) => "System.out/printStackTrace",
            CodeLang.Php when PhpDump().IsMatch(text) => "var_dump/print_r/dd",
            CodeLang.Ruby when RubyDebugger().IsMatch(text) => "binding.pry/byebug",
            _ => null,
        };
    }

    [GeneratedRegex(@"^(<{7}|>{7})( |$)", RegexOptions.CultureInvariant)]
    private static partial Regex ConflictMarker();

    [GeneratedRegex(@"(//|#|/\*|^\s*\*|<!--|--)\s*.*?\b(?<tag>TODO|FIXME|HACK|XXX)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TodoTag();

    [GeneratedRegex(@"\bDebugger\.(Break|Launch)\(\)", RegexOptions.CultureInvariant)]
    private static partial Regex CsDebugger();

    [GeneratedRegex(@"\bConsole\.Write(Line)?\(", RegexOptions.CultureInvariant)]
    private static partial Regex CsConsole();

    [GeneratedRegex(@"^\s*debugger\s*;?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex JsDebugger();

    [GeneratedRegex(@"\bconsole\.(log|debug|trace)\(", RegexOptions.CultureInvariant)]
    private static partial Regex JsConsole();

    [GeneratedRegex(@"\bbreakpoint\(\)|\b(i?pdb)\.set_trace\(|^\s*import\s+i?pdb\b", RegexOptions.CultureInvariant)]
    private static partial Regex PyDebugger();

    [GeneratedRegex(@"^\s*print\(", RegexOptions.CultureInvariant)]
    private static partial Regex PyPrint();

    [GeneratedRegex(@"\bdbg!\(", RegexOptions.CultureInvariant)]
    private static partial Regex RustDbg();

    [GeneratedRegex(@"\bSystem\.(out|err)\.print(ln)?\(|\.printStackTrace\(\)", RegexOptions.CultureInvariant)]
    private static partial Regex JavaPrint();

    [GeneratedRegex(@"\b(var_dump|print_r|dd)\(", RegexOptions.CultureInvariant)]
    private static partial Regex PhpDump();

    [GeneratedRegex(@"\bbinding\.pry\b|^\s*byebug\b", RegexOptions.CultureInvariant)]
    private static partial Regex RubyDebugger();

    /// <summary>Файлы ветки больше 1 МБ (по дереву HEAD; одним вызовом git ls-tree для изменённых путей).</summary>
    private static async Task<List<string>> LargeFilesAsync(ToolContext ctx, string repo, DiffSet set, List<string> warnings)
    {
        var paths = set.Files.Select(f => f.Path).Take(300).ToList();
        if (paths.Count == 0) return [];
        var items = new List<string>();
        var r = await Git.RunAsync(repo, ["ls-tree", "-r", "-l", "HEAD", "--", .. paths.Select(p => ":(literal)" + p)], ctx.Ct, maxChars: 2_000_000).ConfigureAwait(false);
        if (!r.Success) return [];
        foreach (var line in r.StdOut.Split('\n'))
        {
            // «<mode> <type> <sha> <size>\t<path>»
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var meta = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 4 || !long.TryParse(meta[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= LargeFileBytes) continue;
            var path = Git.Unquote(line[(tab + 1)..].Trim());
            items.Add($"{path}: large file ({size / 1024.0 / 1024.0:0.0} MB)");
            warnings.Add($"large file in the branch: {path} ({size / 1024.0 / 1024.0:0.0} MB)");
        }
        return items;
    }

    // ───────────────────────── текст PR ─────────────────────────

    /// <summary>
    /// Убрать строки атрибуции ИИ-ассистентов (Co-Authored-By, «Generated with …», «🤖 …»), если модель их добавила:
    /// в тексте PR их быть не должно.
    /// </summary>
    internal static string StripAttribution(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => !Attribution().IsMatch(l)).ToList();
        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@"(?i)(co-authored-by\s*:|🤖|\b(generated|created|written|authored|made)\s+(with|by|using)\b.*\b(claude|anthropic|chatgpt|gpt-?\d|copilot|openai|gemini|llm|ai|assistant|offload)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex Attribution();

    /// <summary>«Title: …» в первой строке → (заголовок, остальное).</summary>
    internal static (string? Title, string Body) SplitTitle(string draft)
    {
        var t = draft.Trim();
        var nl = t.IndexOf('\n');
        var first = (nl < 0 ? t : t[..nl]).Trim();
        if (!first.StartsWith("Title:", StringComparison.OrdinalIgnoreCase)) return (null, t);
        return (first[6..].Trim(), nl < 0 ? "" : t[(nl + 1)..].Trim());
    }

    private static int SeverityRank(string s) => s switch { "critical" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4 };
}
