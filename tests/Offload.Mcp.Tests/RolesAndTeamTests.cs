using System.Text.Json;
using Offload.Core.Roles;
using Offload.Mcp.Http;
using Offload.Mcp.Infrastructure;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Tests;

/// <summary>Роли локальной модели: параметр role, local_roles, local_team (параллельно и цепочкой), защита записи по HTTP.</summary>
[Collection("AppPaths")]
public sealed class RolesAndTeamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Calc = "public class Calc\n{\n    public int Add(int a, int b) => a + b;\n}\n";

    private static (FakeLlamaServer Llama, TestEnv Env) Setup(Func<JsonElement, string>? responder = null)
    {
        var llama = new FakeLlamaServer { Responder = responder ?? (_ => "Answer from the model.") };
        var env = new TestEnv(llama.Port);
        env.WriteFile("src/Calc.cs", Calc);
        return (llama, env);
    }

    [Fact]
    public async Task AskFiles_WithRole_PutsRolePromptIntoSystemMessage()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var ctx = env.Context(new SessionState(), Ct);

        await AskFilesTool.RunAsync(ctx, ["src"], "Review it", "brief", 0, fresh: true, ToolHelpers.ResolveRole(ctx, "reviewer"));

        var system = FakeLlamaServer.SystemText(JsonDocument.Parse(llama.Requests.Single()).RootElement);
        Assert.Contains("Your role: reviewer", system);
        Assert.Contains("Review the provided code or diff as a senior reviewer", system);
        Assert.Contains("Execute the delegated sub-task", system); // родитель engineer идёт первым
    }

    [Fact]
    public async Task AskFiles_CacheSeparatesRoles()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var state = new SessionState();

        await AskFilesTool.RunAsync(env.Context(state, Ct), ["src"], "Check it", "brief", 0);
        var ctx = env.Context(state, Ct);
        await AskFilesTool.RunAsync(ctx, ["src"], "Check it", "brief", 0, fresh: false, ToolHelpers.ResolveRole(ctx, "tester"));
        Assert.Equal(2, llama.Requests.Count); // та же задача, но другая роль — не из кэша

        var ctx2 = env.Context(state, Ct);
        await AskFilesTool.RunAsync(ctx2, ["src"], "Check it", "brief", 0, fresh: false, ToolHelpers.ResolveRole(ctx2, "tester"));
        Assert.Equal(2, llama.Requests.Count); // повтор с той же ролью — из кэша
    }

    [Fact]
    public void ResolveRole_UnknownRole_IsToolExceptionWithAvailableList()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var ex = Assert.Throws<ToolException>(() => ToolHelpers.ResolveRole(env.Context(new SessionState(), Ct), "no-such"));
        Assert.Contains("Unknown role", ex.Message);
        Assert.Contains("reviewer", ex.Message);
        Assert.Null(ToolHelpers.ResolveRole(env.Context(new SessionState(), Ct), "  "));
    }

    [Fact]
    public void RoleBrief_IsSeparateFromTaskText()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var ctx = env.Context(new SessionState(), Ct);
        var brief = ToolHelpers.RoleBrief(ctx, "migrator");
        Assert.StartsWith("ROLE: migrator", brief);
        Assert.True(brief!.Length <= ToolHelpers.RoleBriefMaxChars);
        Assert.Null(ToolHelpers.RoleBrief(ctx, null));
    }

    [Fact]
    public async Task Roles_DefineShowListDelete()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var ctx = env.Context(new SessionState(), Ct);

        var defined = await RolesTool.RunAsync(ctx, "define", "api-reviewer", "Reviews HTTP APIs", "reviewer", ["csharp-dotnet"], "Check status codes and idempotency.");
        Assert.Contains("api-reviewer", defined);
        Assert.True(File.Exists(Path.Combine(env.Workspace, ".offload", "roles", "api-reviewer.md")));

        var list = await RolesTool.RunAsync(ctx, "list", null, null, null, null, null);
        Assert.Contains("api-reviewer [project] extends reviewer", list);
        Assert.Contains("Presets available", list);

        var shown = await RolesTool.RunAsync(ctx, "show", "api-reviewer", null, null, null, null);
        Assert.Contains("chain: engineer -> reviewer -> api-reviewer", shown);
        Assert.Contains("Check status codes and idempotency.", shown);

        var deleted = await RolesTool.RunAsync(ctx, "delete", "api-reviewer", null, null, null, null);
        Assert.Contains("deleted", deleted);
        Assert.False(File.Exists(Path.Combine(env.Workspace, ".offload", "roles", "api-reviewer.md")));
    }

    [Fact]
    public async Task Roles_BadInput_IsToolException()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var ctx = env.Context(new SessionState(), Ct);
        await Assert.ThrowsAsync<ToolException>(() => RolesTool.RunAsync(ctx, "define", "../evil", "", null, null, "x"));
        await Assert.ThrowsAsync<ToolException>(() => RolesTool.RunAsync(ctx, "define", "fine-name", "", "no-parent", null, "x"));
        await Assert.ThrowsAsync<ToolException>(() => RolesTool.RunAsync(ctx, "explode", null, null, null, null, null));
        Assert.False(Directory.Exists(Path.Combine(env.Workspace, ".offload")), "Ничего не должно быть записано.");
    }

    [Fact]
    public async Task Roles_DefineExistingNeedsOverwrite_BuiltinNamesRefused()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var ctx = env.Context(new SessionState(), Ct);

        await RolesTool.RunAsync(ctx, "define", "my-role", "", "engineer", null, "v1");
        var again = await Assert.ThrowsAsync<ToolException>(() => RolesTool.RunAsync(ctx, "define", "my-role", "", "engineer", null, "v2"));
        Assert.Contains("overwrite", again.Message);
        await RolesTool.RunAsync(ctx, "define", "my-role", "", "engineer", null, "v2", overwrite: true);
        await Assert.ThrowsAsync<ToolException>(() => RolesTool.RunAsync(ctx, "define", "reviewer", "", null, null, "evil"));
    }

    [Fact]
    public async Task Team_SynthesisIgnoresRepositoryRoleFiles()
    {
        var (llama, env) = Setup(req => FakeLlamaServer.SystemText(req).Contains("lead of a small review team", StringComparison.Ordinal) ? "merged" : "report");
        using var _ = llama;
        using var __ = env;
        env.WriteFile(".offload/roles/engineer.md", "---\nname: engineer\n---\nREPO-INJECTED-INSTRUCTION");
        env.WriteFile(".offload/roles/repo-role.md", "---\nname: repo-role\nextends: engineer\n---\nRepo role body.");

        await TeamTool.RunAsync(env.Context(new SessionState(), Ct), ["src"], "Assess", ["reviewer", "repo-role"], "parallel", true, 300);

        Assert.DoesNotContain(llama.Requests, r => FakeLlamaServer.SystemText(JsonDocument.Parse(r).RootElement).Contains("REPO-INJECTED-INSTRUCTION"));
    }

    [Fact]
    public async Task Team_Parallel_EachRoleAnswersAndLeadSynthesizes()
    {
        var (llama, env) = Setup(req =>
        {
            var system = FakeLlamaServer.SystemText(req);
            if (system.Contains("lead of a small review team", StringComparison.Ordinal)) return "Agreed: both roles flag Add.";
            return system.Contains("Your role: tester", StringComparison.Ordinal) ? "Tester: add a test for overflow." : "Reviewer: no significant issues found.";
        });
        using var _ = llama;
        using var __ = env;

        var text = await TeamTool.RunAsync(env.Context(new SessionState(), Ct), ["src"], "Assess Calc.Add", ["reviewer", "tester=overflow cases"], "parallel", true, 400);

        Assert.Contains("Team report · 2 roles · parallel", text);
        Assert.Contains("## reviewer (engineer -> reviewer)", text);
        Assert.Contains("Reviewer: no significant issues found.", text);
        Assert.Contains("## tester", text);
        Assert.Contains("Tester: add a test for overflow.", text);
        Assert.Contains("## Lead synthesis", text);
        Assert.Contains("Agreed: both roles flag Add.", text);
        Assert.Single(text.Split("coverage:").Skip(1)); // охват файлов — один раз в конце
        Assert.Equal(3, llama.Requests.Count);
        Assert.Contains(llama.Requests, r => FakeLlamaServer.UserText(JsonDocument.Parse(r).RootElement).Contains("Your focus as tester: overflow cases"));
    }

    [Fact]
    public async Task Team_Pipeline_LaterRoleSeesEarlierReport()
    {
        var (llama, env) = Setup(req => FakeLlamaServer.SystemText(req).Contains("Your role: reviewer", StringComparison.Ordinal)
            ? "FINDING-ALPHA at src/Calc.cs:3"
            : "Tester saw the report.");
        using var _ = llama;
        using var __ = env;

        var text = await TeamTool.RunAsync(env.Context(new SessionState(), Ct), ["src"], "Assess Calc.Add", ["reviewer", "tester"], "pipeline", false, 400);

        Assert.DoesNotContain("Lead synthesis", text);
        var testerRequest = llama.Requests.Last(r => FakeLlamaServer.SystemText(JsonDocument.Parse(r).RootElement).Contains("Your role: tester"));
        var user = FakeLlamaServer.UserText(JsonDocument.Parse(testerRequest).RootElement);
        Assert.Contains("Reports of previous roles", user);
        Assert.Contains("FINDING-ALPHA", user);
    }

    [Fact]
    public async Task Team_AllRolesFailing_IsAnErrorNotASuccess()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        // Путей нет ни в одном файле проекта: каждая роль получает ошибку чтения.
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            TeamTool.RunAsync(env.Context(new SessionState(), Ct), ["no-such-folder"], "Assess", ["reviewer", "tester"], "parallel", true, 300));
        Assert.Contains("Every role failed", ex.Message);
        Assert.Empty(llama.Requests); // сводка по сообщениям об ошибках не запрашивается
    }

    [Fact]
    public async Task Team_ValidatesArguments()
    {
        var (llama, env) = Setup();
        using var _ = llama;
        using var __ = env;
        var ctx = env.Context(new SessionState(), Ct);
        await Assert.ThrowsAsync<ToolException>(() => TeamTool.RunAsync(ctx, ["src"], "x", [], "parallel", true, 400));
        await Assert.ThrowsAsync<ToolException>(() => TeamTool.RunAsync(ctx, ["src"], "x", ["a1", "a2", "a3", "a4", "a5", "a6", "a7"], "parallel", true, 400));
        await Assert.ThrowsAsync<ToolException>(() => TeamTool.RunAsync(ctx, ["src"], "x", ["reviewer"], "sideways", true, 400));
        await Assert.ThrowsAsync<ToolException>(() => TeamTool.RunAsync(ctx, ["src"], "x", ["no-such-role"], "parallel", true, 400));
        Assert.Empty(llama.Requests);
    }

    [Fact]
    public void SplitCoverage_SeparatesTrailingCoverageLine()
    {
        var (body, cov) = TeamTool.SplitCoverage("Answer line.\n\ncoverage: full (2 files)");
        Assert.Equal("Answer line.", body);
        Assert.Equal("coverage: full (2 files)", cov);
        Assert.Equal(("No coverage here.\n\nSecond paragraph.", ""), TeamTool.SplitCoverage("No coverage here.\n\nSecond paragraph."));
    }

    [Fact]
    public void HttpPolicy_BlocksRoleWritesWithoutAllowExec()
    {
        static Dictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.NotNull(HttpExecPolicy.Refusal(Offload.Core.McpToolNames.Roles, Args("{\"action\":\"define\"}")));
        Assert.NotNull(HttpExecPolicy.Refusal(Offload.Core.McpToolNames.Roles, Args("{\"action\":\"delete\"}")));
        Assert.Null(HttpExecPolicy.Refusal(Offload.Core.McpToolNames.Roles, Args("{\"action\":\"list\"}")));
        Assert.Null(HttpExecPolicy.Refusal(Offload.Core.McpToolNames.Team, Args("{}")));
    }
}
