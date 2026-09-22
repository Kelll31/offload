using System.Security.Cryptography;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.Models.Tests;

[Collection("AppPaths")]
public class ModelManagerTests
{
    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static CatalogModel FakeModel(string repo, string path, byte[] data, string? sha = null, string id = "fake-model") =>
        new(id, "Тестовая модель", "Описание", repo, ["Q4_K_M"], data.Length, 1, 1, false, 262144, 32768,
            new KvSpec(6, 2, 256), true, new SamplingSettings { Temperature = 0.6, TopP = 0.95 }, "MIT", 1,
            IsReasoning: true, Revision: "f6d5376be1edb4d416d56da11e5397a961aca8ae",
            Files: [new CatalogFile("Q4_K_M", path, data.Length, sha ?? Sha(data))],
            Reasoning: ReasoningControl.EnableThinkingKwarg, HasMtp: true, Architecture: "qwen35moe", BlockCount: 24);

    private static byte[] ModelBytes(int tail = 3_000_000) =>
        new GgufWriter { TailBytes = tail }.Str("general.architecture", "qwen35moe").U32("qwen35moe.context_length", 262144).Build();

    [Fact]
    public void ModelsDir_DefaultsToDataDirAndIsCreated()
    {
        using var home = new TempHome();
        var cfg = ConfigStore.Current;
        Assert.Equal(AppPaths.DefaultModelsDir, ModelManager.ModelsDir(cfg));
        Assert.True(Directory.Exists(AppPaths.DefaultModelsDir));
        cfg.Models.ModelsDir = Path.Combine(home.Path, "elsewhere");
        Assert.Equal(Path.Combine(home.Path, "elsewhere"), ModelManager.ModelsDir(cfg));
        Assert.True(Directory.Exists(Path.Combine(home.Path, "elsewhere")));
        cfg.Models.ModelsDir = null;
        Assert.Equal(AppPaths.DefaultModelsDir, ModelManager.ModelsDir(cfg));
    }

