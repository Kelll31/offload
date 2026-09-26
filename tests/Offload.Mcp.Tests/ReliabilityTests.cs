using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Usage;
using Offload.Llama;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Классификация ошибок llama-server и английские тексты ошибок для IDE.</summary>
public class LlamaErrorMappingTests
{
    internal static bool HasCyrillic(string s) => s.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    [Fact]
    public void MapError_UsesKind_NotMessageText()
    {
        // Переполнение — по признаку Kind, даже если текст ничего не говорит о контексте.
        var overflow = LocalModel.MapError(new LlamaApiException("что-то пошло не так", 400)
        {
            Kind = LlamaErrorKind.ContextExceeded,
            PromptTokens = 9000,
            ContextSize = 8192,
        });
        var ce = Assert.IsType<ContextExceededException>(overflow);
        Assert.Contains("9000 tok requested, context 8192 tok", ce.Message);

        // Слово «context»/«контекст» в тексте прочих ошибок — не переполнение.
        var rejected = LocalModel.MapError(new LlamaApiException("llama-server отклонил запрос: контекст", 400)
        {
            Kind = LlamaErrorKind.Rejected,
            Detail = "Invalid context shift parameter",
        });
        Assert.IsType<ToolException>(rejected);
        Assert.Contains("rejected the request (HTTP 400). Details: Invalid context shift parameter", rejected.Message);

        var failed = LocalModel.MapError(new LlamaApiException("Внутренняя ошибка llama-server: context", 500) { Detail = "context slot failed" });
        Assert.IsType<ToolException>(failed);
        Assert.Contains("failed (HTTP 500). Details: context slot failed", failed.Message);
    }

    [Fact]
    public void MapError_NeverLeaksLocalizedText()
    {
        Exception[] mapped =
        [
            LocalModel.MapError(new LlamaApiException("Соединение с llama-server прервалось во время генерации.") { Kind = LlamaErrorKind.ConnectionLost }),
            LocalModel.MapError(new LlamaApiException("Сервер не запущен: нет соединения.") { Kind = LlamaErrorKind.NotRunning, Detail = "nothing is listening" }),
            LocalModel.MapError(new LlamaApiException("Внутренняя ошибка llama-server: сбой", 500)),
            LocalModel.MapError(new LlamaApiException("llama-server отклонил запрос", 422)),
            // Подробность с кириллицей (например, текст ОС) отбрасывается целиком.
            LocalModel.MapError(new LlamaApiException("Нет связи") { Detail = "Подключение не установлено" }),
        ];
        foreach (var e in mapped) Assert.False(HasCyrillic(e.Message), e.Message);
        Assert.Contains("not running", mapped[1].Message);
        Assert.Contains("Lost connection", mapped[0].Message);
        Assert.DoesNotContain("Details:", mapped[4].Message);
    }

    [Fact]
    public void EnglishDetail_DropsLocalizedAndCollapsesWhitespace()
    {
        Assert.Null(LocalModel.EnglishDetail("Ошибка сети"));
        Assert.Null(LocalModel.EnglishDetail("  "));
        Assert.Equal("a b c", LocalModel.EnglishDetail("a\n b\t\tc"));
        Assert.EndsWith("…", LocalModel.EnglishDetail(new string('x', 400)));
    }
}

/// <summary>Сквозные ошибки модели через LocalModel/инструменты с поддельным сервером.</summary>
[Collection("AppPaths")]
public class LocalModelFailureTests
{
    private static LocalModel Model(string baseUrl, AppConfig cfg) =>
        new(new LlamaClient(baseUrl, TestEnv.ApiKey), cfg, new ProgressReporter(null, null), new ToolStats());

    [Fact]
    public async Task ConnectionRefused_EnglishToolException()
    {
        using var env = new TestEnv();
        var cfg = ConfigStore.Reload();
        var model = Model($"http://127.0.0.1:{TestEnv.FreePort()}", cfg);
        var ex = await Assert.ThrowsAsync<ToolException>(() => model.ChatAsync("s", "u", 16, "t", TestContext.Current.CancellationToken));
        Assert.Contains("not running", ex.Message);
        Assert.False(LlamaErrorMappingTests.HasCyrillic(ex.Message), ex.Message);
    }

