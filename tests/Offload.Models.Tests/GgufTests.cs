namespace Offload.Models.Tests;

public sealed class GgufTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-gguf-" + Guid.NewGuid().ToString("N"));

    public GgufTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Read_ParsesMetadataAndSkipsBigArrays()
    {
        var path = GgufWriter.QwenMoe()
            .U8("custom.u8", 7)
            .I16("custom.i16", -3)
            .U64("custom.u64", 1234567890123)
            .F64("custom.f64", 3.5)
            .Bool("custom.flag", true)
            .NestedArray("custom.nested")
            .WriteTo(P("moe.gguf"));

        var info = GgufReader.Read(path);
        Assert.Equal("qwen35moe", info.Architecture);
        Assert.Equal("Test Qwen MoE", info.Name);
        Assert.Equal(262144, info.ContextLength);
        Assert.Equal(40, info.BlockCount);
        Assert.Equal(2, info.HeadCountKv); // массив по слоям → максимум
        Assert.Equal(16, info.HeadCount);
        Assert.Equal(2048, info.EmbeddingLength);
        Assert.Equal(256, info.KeyLength);
        Assert.Equal(256, info.ValueLength);
        Assert.True(info.IsMoe);
        Assert.Equal(256, info.ExpertCount);
        Assert.Equal(8, info.ExpertUsedCount);
        Assert.Equal(4, info.FullAttentionInterval);
        Assert.Equal(3, info.Version);
        Assert.Contains("enable_thinking", info.ChatTemplate);

        var kv = info.ToKvSpec();
        Assert.Equal((10, 2, 256), (kv.Layers, kv.KvHeads, kv.HeadDim));
        Assert.Equal(20480, FitCalculator.KvBytesPerToken(kv, "f16"));
    }

    [Fact]
    public void Read_DoesNotReadTheWholeFile()
    {
        var data = new GgufWriter { TailBytes = 20_000_000 }
            .Str("general.architecture", "llama")
            .U32("llama.context_length", 8192)
            .BigU8Array("tokenizer.ggml.token_type", 30_000_000)
            .U32("llama.block_count", 32)
            .Build();
        using var counting = new CountingStream(new MemoryStream(data));
        var info = GgufReader.Read(counting);
        Assert.Equal(8192, info.ContextLength);
        Assert.Equal(32, info.BlockCount);
        Assert.False(info.IsMoe);
        Assert.True(counting.BytesRead < 64 * 1024, $"прочитано {counting.BytesRead} байт");
    }

    [Fact]
    public void Read_BigEndianAndVersion2()
    {
        var be = new GgufWriter { BigEndian = true }
            .Str("general.architecture", "gpt-oss")
            .U32("gpt-oss.context_length", 131072)
            .U32("gpt-oss.block_count", 24)
            .U32("gpt-oss.attention.head_count_kv", 8)
            .U32("gpt-oss.attention.key_length", 64)
            .U32("gpt-oss.attention.sliding_window", 128)
            .U32("gpt-oss.expert_count", 32)
            .WriteTo(P("be.gguf"));
        var info = GgufReader.Read(be);
        Assert.Equal((131072, 24, 8, 32), (info.ContextLength, info.BlockCount, info.HeadCountKv, info.ExpertCount));
        var kv = info.ToKvSpec();
        Assert.Equal((24, 12, 128), (kv.Layers, kv.FullAttentionLayers, kv.SlidingWindow));

        var v2 = new GgufWriter { Version = 2 }.Str("general.architecture", "llama").U32("llama.block_count", 7).WriteTo(P("v2.gguf"));
        Assert.Equal(7, GgufReader.Read(v2).BlockCount);
        Assert.Equal(2, GgufReader.Read(v2).Version);
    }

    [Fact]
    public void Read_HybridWithMtpLayer()
    {
        var path = new GgufWriter()
            .Str("general.architecture", "qwen35")
            .U32("qwen35.block_count", 65)
            .U32("qwen35.nextn_predict_layers", 1)
            .U32("qwen35.full_attention_interval", 4)
            .U32("qwen35.attention.head_count_kv", 4)
            .U32("qwen35.attention.key_length", 256)
            .WriteTo(P("mtp.gguf"));
        var info = GgufReader.Read(path);
        Assert.Equal(1, info.NextNPredictLayers);
        Assert.Equal(16, info.ToKvSpec().Layers); // 64 основных слоя / 4
    }

    [Fact]
    public void Read_UnknownArchitectureKeysAreFound()
    {
        var path = new GgufWriter().U32("mystery.context_length", 4096).U32("mystery.block_count", 12).WriteTo(P("noarch.gguf"));
        var info = GgufReader.Read(path);
        Assert.Null(info.Architecture);
        Assert.Equal((4096, 12), (info.ContextLength, info.BlockCount));
    }

    [Fact]
    public void Read_MalformedFiles_ThrowClearErrors()
    {
        File.WriteAllBytes(P("notgguf.gguf"), "PK\x03\x04 not a model at all"u8.ToArray());
        var e1 = Assert.Throws<InvalidDataException>(() => GgufReader.Read(P("notgguf.gguf")));
        Assert.Contains("не является моделью GGUF", e1.Message);

        var full = GgufWriter.QwenMoe().Build();
        File.WriteAllBytes(P("cut.gguf"), full[..(full.Length / 2)]);
        var e2 = Assert.Throws<InvalidDataException>(() => GgufReader.Read(P("cut.gguf")));
        Assert.Contains("файл неполный или повреждён", e2.Message);

        File.WriteAllBytes(P("empty.gguf"), []);
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(P("empty.gguf")));

        var v1 = new GgufWriter { Version = 1 }.WriteTo(P("v1.gguf"));
        Assert.Contains("v1", Assert.Throws<InvalidDataException>(() => GgufReader.Read(v1)).Message);

        // Длина строки больше файла.
        var bad = new GgufWriter().Str("general.architecture", "llama").Build();
        BitConverter.GetBytes(ulong.MaxValue / 2).CopyTo(bad, 24);
        File.WriteAllBytes(P("badlen.gguf"), bad);
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(P("badlen.gguf")));

        // Огромное число метаданных.
        var many = new GgufWriter().Build();
        BitConverter.GetBytes(10_000_000_000UL).CopyTo(many, 16);
        File.WriteAllBytes(P("many.gguf"), many);
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(P("many.gguf")));

        // Неизвестный тип значения.
        var unknownType = new GgufWriter().U32("x.block_count", 1).Build();
        var typeOffset = 24 + 8 + "x.block_count".Length;
        BitConverter.GetBytes(99u).CopyTo(unknownType, typeOffset);
        File.WriteAllBytes(P("type.gguf"), unknownType);
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(P("type.gguf")));

        Assert.Throws<FileNotFoundException>(() => GgufReader.Read(P("missing.gguf")));
    }

    /// <summary>Поток, считающий фактически прочитанные байты (пропуск через Seek не считается).</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
