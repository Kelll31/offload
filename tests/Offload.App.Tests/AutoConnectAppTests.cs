using Offload.App.Forms.Wizard;
using Offload.App.Services;
using Offload.Core.Config;

namespace Offload.App.Tests;

public class AutoConnectAppTests
{
    [Fact]
    public void RestartText_NamesClaudeActions_AndOtherIdes()
    {
        Assert.Equal("", DoneStep.RestartText([]));

        var text = DoneStep.RestartText(["claude-code", "claude-desktop", "cursor"]);

        Assert.Contains("/mcp", text, StringComparison.Ordinal);
        Assert.Contains("Claude Desktop", text, StringComparison.Ordinal);
        Assert.Contains("Cursor", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeLinkText_FollowsStoredChecks()
    {
        var cfg = new AppConfig();
        Assert.Null(Texts.ClaudeLink(cfg));

        cfg.Integrations.Add("claude-code");
        Assert.Contains("подключён", Texts.ClaudeLink(cfg), StringComparison.Ordinal);

        cfg.RecordCheck("claude-code", ok: false, "не запустился", DateTime.UtcNow);
        Assert.Contains("внимания", Texts.ClaudeLink(cfg), StringComparison.Ordinal);
    }
}
