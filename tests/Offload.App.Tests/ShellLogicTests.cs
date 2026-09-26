using System.Drawing;
using System.Windows.Forms;
using Offload.App.Forms.Pages;
using Offload.App.Services;
using Offload.Core.Config;
using Offload.Core.Localization;
using Offload.Core.Usage;

namespace Offload.App.Tests;

public sealed class WindowPlacementTests
{
    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    private static readonly Rectangle Right = new(1920, 0, 2560, 1400);
    private static readonly Size Min = new(860, 600);

    private static WindowPlacement P(int x, int y, int w, int h, bool max = false) =>
        new() { X = x, Y = y, Width = w, Height = h, Maximized = max };

    [Fact]
    public void Restore_SameMonitor_KeepsBounds()
    {
        var r = WindowPlacements.Restore(P(100, 50, 1120, 760), [Primary, Right], _ => 96, 96, Min);
        Assert.Equal(new Rectangle(100, 50, 1120, 760), r);
    }

    [Fact]
    public void Restore_MonitorRemoved_ReturnsNull()
    {
        var saved = P(2200, 100, 1120, 760); // был на правом мониторе, которого больше нет
        Assert.Null(WindowPlacements.Restore(saved, [Primary], _ => 96, 96, Min));
    }

    [Fact]
    public void Restore_PartlyOffScreen_IsPulledInside()
    {
        var r = WindowPlacements.Restore(P(1500, 700, 1120, 760), [Primary], _ => 96, 96, Min)!.Value;
        Assert.True(Primary.Contains(r), $"окно {r} должно целиком помещаться на экране");
        Assert.Equal(new Size(1120, 760), r.Size);
    }

    [Fact]
    public void Restore_ScalesLogicalSizeToMonitorDpi_AndClampsToWorkingArea()
    {
        var r = WindowPlacements.Restore(P(2000, 100, 1000, 700), [Primary, Right], p => Right.Contains(p) ? 144 : 96, 96, Min)!.Value;
        Assert.Equal(new Size(1500, 1050), r.Size);

        var big = WindowPlacements.Restore(P(10, 10, 5000, 3000), [Primary], _ => 96, 96, Min)!.Value;
        Assert.Equal(Primary, big);
    }

    [Fact]
    public void Restore_TooSmallOrInvalid_UsesMinimumOrNothing()
    {
        var r = WindowPlacements.Restore(P(10, 10, 100, 100), [Primary], _ => null, 96, Min)!.Value;
        Assert.Equal(Min, r.Size);
        Assert.Null(WindowPlacements.Restore(P(0, 0, 0, 0), [Primary], _ => 96, 96, Min));
        Assert.Null(WindowPlacements.Restore(null, [Primary], _ => 96, 96, Min));
    }

    [Fact]
    public void Capture_StoresLogicalSize()
    {
        var p = WindowPlacements.Capture(new Rectangle(-1500, 20, 1680, 1140), maximized: true, dpi: 144);
        Assert.Equal((-1500, 20, 1120, 760, true), (p.X, p.Y, p.Width, p.Height, p.Maximized));
        Assert.True(WindowPlacements.Same(p, WindowPlacements.Capture(new Rectangle(-1500, 20, 1680, 1140), true, 144)));
        Assert.False(WindowPlacements.Same(p, null));
    }
}

public sealed class ThrottledValueTests
{
    [Fact]
    public async Task GetAsync_CallsFetchAtMostOncePerTtl()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new ThrottledValue<int>(TimeSpan.FromSeconds(10), () => now);
        var calls = 0;
        Task<int> Fetch() => Task.FromResult(++calls);

        Assert.False(cache.TryGetFresh(out _));
        Assert.Equal(1, await cache.GetAsync(Fetch));
        Assert.Equal(1, await cache.GetAsync(Fetch));
        now = now.AddSeconds(9);
        Assert.True(cache.TryGetFresh(out var v));
        Assert.Equal(1, v);
        now = now.AddSeconds(2);
        Assert.False(cache.TryGetFresh(out _));
        Assert.Equal(2, await cache.GetAsync(Fetch));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetAsync_ConcurrentCallers_ShareOneFetch()
    {
        var cache = new ThrottledValue<int>(TimeSpan.FromSeconds(10));
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<int> Fetch()
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        }

        var a = cache.GetAsync(Fetch);
        var b = cache.GetAsync(Fetch);
        gate.SetResult(7);
        Assert.Equal((7, 7), (await a, await b));
        Assert.Equal(1, calls);
    }
}

public sealed class ShellTextsTests
{
    [Theory]
    [InlineData(9, "Ctrl+1 … Ctrl+9")]
    [InlineData(8, "Ctrl+1 … Ctrl+8")]
    [InlineData(12, "Ctrl+1 … Ctrl+9")]
    [InlineData(1, "Ctrl+1")]
    public void SectionShortcut_FollowsPageCount(int count, string expected) =>
        Assert.Equal(expected, AboutPage.SectionShortcut(count));

    [Fact]
    public void ServerNotifications_OpenLogForProblems()
    {
        Assert.Equal(Tabs.Log, TrayApplicationContext.ServerNotificationTab(ToolTipIcon.Error));
        Assert.Equal(Tabs.Log, TrayApplicationContext.ServerNotificationTab(ToolTipIcon.Warning));
        Assert.Equal(Tabs.Status, TrayApplicationContext.ServerNotificationTab(ToolTipIcon.Info));
    }

    [Fact]
    public void ClientName_TranslatesOnlyUnknownKey()
    {
        Assert.Equal(L.T("неизвестно"), StatusPage.ClientName(UsageLog.UnknownClient));
        Assert.Equal("cursor", StatusPage.ClientName("cursor"));
    }
}
