using System.Text;
using System.Text.RegularExpressions;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Разбор/применение unified diff (UnifiedPatch) и local_apply_patch: атомарность, кодировки, dry_run, защита путей, проверка с откатом и откат задачи.</summary>
[Collection("AppPaths")]
public sealed class ApplyPatchTests
{
    private static string P(params string[] lines) => string.Join("\n", lines) + "\n";

    private static string JobId(string output)
    {
        var m = Regex.Match(output, @"job_id: (\S+)");
        Assert.True(m.Success, "в ответе нет job_id: " + output);
        return m.Groups[1].Value;
    }

    private static FilePatch Single(string patch)
    {
        var files = UnifiedPatch.Parse(patch);
        UnifiedPatch.StripPrefixes(files);
        return Assert.Single(files);
    }

    private static readonly string SimpleHunk = P(
        "--- a/f.txt",
        "+++ b/f.txt",
        "@@ -1,3 +1,3 @@",
        " one",
        "-two",
        "+TWO",
        " three");

    [Fact]
    public void Parse_GitDiffWithTwoFiles_StripsPrefixes()
    {
        var patch = P(
            "diff --git a/src/a.txt b/src/a.txt",
            "index 1111111..2222222 100644",
            "--- a/src/a.txt",
            "+++ b/src/a.txt",
            "@@ -1,3 +1,3 @@",
            " one",
            "-two",
            "+TWO",
            " three",
            "diff --git a/b.txt b/b.txt",
            "index 3333333..4444444 100644",
            "--- a/b.txt",
            "+++ b/b.txt",
            "@@ -2,2 +2,3 @@ context",
            " x",
            " y",
            "+z");
        var files = UnifiedPatch.Parse(patch);
        UnifiedPatch.StripPrefixes(files);

        Assert.Equal(2, files.Count);
        Assert.Equal("src/a.txt", files[0].OldPath);
        Assert.Equal("src/a.txt", files[0].NewPath);
        Assert.Equal("b.txt", files[1].Target);
        var h = Assert.Single(files[0].Hunks);
        Assert.Equal(1, h.OldStart);
        Assert.Equal(3, h.OldCount);
        Assert.Equal(new[] { ' ', '-', '+', ' ' }, h.Lines.Select(l => l.Op));
        var h2 = Assert.Single(files[1].Hunks);
        Assert.Equal(2, h2.OldStart);
        Assert.Equal(3, h2.Lines.Count);
    }

    [Fact]
    public void Apply_WithCorrectLineNumbers_ReplacesLine()
    {
        var (lines, fin, error) = UnifiedPatch.Apply(["one", "two", "three"], true, Single(SimpleHunk));
        Assert.Null(error);
        Assert.Equal(new[] { "one", "TWO", "three" }, lines);
        Assert.True(fin);
    }

    [Fact]
    public void Apply_WithShiftedLineNumbers_FindsBlockNearby()
    {
        string[] original = ["h1", "h2", "h3", "h4", "h5", "one", "two", "three", "tail"];
        var (lines, _, error) = UnifiedPatch.Apply(original, true, Single(SimpleHunk));
        Assert.Null(error);
        Assert.Equal(new[] { "h1", "h2", "h3", "h4", "h5", "one", "TWO", "three", "tail" }, lines);
    }

    [Fact]
    public void Apply_WhitespaceOnlyDifferencesInContext_StillMatch()
    {
        var patch = P(
            "--- a/f.cs",
            "+++ b/f.cs",
            "@@ -1,3 +1,3 @@",
            " if (x) {",
            "-two();",
            "+TWO();",
            " }");
        var (lines, _, error) = UnifiedPatch.Apply(["if (x)   {", "\t\ttwo();  ", "}\t"], true, Single(patch));
        Assert.Null(error);
        Assert.Equal(3, lines!.Count);
        Assert.Equal("TWO();", lines[1]);
        // Контекстные строки остаются как в файле: патч меняет только строки «-»/«+».
        Assert.Equal("if (x)   {", lines[0]);
        Assert.Equal("}\t", lines[2]);
    }

