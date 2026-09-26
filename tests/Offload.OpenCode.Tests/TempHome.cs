using System.Runtime.CompilerServices;
using Offload.Core;
using Offload.Integrations;

namespace Offload.OpenCode.Tests;

/// <summary>
/// Временный корень данных Offload и песочница IntegrationEnvironment (домашняя папка — внутри временной),
/// чтобы тесты не трогали профиль пользователя, в том числе глобальный конфиг OpenCode (~/.config/opencode).
/// </summary>
public sealed class TempHome : IDisposable
{
    private readonly IDisposable _sandbox;

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Домашняя папка песочницы (вместо %USERPROFILE%).</summary>
    public string Profile => System.IO.Path.Combine(Path, "profile");

    /// <summary>Подмена %USERPROFILE%\.config\opencode.</summary>
    public string GlobalOpenCodeDir => System.IO.Path.Combine(Profile, ".config", "opencode");

    public TempHome(IReadOnlyDictionary<string, string>? variables = null)
    {
        Directory.CreateDirectory(Profile);
        AppPaths.OverrideDataDir(Path);
        _sandbox = IntegrationEnvironment.Override(Profile, variables);
    }

    public void Dispose()
    {
        _sandbox.Dispose();
        AppPaths.OverrideDataDir(null);
        try { Directory.Delete(Path, true); } catch { }
    }
}

/// <summary>
/// Страховка: ещё до первого теста все корни IntegrationEnvironment указывают во временную папку — даже тест
/// без <see cref="TempHome"/> не доберётся до настоящего глобального конфига OpenCode.
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-catchall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(IntegrationEnvironment.UserProfileVar, root);
        Environment.SetEnvironmentVariable(IntegrationEnvironment.AppDataVar, System.IO.Path.Combine(root, "AppData", "Roaming"));
        Environment.SetEnvironmentVariable(IntegrationEnvironment.LocalAppDataVar, System.IO.Path.Combine(root, "AppData", "Local"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, true); } catch { }
        };
    }
}

[CollectionDefinition("AppPaths", DisableParallelization = true)]
public class AppPathsCollection;
