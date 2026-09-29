using System.Text;

namespace Offload.Llama;

/// <summary>
/// Чтение ответов сервера с ограничением размера: удалённый сервер (клиентский режим) недоверенный — бесконечный ответ
/// или строка потока без перевода строки не должны съесть память трея или MCP.
/// </summary>
internal static class BoundedRead
{
    /// <summary>Потолок JSON-ответов служебных запросов (/props, /tokenize), символов.</summary>
    public const int MaxJsonChars = 4 * 1024 * 1024;

    /// <summary>Потолок ответа чата без потока (application/json), символов.</summary>
    public const int MaxChatBodyChars = 16 * 1024 * 1024;

    /// <summary>Потолок одной строки потока SSE, символов.</summary>
    public const int MaxSseLineChars = 4 * 1024 * 1024;

    /// <summary>Весь текст ответа, если он не длиннее <paramref name="maxChars"/>; иначе null (ответ отброшен).</summary>
    public static async Task<string?> ReadTextAsync(Stream stream, int maxChars, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sb = new StringBuilder();
        var buf = new char[16 * 1024];
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) return sb.ToString();
            if (sb.Length + n > maxChars) return null;
            sb.Append(buf, 0, n);
        }
    }
}

/// <summary>
/// Построчное чтение (как <see cref="TextReader.ReadLineAsync(CancellationToken)"/>: разделители \n, \r, \r\n) с потолком длины
/// строки: длиннее — <see cref="InvalidDataException"/>.
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maxLineChars)
{
    private readonly char[] _buf = new char[8 * 1024];
    private readonly StringBuilder _line = new();
    private int _pos;
    private int _len;
    private bool _skipLf;

    public async ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        _line.Clear();
        var any = false;
        while (true)
        {
            if (_pos >= _len)
            {
                _len = await reader.ReadAsync(_buf.AsMemory(), ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0) return any ? _line.ToString() : null;
            }
            if (_skipLf)
            {
                _skipLf = false;
                if (_buf[_pos] == '\n')
                {
                    _pos++;
                    continue;
                }
            }
            any = true;
            var start = _pos;
            while (_pos < _len && _buf[_pos] is not ('\n' or '\r')) _pos++;
            if (_line.Length + (_pos - start) > maxLineChars)
                throw new InvalidDataException($"a line of the response is longer than {maxLineChars} characters"); // l10n-ignore: подробность для журнала
            _line.Append(_buf, start, _pos - start);
            if (_pos < _len)
            {
                _skipLf = _buf[_pos] == '\r';
                _pos++;
                return _line.ToString();
            }
        }
    }
}
