using System.Net;
using System.Text;
using System.Text.Json;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Ipc;
using Offload.Mcp.Index;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>
/// Гибридный поиск (ROADMAP §6.3): векторы в постоянном индексе, слияние BM25 и косинуса (RRF), реранкер роли rerank,
/// recall памяти по смыслу. Серверы embed/rerank — поддельные, векторы — фиксированные (детерминированно, без сети и GPU).
/// </summary>
[Collection("AppPaths")]
public sealed class HybridSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string EmbedId = "qwen3-embed-test";
    private const string RerankId = "reranker-test";

    /// <summary>Фиксированные «смыслы»: деньги, разбор текста, прочее.</summary>
    internal static float[] Meaning(string text)
    {
        var t = text.ToLowerInvariant();
        var money = t.Contains("invoice") || t.Contains("money") || t.Contains("деньг") ? 1f : 0f;
        var parse = t.Contains("parse") || t.Contains("tokeniz") ? 1f : 0f;
        return [money, parse, 0.1f];
    }

    private static TestEnv Env(FakeLlamaServer main, bool embed = true, bool rerank = false, Action<AppConfig>? more = null) =>
        new(main.Port, configure: c =>
        {
            if (embed)
            {
                c.Models.Installed.Add(new InstalledModel { Id = EmbedId, DisplayName = "Embed", Kind = ModelKind.Embed });
                c.Models.Roles.Embed = EmbedId;
            }
            if (rerank)
            {
                c.Models.Installed.Add(new InstalledModel { Id = RerankId, DisplayName = "Rerank", Kind = ModelKind.Rerank });
                c.Models.Roles.Rerank = RerankId;
            }
            more?.Invoke(c);
        });

    /// <summary>Трей «запускает» серверы ролей: адрес — поддельного сервера этой роли.</summary>
    private static SessionState Tray(FakeVectorServer? embed, FakeVectorServer? rerank = null) => new()
    {
        IpcOverride = (req, _) =>
        {
            var role = req.Args?.GetValueOrDefault(IpcRoleArgs.Role);
            var server = role == "embed" ? embed : role == "rerank" ? rerank : null;
            return Task.FromResult<IpcResponse?>(server is null
                ? new IpcResponse(false, "no such role")
                : new IpcResponse(true, null, new() { [IpcRoleArgs.Role] = role!, [IpcRoleArgs.BaseUrl] = $"http://127.0.0.1:{server.Port}" }));
        },
    };

    private static void WriteProject(TestEnv env)
    {
        env.WriteFile("src/Billing/InvoiceCalculator.cs", """
            namespace Shop.Billing;

            public sealed class InvoiceCalculator
            {
                public decimal Total(decimal net, decimal vat) => net + net * vat;

                public decimal Discount(decimal total) => total * 0.9m;
            }
            """);
        env.WriteFile("src/Text/Tokenizer.cs", """
            namespace Shop.Text;

            public sealed class Tokenizer
            {
                public string[] Split(string text) => text.Split(' ');
            }
            """);
        env.WriteFile("src/Orders/OrderService.cs", """
            namespace Shop.Orders;

            public sealed class OrderService
            {
                public void Submit(string customer) { }
            }
            """);
    }

    // ───────────────────────── чистые функции ─────────────────────────

    [Fact]
    public void Chunker_SymbolsGapsAndWindows()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"    var line{i} = compute({i}); // filler text").ToArray();
        var symbols = new List<CodeSymbol>
        {
            new("Foo", "class", 1, 200, null, "class Foo"),
            new("A", "method", 10, 20, "Foo", "void A()"),
            new("Local", "function", 12, 14, "Foo.A", "void Local()"),
            new("B", "method", 21, 30, "Foo", "void B()"),
            new("Big", "method", 40, 170, "Foo", "void Big()"),
        };
        var chunks = EmbeddingsChunker.Chunk(symbols, lines);

        // Строки 1–39: промежутки + A + B склеены (≤ 60 строк), вложенная Local отдельно не режется.
        Assert.Equal(new TextChunk(1, 39, "Foo.A, Foo.B"), chunks[0]);
        // Длинный метод — окнами по 60 строк.
        Assert.Equal((40, 99, "Foo.Big"), (chunks[1].StartLine, chunks[1].EndLine, chunks[1].Label));
        Assert.Equal((100, 159, "Foo.Big (cont.)"), (chunks[2].StartLine, chunks[2].EndLine, chunks[2].Label));
        Assert.Equal((160, 170), (chunks[3].StartLine, chunks[3].EndLine));
        Assert.Equal(new TextChunk(171, 200, "lines 171-200"), chunks[4]);
        Assert.Equal(5, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.EndLine - c.StartLine < EmbeddingsChunker.Window));

        // Без символов — окна по 60 строк; пустые строки не эмбеддятся.
        var plain = EmbeddingsChunker.Chunk([], lines.Take(130).ToArray());
        Assert.Equal([(1, 60), (61, 120), (121, 130)], plain.Select(c => (c.StartLine, c.EndLine)));
        Assert.Empty(EmbeddingsChunker.Chunk([], ["", "   ", "}"]));
    }

    [Fact]
    public void Fuse_ReciprocalRankFusion_IsDeterministic()
    {
        var fused = VectorMath.Fuse(StringComparer.Ordinal, ["a", "b", "c"], ["c", "d", "a"]);
        // a: 1/61 + 1/63, c: 1/63 + 1/61 — равенство решает первое появление; затем b (1/62) и d (1/62).
        Assert.Equal(["a", "c", "b", "d"], fused.Select(x => x.Item));
        Assert.Equal(1.0 / 61 + 1.0 / 63, fused[0].Score, 12);
    }

    [Fact]
    public void Vectors_NormalizeAndHalfRoundTrip()
    {
        var v = VectorMath.Normalize([3f, 4f]);
        Assert.Equal(0.6f, v[0], 5);
        Assert.Equal(0.8f, v[1], 5);
        Assert.Equal([0f, 0f], VectorMath.Normalize([0f, 0f]));
        var back = VectorMath.FromHalfBytes(VectorMath.ToHalfBytes(v));
        Assert.Equal(v[0], back[0], 3);
        Assert.Equal(1f, VectorMath.DotHalf(v, VectorMath.ToHalfBytes(v)), 3);
    }

    [Fact]
    public void QueryText_UsesModelInstructions()
    {
        Assert.StartsWith("Instruct: ", Embedder.QueryText("qwen3-embed-0.6b-q8", "где парсер"));
        Assert.EndsWith("\nQuery: где парсер", Embedder.QueryText("qwen3-embed-0.6b-q8", "где парсер"));
        Assert.Equal("search_query: x", Embedder.QueryText("nomic-embed-text", "x"));
        Assert.Equal("x", Embedder.QueryText("other", "x"));
        Assert.Equal("search_document: y", Embedder.DocumentText("nomic-embed-text", "y"));
        Assert.Equal("y", Embedder.DocumentText("qwen3-embed-0.6b-q8", "y"));
    }

    [Fact]
    public void Store_SaveSearchAndDimensionChange()
    {
        using var home = new TempHome();
        EmbeddingsStore.ResetPrune();
        var root = Path.Combine(home.Path, "proj");
        using (var store = EmbeddingsStore.Open(root, "m1"))
        {
            Assert.Equal(0, store.Dimension);
            store.Save([
                ("k1", [new ChunkVector(0, 1, 10, "A", VectorMath.Normalize([1f, 0f, 0f])), new ChunkVector(1, 11, 20, "B", VectorMath.Normalize([0f, 1f, 0f]))]),
                ("k2", [new ChunkVector(0, 1, 5, "C", VectorMath.Normalize([1f, 1f, 0f]))]),
                ("empty", []),
            ]);
            Assert.Equal(3, store.Dimension);
            Assert.Equal(["empty", "k1", "k2"], store.Embedded(["k1", "k2", "empty", "missing"]).Order());

            var hits = store.BestPerKey([0f, 1f, 0f], new HashSet<string> { "k1", "k2" });
            Assert.Equal(("B", 11), (hits["k1"].Label, hits["k1"].StartLine));
            Assert.InRange(hits["k2"].Score, 0.70f, 0.71f);
            // Вне области — не ищется.
            Assert.Single(store.BestPerKey([0f, 1f, 0f], new HashSet<string> { "k2" }));
        }
        // Другая модель — свои векторы.
        using (var other = EmbeddingsStore.Open(root, "m2"))
            Assert.Empty(other.Embedded(["k1", "k2"]));
        // Та же модель вернула другую размерность — старые векторы удаляются.
        using (var store = EmbeddingsStore.Open(root, "m1"))
        {
            store.Save([("k3", [new ChunkVector(0, 1, 1, "D", VectorMath.Normalize([1f, 0f]))])]);
            Assert.Equal(2, store.Dimension);
            Assert.Equal(["k3"], store.Embedded(["k1", "k2", "k3"]));
        }
    }

    // ───────────────────────── local_find_context ─────────────────────────

    [Fact]
    public async Task FindContext_NoEmbedRole_KeepsBm25_AndNoVectorStore()
    {
        using var main = new FakeLlamaServer { Responder = _ => "[]" };
        using var env = Env(main, embed: false);
        WriteProject(env);

        var r = await FindContextTool.RunAsync(env.Context(ct: Ct), "InvoiceCalculator Total", null, 3, 0, "rank", useModel: true);

        Assert.Contains("InvoiceCalculator.cs", r);
        Assert.Contains("ranking: bm25", r);
        Assert.DoesNotContain("vectors:", r);
        Assert.False(Directory.Exists(EmbeddingsStore.DirPath), "без роли embed база векторов не создаётся");
    }

    [Fact]
    public async Task FindContext_Hybrid_FindsFileWithoutKeywordMatch_AndEmbedsIncrementally()
    {
        using var main = new FakeLlamaServer { Responder = _ => "[]" };
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning };
        using var env = Env(main);
        WriteProject(env);
        var state = Tray(embed);

        // Ни одного слова задачи нет в InvoiceCalculator.cs — находят только векторы («money» ≈ invoice).
        const string task = "where is the money owed computed for a customer";
        var r = await FindContextTool.RunAsync(env.Context(state, Ct), task, null, 2, 0, "rank", useModel: true);

        // BM25 находит только OrderService.cs (слово customer); RRF сливает его с векторным списком, где первый — счёт.
        var files = r.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, l => l.Contains("InvoiceCalculator.cs", StringComparison.Ordinal) && l.Contains("semantic match", StringComparison.Ordinal));
        Assert.Contains(files, l => l.Contains("OrderService.cs", StringComparison.Ordinal));
        Assert.Contains("ranking: hybrid · vectors: 100% of files (3/3), +3 chunks this call", r);
        var firstCall = embed.EmbeddedTexts.Count;
        Assert.Equal(4, firstCall); // 3 файла по одному фрагменту + запрос
        Assert.True(File.Exists(EmbeddingsStore.DbPathFor(env.Workspace)));

        // Второй вызов: векторы уже есть — эмбеддится только запрос.
        var again = await FindContextTool.RunAsync(env.Context(state, Ct), task, null, 2, 0, "rank", useModel: true);
        Assert.Contains("vectors: 100% of files (3/3)", again);
        Assert.DoesNotContain("this call", again);
        Assert.Equal(firstCall + 1, embed.EmbeddedTexts.Count);

        // Изменён один файл — пересчитывается только он.
        env.WriteFile("src/Text/Tokenizer.cs", "namespace Shop.Text;\n\npublic sealed class Tokenizer\n{\n    public string[] Split(string text) => text.Split(',');\n}\n");
        File.SetLastWriteTimeUtc(env.PathOf("src/Text/Tokenizer.cs"), DateTime.UtcNow.AddMinutes(1));
        var before = embed.EmbeddedTexts.Count;
        await FindContextTool.RunAsync(env.Context(state, Ct), task, null, 2, 0, "rank", useModel: true);
        var added = embed.EmbeddedTexts.Skip(before).ToList();
        Assert.Equal(2, added.Count); // один фрагмент Tokenizer.cs + запрос
        Assert.Contains("Split(',')", added[0]);
        Assert.StartsWith("Instruct: ", added[1]);
    }

    [Fact]
    public async Task FindContext_Hybrid_BudgetLimitsChunks_AndReportsCoverage()
    {
        using var main = new FakeLlamaServer { Responder = _ => "[]" };
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning };
        using var env = Env(main);
        for (var i = 0; i < 6; i++) env.WriteFile($"src/Mod{i}.cs", $"namespace Shop;\n\npublic static class Mod{i}\n{{\n    public static int Value{i}() => {i} * 42;\n}}\n");
        var state = Tray(embed);
        var saved = EmbeddingsIndex.MaxChunksPerCall;
        EmbeddingsIndex.MaxChunksPerCall = 2;
        try
        {
            var r = await FindContextTool.RunAsync(env.Context(state, Ct), "Mod3 Value3", null, 3, 0, "rank", useModel: true);
            Assert.Contains("vectors: 33% of files (2/6), +2 chunks this call; the rest are embedded on later calls", r);
            // Кандидаты BM25 эмбеддятся первыми.
            Assert.Contains("Mod3", embed.EmbeddedTexts[0]);

            await FindContextTool.RunAsync(env.Context(state, Ct), "Mod3 Value3", null, 3, 0, "rank", useModel: true);
            var third = await FindContextTool.RunAsync(env.Context(state, Ct), "Mod3 Value3", null, 3, 0, "rank", useModel: true);
            Assert.Contains("vectors: 100% of files (6/6)", third);
        }
        finally
        {
            EmbeddingsIndex.MaxChunksPerCall = saved;
        }
    }

    [Fact]
    public async Task FindContext_Reranker_ReordersTopCandidates()
    {
        using var main = new FakeLlamaServer { Responder = _ => "[]" };
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning };
        // Реранкер «знает», что задача про токенизацию.
        using var rerank = new FakeVectorServer("offload-rerank") { Rerank = (_, doc) => doc.Contains("Tokenizer", StringComparison.Ordinal) ? 0.9 : 0.1 };
        using var env = Env(main, rerank: true);
        WriteProject(env);

        var r = await FindContextTool.RunAsync(env.Context(Tray(embed, rerank), Ct), "Shop classes: OrderService InvoiceCalculator Tokenizer", null, 3, 0, "rank", useModel: true);

        var files = r.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal)).ToList();
        Assert.Contains("Tokenizer.cs", files[0]);
        Assert.Contains("rerank 0.90", files[0]);
        Assert.Contains("ranking: hybrid+rerank", r);
        Assert.NotEmpty(rerank.RerankDocuments);
        // Реранк моделью не вызывался: запросы к основной модели — только расширение запроса.
        Assert.Single(main.Requests);
    }

    [Fact]
    public async Task FindContext_EmbedServerFails_FallsBackToBm25()
    {
        using var main = new FakeLlamaServer { Responder = _ => "[]" };
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning, Fail = true };
        using var env = Env(main);
        WriteProject(env);

        var r = await FindContextTool.RunAsync(env.Context(Tray(embed), Ct), "InvoiceCalculator Total", null, 3, 0, "rank", useModel: true);

        Assert.Contains("InvoiceCalculator.cs", r);
        Assert.Contains("ranking: bm25 · vectors: unavailable", r);
    }

    [Fact]
    public async Task FindContext_UseModelFalse_NeverTouchesEmbedServer()
    {
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning };
        using var env = new TestEnv(configure: c =>
        {
            c.Models.Installed.Add(new InstalledModel { Id = EmbedId, Kind = ModelKind.Embed });
            c.Models.Roles.Embed = EmbedId;
        });
        WriteProject(env);

        var r = await FindContextTool.RunAsync(env.Context(Tray(embed), Ct), "InvoiceCalculator Total", null, 3, 0, "pack", useModel: false);

        Assert.Contains("ranking: bm25\n", r);
        Assert.Empty(embed.EmbeddedTexts);
    }

    [Fact]
    public async Task FindContext_EmbedsOnlyRedactedText()
    {
        const string key = "AKIA" + "Q3EGRXZJ7N2LMP4K";
        using var main = new FakeLlamaServer { Responder = _ => "[]" };
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning };
        // Даже если пользователь отключил маскирование в ответах, в векторы уходит только замаскированный текст.
        using var env = Env(main, more: c => c.Mcp.RedactSecrets = false);
        env.WriteFile("src/Cloud/Uploader.cs", $"namespace Shop.Cloud;\n\npublic static class Uploader\n{{\n    private const string AccessKey = \"{key}\";\n    public static void Upload() {{ }}\n}}\n");

        await FindContextTool.RunAsync(env.Context(Tray(embed), Ct), "Uploader Upload", null, 3, 0, "rank", useModel: true);

        Assert.NotEmpty(embed.EmbeddedTexts);
        Assert.DoesNotContain(embed.EmbeddedTexts, t => t.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindContext_PackIncludesSemanticChunk()
    {
        using var main = new FakeLlamaServer { Responder = _ => "[]" };
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning };
        using var env = Env(main);
        WriteProject(env);

        var r = await FindContextTool.RunAsync(env.Context(Tray(embed), Ct), "where is the money owed computed for a customer", null, 2, 3000, "pack", useModel: true);

        Assert.Contains("## src/Billing/InvoiceCalculator.cs", r);
        Assert.Contains("public decimal Total(decimal net, decimal vat)", r);
    }

    // ───────────────────────── local_memory ─────────────────────────

    [Fact]
    public async Task MemoryRecall_WithEmbeddings_FindsByMeaning()
    {
        using var main = new FakeLlamaServer();
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning };
        using var env = Env(main);
        var ctx = env.Context(Tray(embed), Ct);
        MemoryTool.Run(ctx, "store", "Invoices are rounded to cents only at the very end", "convention", null, null, null, 0);
        MemoryTool.Run(ctx, "store", "The tokenizer splits on whitespace", "fact", null, null, null, 0);

        // Слов запроса нет ни в одной записи: по словам — пусто, по смыслу — запись про счета.
        Assert.StartsWith("Nothing in project memory matches", MemoryTool.Run(ctx, "recall", null, null, null, "money handling", null, 0));
        var r = await MemoryTool.RunAsync(env.Context(Tray(embed), Ct), "recall", null, null, null, "money handling", null, 0);

        Assert.Contains("Invoices are rounded", r);
        Assert.DoesNotContain("tokenizer", r);
        Assert.EndsWith("ranking: hybrid", r);
        var embeddedBefore = embed.EmbeddedTexts.Count;

        // Векторы записей сохраняются: повторный recall эмбеддит только запрос.
        await MemoryTool.RunAsync(env.Context(Tray(embed), Ct), "recall", null, null, null, "money handling", null, 0);
        Assert.Equal(embeddedBefore + 1, embed.EmbeddedTexts.Count);
    }

    [Fact]
    public async Task MemoryRecall_EmbedUnavailable_FallsBackToWords()
    {
        using var main = new FakeLlamaServer();
        using var embed = new FakeVectorServer("offload-embed") { Embed = Meaning, Fail = true };
        using var env = Env(main);
        var ctx = env.Context(Tray(embed), Ct);
        MemoryTool.Run(ctx, "store", "We use PostgreSQL 16 for orders", "decision", null, null, null, 0);

        var r = await MemoryTool.RunAsync(env.Context(Tray(embed), Ct), "recall", null, null, null, "postgresql", null, 0);

        Assert.Contains("PostgreSQL 16", r);
        Assert.DoesNotContain("ranking:", r);
    }
}

