using System.Diagnostics;
using System.Text;
using Offload.Core.Config;
using Offload.Core.Processes;
using Offload.Llama;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

public class OutputCleanerTests
{
    [Theory]
    [InlineData("<think>hmm</think>\nAnswer", "Answer")]
    [InlineData("<think>a</think>X<think>b</think>Y", "XY")]
    [InlineData("<think>never closed, all reasoning", "")]
    [InlineData("  <THINK>\nstill thinking", "")]
    [InlineData("reasoning tail</think>\n\nReal answer", "Real answer")]
    [InlineData("Plain answer", "Plain answer")]
    public void StripThink(string input, string expected) => Assert.Equal(expected, OutputCleaner.StripThink(input));

    [Fact]
    public void StripFence()
    {
        Assert.Equal("class A {}\n", OutputCleaner.StripFence("```csharp\nclass A {}\n```"));
        Assert.Equal("class A {}\n", OutputCleaner.StripFence("```\nclass A {}\n```\n"));
        Assert.Equal("x = 1\n", OutputCleaner.StripFence("Here is the file:\n```python\nx = 1\n```\nHope it helps."));
        Assert.Equal("no fence\n", OutputCleaner.StripFence("no fence"));
        // Markdown: блок внутри документа не вырезается.
        var md = "# Title\n\nText\n```bash\nls\n```\n";
        Assert.Equal(md, OutputCleaner.StripFence(md, allowInnerBlock: false));
        // Обрыв без закрывающей ограды — берём тело.
        Assert.Equal("line1\nline2\n", OutputCleaner.StripFence("```cs\nline1\nline2"));
    }

    [Theory]
    [InlineData("NO_CHANGES", true)]
    [InlineData("  `NO_CHANGES`  ", true)]
    [InlineData("no changes.", true)]
    [InlineData("NO_CHANGES but here is code", false)]
    public void NoChanges(string text, bool expected) => Assert.Equal(expected, OutputCleaner.IsNoChanges(text));

    [Fact]
    public void Elision()
    {
        var original = "class A\n{\n    void M() { }\n}\n";
        Assert.True(OutputCleaner.HasElision("class A\n{\n    // ... rest of the code unchanged\n}\n", original));
        Assert.True(OutputCleaner.HasElision("class A\n{\n    // ...\n}\n", original));
        Assert.True(OutputCleaner.HasElision("# existing code here\n", "x = 1\n"));
        Assert.False(OutputCleaner.HasElision("class A\n{\n    void M() { Log(); }\n}\n", original));
        // Маркер уже был в исходнике — не считается.
        Assert.False(OutputCleaner.HasElision("// ...\nx\n", "// ...\ny\n"));
    }
}

public class TextCodecTests
{
    [Fact]
    public void Cp1251_DetectedAndRoundTripped()
    {
        var text = "// Модуль расчёта\r\nunit Calc;\r\n";
        var bytes = TextCodec.Cp1251.GetBytes(text);
        var (decoded, format) = TextCodec.Decode(bytes);
        Assert.Equal(text, decoded);
        Assert.Equal(TextEncodingKind.Windows1251, format.Kind);
        Assert.Equal("\r\n", format.NewLine);
        var back = TextCodec.Encode(decoded.Replace("\r\n", "\n") + "// ещё\n", format, out var fallback);
        Assert.False(fallback);
        Assert.Equal(text + "// ещё\r\n", TextCodec.Cp1251.GetString(back));
    }

    [Fact]
    public void Cp1251_FallsBackToUtf8BomForUnrepresentable()
    {
        var format = new TextFormat(TextEncodingKind.Windows1251, "\n", true);
        var bytes = TextCodec.Encode("emoji 😀", format, out var fallback);
        Assert.True(fallback);
        Assert.Equal(0xEF, bytes[0]);
    }

