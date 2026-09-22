using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Util;

namespace Offload.Llama;

/// <summary>Файл релиза llama.cpp на GitHub.</summary>
public sealed record LlamaAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <summary>Релиз llama.cpp (тег вида b11102).</summary>
public sealed record LlamaRelease(string Tag, DateTime PublishedAtUtc, IReadOnlyList<LlamaAsset> Assets);

/// <summary>Выбранные архивы для установки: основной и (для CUDA) runtime-библиотеки.</summary>
public sealed record LlamaBuildSelection(LlamaBackend Backend, LlamaRelease Release, LlamaAsset Main, LlamaAsset? CudaRuntime);

/// <summary>Рекомендация сборки для текущего оборудования с объяснением на русском.</summary>
public sealed record BackendRecommendation(LlamaBackend Backend, string ReasonRu);

/// <summary>
/// Работа с релизами ggml-org/llama.cpp: получение последнего релиза, выбор сборки под GPU.
/// </summary>
public static class LlamaReleaseResolver
{
    public const string Repo = "ggml-org/llama.cpp";

    /// <summary>
    /// Последний релиз со сборками для Windows: тег из releases/latest/download/nightly-tag.txt → releases/tags/{tag},
    /// запасной путь — releases?per_page=10. Ответ кэшируется в llama-release-cache.json (1 ч; без сети — без срока).
    /// </summary>
    public static Task<LlamaRelease> GetLatestAsync(CancellationToken ct = default) =>
        GitHubReleases.GetLatestAsync(GitHubReleases.HasWindowsBuild, "Windows", ct);

    /// <summary>
    /// Рекомендуемая сборка: NVIDIA → CUDA 12.4 (RTX 30/40 с драйвером ≥ 527.41, прочие ≥ 551.78),
    /// Blackwell (RTX 50) → CUDA 13 (драйвер ≥ 580), старый драйвер → Vulkan; AMD и Intel → Vulkan
    /// (ROCm/SYCL — вручную), нет дискретной видеокарты → CPU.
    /// </summary>
    public static BackendRecommendation Recommend(HardwareInfo hw) => BackendAdvisor.Recommend(hw);

    /// <summary>Выбор архивов релиза для указанной сборки. null — такой сборки в релизе нет.</summary>
    public static LlamaBuildSelection? Select(LlamaRelease release, LlamaBackend backend, bool arm64 = false) =>
        AssetSelector.Select(release, backend, arm64);

    /// <summary>Название сборки для интерфейса (русский): «NVIDIA CUDA 12.4», «Vulkan (любая видеокарта)», «Только процессор».</summary>
    public static string DisplayName(LlamaBackend backend) => BackendAdvisor.DisplayName(backend);

    /// <summary>Какие сборки вообще можно выбрать на этом оборудовании (для выпадающего списка).</summary>
    public static IReadOnlyList<LlamaBackend> AvailableBackends(HardwareInfo hw) => BackendAdvisor.Available(hw);
}

public sealed record LlamaInstallResult(string Tag, LlamaBackend Backend, string InstallDir, string ServerExePath);

public sealed record LlamaUpdateInfo(bool UpdateAvailable, string? InstalledTag, string LatestTag);

/// <summary>
/// Установка llama.cpp в %LOCALAPPDATA%\Offload\llama.cpp\&lt;tag&gt;-&lt;backend&gt;.
/// Скачивает архив(ы), распаковывает, проверяет наличие llama-server.exe, обновляет конфиг
/// (Llama.InstalledTag/InstalledBackend/InstallDir) и удаляет старые версии.
/// </summary>
public static class LlamaInstaller
{
    /// <param name="backend">Конкретная сборка; Auto разрешается через Recommend по текущему оборудованию.</param>
    /// <remarks>
    /// Повторный вызов для уже установленных тега и сборки возвращается сразу. Если в релизах нет CUDA 12,
    /// а оборудование поддерживает CUDA 13, ставится CUDA 13 (фактическая сборка — в результате).
    /// Llama.Backend = Auto заменяется установленной сборкой.
    /// </remarks>
    /// <exception cref="LlamaVcRuntimeMissingException">Нет Visual C++ Redistributable — предложите VcRuntime.InstallAsync и повторите.</exception>
    public static Task<LlamaInstallResult> InstallAsync(
        LlamaBackend backend,
        IProgress<StepProgress>? progress = null,
        CancellationToken ct = default) =>
        InstallerImpl.InstallAsync(backend, progress, ct);

    /// <summary>Установлен ли llama.cpp и существует ли llama-server.exe из конфига.</summary>
    public static bool IsInstalled(AppConfig cfg) => InstallerImpl.GetServerExePath(cfg) is not null;

    /// <summary>Путь к llama-server.exe установленной сборки или null.</summary>
    public static string? GetServerExePath(AppConfig cfg) => InstallerImpl.GetServerExePath(cfg);

    /// <summary>Есть ли более новая сборка того же типа (сравниваются номера bNNNNN).</summary>
    public static Task<LlamaUpdateInfo> CheckUpdateAsync(AppConfig cfg, CancellationToken ct = default) =>
        InstallerImpl.CheckUpdateAsync(cfg, ct);
}

/// <summary>
/// Visual C++ 2015–2022 x64 Redistributable: сборки llama.cpp импортируют MSVCP140.dll/VCRUNTIME140(_1).dll,
/// которые не входят в архивы.
/// </summary>
public static class VcRuntime
{
    public const string DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>Установщик для Windows на ARM (используется автоматически на ARM64).</summary>
    public const string DownloadUrlArm64 = VcRuntimeCheck.DownloadUrlArm64;

    /// <summary>
    /// Установлен ли runtime: реестр VC\Runtimes\x64 (оба представления реестра, Installed=1) и
    /// msvcp140.dll, vcruntime140.dll, vcruntime140_1.dll в System32 версии не ниже 14.40.
    /// </summary>
    public static bool IsInstalled() => VcRuntimeCheck.GetStatus() == VcRuntimeStatus.Ok;

    /// <summary>Скачать и запустить установщик (появится запрос UAC). true — установлен.</summary>
    /// <remarks>Отказ в запросе UAC или ошибка установщика → false. Коды 0/1638/3010 — успех.</remarks>
    public static Task<bool> InstallAsync(IProgress<StepProgress>? progress = null, CancellationToken ct = default) =>
        VcRuntimeCheck.InstallAsync(progress, ct);
}

/// <summary>Проверка, что сборка видит видеокарту (llama-server --list-devices).</summary>
public static class LlamaDevices
{
    /// <summary>Список устройств в текстовом виде, например «CUDA0: NVIDIA GeForce RTX 3090 (24575 MiB, 23000 MiB free)».</summary>
    /// <remarks>Пустой список — сборка не видит видеокарту (будет работать только процессор).</remarks>
    public static Task<IReadOnlyList<string>> ListAsync(string serverExePath, CancellationToken ct = default) =>
        DeviceList.ListAsync(serverExePath, ct);
}
