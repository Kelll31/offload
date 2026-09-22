using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Tests;

public class TomlPatcherTests
{
    internal const string CodexSample =
        "# Codex config\n" +
        "model = \"gpt-5.5\"\n" +
        "approval_policy = \"on-request\"   # ask\n" +
        "\n" +
        "[mcp_servers.other]\n" +
        "command = \"npx\"\n" +
        "args = [\n  \"-y\", # package\n  \"@other/server\",\n]\n" +
        "\n" +
        "# comment about profiles\n" +
        "[profiles.fast]\n" +
        "model = 'gpt-5-mini'\n" +
        "\n" +
        "[[skills]]\n" +
        "name = \"a\"\n" +
        "text = \"\"\"\nmulti\n[mcp_servers.offload]\ncommand = 'fake'\n\"\"\"\n" +
        "lit = '''\n[not.a.table]\n'''\n";

    private static McpServerSpec Spec(string cmd) => new("offload", cmd, ["--mcp"], new Dictionary<string, string>());

    [Fact]
    public void Register_Unregister_RoundTripsByteForByte()
    {
        var spec = Spec(@"C:\Users\Иван\AppData\Local\Programs\Offload\Offload.exe");
        var registered = CodexIntegration.BuildRegistered(CodexSample, spec)!;
        Assert.Contains("[mcp_servers.offload]\ncommand = 'C:\\Users\\Иван\\AppData\\Local\\Programs\\Offload\\Offload.exe'\nargs = [\"--mcp\"]\n", registered);
        Assert.Contains("tool_timeout_sec = 1800\n", registered);
        Assert.Contains("startup_timeout_sec = 30\n", registered);
        Assert.Contains("enabled = true\n", registered);
        // Вставлено рядом с другими mcp_servers, до комментария следующей таблицы.
        Assert.True(registered.IndexOf("[mcp_servers.offload]\ncommand = 'C:", StringComparison.Ordinal) < registered.IndexOf("# comment about profiles", StringComparison.Ordinal));

        var p = new TomlTablePatcher(TomlDocument.Parse(registered), ["mcp_servers", "offload"]);
        Assert.Null(p.Unsupported);
        Assert.Equal(spec.Command, p.Document.ReadString(p.MainValue("command")!));

        // Повторная регистрация — без изменений.
        Assert.Null(CodexIntegration.BuildRegistered(registered, spec));

        var removed = CodexIntegration.BuildUnregistered(registered, "offload");
        Assert.Equal(CodexSample, removed);
    }

    [Fact]
    public void MultilineStringsWithHeaders_AreNotTables()
    {
        var p = new TomlTablePatcher(TomlDocument.Parse(CodexSample), ["mcp_servers", "offload"]);
        Assert.Null(p.Main);
        Assert.Empty(p.Ranges);
    }

    [Fact]
    public void Update_KeepsUserKeysAndSubtables_ReplacesOwned()
    {
        const string text =
            "[mcp_servers.offload]\ncommand = \"C:\\\\old\\\\Offload.exe\"\nargs = [\"--mcp\"]\nenabled = false\ntool_timeout_sec = 99\n\n" +
            "[mcp_servers.offload.tools.local_edit_files]\napproval_mode = \"approve\"\n\n[other]\nx = 1\n";
        var updated = CodexIntegration.BuildRegistered(text, Spec(@"D:\new\Offload.exe"))!;
        Assert.Contains("command = 'D:\\new\\Offload.exe'", updated);
        Assert.Contains("enabled = false", updated);
        Assert.Contains("tool_timeout_sec = 99", updated);
        Assert.DoesNotContain("tool_timeout_sec = 1800", updated);
        Assert.Contains("startup_timeout_sec = 30", updated);
        Assert.Contains("[mcp_servers.offload.tools.local_edit_files]\napproval_mode = \"approve\"", updated);
        Assert.EndsWith("[other]\nx = 1\n", updated);

        var removed = CodexIntegration.BuildUnregistered(updated, "offload")!;
        Assert.DoesNotContain("offload", removed);
        Assert.Equal("[other]\nx = 1\n", removed);
    }

    [Fact]
    public void QuotedHeaderForms_AreRecognized()
    {
        const string text = "[ mcp_servers . \"offload\" ]\ncommand = 'C:\\x\\Offload.exe'\nargs = ['--mcp']\n";
        var p = new TomlTablePatcher(TomlDocument.Parse(text), ["mcp_servers", "offload"]);
        Assert.NotNull(p.Main);
        Assert.Equal(["--mcp"], p.Document.ReadStringArray(p.MainValue("args")!)!);
        Assert.Equal("", CodexIntegration.BuildUnregistered(text, "offload"));
    }

    [Theory]
    [InlineData("mcp_servers = { other = { command = \"x\" } }\n")]
    [InlineData("[mcp_servers]\noffload = { command = 'C:\\x\\Offload.exe' }\n")]
    [InlineData("mcp_servers.offload.command = 'C:\\x\\Offload.exe'\n")]
    [InlineData("[[mcp_servers.offload]]\ncommand = 'C:\\x\\Offload.exe'\n")]
    [InlineData("[[mcp_servers]]\nname = 'x'\n")]
    public void InlineOrDottedDefinitions_AreUnsupported(string text)
    {
        var p = new TomlTablePatcher(TomlDocument.Parse(text), ["mcp_servers", "offload"]);
        Assert.NotNull(p.Unsupported);
        Assert.Throws<TomlPatchException>(() => CodexIntegration.BuildRegistered(text, Spec(@"C:\y\Offload.exe")));
    }

    [Theory]
    [InlineData("a = \n")]
    [InlineData("[unclosed\n")]
    [InlineData("s = \"not closed\n")]
    [InlineData("m = \"\"\"never closed\n")]
    [InlineData("arr = [1, 2\n")]
    public void Malformed_Throws(string text) => Assert.Throws<TomlPatchException>(() => TomlDocument.Parse(text));

    [Theory]
    [InlineData(@"C:\it's\Offload.exe")]
    [InlineData("C:\\путь с пробелами\\Offload.exe")]
    public void PathsNeedingEscapes_RoundTrip(string cmd)
    {
        var text = CodexIntegration.BuildRegistered("", Spec(cmd))!;
        var p = new TomlTablePatcher(TomlDocument.Parse(text), ["mcp_servers", "offload"]);
        Assert.Equal(cmd, p.Document.ReadString(p.MainValue("command")!));
    }

    [Fact]
    public void Crlf_AndNoTrailingNewline()
    {
        const string text = "model = \"x\"\r\n[profiles.a]\r\nk = 1";
        var registered = CodexIntegration.BuildRegistered(text, Spec(@"C:\a\Offload.exe"))!;
        Assert.Contains("\r\n[mcp_servers.offload]\r\ncommand", registered);
        Assert.StartsWith(text, registered);
        var removed = CodexIntegration.BuildUnregistered(registered, "offload")!;
        Assert.Equal(text + "\r\n", removed);
    }
}
