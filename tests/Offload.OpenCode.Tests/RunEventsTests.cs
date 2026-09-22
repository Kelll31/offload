using System.Text;

namespace Offload.OpenCode.Tests;

public class RunEventsTests
{
    private const string Wd = @"C:\proj";

    private static string StepStart() =>
        """{"type":"step_start","timestamp":1,"sessionID":"ses_1","part":{"id":"prt_1","type":"step-start"}}""";

    private static string Text(string text) =>
        "{\"type\":\"text\",\"timestamp\":2,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_2\",\"type\":\"text\",\"text\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + ",\"time\":{\"start\":1,\"end\":2}}}";

    private static string Tool(string tool, string inputJson, string status = "completed", string title = "") =>
        "{\"type\":\"tool_use\",\"timestamp\":3,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_3\",\"type\":\"tool\",\"callID\":\"c1\",\"tool\":\""
        + tool + "\",\"state\":{\"status\":\"" + status + "\",\"input\":" + inputJson + ",\"output\":\"ok\",\"title\":\"" + title + "\",\"metadata\":{},\"time\":{\"start\":1,\"end\":2}}}}";

    private static string StepFinish(string reason, int input, int output, int cacheRead = 0) =>
        "{\"type\":\"step_finish\",\"timestamp\":4,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_4\",\"type\":\"step-finish\",\"reason\":\""
        + reason + "\",\"cost\":0,\"tokens\":{\"input\":" + input + ",\"output\":" + output + ",\"reasoning\":0,\"cache\":{\"read\":" + cacheRead + ",\"write\":0}}}}";

    [Fact]
    public void FullRun_FinalTextToolsTokens()
    {
        var ev = new RunEvents(Wd);
        var progress = new List<string?>
        {
            ev.Feed(StepStart()),
            ev.Feed(Text("Сначала прочитаю файл.")),
            ev.Feed(Tool("read", """{"filePath":"C:\\proj\\README.md"}""", title: "README.md")),
            ev.Feed(StepFinish("tool-calls", 1000, 40, cacheRead: 200)),
            ev.Feed(StepStart()),
            ev.Feed(Tool("edit", """{"filePath":"C:\\proj\\src\\a.cs","oldString":"x","newString":"y"}""")),
            ev.Feed(StepFinish("tool-calls", 1300, 60)),
            ev.Feed(StepStart()),
            ev.Feed(Text("Result: done.")),
            ev.Feed(Text("Changed files:\nsrc/a.cs (modified)")),
            ev.Feed(StepFinish("stop", 1500, 30)),
        };

        Assert.Equal("Result: done.\n\nChanged files:\nsrc/a.cs (modified)", ev.FinalText);
        Assert.Equal(new[] { "read README.md", "edit src/a.cs" }, ev.ToolCalls);
        Assert.Equal(3, ev.Steps);
        Assert.Equal(1000 + 200 + 1300 + 1500, ev.PromptTokens);
        Assert.Equal(40 + 60 + 30, ev.CompletionTokens);
        Assert.Equal("stop", ev.LastFinishReason);
        Assert.Empty(ev.Errors);
        Assert.Contains("шаг 1: чтение: README.md", progress);
        Assert.Contains("шаг 2: правка: src/a.cs", progress);
        Assert.Contains("шаг 3: модель думает…", progress);
    }

    [Fact]
    public void FinalText_FallsBackToLastTextWhenRunEndsWithTool()
    {
        var ev = new RunEvents(Wd);
        ev.Feed(StepStart());
        ev.Feed(Text("Первый ответ"));
        ev.Feed(Text("Создаю файл"));
        ev.Feed(Tool("write", """{"filePath":"C:\\proj\\hello.txt","content":"hi"}"""));
        Assert.Equal("Создаю файл", ev.FinalText);
        Assert.Equal(new[] { "write hello.txt" }, ev.ToolCalls);
    }

