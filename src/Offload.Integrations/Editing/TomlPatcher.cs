using System.Globalization;
using System.Text;

namespace Offload.Integrations.Editing;

/// <summary>Ошибка разбора TOML или невозможность безопасной правки.</summary>
internal sealed class TomlPatchException(string message) : Exception(message);

internal enum TomlStatementKind { Table, ArrayTable, KeyValue }

/// <summary>Инструкция TOML: заголовок таблицы или «ключ = значение». [Start, End) — целые строки (с отступом и переводом строки).</summary>
internal sealed class TomlStatement
{
    public TomlStatementKind Kind { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public IReadOnlyList<string> Key { get; init; } = [];
    public int ValueStart { get; init; } = -1;
    public int ValueEnd { get; init; } = -1;
}

/// <summary>
/// Минимальный построчный разбор TOML (заголовки, ключи, строки всех 4 видов, многострочные массивы,
/// встроенные таблицы, комментарии) и точечная правка одной таблицы [a.b] с её подтаблицами.
/// Весь остальной текст (включая комментарии) сохраняется без изменений.
/// </summary>
internal sealed class TomlDocument
{
    private readonly string _s;
    private int _i;
    private readonly List<TomlStatement> _statements = [];

    public string Text => _s;
    public IReadOnlyList<TomlStatement> Statements => _statements;
    public string NewLine { get; }

    private TomlDocument(string text)
    {
        _s = text;
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        var lf = text.IndexOf('\n');
        NewLine = crlf >= 0 && crlf + 1 == lf ? "\r\n" : "\n";
    }

    /// <exception cref="TomlPatchException">Синтаксическая ошибка.</exception>
    public static TomlDocument Parse(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var d = new TomlDocument(text);
        d.ParseAll();
        return d;
    }

    // ------------------------------------------------------------------ разбор

    private TomlPatchException Error(string reason)
    {
        var line = 1;
        for (var k = 0; k < _i && k < _s.Length; k++)
        {
            if (_s[k] == '\n') line++;
        }
        return new TomlPatchException($"{reason} (строка {line})");
    }

    private bool Eof => _i >= _s.Length;

    private void SkipSpaces()
    {
        while (!Eof && _s[_i] is ' ' or '\t') _i++;
    }

    private bool AtLineBreak() => !Eof && (_s[_i] == '\n' || (_s[_i] == '\r' && _i + 1 < _s.Length && _s[_i + 1] == '\n'));

    private void ConsumeLineBreak()
    {
        if (Eof) return;
        if (_s[_i] == '\r') _i += 2;
        else _i++;
    }

    private void SkipComment()
    {
        if (!Eof && _s[_i] == '#')
        {
            while (!Eof && _s[_i] != '\n' && _s[_i] != '\r') _i++;
        }
    }

    /// <summary>После инструкции: пробелы, комментарий, конец строки.</summary>
    private void ExpectLineEnd()
    {
        SkipSpaces();
        SkipComment();
        if (Eof) return;
        if (!AtLineBreak()) throw Error("ожидался конец строки");
        ConsumeLineBreak();
    }

    private void ParseAll()
    {
        while (!Eof)
        {
            var lineStart = _i;
            SkipSpaces();
            if (Eof) break;
            if (AtLineBreak())
            {
                ConsumeLineBreak();
                continue;
            }
            if (_s[_i] == '#')
            {
                SkipComment();
                if (!Eof)
                {
                    if (!AtLineBreak()) throw Error("ожидался конец строки");
                    ConsumeLineBreak();
                }
                continue;
            }
            if (_s[_i] == '[')
            {
                var isArray = _i + 1 < _s.Length && _s[_i + 1] == '[';
                _i += isArray ? 2 : 1;
                var key = ParseKey();
                SkipSpaces();
                if (Eof || _s[_i] != ']') throw Error("ожидалась «]» в заголовке таблицы");
                _i++;
                if (isArray)
                {
                    if (Eof || _s[_i] != ']') throw Error("ожидалась «]]» в заголовке массива таблиц");
                    _i++;
                }
                ExpectLineEnd();
                _statements.Add(new TomlStatement
                {
                    Kind = isArray ? TomlStatementKind.ArrayTable : TomlStatementKind.Table,
                    Start = lineStart,
                    End = _i,
                    Key = key,
                });
                continue;
            }

            var k = ParseKey();
            SkipSpaces();
            if (Eof || _s[_i] != '=') throw Error("ожидался знак «=»");
            _i++;
            SkipSpaces();
            var vs = _i;
            ParseValue();
            var ve = _i;
            ExpectLineEnd();
            _statements.Add(new TomlStatement
            {
                Kind = TomlStatementKind.KeyValue,
                Start = lineStart,
                End = _i,
                Key = k,
                ValueStart = vs,
                ValueEnd = ve,
            });
        }
    }

