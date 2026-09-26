using Offload.App.Services;
using Offload.Core.Ipc;

namespace Offload.App.Tests;

/// <summary>Фоновые задачи в трее: отказ на неверное описание без уведомления, тексты итогов, занятость при выходе.</summary>
[Collection("AppPaths")]
public sealed class BackgroundJobHostTests
{
    [Fact]
    public void Start_InvalidSpec_RejectedWithoutNotification()
    {
        using var home = new TempHome();
        var notified = 0;
        using var host = new BackgroundJobHost((_, _, _) => notified++);

        var noSpec = host.Start(new IpcRequest(IpcCommands.JobStart));
        var garbage = host.Start(new IpcRequest(IpcCommands.JobStart, new() { ["spec"] = "{\"jobId\":\"../../x\",\"roots\":[\"C:\\\\\"]}" }));

        Assert.False(noSpec.Ok, "без описания задача не принимается");
        Assert.False(garbage.Ok, "подделанное описание не принимается");
        Assert.Equal("rejected", garbage.Data?["error"]);
        Assert.Equal(0, notified);
        Assert.Equal(0, BackgroundJobHost.RunningCount);
    }

    [Fact]
    public void RecoverInterrupted_NothingToRecover_NoNotification()
    {
        using var home = new TempHome();
        var notified = 0;
        using var host = new BackgroundJobHost((_, _, _) => notified++);
        host.RecoverInterrupted();
        Assert.Equal(0, notified);
    }

    [Theory]
    [InlineData("applied", ToolTipIcon.Info)]
    [InlineData("pending_merge", ToolTipIcon.Info)]
    [InlineData("failed", ToolTipIcon.Error)]
    [InlineData("cancelled", ToolTipIcon.Warning)]
    [InlineData("conflict", ToolTipIcon.Warning)]
    public void Outcome_MapsStatusToIcon(string status, ToolTipIcon icon)
    {
        Assert.Equal(icon, BackgroundJobHost.Outcome(status).Icon);
        Assert.NotEqual(status, BackgroundJobHost.StatusText(status));
    }

    [Fact]
    public void AddBusy_NoJobs_KeepsDescription()
    {
        Assert.Null(BackgroundJobHost.AddBusy(null));
        Assert.Equal("x", BackgroundJobHost.AddBusy("x"));
    }
}
