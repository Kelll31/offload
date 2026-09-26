using Offload.App.Services;

namespace Offload.App.Tests;

public sealed class UsageStatsTests
{
    [Fact]
    public void Dollars_Zero_FormatsWithoutFraction()
    {
        Assert.False(string.IsNullOrWhiteSpace(UsageStats.Dollars(0)), "форматирование суммы не должно быть пустым");
    }
}
