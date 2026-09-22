using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offload.Integrations.Editing;

namespace Offload.Integrations.Tests;

public class JsoncEditorTests
{
    private static readonly string[] ServerPath = ["servers", "offload"];

    private static JsonObject Entry(string cmd = @"C:\Program Files\Offload\Offload.exe") => new()
    {
        ["type"] = "stdio",
        ["command"] = cmd,
        ["args"] = new JsonArray("--mcp"),
    };

    private static JsonNode? Oracle(string text)
    {
        Assert.True(JsonTree.TryParseOracle(text, out var n), "результат не разбирается System.Text.Json:\n" + text);
        return n;
    }

    private static string SetAndCheck(string text, IReadOnlyList<string> path, JsonNode value)
    {
        var ed = JsoncEditor.Parse(text);
        Assert.True(ed.Set(path, value));
        Assert.False(ed.LastEditUsedFallback, "ожидалась точечная вставка:\n" + ed.Text);
        var got = JsonTree.Navigate(Oracle(ed.Text), path);
        Assert.True(JsonTree.SemanticEquals(value, got));
        return ed.Text;
    }

    [Fact]
    public void Insert_IntoObjectWithComments_PreservesEverythingElse()
    {
        const string text = "{\n\t// Серверы MCP\n\t\"servers\": {\n\t\t\"github\": {\n\t\t\t\"type\": \"http\",\n\t\t\t\"url\": \"https://x/\" // удалённый\n\t\t}\n\t},\n\t/* inputs */\n\t\"inputs\": []\n}\n";
        var result = SetAndCheck(text, ServerPath, Entry());
        Assert.Contains("// Серверы MCP", result);
        Assert.Contains("// удалённый", result);
        Assert.Contains("/* inputs */", result);
        // Отступ табами унаследован.
        Assert.Contains("\n\t\t\"offload\": {\n\t\t\t\"type\": \"stdio\",", result);
        // Удаление возвращает исходный текст байт в байт.
        var ed = JsoncEditor.Parse(result);
        Assert.True(ed.Remove(ServerPath));
        Assert.Equal(text, ed.Text);
    }

    [Fact]
    public void Insert_AfterMemberWithLineComment_KeepsCommentOnItsLine()
    {
        const string text = "{\n  \"servers\": {\n    \"a\": 1 // первый\n  }\n}";
        var result = SetAndCheck(text, ServerPath, Entry());
        Assert.Contains("\"a\": 1, // первый\n    \"offload\": {", result);
        var ed = JsoncEditor.Parse(result);
        ed.Remove(ServerPath);
        Assert.Equal(text, ed.Text);
    }

    [Fact]
    public void TrailingCommas_StyleKept_AndRoundTrip()
    {
        const string text = "// Zed settings\n{\n  \"theme\": \"One Dark\",\n  \"context_servers\": {\n    \"other\": {\n      \"command\": \"node\",\n      \"args\": [\"server.js\",],\n    },\n  },\n  \"vim_mode\": false,\n}\n";
        var path = new[] { "context_servers", "offload" };
        var result = SetAndCheck(text, path, new JsonObject { ["command"] = "x", ["timeout"] = 1800 });
        Assert.Contains("    },\n    \"offload\": {", result);
        var ed = JsoncEditor.Parse(result);
        ed.Remove(path);
        Assert.Equal(text, ed.Text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ }")]
    [InlineData("{\n}")]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("// только комментарий\n")]
    [InlineData("{\n  // пусто\n}")]
    [InlineData("{ /* пусто */ }")]
    [InlineData("{\"servers\":{}}")]
    [InlineData("{\"servers\":null}")]
    [InlineData("{\"other\":1}")]
    [InlineData("{\n  \"servers\": {\n  }\n}")]
    public void Insert_IntoEmptyOrMissingContainers(string text)
    {
        var result = SetAndCheck(text, ServerPath, Entry());
        if (text.Contains("//")) Assert.Contains(text.Contains("пусто") ? "пусто" : "только комментарий", result);
    }

    [Fact]
    public void Minified_StaysCompact()
    {
        const string text = "{\"a\":1,\"servers\":{\"x\":{\"command\":\"y\"}}}";
        var result = SetAndCheck(text, ServerPath, Entry("c"));
        Assert.DoesNotContain("\n", result);
        var ed = JsoncEditor.Parse(result);
        ed.Remove(ServerPath);
        Assert.Equal(text, ed.Text);
    }