    [Fact]
    public void Parse_FormatPatchSignatureAfterCountedHunk_IsNotPartOfHunk()
    {
        var files = UnifiedPatch.Parse(P(
            "--- a/f.txt",
            "+++ b/f.txt",
            "@@ -1,2 +1,2 @@",
            " one",
            "-two",
            "+TWO",
            "-- ",
            "2.45.0"));
        var hunk = Assert.Single(Assert.Single(files).Hunks);
        Assert.Equal(3, hunk.Lines.Count);
        var (lines, _, error) = UnifiedPatch.Apply(["one", "two"], true, files[0]);
        Assert.Null(error);
        Assert.Equal(new[] { "one", "TWO" }, lines);
    }

    [Fact]
    public void MergeSections_TwoSectionsForOneFile_AppliesBothHunks()
    {
        var files = UnifiedPatch.Parse(P(
            "--- a/f.txt", "+++ b/f.txt", "@@ -1,1 +1,1 @@", "-a", "+A",
            "--- a/f.txt", "+++ b/f.txt", "@@ -3,1 +3,1 @@", "-c", "+C"));
        UnifiedPatch.StripPrefixes(files);
        var merged = Assert.Single(ApplyPatchTool.MergeSections(files));
        var (lines, _, error) = UnifiedPatch.Apply(["a", "b", "c"], true, merged);
        Assert.Null(error);
        Assert.Equal(new[] { "A", "b", "C" }, lines);
    }