    private List<string> ParseKey()
    {
        var parts = new List<string>();
        while (true)
        {
            SkipSpaces();
            if (Eof) throw Error("ожидался ключ");
            var c = _s[_i];
            if (c == '"')
            {
                if (_s.AsSpan(_i).StartsWith("\"\"\"")) throw Error("многострочная строка не может быть ключом");
                parts.Add(ReadBasicString());
            }
            else if (c == '\'')
            {
                if (_s.AsSpan(_i).StartsWith("'''")) throw Error("многострочная строка не может быть ключом");
                parts.Add(ReadLiteralString());
            }
            else
            {
                var start = _i;
                while (!Eof && (char.IsAsciiLetterOrDigit(_s[_i]) || _s[_i] is '_' or '-')) _i++;
                if (_i == start) throw Error("некорректный ключ");
                parts.Add(_s[start.._i]);
            }
            SkipSpaces();
            if (!Eof && _s[_i] == '.')
            {
                _i++;
                continue;
            }
            return parts;
        }
    }

    private void ParseValue()
    {
        if (Eof) throw Error("ожидалось значение");
        var c = _s[_i];
        if (c == '"')
        {
            if (_s.AsSpan(_i).StartsWith("\"\"\"")) SkipMultiline('"');
            else ReadBasicString();
            return;
        }
        if (c == '\'')
        {
            if (_s.AsSpan(_i).StartsWith("'''")) SkipMultiline('\'');
            else ReadLiteralString();
            return;
        }
        if (c == '[')
        {
            _i++;
            while (true)
            {
                SkipArrayTrivia();
                if (Eof) throw Error("незакрытый массив");
                if (_s[_i] == ']')
                {
                    _i++;
                    return;
                }
                ParseValue();
                SkipArrayTrivia();
                if (Eof) throw Error("незакрытый массив");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (_s[_i] == ']')
                {
                    _i++;
                    return;
                }
                throw Error("ожидалась «,» или «]» в массиве");
            }
        }
        if (c == '{')
        {
            _i++;
            var first = true;
            while (true)
            {
                SkipArrayTrivia();
                if (Eof) throw Error("незакрытая встроенная таблица");
                if (_s[_i] == '}')
                {
                    _i++;
                    return;
                }
                if (!first)
                {
                    if (_s[_i] != ',') throw Error("ожидалась «,» во встроенной таблице");
                    _i++;
                    SkipArrayTrivia();
                    if (!Eof && _s[_i] == '}')
                    {
                        _i++;
                        return;
                    }
                }
                first = false;
                ParseKey();
                SkipSpaces();
                if (Eof || _s[_i] != '=') throw Error("ожидался знак «=» во встроенной таблице");
                _i++;
                SkipSpaces();
                ParseValue();
            }
        }

        // Скаляр: число, дата/время, true/false, inf/nan.
        var start = _i;
        while (!Eof && _s[_i] is not (',' or ']' or '}' or '#' or '\r' or '\n')) _i++;
        while (_i > start && _s[_i - 1] is ' ' or '\t') _i--;
        if (_i == start) throw Error("ожидалось значение");
        var token = _s[start.._i];
        if (token.Contains('"') || token.Contains('\'') || token.Contains('=') || token.Contains('[') || token.Contains('{'))
            throw Error("некорректное значение");
    }

    /// <summary>Внутри массивов/встроенных таблиц допускаются переводы строк и комментарии.</summary>
    private void SkipArrayTrivia()
    {
        while (!Eof)
        {
            var c = _s[_i];
            if (c is ' ' or '\t' or '\n') _i++;
            else if (c == '\r' && _i + 1 < _s.Length && _s[_i + 1] == '\n') _i += 2;
            else if (c == '#') SkipComment();
            else return;
        }
    }

    private string ReadBasicString()
    {
        _i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (Eof || _s[_i] is '\n' or '\r') throw Error("незакрытая строка");
            var c = _s[_i];
            if (c == '"')
            {
                _i++;
                return sb.ToString();
            }
            if (c == '\\')
            {
                AppendEscape(sb);
                continue;
            }
            sb.Append(c);
            _i++;
        }
    }

