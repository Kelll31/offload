using Offload.App.Services;

namespace Offload.App.Tests;

public sealed class DevModeTests
{
    [Theory]
    [InlineData(null, @"E:\src\offload\src\Offload.App\bin\Debug\net10.0-windows\win-x64\Offload.exe", true)]
    [InlineData(null, @"E:\src\offload\src\Offload.App\bin\Release\net10.0-windows\win-x64\Offload.exe", true)]
    [InlineData(null, @"C:\Users\u\AppData\Local\Programs\Offload\Offload.exe", false)]
    [InlineData(null, @"E:\github\offload\publish\Offload.exe", false)]
    [InlineData("1", @"C:\Users\u\AppData\Local\Programs\Offload\Offload.exe", true)]
    [InlineData("true", @"C:\tools\Offload.exe", true)]
    [InlineData("0", @"E:\src\offload\src\Offload.App\bin\Debug\Offload.exe", false)]
    [InlineData(null, null, false)]
    public void Detect_EnvAndPath(string? env, string? exe, bool expected)
    {
        Assert.Equal(expected, DevMode.Detect(env, exe));
    }
}