    [Fact]
    public void Apply_MismatchingContext_ReturnsError()
    {
        var (lines, _, error) = UnifiedPatch.Apply(["alpha", "beta", "gamma"], true, Single(SimpleHunk));
        Assert.Null(lines);
        Assert.NotNull(error);
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void Apply_InsertionHunkWithZeroOldCount_InsertsAfterLine()
    {
        var patch = P(
            "--- a/f.txt",
            "+++ b/f.txt",
            "@@ -3,0 +4,2 @@",
            "+x",
            "+y");
        var f = Single(patch);
        Assert.Equal(2, Assert.Single(f.Hunks).Lines.Count);
        var (lines, _, error) = UnifiedPatch.Apply(["a", "b", "c", "d"], true, f);
        Assert.Null(error);
        Assert.Equal(new[] { "a", "b", "c", "x", "y", "d" }, lines);
    }

    [Fact]
    public void Apply_NewFileFromDevNull_CreatesContent()
    {
        var patch = P(
            "diff --git a/new.txt b/new.txt",
            "new file mode 100644",
            "--- /dev/null",
            "+++ b/new.txt",
            "@@ -0,0 +1,2 @@",
            "+hello",
            "+world");
        var f = Single(patch);
        Assert.True(f.IsNew);
        Assert.Equal("new.txt", f.Target);
        var (lines, fin, error) = UnifiedPatch.Apply([], true, f);
        Assert.Null(error);
        Assert.Equal(new[] { "hello", "world" }, lines);
        Assert.True(fin);
    }

    [Fact]
    public void Apply_NoNewlineMarkerOnOldSide_AddsFinalNewline()
    {
        var patch = P(
            "--- a/f.txt",
            "+++ b/f.txt",
            "@@ -1,2 +1,2 @@",
            " a",
            "-b",
            "\\ No newline at end of file",
            "+c");
        var f = Single(patch);
        var h = Assert.Single(f.Hunks);
        Assert.True(h.NoNewlineAtEndOfOld);
        Assert.False(h.NoNewlineAtEndOfNew);
        var (lines, fin, error) = UnifiedPatch.Apply(["a", "b"], false, f);
        Assert.Null(error);
        Assert.Equal(new[] { "a", "c" }, lines);
        Assert.True(fin, "после правки файл должен заканчиваться переводом строки");
    }

    [Fact]
    public void Apply_NoNewlineMarkerOnNewSide_RemovesFinalNewline()
    {
        var patch = P(
            "--- a/f.txt",
            "+++ b/f.txt",
            "@@ -1,2 +1,2 @@",
            " a",
            "-b",
            "+c",
            "\\ No newline at end of file");
        var f = Single(patch);
        Assert.True(Assert.Single(f.Hunks).NoNewlineAtEndOfNew);
        var (lines, fin, error) = UnifiedPatch.Apply(["a", "b"], true, f);
        Assert.Null(error);
        Assert.Equal(new[] { "a", "c" }, lines);
        Assert.False(fin, "маркер на новой стороне — без завершающего перевода строки");
    }

    [Fact]
    public void Parse_SqlCommentRemovedInsideCountedHunk_IsNotAFileHeader()
    {
        // Удаление «-- x» и добавление «++ y» дают строки «--- x» / «+++ y», похожие на заголовок файла.
        var patch = P(
            "--- a/q.sql",
            "+++ b/q.sql",
            "@@ -1,3 +1,3 @@",
            " select 1;",
            "--- x",
            "+++ y",
            " select 2;");
        var files = UnifiedPatch.Parse(patch);
        UnifiedPatch.StripPrefixes(files);
        var f = Assert.Single(files);
        Assert.Equal("q.sql", f.Target);
        var h = Assert.Single(f.Hunks);
        Assert.Equal(4, h.Lines.Count);
        var (lines, _, error) = UnifiedPatch.Apply(["select 1;", "-- x", "select 2;"], true, f);
        Assert.Null(error);
        Assert.Equal(new[] { "select 1;", "++ y", "select 2;" }, lines);
    }

    private static readonly string ModifyA = P(
        "diff --git a/src/a.txt b/src/a.txt",
        "--- a/src/a.txt",
        "+++ b/src/a.txt",
        "@@ -1,3 +1,3 @@",
        " one",
        "-two",
        "+TWO",
        " three");

    private static readonly string CreateNew = P(
        "diff --git a/src/new.txt b/src/new.txt",
        "new file mode 100644",
        "--- /dev/null",
        "+++ b/src/new.txt",
        "@@ -0,0 +1,1 @@",
        "+created");

    [Fact]
    public async Task Run_ModifyCreateDelete_InOnePatch()
    {
        using var env = new TestEnv();
        env.WriteFile("src/a.txt", "one\ntwo\nthree\n");
        env.WriteFile("old.txt", "bye\n");
        var patch = ModifyA + CreateNew + P(
            "diff --git a/old.txt b/old.txt",
            "deleted file mode 100644",
            "--- a/old.txt",
            "+++ /dev/null",
            "@@ -1 +0,0 @@",
            "-bye");

        var r = await ApplyPatchTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), patch, null, false, true, 0);

