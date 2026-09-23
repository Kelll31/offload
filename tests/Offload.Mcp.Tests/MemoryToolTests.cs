using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>local_memory: запись, поиск, список, удаление; отказ хранить секреты; файл памяти — в папке данных Offload.</summary>
[Collection("AppPaths")]
public sealed partial class MemoryToolTests
{
    private const string Fact = "Integration tests need the local Postgres container running";

    [GeneratedRegex(@"\bm[0-9a-f]{6}\b")]
    private static partial Regex IdRegex();

    private static string Store(ToolContext ctx, string text, string? kind = null, string[]? tags = null) =>
        MemoryTool.Run(ctx, "store", text, kind, tags, null, null, 0);

    [Fact]
    public void Store_ThenRecallByWord_ReturnsEntry()
    {
        using var env = new TestEnv();
        var ctx = env.Context();

        var stored = Store(ctx, Fact, "convention", ["db", "Testing"]);
        Assert.StartsWith("Stored convention m", stored);
        Assert.Contains("(1 entries for this project)", stored);

        var recall = MemoryTool.Run(ctx, "recall", null, null, null, "postgres", null, 0);
        Assert.Contains("project memory", recall);
        Assert.Contains("[convention] " + Fact, recall);
        Assert.Contains("#db #testing", recall);

        // Поиск по тегу тоже находит запись.
        Assert.Contains(Fact, MemoryTool.Run(ctx, "recall", null, null, null, "testing", null, 0));
    }

    [Fact]
    public void Store_Duplicate_SaysAlreadyStored()
    {
        using var env = new TestEnv();
        var ctx = env.Context();
        var id = IdRegex().Match(Store(ctx, Fact)).Value;

        var again = Store(ctx, Fact.ToUpperInvariant());

        Assert.Equal($"Already stored as {id}.", again);
        Assert.StartsWith("1 entries;", MemoryTool.Run(ctx, "list", null, null, null, null, null, 0));
    }

    [Fact]
    public void List_ShowsEntriesAndFiltersByKind()
    {
        using var env = new TestEnv();
        var ctx = env.Context();
        Store(ctx, "We chose SQLite over LiteDB for the cache", "decision");
        Store(ctx, "Private fields use the underscore prefix", "convention");

        var all = MemoryTool.Run(ctx, "list", null, null, null, null, null, 0);
        Assert.StartsWith("2 entries; newest 2:", all);
        Assert.Contains("[decision] We chose SQLite", all);
        Assert.Contains("[convention] Private fields", all);

        var decisions = MemoryTool.Run(ctx, "list", null, "decision", null, null, null, 0);
        Assert.Contains("SQLite", decisions);
        Assert.DoesNotContain("underscore", decisions);

        Assert.Equal("No entries.", MemoryTool.Run(ctx, "list", null, "todo", null, null, null, 0));
    }

    [Fact]
    public void Forget_ById_RemovesEntry()
    {
        using var env = new TestEnv();
        var ctx = env.Context();
        var id = IdRegex().Match(Store(ctx, Fact)).Value;
        Store(ctx, "The public API is versioned under /v2");

        Assert.Equal($"Forgot {id}.", MemoryTool.Run(ctx, "forget", null, null, null, null, id, 0));

        var list = MemoryTool.Run(ctx, "list", null, null, null, null, null, 0);
        Assert.DoesNotContain(Fact, list);
        Assert.Contains("versioned under /v2", list);
        Assert.StartsWith("Nothing in project memory matches", MemoryTool.Run(ctx, "recall", null, null, null, "postgres", null, 0));
        Assert.Throws<ToolException>(() => MemoryTool.Run(ctx, "forget", null, null, null, null, id, 0));
    }

    [Fact]
    public void Store_TextWithSecret_IsRefused()
    {
        using var env = new TestEnv();
        var ctx = env.Context();

        var ex = Assert.Throws<ToolException>(() => Store(ctx, "CI token is gh" + "p_R8mK2qW9zL4vN7bX1cY6hJ3dF5gT0pAsQeUo, rotate monthly"));

        Assert.Contains("secret", ex.Message);
        Assert.False(File.Exists(MemoryTool.FileFor(env.Workspace)), "секрет не должен попасть в файл памяти");
    }

    [Fact]
    public void Store_InvalidKind_Throws()
    {
        using var env = new TestEnv();

        var ex = Assert.Throws<ToolException>(() => Store(env.Context(), Fact, "banana"));

        Assert.Contains("kind must be one of", ex.Message);
    }

    [Fact]
    public void Run_InvalidAction_Throws()
    {
        using var env = new TestEnv();

        Assert.Throws<ToolException>(() => MemoryTool.Run(env.Context(), "erase", null, null, null, null, null, 0));
    }

    [Fact]
    public void Recall_EmptyAndNoMatch_ExplainsWhy()
    {
        using var env = new TestEnv();
        var ctx = env.Context();

        Assert.StartsWith("No project memory yet", MemoryTool.Run(ctx, "recall", null, null, null, "anything", null, 0));

        Store(ctx, Fact);
        Assert.Equal("Nothing in project memory matches \"kubernetes\".", MemoryTool.Run(ctx, "recall", null, null, null, "kubernetes", null, 0));
    }

    [Fact]
    public void FileFor_LivesInDataDirNotInWorkspace()
    {
        using var env = new TestEnv();
        var ctx = env.Context();
        Store(ctx, Fact);

        var file = MemoryTool.FileFor(env.Workspace);

        Assert.True(File.Exists(file), "файл памяти создаётся при записи");
        Assert.True(PathGuard.IsInside(file, AppPaths.DataDir), $"файл памяти должен лежать в папке данных: {file}");
        Assert.False(PathGuard.IsInside(file, env.Workspace), "файл памяти не должен лежать в репозитории");
        Assert.Empty(Directory.EnumerateFileSystemEntries(env.Workspace));
        // Один и тот же проект — один файл, независимо от регистра и завершающего разделителя.
        Assert.Equal(file, MemoryTool.FileFor(env.Workspace.ToUpperInvariant() + Path.DirectorySeparatorChar));
    }
}
