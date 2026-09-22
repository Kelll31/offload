using Offload.Core;
using Offload.Core.Config;

namespace Offload.Llama.Tests;

/// <summary>Временный корень данных Offload, чтобы тесты не трогали профиль пользователя.</summary>
public sealed class TempHome : IDisposable
{
    public string Path { get; }

    public TempHome(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-llama-tests-" + Guid.NewGuid().ToString("N"));
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

/// <summary>Тесты, меняющие AppPaths/ConfigStore/статические адреса GitHub, выполняются последовательно.</summary>
[CollectionDefinition("AppPaths", DisableParallelization = true)]
public sealed class AppPathsCollection;
