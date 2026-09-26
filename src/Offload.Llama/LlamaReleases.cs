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
    /// (ROCm/SYCL/OpenVINO — вручную), нет дискретной видеокарты → CPU.
    /// </summary>
    /// <param name="hw">Оборудование.</param>
    /// <param name="modelHasIqTensors">
    /// Есть ли IQ-тензоры в выбранной модели (<c>ModelCatalog.HasIqTensors</c>); null — неизвестно. Влияет на пояснение
    /// про ошибку CUDA 13.x с IQ-квантами (llama.cpp #21255).
    /// </param>
    public static BackendRecommendation Recommend(HardwareInfo hw, bool? modelHasIqTensors = null) =>
        BackendAdvisor.Recommend(hw, modelHasIqTensors);

    /// <summary>Выбор архивов релиза для указанной сборки. null — такой сборки в релизе нет.</summary>
    public static LlamaBuildSelection? Select(LlamaRelease release, LlamaBackend backend, bool arm64 = false) =>
        AssetSelector.Select(release, backend, arm64);

    /// <summary>Название сборки для интерфейса (русский): «NVIDIA CUDA 12.4», «Vulkan (любая видеокарта)», «Только процессор».</summary>
    public static string DisplayName(LlamaBackend backend) => BackendAdvisor.DisplayName(backend);

    /// <summary>Какие сборки вообще можно выбрать на этом оборудовании (для выпадающего списка).</summary>
    public static IReadOnlyList<LlamaBackend> AvailableBackends(HardwareInfo hw) => BackendAdvisor.Available(hw);
}

public sealed record LlamaInstallResult(string Tag, LlamaBackend Backend, string InstallDir, string ServerExePath);

/// <param name="UpdateAvailable">Есть более новая сборка того же типа (не бывает при закреплённой версии).</param>
/// <param name="InstalledTag">Установленный тег или null.</param>
/// <param name="LatestTag">Последний тег (при закреплении — закреплённый).</param>
/// <param name="Pinned">Версия закреплена (<c>Llama.PinnedTag</c>): обновления не проверялись.</param>
public sealed record LlamaUpdateInfo(bool UpdateAvailable, string? InstalledTag, string LatestTag, bool Pinned = false);

/// <summary>Сохранённая на диске сборка llama.cpp (текущая, закреплённая или одна из предыдущих).</summary>
public sealed record LlamaInstalledBuild(string Tag, LlamaBackend Backend, string InstallDir, DateTime InstalledAtUtc, bool IsCurrent, bool IsPinned);

/// <summary>
/// Новая сборка не прошла проверку запуска: текущей осталась (или стала) прежняя сборка. Сообщение — для пользователя.
/// </summary>
public sealed class LlamaInstallRolledBackException : InvalidOperationException
{
    public LlamaInstallRolledBackException(string message, LlamaInstallResult? switchedTo, Exception inner) : base(message, inner) =>
        SwitchedTo = switchedTo;

    /// <summary>Сборка, на которую переключился конфиг; null — конфиг не менялся (прежняя сборка так и осталась текущей).</summary>
    public LlamaInstallResult? SwitchedTo { get; }
}

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

    /// <summary>
    /// Есть ли более новая сборка того же типа (сравниваются номера bNNNNN). При закреплённой версии сеть не используется
    /// и обновление не предлагается; сборка, которая уже не запустилась (<c>Llama.FailedTag</c>), тоже не предлагается.
    /// </summary>
    public static Task<LlamaUpdateInfo> CheckUpdateAsync(AppConfig cfg, CancellationToken ct = default) =>
        InstallerImpl.CheckUpdateAsync(cfg, ct);

    /// <summary>Сколько предыдущих сборок хранится сверх текущей и закреплённой.</summary>
    public const int KeepPreviousBuilds = InstallerImpl.KeepPreviousBuilds;

    /// <summary>Сохранённые на диске сборки (новые первыми). Читает файловую систему — вызывать не в UI-потоке.</summary>
    public static IReadOnlyList<LlamaInstalledBuild> ListInstalledBuilds(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return InstallerImpl.ListBuildsCore(cfg);
    }

    /// <summary>
    /// Сделать текущей сохранённую сборку (откат или возврат) без загрузки: проверка «--version» и обновление конфига.
    /// Перезапуск сервера — забота вызывающего.
    /// </summary>
    public static Task<LlamaInstallResult> SwitchToAsync(string installDir, CancellationToken ct = default) =>
        InstallerImpl.SwitchToAsync(installDir, ct);

    /// <summary>Закрепить версию (тег bNNNNN) или снять закрепление (null): установка ставит закреплённую версию.</summary>
    public static void SetPinnedTag(string? tag) => InstallerImpl.SetPinnedTag(tag);

    /// <summary>
    /// Сообщить результат запуска сервера. Нужен после обновления: если новая сборка ни разу не запустила сервер и запуск
    /// не удался, конфиг переключается на прежнюю сборку и возвращается она — сервер нужно запустить ещё раз.
    /// Если и с прежней сборкой запуск не удался, возвращается новая (дело не в сборке) и результат — null.
    /// Без незавершённого обновления вызов ничего не делает и не обращается к диску.
    /// </summary>
    /// <returns>Сборка, на которую выполнен откат (повторить запуск), или null.</returns>
    public static Task<LlamaInstallResult?> ReportServerStartAsync(bool started, CancellationToken ct = default) =>
        InstallerImpl.ReportServerStartAsync(started, ct);
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
