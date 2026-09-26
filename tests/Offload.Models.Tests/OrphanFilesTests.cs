using System.Security.Cryptography;
using Offload.Core;
using Offload.Core.Config;

namespace Offload.Models.Tests;

[Collection("AppPaths")]
public class OrphanFilesTests
{
    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] ModelBytes(int tail) =>
        new GgufWriter { TailBytes = tail }.Str("general.architecture", "qwen35moe").U32("qwen35moe.context_length", 262144).Build();

    private static void Install(string id, string path) =>
        ConfigStore.Update(c => c.Models.Installed.Add(new InstalledModel { Id = id, DisplayName = id, FilePath = path }));

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task DownloadOtherQuant_LeavesOldFile_OfferedAsOrphan()
    {
        using var home = new TempHome();
        using var hub = new LocalHub();
        var q4 = ModelBytes(20_000);
        var q8 = ModelBytes(40_000);
        hub.AddFile("test/Fake-GGUF", "Fake-Q4_K_M.gguf", q4, Sha(q4));
        hub.AddFile("test/Fake-GGUF", "Fake-Q8_0.gguf", q8, Sha(q8));
        var model = new CatalogModel("fake-model", "Тестовая модель", "", "test/Fake-GGUF", ["Q4_K_M", "Q8_0"], q4.Length, 1, 1, false, 4096, 4096,
            new KvSpec(6, 2, 256), true, new SamplingSettings(), "MIT", 1,
            Revision: "f6d5376be1edb4d416d56da11e5397a961aca8ae",
            Files: [new CatalogFile("Q4_K_M", "Fake-Q4_K_M.gguf", q4.Length, Sha(q4)), new CatalogFile("Q8_0", "Fake-Q8_0.gguf", q8.Length, Sha(q8))]);
        var saved = HfClient.Endpoint;
        HfClient.Endpoint = hub.BaseUrl;
        try
        {
            var first = await ModelManager.DownloadAsync(model, "Q4_K_M", null, TestContext.Current.CancellationToken);
            Assert.Empty(ModelManager.OrphanFiles(first.FilePath)); // файл ещё используется

            var second = await ModelManager.DownloadAsync(model, "Q8_0", null, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(first.FilePath), "старый файл сам не удаляется");
            Assert.Equal([first.FilePath], ModelManager.OrphanFiles(first.FilePath));
            Assert.Empty(ModelManager.OrphanFiles(second.FilePath));

            Assert.Equal([first.FilePath], ModelManager.DeleteOrphanFiles(first.FilePath));
            Assert.False(File.Exists(first.FilePath));
            Assert.True(File.Exists(second.FilePath));
            Assert.Empty(ModelManager.OrphanFiles(first.FilePath));
        }
        finally
        {
            HfClient.Endpoint = saved;
        }
    }

    [Fact]
    public void OrphanFiles_SkipsUsedOutsideAndMissingFiles()
    {
        using var home = new TempHome();
        var dir = ModelManager.ModelsDir(ConfigStore.Current);
        var shared = Touch(Path.Combine(dir, "shared.gguf"));
        Install("other", shared);
        var outside = Touch(Path.Combine(home.Path, "elsewhere", "mine.gguf"));

        Assert.Empty(ModelManager.OrphanFiles(shared));               // нужен другой модели
        Assert.Empty(ModelManager.OrphanFiles(outside));              // вне папки моделей — не наш файл
        Assert.Empty(ModelManager.OrphanFiles(Path.Combine(dir, "gone.gguf")));
        Assert.Empty(ModelManager.OrphanFiles(null));
        Assert.Empty(ModelManager.DeleteOrphanFiles(outside));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(shared));
    }

    [Fact]
    public void OrphanFiles_IncludesAllShards()
    {
        using var home = new TempHome();
        var dir = ModelManager.ModelsDir(ConfigStore.Current);
        var shards = Enumerable.Range(1, 3).Select(i => Touch(Path.Combine(dir, $"Big-Q4_K_M-{i:D5}-of-00003.gguf"))).ToList();

        Assert.Equal(shards, ModelManager.OrphanFiles(shards[0]));
        ModelManager.DeleteOrphanFiles(shards[0]);
        Assert.All(shards, s => Assert.False(File.Exists(s)));
    }
}
