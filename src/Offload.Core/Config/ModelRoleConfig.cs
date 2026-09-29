namespace Offload.Core.Config;

/// <summary>
/// Роли моделей в настройках (ROADMAP §5.2): какая установленная модель обслуживает роль, порт и адрес её сервера.
/// Роль quality — активная модель на основном сервере; fast/embed/rerank — вспомогательные серверы на своих портах.
/// </summary>
public static class ModelRoleConfig
{
    /// <summary>Все роли в порядке показа.</summary>
    public static IReadOnlyList<ModelRole> All { get; } = [ModelRole.Quality, ModelRole.Fast, ModelRole.Embed, ModelRole.Rerank, ModelRole.Fim];

    /// <summary>
    /// Вспомогательные роли MCP (свой llama-server, запуск по первому запросу из IDE через трей). Роль fim сюда не входит:
    /// её сервер держит запущенным служба автодополнения, а IDE обращается к нему напрямую, минуя MCP.
    /// </summary>
    public static IReadOnlyList<ModelRole> Auxiliary { get; } = [ModelRole.Fast, ModelRole.Embed, ModelRole.Rerank];

    /// <summary>Роли, которым назначается модель (все, кроме quality — это активная модель).</summary>
    public static IReadOnlyList<ModelRole> Assignable { get; } = [ModelRole.Fast, ModelRole.Embed, ModelRole.Rerank, ModelRole.Fim];

    /// <summary>Контекст сервера эмбеддингов/реранка: один запрос должен поместиться в пакет (-b/-ub = контекст).</summary>
    public const int EmbedContext = 8192;

    /// <summary>
    /// Контекст сервера автодополнения: окно вокруг курсора и фрагменты соседних файлов, которые шлёт IDE. Маленький контекст —
    /// быстрая обработка запроса и немного видеопамяти под KV-кэш.
    /// </summary>
    public const int FimContext = 8192;

    /// <summary>Порт сервера автодополнения по умолчанию — как у llama.vscode (new_completion_model_port = 8012).</summary>
    public const int DefaultFimPort = 8012;

    /// <summary>Ключ роли в конфиге и IPC: quality, fast, embed, rerank.</summary>
    public static string Key(this ModelRole role) => role switch
    {
        ModelRole.Fast => "fast",
        ModelRole.Embed => "embed",
        ModelRole.Rerank => "rerank",
        ModelRole.Fim => "fim",
        _ => "quality",
    };

    /// <summary>Разбор ключа роли (без учёта регистра). Пусто или неизвестно — false.</summary>
    public static bool TryParse(string? value, out ModelRole role)
    {
        foreach (var r in All)
        {
            if (string.Equals(value?.Trim(), r.Key(), StringComparison.OrdinalIgnoreCase))
            {
                role = r;
                return true;
            }
        }
        role = ModelRole.Quality;
        return false;
    }

    /// <summary>Назначение модели, которое нужно роли.</summary>
    public static ModelKind KindFor(ModelRole role) => role switch
    {
        ModelRole.Embed => ModelKind.Embed,
        ModelRole.Rerank => ModelKind.Rerank,
        ModelRole.Fim => ModelKind.Fim,
        _ => ModelKind.Chat,
    };

    /// <summary>Роль, которой назначается установленная модель такого назначения (chat — null: она становится активной).</summary>
    public static ModelRole? RoleFor(ModelKind kind) => kind switch
    {
        ModelKind.Embed => ModelRole.Embed,
        ModelKind.Rerank => ModelRole.Rerank,
        ModelKind.Fim => ModelRole.Fim,
        _ => null,
    };

    /// <summary>
    /// Может ли модель обслуживать роль: назначение совпадает с нужным роли. Пользовательскую модель (её назначение неизвестно)
    /// можно назначить и эмбеддингам/реранку/автодополнению — пользователь знает, что за файл он добавил.
    /// </summary>
    public static bool IsCompatible(InstalledModel model, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(model);
        var need = KindFor(role);
        return model.Kind == need || (model.IsCustom && need != ModelKind.Chat);
    }

