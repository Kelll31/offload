using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Offload.Integrations.Editing;

/// <summary>
/// Независимая («оракульная») проверка правок: разбор через System.Text.Json и каноническое сравнение деревьев.
/// Дубликаты ключей — «побеждает последний», как в JSON.parse.
/// </summary>
internal static class JsonTree
{
    public static readonly JsonDocumentOptions OracleOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = JsoncParser.MaxDepth + 8,
    };

    /// <summary>Кодировщик для записи: кириллица и прочие символы как есть, экранируются только обязательные.</summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = Encoder };

    /// <summary>Разобрать текст через System.Text.Json. Пустой текст (только комментарии/пробелы) → (true, null).</summary>
    public static bool TryParseOracle(string text, out JsonNode? root)
    {
        root = null;
        if (IsBlankOrComments(text)) return true;
        try
        {
            using var doc = JsonDocument.Parse(text, OracleOptions);
            root = FromElement(doc.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsBlankOrComments(string text)
    {
        try
        {
            JsoncParser.Parse(text, out var root);
            return root is null;
        }
        catch (JsoncParseException)
        {
            return false;
        }
    }

    public static JsonNode? FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var p in e.EnumerateObject()) obj[p.Name] = FromElement(p.Value);
                return obj;
            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in e.EnumerateArray()) arr.Add(FromElement(item));
                return arr;
            case JsonValueKind.String:
                return JsonValue.Create(e.GetString()!);
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Null:
                return null;
            default:
                return JsonNode.Parse(e.GetRawText());
        }
    }

    /// <summary>Узел из дерева нашего разборщика (строки раскодированы, числа — исходный текст).</summary>
    public static JsonNode? FromJsonc(string text, JsoncValue v)
    {
        switch (v.Kind)
        {
            case JsoncKind.Object:
                var obj = new JsonObject();
                foreach (var it in v.Items) obj[it.Key!] = FromJsonc(text, it.Value);
                return obj;
            case JsoncKind.Array:
                var arr = new JsonArray();
                foreach (var it in v.Items) arr.Add(FromJsonc(text, it.Value));
                return arr;
            case JsoncKind.String:
                return JsonValue.Create(v.StringValue!);
            case JsoncKind.True:
                return JsonValue.Create(true);
            case JsoncKind.False:
                return JsonValue.Create(false);
            case JsoncKind.Null:
                return null;
            default:
                return JsonNode.Parse(text[v.Start..v.End]);
        }
    }

    /// <summary>Каноническая строка: ключи отсортированы, строки экранированы единообразно, числа — как в тексте.</summary>
    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    public static bool SemanticEquals(JsonNode? a, JsonNode? b) => Canonical(a) == Canonical(b);

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(k)).Append(':');
                    Write(sb, v);
                }
                sb.Append('}');
                return;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, a[i]);
                }
                sb.Append(']');
                return;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) sb.Append(Quote(s));
                else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else sb.Append(v.ToJsonString());
                return;
        }
    }

    public static string Quote(string s) => JsonSerializer.Serialize(s, StringOptions);

    /// <summary>Строковое значение узла (или null, если это не строка).</summary>
    public static string? AsString(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Массив строк (null, если это не массив строк).</summary>
    public static List<string>? AsStringList(JsonNode? n)
    {
        if (n is not JsonArray a) return null;
        var list = new List<string>(a.Count);
        foreach (var item in a)
        {
            var s = AsString(item);
            if (s is null) return null;
            list.Add(s);
        }
        return list;
    }

    /// <summary>Строковые элементы массива (прочие элементы пропускаются); не массив — пустой список.</summary>
    public static List<string> AsStringListLenient(JsonNode? n) =>
        n is JsonArray a ? a.Select(AsString).Where(s => s is not null).Select(s => s!).ToList() : [];

    // ---- Те же операции над деревом JsonNode (ожидаемый результат для проверки правки) ----

    /// <summary>Установить значение по пути, создавая промежуточные объекты (null заменяется объектом).</summary>
    public static JsonNode ApplySet(JsonNode? root, IReadOnlyList<string> path, JsonNode? value)
    {
        root ??= new JsonObject();
        if (root is not JsonObject cur) throw new JsoncEditException("корень файла не является объектом");
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (!cur.TryGetPropertyValue(path[i], out var child) || child is null)
            {
                child = new JsonObject();
                cur[path[i]] = child;
            }
            if (child is not JsonObject next) throw new JsoncEditException($"«{path[i]}» не является объектом");
            cur = next;
        }
        cur[path[^1]] = value?.DeepClone();
        return root;
    }

    public static JsonNode? ApplyRemove(JsonNode? root, IReadOnlyList<string> path)
    {
        var parent = Navigate(root, path.Take(path.Count - 1)) as JsonObject;
        parent?.Remove(path[^1]);
        return root;
    }

    public static JsonNode ApplyAppend(JsonNode? root, IReadOnlyList<string> path, IEnumerable<JsonNode?> values)
    {
        var existing = Navigate(root, path);
        if (existing is JsonArray arr)
        {
            foreach (var v in values) arr.Add(v?.DeepClone());
            return root!;
        }
        return ApplySet(root, path, new JsonArray(values.Select(v => v?.DeepClone()).ToArray()));
    }

    public static JsonNode? ApplyRemoveWhere(JsonNode? root, IReadOnlyList<string> path, Func<JsonNode?, bool> predicate)
    {
        if (Navigate(root, path) is not JsonArray arr) return root;
        for (var i = arr.Count - 1; i >= 0; i--)
        {
            if (predicate(arr[i])) arr.RemoveAt(i);
        }
        return root;
    }

    public static JsonNode? Navigate(JsonNode? root, IEnumerable<string> path)
    {
        var cur = root;
        foreach (var key in path)
        {
            if (cur is not JsonObject o || !o.TryGetPropertyValue(key, out var next)) return null;
            cur = next;
        }
        return cur;
    }
}
