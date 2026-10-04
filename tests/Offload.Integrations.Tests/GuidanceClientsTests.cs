using Offload.Integrations;

namespace Offload.Integrations.Tests;

/// <summary>Инструкции по делегированию в глобальных файлах правил: OpenCode, Windsurf, Cline, Roo Code (+ прежние Codex и Gemini).</summary>
public sealed class GuidanceClientsTests
{
    [Fact]
    public void Registry_HasAllClientsAndNoCursor()
    {
        var ids = ClientGuidance.All.Select(g => g.Id).ToList();
        Assert.Equal(["codex", "gemini-cli", "opencode", "windsurf", "cline", "roo-code"], ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain("cursor", ids); // глобальных правил Cursor в файле нет
    }

    [Theory]
    [InlineData("opencode", new[] { ".config", "opencode" }, new[] { ".config", "opencode", "AGENTS.md" })]
    [InlineData("windsurf", new[] { ".codeium", "windsurf" }, new[] { ".codeium", "windsurf", "memories", "global_rules.md" })]
    [InlineData("cline", new[] { "Documents", "Cline" }, new[] { "Documents", "Cline", "Rules", "offload.md" })]
    [InlineData("roo-code", new[] { ".roo" }, new[] { ".roo", "rules", "offload.md" })]
    public void InstallRemove_UsesExpectedPath_AndKeepsUserText(string id, string[] clientDir, string[] file)
    {
        using var sb = new Sandbox();
        var g = ClientGuidance.Find(id)!;

        // Клиента нет — ничего не создаём.
        Assert.False(g.Install().Ok);
        Assert.False(File.Exists(sb.P(file)));

        sb.Dir(clientDir);
        Assert.Equal(sb.P(file), g.FilePath);
        var installed = g.Install();
        Assert.True(installed.Ok, installed.Message);
        Assert.True(File.Exists(sb.P(file)));
        Assert.True(g.IsInstalled());
        Assert.Equal(ManagedState.Current, g.State());

        // Текст пользователя в файле сохраняется при снятии секции.
        const string user = "# Mine\nKeep tabs.\n";
        File.WriteAllText(sb.P(file), user + File.ReadAllText(sb.P(file)));
        Assert.True(g.Remove().Ok);
        Assert.Equal(user, File.ReadAllText(sb.P(file)));
    }

    [Theory]
    [InlineData("opencode", new[] { ".config", "opencode" })]
    [InlineData("windsurf", new[] { ".codeium", "windsurf" })]
    [InlineData("cline", new[] { "Documents", "Cline" })]
    [InlineData("roo-code", new[] { ".roo" })]
    public void SectionOnlyFile_IsDeletedOnRemove(string id, string[] clientDir)
    {
        using var sb = new Sandbox();
        var g = ClientGuidance.Find(id)!;
        sb.Dir(clientDir);
        Assert.True(g.Install().Ok);
        Assert.True(File.Exists(g.FilePath));
        Assert.True(g.Remove().Ok);
        Assert.False(File.Exists(g.FilePath));
    }

    [Fact]
    public void Windsurf_SectionFitsTheClientLimit()
    {
        // Windsurf читает не больше 6000 символов global_rules.md: секция Offload должна оставлять место правилам пользователя.
        Assert.True(GuidanceSection.Template().Length < 4000, GuidanceSection.Template().Length.ToString());
    }
}
