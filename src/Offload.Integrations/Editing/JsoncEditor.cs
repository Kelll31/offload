using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Offload.Integrations.Editing;

/// <summary>Правку нельзя выполнить безопасно (структура файла не та, что ожидалась).</summary>
internal sealed class JsoncEditException(string message) : Exception(message);

/// <summary>
/// Точечная правка JSON/JSONC «на месте»: меняется только текст целевого члена/элемента,
/// комментарии, отступы, порядок ключей и форматирование остального файла сохраняются байт в байт.
/// Каждая правка проверяется независимым разбором (System.Text.Json): результат должен совпасть
/// с ожидаемым деревом. Если точечная вставка не прошла проверку, полная пересериализация допускается
/// только для файлов без комментариев; иначе — исключение, файл не меняется.
/// </summary>
internal sealed class JsoncEditor
{
    private JsoncValue? _root;
    private string _newLine = "\n";
    private string _indentUnit = "  ";
    private bool _colonSpace = true;

    public string Text { get; private set; } = "";

    /// <summary>В исходном тексте был BOM (при записи он не сохраняется).</summary>
    public bool HadBom { get; private set; }

    public bool HasComments { get; private set; }

    public bool HasTrailingCommas { get; private set; }

    public bool IsEmpty => _root is null;

    /// <summary>Последняя правка выполнена полной пересериализацией (запасной путь), а не точечной вставкой.</summary>
    public bool LastEditUsedFallback { get; private set; }

    private JsoncEditor() { }