    [Fact]
    public void ToolArguments_AndErrors()
    {
        var ev = new RunEvents(Wd);
        ev.Feed(StepStart());
        ev.Feed(Tool("bash", """{"command":"dotnet build\n--no-restore","description":"Build"}""", title: "Build"));
        ev.Feed(Tool("grep", """{"pattern":"TODO","path":"C:\\proj\\src"}"""));
        ev.Feed(Tool("glob", """{"pattern":"**/*.cs"}"""));
        ev.Feed(Tool("read", """{"filePath":"D:\\other\\x.txt"}""", status: "error"));
        ev.Feed(Tool("mystery", "{}", title: "что-то"));
        Assert.Equal(new[]
        {
            "bash dotnet build --no-restore",
            "grep TODO (src)",
            "glob **/*.cs",
            "read D:/other/x.txt (ошибка)",
            "mystery что-то",
        }, ev.ToolCalls);
    }

    [Fact]
    public void ErrorEvents_AreCollected()
    {
        var ev = new RunEvents(Wd);
        var msg = ev.Feed("""{"type":"error","timestamp":5,"sessionID":"ses_1","error":{"name":"APIError","data":{"message":"Connection refused: 127.0.0.1:8765"}}}""");
        ev.Feed("""{"type":"error","error":{"name":"UnknownError"}}""");
        ev.Feed("""{"type":"error","error":"plain text"}""");
        Assert.Equal("ошибка: Connection refused: 127.0.0.1:8765", msg);
        Assert.Equal(new[] { "Connection refused: 127.0.0.1:8765", "UnknownError", "plain text" }, ev.Errors);
    }

    [Fact]
    public void NonJsonAndBrokenLines_AreIgnored()
    {
        var ev = new RunEvents(Wd);
        Assert.Null(ev.Feed(""));
        Assert.Null(ev.Feed("INFO  2026-09-22 service=bus starting"));
        Assert.Null(ev.Feed("{\"type\":\"text\",\"part\":{\"text\":\"обры"));
        Assert.Null(ev.Feed("[1,2,3]"));
        Assert.Null(ev.Feed("""{"no_type":true}"""));
        Assert.Null(ev.Feed("""{"type":"reasoning","part":{"text":"hmm"}}"""));
        Assert.Equal(1, ev.Events); // только reasoning — событие, остальное мусор
        Assert.Equal("", ev.FinalText);
    }

    [Fact]
    public void TruncatedToolLine_IsSalvaged()
    {
        var ev = new RunEvents(Wd);
        var prefix = Tool("edit", """{"filePath":"C:\\proj\\big \"file\".cs","oldString":"aaaa""")[..220];
        var msg = ev.FeedTruncated(prefix);
        Assert.NotNull(msg);
        Assert.Single(ev.ToolCalls);
        Assert.StartsWith("edit big", ev.ToolCalls[0]);
        Assert.Null(ev.FeedTruncated("""{"type":"text","part":{"text":"aaaa"""));
    }

    [Fact]
    public void StripAnsi_RemovesEscapes()
    {
        Assert.Equal("Error: boom", RunEvents.StripAnsi("\u001b[91m\u001b[1mError: \u001b[0mboom"));
        Assert.Equal("title", RunEvents.StripAnsi("\u001b]0;x\u0007title"));
        Assert.Equal("plain", RunEvents.StripAnsi("plain"));
    }

    [Fact]
    public async Task PumpLines_HandlesCrLfLongLinesAndTail()
    {
        var huge = new string('x', 5000);
        var input = "first\r\n" + huge + "\nsecond\n\nlast-no-newline";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(input)), Encoding.UTF8);
        var lines = new List<(string Line, bool Truncated)>();
        await OpenCodeRunner.PumpLinesAsync(reader, 1000, (l, t) => lines.Add((l, t)));
        Assert.Equal(4, lines.Count);
        Assert.Equal(("first", false), lines[0]);
        Assert.True(lines[1].Truncated);
        Assert.Equal(1000, lines[1].Line.Length);
        Assert.Equal(("second", false), lines[2]);
        Assert.Equal(("last-no-newline", false), lines[3]);
    }

    [Fact]
    public void TailBuffer_KeepsLastLines()
    {
        var t = new TailBuffer(3);
        foreach (var s in new[] { "a", "", "\u001b[31mb\u001b[0m", "c", "d" }) t.Add(s);
        Assert.Equal(new[] { "b", "c", "d" }, t.Lines());
        Assert.Equal("c\nd", t.Text(2));
    }
}
