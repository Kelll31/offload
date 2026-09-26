using System.Text.Json;
using Offload.Core;
using Offload.Core.Usage;
using Offload.Llama;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Справедливая очередь GPU: политика слотов, уступка приоритету, агент при одном слоте, доска очереди.</summary>
[Collection("AppPaths")]
public class FairGpuQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GpuRequest Req(GpuPriority p, int parallel, string tool = "t", string? client = "ide") => new(tool, client, p, parallel);

    [Fact]
    public void SlotsFor_AgentNeverTakesTheLastSlot()
    {
        Assert.Empty(GpuQueue.SlotsFor(1, GpuPriority.Agent));
        Assert.Equal([0], GpuQueue.SlotsFor(2, GpuPriority.Agent));
        Assert.Equal([0, 1], GpuQueue.SlotsFor(3, GpuPriority.Agent));
        // Не-агентные вызовы — все слоты, начиная с резервного (старшего).
        Assert.Equal([0], GpuQueue.SlotsFor(1, GpuPriority.Interactive));
        Assert.Equal([2, 1, 0], GpuQueue.SlotsFor(3, GpuPriority.Normal));
        Assert.Equal(16, GpuQueue.SlotsFor(99, GpuPriority.Interactive).Length);
    }

    [Fact]
    public void PriorityFor_InteractiveToolsFirst()
    {
        Assert.Equal(GpuPriority.Interactive, GpuQueue.PriorityFor(McpToolNames.AskFiles));
        Assert.Equal(GpuPriority.Interactive, GpuQueue.PriorityFor(McpToolNames.SummarizeLog));
        Assert.Equal(GpuPriority.Interactive, GpuQueue.PriorityFor(McpToolNames.CommitMessage));
        Assert.Equal(GpuPriority.Interactive, GpuQueue.PriorityFor(McpToolNames.FindContext));
        Assert.Equal(GpuPriority.Interactive, GpuQueue.PriorityFor(McpToolNames.ReviewDiff));
        Assert.Equal(GpuPriority.Normal, GpuQueue.PriorityFor(McpToolNames.WriteFile));
        Assert.Equal(GpuPriority.Normal, GpuQueue.PriorityFor(McpToolNames.AgentTask));
    }

    [Fact]
    public void ShouldYield_OnlyToWaitingHigherPriority_AndNotAfterAging()
    {
        var now = DateTime.UtcNow;
        GpuQueueEntry E(string id, GpuPriority p, string state) => new(id, 1, "t", null, p, state, -1, now, null);
        var waitingInteractive = new[] { E("me", GpuPriority.Normal, GpuQueueEntry.Waiting), E("x", GpuPriority.Interactive, GpuQueueEntry.Waiting) };
        Assert.True(GpuQueue.ShouldYield(GpuPriority.Normal, TimeSpan.FromSeconds(1), waitingInteractive, "me"));
        Assert.True(GpuQueue.ShouldYield(GpuPriority.Agent, TimeSpan.FromSeconds(1), waitingInteractive, "me"));
        Assert.False(GpuQueue.ShouldYield(GpuPriority.Interactive, TimeSpan.Zero, waitingInteractive, "me"));
        // Против голодания: после PriorityAging — на равных.
        Assert.False(GpuQueue.ShouldYield(GpuPriority.Normal, GpuQueue.PriorityAging, waitingInteractive, "me"));
        // Держатель слота (не ждущий) и сам вызов уступки не требуют.
        Assert.False(GpuQueue.ShouldYield(GpuPriority.Normal, TimeSpan.Zero, [E("x", GpuPriority.Interactive, GpuQueueEntry.Holding)], "me"));
        Assert.False(GpuQueue.ShouldYield(GpuPriority.Normal, TimeSpan.Zero, [E("me", GpuPriority.Interactive, GpuQueueEntry.Waiting)], "me"));
        // Равный приоритет не уступает.
        Assert.False(GpuQueue.ShouldYield(GpuPriority.Normal, TimeSpan.Zero, [E("x", GpuPriority.Normal, GpuQueueEntry.Waiting)], "me"));
        Assert.True(GpuQueue.ShouldYield(GpuPriority.Agent, TimeSpan.Zero, [E("x", GpuPriority.Normal, GpuQueueEntry.Waiting)], "me"));
    }

    [Fact]
    public async Task AgentWithSingleSlot_HoldsNoMutex_InteractiveGoesThrough()
    {
        using var home = new TempHome();
        var progress = new ProgressReporter(null, null);
        await using var agent = await GpuQueue.AcquireAsync(Req(GpuPriority.Agent, 1, McpToolNames.AgentTask), progress, Ct);
        Assert.Equal(-1, agent.Index);
        Assert.Equal(0, GpuQueue.CountBusy(1));

        await using (var ask = await GpuQueue.AcquireAsync(Req(GpuPriority.Interactive, 1, McpToolNames.AskFiles), progress, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct))
        {
            Assert.Equal(0, ask.Index);
            var board = GpuQueueBoard.Read();
            Assert.Contains(board, e => e.Tool == McpToolNames.AgentTask && e.State == GpuQueueEntry.Holding && e.Slot == -1);
            Assert.Contains(board, e => e.Tool == McpToolNames.AskFiles && e.State == GpuQueueEntry.Holding && e.Slot == 0);
        }
        Assert.DoesNotContain(GpuQueueBoard.Read(), e => e.Tool == McpToolNames.AskFiles);
    }

    [Fact]
    public async Task TwoSlots_AgentKeepsReservedSlotFree()
    {
        using var home = new TempHome();
        var progress = new ProgressReporter(null, null);
        var agent = await GpuQueue.AcquireAsync(Req(GpuPriority.Agent, 2), progress, Ct);
        Assert.Equal(0, agent.Index);

        // Второй агент ждёт: ему доступен только слот 0.
        var agent2 = GpuQueue.AcquireAsync(Req(GpuPriority.Agent, 2), progress, Ct);
        await Task.Delay(700, Ct);
        Assert.False(agent2.IsCompleted, "второй агент не должен занять резервный слот");

        // Интерактивный вызов проходит сразу — в резервный слот.
        await using (var ask = await GpuQueue.AcquireAsync(Req(GpuPriority.Interactive, 2), progress, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct))
            Assert.Equal(1, ask.Index);

        await agent.DisposeAsync();
        await using var second = await agent2.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, second.Index);
    }

    [Fact]
    public async Task InteractiveWaiter_GetsFreedSlotBeforeNormalWaiter()
    {
        using var home = new TempHome();
        var progress = new ProgressReporter(null, null);
        var holder = await GpuQueue.AcquireAsync(Req(GpuPriority.Normal, 1, "holder"), progress, Ct);
        var normal = GpuQueue.AcquireAsync(Req(GpuPriority.Normal, 1, McpToolNames.WriteFile), progress, Ct);
        await Task.Delay(300, Ct);
        var interactive = GpuQueue.AcquireAsync(Req(GpuPriority.Interactive, 1, McpToolNames.AskFiles), progress, Ct);
        // Обычный ожидающий успевает увидеть интерактивного на доске и перестать ждать мьютекс.
        await Task.Delay(900, Ct);
        var waiting = GpuQueueBoard.Read().Where(e => e.State == GpuQueueEntry.Waiting).Select(e => e.Tool).ToList();
        Assert.Contains(McpToolNames.WriteFile, waiting);
        Assert.Contains(McpToolNames.AskFiles, waiting);

        await holder.DisposeAsync();
        var first = await interactive.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(normal.IsCompleted, "обычный вызов должен пропустить интерактивный вперёд");
        await first.DisposeAsync();
        await using var second = await normal.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(second.Waited > TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AcquireForTool_RecordsQueueWait_AndBoardShowsHolder()
    {
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        var ctx = env.Context(ct: Ct);
        var blocker = await GpuQueue.AcquireAsync(Req(GpuPriority.Normal, 1, McpToolNames.WriteFile, "cursor"), ctx.Progress, Ct);
        var status = StatusTool.QueueLine(1, GpuQueue.CountBusy(1), GpuQueueBoard.Read(), DateTime.UtcNow);
        Assert.Contains("queue: 1/1 slot(s) busy", status);
        Assert.Contains($"slot 0: {McpToolNames.WriteFile} (cursor), held", status);

        var pending = GpuQueue.AcquireAsync(ctx, Ct);
        await Task.Delay(600, Ct);
        status = StatusTool.QueueLine(1, GpuQueue.CountBusy(1), GpuQueueBoard.Read(), DateTime.UtcNow);
        Assert.Contains("1 waiting", status);
        Assert.Contains("waiting: 1 (test", status);
        await blocker.DisposeAsync();
        await using (await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct)) { }
        Assert.True(ctx.Stats.QueueWait >= TimeSpan.FromMilliseconds(500), $"ожидание: {ctx.Stats.QueueWait}");
    }

    [Fact]
    public void Describe_ListsHoldersAndWaiting()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var board = new List<GpuQueueEntry>
        {
            new("a", 1, McpToolNames.AgentTask, "claude-code", GpuPriority.Agent, GpuQueueEntry.Holding, -1, now.AddMinutes(-7), now.AddMinutes(-6)),
            new("b", 1, McpToolNames.AskFiles, null, GpuPriority.Interactive, GpuQueueEntry.Holding, 0, now.AddSeconds(-4), now.AddSeconds(-3)),
            new("c", 1, McpToolNames.WriteFile, "cursor", GpuPriority.Normal, GpuQueueEntry.Waiting, -1, now.AddSeconds(-10), null),
        };
        var text = GpuQueueBoard.Describe(board, now);
        Assert.Contains($"slot 0: {McpToolNames.AskFiles}, held 3 s", text);
        Assert.Contains($"no slot (shares llama-server's own queue): {McpToolNames.AgentTask} (claude-code), held 6 min", text);
        Assert.Contains($"waiting: 1 ({McpToolNames.WriteFile} [normal]; longest 10 s)", text);
        Assert.True(text.IndexOf("slot 0", StringComparison.Ordinal) < text.IndexOf("no slot", StringComparison.Ordinal));

        // Слоты заняты процессом без записи на доске (старая версия Offload) — видно хотя бы число.
        var status = StatusTool.QueueLine(3, 2, board, now);
        Assert.Contains("queue: 2/3 slot(s) busy, 1 waiting", status);
        Assert.Contains("1 slot(s) held without details", status);
    }
}

