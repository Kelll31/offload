using Offload.App.Services;
using Offload.Core.Config;
using Offload.Llama;

namespace Offload.App.Tests;

public sealed class HealthWatchdogTests
{
    private static WatchdogVerdict Running(HealthWatchdog w, HealthState h) => w.Observe(ServerState.Running, processAlive: true, inOperation: false, h);

    [Fact]
    public void ThreeConsecutiveFailures_AfterReady_Restart()
    {
        var w = new HealthWatchdog();
        Assert.Equal(WatchdogVerdict.Healthy, Running(w, HealthState.Ready));
        Assert.Equal(WatchdogVerdict.Suspect, Running(w, HealthState.Down));
        Assert.Equal(WatchdogVerdict.Suspect, Running(w, HealthState.Down));
        Assert.Equal(WatchdogVerdict.Restart, Running(w, HealthState.Down));
        Assert.Equal(0, w.Failures);
        Assert.False(w.WasReady, "после решения о перезапуске сторож начинает заново");
    }

    [Fact]
    public void ReadyBetweenFailures_ResetsCounter()
    {
        var w = new HealthWatchdog();
        Running(w, HealthState.Ready);
        Running(w, HealthState.Down);
        Running(w, HealthState.Down);
        Assert.Equal(WatchdogVerdict.Healthy, Running(w, HealthState.Ready));
        Assert.Equal(WatchdogVerdict.Suspect, Running(w, HealthState.Down));
        Assert.Equal(WatchdogVerdict.Suspect, Running(w, HealthState.Down));
        Assert.Equal(2, w.Failures);
    }

    [Fact]
    public void NeverReady_FailuresNotCounted()
    {
        var w = new HealthWatchdog();
        for (var i = 0; i < 10; i++)
            Assert.Equal(WatchdogVerdict.Skip, Running(w, HealthState.Down));
        Assert.Equal(0, w.Failures);
    }

    [Fact]
    public void Loading_AfterReady_IsNeutral()
    {
        // 503 после готовности — модель загружается заново после сна: не сбой, но и не сброс счётчика.
        var w = new HealthWatchdog();
        Running(w, HealthState.Ready);
        Running(w, HealthState.Down);
        for (var i = 0; i < 10; i++)
            Assert.Equal(WatchdogVerdict.Skip, Running(w, HealthState.Loading));
        Assert.Equal(1, w.Failures);
        Assert.True(w.WasReady);
    }

    [Theory]
    [InlineData(ServerState.Starting, true, false)]
    [InlineData(ServerState.Stopping, true, false)]
    [InlineData(ServerState.Stopped, false, false)]
    [InlineData(ServerState.Failed, false, false)]
    [InlineData(ServerState.Running, false, false)]
    [InlineData(ServerState.Running, true, true)]
    public void NotMonitorable_SkipsAndResets(ServerState state, bool alive, bool inOperation)
    {
        var w = new HealthWatchdog();
        Running(w, HealthState.Ready);
        Running(w, HealthState.Down);
        Running(w, HealthState.Down);
        Assert.Equal(WatchdogVerdict.Skip, w.Observe(state, alive, inOperation, HealthState.Down));
        Assert.Equal(0, w.Failures);
        Assert.False(w.WasReady, "после остановки/запуска сервер должен снова стать готовым, прежде чем считать сбои");
    }

    [Fact]
    public void CustomThreshold_One_RestartsImmediately()
    {
        var w = new HealthWatchdog(1);
        Running(w, HealthState.Ready);
        Assert.Equal(WatchdogVerdict.Restart, Running(w, HealthState.Down));
    }
}

public sealed class RestartBudgetTests
{
    [Fact]
    public void TryTake_LimitWithinWindow_ThenFreesAfterWindow()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var budget = new RestartBudget(3, TimeSpan.FromMinutes(10), () => now);
        Assert.True(budget.TryTake(out var a1));
        Assert.True(budget.TryTake(out _));
        Assert.True(budget.TryTake(out var a3));
        Assert.Equal(1, a1);
        Assert.Equal(3, a3);
        Assert.False(budget.TryTake(out _), "четвёртая попытка за окно должна быть отклонена");

        now = now.AddMinutes(11);
        Assert.True(budget.TryTake(out var again));
        Assert.Equal(1, again);
    }
}

public sealed class OpenCodeGlobalRegistrationTests
{
    [Theory]
    [InlineData(true, 8765, 8766, true)]
    [InlineData(true, 8765, 8765, false)]
    [InlineData(false, 8765, 8766, false)]
    [InlineData(false, 8765, 8765, false)]
    public void ShouldUpdateGlobalOpenCode_OnlyWhenOptedInAndPortChanged(bool optedIn, int before, int after, bool expected)
    {
        var cfg = new AppConfig();
        cfg.OpenCode.RegisterInGlobalConfig = optedIn;
        cfg.Server.Port = after;
        Assert.Equal(expected, ServerController.ShouldUpdateGlobalOpenCode(cfg, before));
    }
}
