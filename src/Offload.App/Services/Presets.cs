using Offload.Core;

namespace Offload.App.Services;

/// <summary>Пресеты системного промпта — встроенные ресурсы сборки Offload.Core (Offload.Core.Presets.*.md).</summary>
internal static class Presets
{
    private const string Prefix = "Offload.Core.Presets.";

    public sealed record Preset(string Title, string? ResourceName)
    {
        public override string ToString() => Title;
    }

    private static readonly Dictionary<string, string> KnownTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["delphi-vcl"] = "Delphi VCL",
        ["csharp-dotnet"] = "C# / .NET",
        ["python"] = "Python",
        ["typescript-node"] = "TypeScript / Node.js",
        ["react-frontend"] = "React / frontend",
        ["go"] = "Go",
        ["rust"] = "Rust",
        ["java-spring"] = "Java / Spring",
        ["kotlin-android"] = "Kotlin / Android",
        ["cpp-modern"] = "C++ (modern)",
        ["c-embedded"] = "C (embedded)",
        ["php-laravel"] = "PHP / Laravel",
        ["swift-ios"] = "Swift / iOS",
        ["ruby-rails"] = "Ruby / Rails",
        ["sql-postgres"] = "SQL / PostgreSQL",
        ["shell-powershell"] = "Shell (PowerShell / bash)",
        ["unity-csharp"] = "Unity (C#)",
        ["godot-gdscript"] = "Godot 4 (GDScript)",
    };

    public static IReadOnlyList<Preset> All()
    {
        var list = new List<Preset> { new(L.T("Нет"), null) };
        try
        {
            foreach (var name in typeof(AppPaths).Assembly.GetManifestResourceNames()
                         .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                var id = name[Prefix.Length..^3];
                list.Add(new Preset(KnownTitles.TryGetValue(id, out var t) ? t : id, name));
            }
        }
        catch
        {
            // Нет ресурсов — только «Нет».
        }
        return list;
    }

    public static string? Load(Preset preset)
    {
        if (preset.ResourceName is null) return null;
        using var s = typeof(AppPaths).Assembly.GetManifestResourceStream(preset.ResourceName);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd().Replace("\r\n", "\n").Replace("\n", Environment.NewLine).Trim();
    }
}
