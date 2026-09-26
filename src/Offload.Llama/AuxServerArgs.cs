using System.Globalization;
using Offload.Core;
using Offload.Core.Config;

namespace Offload.Llama;

/// <summary>
/// Аргументы вспомогательного llama-server роли (fast / embed / rerank): своя модель, порт и псевдоним, один слот.
/// Доп. аргументы пользователя (ServerSettings.ExtraArgs) сюда не попадают — они подобраны под основную модель.
/// </summary>
public static class AuxServerArgs
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Допустимые значения --pooling (none не подходит для /v1/embeddings, rank — только реранк).</summary>
    private static readonly string[] PoolingTypes = ["mean", "cls", "last"];

    /// <summary>Псевдоним модели в API: offload (основной), offload-fast, offload-embed, offload-rerank.</summary>
    public static string AliasFor(ModelRole role) =>
        role == ModelRole.Quality ? LlamaServerArgs.DefaultAlias : LlamaServerArgs.DefaultAlias + "-" + role.Key();

    /// <summary>Журнал вспомогательного сервера (у основного — llama-server.log).</summary>
    public static string LogFilePath(ModelRole role) =>
        role == ModelRole.Quality ? LlamaServerProcess.LogFilePath : Path.Combine(AppPaths.LogsDir, $"llama-server-{role.Key()}.log");

    /// <summary>Нормализованный пулинг модели эмбеддингов или null (решает заголовок GGUF).</summary>
    public static string? NormalizePooling(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return PoolingTypes.Contains(v) ? v : null;
    }

    /// <summary>План запуска вспомогательного сервера роли на заданном порту.</summary>
    public static ServerLaunchPlan Build(AppConfig cfg, ModelRole role, InstalledModel model, string serverExePath, int port)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExePath);
        if (role == ModelRole.Quality) throw new ArgumentException("Роль quality обслуживает основной сервер", nameof(role)); // l10n-ignore: ошибка программиста
        if (string.IsNullOrWhiteSpace(model.FilePath))
            throw new ArgumentException(L.T("У модели не указан файл GGUF."), nameof(model));

        var s = cfg.Server ?? new ServerSettings();
        var ctx = ModelRoleConfig.AuxContext(role, model);
        var args = new List<string>
        {
            "-m", Path.GetFullPath(model.FilePath),
            "--host", string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host.Trim(),
            "--port", port.ToString(Inv),
            "--alias", AliasFor(role),
            "-c", ctx.ToString(Inv),
            "-np", "1",
        };

        var fa = LlamaServerArgs.NormalizeFlashAttention(s.FlashAttention);
        args.AddRange(["-fa", fa]);
        if (s.Threads > 0) args.AddRange(["-t", s.Threads.ToString(Inv)]);
        if (s.IdleUnloadMinutes > 0) args.AddRange(["--sleep-idle-seconds", (s.IdleUnloadMinutes * 60L).ToString(Inv)]);

        switch (role)
        {
            case ModelRole.Fast:
                if (LlamaServerArgs.NormalizeCacheType(s.CacheType) is { } cache)
                {
                    args.AddRange(["-ctk", cache]);
                    if (fa != "off" || cache is "f32" or "f16" or "bf16") args.AddRange(["-ctv", cache]);
                }
                args.Add("--jinja");
                var sp = model.Sampling ?? new SamplingSettings();
                AddNumber(args, "--temp", sp.Temperature);
                AddNumber(args, "--top-p", sp.TopP);
                args.AddRange(["--top-k", Math.Max(0, sp.TopK).ToString(Inv)]);
                AddNumber(args, "--min-p", sp.MinP);
                AddNumber(args, "--repeat-penalty", sp.RepeatPenalty);
                if (sp.PresencePenalty > 0) AddNumber(args, "--presence-penalty", sp.PresencePenalty);
                break;

            case ModelRole.Embed:
                args.Add("--embeddings");
                if (NormalizePooling(model.Pooling) is { } pooling) args.AddRange(["--pooling", pooling]);
                // Весь вход эмбеддинга обрабатывается одним физическим пакетом: иначе длинный текст отклоняется.
                args.AddRange(["-b", ctx.ToString(Inv), "-ub", ctx.ToString(Inv)]);
                break;

            case ModelRole.Rerank:
                args.Add("--reranking");
                args.AddRange(["-b", ctx.ToString(Inv), "-ub", ctx.ToString(Inv)]);
                break;
        }

        args.Add("--no-webui");
        args.AddRange(["--log-colors", "off"]);
        return new ServerLaunchPlan(Path.GetFullPath(serverExePath), args, ctx, 1, 0, AliasFor(role));
    }

    private static void AddNumber(List<string> args, string flag, double value)
    {
        if (double.IsFinite(value)) args.AddRange([flag, value.ToString("0.######", Inv)]);
    }
}