    [Fact]
    public void Crlf_IsPreserved()
    {
        const string text = "{\r\n  \"servers\": {\r\n    \"a\": {}\r\n  }\r\n}\r\n";
        var result = SetAndCheck(text, ServerPath, Entry());
        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
        var ed = JsoncEditor.Parse(result);
        ed.Remove(ServerPath);
        Assert.Equal(text, ed.Text);
    }

    [Fact]
    public void Replace_ExistingValue_OnlyThatValueChanges()
    {
        const string text = "{\n  // c1\n  \"servers\": {\n    \"offload\": { \"command\": \"old\" }, // наш\n    \"b\": 2\n  }\n}";
        var result = SetAndCheck(text, ServerPath, Entry("new"));
        Assert.Contains("// c1", result);
        Assert.Contains("}, // наш\n    \"b\": 2", result);
        Assert.DoesNotContain("old", result);
    }

    [Fact]
    public void Unicode_CyrillicAndEscapes()
    {
        const string text = "{\"name\": \"Иван \\u0041 \\\"q\\\"\", \"servers\": {}}";
        var cmd = @"C:\Users\Иван Петров\AppData\Local\Offload\Offload.exe";
        var result = SetAndCheck(text, ServerPath, Entry(cmd));
        Assert.Contains("Иван Петров", result); // кириллица без \\u-экранирования
        Assert.Contains("\"Иван \\u0041 \\\"q\\\"\"", result); // чужая строка не переписана
        var ed = JsoncEditor.Parse(result);
        ed.TryGet([.. ServerPath, "command"], out var c);
        Assert.Equal(cmd, JsonTree.AsString(c));
    }

    [Fact]
    public void Set_SameValue_IsNoOp()
    {
        var text = SetAndCheck("{}", ServerPath, Entry());
        var ed = JsoncEditor.Parse(text);
        Assert.False(ed.Set(ServerPath, Entry()));
        Assert.Equal(text, ed.Text);
    }

    [Fact]
    public void Remove_OnlyMember_CollapsesToEmptyObject()
    {
        var ed = JsoncEditor.Parse("{\n  \"servers\": {\n    \"offload\": {\"a\": 1}\n  }\n}");
        Assert.True(ed.Remove(ServerPath));
        Assert.Equal("{\n  \"servers\": {}\n}", ed.Text);
    }

    [Fact]
    public void Remove_FirstAndMiddleMembers()
    {
        var ed = JsoncEditor.Parse("{\n  \"offload\": 1,\n  \"b\": 2\n}");
        ed.Remove(["offload"]);
        Assert.Equal("{\n  \"b\": 2\n}", ed.Text);

        ed = JsoncEditor.Parse("{\"a\":1, \"offload\":2, \"b\":3}");
        ed.Remove(["offload"]);
        Assert.Equal("{\"a\":1, \"b\":3}", ed.Text);
    }

    [Fact]
    public void Remove_DuplicateKeys_RemovesAll()
    {
        var ed = JsoncEditor.Parse("{\"s\":{\"offload\":1,\"x\":2,\"offload\":3}}");
        Assert.True(ed.Remove(["s", "offload"]));
        Assert.Equal("{\"s\":{\"x\":2}}", ed.Text);
    }

    [Fact]
    public void Remove_Missing_ReturnsFalse()
    {
        var ed = JsoncEditor.Parse("{\"a\":1}");
        Assert.False(ed.Remove(["servers", "offload"]));
        Assert.Equal("{\"a\":1}", ed.Text);
    }

