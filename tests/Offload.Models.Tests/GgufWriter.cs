using System.Buffers.Binary;
using System.Text;

namespace Offload.Models.Tests;

/// <summary>Генератор минимальных файлов GGUF (только заголовок с метаданными) для тестов.</summary>
internal sealed class GgufWriter
{
    private readonly List<Action<Out>> _kv = [];

    public bool BigEndian { get; init; }
    public uint Version { get; init; } = 3;
    public ulong TensorCount { get; init; }

    /// <summary>Байты, дописываемые после заголовка (имитация данных тензоров).</summary>
    public int TailBytes { get; init; }

    public GgufWriter Str(string key, string value) => Add(key, 8, o => o.Str(value));
    public GgufWriter U8(string key, byte v) => Add(key, 0, o => o.Raw([v]));
    public GgufWriter I16(string key, short v) => Add(key, 3, o => o.U16((ushort)v));
    public GgufWriter U32(string key, uint v) => Add(key, 4, o => o.U32(v));
    public GgufWriter I32(string key, int v) => Add(key, 5, o => o.U32((uint)v));
    public GgufWriter U64(string key, ulong v) => Add(key, 10, o => o.U64(v));
    public GgufWriter F32(string key, float v) => Add(key, 6, o => o.U32(BitConverter.SingleToUInt32Bits(v)));
    public GgufWriter F64(string key, double v) => Add(key, 12, o => o.U64(BitConverter.DoubleToUInt64Bits(v)));
    public GgufWriter Bool(string key, bool v) => Add(key, 7, o => o.Raw([(byte)(v ? 1 : 0)]));

    public GgufWriter StrArray(string key, IReadOnlyList<string> values) => Add(key, 9, o =>
    {
        o.U32(8);
        o.U64((ulong)values.Count);
        foreach (var v in values) o.Str(v);
    });

    public GgufWriter I32Array(string key, IReadOnlyList<int> values) => Add(key, 9, o =>
    {
        o.U32(5);
        o.U64((ulong)values.Count);
        foreach (var v in values) o.U32((uint)v);
    });

    /// <summary>Большой массив uint8 (как token_type/merges) — читатель должен пропустить его перемещением.</summary>
    public GgufWriter BigU8Array(string key, int count) => Add(key, 9, o =>
    {
        o.U32(0);
        o.U64((ulong)count);
        o.Raw(new byte[count]);
    });

    /// <summary>Массив массивов int32 (редкий, но допустимый тип).</summary>
    public GgufWriter NestedArray(string key) => Add(key, 9, o =>
    {
        o.U32(9);
        o.U64(2);
        for (var i = 0; i < 2; i++)
        {
            o.U32(5);
            o.U64(3);
            for (var j = 0; j < 3; j++) o.U32((uint)j);
        }
    });

    private GgufWriter Add(string key, uint type, Action<Out> value)
    {
        _kv.Add(o =>
        {
            o.Str(key);
            o.U32(type);
            value(o);
        });
        return this;
    }

    public byte[] Build()
    {
        var o = new Out(BigEndian);
        o.Raw("GGUF"u8.ToArray());
        o.U32(Version);
        o.U64(TensorCount);
        o.U64((ulong)_kv.Count);
        foreach (var kv in _kv) kv(o);
        if (TailBytes > 0) o.Raw(new byte[TailBytes]);
        return o.ToArray();
    }

    public string WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Build());
        return path;
    }

    /// <summary>Типичный заголовок гибридной MoE-модели Qwen3.5 (qwen35moe).</summary>
    public static GgufWriter QwenMoe(string name = "Test Qwen MoE", string template = "{% if enable_thinking %}<think>{% endif %}<tool_call>") =>
        new GgufWriter()
            .Str("general.architecture", "qwen35moe")
            .Str("general.name", name)
            .U32("general.quantization_version", 2)
            .F32("general.sampling.temp", 1.0f)
            .U32("qwen35moe.block_count", 40)
            .U32("qwen35moe.context_length", 262144)
            .U32("qwen35moe.embedding_length", 2048)
            .U32("qwen35moe.attention.head_count", 16)
            .I32Array("qwen35moe.attention.head_count_kv", Enumerable.Range(0, 40).Select(i => i % 4 == 3 ? 2 : 0).ToArray())
            .U32("qwen35moe.attention.key_length", 256)
            .U32("qwen35moe.attention.value_length", 256)
            .U32("qwen35moe.expert_count", 256)
            .U32("qwen35moe.expert_used_count", 8)
            .U32("qwen35moe.full_attention_interval", 4)
            .StrArray("tokenizer.ggml.tokens", Enumerable.Range(0, 1000).Select(i => "tok" + i).ToArray())
            .BigU8Array("tokenizer.ggml.token_type", 200_000)
            .Str("tokenizer.chat_template", template);

    private sealed class Out(bool bigEndian)
    {
        private readonly MemoryStream _ms = new();
        private readonly byte[] _b = new byte[8];

        public void Raw(byte[] bytes) => _ms.Write(bytes);

        public void U16(ushort v)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(_b, v); else BinaryPrimitives.WriteUInt16LittleEndian(_b, v);
            _ms.Write(_b, 0, 2);
        }

        public void U32(uint v)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(_b, v); else BinaryPrimitives.WriteUInt32LittleEndian(_b, v);
            _ms.Write(_b, 0, 4);
        }

        public void U64(ulong v)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(_b, v); else BinaryPrimitives.WriteUInt64LittleEndian(_b, v);
            _ms.Write(_b, 0, 8);
        }

        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            U64((ulong)bytes.Length);
            _ms.Write(bytes);
        }

        public byte[] ToArray() => _ms.ToArray();
    }
}
