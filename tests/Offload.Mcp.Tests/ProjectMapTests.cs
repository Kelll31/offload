using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Карта проекта (ProjectMap) и инструмент local_project_map по секциям.</summary>
[Collection("AppPaths")]
public sealed class ProjectMapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string N(string s) => s.ReplaceLineEndings("\n");

    private static void Seed(TestEnv env)
    {
        env.WriteFile("App.slnx", N("""
            <Solution>
              <Project Path="src/App/App.csproj" />
              <Project Path="src/Lib/Lib.csproj" />
              <Project Path="tests/App.Tests/App.Tests.csproj" />
            </Solution>
            """));
        env.WriteFile("src/App/App.csproj", N("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """));
        env.WriteFile("src/Lib/Lib.csproj", N("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """));
        env.WriteFile("tests/App.Tests/App.Tests.csproj", N("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="3.0.0" />
                <ProjectReference Include="..\..\src\App\App.csproj" />
              </ItemGroup>
            </Project>
            """));
        env.WriteFile("src/App/Program.cs", N("""
            namespace App;

            internal static class Program
            {
                private static void Main(string[] args)
                {
                    System.Console.WriteLine(args.Length);
                }
            }
            """));
        env.WriteFile("src/App/Controllers/OrdersController.cs", N("""
            namespace App.Controllers;

            public sealed class OrdersController
            {
                [HttpGet("orders/{id}")]
                public string GetOrder(int id)
                {
                    return "order " + id;
                }
            }
            """));
        env.WriteFile("src/App/Settings.cs", N("""
            namespace App;

            public sealed class Settings(IConfiguration Configuration)
            {
                public string ApiUrl => Environment.GetEnvironmentVariable("API_URL") ?? "";

                public string JwtKey => Configuration["Jwt:Key"] ?? "";
            }
            """));
        env.WriteFile("src/Lib/Calc.cs", N("""
            namespace Lib;

            public static class Calc
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """));
        env.WriteFile("tests/App.Tests/CalcTests.cs", N("""
            namespace App.Tests;

            public sealed class CalcTests
            {
                public void Add_Works()
                {
                    var x = Lib.Calc.Add(1, 2);
                }
            }
            """));
        env.WriteFile("package.json", N("""
            {
              "name": "web",
              "scripts": { "test": "jest", "build": "tsc", "lint": "eslint ." },
              "devDependencies": { "jest": "^29.0.0" }
            }
            """));
        env.WriteFile("CLAUDE.md", "# Rules\nAlways run the unit tests before committing.\n");
    }

    [Fact]
    public async Task BuildAsync_DetectsProjectsPackagesEntryPointsAndCommands()
    {
        using var env = new TestEnv();
        Seed(env);

        var (map, index) = await ProjectMap.BuildAsync(env.Context(ct: Ct), Ct);

        var app = Assert.Single(map.Projects, p => p.Manifest == "src/App/App.csproj");
        Assert.Equal("dotnet", app.Kind);
        Assert.Equal("App", app.Name);
        Assert.Equal("Exe", app.OutputType);
        Assert.Equal("net10.0", app.Framework);
        Assert.False(app.IsTest);
        Assert.Contains(app.Packages, p => p.Name == "Newtonsoft.Json" && p.Version == "13.0.3" && !p.Dev);
        Assert.Equal(new[] { "Lib" }, app.ProjectRefs);
        Assert.Equal("src/App", app.Dir);

        var lib = Assert.Single(map.Projects, p => p.Manifest == "src/Lib/Lib.csproj");
        Assert.Equal("dotnet", lib.Kind);
        Assert.False(lib.IsTest);

        var tests = Assert.Single(map.Projects, p => p.Manifest == "tests/App.Tests/App.Tests.csproj");
        Assert.True(tests.IsTest, "проект с xunit.v3 — тестовый");
        Assert.Equal("xunit", tests.TestFramework);
        Assert.Equal(new[] { "App" }, tests.ProjectRefs);

        var node = Assert.Single(map.Projects, p => p.Kind == "node");
        Assert.Equal("web", node.Name);
        Assert.Equal("jest", node.TestFramework);
        Assert.Contains(node.Packages, p => p.Name == "jest" && p.Dev);
        Assert.Equal(new[] { "build", "lint", "test" }, node.Scripts.Keys.Order(StringComparer.Ordinal));

        Assert.Contains(map.EntryPoints, e => e.Path == "src/App/Program.cs" && e.Why == "Main");
        Assert.DoesNotContain(map.EntryPoints, e => e.Path.StartsWith("tests/", StringComparison.Ordinal));

        var build = Assert.Single(map.Commands, c => c.Kind == "build" && c.Command == "dotnet build");
        Assert.True(build.Allowed, "dotnet build разрешён белым списком по умолчанию");
        Assert.Contains(map.Commands, c => c.Kind == "test" && c.Command == "dotnet test" && c.Allowed);
        Assert.Contains(map.Commands, c => c.Kind == "test" && c.Command == "npm test" && c.Allowed);
        Assert.Contains(map.Commands, c => c.Kind == "build" && c.Command == "npm run build");
        Assert.Contains(map.Commands, c => c.Kind == "lint" && c.Command == "npm run lint");
        Assert.Equal("dotnet build", map.Pick("build")!.Command);

        Assert.Contains("CLAUDE.md", map.RuleFiles);
        Assert.Contains(map.Languages, l => l.Lang == "CSharp");
        Assert.Contains(index.Files, f => f.Display == "src/App/Program.cs");
        Assert.True(map.TotalFiles >= 11, $"файлов: {map.TotalFiles}");
    }

    [Fact]
    public async Task OwnerOf_ReturnsDeepestManifest()
    {
        using var env = new TestEnv();
        Seed(env);
        var (map, _) = await ProjectMap.BuildAsync(env.Context(ct: Ct), Ct);

        Assert.Equal("src/App/App.csproj", ProjectMap.OwnerOf(map, "src/App/Controllers/OrdersController.cs")?.Manifest);
        Assert.Equal("src/Lib/Lib.csproj", ProjectMap.OwnerOf(map, "src\\Lib\\Calc.cs")?.Manifest);
        Assert.Equal("tests/App.Tests/App.Tests.csproj", ProjectMap.OwnerOf(map, "tests/App.Tests/CalcTests.cs")?.Manifest);
        // Корневой package.json владеет всем, что не попало в более глубокий проект.
        Assert.Equal("package.json", ProjectMap.OwnerOf(map, "docs/readme.md")?.Manifest);
    }

    [Fact]
    public async Task OwnerOf_NoRootManifest_ReturnsNullOutsideProjects()
    {
        using var env = new TestEnv();
        env.WriteFile("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        var (map, _) = await ProjectMap.BuildAsync(env.Context(ct: Ct), Ct);

        Assert.Null(ProjectMap.OwnerOf(map, "other/File.cs"));
        // Префикс имени папки не считается вложенностью.
        Assert.Null(ProjectMap.OwnerOf(map, "src/LibExtra/File.cs"));
        Assert.Equal("src/Lib/Lib.csproj", ProjectMap.OwnerOf(map, "src/Lib/File.cs")?.Manifest);
    }

    [Fact]
    public async Task RunAsync_Overview_ListsProjectsAndCommands()
    {
        using var env = new TestEnv();
        Seed(env);

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), null, 0);

        Assert.Contains("projects:", text);
        Assert.Contains("src/App/App.csproj [dotnet net10.0, Exe] → Lib", text);
        Assert.Contains("tests/App.Tests/App.Tests.csproj", text);
        Assert.Contains("packages: Newtonsoft.Json", text);
        Assert.Contains("build: dotnet build ✓", text);
        Assert.Contains("test: npm test ✓", text);
        Assert.Contains("src/App/Program.cs (Main)", text);
        Assert.Contains("CLAUDE.md", text);
    }

    [Fact]
    public async Task RunAsync_Routes_FindsAttributeRouteWithHandler()
    {
        using var env = new TestEnv();
        Seed(env);

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), "routes", 0);

        Assert.Contains("GET orders/{id}", text);
        Assert.Contains("src/App/Controllers/OrdersController.cs:5", text);
        Assert.Contains("→ OrdersController.GetOrder", text);
    }

    [Fact]
    public async Task RunAsync_Env_FindsEnvironmentVariable()
    {
        using var env = new TestEnv();
        Seed(env);

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), "env", 0);

        Assert.Contains("API_URL", text);
        Assert.Contains("src/App/Settings.cs:5", text);
    }

    [Fact]
    public async Task RunAsync_Config_FindsConfigurationKey()
    {
        using var env = new TestEnv();
        Seed(env);

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), "config", 0);

        Assert.Contains("keys read in code:", text);
        Assert.Contains("Jwt:Key", text);
        Assert.Contains("src/App/Settings.cs:7", text);
    }

    [Fact]
    public async Task RunAsync_Config_ListsSettingsFileKeysWithoutValues()
    {
        using var env = new TestEnv();
        Seed(env);
        env.WriteFile("src/App/appsettings.json", "{ \"Logging\": { \"Level\": \"Debug\" }, \"Db\": \"Server=db;Pwd=topsecretvalue\" }\n");

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), "config", 0);

        Assert.Contains("src/App/appsettings.json: Logging:Level, Db", text);
        Assert.DoesNotContain("topsecretvalue", text);
    }

    [Fact]
    public async Task RunAsync_Rules_IncludesClaudeMdText()
    {
        using var env = new TestEnv();
        Seed(env);

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), "rules", 0);

        Assert.Contains("=== CLAUDE.md ===", text);
        Assert.Contains("Always run the unit tests before committing.", text);
    }

    [Fact]
    public async Task RunAsync_Conventions_ReportsIndentation()
    {
        using var env = new TestEnv();
        Seed(env);

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), "Conventions", 0);

        Assert.Contains("CSharp", text);
        Assert.Contains("indentation:", text);
        Assert.Contains("namespaces: file-scoped", text);
        Assert.Contains("braces: Allman (own line)", text);
    }

    [Fact]
    public async Task RunAsync_Conventions_FourSpaceCode_ReportsFourSpaces()
    {
        // Все отступы кратны 4 (4/8/12): единица отступа — 4, а не 2 (2 делит те же отступы, но не является шагом).
        using var env = new TestEnv();
        Seed(env);

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), "conventions", 0);

        Assert.Contains("indentation: 4 spaces", text);
    }

    [Fact]
    public async Task RunAsync_Conventions_TwoSpaceCode_ReportsTwoSpaces()
    {
        using var env = new TestEnv();
        env.WriteFile("src/A.cs", "namespace A;\n\npublic class A\n{\n  public int X()\n  {\n    return 1;\n  }\n}\n");

        var text = await ProjectMapTool.RunAsync(env.Context(ct: Ct), "conventions", 0);

        Assert.Contains("indentation: 2 spaces", text);
    }

    [Fact]
    public async Task RunAsync_InvalidSection_Throws()
    {
        using var env = new TestEnv();

        var ex = await Assert.ThrowsAsync<ToolException>(() => ProjectMapTool.RunAsync(env.Context(ct: Ct), "bogus", 0));
        Assert.Contains("section must be one of", ex.Message);
    }

    [Fact]
    public async Task RunAsync_EmptyWorkspace_ReportsNothingFound()
    {
        using var env = new TestEnv();

        Assert.Equal("No HTTP routes found.", await ProjectMapTool.RunAsync(env.Context(ct: Ct), "routes", 0));
        Assert.StartsWith("No repository rule files", await ProjectMapTool.RunAsync(env.Context(ct: Ct), "rules", 0));
        Assert.Equal("No environment variables found.", await ProjectMapTool.RunAsync(env.Context(ct: Ct), "env", 0));
    }
}