        Assert.Contains("job_id:", r);
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(env.PathOf("src/a.txt")));
        Assert.Equal("created\n", File.ReadAllText(env.PathOf("src/new.txt")));
        Assert.False(File.Exists(env.PathOf("old.txt")), "удаляемый файл должен исчезнуть");
    }

    [Fact]
    public async Task Run_CrlfAndUtf8Bom_ArePreserved()
    {
        using var env = new TestEnv();
        env.WriteFile("src/c.cs", "line1\r\nline2\r\nline3\r\n", new UTF8Encoding(true));
        var patch = P(
            "--- a/src/c.cs",
            "+++ b/src/c.cs",
            "@@ -1,3 +1,3 @@",
            " line1",
            "-line2",
            "+LINE2",
            " line3");

        await ApplyPatchTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), patch, null, false, true, 0);

        var bytes = File.ReadAllBytes(env.PathOf("src/c.cs"));
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM UTF-8 должен сохраниться");
        Assert.Equal("line1\r\nLINE2\r\nline3\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task Run_DryRun_WritesNothing()
    {
        using var env = new TestEnv();
        env.WriteFile("src/a.txt", "one\ntwo\nthree\n");

        var r = await ApplyPatchTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), ModifyA + CreateNew, null, true, true, 0);

        Assert.Contains("dry run", r);
        Assert.Contains("src/a.txt", r);
        Assert.Contains("src/new.txt", r);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(env.PathOf("src/a.txt")));
        Assert.False(File.Exists(env.PathOf("src/new.txt")), "dry_run не должен создавать файлы");
    }

    [Fact]
    public async Task Run_FailingHunkInSecondFile_LeavesFirstFileUntouched()
    {
        using var env = new TestEnv();
        env.WriteFile("src/a.txt", "one\ntwo\nthree\n");
        env.WriteFile("src/b.txt", "alpha\nbeta\n");
        var patch = ModifyA + P(
            "--- a/src/b.txt",
            "+++ b/src/b.txt",
            "@@ -1,2 +1,2 @@",
            " nothing",
            "-like",
            "+this");

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            ApplyPatchTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), patch, null, false, true, 0));

        Assert.Contains("src/b.txt", ex.Message);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(env.PathOf("src/a.txt")));
        Assert.Equal("alpha\nbeta\n", File.ReadAllText(env.PathOf("src/b.txt")));
    }

    [Fact]
    public async Task Run_PathTraversal_IsRefused()
    {
        using var env = new TestEnv();
        var name = "outside-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var patch = P(
            "--- /dev/null",
            "+++ b/../" + name,
            "@@ -0,0 +1,1 @@",
            "+pwned");

        await Assert.ThrowsAsync<ToolException>(() =>
            ApplyPatchTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), patch, null, false, true, 0));

        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(env.Workspace)!, name)), "файл вне рабочей папки не должен появиться");
    }

    [Fact]
    public async Task Run_AbsolutePathOutsideWorkspace_IsRefused()
    {
        using var env = new TestEnv();
        var target = Path.Combine(Path.GetTempPath(), "pc-abs-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var patch = P(
            "--- /dev/null",
            "+++ " + target.Replace('\\', '/'),
            "@@ -0,0 +1,1 @@",
            "+pwned");

        try
        {
            await Assert.ThrowsAsync<ToolException>(() =>
                ApplyPatchTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), patch, null, false, true, 0));
            Assert.False(File.Exists(target), "абсолютный путь вне проекта не должен записываться");
        }
        finally
        {
            try { File.Delete(target); } catch { }
        }
    }

    [Fact]
    public async Task Run_VerifyFails_RollsBackAllFiles()
    {
        using var env = new TestEnv();
        env.WriteFile("src/a.txt", "one\ntwo\nthree\n");

        var r = await ApplyPatchTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), ModifyA + CreateNew,
            "dotnet build missing-project.csproj", false, true, 120);

        Assert.Contains("ROLLED BACK", r);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(env.PathOf("src/a.txt")));
        Assert.False(File.Exists(env.PathOf("src/new.txt")), "созданный патчем файл удаляется при откате");
        Assert.Equal(JobStatus.Reverted, JobStore.Load(JobId(r)).Status);
    }

    [Fact]
    public async Task Run_JobCanBeReverted()
    {
        using var env = new TestEnv();
        env.WriteFile("src/a.txt", "one\ntwo\nthree\n");

        var r = await ApplyPatchTool.RunAsync(env.Context(ct: TestContext.Current.CancellationToken), ModifyA + CreateNew, null, false, true, 0);
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(env.PathOf("src/a.txt")));

        var undo = JobStore.Revert(JobStore.Load(JobId(r)), force: false);

        Assert.True(undo.Ok, undo.Message);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(env.PathOf("src/a.txt")));
        Assert.False(File.Exists(env.PathOf("src/new.txt")), "созданный патчем файл удаляется при откате");
    }
}