    [Fact]
    public void Utf8Bom_Crlf_NoFinalNewline_Preserved()
    {
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("a\r\nb")).ToArray();
        var (text, format) = TextCodec.Decode(original);
        Assert.Equal(TextEncodingKind.Utf8Bom, format.Kind);
        Assert.False(format.FinalNewline);
        var encoded = TextCodec.Encode("a\nb\nc\n", format, out _);
        Assert.Equal(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("a\r\nb\r\nc")).ToArray(), encoded);
        Assert.Equal("a\r\nb", text);
    }

    [Fact]
    public void Utf16_AndTruncatedUtf8()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("привет\n")).ToArray();
        Assert.False(TextCodec.LooksBinary(utf16));
        Assert.Equal("привет\n", TextCodec.Decode(utf16).Text);
        // Обрезка посреди многобайтового символа не должна приводить к cp1251.
        var bytes = Encoding.UTF8.GetBytes("abc привет");
        var (t, f) = TextCodec.Decode(bytes.AsSpan(0, bytes.Length - 1), truncated: true);
        Assert.Equal(TextEncodingKind.Utf8, f.Kind);
        Assert.Equal("abc приве", t);
        Assert.True(TextCodec.LooksBinary(new byte[] { 1, 2, 0, 3 }));
    }
}

public class LogPrefilterTests
{
    [Fact]
    public void KeepsHeadErrorsTailAndCollapsesRepeats()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 10_000; i++)
        {
            if (i == 5000) lines.Add("src/Foo.cs(12,5): error CS0103: The name 'x' does not exist");
            else if (i is > 6000 and <= 6050) lines.Add("2026-09-22 10:00:01 WARN retrying connection: failed");
            else if (i == 7000) lines.Add("\x1b[31mFAILED\x1b[0m Tests.FooTests.Bar");
            else lines.Add($"info line {i}");
        }
        var d = LogPrefilter.Filter(lines);
        Assert.Equal(10_000, d.TotalLines);
        var text = LogPrefilter.Render(d, 20_000);
        Assert.Contains("L1: info line 1", text);
        Assert.Contains("L5000: src/Foo.cs(12,5): error CS0103", text);
        Assert.Contains("L4999: info line 4999", text);
        Assert.Contains("[×50]", text);
        Assert.Contains("L7000: FAILED Tests.FooTests.Bar", text);
        Assert.DoesNotContain("\x1b", text, StringComparison.Ordinal);
        Assert.Contains("L10000: info line 10000", text);
        Assert.Contains("lines omitted", text);
        Assert.True(text.Length < 20_000);

        var small = LogPrefilter.Render(d, 1500);
        Assert.True(small.Length < 2500, small.Length.ToString());
        Assert.Contains("L5000", small);
    }

    [Fact]
    public void CarriageReturnProgressKeepsLastSegment() =>
        Assert.Equal("100%", LogPrefilter.CleanLine("10%\r50%\r100%"));

    [Fact]
    public void TailOffsetNumbers()
    {
        var d = LogPrefilter.Filter(["a", "error here", "b"], headLines: 1, tailLines: 5, firstLineNumber: 101);
        var text = LogPrefilter.Render(d, 5000);
        Assert.Contains("L101: a", text);
        Assert.Contains("L102: error here", text);
        Assert.Contains("L103: b", text);
    }
}

public class ChunkAndDiffTests
{
    private static GatheredFile File(string name, int lines, int width = 40) => new()
    {
        FullPath = @"C:\x\" + name,
        Display = name,
        Text = string.Join("\n", Enumerable.Range(1, lines).Select(i => $"{i}:" + new string('x', width))) + "\n",
        Format = TextFormat.DefaultUtf8Lf,
        SizeBytes = lines * width,
    };

    [Fact]
    public void Planner_SmallFilesShareOneChunk()
    {
        var plan = ChunkPlanner.Plan([File("a.cs", 10), File("b.cs", 10)], 4000, 8);
        Assert.Single(plan.Chunks);
        Assert.Equal(2, plan.Chunks[0].Parts.Count);
        Assert.Contains("=== a.cs (10 lines) ===\n1| 1:", plan.Chunks[0].Render());
    }

