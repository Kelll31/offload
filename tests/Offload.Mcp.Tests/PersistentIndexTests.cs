using System.Text;
using Microsoft.Data.Sqlite;
using Offload.Mcp.Index;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>
/// Постоянный индекс кода (SQLite в DataDir/index): расположение, инкрементальное обновление, миграция схемы, отсутствие секретов
/// в базе, параллельный доступ, запасной индекс в памяти, граф вызовов, BM25 и кэш карты проекта.
/// </summary>
[Collection("AppPaths")]
public sealed class PersistentIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string GitHubToken = "gh" + "p_" + "R8mK2qW9zL4vN7bX1cY6hJ3dF5gT0pAsQeUo";

    private static void Seed(TestEnv env)
    {
        env.WriteFile("src/OrderService.cs", """
            namespace Shop;

            public sealed class OrderService(IRepo repo)
            {
                public void Submit(Order order)
                {
                    Validate(order);
                    repo.Save(order);
                }

                private static void Validate(Order order)
                {
                    if (order.Id <= 0) throw new ArgumentException("bad id");
                }
            }

            """);
        env.WriteFile("src/Repo.cs", """
            namespace Shop;

            public sealed class Repo : IRepo
            {
                public void Save(Order order) => Console.WriteLine(order.Id);
            }

            """);
        env.WriteFile("src/IRepo.cs", "namespace Shop;\n\npublic interface IRepo\n{\n    void Save(Order order);\n}\n");
        env.WriteFile("src/Order.cs", "public sealed record Order(int Id);\n");
        env.WriteFile("README.md", "# Shop\nOrders are submitted by OrderService.\n");
    }

    private static Task<IndexView> Open(TestEnv env, bool codeOnly = false, IReadOnlyList<string>? scope = null) =>
        CodeIndex.OpenAsync(env.Context(ct: Ct), scope, codeOnly, Ct);

    private static SqliteConnection Db(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public async Task Open_CreatesDatabaseInDataDir_NotInProject()
    {
        using var env = new TestEnv();
        Seed(env);

        using (var view = await Open(env))
        {
            Assert.True(view.Persistent, "индекс должен быть на диске");
            Assert.Equal(IndexStore.DbPathFor(env.Workspace), view.DbPath);
            Assert.StartsWith(Path.Combine(env.Home.Path, "index"), view.DbPath!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(5, view.Files.Count);
            Assert.Equal(5, view.Sync.Parsed);
        }
        Assert.True(File.Exists(IndexStore.DbPathFor(env.Workspace)));
        Assert.Empty(Directory.EnumerateFiles(env.Workspace, "*.db*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Sync_IsIncremental_ChangedTouchedAddedRemoved()
    {
        using var env = new TestEnv();
        Seed(env);
        using (var first = await Open(env)) Assert.Equal(5, first.Sync.Parsed);

        using (var again = await Open(env))
        {
            Assert.Equal(0, again.Sync.Parsed);
            Assert.Equal(0, again.Sync.Touched);
        }

        // Изменён один файл, другой только «тронут» (то же содержимое, новое время), третий удалён, четвёртый добавлен.
        env.WriteFile("src/Order.cs", "public sealed record Order(int Id, string Customer);\n");
        File.SetLastWriteTimeUtc(env.PathOf("src/Repo.cs"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(env.PathOf("README.md"));
        env.WriteFile("src/Audit.cs", "namespace Shop;\n\npublic static class Audit\n{\n    public static void Log(Order o) => System.Console.WriteLine(o);\n}\n");

        using var view = await Open(env);
        Assert.Equal(2, view.Sync.Parsed);
        Assert.Equal(1, view.Sync.Touched);
        Assert.Equal(1, view.Sync.Removed);
        Assert.Equal(5, view.Files.Count);
        Assert.Contains(view.SymbolsNamed("Audit"), s => s.File.Display == "src/Audit.cs");
        var order = Assert.Single(view.SymbolsNamed("Order"));
        Assert.Contains("string Customer", order.Symbol.Signature);
    }

    [Fact]
    public async Task ScopedOpen_DoesNotPruneFilesOutsideScope()
    {
        using var env = new TestEnv();
        Seed(env);
        using (var all = await Open(env)) Assert.Equal(5, all.Files.Count);

        using (var scoped = await Open(env, scope: ["src/Repo.cs"]))
        {
            Assert.Single(scoped.Files);
            Assert.Equal(0, scoped.Sync.Removed);
        }
        using var again = await Open(env);
        Assert.Equal(0, again.Sync.Parsed);
        Assert.Equal(5, again.Files.Count);
    }

    [Fact]
    public async Task SchemaOrExtractorMismatch_RebuildsIndex()
    {
        using var env = new TestEnv();
        Seed(env);
        using (await Open(env)) { }
        var db = IndexStore.DbPathFor(env.Workspace);
        using (var conn = Db(db))
        {
            Assert.Equal("wal", Scalar(conn, "PRAGMA journal_mode;"));
            Assert.Equal(IndexStore.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), Scalar(conn, "SELECT value FROM meta WHERE key='schema'"));
            Scalar(conn, "UPDATE meta SET value = '0' WHERE key = 'schema'");
        }
        using (var rebuilt = await Open(env)) Assert.Equal(5, rebuilt.Sync.Parsed);

        using (var conn = Db(db)) Scalar(conn, "UPDATE meta SET value = 'old-build' WHERE key = 'extractor'");
        using (var rebuilt = await Open(env)) Assert.Equal(5, rebuilt.Sync.Parsed);
        using var stable = await Open(env);
        Assert.Equal(0, stable.Sync.Parsed);
    }

    [Fact]
    public async Task CorruptDatabase_IsRebuilt()
    {
        using var env = new TestEnv();
        Seed(env);
        var db = IndexStore.DbPathFor(env.Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        File.WriteAllText(db, "this is not a sqlite database, just garbage text that is long enough to fill the header ......................");

        using var view = await Open(env);
        Assert.True(view.Persistent, "испорченная база должна быть пересоздана на диске");
        Assert.Equal(5, view.Files.Count);
    }

    [Fact]
    public async Task Database_NeverContainsRawSecrets()
    {
        using var env = new TestEnv();
        Seed(env);
        env.WriteFile("src/Config.cs", $"public static class Config\n{{\n    public const string Token = \"{GitHubToken}\";\n}}\n");
        env.WriteFile("appsettings.json", "{\n  \"ConnectionStrings\": {\n    \"Default\": \"Server=db;User Id=sa;Password=Sup3rS3cretPassw0rd;\"\n  }\n}\n");

        using (var view = await Open(env))
        {
            Assert.Contains(view.Files, f => f.Display == "src/Config.cs");
            // Поиск по словарю тоже не находит значение секрета.
            var config = view.FileByDisplay("src/Config.cs")!;
            Assert.DoesNotContain(config.Id, view.CandidateFiles(GitHubToken[4..20], wholeWord: false, caseSensitive: true));
        }
        var db = IndexStore.DbPathFor(env.Workspace);
        using (var conn = Db(db))
        {
            Assert.True(Convert.ToInt64(Scalar(conn, "SELECT redacted FROM files WHERE path LIKE '%Config.cs'"), System.Globalization.CultureInfo.InvariantCulture) > 0,
                "маскирование в файле должно быть отмечено");
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM terms WHERE text LIKE '%Sup3rS3cret%' OR text LIKE '%R8mK2qW9zL4vN7bX1%'"));
        }
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(db)!))
        {
            var bytes = await File.ReadAllBytesAsync(file, Ct);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain(GitHubToken, text);
            Assert.DoesNotContain("Sup3rS3cretPassw0rd", text);
        }
    }

    [Fact]
    public async Task SecretFiles_AreNeverIndexed()
    {
        using var env = new TestEnv();
        Seed(env);
        env.WriteFile(".env", "API_TOKEN=Zz9SUPERSECRETVALUE\n");

        using (var view = await Open(env)) Assert.DoesNotContain(view.Files, f => f.Display == ".env");
        using var conn = Db(IndexStore.DbPathFor(env.Workspace));
        Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM files WHERE path LIKE '%.env'"));
        Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM terms WHERE text LIKE '%SUPERSECRET%'"));
    }

    [Fact]
    public async Task ParallelOpens_SameRoot_AllSucceed()
    {
        using var env = new TestEnv();
        Seed(env);

        var views = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Open(env), Ct)));
        try
        {
            Assert.All(views, v => Assert.Equal(5, v.Files.Count));
            Assert.All(views, v => Assert.True(v.Persistent));
            // Разобрал файлы только первый получивший блокировку, остальные увидели готовый индекс.
            Assert.Equal(5, views.Sum(v => v.Sync.Parsed));
            Assert.All(views, v => Assert.Single(v.SymbolsNamed("Submit")));
        }
        finally
        {
            foreach (var v in views) v.Dispose();
        }
    }

    [Fact]
    public async Task UnavailableDataDir_FallsBackToMemory()
    {
        using var env = new TestEnv();
        Seed(env);
        // На месте папки index — файл: базу на диске создать нельзя.
        await File.WriteAllTextAsync(Path.Combine(env.Home.Path, "index"), "not a directory", Ct);

        using (var view = await Open(env))
        {
            Assert.False(view.Persistent);
            Assert.Equal(5, view.Files.Count);
        }
        var r = await SymbolsTool.RunAsync(env.Context(ct: Ct), "callers", "Validate", null, null, 0, 0);
        Assert.Contains("← OrderService.Submit  src/OrderService.cs:5", r);
    }

    [Fact]
    public async Task References_UseInvertedIndex_AndMatchTextualSemantics()
    {
        using var env = new TestEnv();
        Seed(env);
        env.WriteFile("src/Notes.cs", "namespace Shop;\n\npublic static class Notes\n{\n    // Validate(order) in a comment\n    public static string Text() => \"Validate(order)\";\n}\n");

        using var view = await Open(env, codeOnly: true);
        var refs = view.References("Validate", 10);
        var r = Assert.Single(refs);
        Assert.Equal("src/OrderService.cs", r.File.Display);
        Assert.Equal(7, r.Line);
        Assert.Equal("Submit", r.Enclosing?.Name);
    }

    [Fact]
    public async Task CallGraph_CycleAtDepth4_Terminates()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Ping.cs", """
            namespace Net;

            public sealed class Ping
            {
                public void A(int n) { if (n > 0) B(n - 1); }

                public void B(int n) { if (n > 0) C(n - 1); }

                public void C(int n) { if (n > 0) A(n - 1); }
            }

            """);

        var callees = await SymbolsTool.RunAsync(env.Context(ct: Ct), "callees", "A", null, null, 4, 0);
        Assert.Contains("  → Ping.B  src/Ping.cs:7", callees);
        Assert.Contains("    → Ping.C  src/Ping.cs:9", callees);
        // A уже показан корнем: на глубине 3 обход останавливается, без бесконечного цикла.
        Assert.DoesNotContain("      → Ping.A", callees);

        var callers = await SymbolsTool.RunAsync(env.Context(ct: Ct), "callers", "A", null, null, 4, 0);
        Assert.Contains("  ← Ping.C  src/Ping.cs:9", callers);
        Assert.Contains("    ← Ping.B  src/Ping.cs:7", callers);
    }

    [Fact]
    public async Task Callers_FilterForeignTypeQualifierAndPrivateAcrossFiles()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Store.cs", "namespace S;\n\npublic sealed class Store\n{\n    public void Save() { }\n\n    private void Flush() { }\n}\n");
        env.WriteFile("src/Other.cs", "namespace S;\n\npublic static class Other\n{\n    public static void Save() { }\n\n    public static void SaveTwice() { Save(); Save(); }\n}\n");
        env.WriteFile("src/Client.cs", """
            namespace S;

            public sealed class Client(Store store)
            {
                public void UsesStore() => store.Save();

                public void UsesOther() => Other.Save();

                public void UsesFlush() => Flush();

                private void Flush() { }
            }

            """);

        var r = await SymbolsTool.RunAsync(env.Context(ct: Ct), "callers", "Store.Save", null, null, 0, 0);
        Assert.Contains("← Client.UsesStore", r);
        Assert.DoesNotContain("UsesOther", r);
        // «Save()» внутри Other — вызов своего Other.Save, а не Store.Save.
        Assert.DoesNotContain("SaveTwice", r);
        Assert.Contains("← Other.SaveTwice", await SymbolsTool.RunAsync(env.Context(ct: Ct), "callers", "Other.Save", null, null, 0, 0));

        var priv = await SymbolsTool.RunAsync(env.Context(ct: Ct), "callers", "Store.Flush", null, null, 0, 0);
        Assert.DoesNotContain("UsesFlush", priv);
    }

    [Fact]
    public void Tokens_PartsAndChains()
    {
        Assert.Equal(["orderservice", "order", "service"], IndexTokens.Parts("OrderService"));
        Assert.Equal(["httpserver", "http", "server"], IndexTokens.Parts("HTTPServer"));
        Assert.Equal(["parse_header", "parse", "header"], IndexTokens.Parts("parse_header"));
        Assert.Equal(["save"], IndexTokens.Parts("Save"));
        Assert.True(IndexTokens.IsIndexable("repo_1"));
        Assert.False(IndexTokens.IsIndexable("repo.save"));
        Assert.False(IndexTokens.IsIndexable("42"));
        Assert.False(IndexTokens.IsIndexable("x"));

        Assert.Equal("a.b", IndexTokens.ChainOf("x = a.b.Save(1);", "x = a.b.".Length));
        Assert.Equal("repo", IndexTokens.ChainOf("repo?.Save(1);", "repo?.".Length));
        Assert.Equal("()", IndexTokens.ChainOf("Get().Save(1);", "Get().".Length));
        Assert.Equal("new", IndexTokens.ChainOf("var r = new Repo();", "var r = new ".Length));
        Assert.Null(IndexTokens.ChainOf("Save(1);", 0));
        Assert.Equal("b", IndexTokens.ImmediateReceiver("a.b"));
    }

    [Fact]
    public async Task FindContext_Bm25_RareTermOutranksCommonOne_AndPrefixMatches()
    {
        using var env = new TestEnv();
        // «Handler» встречается везде, «Throttle» — в одном файле; «retrying» находится по термину «retry».
        for (var i = 0; i < 12; i++)
            env.WriteFile($"src/H{i}.cs", $"namespace N;\n\npublic sealed class H{i}\n{{\n    public void Handle(Handler handler) => handler.Handle();\n}}\n");
        env.WriteFile("src/Gate.cs", "namespace N;\n\npublic sealed class Gate\n{\n    public void Pass(Handler handler) { Throttle(); }\n\n    private static void Throttle() { }\n}\n");
        env.WriteFile("src/Backoff.cs", "namespace N;\n\npublic sealed class Backoff\n{\n    public void Wait() { var retryingNow = true; }\n}\n");

        var rank = await FindContextTool.RunAsync(env.Context(ct: Ct), "throttle the handler", null, 3, 0, "rank", useModel: false);
        var lines = rank.Replace("\r", "").Split('\n');
        var header = Array.IndexOf(lines, "files by relevance:");
        Assert.StartsWith("  src/Gate.cs  — matches", lines[header + 1]);

        var retry = await FindContextTool.RunAsync(env.Context(ct: Ct), "add retry logic", null, 3, 0, "rank", useModel: false);
        Assert.Contains("src/Backoff.cs", retry);
    }

    [Fact]
    public async Task SearchWord_UsesCandidates_ButFindsSameAsFullScan()
    {
        using var env = new TestEnv(configure: c => c.Mcp.RedactSecrets = false);
        Seed(env);
        var full = await SearchCodeTool.RunAsync(env.Context(ct: Ct), "Repo", "word", null, true, 0, 0, false);

        using var env2 = new TestEnv();
        Seed(env2);
        var indexed = await SearchCodeTool.RunAsync(env2.Context(ct: Ct), "Repo", "word", null, true, 0, 0, false);
        Assert.Equal(full.Split(" · ")[0], indexed.Split(" · ")[0]);

        var sub = await SearchCodeTool.RunAsync(env2.Context(ct: Ct), "rderServ", "text", null, false, 0, 0, false);
        Assert.Contains("src/OrderService.cs", sub);
        Assert.Contains("README.md", sub);
    }

    [Fact]
    public async Task Ids_NotReusedAfterPrune()
    {
        using var env = new TestEnv();
        Seed(env);
        using (await Open(env)) { }
        var db = IndexStore.DbPathFor(env.Workspace);
        long maxFile, maxSymbol;
        using (var conn = Db(db))
        {
            maxFile = (long)Scalar(conn, "SELECT MAX(id) FROM files")!;
            maxSymbol = (long)Scalar(conn, "SELECT MAX(id) FROM symbols")!;
            // Удаляем файлы с наибольшими id строк files и symbols: при MAX(id)+1 их id достались бы новому файлу.
            File.Delete((string)Scalar(conn, "SELECT path FROM files ORDER BY id DESC LIMIT 1")!);
            var withLastSymbol = (string?)Scalar(conn, "SELECT f.path FROM symbols s JOIN files f ON f.id = s.file_id ORDER BY s.id DESC LIMIT 1");
            if (withLastSymbol is not null && File.Exists(withLastSymbol)) File.Delete(withLastSymbol);
        }
        using (var pruned = await Open(env)) Assert.True(pruned.Sync.Removed >= 1, "удалённые файлы должны уйти из индекса");

        env.WriteFile("src/Fresh.cs", "namespace Shop;\n\npublic sealed class Fresh\n{\n    public void Go() { }\n}\n");
        using var view = await Open(env);
        var fresh = view.FileByDisplay("src/Fresh.cs")!;
        Assert.True(fresh.Id > maxFile, $"id нового файла ({fresh.Id}) не должен повторять id удалённого (≤ {maxFile})");
        Assert.All(view.SymbolsOf(fresh), s => Assert.True(s.Id > maxSymbol, "id символов тоже не переиспользуются"));
    }

    [Fact]
    public async Task Snapshot_IsPinnedWhenOpened_LaterWritesInvisible()
    {
        using var env = new TestEnv();
        Seed(env);
        using var view = await Open(env);
        // Другой процесс меняет базу после открытия: представление продолжает видеть свой снимок.
        using (var conn = Db(IndexStore.DbPathFor(env.Workspace))) Scalar(conn, "DELETE FROM symbols");
        Assert.Single(view.SymbolsNamed("Submit"));
    }

    [Fact]
    public async Task DbName_IncludesExtractor_OtherVersionsKeptOrSwept()
    {
        using var env = new TestEnv();
        Seed(env);
        var db = IndexStore.DbPathFor(env.Workspace);
        Assert.EndsWith("-" + IndexStore.Extractor[..8] + ".db", db, StringComparison.Ordinal);
        var dir = Path.GetDirectoryName(db)!;
        Directory.CreateDirectory(dir);
        var hash = IndexStore.RootHash(env.Workspace);
        string Fake(string name, TimeSpan age)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, "index of another Offload version");
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow - age);
            return p;
        }
        var liveOld = Fake(hash + "-11111111.db", TimeSpan.FromHours(2));        // работает процесс старой версии
        var abandoned = Fake(hash + "-00000000.db", TimeSpan.FromDays(4));       // старая версия давно не запускалась
        var otherRoot = Fake(new string('f', 20) + "-00000000.db", TimeSpan.FromDays(4));

        IndexStore.ResetSweep();
        using (var view = await Open(env)) Assert.Equal(db, view.DbPath);

        Assert.Equal("index of another Offload version", File.ReadAllText(liveOld));
        Assert.False(File.Exists(abandoned), "индекс того же корня от старой версии, не используемый 3+ дня, удаляется");
        Assert.True(File.Exists(otherRoot), "индекс другого корня живёт 45 дней");
    }

    [Fact]
    public async Task LockHeldTooLong_FallsBackToMemoryQuickly()
    {
        using var env = new TestEnv();
        Seed(env);
        var db = IndexStore.DbPathFor(env.Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        var saved = IndexStore.LockTimeout;
        IndexStore.LockTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            // Другой процесс держит блокировку записи (строит индекс с нуля).
            using var held = new FileStream(db + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var view = await Open(env);
            Assert.False(view.Persistent, "при занятой блокировке — индекс в памяти");
            Assert.Equal(5, view.Files.Count);
            Assert.Single(view.SymbolsNamed("Submit"));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"ожидание блокировки слишком долгое: {sw.Elapsed}");
        }
        finally
        {
            IndexStore.LockTimeout = saved;
        }
    }

    [Fact]
    public async Task SearchText_CaseInsensitive_FindsKelvinSign()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Units.cs", "namespace U;\n\npublic static class Units\n{\n    public const string Name = \"KELVIN\";\n}\n");
        env.WriteFile("src/Other.cs", "namespace U;\n\npublic static class Other\n{\n}\n");

        // Regex без учёта регистра сопоставляет k со знаком Кельвина, а в словаре от этой строки есть только «ELVIN».
        var r = await SearchCodeTool.RunAsync(env.Context(ct: Ct), "kelvin", "text", null, false, 0, 0, false);
        Assert.Contains("src/Units.cs", r);
        Assert.DoesNotContain("src/Other.cs", r);
        var word = await SearchCodeTool.RunAsync(env.Context(ct: Ct), "kelvin", "word", null, false, 0, 0, false);
        Assert.Contains("src/Units.cs", word);
    }

    [Fact]
    public void CanPrune_CaseInsensitive_ExcludesExactlyLettersWithNonAsciiCaseVariants()
    {
        var strings = Enumerable.Range(0x80, 0xFFFF - 0x80 + 1).Select(c => ((char)c).ToString()).ToArray();
        for (var c = 'a'; c <= 'z'; c++)
            foreach (var letter in new[] { c, char.ToUpperInvariant(c) })
            {
                var re = new System.Text.RegularExpressions.Regex(letter.ToString(),
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                var foldsOutsideAscii = strings.Any(re.IsMatch);
                Assert.True(IndexTokens.CanPrune("_" + letter, caseSensitive: true));
                Assert.True(!foldsOutsideAscii == IndexTokens.CanPrune("_" + letter, caseSensitive: false),
                    $"буква '{letter}': совпадает с не-ASCII символом без учёта регистра = {foldsOutsideAscii}");
            }
    }

    [Fact]
    public async Task ProjectMap_ManifestCache_ReusedUntilManifestChanges()
    {
        using var env = new TestEnv();
        Seed(env);
        env.WriteFile("src/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");

        var first = await ProjectMap.GetAsync(env.Context(ct: Ct), Ct);
        var second = await ProjectMap.GetAsync(env.Context(ct: Ct), Ct);
        Assert.Same(Assert.Single(first.Projects), Assert.Single(second.Projects));
        Assert.Contains(first.Languages, l => l.Lang == "CSharp" && l.Files == 4);

        env.WriteFile("src/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>\n");
        File.SetLastWriteTimeUtc(env.PathOf("src/Shop.csproj"), DateTime.UtcNow.AddMinutes(2));
        var third = await ProjectMap.GetAsync(env.Context(ct: Ct), Ct);
        var p = Assert.Single(third.Projects);
        Assert.NotSame(first.Projects[0], p);
        Assert.Equal("net9.0", p.Framework);
    }
}
