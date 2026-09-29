using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Core.Security;
using Offload.Core.Util;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>
/// Кэш результатов модели: попадание на тех же входах, промах после правки файла / смены модели / fresh, похожий вопрос по
/// эмбеддингам, срок жизни и вытеснение по размеру, две базы на один файл, не кэшируются ошибки/отмена/обрезанные ответы,
/// в кэш попадает только замаскированный текст.
/// </summary>
[Collection("AppPaths")]
public sealed class WorkCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string GitHubToken = "gh" + "p_" + "R8mK2qW9zL4vN7bX1cY6hJ3dF5gT0pAsQeUo";

    private const string CalcV1 = "public class Calc\n{\n    public int Add(int a, int b) => a + b;\n}\n";

    private static (FakeLlamaServer Llama, TestEnv Env) Setup(Action<AppConfig>? configure = null)
    {
        var llama = new FakeLlamaServer { Responder = _ => "Add is defined at src/Calc.cs:3." };
        var env = new TestEnv(llama.Port, configure: configure);
        env.WriteFile("src/Calc.cs", CalcV1);
        return (llama, env);
    }

    private static Task<string> AskAsync(TestEnv env, string question, SessionState? state = null, bool fresh = false, CancellationToken? ct = null) =>
        AskFilesTool.RunAsync(env.Context(state, ct ?? Ct), ["src"], question, "brief", 0, fresh);

    private static void Reconfigure(Action<AppConfig> change)
    {
        var cfg = ConfigStore.Reload();
        change(cfg);
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(cfg, Json.Options));
    }

    private static int Entries(TestEnv env) => WorkCacheStore.TryStats(env.Workspace)?.Entries ?? 0;

    [Fact]
    public async Task AskFiles_SameQuestionUnchangedFiles_AnsweredFromCache()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var state = new SessionState();

        var first = await AskAsync(env, "Where is Add defined?", state);
        var ctx = env.Context(state, Ct);
        var second = await AskFilesTool.RunAsync(ctx, ["src"], "  where is ADD defined  ", "brief", 0);

        Assert.Equal(first, second);
        Assert.Single(llama.Requests);
        Assert.Contains("cached · inputs unchanged since", ctx.FooterNote);
        Assert.Equal(1, ctx.Stats.FilesRead);
        Assert.True(ctx.Stats.TokensRead > 0, "попадание учитывает материал, который облачной модели не пришлось читать");
        Assert.Equal(0, ctx.Stats.ModelCalls);
        Assert.Equal(1, state.WorkCache.Hits);
        Assert.Contains("this session 1/2 hits", StatusTool.CacheLine(env.Context(state, Ct)));
    }

    [Fact]
    public async Task AskFiles_ThroughToolRunner_FooterMarksCachedAndCountsSavings()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        env.WriteFile("src/Big.cs", string.Concat(Enumerable.Range(1, 300).Select(i => $"// filler line {i} describing the calculator module\n")));
        var state = new SessionState { PinnedRoots = [env.Workspace] };
        Task<CallToolResult> Call() => ToolRunner.RunAsync(McpToolNames.AskFiles, state, null,
            SecretRedactor.RedactingOutput(c => AskFilesTool.RunAsync(c, ["src"], "Where is Add defined?", "brief", 0)), Ct);

        await Call();
        var cached = await Call();

        var text = string.Join("\n", cached.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.NotEqual(true, cached.IsError);
        Assert.Contains("offload · cached · inputs unchanged since", text);
        Assert.Contains("cloud tokens avoided", text);
        Assert.Single(llama.Requests);
    }

    [Fact]
    public async Task AskFiles_FileEdited_Miss()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;

        await AskAsync(env, "Where is Add defined?");
        env.WriteFile("src/Calc.cs", CalcV1.Replace("a + b", "b + a"));
        var ctx = env.Context(ct: Ct);
        await AskFilesTool.RunAsync(ctx, ["src"], "Where is Add defined?", "brief", 0);

        Assert.Equal(2, llama.Requests.Count);
        Assert.Null(ctx.FooterNote);
    }

    [Fact]
    public async Task AskFiles_DifferentModelOrArgs_Miss_FreshBypasses()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;

        await AskAsync(env, "Where is Add defined?");
        Reconfigure(c => c.Models.Installed[0].Sampling.Temperature = 0.1);
        await AskAsync(env, "Where is Add defined?");
        Assert.Equal(2, llama.Requests.Count);

        Reconfigure(c => c.Models.Installed[0].Quant = "Q8_0");
        await AskAsync(env, "Where is Add defined?");
        Assert.Equal(3, llama.Requests.Count);

        await AskFilesTool.RunAsync(env.Context(ct: Ct), ["src"], "Where is Add defined?", "bullets", 0);
        Assert.Equal(4, llama.Requests.Count);

        await AskAsync(env, "Where is Add defined?", fresh: true);
        Assert.Equal(5, llama.Requests.Count);

        await AskAsync(env, "Where is Add defined?");
        Assert.Equal(5, llama.Requests.Count);
    }

    [Fact]
    public async Task AskFiles_Disabled_NeverCaches()
    {
        var (llama, env) = Setup(c => c.Mcp.WorkCache = false);
        using var _ = llama;
        using var __ = env;

        await AskAsync(env, "Where is Add defined?");
        await AskAsync(env, "Where is Add defined?");

        Assert.Equal(2, llama.Requests.Count);
        Assert.False(File.Exists(WorkCacheStore.DbPathFor(env.Workspace)), "выключенный кэш не создаёт базу");
    }

    [Fact]
    public async Task AskFiles_SimilarQuestionWithEmbeddings_Hit_ButDifferentIdentifiersOrNegation_Miss()
    {
        using var llama = new FakeLlamaServer { Responder = _ => "Add is defined at src/Calc.cs:3." };
        // Все вопросы про «add» — один и тот же вектор: различать их должна защита по идентификаторам и отрицаниям.
        using var embed = new FakeVectorServer("offload-embed") { Embed = t => t.Contains("add", StringComparison.OrdinalIgnoreCase) ? [1f, 0f] : [0f, 1f] };
        using var env = new TestEnv(llama.Port, configure: c =>
        {
            c.Models.Installed.Add(new InstalledModel { Id = "qwen3-embed-test", DisplayName = "Embed", Kind = ModelKind.Embed });
            c.Models.Roles.Embed = "qwen3-embed-test";
        });
        env.WriteFile("src/Calc.cs", CalcV1);
        var state = new SessionState
        {
            IpcOverride = (req, _) => Task.FromResult<IpcResponse?>(req.Args?.GetValueOrDefault(IpcRoleArgs.Role) == "embed"
                ? new IpcResponse(true, null, new() { [IpcRoleArgs.Role] = "embed", [IpcRoleArgs.BaseUrl] = $"http://127.0.0.1:{embed.Port}" })
                : new IpcResponse(false, "no such role")),
        };

        await AskAsync(env, "Where is the Add method defined?", state);
        var ctx = env.Context(state, Ct);
        await AskFilesTool.RunAsync(ctx, ["src"], "In which file is the Add method implemented?", "brief", 0);
        Assert.Single(llama.Requests);
        Assert.Contains("similar question", ctx.FooterNote);
        Assert.Equal(1, state.WorkCache.SimilarHits);

        await AskAsync(env, "Where is the Add method not used?", state);
        Assert.Equal(2, llama.Requests.Count);
        await AskAsync(env, "Where is Calc.Add_2 defined?", state);
        Assert.Equal(3, llama.Requests.Count);
    }

    [Fact]
    public async Task AskFiles_ModelError_TruncatedOrCancelled_NotCached()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;

        llama.ErrorResponder = n => n == 1 ? (500, "{\"error\":{\"code\":500,\"message\":\"boom\",\"type\":\"server_error\"}}") : null;
        await Assert.ThrowsAsync<ToolException>(() => AskAsync(env, "Where is Add defined?"));
        Assert.Equal(0, Entries(env));

        llama.ErrorResponder = null;
        llama.FinishReason = "length";
        var cut = await AskAsync(env, "Where is Add defined?");
        Assert.Contains("[answer cut at max_answer_tokens", cut);
        Assert.Equal(0, Entries(env));

        llama.FinishReason = "stop";
        llama.Delay = TimeSpan.FromSeconds(3);
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskAsync(env, "Where is Add defined?", ct: cts.Token));
        }
        Assert.Equal(0, Entries(env));

        llama.Delay = TimeSpan.Zero;
        var before = llama.Requests.Count;
        await AskAsync(env, "Where is Add defined?");
        await AskAsync(env, "Where is Add defined?");
        Assert.Equal(before + 1, llama.Requests.Count);
        Assert.Equal(1, Entries(env));
    }

    [Fact]
    public async Task AskFiles_CachedTextIsRedacted()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        llama.Responder = _ => $"The token {GitHubToken} is used at src/Calc.cs:3.";

        var first = await AskAsync(env, "Which token is used?");
        var cached = await AskAsync(env, "Which token is used?");

        Assert.Contains(GitHubToken, first); // без обёртки RedactingOutput — как ответила модель
        Assert.DoesNotContain(GitHubToken, cached);
        Assert.Contains("«redacted:github-token»", cached);
        Assert.Single(llama.Requests);
    }

    [Fact]
    public async Task AskFiles_Expired_Miss()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        await AskAsync(env, "Where is Add defined?");
        try
        {
            WorkCacheStore.Clock = () => DateTimeOffset.UtcNow.AddDays(15);
            await AskAsync(env, "Where is Add defined?");
        }
        finally
        {
            WorkCacheStore.Clock = () => DateTimeOffset.UtcNow;
        }
        Assert.Equal(2, llama.Requests.Count);
    }

    [Fact]
    public async Task SummarizeLog_SameContentDifferentFile_Hit()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        llama.Responder = _ => "Failing: build\nRoot error: Foo.cs:10 CS1002 (L2)\nLikely cause: missing semicolon\nNext step: add it";
        const string log = "Build started\r\nFoo.cs(10,3): error CS1002: ; expected\r\nBuild FAILED\r\n";
        env.WriteFile(".offload/runs/a.log", log);
        env.WriteFile(".offload/runs/b.log", log);

        var a = await SummarizeLogTool.RunAsync(env.Context(ct: Ct), ".offload/runs/a.log", null, 0, 0);
        var b = await SummarizeLogTool.RunAsync(env.Context(ct: Ct), ".offload/runs/b.log", null, 0, 0);

        Assert.Single(llama.Requests);
        Assert.Contains("log: .offload/runs/b.log", b);
        Assert.Equal(a.Split("\n\nlog:")[0], b.Split("\n\nlog:")[0]);
    }

    [Fact]
    public async Task ReviewDiff_UnchangedDiff_Hit_ChangedDiff_Miss()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        llama.Responder = _ => "[low] src/Calc.cs:3 - operand order changed - revert";
        async Task Git(params string[] args) => Assert.True((await Offload.Mcp.Infrastructure.Git.RunAsync(env.Workspace, args, Ct)).Success);
        await Git("init", "-q");
        await Git("config", "user.name", "Test");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "commit.gpgsign", "false");
        await Git("add", "-A");
        await Git("commit", "-q", "-m", "init");
        env.WriteFile("src/Calc.cs", CalcV1.Replace("a + b", "b + a"));

        var first = await ReviewDiffTool.RunAsync(env.Context(ct: Ct), null, "all", null, 0);
        var second = await ReviewDiffTool.RunAsync(env.Context(ct: Ct), null, "all", null, 0);
        Assert.Equal(first, second);
        Assert.Single(llama.Requests);

        await ReviewDiffTool.RunAsync(env.Context(ct: Ct), null, "all", null, 0, fresh: true);
        Assert.Equal(2, llama.Requests.Count);

        env.WriteFile("src/Calc.cs", CalcV1.Replace("a + b", "a - b"));
        await ReviewDiffTool.RunAsync(env.Context(ct: Ct), null, "all", null, 0);
        Assert.Equal(3, llama.Requests.Count);
    }

    /// <summary>Значения столбца всех записей кэша рабочей папки (прямо из SQLite).</summary>
    private static List<string?> Column(TestEnv env, string column)
    {
        using var conn = new SqliteConnection($"Data Source={WorkCacheStore.DbPathFor(env.Workspace)};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM entries";
        using var r = cmd.ExecuteReader();
        var list = new List<string?>();
        while (r.Read()) list.Add(r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        return list;
    }

    [Fact]
    public async Task AskFiles_RemoteMode_KeyFollowsRemoteServerAndModel_NotLocalModel()
    {
        using var a = new FakeLlamaServer { ModelsAlias = "remote-a", Responder = _ => "Add is defined at src/Calc.cs:3." };
        using var b = new FakeLlamaServer { ModelsAlias = "remote-b", Responder = _ => "Add is defined at src/Calc.cs:3." };
        using var env = new TestEnv(TestEnv.FreePort(), configure: c =>
        {
            c.Remote.Enabled = true;
            c.Remote.Url = $"http://127.0.0.1:{a.Port}";
            c.Remote.ApiKeyProtected = Dpapi.Protect(TestEnv.ApiKey);
        });
        env.WriteFile("src/Calc.cs", CalcV1);

        await AskAsync(env, "Where is Add defined?");
        await AskAsync(env, "Where is Add defined?");
        Assert.Single(a.Requests); // тот же удалённый сервер и модель — попадание

        // Своя установленная модель к ответу удалённой отношения не имеет: её смена ключ не меняет.
        Reconfigure(c => c.Models.Installed[0].Quant = "Q8_0");
        await AskAsync(env, "Where is Add defined?");
        Assert.Single(a.Requests);

        Reconfigure(c => c.Remote.ModelId = "remote-a");
        await AskAsync(env, "Where is Add defined?");
        Assert.Equal(2, a.Requests.Count); // другая модель удалённого сервера — промах

        Reconfigure(c => c.Remote.Url = $"http://127.0.0.1:{b.Port}/v1");
        await AskAsync(env, "Where is Add defined?");
        Assert.Single(b.Requests); // другой удалённый сервер — промах
        Assert.Equal(2, a.Requests.Count);
    }

    [Fact]
    public async Task AskFiles_StoredQuestionIsRedacted()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;

        await AskAsync(env, $"Where is the token {GitHubToken} used?");
        await AskAsync(env, $"Where is the token {GitHubToken} used?");

        Assert.Single(llama.Requests);
        var question = Assert.Single(Column(env, "question"));
        Assert.DoesNotContain(GitHubToken, question);
        Assert.Contains("«redacted:github-token»", question);
    }

    [Fact]
    public async Task ReviewDiff_NestedInAnotherTool_HitIsModelStep_StoresOnlyOwnWork()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        llama.Responder = _ => "[low] src/Calc.cs:3 - operand order changed - revert";
        async Task Git(params string[] args) => Assert.True((await Offload.Mcp.Infrastructure.Git.RunAsync(env.Workspace, args, Ct)).Success);
        await Git("init", "-q");
        await Git("config", "user.name", "Test");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "commit.gpgsign", "false");
        await Git("add", "-A");
        await Git("commit", "-q", "-m", "init");
        env.WriteFile("src/Calc.cs", CalcV1.Replace("a + b", "b + a"));

        // Промах внутри pr_ready после другого обращения к модели: в запись идут только токены самого ревью.
        var outer = env.ContextFor(McpToolNames.PrReady, ct: Ct);
        var model = await outer.GetModelAsync();
        await model.ChatAsync("sys", "some earlier step of the outer tool", 64, "earlier", Ct);
        var before = outer.Stats.CompletionTokens;
        await ReviewDiffTool.RunAsync(outer, null, "all", null, 0);
        Assert.Equal(2, llama.Requests.Count);
        Assert.Equal((outer.Stats.CompletionTokens - before).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(Column(env, "gen_tokens")));
        Assert.Null(outer.FooterNote);

        // Попадание внутри pr_ready: пометка «шаг модели из кэша», а не «весь ответ из кэша».
        var nested = env.ContextFor(McpToolNames.PrReady, ct: Ct);
        await ReviewDiffTool.RunAsync(nested, null, "all", null, 0);
        Assert.Equal(2, llama.Requests.Count);
        Assert.StartsWith("cached model step · inputs unchanged since", nested.FooterNote);

        // Тот же результат вызовом самого local_review_diff — обычная пометка кэша.
        var direct = env.ContextFor(McpToolNames.ReviewDiff, ct: Ct);
        await ReviewDiffTool.RunAsync(direct, null, "all", null, 0);
        Assert.Equal(2, llama.Requests.Count);
        Assert.StartsWith("cached · inputs unchanged since", direct.FooterNote);
    }

    [Fact]
    public void Store_TtlAndLruEviction()
    {
        using var home = new TempHome();
        WorkCacheStore.ResetPrune();
        var root = Path.Combine(home.Path, "ws");
        var t = DateTimeOffset.UtcNow;
        var saved = WorkCacheStore.Clock;
        WorkCacheStore.Clock = () => t;
        try
        {
            using var store = WorkCacheStore.Open(root, 20_000, TimeSpan.FromDays(14));
            var body = new string('x', 1500); // ≈3.2 КБ на запись
            WorkCacheEntry E(string key) => new(key, "fp", "op", null, body, WorkCacheStore.Clock(), 1, 10, 5, null, null);
            foreach (var k in new[] { "A", "B", "C", "D", "E" })
            {
                store.Put(E(k));
                t = t.AddSeconds(1);
            }
            store.Touch("A");
            t = t.AddSeconds(1);
            store.Put(E("F"));
            t = t.AddSeconds(1);
            store.Put(E("G")); // сверх 20 КБ — вытесняются давно не использованные B и C

            Assert.NotNull(store.Get("A"));
            Assert.Null(store.Get("B"));
            Assert.Null(store.Get("C"));
            Assert.NotNull(store.Get("D"));
            Assert.NotNull(store.Get("G"));
            Assert.Equal(5, store.Count());

            // Слишком большая запись не вытесняет весь кэш.
            store.Put(new WorkCacheEntry("H", "fp", "op", null, new string('y', 20_000), t, 0, 0, 0, null, null));
            Assert.Null(store.Get("H"));

            t = t.AddDays(15);
            Assert.Null(store.Get("A"));
            Assert.Empty(store.Candidates("fp", "none", 10));
        }
        finally
        {
            WorkCacheStore.Clock = saved;
        }
    }

    [Fact]
    public async Task Store_TwoConnectionsSameDb_ConcurrentWritesAndReads()
    {
        using var home = new TempHome();
        var root = Path.Combine(home.Path, "ws");
        using (WorkCacheStore.Open(root, 50L << 20, TimeSpan.FromDays(14))) { }

        async Task Worker(string prefix, string other)
        {
            await Task.Yield();
            using var store = WorkCacheStore.Open(root, 50L << 20, TimeSpan.FromDays(14));
            for (var i = 0; i < 60; i++)
            {
                store.Put(new WorkCacheEntry($"{prefix}{i}", "fp", "op", "q" + i, "result " + i, DateTimeOffset.UtcNow, 0, 0, 0, null, null));
                store.Get($"{other}{i / 2}");
                store.Touch($"{other}{i / 3}");
            }
        }

        await Task.WhenAll(Task.Run(() => Worker("a", "b"), Ct), Task.Run(() => Worker("b", "a"), Ct));

        using var check = WorkCacheStore.Open(root, 50L << 20, TimeSpan.FromDays(14));
        Assert.Equal(120, check.Count());
        Assert.Equal("result 59", check.Get("a59")!.Result);
        Assert.Equal("result 7", check.Get("b7")!.Result);
    }

    [Fact]
    public void Compatible_RequiresSameIdentifiersAndNegations()
    {
        Assert.True(WorkCache.Compatible("where is the add method defined", "in which file is the add method implemented"));
        Assert.False(WorkCache.Compatible("where is OrderService.Submit called", "where is OrderService.Cancel called"));
        Assert.False(WorkCache.Compatible("where is `retry_count` read", "where is `retry_limit` read"));
        Assert.False(WorkCache.Compatible("which tests cover parsing", "which tests do not cover parsing"));
        Assert.False(WorkCache.Compatible("what changed in v2", "what changed in v3"));
        Assert.Equal("where is add defined", WorkCache.NormalizeQuestion("  Where is   ADD defined?? "));
    }
}