    [Fact]
    public void Planner_SplitsLargeFileContiguously()
    {
        var big = File("big.cs", 3000);
        var plan = ChunkPlanner.Plan([big], 3000, 100);
        Assert.True(plan.Chunks.Count > 1);
        var parts = plan.Chunks.SelectMany(c => c.Parts).ToList();
        Assert.Equal(1, parts[0].FromLine);
        for (var i = 1; i < parts.Count; i++) Assert.Equal(parts[i - 1].ToLine + 1, parts[i].FromLine);
        Assert.Equal(3000, parts[^1].ToLine);
        Assert.All(plan.Chunks, c => Assert.True(c.Tokens <= 3000));
        Assert.Empty(plan.Uncovered);
    }

    [Fact]
    public void Planner_ReportsUncoveredBeyondMaxChunks()
    {
        var files = Enumerable.Range(0, 10).Select(i => File($"f{i}.cs", 200)).ToList();
        var plan = ChunkPlanner.Plan(files, 3000, 2);
        Assert.Equal(2, plan.Chunks.Count);
        Assert.NotEmpty(plan.Uncovered);
        Assert.Equal(10, plan.CoveredFiles(files) + plan.Uncovered.Count + plan.Partial.Count);
    }

    [Fact]
    public void Myers_CountsAndUnified()
    {
        string[] a = ["a", "b", "c", "d", "e", "f", "g"];
        string[] b = ["a", "b", "X", "d", "e", "f", "g", "h"];
        Assert.Equal((2, 1), LineDiff.Stats(a, b));
        var u = LineDiff.Unified(a, b, "a/x", "b/x", context: 1);
        Assert.Equal("--- a/x\n+++ b/x\n@@ -2,3 +2,3 @@\n b\n-c\n+X\n d\n@@ -7 +7,2 @@\n g\n+h\n", u);
        Assert.Equal("", LineDiff.Unified(a, a, "a", "b"));
        Assert.Equal("--- /dev/null\n+++ b/n\n@@ -0,0 +1,2 @@\n+1\n+2\n", LineDiff.Unified([], ["1", "2"], "/dev/null", "b/n"));
    }

    [Fact]
    public void Myers_RandomEditsReconstructBothSides()
    {
        var rnd = new Random(42);
        for (var round = 0; round < 200; round++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => ((char)('a' + rnd.Next(5))).ToString()).ToList();
            var b = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => ((char)('a' + rnd.Next(5))).ToString()).ToList();
            var edits = LineDiff.Compute(a, b);
            var ra = edits.Where(e => e.Op != DiffOp.Insert).Select(e => a[e.OldIndex]).ToList();
            var rb = edits.Where(e => e.Op != DiffOp.Delete).Select(e => b[e.NewIndex]).ToList();
            Assert.Equal(a, ra);
            Assert.Equal(b, rb);
            Assert.All(edits.Where(e => e.Op == DiffOp.Equal), e => Assert.Equal(a[e.OldIndex], b[e.NewIndex]));
            // Минимальность: число правок = |a| + |b| - 2·LCS.
            var (add, del) = LineDiff.Stats(edits);
            Assert.Equal(a.Count + b.Count - 2 * Lcs(a, b), add + del);
        }
    }

    private static int Lcs(List<string> a, List<string> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        for (var j = 1; j <= b.Count; j++)
            dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[a.Count, b.Count];
    }
}

public class ModelRequestTests
{
    [Fact]
    public void ThinkingDisabledPerModel()
    {
        var qwen = new InstalledModel { Reasoning = ReasoningControl.EnableThinkingKwarg, Sampling = new SamplingSettings { Temperature = 0.7, PresencePenalty = 1.5 } };
        var r = LocalModel.BuildRequest(qwen, [ChatMessage.User("hi")], 100);
        Assert.Equal(false, r.ChatTemplateKwargs!["enable_thinking"]);
        Assert.Null(r.ReasoningEffort);
        Assert.Equal(1.5, r.PresencePenalty);
        Assert.Equal(0.7, r.Temperature);

        var oss = new InstalledModel { Reasoning = ReasoningControl.ReasoningEffort };
        var r2 = LocalModel.BuildRequest(oss, [ChatMessage.User("hi")], 100);
        Assert.Equal("low", r2.ReasoningEffort);
        Assert.Null(r2.ChatTemplateKwargs);
        Assert.Null(r2.PresencePenalty);

        var plain = LocalModel.BuildRequest(null, [ChatMessage.User("hi")], 5);
        Assert.Null(plain.ChatTemplateKwargs);
        Assert.Equal(16, plain.MaxTokens);
    }