/// <summary>Поддельный вспомогательный llama-server: /v1/embeddings и /v1/rerank по заданным функциям.</summary>
internal sealed class FakeVectorServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _embedded = [];
    private readonly List<string> _reranked = [];
    private readonly string _alias;

    public FakeVectorServer(string alias)
    {
        _alias = alias;
        Port = TestEnv.FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }

    /// <summary>Вектор текста (/v1/embeddings).</summary>
    public Func<string, float[]> Embed { get; set; } = _ => [1f, 0f];

    /// <summary>Оценка документа по запросу (/v1/rerank).</summary>
    public Func<string, string, double> Rerank { get; set; } = (_, _) => 0.5;

    /// <summary>Отвечать 500 на /v1/embeddings и /v1/rerank.</summary>
    public bool Fail { get; set; }

    public IReadOnlyList<string> EmbeddedTexts
    {
        get { lock (_embedded) return [.. _embedded]; }
    }

    public IReadOnlyList<string> RerankDocuments
    {
        get { lock (_reranked) return [.. _reranked]; }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/health")
            {
                await WriteJson(ctx, 200, "{\"status\":\"ok\"}");
                return;
            }
            if (path == "/v1/models")
            {
                await WriteJson(ctx, 200, JsonSerializer.Serialize(new { @object = "list", data = new[] { new { id = _alias, @object = "model" } } }));
                return;
            }
            if (ctx.Request.Headers["Authorization"] != "Bearer " + TestEnv.ApiKey)
            {
                await WriteJson(ctx, 401, "{\"error\":{\"code\":401,\"message\":\"Invalid API Key\"}}");
                return;
            }
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
            if (Fail && path is "/v1/embeddings" or "/v1/rerank")
            {
                await WriteJson(ctx, 500, "{\"error\":{\"code\":500,\"message\":\"boom\"}}");
                return;
            }
            using var doc = JsonDocument.Parse(body);
            switch (path)
            {
                case "/v1/embeddings":
                {
                    var inputs = doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    lock (_embedded) _embedded.AddRange(inputs);
                    var data = inputs.Select((t, i) => new { @object = "embedding", index = i, embedding = Embed(t) }).ToArray();
                    await WriteJson(ctx, 200, JsonSerializer.Serialize(new { @object = "list", data, usage = new { prompt_tokens = inputs.Count * 10 } }));
                    return;
                }
                case "/v1/rerank":
                {
                    var query = doc.RootElement.GetProperty("query").GetString() ?? "";
                    var docs = doc.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    lock (_reranked) _reranked.AddRange(docs);
                    var results = docs.Select((d, i) => new { index = i, relevance_score = Rerank(query, d) }).ToArray();
                    await WriteJson(ctx, 200, JsonSerializer.Serialize(new { results }));
                    return;
                }
                default:
                    await WriteJson(ctx, 404, "{}");
                    return;
            }
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private static async Task WriteJson(HttpListenerContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
