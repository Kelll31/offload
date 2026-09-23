using System.Text;

namespace Offload.Integrations.Editing;

/// <summary>Ошибка разбора JSON/JSONC с позицией (строки и столбцы с 1).</summary>
internal sealed class JsoncParseException(string reason, int line, int column)
    : Exception(L.F("{0} (строка {1}, столбец {2})", reason, line, column))
{
    public string Reason { get; } = reason;
    public int Line { get; } = line;
    public int Column { get; } = column;
}

internal enum JsoncKind { Object, Array, String, Number, True, False, Null }

/// <summary>Значение в тексте: границы [Start, End) и (для контейнеров) элементы.</summary>
internal sealed class JsoncValue
{
    public JsoncKind Kind { get; init; }
    public int Start { get; init; }
    public int End { get; set; }
    public List<JsoncItem> Items { get; } = [];

    /// <summary>Раскодированная строка (для String).</summary>
    public string? StringValue { get; init; }

    public bool IsContainer => Kind is JsoncKind.Object or JsoncKind.Array;
}

/// <summary>Член объекта или элемент массива.</summary>
internal sealed class JsoncItem
{
    /// <summary>Начало ключа (член объекта) или значения (элемент массива).</summary>
    public int Start { get; init; }
    public string? Key { get; init; }
    public int ColonPos { get; init; } = -1;
    public required JsoncValue Value { get; init; }

    /// <summary>Позиция запятой после значения (-1, если её нет).</summary>
    public int CommaPos { get; set; } = -1;
}

/// <summary>
/// Разбор JSONC с сохранением позиций: строки с экранированием, комментарии // и /* */,
/// висячие запятые. Не допускает ничего, что отверг бы System.Text.Json в «мягком» режиме.
/// </summary>
internal sealed class JsoncParser
{
    public const int MaxDepth = 256;

    private readonly string _s;
    private int _i;

    public bool HasComments { get; private set; }
    public bool HasTrailingCommas { get; private set; }

    private JsoncParser(string text) => _s = text;

    public static JsoncParser Parse(string text, out JsoncValue? root)
    {
        var p = new JsoncParser(text);
        p.SkipTrivia();
        if (p._i >= p._s.Length)
        {
            root = null;
            return p;
        }
        root = p.ParseValue(0);
        p.SkipTrivia();
        if (p._i < p._s.Length) throw p.Error(L.T("лишние символы после конца JSON"));
        return p;
    }

    private JsoncParseException Error(string reason) => Error(reason, _i);

    private JsoncParseException Error(string reason, int pos)
    {
        var line = 1;
        var col = 1;
        for (var k = 0; k < pos && k < _s.Length; k++)
        {
            if (_s[k] == '\n') { line++; col = 1; }
            else if (_s[k] != '\r') col++;
        }
        return new JsoncParseException(reason, line, col);
    }

