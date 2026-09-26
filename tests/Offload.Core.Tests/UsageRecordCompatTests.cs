using System.Text.Json;
using Offload.Core.Usage;
using Offload.Core.Util;

namespace Offload.Core.Tests;

/// <summary>Новые поля UsageRecord (Workspace, QueueWaitMs, GenerationTps) не ломают чтение старых записей.</summary>
[Collection("AppPaths")]
public class UsageRecordCompatTests
{
    private const string OldLine =
        "{\"timestampUtc\":\"2025-11-02T10:00:00Z\",\"tool\":\"local_ask_files\",\"client\":\"claude-code\",\"promptTokens\":1000," +
        "\"completionTokens\":200,\"durationMs\":1500,\"ok\":true,\"model\":\"Qwen\",\"estimatedSavedTokens\":900}";

    [Fact]
    public void OldRecord_WithoutNewFields_StillLoads()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.UsageFile)!);
        File.WriteAllText(AppPaths.UsageFile, OldLine + "\n");

        var rec = Assert.Single(UsageLog.ReadAll());
        Assert.Equal("local_ask_files", rec.Tool);
        Assert.Equal(900, rec.EstimatedSavedTokens);
        Assert.Null(rec.Workspace);
        Assert.Equal(0, rec.QueueWaitMs);
        Assert.Null(rec.GenerationTps);

        var parsed = UsageLog.Parse(OldLine);
        Assert.NotNull(parsed);
        Assert.Equal(rec, parsed);
    }

    [Fact]
    public void NewFields_RoundTrip_AndNullsAreOmitted()
    {
        using var home = new TempHome();
        var rec = new UsageRecord(DateTime.UtcNow, "local_search_code", null, 0, 0, 40, true, null, 1234)
        {
            Workspace = "offload#1a2b3c4d",
            QueueWaitMs = 3500,
            GenerationTps = 41.5,
        };
        UsageLog.AppendLocal(rec);
        UsageLog.AppendLocal(new UsageRecord(DateTime.UtcNow, "local_symbols", null, 0, 0, 10, true));

        var all = UsageLog.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("offload#1a2b3c4d", all[0].Workspace);
        Assert.Equal(3500, all[0].QueueWaitMs);
        Assert.Equal(41.5, all[0].GenerationTps);

        var json = JsonSerializer.Serialize(all[1], Json.Compact);
        Assert.DoesNotContain("workspace", json);
        Assert.DoesNotContain("generationTps", json);
        Assert.Contains("\"queueWaitMs\":0", json);
    }

    [Fact]
    public void NewRecord_ParsedByReaderThatIgnoresUnknownFields()
    {
        // Старый трей получает запись по IPC: неизвестные свойства должны пропускаться, а не ломать разбор.
        var json = JsonSerializer.Serialize(new UsageRecord(DateTime.UtcNow, "t", "c", 1, 2, 3, true) { QueueWaitMs = 5, Workspace = "w#00000000" }, Json.Compact)
            .Replace("\"queueWaitMs\"", "\"someFutureField\":1,\"queueWaitMs\"", StringComparison.Ordinal);
        var parsed = UsageLog.Parse(json);
        Assert.NotNull(parsed);
        Assert.Equal(5, parsed!.QueueWaitMs);
        Assert.Equal("w#00000000", parsed.Workspace);
    }
}
