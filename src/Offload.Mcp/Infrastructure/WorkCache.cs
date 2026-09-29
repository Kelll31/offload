using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Mcp.Index;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Ключ кэша результатов модели: операция (инструмент + версия шаблона промпта), аргументы, хэши содержимого всех входов
/// (каждый файл, текст diff, выжимка лога) и — отдельно — нормализованный вопрос. Модель и настройки, влияющие на промпт,
/// добавляет <see cref="WorkCache"/>. Отпечаток (fingerprint) — всё, кроме вопроса: по нему ищутся похожие вопросы
/// над теми же входами. Любое изменение входа — другой отпечаток, то есть промах.
/// </summary>
/// <param name="tool">
/// Инструмент, чей это результат целиком (например, local_review_diff). Вызов из другого инструмента (review_diff внутри
/// pr_ready/security_review) — вложенный: попадание не выдаёт весь ответ внешнего инструмента за кэшированный.
/// </param>
internal sealed class WorkCacheKey(string op, string? tool = null)
{
    private readonly StringBuilder _parts = new();

    public string Op { get; } = op;

    /// <summary>Инструмент-владелец результата (null — тот, что вызывает).</summary>
    public string? Tool { get; } = tool;

    /// <summary>Обращения к модели и сгенерированные токены вызова до поиска в кэше: сохраняется только работа этой операции.</summary>
    internal int BaseModelCalls { get; set; }

    internal long BaseGenTokens { get; set; }

    /// <summary>Вопрос после нормализации (регистр, пробелы, завершающая пунктуация); null — у операции нет вопроса.</summary>
    public string? Question { get; private set; }

    /// <summary>Вопрос как задан (только пробелы схлопнуты): для сравнения по смыслу и проверки идентификаторов (CamelCase).</summary>
    public string? RawQuestion { get; private set; }

    /// <summary>Вектор вопроса, посчитанный при поиске похожих (сохраняется вместе с результатом).</summary>
    internal float[]? QuestionVector { get; set; }

    /// <summary>Модель эмбеддингов, которой посчитан <see cref="QuestionVector"/>.</summary>
    internal string? VectorModel { get; set; }

    /// <summary>Аргумент, влияющий на результат (формат, лимит ответа, фокус…).</summary>
    public WorkCacheKey Arg(string name, object? value)
    {
        _parts.Append("a:").Append(name).Append('=').Append(WorkCache.Sha(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "\0null")).Append('\n');
        return this;
    }

    /// <summary>Вход по содержимому: в ключ идёт хэш текста (сам текст не хранится).</summary>
    public WorkCacheKey Input(string name, string content)
    {
        _parts.Append("i:").Append(WorkCache.Sha(name)).Append('=').Append(WorkCache.Sha(content)).Append('\n');
        return this;
    }

    /// <summary>Прочитанные файлы: путь, содержимое (после маскирования секретов), признак неполного чтения; и строка покрытия.</summary>
    public WorkCacheKey Files(GatherResult gathered)
    {
        foreach (var f in gathered.Files.OrderBy(f => f.Display, StringComparer.Ordinal))
            Input("file:" + f.Display + (f.Truncated ? "#partial" : ""), f.Text);
        return Arg("coverage", gathered.CoverageLine());
    }

    /// <summary>Набор изменений git: описание, каждый файл (путь + текст diff), неотслеживаемые и исключённые файлы.</summary>
    public WorkCacheKey Diff(DiffSet set)
    {
        Arg("diff", set.Description).Arg("diff-truncated", set.Truncated);
        foreach (var f in set.Files) Input("diff:" + f.Path + (f.Binary ? "#bin" : ""), f.Text);
        Arg("untracked", string.Join('\n', set.Untracked));
        return Arg("excluded", string.Join('\n', set.Excluded));
    }

    public WorkCacheKey Ask(string question)
    {
        RawQuestion = string.Join(' ', question.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Question = WorkCache.NormalizeQuestion(question);
        return this;
    }

    internal string Fingerprint(string modelIdentity) => WorkCache.Sha($"{WorkCache.Version}\n{Op}\n{modelIdentity}\n{_parts}");

    internal string Exact(string fingerprint) => WorkCache.Sha(fingerprint + "\nq:" + (Question ?? ""));
}

/// <summary>Счётчики кэша за сессию MCP-процесса.</summary>
internal sealed class WorkCacheCounters
{
    private long _lookups;
    private long _hits;
    private long _similar;
    private long _stored;
    private long _skippedTokens;

