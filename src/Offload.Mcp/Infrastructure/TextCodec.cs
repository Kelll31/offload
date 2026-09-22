using System.Text;

namespace Offload.Mcp.Infrastructure;

internal enum TextEncodingKind { Utf8, Utf8Bom, Utf16LE, Utf16BE, Windows1251 }

/// <summary>Кодировка и переводы строк файла — сохраняются при перезаписи (важно для Delphi/cp1251 и CRLF).</summary>
internal sealed record TextFormat(TextEncodingKind Kind, string NewLine, bool FinalNewline)
{
    public static readonly TextFormat DefaultUtf8Lf = new(TextEncodingKind.Utf8, "\n", true);

    public string Describe() => Kind switch
    {
        TextEncodingKind.Utf8 => "utf-8",
        TextEncodingKind.Utf8Bom => "utf-8 bom",
        TextEncodingKind.Utf16LE => "utf-16le",
        TextEncodingKind.Utf16BE => "utf-16be",
        _ => "windows-1251",
    } + (NewLine == "\r\n" ? ", crlf" : ", lf");
}

/// <summary>Определение кодировки (BOM → строгий UTF-8 → Windows-1251), двоичных файлов и обратное кодирование.</summary>
internal static class TextCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8NoBom = new(false, false);

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".obj", ".o", ".a", ".lib", ".so", ".dylib", ".class", ".jar", ".war", ".pyc", ".pyd",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".psd", ".svgz",
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".tar", ".cab", ".msi", ".nupkg", ".snupkg", ".vsix",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods",
        ".mp3", ".mp4", ".wav", ".ogg", ".flac", ".avi", ".mov", ".mkv", ".webm",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".bin", ".dat", ".pak", ".gguf", ".safetensors", ".onnx", ".pt", ".ckpt",
        ".dcu", ".bpl", ".dcp", ".res", ".dres", ".rsm", ".identcache", ".snk", ".suo", ".cache",
    };

    static TextCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Cp1251 { get; } = CreateCp1251();

    private static Encoding CreateCp1251()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }

    public static bool IsBinaryExtension(string path) => BinaryExtensions.Contains(Path.GetExtension(path));

    /// <summary>NUL в первых 8 КБ (кроме UTF-16 с BOM) — двоичный файл.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        if (HasUtf16Bom(data)) return false;
        var head = data[..Math.Min(data.Length, 8192)];
        return head.IndexOf((byte)0) >= 0;
    }

    private static bool HasUtf16Bom(ReadOnlySpan<byte> d) =>
        d.Length >= 2 && ((d[0] == 0xFF && d[1] == 0xFE) || (d[0] == 0xFE && d[1] == 0xFF));

    /// <summary>Декодировать содержимое. truncated — данные обрезаны (последний многобайтовый символ мог разорваться).</summary>
    public static (string Text, TextFormat Format) Decode(ReadOnlySpan<byte> data, bool truncated = false)
    {
        TextEncodingKind kind;
        string text;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            kind = TextEncodingKind.Utf8Bom;
            text = Utf8NoBom.GetString(TrimUtf8Tail(data[3..], truncated));
        }
        else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            kind = TextEncodingKind.Utf16LE;
            var body = data[2..];
            if (body.Length % 2 == 1) body = body[..^1];
            text = Encoding.Unicode.GetString(body);
        }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            kind = TextEncodingKind.Utf16BE;
            var body = data[2..];
            if (body.Length % 2 == 1) body = body[..^1];
            text = Encoding.BigEndianUnicode.GetString(body);
        }
        else
        {
            var body = TrimUtf8Tail(data, truncated);
            try
            {
                text = StrictUtf8.GetString(body);
                kind = TextEncodingKind.Utf8;
            }
            catch (DecoderFallbackException)
            {
                // Не UTF-8 — скорее всего ANSI-файл с кириллицей (Delphi, старые проекты).
                text = Cp1251.GetString(data);
                kind = TextEncodingKind.Windows1251;
            }
        }
        var newLine = DetectNewLine(text);
        var finalNewline = text.EndsWith('\n');
        return (text, new TextFormat(kind, newLine, finalNewline));
    }

    /// <summary>Отбросить неполную UTF-8 последовательность в конце обрезанных данных.</summary>
    private static ReadOnlySpan<byte> TrimUtf8Tail(ReadOnlySpan<byte> data, bool truncated)
    {
        if (!truncated || data.Length == 0) return data;
        var i = data.Length - 1;
        var back = 0;
        while (i >= 0 && back < 4 && (data[i] & 0xC0) == 0x80)
        {
            i--;
            back++;
        }
        if (i < 0) return data;
        var lead = data[i];
        var need = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
        return need > back + 1 ? data[..i] : data;
    }

    public static string DetectNewLine(string text)
    {
        int crlf = 0, lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }
        return crlf > lf ? "\r\n" : "\n";
    }

    /// <summary>
    /// Закодировать текст в формате исходного файла: переводы строк, завершающий перевод строки, кодировка/BOM.
    /// Если символы не представимы в cp1251 — UTF-8 с BOM (usedFallback = true).
    /// </summary>
    public static byte[] Encode(string text, TextFormat format, out bool usedFallback)
    {
        usedFallback = false;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (format.FinalNewline)
        {
            if (!normalized.EndsWith('\n')) normalized += "\n";
        }
        else
        {
            normalized = normalized.TrimEnd('\n');
        }
        if (format.NewLine == "\r\n") normalized = normalized.Replace("\n", "\r\n");

        switch (format.Kind)
        {
            case TextEncodingKind.Utf8Bom:
                return [.. Encoding.UTF8.GetPreamble(), .. Utf8NoBom.GetBytes(normalized)];
            case TextEncodingKind.Utf16LE:
                return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(normalized)];
            case TextEncodingKind.Utf16BE:
                return [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(normalized)];
            case TextEncodingKind.Windows1251:
                try
                {
                    return Cp1251.GetBytes(normalized);
                }
                catch (EncoderFallbackException)
                {
                    usedFallback = true;
                    return [.. Encoding.UTF8.GetPreamble(), .. Utf8NoBom.GetBytes(normalized)];
                }
            default:
                return Utf8NoBom.GetBytes(normalized);
        }
    }

    /// <summary>Строки без символов перевода строки (\r\n и \n). Завершающий перевод строки не даёт пустой строки.</summary>
    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Split('\n');
        var count = lines.Length;
        if (text.EndsWith('\n')) count--;
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var l = lines[i];
            result[i] = l.EndsWith('\r') ? l[..^1] : l;
        }
        return result;
    }
}

/// <summary>Консервативная оценка токенов без обращения к серверу (код ≈ 3 символа на токен, не-ASCII — 2).</summary>
internal static class Tokens
{
    public static int Estimate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int ascii = 0, other = 0;
        foreach (var c in s)
        {
            if (c < 128) ascii++;
            else other++;
        }
        return (int)Math.Ceiling(ascii / 3.0 + other / 2.0);
    }

    public static string Format(long tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.#}M"
        : tokens >= 10_000 ? $"{tokens / 1000.0:0}k"
        : tokens >= 1000 ? $"{tokens / 1000.0:0.#}k"
        : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