/// <summary>Честный учёт экономии: детерминированные инструменты, потолок, diff агента, /tokenize, поля UsageRecord.</summary>
[Collection("AppPaths")]
public class SavingsAccountingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<UsageRecord> CaptureUsage(Action body)
    {
        var records = new List<UsageRecord>();
        var savedSink = UsageRecorder.Sink;
        var savedTray = UsageRecorder.IsTrayRunning;
        try
        {
            UsageRecorder.IsTrayRunning = () => false;
            UsageRecorder.Sink = r => { lock (records) records.Add(r); };
            body();
        }
        finally
        {
            UsageRecorder.Sink = savedSink;
            UsageRecorder.IsTrayRunning = savedTray;
        }
        return records;
    }

    [Fact]
    public void Estimate_DeterministicScanIsCappedAtTwentyTimesResponse()
    {
        var s = new ToolStats();
        s.AddScanned(3_000_000, 500); // ≈1M токенов просмотрено
        var response = new string('x', 300); // 100 токенов
        Assert.Equal(Savings.ScanCapFactor * 100 - 100, Savings.Estimate(s, response, null));

        var small = new ToolStats();
        small.AddScanned(900, 1); // 300 токенов < потолка
        Assert.Equal(300 - 100, Savings.Estimate(small, response, null));
        // Поправка токенизатора масштабирует итог.
        Assert.Equal(400, Savings.Estimate(small, response, 2.0));
    }

    [Fact]
    public void Estimate_ModelMaterialAndWrittenCode()
    {
        var s = new ToolStats { TokensRead = 5000, TokensWritten = 100 };
        s.AddScanned(300, 1); // меньше прочитанного — не суммируется с ним
        Assert.Equal(5000 + 100 * Savings.WriteWeight - 10, Savings.Estimate(s, new string('y', 30), null));
        Assert.Equal(0, Savings.Estimate(new ToolStats(), "long answer without any material behind it", null));
    }

    [Fact]
    public async Task DeterministicTool_RecordsUsage_WithFooterAndWorkspace()
    {
        using var env = new TestEnv();
        for (var i = 0; i < 30; i++)
            env.WriteFile($"src/File{i}.cs", $"namespace Demo;\npublic class File{i}\n{{\n    public int Value{i}() => {i};\n" + new string(' ', 10) + "// filler text to scan\n}\n");
        var ctx = env.Context(ct: Ct);
        var text = await SearchCodeTool.RunAsync(ctx, "Value17", "text", null, false, 0, 20, false);
        string final = "";
        var records = CaptureUsage(() => final = ToolRunner.Finish(text, ctx, ok: true));

        Assert.Contains("File17.cs", final);
        Assert.Contains("scanned 30 files", final);
        Assert.Contains("cloud tokens avoided", final);
        var rec = Assert.Single(records);
        Assert.Equal("test", rec.Tool);
        Assert.Null(rec.Model);
        Assert.Equal(0, rec.PromptTokens);
        Assert.True(rec.EstimatedSavedTokens > 0);
        Assert.True(rec.EstimatedSavedTokens <= Savings.ScanCapFactor * Tokens.Estimate(text));
        Assert.StartsWith(Path.GetFileName(env.Workspace) + "#", rec.Workspace);
        Assert.Null(rec.GenerationTps);
        Assert.True(ctx.State.SavedTokens > 0);
    }

    [Fact]
    public void Finish_NothingProcessed_RecordsNothing()
    {
        using var env = new TestEnv();
        var ctx = env.Context(ct: Ct);
        string final = "";
        var records = CaptureUsage(() => final = ToolRunner.Finish("status text", ctx, ok: true));
        Assert.Empty(records);
        Assert.Equal("status text", final);
    }

    [Fact]
    public void Footer_ReportsQueueWaitOverTwoSeconds()
    {
        using var env = new TestEnv();
        var ctx = env.Context(ct: Ct);
        ctx.Stats.AddQueueWait(TimeSpan.FromSeconds(1));
        Assert.Equal("done", ToolRunner.Finish("done", ctx, ok: true));

        ctx.Stats.AddQueueWait(TimeSpan.FromSeconds(6));
        ctx.Stats.AddScanned(3000, 2);
        string final = "";
        var records = CaptureUsage(() => final = ToolRunner.Finish("done", ctx, ok: true));
        Assert.Contains("waited 7 s for a GPU slot", final);
        Assert.Equal(7000, Assert.Single(records).QueueWaitMs);
    }

    [Fact]
    public void DiffAddedTokens_CountsAddedLinesOnly_AndExtrapolatesTruncatedDiff()
    {
        var diff = " a.cs | 3 ++-\n\ndiff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,3 @@\n context line\n-removed line\n+added line one\n+added two\n";
        var expected = Tokens.Estimate("added line one") + 1 + Tokens.Estimate("added two") + 1;
        Assert.Equal(expected, Savings.DiffAddedTokens(diff, 2));
        // numstat знает о 4 добавленных строках, а в обрезанном тексте их 2 — результат удваивается.
        Assert.Equal(expected * 2, Savings.DiffAddedTokens(diff, 4));
        Assert.Equal(0, Savings.DiffAddedTokens("Binary files a/x.png and b/x.png differ\n", 0));
    }

    [Fact]
    public void InsertedTokens_OnlyChangedLines_NotWholeFile()
    {
        var before = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line number {i} of a big file")) + "\n";
        var after = before.Replace("line number 100 of a big file", "CHANGED");
        Assert.Equal(Tokens.Estimate("CHANGED") + 1, Savings.InsertedTokens(before, after));
        Assert.Equal(0, Savings.InsertedTokens(before, before));
        Assert.True(Savings.InsertedTokens(before, after) < Tokens.Estimate(after) / 50);
    }

    [Fact]
    public void WorkspaceId_NameAndStableHash_NoFullPath()
    {
        var a = Savings.WorkspaceId(@"C:\Work\My Project");
        Assert.Matches(@"^My Project#[0-9a-f]{8}$", a);
        // Регистр и завершающий разделитель не меняют хэш (пути Windows регистронезависимы).
        Assert.Equal(a!.Split('#')[1], Savings.WorkspaceId(@"c:\work\my project\")!.Split('#')[1]);
        Assert.NotEqual(a, Savings.WorkspaceId(@"D:\Other\My Project"));
        Assert.DoesNotContain("Work", a);
        Assert.Null(Savings.WorkspaceId(null));
        Assert.Matches(@"^.+#[0-9a-f]{8}$", Savings.WorkspaceId(@"C:\"));
    }

    [Fact]
    public async Task TokenCounter_CalibratesFromTokenize_WithCache()
    {
        TokenCounter.Reset();
        using var llama = new FakeLlamaServer { Tokenizer = s => s.Length / 2 };
        var client = new LlamaClient($"http://127.0.0.1:{llama.Port}", TestEnv.ApiKey);
        var sample = string.Concat(Enumerable.Repeat("public int Add(int a, int b) => a + b;\n", 40));
        var ratio = await TokenCounter.CalibrateAsync(client, "m", sample, Ct);
        var expected = sample.Length / 2 / (double)Tokens.Estimate(sample);
        Assert.NotNull(ratio);
        Assert.Equal(expected, ratio!.Value, 3);
        Assert.Equal(ratio, TokenCounter.RatioFor("m"));

        // Тот же текст — из кэша, без обращения к серверу.
        var calls = llama.TokenizeCalls;
        Assert.Equal(sample.Length / 2, await TokenCounter.CountExactAsync(client, "m", sample, Ct));
        Assert.Equal(calls, llama.TokenizeCalls);
        TokenCounter.Reset();
    }

    [Fact]
    public async Task TokenCounter_NoTokenize_NoCorrection()
    {
        TokenCounter.Reset();
        using var llama = new FakeLlamaServer { Tokenizer = null };
        var client = new LlamaClient($"http://127.0.0.1:{llama.Port}", TestEnv.ApiKey);
        var sample = new string('a', 5000);
        Assert.Null(await TokenCounter.CalibrateAsync(client, "m", sample, Ct));
        Assert.Null(await TokenCounter.CountExactAsync(client, "m", sample, Ct));
        Assert.Null(TokenCounter.RatioFor("m"));
    }

    [Fact]
    public async Task AskFiles_EndToEnd_RecordsTpsWorkspaceAndCalibratedSavings()
    {
        TokenCounter.Reset();
        using var llama = new FakeLlamaServer();
        using var env = new TestEnv(llama.Port);
        env.WriteFile("src/Calc.cs", string.Concat(Enumerable.Range(0, 60).Select(i => $"public int Add{i}(int a, int b) => a + b + {i};\n")));
        llama.Responder = _ => "Add7 is at src/Calc.cs:8.";
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.AskFiles, new() { ["paths"] = new[] { "src" }, ["question"] = "Where is Add7?" });
        Assert.False(isError, text);
        Assert.True(llama.TokenizeCalls > 0, "после вызова модели оценка калибруется через /tokenize");
        Assert.NotNull(TokenCounter.RatioFor("test-coder"));

        Assert.True(await UsageRecorder.FlushAsync(TimeSpan.FromSeconds(10)));
        var rec = Assert.Single(UsageLog.ReadAll());
        Assert.Equal(42.5, rec.GenerationTps);
        Assert.StartsWith(Path.GetFileName(env.Workspace) + "#", rec.Workspace);
        Assert.InRange(rec.QueueWaitMs, 0, 2000);
        Assert.True(rec.EstimatedSavedTokens > 0);
        TokenCounter.Reset();
    }

    [Fact]
    public async Task SearchCode_EndToEnd_NowCountsAsSavedCall()
    {
        using var env = new TestEnv();
        for (var i = 0; i < 10; i++) env.WriteFile($"lib/M{i}.cs", $"class M{i} {{ void Run() {{ Helper.Call{i}(); }} }}\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.SearchCode, new() { ["query"] = "Call3" });
        Assert.False(isError, text);
        Assert.Contains("scanned 10 files", text);
        Assert.True(await UsageRecorder.FlushAsync(TimeSpan.FromSeconds(10)));
        var rec = Assert.Single(UsageLog.ReadAll());
        Assert.Equal(McpToolNames.SearchCode, rec.Tool);
        Assert.Null(rec.Model);
        Assert.Equal(1, h.State.ModelCalls);
    }
}

public class TextUtilTests
{
    [Fact]
    public void Short_CutsWithEllipsis_AndKeepsSurrogatePairs()
    {
        Assert.Equal("", TextUtil.Short(null, 5));
        Assert.Equal("abc", TextUtil.Short("abc", 5));
        Assert.Equal("abcde…", TextUtil.Short("abcdefgh", 5));
        var emoji = "ab\U0001F600cd";
        Assert.Equal("ab…", TextUtil.Short(emoji, 3));
    }

    [Theory]
    [InlineData("[\"a\",\"b\"]", 2)]
    [InlineData("Here you go:\n[\"a\", \"b\", \"c\"]\nHope that helps.", 3)]
    [InlineData("[\"a\"] (see [1] for details)", 1)]
    [InlineData("keywords: [\"x\", \"y]z\"] done]", 2)]
    public void ParseJsonFragment_Array(string text, int count)
    {
        using var doc = TextUtil.ParseJsonFragment(text, JsonValueKind.Array);
        Assert.NotNull(doc);
        Assert.Equal(count, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void ParseJsonFragment_ObjectAndFailures()
    {
        using (var doc = TextUtil.ParseJsonFragment("result: {\"n\": 1, \"s\": \"}\"} trailing }", JsonValueKind.Object))
            Assert.Equal(1, doc!.RootElement.GetProperty("n").GetInt32());
        Assert.Null(TextUtil.ParseJsonFragment("no json here", JsonValueKind.Array));
        Assert.Null(TextUtil.ParseJsonFragment("[not, valid json]", JsonValueKind.Array));
        Assert.Null(TextUtil.ParseJsonFragment(null, JsonValueKind.Object));
        // Объект, когда ждали массив, не подходит.
        Assert.Null(TextUtil.ParseJsonFragment("{\"a\":1}", JsonValueKind.Array));
    }
}