    [Fact]
    public void ParentNotObject_Throws_AndTextUnchanged()
    {
        var ed = JsoncEditor.Parse("{\"servers\": [1, 2]}");
        Assert.Throws<JsoncEditException>(() => ed.Set(ServerPath, Entry()));
        Assert.Equal("{\"servers\": [1, 2]}", ed.Text);
        Assert.Throws<JsoncEditException>(() => JsoncEditor.Parse("[1]").Set(ServerPath, Entry()));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"a\": 1,, }")]
    [InlineData("{\"a\": 1 \"b\": 2}")]
    [InlineData("{\"a\": 'x'}")]
    [InlineData("{\"a\": tru}")]
    [InlineData("{\"a\": 01}")]
    [InlineData("{\"a\": \"line\nbreak\"}")]
    [InlineData("{/* not closed }")]
    [InlineData("{\"a\":1} {}")]
    [InlineData("{a: 1}")]
    [InlineData("{\"a\": NaN}")]
    public void Malformed_Throws(string text)
    {
        var ex = Assert.Throws<JsoncParseException>(() => JsoncEditor.Parse(text));
        Assert.True(ex.Line >= 1);
    }

    [Fact]
    public void Bom_IsStripped()
    {
        var ed = JsoncEditor.Parse("\uFEFF{\"a\":1}");
        Assert.True(ed.HadBom);
        ed.Set(["b"], 2);
        Assert.False(ed.Text.StartsWith('\uFEFF'));
    }

    [Fact]
    public void Array_AppendAndRemove_PreserveOtherElementsExactly()
    {
        const string text = "{\n  \"permissions\": {\n    \"allow\": [\n      \"Bash(echo \\\"exit $?\\\")\",\n      \"Read(//tmp/**)\" // мой\n    ]\n  }\n}\n";
        var path = new[] { "permissions", "allow" };
        var ed = JsoncEditor.Parse(text);
        Assert.True(ed.AppendToArray(path, [JsonValue.Create("mcp__offload__local_status"), JsonValue.Create("mcp__offload__local_ask_files")]));
        Assert.False(ed.LastEditUsedFallback);
        Assert.Contains("\"Bash(echo \\\"exit $?\\\")\",", ed.Text);
        Assert.Contains("// мой", ed.Text);
        Assert.True(ed.RemoveFromArray(path, n => JsonTree.AsString(n)?.StartsWith("mcp__offload") == true));
        Assert.Equal(text, ed.Text);
    }

    [Fact]
    public void Array_AppendToMissingArray_CreatesIt()
    {
        var ed = JsoncEditor.Parse("{\n  \"model\": \"x\"\n}");
        ed.AppendToArray(["permissions", "allow"], [JsonValue.Create("a")]);
        var n = JsonTree.Navigate(Oracle(ed.Text), ["permissions", "allow"]);
        Assert.Equal(["a"], JsonTree.AsStringList(n)!);
    }

    // ------------------------------------------------------------------ фаззинг

    private sealed class Gen(int seed)
    {
        private readonly Random _r = new(seed);
        public readonly List<string> Comments = [];
        public bool Crlf;
        public string Unit = "  ";
        public bool Compact;

        private string Nl => Crlf ? "\r\n" : "\n";

        public string Trivia(bool allowNewline)
        {
            if (Compact) return _r.Next(20) == 0 ? Comment(false) : "";
            var sb = new StringBuilder();
            switch (_r.Next(8))
            {
                case 0: sb.Append(' '); break;
                case 1: sb.Append(Comment(false)); break;
                case 2 when allowNewline: sb.Append(' ').Append(Comment(true)); break;
            }
            return sb.ToString();
        }

        private string Comment(bool line)
        {
            var id = $"c{Comments.Count}_{_r.Next(1000)}";
            Comments.Add(id);
            return line ? $"// {id} Привет{Nl}" : $"/* {id} */";
        }

        public string Key() => _r.Next(6) switch
        {
            0 => "ключ" + _r.Next(5),
            1 => "k\\\"q" + _r.Next(3),
            _ => "k" + _r.Next(8),
        };

        public string Scalar() => _r.Next(6) switch
        {
            0 => _r.Next(-100, 100).ToString(),
            1 => "true",
            2 => "null",
            3 => "1.5e3",
            4 => "\"строка \\\\ \\n \\u00e9\"",
            _ => "\"s" + _r.Next(100) + "\"",
        };

        public string Value(int depth, int indent)
        {
            var k = depth > 3 ? 0 : _r.Next(5);
            if (k < 2) return Scalar();
            var isObj = k != 2;
            var n = _r.Next(0, 4);
            var pad = Compact ? "" : string.Concat(Enumerable.Repeat(Unit, indent + 1));
            var closePad = Compact ? "" : string.Concat(Enumerable.Repeat(Unit, indent));
            var nl = Compact ? "" : Nl;
            var sb = new StringBuilder(isObj ? "{" : "[");
            if (n == 0)
            {
                sb.Append(Trivia(false));
            }
            else
            {
                sb.Append(nl);
                for (var i = 0; i < n; i++)
                {
                    sb.Append(pad);
                    if (isObj) sb.Append('"').Append(Key()).Append(i).Append("\":").Append(Compact ? "" : " ");
                    sb.Append(Value(depth + 1, indent + 1));
                    var last = i == n - 1;
                    if (!last || _r.Next(4) == 0) sb.Append(',');
                    sb.Append(Trivia(true));
                    if (!sb.ToString().EndsWith('\n')) sb.Append(nl);
                }
                sb.Append(closePad);
            }
            sb.Append(isObj ? "}" : "]");
            return sb.ToString();
        }

        public string Document()
        {
            Crlf = _r.Next(3) == 0;
            Compact = _r.Next(6) == 0;
            Unit = _r.Next(3) switch { 0 => "\t", 1 => "    ", _ => "  " };
            // Корень — всегда объект; комментарии отброшенных попыток не учитываем.
            string doc;
            do
            {
                Comments.Clear();
                doc = Value(0, 0);
            }
            while (!doc.StartsWith('{'));
            var prefix = _r.Next(3) == 0 ? Comment(true) : "";
            return prefix + doc + (_r.Next(2) == 0 ? Nl : "");
        }

        public int Next(int n) => _r.Next(n);
    }

    private static List<List<string>> ObjectPaths(JsonNode? root, List<string>? prefix = null)
    {
        prefix ??= [];
        var list = new List<List<string>>();
        if (root is JsonObject o)
        {
            list.Add(prefix);
            foreach (var (k, v) in o) list.AddRange(ObjectPaths(v, [.. prefix, k]));
        }
        return list;
    }

    [Fact]
    public void Fuzz_SetRemoveAppend_ProduceValidJsonWithExpectedSemantics()
    {
        var failures = new List<string>();
        for (var seed = 0; seed < 1500; seed++)
        {
            var g = new Gen(seed);
            var text = g.Document();
            JsoncEditor ed;
            try
            {
                ed = JsoncEditor.Parse(text);
            }
            catch (JsoncParseException ex)
            {
                failures.Add($"seed {seed}: генератор выдал невалидный текст ({ex.Message}):\n{text}");
                continue;
            }
            Assert.True(JsonTree.TryParseOracle(text, out var before), $"seed {seed}: оракул не разобрал исходник");

            var objects = ObjectPaths(before);
            var parent = objects[g.Next(objects.Count)];
            var op = g.Next(3);
            var value = new JsonObject { ["command"] = "C:\\Иван\\Offload.exe", ["args"] = new JsonArray("--mcp"), ["n"] = 3600 };
            try
            {
                switch (op)
                {
                    case 0:
                    {
                        var path = parent.Append("offload").ToList();
                        ed.Set(path, value);
                        // Все комментарии сохранены (новый ключ ничего не заменял).
                        if (JsonTree.Navigate(before, path) is null)
                        {
                            foreach (var c in g.Comments)
                            {
                                if (!ed.Text.Contains(c))
                                {
                                    failures.Add($"seed {seed}: потерян комментарий {c}\n--- было:\n{text}\n--- стало:\n{ed.Text}");
                                    break;
                                }
                            }
                        }
                        break;
                    }
                    case 1:
                    {
                        var obj = (JsonObject)JsonTree.Navigate(before, parent)!;
                        if (obj.Count == 0) continue;
                        var key = obj.ElementAt(g.Next(obj.Count)).Key;
                        ed.Remove([.. parent, key]);
                        break;
                    }
                    default:
                        ed.Set([.. parent, "nested", "deeper"], JsonValue.Create("v"));
                        break;
                }
                if (ed.LastEditUsedFallback) failures.Add($"seed {seed} op {op}: точечная правка не прошла проверку:\n{text}");
                JsoncEditor.Parse(ed.Text); // наш разборщик понимает результат
                Assert.True(JsonTree.TryParseOracle(ed.Text, out _), $"seed {seed}: результат невалиден");
            }
            catch (JsoncEditException ex)
            {
                failures.Add($"seed {seed} op {op}: {ex.Message}\n{text}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n----\n", failures.Take(5)) + $"\n(всего {failures.Count})");
    }

    [Fact]
    public void Fuzz_RandomBytes_NeverAcceptWhatOracleRejects()
    {
        var r = new Random(42);
        const string alphabet = "{}[],:\"\\/* \n\tabtrufalsn0123456789.-eE";
        for (var i = 0; i < 5000; i++)
        {
            var len = r.Next(1, 30);
            var s = new string(Enumerable.Range(0, len).Select(_ => alphabet[r.Next(alphabet.Length)]).ToArray());
            bool ours;
            try { JsoncParser.Parse(s, out var root); ours = true; } catch (JsoncParseException) { ours = false; }
            if (!ours) continue;
            // Всё, что приняли мы, должен принять и System.Text.Json (или это пустой/комментарный текст).
            Assert.True(JsonTree.TryParseOracle(s, out _), $"мы приняли, а System.Text.Json — нет: {JsonSerializer.Serialize(s)}");
        }
    }
}