    public long Lookups => Interlocked.Read(ref _lookups);
    public long Hits => Interlocked.Read(ref _hits);
    public long SimilarHits => Interlocked.Read(ref _similar);
    public long Stored => Interlocked.Read(ref _stored);

    /// <summary>Токены генерации локальной модели, которые не пришлось генерировать повторно.</summary>
    public long SkippedTokens => Interlocked.Read(ref _skippedTokens);

    internal void Lookup() => Interlocked.Increment(ref _lookups);

    internal void Hit(bool similar, long genTokens)
    {
        Interlocked.Increment(ref _hits);
        if (similar) Interlocked.Increment(ref _similar);
        Interlocked.Add(ref _skippedTokens, Math.Max(0, genTokens));
    }

    internal void Store() => Interlocked.Increment(ref _stored);
}

/// <summary>
/// Кэш результатов модели («не думать второй раз над тем, что уже решено на неизменённом коде») для инструментов только для
/// чтения: ask_files, review_diff (и модельная часть security_review), summarize_log (и разбор лога в local_verify),
/// commit_message, шаги модели find_context. Ключ — операция + версия Offload (MVID сборки: шаблоны промптов) + модель
/// (идентификатор, файл, квант, сэмплинг, роль, контекст) + дополнительный системный промпт + аргументы + хэши содержимого
/// всех входов + нормализованный вопрос. Похожий вопрос (косинус эмбеддингов ≥ <see cref="SemanticThreshold"/>, те же
/// идентификаторы/числа/отрицания) над теми же входами — тоже попадание, если назначена модель роли embed.
/// Не кэшируются: ошибки, отмена, обрезанные по max_tokens ответы, вызовы без модели, инструменты записи. Текст хранится
/// после <see cref="SecretRedactor"/>; при выключенном маскировании секретов кэш не используется. Сбой базы не мешает вызову.
/// </summary>
internal static partial class WorkCache
{
    /// <summary>Порог косинуса «тот же вопрос другими словами» (консервативный: лучше промах, чем чужой ответ).</summary>
    public const float SemanticThreshold = 0.93f;

    /// <summary>Сколько кандидатов с тем же отпечатком сравнивать по смыслу.</summary>
    private const int MaxSemanticCandidates = 16;

