using Offload.App.Services;

namespace Offload.App.Tests;

public class PresetsTests
{
    private static List<Presets.Preset> Real() => Presets.All().Where(p => p.ResourceName is not null).ToList();

    [Fact]
    public void All_ContainsLanguagePresets()
    {
        var ids = Real().Select(p => p.ResourceName![("Offload.Core.Presets.".Length)..^3]).ToHashSet();
        foreach (var id in new[] { "delphi-vcl", "csharp-dotnet", "python", "typescript-node", "react-frontend", "go", "rust", "java-spring",
                     "kotlin-android", "cpp-modern", "c-embedded", "php-laravel", "swift-ios", "ruby-rails", "sql-postgres", "shell-powershell",
                     "unity-csharp", "godot-gdscript" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void EveryPreset_LoadsWithinBudgetAndHasTitle()
    {
        var all = Real();
        foreach (var p in all)
        {
            var id = p.ResourceName![("Offload.Core.Presets.".Length)..^3];
            var text = Presets.Load(p);
            Assert.False(string.IsNullOrWhiteSpace(text), $"Пресет {id} пуст.");
            // Пресет попадает в системный промпт каждого вызова: длинный съедает контекст модели (Delphi — исторический, длиннее).
            if (id != "delphi-vcl") Assert.True(text!.Length <= 1700, $"Пресет {id} слишком длинный: {text.Length}.");
            Assert.NotEqual(id, p.Title);
        }
        Assert.Equal(all.Count, all.Select(p => p.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
