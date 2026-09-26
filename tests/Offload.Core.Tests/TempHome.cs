using Offload.Core;

namespace Offload.Core.Tests;

/// <summary>Временный корень данных Offload, чтобы тесты не трогали профиль пользователя.</summary>
public sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-" + Guid.NewGuid().ToString("N"));

    public TempHome()
    {
        Directory.CreateDirectory(Path);
        AppPaths.OverrideDataDir(Path);
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(null);
        try { Directory.Delete(Path, true); } catch { }
    }
}

internal static class TestSafety
{
    /// <summary>Страховка: даже вне TempHome тесты не должны читать и писать настоящий %LOCALAPPDATA%\Offload.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar)))
            Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar,
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-core-default-home"));
    }
}
