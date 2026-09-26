using System.Text.Json;

namespace Offload.Mcp.Infrastructure;

/// <summary>Мелкие общие операции над текстом ответов (вместо копий в каждом инструменте).</summary>
internal static class TextUtil
{
    /// <summary>Обрезать строку до max символов с «…» (null → пустая строка).</summary>
    public static string Short(string? s, int max)
    {
        if (s is null) return "";
        if (s.Length <= max) return s;
        var cut = Math.Max(0, max);
        // Не рвём суррогатную пару.
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }

    /// <summary>
    /// Найти в тексте модели JSON-массив (kind = Array) или объект (kind = Object) и разобрать его. Сначала — от первой
    /// открывающей до последней закрывающей скобки (обычный случай: JSON с пояснениями вокруг), затем — первый
    /// сбалансированный фрагмент, который разбирается (лишние скобки в пояснениях после JSON). null — ничего не нашлось.
    /// Вызывающий освобождает документ.
    /// </summary>
    public static JsonDocument? ParseJsonFragment(string? text, JsonValueKind kind)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var (open, close) = kind switch
        {
            JsonValueKind.Array => ('[', ']'),
            JsonValueKind.Object => ('{', '}'),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Array or Object expected"),
        };
        var start = text.IndexOf(open, StringComparison.Ordinal);
        var end = text.LastIndexOf(close);
        if (start < 0 || end <= start) return null;
        if (TryParse(text[start..(end + 1)], kind) is { } whole) return whole;

        for (var s = start; s >= 0 && s < text.Length; s = text.IndexOf(open, s + 1))
        {
            var e = MatchingClose(text, s, open, close);
            if (e < 0) continue;
            if (TryParse(text[s..(e + 1)], kind) is { } doc) return doc;
        }
        return null;
    }

    private static JsonDocument? TryParse(string json, JsonValueKind kind)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == kind) return doc;
            doc.Dispose();
        }
        catch (JsonException)
        {
            // Не JSON — пробуем дальше.
        }
        return null;
    }

    /// <summary>Индекс парной закрывающей скобки с учётом строк JSON (кавычки, экранирование) или −1.</summary>
    private static int MatchingClose(string text, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return i;
        }
        return -1;
    }
}