    [Fact]
    public void LlamaErrorsMappedToEnglish()
    {
        Assert.Contains("API key", LocalModel.MapError(new LlamaApiException("Неверный ключ", 401)).Message);
        Assert.IsType<ContextExceededException>(LocalModel.MapError(new LlamaApiException("Запрос не помещается в контекст модели", 400)));
        Assert.Contains("Lost connection", LocalModel.MapError(new LlamaApiException("нет связи")).Message);
    }

    [Fact]
    public void Instructions_FitIn2048Chars()
    {
        Assert.True(ServerInstructions.Build(null).Length <= ServerInstructions.MaxLength);
        var cfg = new AppConfig { SetupCompleted = true };
        cfg.Models.Installed.Add(new InstalledModel { Id = "m", DisplayName = new string('Q', 500), Quant = "Q4_K_M", RecommendedContext = 131072 });
        var text = ServerInstructions.Build(cfg);
        Assert.True(text.Length <= ServerInstructions.MaxLength, text.Length.ToString());
        // Обычное имя модели помещается целиком, с запасом.
        var normal = new AppConfig { SetupCompleted = true };
        normal.Models.Installed.Add(new InstalledModel { Id = "m", DisplayName = "Qwen3-Coder-30B-A3B-Instruct", Quant = "UD-Q4_K_XL", RecommendedContext = 65536 });
        var normalText = ServerInstructions.Build(normal);
        Assert.EndsWith("the server auto-starts on the first call.", normalText);
        Assert.True(normalText.Length <= 2048, normalText.Length.ToString());
        Assert.Contains("ToolSearch query \"offload\" (max_results 25)", text);
        Assert.Contains("Status at startup: model QQQ", text);
        foreach (var tool in Offload.Core.McpToolNames.All.Where(t => t != Offload.Core.McpToolNames.Status))
            Assert.Contains(tool, text);
    }

    [Fact]
    public void CommitMessageCleanup()
    {
        Assert.Equal("feat: add x", CommitMessageTool.Clean("\"feat: add x.\""));
        Assert.Equal("fix: y\n\n- body", CommitMessageTool.Clean("```\nfix: y\n- body\n```"));
        var longSubject = "feat: " + string.Join(' ', Enumerable.Repeat("word", 30));
        Assert.True(CommitMessageTool.Clean(longSubject).Length <= 72);
    }

    [Fact]
    public void RewriteOutcomes()
    {
        var t = new EditTarget
        {
            Path = @"C:\x\a.cs", Display = "src/a.cs", OriginalText = "class A {}\n", CurrentText = "class A {}\n",
            Format = TextFormat.DefaultUtf8Lf, Tokens = 5,
        };
        Assert.Null(EditFilesTool.ApplyRewrite(t, "NO_CHANGES", false).NewText);
        Assert.Contains("cut off", EditFilesTool.ApplyRewrite(t, "class A { int x; }", true).Note);
        Assert.Equal("unchanged", EditFilesTool.ApplyRewrite(t, "```cs\nclass A {}\n```", false).Note);
        Assert.Equal("class A { int x; }\n", EditFilesTool.ApplyRewrite(t, "=== src/a.cs ===\nclass A { int x; }\n=== end of src/a.cs ===", false).NewText);
        Assert.Contains("abbreviated", EditFilesTool.ApplyRewrite(t, "class A {\n// ... existing code ...\n}", false).Note);
    }

    [Fact]
    public void GitDiffSplitExcludesSecretsAndAnnotates()
    {
        var diff = "diff --git a/src/a.cs b/src/a.cs\nindex 1..2 100644\n--- a/src/a.cs\n+++ b/src/a.cs\n@@ -10,3 +10,4 @@ class A\n ctx\n-old\n+new1\n+new2\n ctx2\n" +
                   "diff --git a/.env b/.env\n--- a/.env\n+++ b/.env\n@@ -1 +1 @@\n-KEY=1\n+KEY=2\n";
        var set = new DiffSet();
        GitDiffs.Split(diff, set, new McpSettings().SecretFilePatterns);
        Assert.Single(set.Files);
        Assert.Equal(".env", Assert.Single(set.Excluded));
        Assert.Equal((2, 1), (set.Files[0].Added, set.Files[0].Removed));
        var ann = GitDiffs.Annotate(set.Files[0]);
        Assert.Contains("    10|  ctx", ann);
        Assert.Contains("      |- old", ann);
        Assert.Contains("    11|+ new1", ann);
        Assert.Contains("    13|  ctx2", ann);
        Assert.DoesNotContain("KEY", ann);
    }

