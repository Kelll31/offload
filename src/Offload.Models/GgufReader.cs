using System.Buffers.Binary;
using System.Text;

namespace Offload.Models;

/// <summary>Метаданные GGUF (для пользовательских моделей и уточнения KV-кэша).</summary>
public sealed record GgufInfo(
    string? Architecture,
    string? Name,
    int ContextLength,
    int BlockCount,
    int HeadCountKv,
    int HeadCount,
    int EmbeddingLength,
    int KeyLength,
    bool IsMoe,
    int ExpertCount,
    string? ChatTemplate,
    int ValueLength = 0,
    /// <summary>Каждый N-й слой — полное внимание (гибриды Qwen3.5+/Qwen3-Next); 0 — нет данных.</summary>
    int FullAttentionInterval = 0,
    /// <summary>Скользящее окно внимания (gpt-oss, Gemma), токенов; 0 — нет.</summary>
    int SlidingWindow = 0,
    /// <summary>Число MTP-слоёв (nextn_predict_layers); &gt; 0 — можно --spec-type draft-mtp.</summary>
    int NextNPredictLayers = 0,
    int ExpertUsedCount = 0,
    int Version = 0,
    long TensorCount = 0)
{
    /// <summary>Оценка параметров KV-кэша по заголовку (для FitCalculator).</summary>
    public KvSpec ToKvSpec()
    {
        var blocks = Math.Max(1, BlockCount - Math.Max(0, NextNPredictLayers));
        var headDim = KeyLength > 0 && ValueLength > 0 ? (KeyLength + ValueLength + 1) / 2
            : KeyLength > 0 ? KeyLength
            : HeadCount > 0 && EmbeddingLength > 0 ? EmbeddingLength / HeadCount
            : 128;
        var kvHeads = Math.Max(1, HeadCountKv > 0 ? HeadCountKv : HeadCount);

        // Гибриды: KV-кэш только у слоёв полного внимания (каждый interval-й слой).
        if (FullAttentionInterval > 1)
            return new KvSpec((blocks + FullAttentionInterval - 1) / FullAttentionInterval, kvHeads, headDim);
        // gpt-oss: слои чередуются — скользящее окно / полное внимание.
        if (SlidingWindow > 0 && string.Equals(Architecture, "gpt-oss", StringComparison.OrdinalIgnoreCase))
            return new KvSpec(blocks, kvHeads, headDim, FullAttentionLayers: blocks / 2, SlidingWindow: SlidingWindow);
        return new KvSpec(blocks, kvHeads, headDim);
    }
}