    [Fact]
    public async Task Http500_EnglishToolException()
    {
        using var llama = new FakeLlamaServer { ErrorResponder = _ => (500, "{\"error\":{\"code\":500,\"message\":\"out of memory\",\"type\":\"server_error\"}}") };
        using var env = new TestEnv(llama.Port);
        var model = Model($"http://127.0.0.1:{llama.Port}", ConfigStore.Reload());
        var ex = await Assert.ThrowsAsync<ToolException>(() => model.ChatAsync("s", "u", 16, "t", TestContext.Current.CancellationToken));
        Assert.Contains("HTTP 500", ex.Message);
        Assert.Contains("out of memory", ex.Message);
        Assert.False(LlamaErrorMappingTests.HasCyrillic(ex.Message), ex.Message);
    }

    [Fact]
    public async Task CallTimeout_EnglishToolException()
    {
        using var llama = new FakeLlamaServer { Delay = TimeSpan.FromSeconds(5) };
        using var env = new TestEnv(llama.Port);
        var model = Model($"http://127.0.0.1:{llama.Port}", ConfigStore.Reload());
        var saved = LocalModel.CallTimeout;
        LocalModel.CallTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var ex = await Assert.ThrowsAsync<ToolException>(() => model.ChatAsync("s", "u", 16, "t", TestContext.Current.CancellationToken));
            Assert.Contains("did not finish", ex.Message);
            Assert.False(LlamaErrorMappingTests.HasCyrillic(ex.Message), ex.Message);
        }
        finally
        {
            LocalModel.CallTimeout = saved;
        }
    }

    [Fact]
    public async Task AskFiles_Http400MentioningContext_NoRetryAsOverflow()
    {
        using var llama = new FakeLlamaServer
        {
            ErrorResponder = _ => (400, "{\"error\":{\"code\":400,\"message\":\"Invalid context shift: n_keep is too large\",\"type\":\"invalid_request_error\"}}"),
        };
        using var env = new TestEnv(llama.Port);
        env.WriteFile("src/a.cs", "class A { }\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.AskFiles, new() { ["paths"] = new[] { "src" }, ["question"] = "What is A?" });
        Assert.True(isError, text);
        Assert.Contains("rejected the request (HTTP 400)", text);
        Assert.DoesNotContain("context window", text);
        Assert.Single(llama.Requests); // не «переполнение» — без повтора половинными частями
        Assert.False(LlamaErrorMappingTests.HasCyrillic(text), text);
    }

    [Fact]
    public async Task AskFiles_TypedOverflow_RetriesInSmallerParts()
    {
        using var llama = new FakeLlamaServer
        {
            ErrorResponder = n => n == 1
                ? (400, "{\"error\":{\"code\":400,\"message\":\"the request exceeds the available context size, try increasing it\",\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":9000,\"n_ctx\":8192}}")
                : null,
            Responder = _ => "A is an empty class.",
        };
        using var env = new TestEnv(llama.Port);
        env.WriteFile("src/a.cs", "class A { }\n");
        await using var h = await McpHarness.StartAsync(env.Workspace);
        var (isError, text) = await h.CallAsync(McpToolNames.AskFiles, new() { ["paths"] = new[] { "src" }, ["question"] = "What is A?" });
        Assert.False(isError, text);
        Assert.StartsWith("A is an empty class.", text);
        Assert.Equal(2, llama.Requests.Count);
    }
}

/// <summary>Контекст на запрос при общем KV-кэше (-kvu): статус и бюджеты считают долю слота, а не весь буфер.</summary>
[Collection("AppPaths")]
public class SharedKvContextTests
{
    [Fact]
    public void ContextLine_SharedAndOwnSlots()
    {
        Assert.Equal(" · context 65536 tok per request (shared KV 196608 across 3 slots)",
            StatusTool.ContextLine(new ServerProps(65536, 3, null, null, null) { SharedContext = 196608 }));
        Assert.Equal(" · context 8192 tok per request", StatusTool.ContextLine(new ServerProps(8192, 1, null, null, null)));
        Assert.Equal(" · context 4096 tok per request × 2 slots", StatusTool.ContextLine(new ServerProps(4096, 2, null, null, null)));
        Assert.Equal("", StatusTool.ContextLine(new ServerProps(0, 1, null, null, null)));
    }

