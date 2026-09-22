using System.Diagnostics;
using System.Text;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.Llama.Tests;

/// <summary>
/// Полная живая проверка на этой машине: установка рекомендованной сборки с GitHub, список устройств,
/// загрузка небольшой модели, запуск сервера, чат, токены, /props, остановка.
/// Запуск: OFFLOAD_LIVE=1, OFFLOAD_LIVE_HOME=&lt;папка&gt; (данные остаются там для повторного использования).
/// </summary>
[Collection("AppPaths")]
[Trait("Category", "Live")]
public sealed class LiveTests
{
    private const string ModelRepo = "Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF";
    private const string ModelFile = "qwen2.5-coder-0.5b-instruct-q4_k_m.gguf";
    private const long ModelSize = 491_400_064;
    private const string ModelSha256 = "1d9614638d18024d0fbb36575a15f1302a3adf044df10345688ec4f6e1c4ff32";

    [Fact]
    public async Task InstallRunChatStop()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OFFLOAD_LIVE") == "1", "Живая проверка: задайте OFFLOAD_LIVE=1");
        var ct = TestContext.Current.CancellationToken;
        var home = Environment.GetEnvironmentVariable("OFFLOAD_LIVE_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Path.GetTempPath(), "pc-live-home");
        Directory.CreateDirectory(home);
        AppPaths.OverrideDataDir(home);
        var report = new StringBuilder();
        void Say(string s)
        {
            var line = $"{DateTime.Now:HH:mm:ss} {s}";
            report.AppendLine(line);
            TestContext.Current.TestOutputHelper?.WriteLine(line);
            File.AppendAllText(Path.Combine(home, "live-report.txt"), line + Environment.NewLine);
        }

        try
        {
            Log.Init("live");
            Log.MinLevel = LogLevel.Debug;
            var cfg = ConfigStore.Reload();

            var hw = await HardwareDetector.DetectAsync(ct);
            var rec = LlamaReleaseResolver.Recommend(hw);
            Say($"GPU: {string.Join("; ", hw.Gpus.Select(g => $"{g.Name} drv {g.DriverVersion} cc {g.ComputeCapability}"))}");
            Say($"Рекомендация: {rec.Backend} — {rec.ReasonRu}");
            Say($"Доступные сборки: {string.Join(", ", LlamaReleaseResolver.AvailableBackends(hw))}");
            Say($"VC++ runtime установлен: {VcRuntime.IsInstalled()}");

            var sw = Stopwatch.StartNew();
            string? lastStage = null;
            var lastDetailAt = TimeSpan.Zero;
            var progress = new Progress(p =>
            {
                if (p.Stage != lastStage || sw.Elapsed - lastDetailAt > TimeSpan.FromSeconds(15))
                {
                    lastStage = p.Stage;
                    lastDetailAt = sw.Elapsed;
                    Say($"  [{(p.Fraction is double f ? f.ToString("P0") : "…")}] {p.Stage} {p.Detail}");
                }
            });
            var install = await LlamaInstaller.InstallAsync(rec.Backend, progress, ct);
            Say($"Установлено: {install.Tag} {install.Backend} → {install.ServerExePath} за {sw.Elapsed.TotalSeconds:0} с");
            cfg = ConfigStore.Reload();
            Assert.True(LlamaInstaller.IsInstalled(cfg));

            var devices = await LlamaDevices.ListAsync(install.ServerExePath, ct);
            foreach (var d in devices) Say($"Устройство: {d}");
            if (install.Backend is LlamaBackend.Cuda12 or LlamaBackend.Cuda13)
                Assert.Contains(devices, d => d.StartsWith("CUDA", StringComparison.Ordinal));

            var update = await LlamaInstaller.CheckUpdateAsync(cfg, ct);
            Say($"Обновление: доступно={update.UpdateAvailable}, установлен {update.InstalledTag}, последний {update.LatestTag}");

            var modelsDir = cfg.Models.ModelsDir ?? AppPaths.DefaultModelsDir;
            var modelPath = Path.Combine(modelsDir, ModelFile);
            sw.Restart();
            await HttpDownloader.DownloadFileAsync($"https://huggingface.co/{ModelRepo}/resolve/main/{ModelFile}", modelPath,
                ModelSize, ModelSha256, ct: ct);
            Say($"Модель: {modelPath} ({FileUtil.FormatBytes(new FileInfo(modelPath).Length)}) за {sw.Elapsed.TotalSeconds:0} с");

            ConfigStore.Update(c =>
            {
                c.Models.Installed.RemoveAll(m => m.Id == "qwen2.5-coder-0.5b");
                c.Models.Installed.Add(new InstalledModel
                {
                    Id = "qwen2.5-coder-0.5b",
                    DisplayName = "Qwen2.5-Coder 0.5B Instruct (тест)",
                    Repo = ModelRepo,
                    FilePath = modelPath,
                    SizeBytes = ModelSize,
                    Quant = "Q4_K_M",
                    NativeContext = 32768,
                    RecommendedContext = 16384,
                    Architecture = "qwen2",
                    Sampling = new SamplingSettings { Temperature = 0.7, TopP = 0.8, TopK = 20, RepeatPenalty = 1.05 },
                });
                c.Models.ActiveModelId = "qwen2.5-coder-0.5b";
                c.Server.FlashAttention = "on";
                c.Server.CacheType = "q8_0";
            });
            cfg = ConfigStore.Current;

            using var server = new LlamaServerProcess();
            server.StateChanged += s => Say($"  Состояние: {s}");
            sw.Restart();
            await server.StartAsync(cfg, TimeSpan.FromMinutes(5), ct);
            Say($"Сервер готов за {sw.Elapsed.TotalSeconds:0.0} с, PID {server.ProcessId}, {cfg.Server.BaseUrl}");
            Say($"Команда: {LlamaServerArgs.Describe(server.CurrentPlan!)}");
            var pid = server.ProcessId;

            var client = LlamaClient.FromConfig(cfg);
            var props = await client.GetPropsAsync(ct);
            Assert.NotNull(props);
            Say($"/props: n_ctx/слот {props.ContextPerSlot}, слотов {props.TotalSlots}, alias {props.ModelAlias}, build {props.BuildInfo}, tools {props.SupportsToolCalls}");

            var tokens = await client.CountTokensAsync("def add(a, b):\n    return a + b\n", ct);
            Say($"/tokenize: {tokens} токенов");
            Assert.True(tokens > 5);

            var deltas = 0;
            var chat = await client.ChatAsync(new ChatRequest(
                [
                    ChatMessage.System("You are a coding assistant. Answer with code only."),
                    ChatMessage.User("Write a Python function add(a, b) that returns the sum."),
                ], MaxTokens: 128, Temperature: 0.2), _ => deltas++, ct);
            Say($"Чат: {chat.CompletionTokens} ток. ответа, {chat.PromptTokens} ток. запроса, {deltas} фрагментов, " +
                $"генерация {chat.GenerationTokensPerSecond:0.0} ток/с, промпт {chat.PromptTokensPerSecond:0.0} ток/с, {chat.Duration.TotalSeconds:0.00} с, finish {chat.FinishReason}");
            Say($"Ответ: {chat.Content.Replace("\n", " ⏎ ")}");
            Assert.Contains("def add", chat.Content);

            await server.StopAsync();
            Assert.Equal(ServerState.Stopped, server.State);
            await Task.Delay(300, ct);
            var alive = true;
            try
            {
                using var p = Process.GetProcessById(pid!.Value);
                alive = !p.HasExited;
            }
            catch (ArgumentException)
            {
                alive = false;
            }
            Say($"Процесс {pid} после остановки: {(alive ? "ЖИВ" : "завершён")}");
            Assert.False(alive);
        }
        finally
        {
            // Без ConfigStore.Reload(): иначе конфиг прочитался бы (и создался) в профиле пользователя.
            AppPaths.OverrideDataDir(null);
        }
    }

    private sealed class Progress(Action<StepProgress> a) : IProgress<StepProgress>
    {
        public void Report(StepProgress value) => a(value);
    }
}