    /// <exception cref="JsoncParseException">Текст не является корректным JSON/JSONC.</exception>
    public static JsoncEditor Parse(string text)
    {
        var ed = new JsoncEditor();
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            ed.HadBom = true;
            text = text[1..];
        }
        ed.Load(text);
        return ed;
    }

    private void Load(string text)
    {
        var p = JsoncParser.Parse(text, out var root);
        Text = text;
        _root = root;
        HasComments = p.HasComments;
        HasTrailingCommas = p.HasTrailingCommas;
        InferStyle();
    }

    // ------------------------------------------------------------------ чтение

    /// <summary>Значение по пути ключей объектов. false — если пути нет (или по дороге не объект).</summary>
    public bool TryGet(IReadOnlyList<string> path, out JsonNode? value)
    {
        value = null;
        var v = Find(path);
        if (v is null) return false;
        value = JsonTree.FromJsonc(Text, v);
        return true;
    }

    /// <summary>Тип значения по пути (null — пути нет).</summary>
    public JsoncKind? KindAt(IReadOnlyList<string> path) => Find(path)?.Kind;

    public JsonNode? ToNode() => _root is null ? null : JsonTree.FromJsonc(Text, _root);

    private JsoncValue? Find(IReadOnlyList<string> path)
    {
        var cur = _root;
        if (cur is null) return null;
        foreach (var key in path)
        {
            if (cur.Kind != JsoncKind.Object) return null;
            var item = LastMember(cur, key);
            if (item is null) return null;
            cur = item.Value;
        }
        return cur;
    }

    private static JsoncItem? LastMember(JsoncValue obj, string key)
    {
        for (var i = obj.Items.Count - 1; i >= 0; i--)
        {
            if (obj.Items[i].Key == key) return obj.Items[i];
        }
        return null;
    }

    // ------------------------------------------------------------------ операции

    /// <summary>Установить значение члена по пути (создаёт недостающие объекты). false — значение уже такое же.</summary>
    public bool Set(IReadOnlyList<string> path, JsonNode? value)
    {
        if (path.Count == 0) throw new ArgumentException("Пустой путь", nameof(path));
        if (TryGet(path, out var current) && JsonTree.SemanticEquals(current, value)) return false;

        var original = Text;
        var newText = SpliceSet(path, value);
        Commit(original, newText, root => JsonTree.ApplySet(root, path, value));
        return true;
    }

    /// <summary>Удалить член (все дубликаты ключа). false — члена не было.</summary>
    public bool Remove(IReadOnlyList<string> path)
    {
        if (path.Count == 0) throw new ArgumentException("Пустой путь", nameof(path));
        if (Find(path) is null) return false;

        var original = Text;
        var work = this.Clone();
        while (true)
        {
            var parent = work.Find(path.Take(path.Count - 1).ToArray());
            if (parent is null || parent.Kind != JsoncKind.Object) break;
            var index = parent.Items.FindLastIndex(i => i.Key == path[^1]);
            if (index < 0) break;
            work.Load(work.Apply(work.RemoveItemEdits(parent, index)));
        }
        Commit(original, work.Text, root => JsonTree.ApplyRemove(root, path));
        return true;
    }

    /// <summary>Добавить элементы в конец массива (создаёт массив, если его нет). false — нечего добавлять.</summary>
    public bool AppendToArray(IReadOnlyList<string> path, IReadOnlyList<JsonNode?> values)
    {
        if (values.Count == 0) return false;
        var target = Find(path);
        if (target is null || target.Kind == JsoncKind.Null)
            return Set(path, new JsonArray(values.Select(v => v?.DeepClone()).ToArray()));
        if (target.Kind != JsoncKind.Array) throw new JsoncEditException($"«{path[^1]}» не является массивом");

        var original = Text;
        var work = this.Clone();
        foreach (var v in values)
        {
            var arr = work.Find(path)!;
            work.Load(work.Apply(work.InsertEdits(arr, key: null, v)));
        }
        Commit(original, work.Text, root => JsonTree.ApplyAppend(root, path, values));
        return true;
    }

    /// <summary>Удалить элементы массива, подходящие под условие. false — ничего не удалено.</summary>
    public bool RemoveFromArray(IReadOnlyList<string> path, Func<JsonNode?, bool> predicate)
    {
        var target = Find(path);
        if (target is null || target.Kind != JsoncKind.Array) return false;
        if (!target.Items.Any(i => predicate(JsonTree.FromJsonc(Text, i.Value)))) return false;

        var original = Text;
        var work = this.Clone();
        while (true)
        {
            var arr = work.Find(path)!;
            var index = arr.Items.FindLastIndex(i => predicate(JsonTree.FromJsonc(work.Text, i.Value)));
            if (index < 0) break;
            work.Load(work.Apply(work.RemoveItemEdits(arr, index)));
        }
        Commit(original, work.Text, root => JsonTree.ApplyRemoveWhere(root, path, predicate));
        return true;
    }

    private JsoncEditor Clone()
    {
        var c = new JsoncEditor { HadBom = HadBom };
        c.Load(Text);
        return c;
    }

    // ------------------------------------------------------------------ проверка

    private void Commit(string original, string candidate, Func<JsonNode?, JsonNode?> transform)
    {
        if (!JsonTree.TryParseOracle(original, out var before))
            throw new JsoncEditException("файл не удалось разобрать стандартным разборщиком JSON");
        var expected = transform(before);

        if (Verify(candidate, expected))
        {
            Load(candidate);
            LastEditUsedFallback = false;
            return;
        }

        // Запасной путь — только для файлов без комментариев (иначе мы бы их потеряли).
        if (!HasComments)
        {
            var whole = Serialize(expected, multiline: true, baseIndent: "") + _newLine;
            if (Verify(whole, expected))
            {
                Load(whole);
                LastEditUsedFallback = true;
                return;
            }
        }
        throw new JsoncEditException("не удалось безопасно изменить файл: проверка результата не прошла");
    }

    private static bool Verify(string candidate, JsonNode? expected)
    {
        try
        {
            JsoncParser.Parse(candidate, out _);
        }
        catch (JsoncParseException)
        {
            return false;
        }
        return JsonTree.TryParseOracle(candidate, out var actual) && JsonTree.SemanticEquals(expected, actual);
    }

    // ------------------------------------------------------------------ построение правок

    private readonly record struct Edit(int Start, int End, string Replacement);

    private string Apply(List<Edit> edits)
    {
        // С конца файла к началу; вставки в одну позицию — в обратном порядке, чтобы в тексте они шли как в списке.
        var ordered = edits.Select((e, i) => (e, i))
            .OrderByDescending(x => x.e.Start).ThenByDescending(x => x.e.End).ThenByDescending(x => x.i)
            .Select(x => x.e).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].End > ordered[i - 1].Start)
                throw new JsoncEditException("внутренняя ошибка: пересекающиеся правки");
        }
        var sb = new StringBuilder(Text);
        foreach (var e in ordered)
        {
            sb.Remove(e.Start, e.End - e.Start);
            sb.Insert(e.Start, e.Replacement);
        }
        return sb.ToString();
    }

    private string SpliceSet(IReadOnlyList<string> path, JsonNode? value)
    {
        if (_root is null)
        {
            // Пустой файл (или только комментарии): новый объект после имеющегося текста.
            var body = Serialize(BuildNested(path, 0, value), multiline: true, baseIndent: "");
            var prefix = Text.TrimEnd();
            return prefix.Length == 0 ? body + _newLine : prefix + _newLine + body + _newLine;
        }
        if (_root.Kind != JsoncKind.Object) throw new JsoncEditException("корень файла не является объектом");

        var cur = _root;
        for (var i = 0; i < path.Count; i++)
        {
            var member = LastMember(cur, path[i]);
            var last = i == path.Count - 1;
            if (member is null)
            {
                var v = last ? value : BuildNested(path, i + 1, value);
                return Apply(InsertEdits(cur, path[i], v));
            }
            if (last || member.Value.Kind == JsoncKind.Null)
            {
                var v = last ? value : BuildNested(path, i + 1, value);
                return Apply(ReplaceValueEdits(cur, member, v));
            }
            if (member.Value.Kind != JsoncKind.Object)
                throw new JsoncEditException($"«{path[i]}» не является объектом");
            cur = member.Value;
        }
        throw new InvalidOperationException();
    }

    private static JsonNode? BuildNested(IReadOnlyList<string> path, int from, JsonNode? value)
    {
        var node = value?.DeepClone();
        for (var i = path.Count - 1; i >= from; i--)
        {
            node = new JsonObject { [path[i]] = node };
        }
        return node;
    }

    private List<Edit> ReplaceValueEdits(JsoncValue container, JsoncItem item, JsonNode? value)
    {
        var multi = IsMultiline(container) && StartsOwnLine(item.Start);
        var text = Serialize(value, multi, multi ? LineIndent(item.Start) : "");
        return [new Edit(item.Value.Start, item.Value.End, text)];
    }

    /// <summary>Вставка члена (key != null) или элемента массива в конец контейнера.</summary>
    private List<Edit> InsertEdits(JsoncValue c, string? key, JsonNode? value)
    {
        var multi = IsMultiline(c);
        var indent = ItemIndent(c);
        var valueText = Serialize(value, multi, multi ? indent : "");
        var t = key is null ? valueText : JsonTree.Quote(key) + (multi || _colonSpace ? ": " : ":") + valueText;
        var sep = multi || _colonSpace ? " " : "";
        var edits = new List<Edit>();

        if (c.Items.Count > 0)
        {
            var last = c.Items[^1];
            if (last.CommaPos >= 0)
            {
                // Висячая запятая: вставляем после неё и сохраняем стиль (запятая и после нашего элемента).
                if (multi)
                {
                    var q = SameLineTriviaEnd(last.CommaPos + 1);
                    if (IsLineBreakAt(q)) edits.Add(Ins(q, _newLine + indent + t + ","));
                    else edits.Add(Ins(last.CommaPos + 1, " " + t + ","));
                }
                else
                {
                    edits.Add(Ins(last.CommaPos + 1, sep + t + ","));
                }
            }
            else if (multi)
            {
                var q = SameLineTriviaEnd(last.Value.End);
                if (IsLineBreakAt(q))
                {
                    edits.Add(Ins(last.Value.End, ","));
                    edits.Add(Ins(q, _newLine + indent + t));
                }
                else
                {
                    edits.Add(Ins(last.Value.End, ", " + t));
                }
            }
            else
            {
                edits.Add(Ins(last.Value.End, "," + sep + t));
            }
            return edits;
        }

        var open = c.Start;
        var close = c.End - 1;
        var inner = Text.AsSpan(open + 1, close - open - 1);
        if (IsWhitespace(inner))
        {
            var replacement = multi ? _newLine + indent + t + _newLine + LineIndent(open) : t;
            edits.Add(new Edit(open + 1, close, replacement));
        }
        else if (multi && StartsOwnLine(close))
        {
            edits.Add(Ins(LineStart(close), indent + t + _newLine));
        }
        else if (multi)
        {
            edits.Add(Ins(close, _newLine + indent + t + _newLine + LineIndent(open)));
        }
        else
        {
            edits.Add(Ins(close, " " + t + " "));
        }
        return edits;
    }

    private static Edit Ins(int pos, string text) => new(pos, pos, text);

    private List<Edit> RemoveItemEdits(JsoncValue c, int index)
    {
        var it = c.Items[index];
        var isLast = index == c.Items.Count - 1;
        var s = it.Start;
        var e = it.CommaPos >= 0 ? it.CommaPos + 1 : it.Value.End;
        var edits = new List<Edit>();

        if (c.Items.Count == 1)
        {
            var before = Text.AsSpan(c.Start + 1, s - c.Start - 1);
            var after = Text.AsSpan(e, c.End - 1 - e);
            if (IsWhitespace(before) && IsWhitespace(after))
            {
                edits.Add(new Edit(c.Start + 1, c.End - 1, ""));
                return edits;
            }
        }

        if (isLast && it.CommaPos < 0 && index > 0)
        {
            var prev = c.Items[index - 1];
            var between = Text.AsSpan(prev.CommaPos + 1, s - prev.CommaPos - 1);
            if (IsWhitespace(between) && between.IndexOfAny('\r', '\n') < 0)
            {
                edits.Add(new Edit(prev.CommaPos, e, ""));
                return edits;
            }
            edits.Add(new Edit(prev.CommaPos, prev.CommaPos + 1, ""));
        }

        var q = e;
        while (q < Text.Length && Text[q] is ' ' or '\t') q++;
        var ls = LineStart(s);
        if (IsWhitespace(Text.AsSpan(ls, s - ls)))
        {
            if (q + 1 < Text.Length && Text[q] == '\r' && Text[q + 1] == '\n')
            {
                edits.Add(new Edit(ls, q + 2, ""));
                return edits;
            }
            if (q < Text.Length && Text[q] is '\n' or '\r')
            {
                edits.Add(new Edit(ls, q + 1, ""));
                return edits;
            }
        }
        edits.Add(new Edit(s, q, ""));
        return edits;
    }

    // ------------------------------------------------------------------ форматирование

    private string Serialize(JsonNode? node, bool multiline, string baseIndent)
    {
        var (ch, size) = IndentSpec();
        var opts = new JsonWriterOptions
        {
            Indented = multiline,
            Encoder = JsonTree.Encoder,
            IndentCharacter = ch,
            IndentSize = size,
            NewLine = _newLine,
            MaxDepth = JsoncParser.MaxDepth + 8,
        };
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, opts))
        {
            if (node is null) w.WriteNullValue();
            else node.WriteTo(w);
        }
        var s = Encoding.UTF8.GetString(ms.ToArray());
        return multiline && baseIndent.Length > 0 ? s.Replace(_newLine, _newLine + baseIndent) : s;
    }

    private (char Ch, int Size) IndentSpec()
    {
        if (_indentUnit.Length is > 0 and <= 16)
        {
            if (_indentUnit.All(c => c == ' ')) return (' ', _indentUnit.Length);
            if (_indentUnit.All(c => c == '\t')) return ('\t', _indentUnit.Length);
        }
        return (' ', 2);
    }

    private void InferStyle()
    {
        var crlf = Text.IndexOf("\r\n", StringComparison.Ordinal);
        var lf = Text.IndexOf('\n');
        _newLine = crlf >= 0 && crlf + 1 == lf ? "\r\n" : "\n";

        _indentUnit = "  ";
        _colonSpace = true;
        if (_root is null) return;

        var colonFound = false;
        var unitFound = false;
        var stack = new Stack<JsoncValue>();
        stack.Push(_root);
        while (stack.Count > 0 && !(colonFound && unitFound))
        {
            var c = stack.Pop();
            foreach (var it in c.Items)
            {
                if (!colonFound && it.ColonPos >= 0)
                {
                    colonFound = true;
                    _colonSpace = it.ColonPos + 1 < Text.Length && Text[it.ColonPos + 1] is ' ' or '\t' or '\r' or '\n';
                }
                if (!unitFound && StartsOwnLine(it.Start))
                {
                    var outer = LineIndent(c.Start);
                    var inner = LineIndent(it.Start);
                    if (inner.Length > outer.Length && inner.StartsWith(outer, StringComparison.Ordinal))
                    {
                        var unit = inner[outer.Length..];
                        if (unit.All(ch => ch == ' ') || unit.All(ch => ch == '\t'))
                        {
                            _indentUnit = unit;
                            unitFound = true;
                        }
                    }
                }
            }
            for (var i = c.Items.Count - 1; i >= 0; i--)
            {
                if (c.Items[i].Value.IsContainer) stack.Push(c.Items[i].Value);
            }
        }
    }

    private bool IsMultiline(JsoncValue c)
    {
        if (c.Items.Count > 0) return StartsOwnLine(c.Items[0].Start);
        if (Text.AsSpan(c.Start, c.End - c.Start).IndexOfAny('\r', '\n') >= 0) return true;
        // Пустой {} — многострочно, если это корень или сам документ многострочный.
        if (ReferenceEquals(c, _root)) return true;
        return _root is not null && Text.AsSpan(_root.Start, _root.End - _root.Start).IndexOfAny('\r', '\n') >= 0;
    }

    private string ItemIndent(JsoncValue c)
    {
        if (c.Items.Count > 0 && StartsOwnLine(c.Items[^1].Start)) return LineIndent(c.Items[^1].Start);
        return LineIndent(c.Start) + _indentUnit;
    }

    private int LineStart(int pos)
    {
        var i = pos;
        while (i > 0 && Text[i - 1] != '\n' && Text[i - 1] != '\r') i--;
        return i;
    }

    private string LineIndent(int pos)
    {
        var ls = LineStart(pos);
        var i = ls;
        while (i < Text.Length && Text[i] is ' ' or '\t') i++;
        return Text[ls..i];
    }

    private bool StartsOwnLine(int pos)
    {
        var ls = LineStart(pos);
        return IsWhitespace(Text.AsSpan(ls, pos - ls));
    }

    /// <summary>Конец «хвоста» строки после pos: пробелы, однострочные /* */ и // пропускаются.</summary>
    private int SameLineTriviaEnd(int pos)
    {
        var i = pos;
        while (i < Text.Length)
        {
            var c = Text[i];
            if (c is ' ' or '\t')
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < Text.Length && Text[i + 1] == '/')
            {
                while (i < Text.Length && Text[i] != '\r' && Text[i] != '\n') i++;
                return i;
            }
            if (c == '/' && i + 1 < Text.Length && Text[i + 1] == '*')
            {
                var end = Text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0 || Text.AsSpan(i, end - i).IndexOfAny('\r', '\n') >= 0) return i;
                i = end + 2;
                continue;
            }
            return i;
        }
        return i;
    }

    private bool IsLineBreakAt(int q) => q < Text.Length && Text[q] is '\r' or '\n';

    private static bool IsWhitespace(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (c is not (' ' or '\t' or '\r' or '\n')) return false;
        }
        return true;
    }
}