    private void AppendEscape(StringBuilder sb)
    {
        if (_i + 1 >= _s.Length) throw Error("незакрытая строка");
        var e = _s[_i + 1];
        _i += 2;
        switch (e)
        {
            case 'b': sb.Append('\b'); break;
            case 't': sb.Append('\t'); break;
            case 'n': sb.Append('\n'); break;
            case 'f': sb.Append('\f'); break;
            case 'r': sb.Append('\r'); break;
            case 'e': sb.Append('\u001b'); break;
            case '"': sb.Append('"'); break;
            case '\\': sb.Append('\\'); break;
            case 'x': sb.Append(ReadHex(2)); break;
            case 'u': sb.Append(ReadHex(4)); break;
            case 'U': sb.Append(ReadHex(8)); break;
            default: throw Error($"недопустимое экранирование «\\{e}»");
        }
    }

    private string ReadHex(int n)
    {
        if (_i + n > _s.Length) throw Error("неполная escape-последовательность");
        if (!int.TryParse(_s.AsSpan(_i, n), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) ||
            code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
            throw Error("некорректная escape-последовательность");
        _i += n;
        return char.ConvertFromUtf32(code);
    }

    private string ReadLiteralString()
    {
        _i++;
        var start = _i;
        while (true)
        {
            if (Eof || _s[_i] is '\n' or '\r') throw Error("незакрытая строка");
            if (_s[_i] == '\'')
            {
                var v = _s[start.._i];
                _i++;
                return v;
            }
            _i++;
        }
    }

    private void SkipMultiline(char q)
    {
        var delim = new string(q, 3);
        _i += 3;
        while (true)
        {
            if (Eof) throw Error("незакрытая многострочная строка");
            if (q == '"' && _s[_i] == '\\')
            {
                _i += 2;
                continue;
            }
            if (_s.AsSpan(_i).StartsWith(delim))
            {
                // Допускается до двух кавычек перед закрывающими тремя.
                var n = 0;
                while (_i + n < _s.Length && _s[_i + n] == q && n < 5) n++;
                _i += n;
                return;
            }
            _i++;
        }
    }

    // ------------------------------------------------------------------ чтение значений

    /// <summary>Раскодировать строковое значение (любой из 4 видов). null — это не строка.</summary>
    public string? ReadString(TomlStatement kv)
    {
        if (kv.Kind != TomlStatementKind.KeyValue) return null;
        return DecodeString(_s[kv.ValueStart..kv.ValueEnd]);
    }

    /// <summary>Массив строк. null — это не массив строк.</summary>
    public List<string>? ReadStringArray(TomlStatement kv)
    {
        if (kv.Kind != TomlStatementKind.KeyValue) return null;
        var raw = _s[kv.ValueStart..kv.ValueEnd];
        if (!raw.StartsWith('[')) return null;
        var list = new List<string>();
        var p = new TomlDocument(raw) { _i = 1 };
        while (true)
        {
            p.SkipArrayTrivia();
            if (p.Eof) return null;
            if (p._s[p._i] == ']') return list;
            var vs = p._i;
            try
            {
                p.ParseValue();
            }
            catch (TomlPatchException)
            {
                return null;
            }
            var s = DecodeString(raw[vs..p._i]);
            if (s is null) return null;
            list.Add(s);
            p.SkipArrayTrivia();
            if (p.Eof) return null;
            if (p._s[p._i] == ',') p._i++;
        }
    }

    private static string? DecodeString(string raw)
    {
        if (raw.StartsWith("'''"))
        {
            if (raw.Length < 6) return null;
            var body = raw[3..^3];
            return StripFirstNewline(body);
        }
        if (raw.StartsWith('\'') && raw.Length >= 2 && raw.EndsWith('\'')) return raw[1..^1];
        if (raw.StartsWith("\"\"\""))
        {
            if (raw.Length < 6) return null;
            var body = StripFirstNewline(raw[3..^3]);
            // Обратный слеш в конце строки «склеивает» строки.
            var sb = new StringBuilder();
            var d = new TomlDocument(body);
            while (!d.Eof)
            {
                var c = d._s[d._i];
                if (c == '\\')
                {
                    var j = d._i + 1;
                    while (j < body.Length && body[j] is ' ' or '\t') j++;
                    if (j < body.Length && body[j] is '\n' or '\r')
                    {
                        while (j < body.Length && body[j] is ' ' or '\t' or '\n' or '\r') j++;
                        d._i = j;
                        continue;
                    }
                    try { d.AppendEscape(sb); } catch (TomlPatchException) { return null; }
                    continue;
                }
                sb.Append(c);
                d._i++;
            }
            return sb.ToString();
        }
        if (raw.StartsWith('"'))
        {
            var d = new TomlDocument(raw);
            try
            {
                var s = d.ReadBasicString();
                return d._i == raw.Length ? s : null;
            }
            catch (TomlPatchException)
            {
                return null;
            }
        }
        return null;
    }

    private static string StripFirstNewline(string body) =>
        body.StartsWith("\r\n") ? body[2..] : body.StartsWith('\n') ? body[1..] : body;

    // ------------------------------------------------------------------ запись значений

    /// <summary>Строка TOML: буквальная '...' (обратные слеши без экранирования), если возможно, иначе "..." с экранированием.</summary>
    public static string FormatString(string value)
    {
        if (!value.Contains('\'') && !value.Any(c => c < ' ' || c == '\u007f')) return "'" + value + "'";
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ' || c == '\u007f') sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Ключ TOML: голый, если допустимо, иначе в кавычках.</summary>
    public static string FormatKey(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : FormatBasic(key);

    private static string FormatBasic(string value)
    {
        var f = FormatString(value);
        return f.StartsWith('"') ? f : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    public static string FormatStringArray(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(FormatBasic)) + "]";
}

/// <summary>Анализ и правка одной таблицы (например, [mcp_servers.offload]) вместе с подтаблицами.</summary>
internal sealed class TomlTablePatcher
{
    private readonly TomlDocument _doc;
    private readonly IReadOnlyList<string> _table;

    /// <summary>Инструкции основной таблицы (заголовок — первым), или null.</summary>
    public List<TomlStatement>? Main { get; private set; }

    /// <summary>Диапазоны всех «наших» секций (основная и подтаблицы) в порядке следования.</summary>
    public List<(int Start, int End, bool IsMain)> Ranges { get; } = [];

    /// <summary>Причина, по которой файл нельзя безопасно править (таблица задана встроенно или точечными ключами).</summary>
    public string? Unsupported { get; private set; }

    /// <summary>Конец последней секции с тем же первым сегментом (для вставки рядом), или -1.</summary>
    public int SiblingEnd { get; private set; } = -1;

    public TomlDocument Document => _doc;

    public TomlTablePatcher(TomlDocument doc, IReadOnlyList<string> table)
    {
        _doc = doc;
        _table = table;
        Analyze();
    }

    private bool IsOurs(IReadOnlyList<string> key) =>
        key.Count >= _table.Count && _table.Select((t, i) => key[i] == t).All(x => x);

    private void Analyze()
    {
        var st = _doc.Statements;
        IReadOnlyList<string> current = [];
        var sectionIsOurs = false;
        var sectionIsMain = false;
        var sectionStart = 0;
        var sectionEnd = 0;
        var sectionHasSibling = false;

        void Close()
        {
            if (sectionIsOurs) Ranges.Add((sectionStart, sectionEnd, sectionIsMain));
            if (sectionHasSibling && !sectionIsOurs) SiblingEnd = Math.Max(SiblingEnd, sectionEnd);
        }

        for (var i = 0; i < st.Count; i++)
        {
            var s = st[i];
            if (s.Kind != TomlStatementKind.KeyValue)
            {
                Close();
                current = s.Key;
                sectionIsOurs = IsOurs(s.Key);
                sectionIsMain = sectionIsOurs && s.Key.Count == _table.Count && s.Kind == TomlStatementKind.Table;
                sectionHasSibling = s.Key.Count > 0 && s.Key[0] == _table[0];
                sectionStart = s.Start;
                sectionEnd = s.End;
                // [[mcp_servers]] или [[mcp_servers.offload]]: добавление обычной таблицы сделало бы TOML некорректным.
                if (s.Kind == TomlStatementKind.ArrayTable &&
                    (sectionIsOurs || (s.Key.Count < _table.Count && _table.Take(s.Key.Count).SequenceEqual(s.Key))))
                    Unsupported = $"«{string.Join('.', s.Key)}» задан массивом таблиц [[...]]";
                if (sectionIsMain)
                {
                    if (Main is not null) Unsupported = "таблица объявлена дважды";
                    Main = [s];
                }
                continue;
            }

            sectionEnd = s.End;
            if (sectionIsMain) Main!.Add(s);
            if (sectionIsOurs) continue;

            // Полный путь ключа с учётом текущей таблицы.
            var full = current.Concat(s.Key).ToList();
            if (IsOurs(full))
                Unsupported = "запись задана точечными ключами или встроенной таблицей";
            else if (full.Count > 0 && full.Count < _table.Count && _table.Take(full.Count).SequenceEqual(full))
                Unsupported = $"«{string.Join('.', full)}» задан встроенной таблицей";
        }
        Close();
    }

    /// <summary>Значение ключа основной таблицы (одиночный ключ, без точек).</summary>
    public TomlStatement? MainValue(string key) =>
        Main?.Skip(1).LastOrDefault(s => s.Key.Count == 1 && s.Key[0] == key);

    /// <summary>
    /// Записать основную таблицу: ключи из <paramref name="owned"/> пишутся всегда, из <paramref name="defaults"/> —
    /// только если их ещё нет; прочие ключи и подтаблицы пользователя сохраняются как есть.
    /// </summary>
    public string SetTable(IReadOnlyList<(string Key, string Value)> owned, IReadOnlyList<(string Key, string Value)> defaults)
    {
        if (Unsupported is not null) throw new TomlPatchException(Unsupported);
        var nl = _doc.NewLine;
        var text = _doc.Text;
        var header = "[" + string.Join('.', _table.Select(TomlDocument.FormatKey)) + "]";

        var sb = new StringBuilder();
        sb.Append(header).Append(nl);
        foreach (var (k, v) in owned) sb.Append(TomlDocument.FormatKey(k)).Append(" = ").Append(v).Append(nl);

        var ownedKeys = owned.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        if (Main is not null)
        {
            foreach (var s in Main.Skip(1))
            {
                if (s.Key.Count >= 1) present.Add(s.Key[0]);
                if (s.Key.Count == 1 && ownedKeys.Contains(s.Key[0])) continue;
                var line = text[s.Start..s.End];
                sb.Append(line);
                if (!line.EndsWith('\n')) sb.Append(nl);
            }
        }
        foreach (var (k, v) in defaults)
        {
            if (!present.Contains(k) && !ownedKeys.Contains(k)) sb.Append(TomlDocument.FormatKey(k)).Append(" = ").Append(v).Append(nl);
        }
        var table = sb.ToString();

        var main = Ranges.FirstOrDefault(r => r.IsMain);
        if (Main is not null) return text[..main.Start] + table + text[main.End..];

        if (Ranges.Count > 0)
        {
            // Есть только подтаблицы — основную ставим перед первой из них.
            var first = Ranges[0].Start;
            return text[..first] + table + nl + text[first..];
        }

        if (SiblingEnd >= 0)
        {
            var prefix = text[..SiblingEnd];
            if (!prefix.EndsWith('\n')) prefix += nl;
            var rest = text[SiblingEnd..];
            var suffix = rest.Length > 0 && !rest.StartsWith(nl) ? nl : "";
            return prefix + nl + table + suffix + rest;
        }

        if (text.Trim().Length == 0) return table;
        var body = text.EndsWith('\n') ? text : text + nl;
        if (!body.EndsWith(nl + nl) && !(body == nl)) body += nl;
        return body + table;
    }

    /// <summary>Удалить основную таблицу и все её подтаблицы.</summary>
    public string RemoveTable()
    {
        if (Unsupported is not null) throw new TomlPatchException(Unsupported);
        var text = _doc.Text;
        if (Ranges.Count == 0) return text;
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var (start, end, _) in Ranges)
        {
            var a = start;
            var b = end;
            // Убираем лишние пустые строки на стыке.
            if (IsBlankLineBefore(text, a) || a == 0)
            {
                while (b < text.Length && IsBlankLineAt(text, b, out var next)) b = next;
            }
            if (b >= text.Length)
            {
                while (a > pos && IsBlankLineBefore(text, a)) a = PrevLineStart(text, a);
            }
            if (a < pos) a = pos;
            sb.Append(text, pos, a - pos);
            pos = b;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    private static bool IsBlankLineAt(string text, int pos, out int next)
    {
        var i = pos;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        if (i < text.Length && text[i] == '\n') { next = i + 1; return true; }
        if (i + 1 < text.Length && text[i] == '\r' && text[i + 1] == '\n') { next = i + 2; return true; }
        next = pos;
        return false;
    }

    private static int PrevLineStart(string text, int lineStart)
    {
        var i = lineStart - 1; // '\n' предыдущей строки
        if (i > 0 && text[i - 1] == '\r') i--;
        while (i > 0 && text[i - 1] != '\n') i--;
        return i;
    }

    private static bool IsBlankLineBefore(string text, int lineStart)
    {
        if (lineStart == 0) return false;
        var prev = PrevLineStart(text, lineStart);
        return IsBlankLineAt(text, prev, out var next) && next == lineStart;
    }

    /// <summary>Текст без наших секций и без пустых строк — для проверки, что чужое содержимое не изменилось.</summary>
    public string ForeignFingerprint()
    {
        var text = _doc.Text;
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var (start, end, _) in Ranges)
        {
            sb.Append(text, pos, start - pos);
            pos = end;
        }
        sb.Append(text, pos, text.Length - pos);
        var lines = sb.ToString().Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }
}