    private void SkipTrivia()
    {
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                _i++;
            }
            else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
            {
                HasComments = true;
                _i += 2;
                while (_i < _s.Length && _s[_i] != '\n' && _s[_i] != '\r') _i++;
            }
            else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
            {
                HasComments = true;
                var end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                if (end < 0) throw Error(L.T("незакрытый комментарий /*"));
                _i = end + 2;
            }
            else
            {
                return;
            }
        }
    }

    private JsoncValue ParseValue(int depth)
    {
        SkipTrivia();
        if (_i >= _s.Length) throw Error(L.T("неожиданный конец файла"));
        var c = _s[_i];
        return c switch
        {
            '{' => ParseObject(depth),
            '[' => ParseArray(depth),
            '"' => ParseStringValue(),
            't' => ParseLiteral("true", JsoncKind.True),
            'f' => ParseLiteral("false", JsoncKind.False),
            'n' => ParseLiteral("null", JsoncKind.Null),
            '-' or (>= '0' and <= '9') => ParseNumber(),
            _ => throw Error(L.F("неожиданный символ «{0}»", c)),
        };
    }

    private JsoncValue ParseObject(int depth)
    {
        if (depth >= MaxDepth) throw Error(L.T("слишком глубокая вложенность"));
        var obj = new JsoncValue { Kind = JsoncKind.Object, Start = _i };
        _i++;
        var afterComma = false;
        while (true)
        {
            SkipTrivia();
            if (_i >= _s.Length) throw Error(L.T("незакрытый объект «{»"));
            var c = _s[_i];
            if (c == '}')
            {
                if (afterComma) HasTrailingCommas = true;
                _i++;
                obj.End = _i;
                return obj;
            }
            if (c != '"') throw Error(afterComma || obj.Items.Count == 0 ? L.T("ожидалось имя свойства в кавычках") : L.T("ожидалась «,» или «}»"));
            if (obj.Items.Count > 0 && !afterComma) throw Error(L.T("ожидалась «,» или «}»"));

            var keyStart = _i;
            var key = ParseString();
            SkipTrivia();
            if (_i >= _s.Length || _s[_i] != ':') throw Error(L.T("ожидалось «:» после имени свойства"));
            var colon = _i;
            _i++;
            var value = ParseValue(depth + 1);
            var item = new JsoncItem { Start = keyStart, Key = key, ColonPos = colon, Value = value };
            obj.Items.Add(item);

            SkipTrivia();
            if (_i >= _s.Length) throw Error(L.T("незакрытый объект «{»"));
            if (_s[_i] == ',')
            {
                item.CommaPos = _i;
                _i++;
                afterComma = true;
            }
            else if (_s[_i] == '}')
            {
                afterComma = false;
            }
            else
            {
                throw Error(L.T("ожидалась «,» или «}»"));
            }
        }
    }

    private JsoncValue ParseArray(int depth)
    {
        if (depth >= MaxDepth) throw Error(L.T("слишком глубокая вложенность"));
        var arr = new JsoncValue { Kind = JsoncKind.Array, Start = _i };
        _i++;
        var afterComma = false;
        while (true)
        {
            SkipTrivia();
            if (_i >= _s.Length) throw Error(L.T("незакрытый массив «[»"));
            if (_s[_i] == ']')
            {
                if (afterComma) HasTrailingCommas = true;
                _i++;
                arr.End = _i;
                return arr;
            }
            if (_s[_i] == ',') throw Error(L.T("лишняя запятая"));
            if (arr.Items.Count > 0 && !afterComma) throw Error(L.T("ожидалась «,» или «]»"));

            var start = _i;
            var value = ParseValue(depth + 1);
            var item = new JsoncItem { Start = start, Value = value };
            arr.Items.Add(item);

            SkipTrivia();
            if (_i >= _s.Length) throw Error(L.T("незакрытый массив «[»"));
            if (_s[_i] == ',')
            {
                item.CommaPos = _i;
                _i++;
                afterComma = true;
            }
            else if (_s[_i] == ']')
            {
                afterComma = false;
            }
            else
            {
                throw Error(L.T("ожидалась «,» или «]»"));
            }
        }
    }

    private JsoncValue ParseStringValue()
    {
        var start = _i;
        var s = ParseString();
        return new JsoncValue { Kind = JsoncKind.String, Start = start, End = _i, StringValue = s };
    }

    private string ParseString()
    {
        var start = _i;
        _i++; // открывающая кавычка
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) throw Error(L.T("незакрытая строка"), start);
            var c = _s[_i];
            if (c == '"')
            {
                _i++;
                return sb.ToString();
            }
            if (c < ' ') throw Error(L.T("управляющий символ внутри строки (нужно экранирование)"));
            if (c != '\\')
            {
                sb.Append(c);
                _i++;
                continue;
            }
            if (_i + 1 >= _s.Length) throw Error(L.T("незакрытая строка"), start);
            var e = _s[_i + 1];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (_i + 6 > _s.Length) throw Error(L.T("неполная последовательность \\u"));
                    var hex = _s.AsSpan(_i + 2, 4);
                    if (!ushort.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var code))
                        throw Error(L.T("некорректная последовательность \\u"));
                    sb.Append((char)code);
                    _i += 4;
                    break;
                default:
                    throw Error(L.F("недопустимое экранирование «\\{0}»", e));
            }
            _i += 2;
        }
    }

    private JsoncValue ParseLiteral(string word, JsoncKind kind)
    {
        if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error(L.T("неизвестное значение"));
        var start = _i;
        _i += word.Length;
        if (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) throw Error(L.T("неизвестное значение"), start);
        return new JsoncValue { Kind = kind, Start = start, End = _i };
    }

    private JsoncValue ParseNumber()
    {
        var start = _i;
        if (_s[_i] == '-') _i++;
        if (_i >= _s.Length) throw Error(L.T("некорректное число"), start);
        if (_s[_i] == '0')
        {
            _i++;
        }
        else if (_s[_i] is >= '1' and <= '9')
        {
            while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
        }
        else
        {
            throw Error(L.T("некорректное число"), start);
        }
        if (_i < _s.Length && _s[_i] == '.')
        {
            _i++;
            if (_i >= _s.Length || !char.IsAsciiDigit(_s[_i])) throw Error(L.T("некорректное число"), start);
            while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
        }
        if (_i < _s.Length && _s[_i] is 'e' or 'E')
        {
            _i++;
            if (_i < _s.Length && _s[_i] is '+' or '-') _i++;
            if (_i >= _s.Length || !char.IsAsciiDigit(_s[_i])) throw Error(L.T("некорректное число"), start);
            while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
        }
        if (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '.')) throw Error(L.T("некорректное число"), start);
        return new JsoncValue { Kind = JsoncKind.Number, Start = start, End = _i };
    }
}
