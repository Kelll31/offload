namespace Offload.Llama.Tests;

/// <summary>Разбор завершения llama-server по фрагментам настоящих журналов (без запуска процесса).</summary>
public sealed class ServerExitDiagnosticsTests
{
    private static string[] Lines(string log) => log.Replace("\r", "").Split('\n');

    private const string CudaOom = """
        0.00.050.100 I llama_model_loader: loaded meta data with 40 key-value pairs and 579 tensors from model.gguf
        0.00.900.000 I load_tensors: offloaded 49/49 layers to GPU
        0.01.200.000 E ggml_backend_cuda_buffer_type_alloc_buffer: allocating 20480.00 MiB on device 0: cudaMalloc failed: out of memory
        0.01.200.100 E alloc_tensor_range: failed to allocate CUDA0 buffer of size 21474836480
        0.01.200.200 E llama_init_from_model: failed to initialize the context: failed to allocate buffer for kv cache
        0.01.200.300 E common_init_from_params: failed to create context with model 'model.gguf'
        0.01.200.400 E srv    load_model: failed to load model, 'model.gguf'
        0.01.200.500 I srv   operator(): operator(): cleaning up before exit...
        """;

    private const string BrokenGguf = """
        0.00.000.500 I build: 6500 (abcdef0) with MSVC 19.44 for x64
        0.00.010.000 E gguf_init_from_file_impl: invalid magic characters: 'xxxx', expected 'GGUF'
        0.00.010.100 E llama_model_load: error loading model: llama_model_loader: failed to load model from model.gguf
        """;

    private const string PortBusy = """
        0.00.000.500 I main: HTTP server is listening, hostname: 127.0.0.1, port: 8765, http threads: 23
        0.00.001.000 E couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 8765
        """;

    private const string VulkanFail = """
        0.00.001.000 I ggml_vulkan: Found 1 Vulkan devices:
        0.00.002.000 E vk::Device::createBuffer: ErrorOutOfDeviceMemory
        """;

    [Fact]
    public void Hint_RecognizesOutOfMemory()
    {
        var msg = ServerExitDiagnostics.DescribeExit(Lines(CudaOom), 1, whileStarting: true, port: 8765);
        Assert.StartsWith("llama-server не запустился (код 1).", msg);
        Assert.Contains("Не хватило памяти", msg);
        Assert.Contains("Журнал: ", msg);
        Assert.DoesNotContain("0.01.200", msg);
    }

    [Fact]
    public void Hint_RecognizesBrokenModelFile()
    {
        Assert.Contains("файл повреждён", ServerExitDiagnostics.Hint(Lines(BrokenGguf), 1, 0));
    }

    [Fact]
    public void Hint_PortBusy_NamesPort()
    {
        Assert.Equal("Порт 8765 занят другой программой.", ServerExitDiagnostics.Hint(Lines(PortBusy), 1, 8765));
        Assert.Equal("Порт занят другой программой.", ServerExitDiagnostics.Hint(Lines(PortBusy), 1, 0));
    }

    [Fact]
    public void Hint_VulkanOutOfDeviceMemory_IsMemoryNotDriver()
    {
        // «OutOfDeviceMemory» проверяется раньше драйверных признаков (vk::): совет про память полезнее.
        Assert.Contains("Не хватило памяти", ServerExitDiagnostics.Hint(Lines(VulkanFail), 1, 0));
    }

    [Fact]
    public void Hint_UnknownTail_NoHint()
    {
        Assert.Null(ServerExitDiagnostics.Hint(["0.00.001.000 I srv  update_slots: all slots are idle"], 1, 8765));
        Assert.Null(ServerExitDiagnostics.Hint([], 0, 0));
    }

    [Fact]
    public void ImportantLines_PrefersErrors_LastFour_WithoutPrefix()
    {
        var lines = ServerExitDiagnostics.ImportantLines(Lines(CudaOom));
        Assert.Equal(4, lines.Count);
        Assert.StartsWith("alloc_tensor_range:", lines[0]);
        Assert.StartsWith("srv    load_model:", lines[^1]);
        Assert.All(lines, l => Assert.DoesNotMatch(@"^\d+\.\d+", l));
    }

    [Fact]
    public void ImportantLines_NoErrors_LastThree_Truncated()
    {
        var longLine = "0.00.000.001 I " + new string('x', 300);
        var lines = ServerExitDiagnostics.ImportantLines(["0.00.000.000 I a", "0.00.000.000 I b", "", "0.00.000.000 I c", longLine]);
        Assert.Equal(3, lines.Count);
        Assert.Equal("b", lines[0]);
        Assert.Equal(201, lines[2].Length);
        Assert.EndsWith("…", lines[2]);
    }

    [Fact]
    public void PortFromArguments_ReadsPortOrZero()
    {
        Assert.Equal(9000, ServerExitDiagnostics.PortFromArguments(["-m", "x.gguf", "--port", "9000"]));
        Assert.Equal(0, ServerExitDiagnostics.PortFromArguments(["-m", "x.gguf", "--port"]));
        Assert.Equal(0, ServerExitDiagnostics.PortFromArguments([]));
    }

    [Fact]
    public void BlamesBuild_NotForMemoryModelOrPort()
    {
        // Откат сборки при нехватке памяти, битом файле или занятом порте ничего бы не исправил.
        Assert.False(ServerExitDiagnostics.BlamesBuild(Lines(CudaOom), 1));
        Assert.False(ServerExitDiagnostics.BlamesBuild(Lines(VulkanFail), 1));
        Assert.False(ServerExitDiagnostics.BlamesBuild(Lines(BrokenGguf), 1));
        Assert.False(ServerExitDiagnostics.BlamesBuild(Lines(PortBusy), 1));
    }

    [Fact]
    public void BlamesBuild_ForRejectedArgumentsCrashOrUnknown()
    {
        Assert.True(ServerExitDiagnostics.BlamesBuild(["error: invalid argument: --jinja"], 1));
        Assert.True(ServerExitDiagnostics.BlamesBuild(["ggml_cuda_init: failed to initialize CUDA"], 1));
        Assert.True(ServerExitDiagnostics.BlamesBuild(["loading model"], unchecked((int)0xC0000005)));
        Assert.True(ServerExitDiagnostics.BlamesBuild([], 1));
    }
}