/// <summary>Чтение заголовка GGUF (без загрузки весов).</summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF" в little-endian
    private const int MaxKeyBytes = 64 * 1024;
    private const long MaxKvCount = 1_000_000;
    private const int MaxArrayDepth = 4;
    /// <summary>Шаблон чата длиннее этого не сохраняется (защита от мусора).</summary>
    private const int MaxTemplateBytes = 4 * 1024 * 1024;
    /// <summary>Числовые массивы интересующих ключей (напр. head_count_kv по слоям) читаются до этой длины.</summary>
    private const long MaxReadArray = 1 << 16;

    private enum ValueType : uint
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3, UInt32 = 4, Int32 = 5, Float32 = 6, Bool = 7,
        String = 8, Array = 9, UInt64 = 10, Int64 = 11, Float64 = 12,
    }

    public static GgufInfo Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(L.T("Не указан путь к файлу GGUF."), nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException(L.F("Файл модели не найден: {0}", path), path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        try
        {
            return Read(fs);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(L.F("Заголовок GGUF обрывается — файл неполный или повреждён: {0}", Path.GetFileName(path)), ex);
        }
    }

    /// <summary>Разбор заголовка из потока с поддержкой Seek (читается только заголовок).</summary>
    internal static GgufInfo Read(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead) throw new ArgumentException(L.T("Нужен поток с поддержкой чтения и перемещения."), nameof(stream));
        var r = new Reader(stream);

        var magic = r.U32();
        if (magic != Magic) throw new InvalidDataException(L.T("Файл не является моделью GGUF (неверная сигнатура)."));
        var versionRaw = r.U32();
        // Файлы для big-endian систем: версия читается «перевёрнутой».
        if (versionRaw is not (2 or 3) && BinaryPrimitives.ReverseEndianness(versionRaw) is 2 or 3)
        {
            r.BigEndian = true;
            versionRaw = BinaryPrimitives.ReverseEndianness(versionRaw);
        }
        if (versionRaw == 1) throw new InvalidDataException(L.T("Устаревший формат GGUF v1 не поддерживается — скачайте актуальную версию модели."));
        if (versionRaw is not (2 or 3)) throw new InvalidDataException(L.F("Неподдерживаемая версия GGUF: {0}.", versionRaw));

        var tensorCount = r.I64Count(L.T("число тензоров"));
        var kvCount = r.I64Count(L.T("число метаданных"));
        if (kvCount > MaxKvCount) throw new InvalidDataException(L.F("Заголовок GGUF повреждён: слишком много метаданных ({0}).", kvCount));

        var ints = new Dictionary<string, long>(StringComparer.Ordinal);
        string? arch = null, name = null, template = null;

        for (long i = 0; i < kvCount; i++)
        {
            var key = r.String(MaxKeyBytes, L.T("ключ"));
            var type = (ValueType)r.U32();
            switch (key)
            {
                case "general.architecture" when type == ValueType.String:
                    arch = r.String(1024, key);
                    continue;
                case "general.name" when type == ValueType.String:
                    name = r.String(4096, key);
                    continue;
                case "tokenizer.chat_template" when type == ValueType.String:
                    template = r.StringOrSkip(MaxTemplateBytes);
                    continue;
            }
            if (IsWantedNumeric(key) && TryReadNumber(r, type, out var value))
            {
                ints[key] = value;
                continue;
            }
            r.SkipValue(type, 0);
        }

        long Get(string suffix)
        {
            if (arch is not null) return ints.TryGetValue($"{arch}.{suffix}", out var v) ? v : 0;
            // Архитектура не указана — берём первый подходящий ключ.
            foreach (var (k, val) in ints)
            {
                if (k.EndsWith("." + suffix, StringComparison.Ordinal) && !k.StartsWith("general.", StringComparison.Ordinal)) return val;
            }
            return 0;
        }
        static int Clamp(long v) => (int)Math.Clamp(v, 0, int.MaxValue);

        var experts = Clamp(Get("expert_count"));
        return new GgufInfo(
            arch,
            name,
            Clamp(Get("context_length")),
            Clamp(Get("block_count")),
            Clamp(Get("attention.head_count_kv")),
            Clamp(Get("attention.head_count")),
            Clamp(Get("embedding_length")),
            Clamp(Get("attention.key_length")),
            experts > 0,
            experts,
            template,
            ValueLength: Clamp(Get("attention.value_length")),
            FullAttentionInterval: Clamp(Get("full_attention_interval")),
            SlidingWindow: Clamp(Get("attention.sliding_window")),
            NextNPredictLayers: Clamp(Get("nextn_predict_layers")),
            ExpertUsedCount: Clamp(Get("expert_used_count")),
            Version: (int)versionRaw,
            TensorCount: tensorCount);
    }

    private static readonly string[] WantedSuffixes =
    [
        ".context_length", ".block_count", ".attention.head_count_kv", ".attention.head_count", ".embedding_length",
        ".attention.key_length", ".attention.value_length", ".expert_count", ".expert_used_count",
        ".full_attention_interval", ".attention.sliding_window", ".nextn_predict_layers",
    ];

    private static bool IsWantedNumeric(string key) =>
        !key.StartsWith("general.", StringComparison.Ordinal)
        && !key.StartsWith("tokenizer.", StringComparison.Ordinal)
        && WantedSuffixes.Any(s => key.EndsWith(s, StringComparison.Ordinal));

    /// <summary>Скаляр или массив целых (у гибридов head_count_kv — по слоям; берём максимум).</summary>
    private static bool TryReadNumber(Reader r, ValueType type, out long value)
    {
        value = 0;
        if (IsScalarNumber(type))
        {
            value = r.ReadScalarAsInt64(type);
            return true;
        }
        if (type != ValueType.Array) return false;

        var elemType = (ValueType)r.U32();
        var count = r.I64Count(L.T("длина массива"));
        if (!IsScalarNumber(elemType) || count > MaxReadArray)
        {
            r.SkipArrayBody(elemType, count, 1);
            return false;
        }
        long max = 0;
        for (long i = 0; i < count; i++) max = Math.Max(max, r.ReadScalarAsInt64(elemType));
        value = max;
        return true;
    }

    private static bool IsScalarNumber(ValueType t) => t is ValueType.UInt8 or ValueType.Int8 or ValueType.UInt16
        or ValueType.Int16 or ValueType.UInt32 or ValueType.Int32 or ValueType.UInt64 or ValueType.Int64
        or ValueType.Float32 or ValueType.Float64 or ValueType.Bool;

    private static int ScalarSize(ValueType t) => t switch
    {
        ValueType.UInt8 or ValueType.Int8 or ValueType.Bool => 1,
        ValueType.UInt16 or ValueType.Int16 => 2,
        ValueType.UInt32 or ValueType.Int32 or ValueType.Float32 => 4,
        ValueType.UInt64 or ValueType.Int64 or ValueType.Float64 => 8,
        _ => throw new InvalidDataException(L.F("Заголовок GGUF повреждён: неизвестный тип значения {0}.", (uint)t)),
    };

    /// <summary>Чтение примитивов с проверкой границ файла (без чтения файла целиком).</summary>
    private sealed class Reader(Stream s)
    {
        private readonly byte[] _buf = new byte[8];
        private readonly long _length = s.Length;

        public bool BigEndian { get; set; }

        private long Remaining => _length - s.Position;

        private ReadOnlySpan<byte> Bytes(int n)
        {
            s.ReadExactly(_buf, 0, n);
            return _buf.AsSpan(0, n);
        }

        public uint U32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Bytes(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));

        public ulong U64() => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Bytes(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));

        /// <summary>uint64-счётчик, не превышающий размер файла (иначе заголовок повреждён).</summary>
        public long I64Count(string what)
        {
            var v = U64();
            if (v > (ulong)Math.Max(0, _length)) throw new InvalidDataException(L.F("Заголовок GGUF повреждён (файл неполный или повреждён): неверное значение ({0}: {1}).", what, v));
            return (long)v;
        }

        public string String(int maxBytes, string what)
        {
            var len = I64Count(L.F("длина строки «{0}»", what));
            if (len > maxBytes) throw new InvalidDataException(L.F("Заголовок GGUF повреждён: слишком длинная строка ({0}, {1} байт).", what, len));
            if (len > Remaining) throw new EndOfStreamException();
            var bytes = new byte[len];
            s.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Строка, если она не длиннее maxBytes; иначе пропускается (null).</summary>
        public string? StringOrSkip(int maxBytes)
        {
            var len = I64Count(L.T("длина строки"));
            if (len > Remaining) throw new EndOfStreamException();
            if (len > maxBytes)
            {
                s.Seek(len, SeekOrigin.Current);
                return null;
            }
            var bytes = new byte[len];
            s.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        public long ReadScalarAsInt64(ValueType t)
        {
            var size = ScalarSize(t);
            var b = Bytes(size);
            return t switch
            {
                ValueType.UInt8 or ValueType.Bool => b[0],
                ValueType.Int8 => (sbyte)b[0],
                ValueType.UInt16 => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b),
                ValueType.Int16 => BigEndian ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b),
                ValueType.UInt32 => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b),
                ValueType.Int32 => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b),
                ValueType.UInt64 => (long)Math.Min(long.MaxValue, BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b)),
                ValueType.Int64 => BigEndian ? BinaryPrimitives.ReadInt64BigEndian(b) : BinaryPrimitives.ReadInt64LittleEndian(b),
                ValueType.Float32 => (long)Math.Clamp((double)(BigEndian ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadSingleLittleEndian(b)), -9e18, 9e18),
                ValueType.Float64 => (long)Math.Clamp(BigEndian ? BinaryPrimitives.ReadDoubleBigEndian(b) : BinaryPrimitives.ReadDoubleLittleEndian(b), -9e18, 9e18),
                _ => throw new InvalidDataException(L.F("Заголовок GGUF повреждён: неизвестный тип значения {0}.", (uint)t)),
            };
        }

        /// <summary>Пропустить значение (большие массивы вроде словаря токенизатора — перемещением по файлу).</summary>
        public void SkipValue(ValueType t, int depth)
        {
            switch (t)
            {
                case ValueType.String:
                    Skip(I64Count(L.T("длина строки")));
                    break;
                case ValueType.Array:
                    var elem = (ValueType)U32();
                    var count = I64Count(L.T("длина массива"));
                    SkipArrayBody(elem, count, depth + 1);
                    break;
                default:
                    Skip(ScalarSize(t));
                    break;
            }
        }

        public void SkipArrayBody(ValueType elem, long count, int depth)
        {
            if (depth > MaxArrayDepth) throw new InvalidDataException(L.T("Заголовок GGUF повреждён: слишком глубокая вложенность массивов."));
            if (elem == ValueType.String)
            {
                for (long i = 0; i < count; i++) Skip(I64Count(L.T("длина строки")));
            }
            else if (elem == ValueType.Array)
            {
                for (long i = 0; i < count; i++) SkipValue(ValueType.Array, depth);
            }
            else
            {
                var size = ScalarSize(elem);
                if (count > Remaining / size) throw new EndOfStreamException();
                Skip(count * size);
            }
        }

        private void Skip(long n)
        {
            if (n < 0 || n > Remaining) throw new EndOfStreamException();
            s.Seek(n, SeekOrigin.Current);
        }
    }
}
