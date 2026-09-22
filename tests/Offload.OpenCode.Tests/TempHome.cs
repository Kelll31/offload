using Offload.Core;

namespace Offload.OpenCode.Tests;

/// <summary>
/// Временный корень данных Offload и папка «глобального» конфига OpenCode,
/// чтобы тесты не трогали профиль пользователя.
/// </summary>
public sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Подмена %USERPROFILE%\.config\opencode.</summary>
    public string GlobalOpenCodeDir => System.IO.Path.Combine(Path, "user-config", "opencode");

    public TempHome()
    {
        Directory.CreateDirectory(Path);
        AppPaths.OverrideDataDir(Path);
        OpenCodeConfigWriter.GlobalConfigDirOverride = GlobalOpenCodeDir;
    }

    public void Dispose()
    {
        OpenCodeConfigWriter.GlobalConfigDirOverride = null;
        AppPaths.OverrideDataDir(null);
        try { Directory.Delete(Path, true); } catch { }
    }
}

[CollectionDefinition("AppPaths", DisableParallelization = true)]
public class AppPathsCollection;