    [Fact]
    public void PorcelainParsing()
    {
        var map = StrayGuard.ParsePorcelain(" M src/a.cs\0?? new.txt\0R  b.cs\0a.cs\0", @"C:\repo");
        Assert.Equal(" M", map[@"C:\repo\src\a.cs"]);
        Assert.Equal("??", map[@"C:\repo\new.txt"]);
        Assert.Equal("R ", map[@"C:\repo\b.cs"]);
        Assert.Equal("R ", map[@"C:\repo\a.cs"]);
        Assert.Equal(@"C:\repo\src\x.cs", EditFilesTool.ParseChanged("M src/x.cs", @"C:\repo"));
    }

    [Fact]
    public void Git_UnquoteAndRevisions()
    {
        Assert.Equal("a\"b\\c", Git.Unquote("\"a\\\"b\\\\c\""));
        Assert.Equal("тест.cs", Git.Unquote("\"\\321\\202\\320\\265\\321\\201\\321\\202.cs\""));
        Assert.True(Git.IsSafeRevision("main...HEAD"));
        Assert.True(Git.IsSafeRevision("HEAD~3"));
        Assert.False(Git.IsSafeRevision("--output=x"));
        Assert.False(Git.IsSafeRevision("main; rm"));
        Assert.False(Git.IsSafeRevision("a b"));
    }

    [Fact]
    public void Cap_TruncatesWithMarker()
    {
        var s = new string('a', 10_000);
        var c = ToolRunner.Cap(s, 2000);
        Assert.True(c.Length <= 2000);
        Assert.Contains("truncated by Offload", c);
        Assert.Equal("short", ToolRunner.Cap("short", 2000));
    }
}

[Collection("AppPaths")]
public class GpuQueueTests
{
    [Fact]
    public async Task SerializesAcrossHoldersAndReleases()
    {
        using var home = new TempHome();
        var progress = new ProgressReporter(null, null);
        var first = await GpuQueue.AcquireAsync(1, progress, CancellationToken.None);
        Assert.Equal(1, GpuQueue.CountBusy(1));
        var secondTask = GpuQueue.AcquireAsync(1, progress, CancellationToken.None);
        await Task.Delay(1500);
        Assert.False(secondTask.IsCompleted);
        Assert.Contains(progress.History, m => m.Contains("Queued behind"));
        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        await second.DisposeAsync();
        Assert.Equal(0, GpuQueue.CountBusy(1));
    }

    [Fact]
    public async Task CancelWhileQueued()
    {
        using var home = new TempHome();
        var progress = new ProgressReporter(null, null);
        var held = await GpuQueue.AcquireAsync(1, progress, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GpuQueue.AcquireAsync(1, progress, cts.Token));
        await held.DisposeAsync();
        var again = await GpuQueue.AcquireAsync(1, progress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await again.DisposeAsync();
    }
}

public class ChildProcessTests
{
    [Fact]
    public async Task ChildNeverInheritsStdout_AndOutputIsCaptured()
    {
        var res = await ChildProcess.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "echo hello"],
            new ChildProcess.Options { TailLines = 10 }, CancellationToken.None);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("hello", res.StdOut);
        Assert.Contains(res.Tail, l => l.Contains("hello"));
    }

    [Fact]
    public async Task CancellationKillsProcess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChildProcess.RunAsync(
            Path.Combine(Environment.SystemDirectory, "ping.exe"), ["-n", "30", "127.0.0.1"], new ChildProcess.Options { Timeout = TimeSpan.FromMinutes(1) }, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        _ = JobObject.Shared;
    }
}
