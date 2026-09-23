using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Offload.OpenCode;

/// <summary>
/// Разбор событий «opencode run --format json» (одна JSON-строка на событие):
/// step_start, text, tool_use (part.tool, part.state{status,input,output|error,title}),
/// step_finish (part.tokens, part.reason), reasoning, error. Не-JSON строки игнорируются.
/// Итоговый ответ — текст после последнего вызова инструмента (иначе — последний текст).
/// </summary>
internal sealed class RunEvents(string workingDirectory)
{
    private readonly string _workDir = workingDirectory;
    private readonly List<string> _toolCalls = [];
    private readonly List<string> _errors = [];
    private readonly List<string> _textsAfterLastTool = [];
    private string? _lastText;

    public int Steps { get; private set; }
    public int Events { get; private set; }
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public string? LastFinishReason { get; private set; }
    public IReadOnlyList<string> ToolCalls => _toolCalls;
    public IReadOnlyList<string> Errors => _errors;

    public string FinalText =>
        _textsAfterLastTool.Count > 0 ? string.Join("\n\n", _textsAfterLastTool) : _lastText ?? "";

    /// <summary>Обработать строку stdout. Возвращает краткое сообщение о ходе работы (или null).</summary>
    public string? Feed(string line)
    {
        var s = line.Trim();
        if (s.Length < 2 || s[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = Str(root, "type");
            if (type is null) return null;
            Events++;
            var part = root.TryGetProperty("part", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            return type switch
            {
                "step_start" => OnStepStart(),
                "text" => OnText(part),
                "tool_use" => OnTool(part),
                "step_finish" => OnStepFinish(part),
                "error" => OnError(root),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null; // обрывок или журнал — не событие
        }
    }

    /// <summary>
    /// Строка оказалась слишком длинной (например, правка большого файла с полным diff в metadata) и обрезана:
    /// восстанавливаем из начала хотя бы тип события, инструмент и путь.
    /// </summary>
    public string? FeedTruncated(string prefix)
    {
        var type = Regex.Match(prefix, "^\\s*\\{\\s*\"type\"\\s*:\\s*\"(?<t>[a-z_]+)\"", RegexOptions.CultureInvariant);
        if (!type.Success || type.Groups["t"].Value != "tool_use") return null;
        Events++;
        var tool = Regex.Match(prefix, "\"tool\"\\s*:\\s*\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant);
        var file = Regex.Match(prefix, "\"filePath\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        var name = tool.Success ? tool.Groups["v"].Value : "tool";
        string? arg = null;
        if (file.Success)
        {
            try { arg = RelPath(JsonSerializer.Deserialize<string>("\"" + file.Groups["v"].Value + "\"") ?? ""); }
            catch (JsonException) { arg = null; }
        }
        return AddTool(name, arg, failed: false);
    }

    private string OnStepStart()
    {
        Steps++;
        return L.F("шаг {0}: модель думает…", Steps);
    }

    private string? OnText(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object) return null;
        var text = Str(part, "text")?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        _lastText = text;
        _textsAfterLastTool.Add(text);
        return null;
    }

    private string? OnTool(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object) return null;
        var tool = Str(part, "tool") ?? "tool";
        var state = part.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object ? st : default;
        var failed = state.ValueKind == JsonValueKind.Object && Str(state, "status") == "error";
        return AddTool(tool, MainArgument(tool, state), failed);
    }

    private string AddTool(string tool, string? arg, bool failed)
    {
        _textsAfterLastTool.Clear();
        arg = OneLine(arg, 120);
        var entry = string.IsNullOrEmpty(arg) ? tool : $"{tool} {arg}";
        _toolCalls.Add(failed ? entry + " " + L.T("(ошибка)") : entry);
        var what = string.IsNullOrEmpty(arg) ? Verb(tool) : $"{Verb(tool)}: {OneLine(arg, 80)}";
        if (failed) what = L.F("{0} — ошибка", what);
        return Steps > 0 ? L.F("шаг {0}: {1}", Steps, what) : what;
    }

    private string? OnStepFinish(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object) return null;
        LastFinishReason = Str(part, "reason") ?? LastFinishReason;
        if (part.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            long cacheRead = 0, cacheWrite = 0;
            if (t.TryGetProperty("cache", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                cacheRead = Num(c, "read");
                cacheWrite = Num(c, "write");
            }
            // Каждый шаг — отдельный запрос к модели с полным контекстом.
            PromptTokens += Num(t, "input") + cacheRead + cacheWrite;
            CompletionTokens += Num(t, "output") + Num(t, "reasoning");
        }
        return null;
    }

    private string OnError(JsonElement root)
    {
        var msg = ErrorMessage(root.TryGetProperty("error", out var e) ? e : default) ?? L.T("неизвестная ошибка");
        msg = OneLine(StripAnsi(msg), 500) ?? L.T("неизвестная ошибка");
        if (!_errors.Contains(msg)) _errors.Add(msg);
        return L.F("ошибка: {0}", OneLine(msg, 120));
    }

    internal static string? ErrorMessage(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Object:
            {
                if (e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && Str(d, "message") is { Length: > 0 } dm)
                    return dm;
                if (Str(e, "message") is { Length: > 0 } m) return m;
                return Str(e, "name");
            }
            default:
                return null;
        }
    }

    /// <summary>Главный аргумент вызова: путь файла (относительно рабочей папки), шаблон, команда…</summary>
    private string? MainArgument(string tool, JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return null;
        var title = Str(state, "title");
        if (!state.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return title;

        if ((Str(input, "filePath") ?? Str(input, "file_path")) is { Length: > 0 } fp) return RelPath(fp);
        if (tool is "bash" or "shell" && Str(input, "command") is { Length: > 0 } cmd) return cmd;
        if (Str(input, "pattern") is { Length: > 0 } pattern)
        {
            var where = Str(input, "path");
            return string.IsNullOrEmpty(where) ? pattern : $"{pattern} ({RelPath(where)})";
        }
        if (Str(input, "command") is { Length: > 0 } c2) return c2;
        if (Str(input, "path") is { Length: > 0 } path) return RelPath(path);
        if (Str(input, "url") is { Length: > 0 } url) return url;
        if (!string.IsNullOrEmpty(title)) return title;
        return Str(input, "description");
    }

    private string RelPath(string path)
    {
        try
        {
            if (Path.IsPathFullyQualified(path))
            {
                var rel = Path.GetRelativePath(_workDir, path);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) path = rel;
            }
        }
        catch
        {
            // Необычный путь — показываем как есть.
        }
        return path.Replace('\\', '/');
    }

    internal static string Verb(string tool) => tool switch
    {
        "read" => L.T("чтение"),
        "edit" => L.T("правка"),
        "write" => L.T("запись"),
        "apply_patch" or "patch" => L.T("патч"),
        "glob" => L.T("поиск файлов"),
        "grep" => L.T("поиск в коде"),
        "list" => L.T("список файлов"),
        "bash" or "shell" => L.T("команда"),
        "webfetch" => L.T("загрузка страницы"),
        "todowrite" => L.T("план"),
        "task" => L.T("подзадача"),
        "skill" => L.T("навык"),
        "invalid" => L.T("некорректный вызов"),
        _ => tool,
    };

    private static readonly Regex Ansi = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string StripAnsi(string s) => s.IndexOf('\x1B') < 0 ? s : Ansi.Replace(s, "");

    private static string? OneLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!space && sb.Length > 0) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(ch);
                space = false;
            }
            if (sb.Length > max) break;
        }
        var r = sb.ToString().TrimEnd();
        return r.Length > max ? r[..max].TrimEnd() + "…" : r;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt64(out var l)) return Math.Max(0, l);
        return v.TryGetDouble(out var d) && d > 0 ? (long)d : 0;
    }
}

/// <summary>Хвост stderr (для текста ошибки) — последние строки без ANSI-последовательностей.</summary>
internal sealed class TailBuffer(int maxLines)
{
    private readonly Queue<string> _lines = new();

    public void Add(string line)
    {
        var s = RunEvents.StripAnsi(line).TrimEnd();
        if (s.Length == 0) return;
        if (s.Length > 1000) s = s[..1000] + "…";
        lock (_lines)
        {
            _lines.Enqueue(s);
            while (_lines.Count > maxLines) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Lines()
    {
        lock (_lines) return _lines.ToArray();
    }

    public string Text(int lastLines)
    {
        var l = Lines();
        return string.Join("\n", l.Skip(Math.Max(0, l.Count - lastLines)));
    }
}