    /// <summary>Отпечаток сборки: новая версия Offload — другие шаблоны промптов, прежние результаты не используются.</summary>
    internal static string Version { get; } = typeof(WorkCache).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static bool Enabled(ToolContext ctx) =>
        ctx.Cfg.Mcp.WorkCache && ctx.Cfg.Mcp.RedactSecrets && ctx.Roots.Count > 0;

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Нормализация вопроса: регистр, пробелы, завершающая пунктуация.</summary>
    internal static string NormalizeQuestion(string question)
    {
        var s = string.Join(' ', question.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.TrimEnd('?', '.', '!', ' ', '…');
    }

    /// <summary>
    /// Идентичность модели для ключа: всё, от чего зависит её ответ при том же промпте. В клиентском режиме основная модель —
    /// на удалённом сервере: идентичность строится по его адресу, id модели и контексту, а не по своей установленной модели.
    /// </summary>
    internal static string ModelIdentity(AppConfig cfg, ModelRole role, InstalledModel? m)
    {
        if (role == ModelRole.Quality && cfg.IsRemote())
        {
            var ep = cfg.MainEndpoint();
            return string.Join('|',
                role.ToString(),
                "remote",
                ep.BaseUrl.ToLowerInvariant(),
                ep.Model,
                "ctx=" + (cfg.Remote?.ContextSize ?? 0).ToString(CultureInfo.InvariantCulture),
                "extra=" + Sha(cfg.Mcp.ExtraSystemPrompt ?? ""));
        }
        var s = m?.Sampling;
        var sampling = s is null ? "-" : FormattableString.Invariant($"{s.Temperature}/{s.TopP}/{s.TopK}/{s.MinP}/{s.RepeatPenalty}/{s.PresencePenalty}");
        return string.Join('|',
            role.ToString(),
            m?.Id ?? "none",
            Path.GetFileName(m?.FilePath ?? ""),
            m?.SizeBytes.ToString(CultureInfo.InvariantCulture) ?? "0",
            m?.Quant ?? "",
            m?.Reasoning.ToString() ?? "",
            sampling,
            "ctx=" + cfg.Server.ContextSize.ToString(CultureInfo.InvariantCulture),
            "extra=" + Sha(cfg.Mcp.ExtraSystemPrompt ?? ""));
    }

    /// <summary>Модель, которую вызов выбрал бы сейчас (без запуска сервера).</summary>
    private static string PredictedIdentity(ToolContext ctx)
    {
        var role = ModelRouting.Resolve(ctx.Cfg, ctx.Tool);
        return ModelIdentity(ctx.Cfg, role, ctx.Cfg.RoleModel(role));
    }

    /// <summary>Модель, которая фактически ответила (если к ней обращались), иначе предсказанная.</summary>
    private static string ActualIdentity(ToolContext ctx) =>
        ctx.ModelIfUsed is { } m ? ModelIdentity(ctx.Cfg, m.Role, m.Model) : PredictedIdentity(ctx);

    private static WorkCacheStore OpenStore(ToolContext ctx) =>
        WorkCacheStore.Open(ctx.Roots[0], Math.Clamp(ctx.Cfg.Mcp.WorkCacheMaxMb, 1, 4096) * 1024L * 1024,
            TimeSpan.FromDays(Math.Clamp(ctx.Cfg.Mcp.WorkCacheTtlDays, 1, 365)));

    internal static bool IsStoreError(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;

    /// <summary>
    /// Готовый результат для ключа или null (промах, кэш выключен, <paramref name="fresh"/>). При попадании — статистика
    /// вызова (сколько материала облачной модели не пришлось читать), пометка в подвале и счётчики сессии.
    /// </summary>
    public static async Task<string?> TryGetAsync(ToolContext ctx, WorkCacheKey key, bool fresh = false)
    {
        key.BaseModelCalls = ctx.Stats.ModelCalls;
        key.BaseGenTokens = ctx.Stats.CompletionTokens;
        if (!Enabled(ctx) || fresh) return null;
        ctx.State.WorkCache.Lookup();
        WorkCacheEntry? hit = null;
        var similar = false;
        try
        {
            using var store = OpenStore(ctx);
            var fp = key.Fingerprint(PredictedIdentity(ctx));
            hit = store.Get(key.Exact(fp));
            if (hit is null && key.Question is not null && Embedder.Configured(ctx.Cfg))
            {
                var candidates = store.Candidates(fp, key.Exact(fp), MaxSemanticCandidates);
                if (candidates.Count > 0)
                {
                    hit = await SimilarAsync(ctx, store, key, candidates).ConfigureAwait(false);
                    similar = hit is not null;
                }
            }
            if (hit is not null) store.Touch(hit.Key);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            Log.Warn("cache", $"кэш результатов недоступен: {ex.Message}");
            return null;
        }
        if (hit is null) return null;

        // Прочитанный материал — как при промахе (операция сама присваивает эти счётчики).
        ctx.Stats.FilesRead = hit.FilesRead;
        ctx.Stats.TokensRead = hit.TokensRead;
        ctx.State.WorkCache.Hit(similar, hit.GenTokens);
        var since = $" · inputs unchanged since {hit.Created.ToLocalTime():yyyy-MM-dd HH:mm}";
        if (key.Tool is not null && key.Tool != ctx.Tool)
            ctx.FooterNote ??= "cached model step" + since; // вложенный вызов: остальной ответ внешнего инструмента свежий
        else
            ctx.FooterNote = (similar ? "cached (same inputs, similar question)" : "cached") + since;
        Log.Info("cache", $"{ctx.Tool}: результат из кэша ({key.Op}{(similar ? ", похожий вопрос" : "")})");
        return hit.Result;
    }

    /// <summary>
    /// Сохранить результат, если вызов действительно работал с моделью и ни один её ответ не обрезан. Ошибки и отмена сюда
    /// не доходят (исключение прерывает инструмент раньше). Текст маскируется <see cref="SecretRedactor"/>.
    /// </summary>
    public static void Put(ToolContext ctx, WorkCacheKey key, string result)
    {
        if (!Enabled(ctx) || ctx.Ct.IsCancellationRequested || string.IsNullOrWhiteSpace(result)) return;
        if (ctx.Stats.ModelCalls - key.BaseModelCalls <= 0 || ctx.Stats.AnyTruncated) return;
        try
        {
            var fp = key.Fingerprint(ActualIdentity(ctx));
            using var store = OpenStore(ctx);
            store.Put(new WorkCacheEntry(key.Exact(fp), fp, key.Op,
                key.RawQuestion is { } q ? SecretRedactor.Redact(q) : null, SecretRedactor.Redact(result), WorkCacheStore.Clock(),
                ctx.Stats.FilesRead, ctx.Stats.TokensRead, Math.Max(0, ctx.Stats.CompletionTokens - key.BaseGenTokens),
                key.QuestionVector is { } v ? VectorMath.ToHalfBytes(v) : null, key.VectorModel));
            ctx.State.WorkCache.Store();
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            Log.Warn("cache", $"результат не сохранён в кэш: {ex.Message}");
        }
    }

    /// <summary>
    /// Один запрос к модели с кэшем по точному промпту (системный + пользовательский текст + лимит ответа + модель и её контекст):
    /// для шагов, у которых входы уже целиком в промпте (расширение запроса, ранжирование, план в find_context). Слот GPU
    /// занимается только при промахе. Обрезанный или пустой ответ не сохраняется.
    /// </summary>
    public static async Task<string> ChatAsync(ToolContext ctx, LocalModel model, string op, string system, string user, int maxTokens, string label)
    {
        var enabled = Enabled(ctx);
        var identity = ModelIdentity(ctx.Cfg, model.Role, model.Model) + "|n_ctx=" + model.ContextPerSlot.ToString(CultureInfo.InvariantCulture);
        var key = new WorkCacheKey(op).Input("system", system).Input("user", user).Arg("max", maxTokens);
        var exact = key.Exact(key.Fingerprint(identity));
        if (enabled)
        {
            ctx.State.WorkCache.Lookup();
            try
            {
                using var store = OpenStore(ctx);
                if (store.Get(exact) is { } hit)
                {
                    store.Touch(hit.Key);
                    ctx.State.WorkCache.Hit(false, hit.GenTokens);
                    ctx.FooterNote ??= $"cached model step · inputs unchanged since {hit.Created.ToLocalTime():yyyy-MM-dd HH:mm}";
                    return hit.Result;
                }
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                Log.Warn("cache", $"кэш результатов недоступен: {ex.Message}");
            }
        }

        ModelReply reply;
        await using (await GpuQueue.AcquireAsync(ctx, ctx.Ct).ConfigureAwait(false))
            reply = await model.ChatAsync(system, user, maxTokens, label, ctx.Ct).ConfigureAwait(false);
        if (enabled && !reply.Truncated && reply.Text.Trim().Length > 0)
        {
            try
            {
                using var store = OpenStore(ctx);
                store.Put(new WorkCacheEntry(exact, key.Fingerprint(identity), op, null, SecretRedactor.Redact(reply.Text), WorkCacheStore.Clock(),
                    0, 0, reply.Raw.CompletionTokens, null, null));
                ctx.State.WorkCache.Store();
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                Log.Warn("cache", $"результат не сохранён в кэш: {ex.Message}");
            }
        }
        return reply.Text;
    }

    /// <summary>
    /// Похожий вопрос над теми же входами: косинус векторов вопросов ≥ <see cref="SemanticThreshold"/> и совпадают
    /// «значимые» слова (идентификаторы, пути, числа, строки в кавычках) и отрицания. Недостающие векторы кандидатов
    /// считаются и сохраняются. Модель эмбеддингов недоступна — null (промах, без ошибки).
    /// </summary>
    private static async Task<WorkCacheEntry?> SimilarAsync(ToolContext ctx, WorkCacheStore store, WorkCacheKey key, List<WorkCacheEntry> candidates)
    {
        var compatible = candidates.Where(c => c.Question is not null && Compatible(key.RawQuestion!, c.Question)).ToList();
        if (compatible.Count == 0) return null;
        var embedder = await Embedder.ConnectAsync(ctx).ConfigureAwait(false);
        if (embedder is null) return null;
        try
        {
            var missing = compatible.Where(c => c.Vector is null || c.VectorModel != embedder.ModelId).ToList();
            var texts = new List<string> { key.RawQuestion! };
            texts.AddRange(missing.Select(c => c.Question!));
            var vectors = await embedder.EmbedAsync(texts).ConfigureAwait(false);
            key.QuestionVector = vectors[0];
            key.VectorModel = embedder.ModelId;
            for (var i = 0; i < missing.Count; i++) store.SetVector(missing[i].Key, vectors[i + 1], embedder.ModelId);
            WorkCacheEntry? best = null;
            var bestScore = float.MinValue;
            foreach (var c in compatible)
            {
                var idx = missing.IndexOf(c);
                var v = idx >= 0 ? vectors[idx + 1] : VectorMath.FromHalfBytes(c.Vector!);
                var score = VectorMath.Dot(key.QuestionVector, v);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return bestScore >= SemanticThreshold ? best : null;
        }
        catch (Exception ex) when (ex is Offload.Llama.LlamaApiException or ToolException)
        {
            Log.Warn("cache", "сравнение вопросов по смыслу недоступно: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Вопросы могут считаться одним и тем же, только если у них одинаковы значимые слова (идентификаторы с заглавными
    /// внутри, «_», «.», «/», цифры, строки в кавычках) и слова-отрицания: «где вызывается Foo» и «где вызывается Bar»
    /// по смыслу близки, но это разные вопросы.
    /// </summary>
    internal static bool Compatible(string a, string b) =>
        Salient(a).SetEquals(Salient(b)) && Negations(a).SetEquals(Negations(b));

    private static readonly HashSet<string> NegationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "no", "never", "without", "except", "dont", "don't", "doesnt", "doesn't", "isnt", "isn't", "cannot", "can't", "none",
        "не", "нет", "без", "никогда", "кроме", "нельзя",
    };

    private static HashSet<string> Negations(string q) =>
        Word().Matches(q).Select(m => m.Value.ToLowerInvariant()).Where(NegationWords.Contains).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Salient(string q)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Quoted().Matches(q)) set.Add(m.Value.Trim('"', '\'', '`', '«', '»').ToLowerInvariant());
        foreach (Match m in Token().Matches(q))
        {
            var t = m.Value.Trim('.', '/', '-');
            if (t.Length == 0) continue;
            var special = t.Any(char.IsDigit) || t.Contains('_') || t.Contains('.') || t.Contains('/') || t.Contains('\\')
                          || t.Skip(1).Any(char.IsUpper);
            if (special) set.Add(t.ToLowerInvariant());
        }
        return set;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_'’]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"[\p{L}\p{Nd}_./\\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"""[^""]{1,200}""|'[^']{1,200}'|`[^`]{1,200}`|«[^»]{1,200}»", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();
}

/// <summary>Запись кэша результатов.</summary>
internal sealed record WorkCacheEntry(string Key, string Fingerprint, string Op, string? Question, string Result, DateTimeOffset Created,
    int FilesRead, long TokensRead, long GenTokens, byte[]? Vector, string? VectorModel);

/// <summary>Сводка кэша рабочей папки (для local_status).</summary>
internal sealed record WorkCacheStats(int Entries, long Bytes, long Hits);

/// <summary>
/// Хранилище кэша результатов: <c>DataDir/cache/work/&lt;хэш корня&gt;.db</c> (SQLite, WAL, busy_timeout — несколько
/// MCP-процессов на одну базу). Предел размера — удаление давно не использованных (LRU) до 90% предела; срок жизни — от
/// времени создания (просроченные удаляются при первом открытии в процессе и не отдаются никогда).
/// </summary>
internal sealed class WorkCacheStore : IDisposable
{
    public const int SchemaVersion = 1;

    /// <summary>Часы (для тестов срока жизни).</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private static readonly HashSet<string> Pruned = new(StringComparer.OrdinalIgnoreCase);

    private readonly SqliteConnection _conn;
    private readonly long _maxBytes;
    private readonly TimeSpan _ttl;

    private WorkCacheStore(SqliteConnection conn, long maxBytes, TimeSpan ttl, string dbPath)
    {
        _conn = conn;
        _maxBytes = maxBytes;
        _ttl = ttl;
        DbPath = dbPath;
    }

    public string DbPath { get; }

    public static string DirPath => Path.Combine(AppPaths.DataDir, "cache", "work");

    public static string DbPathFor(string root) => Path.Combine(DirPath, IndexStore.RootHash(root) + ".db");

    public static WorkCacheStore Open(string root, long maxBytes, TimeSpan ttl)
    {
        var dbPath = DbPathFor(root);
        Directory.CreateDirectory(DirPath);
        for (var attempt = 0; ; attempt++)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString());
            try
            {
                conn.Open();
                EnsureSchema(conn);
                var store = new WorkCacheStore(conn, maxBytes, ttl, dbPath);
                store.PruneOnce();
                return store;
            }
            catch (SqliteException ex) when (attempt == 0 && ex.SqliteErrorCode is 11 or 26)
            {
                // SQLITE_CORRUPT / SQLITE_NOTADB: кэш испорчен — удаляем, он наполнится заново.
                conn.Dispose();
                Log.Warn("cache", $"база кэша {dbPath} повреждена ({ex.Message}); создаю заново");
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    File.Delete(dbPath + suffix);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }
    }

    /// <summary>Сводка без создания базы: null — кэша для этой папки ещё нет или он недоступен.</summary>
    public static WorkCacheStats? TryStats(string root)
    {
        var path = DbPathFor(root);
        if (!File.Exists(path)) return null;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5,
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(size), 0), COALESCE(SUM(hits), 0) FROM entries";
            using var r = cmd.ExecuteReader();
            return r.Read() ? new WorkCacheStats(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2)) : null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        Exec(conn, null, "PRAGMA busy_timeout=15000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema'";
            if (cmd.ExecuteScalar() as string != SchemaVersion.ToString(CultureInfo.InvariantCulture))
            {
                Exec(conn, tx,
                    """
                    DROP TABLE IF EXISTS entries;
                    CREATE TABLE entries(key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, op TEXT NOT NULL, question TEXT, result TEXT NOT NULL,
                        created INTEGER NOT NULL, used INTEGER NOT NULL, hits INTEGER NOT NULL DEFAULT 0, files_read INTEGER NOT NULL,
                        tokens_read INTEGER NOT NULL, gen_tokens INTEGER NOT NULL, vec BLOB, vec_model TEXT, size INTEGER NOT NULL) WITHOUT ROWID;
                    CREATE INDEX entries_fp ON entries(fingerprint);
                    CREATE INDEX entries_used ON entries(used);
                    INSERT OR REPLACE INTO meta(key, value) VALUES('schema', '1');
                    """);
            }
        }
        tx.Commit();
    }

    private static long Now => Clock().ToUnixTimeMilliseconds();

    private long ExpiredBefore => Now - (long)_ttl.TotalMilliseconds;

    /// <summary>Раз за процесс на базу: удалить просроченные записи.</summary>
    private void PruneOnce()
    {
        lock (Pruned)
        {
            if (!Pruned.Add(DbPath)) return;
        }
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM entries WHERE created < $t";
            cmd.Parameters.AddWithValue("$t", ExpiredBefore);
            var n = cmd.ExecuteNonQuery();
            if (n > 0) Log.Info("cache", $"удалены просроченные результаты кэша: {n}");
        }
        catch (SqliteException ex)
        {
            Log.Debug("cache", "очистка кэша: " + ex.Message);
        }
    }

    /// <summary>Только для тестов: разрешить повторную очистку в этом процессе.</summary>
    internal static void ResetPrune()
    {
        lock (Pruned) Pruned.Clear();
    }

    private const string Columns = "key, fingerprint, op, question, result, created, files_read, tokens_read, gen_tokens, vec, vec_model";

    private static WorkCacheEntry Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)), r.GetInt32(6), r.GetInt64(7), r.GetInt64(8),
        r.IsDBNull(9) ? null : (byte[])r.GetValue(9), r.IsDBNull(10) ? null : r.GetString(10));

    /// <summary>Запись по точному ключу (просроченная — как отсутствующая).</summary>
    public WorkCacheEntry? Get(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM entries WHERE key = $k AND created >= $t";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$t", ExpiredBefore);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Непросроченные записи с тем же отпечатком входов, но другим вопросом (свежие первыми).</summary>
    public List<WorkCacheEntry> Candidates(string fingerprint, string exceptKey, int take)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM entries WHERE fingerprint = $f AND key <> $k AND question IS NOT NULL AND created >= $t ORDER BY used DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$f", fingerprint);
        cmd.Parameters.AddWithValue("$k", exceptKey);
        cmd.Parameters.AddWithValue("$t", ExpiredBefore);
        cmd.Parameters.AddWithValue("$n", take);
        using var r = cmd.ExecuteReader();
        var list = new List<WorkCacheEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Отметить использование (LRU) и попадание.</summary>
    public void Touch(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET used = $u, hits = hits + 1 WHERE key = $k";
        cmd.Parameters.AddWithValue("$u", Now);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    public void SetVector(string key, float[] vector, string model)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET vec = $v, vec_model = $m WHERE key = $k";
        cmd.Parameters.AddWithValue("$v", VectorMath.ToHalfBytes(vector));
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сохранить (заменить) запись и удержать базу в пределе размера.</summary>
    public void Put(WorkCacheEntry e)
    {
        var size = (long)(e.Result.Length + (e.Question?.Length ?? 0)) * 2 + (e.Vector?.Length ?? 0) + 256;
        if (size > _maxBytes / 4) return; // одна запись не должна вытеснять весь кэш
        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO entries(key, fingerprint, op, question, result, created, used, hits, files_read, tokens_read, gen_tokens, vec, vec_model, size)
                VALUES($k, $f, $o, $q, $r, $c, $c, 0, $fr, $tr, $g, $v, $vm, $s)
                """;
            cmd.Parameters.AddWithValue("$k", e.Key);
            cmd.Parameters.AddWithValue("$f", e.Fingerprint);
            cmd.Parameters.AddWithValue("$o", e.Op);
            cmd.Parameters.AddWithValue("$q", (object?)e.Question ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", e.Result);
            cmd.Parameters.AddWithValue("$c", e.Created.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$fr", e.FilesRead);
            cmd.Parameters.AddWithValue("$tr", e.TokensRead);
            cmd.Parameters.AddWithValue("$g", e.GenTokens);
            cmd.Parameters.AddWithValue("$v", (object?)e.Vector ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$vm", (object?)e.VectorModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.ExecuteNonQuery();
        }
        EvictLocked(tx);
        tx.Commit();
    }

    /// <summary>Сверх предела — удалить давно не использованные записи, пока не останется 90% предела.</summary>
    private void EvictLocked(SqliteTransaction tx)
    {
        long total;
        using (var sum = _conn.CreateCommand())
        {
            sum.Transaction = tx;
            sum.CommandText = "SELECT COALESCE(SUM(size), 0) FROM entries";
            total = Convert.ToInt64(sum.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        if (total <= _maxBytes) return;
        var target = _maxBytes * 9 / 10;
        var victims = new List<string>();
        using (var sel = _conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT key, size FROM entries ORDER BY used ASC";
            using var r = sel.ExecuteReader();
            while (total > target && r.Read())
            {
                victims.Add(r.GetString(0));
                total -= r.GetInt64(1);
            }
        }
        using var del = _conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM entries WHERE key = $k";
        var k = del.Parameters.Add("$k", SqliteType.Text);
        foreach (var v in victims)
        {
            k.Value = v;
            del.ExecuteNonQuery();
        }
    }

    /// <summary>Число записей (для тестов и статистики).</summary>
    public int Count()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
