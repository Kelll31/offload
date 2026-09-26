namespace Offload.OpenCode.Tests;

/// <summary>
/// Контракт с форматом «opencode run --format json» (ROADMAP 9.1): разбор записанного вывода настоящего OpenCode.
/// opencode-1.18.32-run.jsonl — запись реального запуска проверенной версии (OpenCodeReleases.PinnedVersion; пути
/// песочницы заменены на C:\proj). opencode-error-run.jsonl — ошибки инструмента и провайдера в том же формате.
/// Меняя проверенную версию, запишите её вывод (logs\opencode-last-run.log) в новую фикстуру и прогоните эти тесты.
/// </summary>
public class RunEventsContractTests
{
    private const string Wd = @"C:\proj";

    private static RunEvents Replay(string fixture, List<string?>? progress = null)
    {
        var ev = new RunEvents(Wd);
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)))
        {
            var p = ev.Feed(line);
            progress?.Add(p);
        }
        return ev;
    }

    [Fact]
    public void PinnedVersionFixture_MatchesPinnedVersion()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"opencode-{OpenCodeReleases.PinnedVersion}-run.jsonl")),
            "для проверенной версии OpenCode нужна записанная фикстура её вывода");
    }

    [Fact]
    public void RecordedRun_ParsesToolsTokensAndFinalText()
    {
        var progress = new List<string?>();
        var ev = Replay($"opencode-{OpenCodeReleases.PinnedVersion}-run.jsonl", progress);

        Assert.Equal(14, ev.Events);
        Assert.Equal(4, ev.Steps);
        Assert.Equal(
            new[] { "read calc.py", "read test_calc.py", "edit calc.py", "edit test_calc.py", "bash pytest -q" },
            ev.ToolCalls);
        // Каждый шаг — отдельный запрос: input + cache.read + cache.write.
        Assert.Equal((606 + 3561) + (212 + 4289) + (31 + 4772) + (36 + 4830), ev.PromptTokens);
        Assert.Equal(123 + 272 + 28 + 58, ev.CompletionTokens);
        Assert.Equal("stop", ev.LastFinishReason);
        Assert.Empty(ev.Errors);
        Assert.StartsWith("Result: Added `power(base, exp)` to calc.py", ev.FinalText, StringComparison.Ordinal);
        Assert.Contains("- test_calc.py (modified)", ev.FinalText, StringComparison.Ordinal);
        Assert.Contains("шаг 2: правка: calc.py", progress);
        Assert.Contains("шаг 3: команда: pytest -q", progress);
    }

    [Fact]
    public void ErrorRun_ToolErrorAndProviderErrors()
    {
        var ev = Replay("opencode-error-run.jsonl");

        Assert.Equal(8, ev.Events); // строка журнала без JSON не считается событием
        Assert.Equal(2, ev.Steps);
        Assert.Equal(new[] { "edit src/missing.cs (ошибка)" }, ev.ToolCalls);
        // Ответа после инструмента нет — итог = последний текст.
        Assert.Equal("I will edit the file.", ev.FinalText);
        Assert.Equal(1000 + 200 + 40, ev.PromptTokens);
        Assert.Equal(50 + 10, ev.CompletionTokens);
        Assert.Equal("tool-calls", ev.LastFinishReason);
        // Повторы одной ошибки схлопываются, ANSI-последовательности убираются.
        Assert.Equal(new[] { "Connection refused: 127.0.0.1:8080", "ProviderInitError" }, ev.Errors);
    }

    [Fact]
    public void RecordedRun_TruncatedToolLine_StillCountsTool()
    {
        var line = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"opencode-{OpenCodeReleases.PinnedVersion}-run.jsonl"))
            .First(l => l.Contains("\"tool\":\"edit\"", StringComparison.Ordinal));
        var ev = new RunEvents(Wd);
        Assert.NotNull(ev.FeedTruncated(line[..400]));
        Assert.Equal(new[] { "edit calc.py" }, ev.ToolCalls);
    }
}
