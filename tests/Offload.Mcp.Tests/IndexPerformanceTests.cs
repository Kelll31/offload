using System.Diagnostics;
using System.Text;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>
/// Производительность постоянного индекса на сгенерированном репозитории (5000 файлов C#): холодный запуск (пустая база),
/// «новый процесс» (база готова, кэш текста пуст) и тёплый запуск. Бюджеты времени нарочно щедрые — тест ловит деградацию
/// на порядки (например, возврат к полному перебору), а не колебания машины; фактические времена пишутся в вывод теста.
/// </summary>
[Collection("AppPaths")]
public sealed class IndexPerformanceTests
{
    private const int FileCount = 5000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static void Generate(TestEnv env)
    {
        for (var i = 0; i < FileCount; i++)
        {
            var next = (i + 1) % FileCount;
            var sb = new StringBuilder();
            sb.Append($"namespace Gen.M{i / 100};\n\n");
            sb.Append($"/// <summary>Generated service {i} with retry and cache helpers.</summary>\n");
            sb.Append($"public sealed class Service{i}(Service{next} next)\n{{\n");
            sb.Append($"    private int _counter{i};\n\n");
            sb.Append($"    public int Handle{i}(string request)\n    {{\n");
            sb.Append($"        var value = Normalize{i}(request);\n");
            sb.Append($"        _counter{i} += value.Length;\n");
            sb.Append($"        return next.Handle{next}(value) + Retry(value);\n    }}\n\n");
            sb.Append($"    private static string Normalize{i}(string input) => input.Trim().ToLowerInvariant();\n\n");
            sb.Append("    public static int Retry(string payload)\n    {\n");
            sb.Append("        for (var attempt = 0; attempt < 3; attempt++)\n        {\n");
            sb.Append("            if (payload.Length > attempt) return attempt;\n        }\n");
            sb.Append("        return -1;\n    }\n");
            // Объём, похожий на настоящий код (~100 строк на файл).
            for (var j = 0; j < 8; j++)
            {
                sb.Append($"\n    public int Step{j}(int amount)\n    {{\n");
                sb.Append($"        var scaled = amount * {j + 2};\n");
                sb.Append($"        if (scaled > {10 * j}) scaled -= Normalize{i}(scaled.ToString()).Length;\n");
                sb.Append($"        // step {j}: keep the counter in sync with the cache\n");
                sb.Append($"        _counter{i} = Math.Max(_counter{i}, scaled);\n");
                sb.Append($"        return scaled + Retry(\"step{j}\");\n    }}\n");
            }
            sb.Append("}\n");
            env.WriteFile($"src/M{i / 100}/Service{i}.cs", sb.ToString());
        }
    }

    private static async Task<(TimeSpan Symbols, TimeSpan Context, TimeSpan Search, string Callers)> RunAll(TestEnv env)
    {
        var sw = Stopwatch.StartNew();
        var callers = await SymbolsTool.RunAsync(env.Context(ct: Ct), "callers", "Service42.Handle42", null, null, 4, 60);
        var symbols = sw.Elapsed;
        sw.Restart();
        await FindContextTool.RunAsync(env.Context(ct: Ct), "Add retry to Service42.Handle42 when Normalize fails", null, 8, 3000, "pack", useModel: false);
        var context = sw.Elapsed;
        sw.Restart();
        await SearchCodeTool.RunAsync(env.Context(ct: Ct), "Normalize4999", "word", null, true, 0, 0, false);
        var search = sw.Elapsed;
        return (symbols, context, search, callers);
    }

    [Fact]
    public async Task GeneratedRepo5k_ColdWarm_WithinBudget()
    {
        using var env = new TestEnv();
        var gen = Stopwatch.StartNew();
        Generate(env);
        Log($"сгенерировано {FileCount} файлов за {gen.Elapsed.TotalSeconds:0.0} с");
        CodeIndex.ClearCache();

        var cold = await RunAll(env);
        CodeIndex.ClearCache(); // «новый MCP-процесс»: база готова, кэша текста нет
        var fresh = await RunAll(env);
        var warm = await RunAll(env);

        static string Fmt((TimeSpan S, TimeSpan C, TimeSpan Q, string _) t) =>
            $"symbols callers d4 {t.S.TotalMilliseconds:0} мс, find_context {t.C.TotalMilliseconds:0} мс, search word {t.Q.TotalMilliseconds:0} мс";
        Log("холодный (пустая база):   " + Fmt(cold));
        Log("новый процесс (база есть): " + Fmt(fresh));
        Log("тёплый:                    " + Fmt(warm));
        var db = new FileInfo(Offload.Mcp.Index.IndexStore.DbPathFor(env.Workspace));
        Log($"размер базы: {db.Length / 1024.0 / 1024.0:0.0} МБ");
        // Для сравнения: прежний путь — загрузка текста всех файлов и полный перебор строк на каждый запрос ссылок.
        CodeIndex.ClearCache();
        var legacy = Stopwatch.StartNew();
        var all = await CodeIndex.LoadAsync(env.Context(ct: Ct), null, codeOnly: true, Ct);
        var loadAll = legacy.Elapsed;
        legacy.Restart();
        for (var k = 0; k < 4; k++) SymbolsTool.FindReferences(all.Files, $"Handle{42 - k}", 200);
        var refsPasses = legacy.Elapsed;
        legacy.Restart();
        var terms = FindContextTool.ExtractTerms("Add retry to Service42.Handle42 when Normalize fails");
        _ = all.Files.AsParallel().Select(f => FindContextTool.Score(f, terms, [], false)).Where(s => s.Score > 0).OrderByDescending(s => s.Score).Take(24).ToList();
        Log($"прежний путь в новом процессе: загрузка текста {loadAll.TotalMilliseconds:0} мс, 4 полных прохода ссылок {refsPasses.TotalMilliseconds:0} мс, " +
            $"оценка find_context по всем строкам {legacy.Elapsed.TotalMilliseconds:0} мс");

        // Граф вызовов — цепочка Service41 → Service42 и дальше назад по кольцу.
        Assert.Contains("← Service41.Handle41", cold.Callers);
        Assert.Contains("      ← Service39.Handle39", cold.Callers);
        Assert.Equal(cold.Callers, warm.Callers);

        Assert.True(cold.Symbols + cold.Context + cold.Search < TimeSpan.FromMinutes(4), "холодный индекс 5000 файлов: " + Fmt(cold));
        Assert.True(fresh.Symbols < TimeSpan.FromSeconds(60), "запрос к готовой базе в новом процессе: " + Fmt(fresh));
        Assert.True(warm.Symbols + warm.Context + warm.Search < TimeSpan.FromSeconds(60), "тёплые запросы: " + Fmt(warm));
    }
}
