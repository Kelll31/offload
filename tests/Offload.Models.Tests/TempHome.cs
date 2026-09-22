using Offload.Core;
using Offload.Core.Config;

namespace Offload.Models.Tests;

/// <summary>Временный корень данных Offload, чтобы тесты не трогали профиль пользователя.</summary>
public sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-" + Guid.NewGuid().ToString("N"));

    public TempHome()
    {
        Directory.CreateDirectory(Path);
        AppPaths.OverrideDataDir(Path);
        ConfigStore.Reload();
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(null);
        try { Directory.Delete(Path, true); } catch { }
    }
}

[CollectionDefinition("AppPaths", DisableParallelization = true)]
public class AppPathsCollection;

internal static class TestSafety
{
    /// <summary>Страховка: даже вне TempHome тесты не должны писать в настоящий %LOCALAPPDATA%\Offload.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar)))
            Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar,
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-default-home"));
    }
}