    [Fact]
    public async Task DownloadAsync_ResolvesDownloadsVerifiesAndRegisters()
    {
        using var home = new TempHome();
        using var hub = new LocalHub();
        var data = ModelBytes();
        hub.AddFile("test/Fake-GGUF", "Fake-Q4_K_M.gguf", data, Sha(data));
        var saved = HfClient.Endpoint;
        HfClient.Endpoint = hub.BaseUrl;
        try
        {
            ConfigStore.Update(c => c.Server.Port = 9999); // чужие настройки не должны пострадать
            var model = FakeModel("test/Fake-GGUF", "Fake-Q4_K_M.gguf", data);
            var reports = new List<StepProgress>();
            var installed = await ModelManager.DownloadAsync(model, null, new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

            var expectedPath = Path.Combine(AppPaths.DefaultModelsDir, "Fake-Q4_K_M.gguf");
            Assert.Equal(expectedPath, installed.FilePath);
            Assert.Equal(data, File.ReadAllBytes(expectedPath));
            Assert.False(File.Exists(expectedPath + ".part"));
            Assert.Contains(hub.Log, l => l.Contains("/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Fake-Q4_K_M.gguf"));
            Assert.Contains(hub.Log, l => l.StartsWith("GET /cdn/"));

            var cfg = ConfigStore.Reload();
            Assert.Equal(9999, cfg.Server.Port);
            var entry = Assert.Single(cfg.Models.Installed);
            Assert.Equal("fake-model", entry.Id);
            Assert.Equal("fake-model", cfg.Models.ActiveModelId);
            Assert.Equal(("Тестовая модель", "test/Fake-GGUF", "Q4_K_M"), (entry.DisplayName, entry.Repo, entry.Quant));
            Assert.Equal((262144, 32768, (long)data.Length), (entry.NativeContext, entry.RecommendedContext, entry.SizeBytes));
            Assert.Equal((ReasoningControl.EnableThinkingKwarg, true, true, false), (entry.Reasoning, entry.HasMtp, entry.GoodToolCalling, entry.IsCustom));
            Assert.Equal("qwen35moe", entry.Architecture);
            Assert.Equal(0.6, entry.Sampling.Temperature);
            Assert.NotSame(model.Sampling, installed.Sampling);

            Assert.Equal("Поиск файлов модели на Hugging Face…", reports[0].Stage);
            Assert.Contains(reports, r => r.Stage.StartsWith("Загрузка модели Тестовая модель"));
            Assert.Contains(reports, r => r.Stage.StartsWith("Проверка целостности"));
            Assert.Equal(1.0, reports[^1].Fraction);
            Assert.Equal("Модель установлена", reports[^1].Stage);

            // Повторная загрузка: файл уже на месте — запись заменяется, а не дублируется.
            var again = await ModelManager.DownloadAsync(model, "q4_k_m", null, TestContext.Current.CancellationToken);
            Assert.Single(ConfigStore.Current.Models.Installed);
            Assert.Equal(installed.FilePath, again.FilePath);
        }
        finally
        {
            HfClient.Endpoint = saved;
        }
    }

    [Fact]
    public async Task DownloadAsync_ResumesAfterConnectionDrop()
    {
        using var home = new TempHome();
        using var hub = new LocalHub();
        var data = ModelBytes(4_000_000);
        hub.AddFile("test/Fake-GGUF", "Fake-Q4_K_M.gguf", data, Sha(data));
        hub.FailAfterBytes = 1_500_000;
        var saved = HfClient.Endpoint;
        HfClient.Endpoint = hub.BaseUrl;
        try
        {
            var installed = await ModelManager.DownloadAsync(FakeModel("test/Fake-GGUF", "Fake-Q4_K_M.gguf", data), ct: TestContext.Current.CancellationToken);
            Assert.Equal(data, File.ReadAllBytes(installed.FilePath));
            // Вторая попытка идёт через тот же resolve-URL и продолжает с места обрыва (Range сохраняется после 302).
            Assert.Contains(hub.Log, l => l.StartsWith("GET /cdn/") && l.Contains("range=bytes="));
        }
        finally
        {
            HfClient.Endpoint = saved;
        }
    }

    [Fact]
    public async Task DownloadAsync_BadSha_IsNotRegistered()
    {
        using var home = new TempHome();
        using var hub = new LocalHub();
        var data = ModelBytes(1000);
        hub.AddFile("test/Fake-GGUF", "Fake-Q4_K_M.gguf", data, Sha(data));
        var saved = HfClient.Endpoint;
        HfClient.Endpoint = hub.BaseUrl;
        try
        {
            var model = FakeModel("test/Fake-GGUF", "Fake-Q4_K_M.gguf", data, sha: new string('0', 64));
            await Assert.ThrowsAsync<DownloadException>(() => ModelManager.DownloadAsync(model, ct: TestContext.Current.CancellationToken));
            Assert.Empty(ConfigStore.Current.Models.Installed);
            Assert.False(File.Exists(Path.Combine(AppPaths.DefaultModelsDir, "Fake-Q4_K_M.gguf")));
        }
        finally
        {
            HfClient.Endpoint = saved;
        }
    }

    [Fact]
    public async Task DownloadAsync_NotEnoughDisk_FailsBeforeDownloading()
    {
        using var home = new TempHome();
        var huge = new CatalogModel("huge", "Огромная", "", "o/r", ["Q4_K_M"], long.MaxValue / 4, 1, 1, false, 4096, 4096,
            new KvSpec(1, 1, 64), false, new SamplingSettings(), "MIT", 1,
            Files: [new CatalogFile("Q4_K_M", "huge.gguf", long.MaxValue / 4, new string('a', 64))]);
        var ex = await Assert.ThrowsAsync<ModelException>(() => ModelManager.DownloadAsync(huge, ct: TestContext.Current.CancellationToken));
        Assert.Contains("Недостаточно места на диске", ex.Message);
        Assert.Contains("свободно", ex.Message);
        Assert.Empty(ConfigStore.Current.Models.Installed);
    }

    [Fact]
    public void AddCustom_ReadsHeaderAndRegisters()
    {
        using var home = new TempHome();
        var outside = Path.Combine(home.Path, "user files", "My Model-Q4_K_M.gguf");
        GgufWriter.QwenMoe(name: "Мой Qwen").WriteTo(outside);

        var m = ModelManager.AddCustom(outside);
        Assert.Equal("custom-my-model-q4_k_m", m.Id);
        Assert.Equal("Мой Qwen", m.DisplayName);
        Assert.True(m.IsCustom);
        Assert.True(m.IsMoe);
        Assert.Equal("qwen35moe", m.Architecture);
        Assert.Equal("Q4_K_M", m.Quant);
        Assert.Equal(262144, m.NativeContext);
        Assert.Equal(32768, m.RecommendedContext);
        Assert.Equal(ReasoningControl.EnableThinkingKwarg, m.Reasoning);
        Assert.True(m.GoodToolCalling);
        Assert.Equal(new FileInfo(outside).Length, m.SizeBytes);
        Assert.Equal(m.Id, ConfigStore.Current.Models.ActiveModelId);

        // Тот же файл повторно — одна запись; другой файл с тем же именем — новый id.
        ModelManager.AddCustom(outside, "Переименованная");
        Assert.Equal("Переименованная", Assert.Single(ConfigStore.Current.Models.Installed).DisplayName);
        var other = GgufWriter.QwenMoe().WriteTo(Path.Combine(home.Path, "другая папка", "My Model-Q4_K_M.gguf"));
        Assert.Equal("custom-my-model-q4_k_m-2", ModelManager.AddCustom(other).Id);
        Assert.Equal(m.Id, ConfigStore.Current.Models.ActiveModelId); // активная не меняется
    }

    [Fact]
    public void AddCustom_GptOss_UsesReasoningEffort_AndSmallContext()
    {
        using var home = new TempHome();
        var path = new GgufWriter().Str("general.architecture", "gpt-oss").U32("gpt-oss.context_length", 16384)
            .Str("tokenizer.chat_template", "<|start|>").WriteTo(Path.Combine(home.Path, "gpt-oss-20b-MXFP4.gguf"));
        var m = ModelManager.AddCustom(path);
        Assert.Equal(ReasoningControl.ReasoningEffort, m.Reasoning);
        Assert.Equal(16384, m.RecommendedContext);
        Assert.Equal("gpt-oss-20b-MXFP4", m.DisplayName);
        Assert.False(m.GoodToolCalling);
    }

    [Fact]
    public void AddCustom_InvalidOrMissing_Throws()
    {
        using var home = new TempHome();
        var bad = Path.Combine(home.Path, "bad.gguf");
        File.WriteAllText(bad, "not a model");
        Assert.Contains("не является моделью GGUF", Assert.Throws<ModelException>(() => ModelManager.AddCustom(bad)).Message);
        Assert.Throws<FileNotFoundException>(() => ModelManager.AddCustom(Path.Combine(home.Path, "nope.gguf")));
        Assert.Empty(ConfigStore.Current.Models.Installed);
    }

    [Fact]
    public void AddCustom_SecondShard_UsesFirst()
    {
        using var home = new TempHome();
        var dir = Path.Combine(home.Path, "split");
        GgufWriter.QwenMoe().WriteTo(Path.Combine(dir, "Big-Q8_0-00001-of-00002.gguf"));
        File.WriteAllBytes(Path.Combine(dir, "Big-Q8_0-00002-of-00002.gguf"), new byte[1000]);
        var m = ModelManager.AddCustom(Path.Combine(dir, "Big-Q8_0-00002-of-00002.gguf"));
        Assert.EndsWith("Big-Q8_0-00001-of-00002.gguf", m.FilePath);
        Assert.Equal("custom-big-q8_0", m.Id);
        Assert.Equal(new FileInfo(m.FilePath).Length + 1000, m.SizeBytes);
    }

    [Fact]
    public void Remove_NeverDeletesFilesOutsideModelsDir()
    {
        using var home = new TempHome();
        var outside = GgufWriter.QwenMoe().WriteTo(Path.Combine(home.Path, "user", "keep-me.gguf"));
        var m = ModelManager.AddCustom(outside);
        ModelManager.Remove(m.Id, deleteFiles: true);
        Assert.True(File.Exists(outside));
        Assert.Empty(ConfigStore.Current.Models.Installed);
        Assert.Null(ConfigStore.Current.Models.ActiveModelId);
    }

    [Fact]
    public void Remove_DeletesShardsInsideModelsDir_AndSwitchesActive()
    {
        using var home = new TempHome();
        var dir = ModelManager.ModelsDir(ConfigStore.Current);
        var first = GgufWriter.QwenMoe().WriteTo(Path.Combine(dir, "M-Q8_0-00001-of-00002.gguf"));
        var second = Path.Combine(dir, "M-Q8_0-00002-of-00002.gguf");
        File.WriteAllBytes(second, [1, 2, 3]);
        File.WriteAllBytes(second + ".part", [1]);
        var a = ModelManager.AddCustom(first);
        var b = ModelManager.AddCustom(GgufWriter.QwenMoe().WriteTo(Path.Combine(home.Path, "other.gguf")));
        Assert.Equal(a.Id, ConfigStore.Current.Models.ActiveModelId);

        ModelManager.Remove(a.Id, deleteFiles: true);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.False(File.Exists(second + ".part"));
        Assert.Equal(b.Id, ConfigStore.Current.Models.ActiveModelId);

        ModelManager.Remove("no-such-model", deleteFiles: true); // не падает
    }

    [Fact]
    public void Remove_KeepsFileUsedByAnotherEntry()
    {
        using var home = new TempHome();
        var dir = ModelManager.ModelsDir(ConfigStore.Current);
        var file = GgufWriter.QwenMoe().WriteTo(Path.Combine(dir, "shared.gguf"));
        var a = ModelManager.AddCustom(file);
        ConfigStore.Update(c => c.Models.Installed.Add(new InstalledModel { Id = "copy", DisplayName = "copy", FilePath = file }));
        ModelManager.Remove(a.Id, deleteFiles: true);
        Assert.True(File.Exists(file));
        Assert.Equal("copy", Assert.Single(ConfigStore.Current.Models.Installed).Id);
    }

    [Fact]
    public void Remove_WithoutDeleteFiles_KeepsFile()
    {
        using var home = new TempHome();
        var file = GgufWriter.QwenMoe().WriteTo(Path.Combine(ModelManager.ModelsDir(ConfigStore.Current), "x.gguf"));
        var m = ModelManager.AddCustom(file);
        ModelManager.Remove(m.Id, deleteFiles: false);
        Assert.True(File.Exists(file));
        Assert.Empty(ConfigStore.Current.Models.Installed);
    }

    [Fact]
    public void SetActive_And_Validate()
    {
        using var home = new TempHome();
        var a = ModelManager.AddCustom(GgufWriter.QwenMoe().WriteTo(Path.Combine(home.Path, "a.gguf")));
        var bPath = GgufWriter.QwenMoe().WriteTo(Path.Combine(home.Path, "b.gguf"));
        var b = ModelManager.AddCustom(bPath);
        ModelManager.SetActive(b.Id.ToUpperInvariant());
        Assert.Equal(b.Id, ConfigStore.Current.Models.ActiveModelId);
        Assert.Throws<ModelException>(() => ModelManager.SetActive("missing"));

        Assert.Equal(0, ModelManager.Validate());
        File.Delete(bPath);
        Assert.Equal(1, ModelManager.Validate());
        Assert.Equal(a.Id, Assert.Single(ConfigStore.Current.Models.Installed).Id);
        Assert.Equal(a.Id, ConfigStore.Current.Models.ActiveModelId);
        Assert.Equal(a.Id, ConfigStore.Reload().Models.ActiveModelId); // сохранено на диск
    }

    [Fact]
    public void Destination_UsesSubfolderOnNameCollision()
    {
        using var home = new TempHome();
        var cfg = ConfigStore.Current;
        var dir = ModelManager.ModelsDir(cfg);
        var m = FakeModel("lmstudio-community/Qwen3.8-27B-GGUF", "Qwen3.8-27B-Q8_0.gguf", [1]);
        Assert.Equal(Path.Combine(dir, "Qwen3.8-27B-Q8_0.gguf"), ModelManager.DestinationFor(cfg, dir, m, "Qwen3.8-27B-Q8_0.gguf"));
        cfg.Models.Installed.Add(new InstalledModel { Id = "other", Repo = "unsloth/Qwen3.8-27B-GGUF", FilePath = Path.Combine(dir, "Qwen3.8-27B-Q8_0.gguf") });
        Assert.Equal(Path.Combine(dir, "lmstudio-community_Qwen3.8-27B-GGUF", "Qwen3.8-27B-Q8_0.gguf"),
            ModelManager.DestinationFor(cfg, dir, m, "Qwen3.8-27B-Q8_0.gguf"));
        Assert.Equal(Path.Combine(dir, "a.gguf"), ModelManager.DestinationFor(cfg, dir, m, "Q8_0/a.gguf"));
    }

    [Theory]
    [InlineData("normal-Q4_K_M.gguf", "normal-Q4_K_M.gguf")]
    [InlineData("a<b>:c?.gguf", "a_b__c_.gguf")]
    [InlineData("..", "model.gguf")]
    [InlineData("  ", "model.gguf")]
    public void SanitizeFileName_Cases(string input, string expected) => Assert.Equal(expected, ModelManager.SanitizeFileName(input));

    [Fact]
    public void ShardFiles_ListsAllParts()
    {
        Assert.Equal(
            [@"C:\m\X-Q8_0-00001-of-00003.gguf", @"C:\m\X-Q8_0-00002-of-00003.gguf", @"C:\m\X-Q8_0-00003-of-00003.gguf"],
            ModelManager.ShardFiles(@"C:\m\X-Q8_0-00001-of-00003.gguf"));
        Assert.Equal([@"C:\m\single.gguf"], ModelManager.ShardFiles(@"C:\m\single.gguf"));
        Assert.Empty(ModelManager.ShardFiles(null));
        Assert.True(ModelManager.IsInside(@"C:\models\a\b.gguf", @"C:\models"));
        Assert.False(ModelManager.IsInside(@"C:\models2\b.gguf", @"C:\models"));
        Assert.False(ModelManager.IsInside(@"C:\models\..\b.gguf", @"C:\models"));
    }

    [Fact]
    public void Describe_FormatsRussianProgress()
    {
        const long GiB = 1024L * 1024 * 1024;
        var total = (long)(22.4 * GiB);
        var p = new DownloadProgress(DownloadStage.Downloading, (long)(1.2 * GiB), total, 85.3 * 1024 * 1024, "f.gguf");
        var s = ModelManager.Describe(p, "Загрузка модели X", 0, total);
        Assert.Equal("Загрузка модели X", s.Stage);
        Assert.Equal("1,2 ГБ из 22,4 ГБ · 85,3 МБ/с · осталось 4 мин", s.Detail);
        Assert.Equal(1.2 / 22.4, s.Fraction!.Value, 3);

        var second = ModelManager.Describe(p with { BytesReceived = 0, Stage = DownloadStage.Connecting }, "x", (long)(10.0 * GiB), total);
        Assert.Equal("Подключение к Hugging Face…", second.Stage);
        Assert.Equal(10.0 / 22.4, second.Fraction!.Value, 3);
        Assert.Equal("1 ч 5 мин", ModelManager.Eta(TimeSpan.FromMinutes(65)));
        Assert.Equal("30 с", ModelManager.Eta(TimeSpan.FromSeconds(29.5)));
    }

    private sealed class SyncProgress(Action<StepProgress> handler) : IProgress<StepProgress>
    {
        public void Report(StepProgress value)
        {
            lock (this) handler(value);
        }
    }
}