    [Fact]
    public async Task Status_And_Budget_UsePerSlotContext()
    {
        using var llama = new FakeLlamaServer { ContextSize = 196608, TotalSlots = 3 };
        using var env = new TestEnv(llama.Port, configure: c =>
        {
            c.Server.Parallel = 3;
            c.Server.ContextSize = 65536;
        });
        var ctx = env.Context(ct: TestContext.Current.CancellationToken);

        var status = await StatusTool.RunAsync(ctx);
        Assert.Contains("context 65536 tok per request (shared KV 196608 across 3 slots)", status);
        Assert.DoesNotContain("196608 tok per request", status);

        var model = new LocalModel(LlamaClient.FromConfig(ctx.Cfg), ctx.Cfg, ctx.Progress, ctx.Stats);
        await model.InitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(65536, model.ContextPerSlot);
        // Бюджет материала (ChunkPlanner) — от доли слота.
        Assert.True(model.MaterialBudget(1000) < 65536, model.MaterialBudget(1000).ToString());
    }
}

/// <summary>Срок задачи: проверке и раундам достаётся остаток, а не весь timeout заново.</summary>
public class TaskDeadlineTests
{
    [Fact]
    public void ForStep_ReturnsRemainingWithMinimum()
    {
        var elapsed = TimeSpan.Zero;
        var dl = new TaskDeadline(TimeSpan.FromMinutes(10), () => elapsed);
        Assert.Equal(TimeSpan.FromMinutes(10), dl.ForStep(TaskDeadline.MinVerifyTime));

        elapsed = TimeSpan.FromMinutes(7);
        Assert.Equal(TimeSpan.FromMinutes(3), dl.ForStep(TaskDeadline.MinVerifyTime));
        Assert.False(dl.Exhausted);

        elapsed = TimeSpan.FromMinutes(9.5);
        Assert.Equal(TaskDeadline.MinVerifyTime, dl.ForStep(TaskDeadline.MinVerifyTime)); // не меньше минуты

        elapsed = TimeSpan.FromMinutes(10);
        Assert.True(dl.Exhausted);
        Assert.Null(dl.ForStep(TaskDeadline.MinVerifyTime));
    }
}

/// <summary>Запись usage.jsonl без ожидания трея внутри вызова инструмента.</summary>
[Collection("AppPaths")]
public class UsageRecorderTests
{
    private static UsageRecord Rec(string tool) => new(DateTime.UtcNow, tool, "test", 10, 5, 100, true);

    [Fact]
    public async Task TrayRunning_WritesInBackground_AndFlushWaits()
    {
        var savedSink = UsageRecorder.Sink;
        var savedTray = UsageRecorder.IsTrayRunning;
        var gate = new ManualResetEventSlim(false);
        var written = new List<string>();
        try
        {
            UsageRecorder.IsTrayRunning = () => true;
            UsageRecorder.Sink = r =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                lock (written) written.Add(r.Tool);
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            UsageRecorder.Enqueue(Rec("slow"));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), "Enqueue не должен ждать трей");
            Assert.False(await UsageRecorder.FlushAsync(TimeSpan.FromMilliseconds(100)));
            gate.Set();
            Assert.True(await UsageRecorder.FlushAsync(TimeSpan.FromSeconds(10)));
            lock (written) Assert.Equal(["slow"], written);
            Assert.Equal(0, UsageRecorder.PendingCount);
        }
        finally
        {
            gate.Set();
            await UsageRecorder.FlushAsync(TimeSpan.FromSeconds(10));
            UsageRecorder.Sink = savedSink;
            UsageRecorder.IsTrayRunning = savedTray;
        }
    }

    [Fact]
    public async Task SinkFailure_FallsBackToFile_NoTray_WritesImmediately()
    {
        using var home = new TempHome();
        var savedSink = UsageRecorder.Sink;
        var savedTray = UsageRecorder.IsTrayRunning;
        try
        {
            UsageRecorder.IsTrayRunning = () => true;
            UsageRecorder.Sink = _ => throw new IOException("pipe broken");
            UsageRecorder.Enqueue(Rec("fallback"));
            Assert.True(await UsageRecorder.FlushAsync(TimeSpan.FromSeconds(10)));

            // Без трея — сразу в файл, без фоновых задач.
            UsageRecorder.IsTrayRunning = () => false;
            UsageRecorder.Sink = savedSink;
            UsageRecorder.Enqueue(Rec("direct"));
            Assert.Equal(0, UsageRecorder.PendingCount);

            var tools = UsageLog.ReadAll().Select(r => r.Tool).ToList();
            Assert.Equal(["fallback", "direct"], tools);
        }
        finally
        {
            UsageRecorder.Sink = savedSink;
            UsageRecorder.IsTrayRunning = savedTray;
        }
    }
}
