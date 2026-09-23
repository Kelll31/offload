using System.Text;
using Offload.Core;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Tools;

/// <summary>
/// local_solve: полный цикл задачи локально одним вызовом — поиск контекста (как local_find_context) → бриф агенту по шаблону
/// вида задачи (feature / bug / refactor / tests / issue) → правки в git-песочнице → проверка командой с исправлениями →
/// ревью изменений локальной моделью → слияние (или ожидание ревью) → «доказательство результата» для облачной модели:
/// изменённые файлы, проверка, диагностика, находки ревью, нерешённые вопросы.
/// </summary>
internal static class SolveTool
{
    public static async Task<string> RunAsync(ToolContext ctx, string? task, string? kind, string? verifyCommand, string[]? allowedPaths, string[]? contextPaths,
        string? merge, int maxFiles, int maxMinutes, bool background, bool review)
    {
        var t = ToolHelpers.RequireText(task, "task", 8000);
        var k = (kind ?? "feature").Trim().ToLowerInvariant();
        if (k is not ("feature" or "bug" or "refactor" or "tests" or "issue")) throw new ToolException("kind must be feature, bug, refactor, tests or issue.");

        // 1. Команда проверки: явная или по карте проекта (для багов и тестов — тесты, иначе тесты или сборка).
        var verify = verifyCommand;
        string? verifyNote = null;
        if (string.IsNullOrWhiteSpace(verify))
        {
            foreach (var kindOfCheck in new[] { "test", "build" })
            {
                try
                {
                    verify = await VerifyTool.ResolveCommandAsync(ctx, null, kindOfCheck).ConfigureAwait(false);
                    break;
                }
                catch (ToolException ex)
                {
                    verifyNote = ex.Message;
                }
            }
        }

        // 2. Контекст: детерминированный поиск + расширение запроса моделью.
        ctx.Progress.Report("Finding relevant code…");
        var index = await CodeIndex.LoadAsync(ctx, null, codeOnly: true, ctx.Ct).ConfigureAwait(false);
        var terms = FindContextTool.ExtractTerms(t);
        var scored = index.Files.AsParallel().WithCancellation(ctx.Ct)
            .Select(f => FindContextTool.Score(f, terms, [], k == "tests"))
            .Where(s => s.Score > 0).OrderByDescending(s => s.Score).Take(8).ToList();
        var hints = (contextPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        foreach (var s in scored.Take(6)) if (!hints.Contains(s.File.Display, StringComparer.OrdinalIgnoreCase)) hints.Add(s.File.Display);

        var ctxText = new StringBuilder();
        if (scored.Count > 0)
        {
            ctxText.Append("Relevant code found by Offload (verify before relying on it):\n");
            foreach (var s in scored.Take(6))
                ctxText.Append($"- {s.File.Display}: {string.Join(", ", s.Symbols.Take(5).Select(x => $"{x.Kind} {x.QualifiedName} (line {x.Line})"))}\n");
        }

        // 3. Бриф.
        var brief = new StringBuilder();
        brief.Append(k switch
        {
            "bug" => "Fix this bug. First find the root cause (read the code paths involved; if feasible, add a failing test that reproduces it), " +
                     "then fix the cause (not the symptom) and make sure the test passes.\n",
            "tests" => "Write automated tests for the code below. Follow the project's existing test framework and style; cover normal cases, edge cases and errors. " +
                       "Do not change production code unless a test reveals a real bug (then mention it).\n",
            "refactor" => "Refactor as described below. Behavior must stay exactly the same; keep public APIs unless the task says otherwise; update all usages.\n",
            "issue" => "Resolve this issue from the tracker. Decide the minimal correct change, implement it and add/adjust tests.\n",
            _ => "Implement this feature completely (code + tests where the project has tests), following the existing architecture and style.\n",
        });
        brief.Append("\nTASK:\n").Append(t.Trim()).Append("\n\n").Append(ctxText);
        brief.Append("\nWhen done, list: Result, Changed files, and any open questions the requester must answer.");

        var preamble = new StringBuilder($"local_solve ({k})");
        preamble.Append(verify is not null ? $" · verify: `{verify}`" : " · verify: none (no allowlisted build/test command found" + (verifyNote is null ? "" : ": " + verifyNote) + ")");
        if (scored.Count > 0) preamble.Append("\ncontext used: ").Append(string.Join(", ", scored.Take(6).Select(s => s.File.Display)));

        return await AgentTaskTool.RunAsync(ctx, new AgentTaskRequest
        {
            Task = brief.ToString(),
            VerifyCommand = verify,
            ContextPaths = [.. hints.Take(12)],
            Merge = string.IsNullOrWhiteSpace(merge) ? "apply" : merge,
            FixAttempts = 3,
            TimeoutMinutes = maxMinutes <= 0 ? 30 : maxMinutes,
            Background = background,
            AllowedPaths = allowedPaths,
            MaxFiles = maxFiles <= 0 ? 25 : maxFiles,
            Review = review,
            Tool = McpToolNames.Solve,
            Preamble = preamble.ToString(),
        }).ConfigureAwait(false);
    }
}