    /// <summary>Идентификатор модели, назначенной роли (quality — активная модель); null — роль не назначена.</summary>
    public static string? AssignedId(this AppConfig cfg, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var roles = cfg.Models?.Roles;
        var id = role switch
        {
            ModelRole.Fast => roles?.Fast,
            ModelRole.Embed => roles?.Embed,
            ModelRole.Rerank => roles?.Rerank,
            ModelRole.Fim => roles?.Fim,
            _ => cfg.Models?.ActiveModelId,
        };
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>
    /// Установленная модель роли. quality — активная модель (<see cref="ConfigStore.ActiveModel"/>); вспомогательная роль —
    /// назначенная модель, если она установлена и подходит роли, иначе null.
    /// </summary>
    public static InstalledModel? RoleModel(this AppConfig cfg, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (role == ModelRole.Quality) return cfg.ActiveModel();
        if (cfg.AssignedId(role) is not { } id) return null;
        var model = cfg.Models?.Installed?.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        return model is not null && IsCompatible(model, role) ? model : null;
    }

    /// <summary>Назначить модель роли (null — снять назначение). quality здесь не меняется — это активная модель.</summary>
    public static void Assign(this AppConfig cfg, ModelRole role, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        cfg.Models.Roles ??= new ModelRoles();
        var id = string.IsNullOrWhiteSpace(modelId) ? null : modelId;
        switch (role)
        {
            case ModelRole.Fast: cfg.Models.Roles.Fast = id; break;
            case ModelRole.Embed: cfg.Models.Roles.Embed = id; break;
            case ModelRole.Rerank: cfg.Models.Roles.Rerank = id; break;
            case ModelRole.Fim: cfg.Models.Roles.Fim = id; break;
        }
    }

    /// <summary>
    /// Порт вспомогательного сервера роли: сохранённый (если сервер когда-то выбрал другой) или порт основного + 1/2/3.
    /// Автодополнение (fim) — фиксированный порт <see cref="DefaultFimPort"/> (IDE настраиваются на него один раз); если он был
    /// занят, сервер выбрал следующий свободный и сохранил его. Никогда не совпадает с портом основного сервера.
    /// Для quality — порт основного.
    /// </summary>
    public static int AuxPort(this ServerSettings s, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (role == ModelRole.Quality) return s.Port;
        if (s.AuxPorts is { } saved && saved.TryGetValue(role.Key(), out var p) && p is > 0 and <= 65535 && p != s.Port) return p;
        if (role == ModelRole.Fim) return s.Port == DefaultFimPort ? DefaultFimPort + 1 : DefaultFimPort;
        var offset = role switch
        {
            ModelRole.Fast => 1,
            ModelRole.Embed => 2,
            _ => 3,
        };
        var port = s.Port + offset;
        if (port > 65535) port = s.Port - offset;
        return port is > 0 and <= 65535 ? port : 8765 + offset;
    }

    /// <summary>
    /// Адрес, на котором трей запускает сервер роли (http://host:port). MCP-процесс по нему не обращается: порт мог занять чужой
    /// процесс, поэтому адрес работающего сервера роли MCP берёт только из ответа трея (IPC start-server, Data["baseUrl"]).
    /// </summary>
    public static string RoleBaseUrl(this AppConfig cfg, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return role == ModelRole.Quality ? cfg.Server.BaseUrl : LanServer.ClientBaseUrl(cfg.Server.LoopbackHost(), cfg.Server.AuxPort(role));
    }

    /// <summary>
    /// Контекст одного запроса на сервере роли. fast — рекомендованный контекст модели (как у основного в режиме по умолчанию);
    /// embed/rerank — не больше <see cref="EmbedContext"/>: весь вход обрабатывается одним пакетом;
    /// fim — <see cref="FimContext"/> (не больше родного контекста модели).
    /// </summary>
    public static int AuxContext(ModelRole role, InstalledModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var native = model.NativeContext > 0 ? model.NativeContext : int.MaxValue;
        var ctx = role switch
        {
            ModelRole.Embed or ModelRole.Rerank => Math.Min(EmbedContext, model.RecommendedContext > 0 ? Math.Max(model.RecommendedContext, 512) : EmbedContext),
            ModelRole.Fim => FimContext,
            _ => model.RecommendedContext > 0 ? model.RecommendedContext : 32768,
        };
        return Math.Max(512, Math.Min(ctx, native));
    }
}
