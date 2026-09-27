using Offload.App.Services;

namespace Offload.App.Tests;

public sealed class AutostartTests
{
    private const string Me = @"C:\Tools\Offload\Offload.exe";
    private const string Other = @"C:\Users\u\AppData\Local\Programs\Offload\Offload.exe";
    private static readonly string Command = $"\"{Me}\" --background";

    [Theory]
    [InlineData("\"C:\\A B\\Offload.exe\" --background", @"C:\A B\Offload.exe")]
    [InlineData(@"C:\A\Offload.exe --background", @"C:\A\Offload.exe")]
    [InlineData(@"C:\A\Offload.exe", @"C:\A\Offload.exe")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("\"", null)]
    public void ExeOf_ParsesRunValue(string? value, string? expected) => Assert.Equal(expected, Autostart.ExeOf(value));

    [Fact]
    public void PointsHere_Enabled() =>
        Assert.Equal((false, true), Autostart.Decide(true, true, Command, Command, _ => true));

    [Fact]
    public void Empty_Disabled() =>
        Assert.Equal((false, false), Autostart.Decide(true, true, null, Command, _ => true));

    [Fact]
    public void OtherExistingCopy_NotHijacked()
    {
        // Установленная копия существует — портативная или dev-копия не переписывает её автозапуск на себя.
        Assert.Equal((false, false), Autostart.Decide(true, true, $"\"{Other}\" --background", Command, p => p == Other));
    }

    [Fact]
    public void ProgramMoved_Repointed() =>
        Assert.Equal((true, true), Autostart.Decide(true, true, $"\"{Other}\" --background", Command, _ => false));

    [Fact]
    public void Uninstall_RemovesOnlyOwnOrDangling()
    {
        Assert.True(Autostart.OwnedBy(Command, Me, _ => true));
        Assert.True(Autostart.OwnedBy($"\"{Other}\" --background", Me, _ => false)); // exe удалён — значение ничьё
        Assert.False(Autostart.OwnedBy($"\"{Other}\" --background", Me, _ => true)); // другая существующая копия
        Assert.False(Autostart.OwnedBy(null, Me, _ => true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ProgramMoved_ButNotWanted_OrSetupPending_NotRepointed(bool setupCompleted, bool wanted) =>
        Assert.Equal((false, false), Autostart.Decide(setupCompleted, wanted, $"\"{Other}\" --background", Command, _ => false));
}
