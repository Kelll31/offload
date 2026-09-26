using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Mcp.Infrastructure;

namespace Offload.Mcp.Index;

/// <summary>Что синхронизировать: корень (определяет файл базы), корни для путей, найденные файлы, лимит чтения, удалять ли пропавшие.</summary>
internal sealed record IndexRequest(string Root, IReadOnlyList<string> Roots, IReadOnlyList<string> Files, int MaxBytes, bool Prune);

/// <summary>Итог синхронизации: файлов, разобрано заново, только обновлены отметки, удалено, время, на диске ли индекс.</summary>
internal sealed record IndexSyncStats(int Files, int Parsed, int Touched, int Removed, TimeSpan Elapsed, bool Persistent);

/// <summary>Строка таблицы files.</summary>
internal sealed class FileRow
{
    public long Id { get; set; }
    public required string Path { get; init; }
    public long Size { get; set; }
    public long Mtime { get; set; }
    public long LimitBytes { get; set; }
    public string Sha { get; set; } = "";
    public int Redacted { get; set; }
    public bool Ok { get; set; }
    public CodeLang Lang { get; set; }
    public int Lines { get; set; }
    public long Chars { get; set; }
    public int Tokens { get; set; }
    public bool Truncated { get; set; }
    public bool Generated { get; set; }
    public bool LongTokens { get; set; }
    public string? Entry { get; set; }
}

/// <summary>Открытое соединение с индексом после синхронизации; запросы идут в одном снимке (транзакция чтения WAL).</summary>
internal sealed class IndexSession : IDisposable
{
    private SqliteTransaction? _snapshot;

    public IndexSession(SqliteConnection connection, bool persistent, string? dbPath, Dictionary<string, FileRow> rows, IndexSyncStats stats)
    {
        Connection = connection;
        Persistent = persistent;
        DbPath = dbPath;
        Rows = rows;
        Stats = stats;
    }

    public SqliteConnection Connection { get; }
    public bool Persistent { get; }
    public string? DbPath { get; }
    /// <summary>Актуальные строки files для найденных в этом вызове файлов (путь → строка); устаревшие и нечитаемые сюда не входят.</summary>
    public Dictionary<string, FileRow> Rows { get; }
    public IndexSyncStats Stats { get; }

    /// <summary>
    /// Все дальнейшие запросы видят одно состояние базы, даже если другой процесс в это время её обновляет. BEGIN DEFERRED сам
    /// снимок не фиксирует — в WAL он берётся первым чтением, поэтому читаем сразу. Вызывать, пока держится блокировка записи:
    /// тогда снимок совпадает с <see cref="Rows"/>.
    /// </summary>
    public void BeginSnapshot()
    {
        if (_snapshot is not null) return;
        _snapshot = Connection.BeginTransaction(deferred: true);
        using var pin = Command("SELECT 1 FROM meta LIMIT 1");
        pin.ExecuteScalar();
    }

    public SqliteCommand Command(string sql)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _snapshot;
        return cmd;
    }

    public void Dispose()
    {
        _snapshot?.Dispose();
        Connection.Dispose();
    }
}

/// <summary>
/// Постоянный инкрементальный индекс кода в SQLite: <c>DataDir/index/&lt;хэш корня&gt;-&lt;версия извлекателя&gt;.db</c> (WAL,
/// ожидание блокировки) — один на все MCP-процессы и IDE одной версии Offload (процессы разных версий пишут в разные файлы и не
/// перестраивают индекс друг друга). Таблицы: files (путь, размер, время, sha замаскированного текста, число замаскированных секретов),
/// symbols (объявления), terms/term_parts/refs (обратный индекс идентификаторов и их частей), calls (вызывающий → имя вызываемого).
/// Обновление — по размеру и времени изменения (при совпадении sha — только отметки), запись — под файловой блокировкой, чтобы
/// несколько процессов не разбирали одно и то же; id строк не переиспользуются (счётчики в meta). Всё хранимое получено из текста после <see cref="SecretRedactor"/>: значения
/// секретов в базу не попадают. Индекс — только ускоритель: какие файлы видны инструменту, решает FileGatherer/PathGuard на
/// каждом вызове, запросы ограничены этим списком. База недоступна — индекс строится в памяти на время вызова.
/// </summary>
internal static class IndexStore
{
    /// <summary>Версия схемы: при несовпадении таблицы пересоздаются.</summary>
    public const int SchemaVersion = 2;

    private const int BatchFiles = 400;

    /// <summary>После скольких перезаписанных/удалённых файлов чистить словарь от слов без вхождений.</summary>
    internal static int OrphanSweepChurn { get; set; } = 20_000;

    /// <summary>
    /// Сколько ждать блокировку записи. Дольше — значит, другой процесс строит индекс с нуля: вызов не ждёт его минутами
    /// (клиенты MCP обрывают долгие вызовы), а сразу строит индекс в памяти.
    /// </summary>
    internal static TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(45);

    /// <summary>Индекс того же корня от другой версии Offload удаляется, если к нему не обращались дольше этого срока.</summary>
    private static readonly TimeSpan SupersededAfter = TimeSpan.FromDays(3);

    private static int _swept;

    /// <summary>
    /// Отпечаток извлекателя (MVID сборки): новая версия Offload.Mcp — другой разбор символов, маскирование или токенизация.
    /// Входит в имя файла базы: старые MCP-процессы (IDE держит их после обновления Offload) и новые, установленная и
    /// dev-сборка работают каждый со своим индексом, а не стирают индекс друг друга на каждом вызове.
    /// </summary>
    internal static string Extractor { get; } = typeof(IndexStore).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static string IndexDir => Path.Combine(AppPaths.DataDir, "index");

    public static string DbPathFor(string root) => Path.Combine(IndexDir, RootHash(root) + "-" + Extractor[..8] + ".db");

    /// <summary>Только для тестов: разрешить повторную очистку старых индексов в этом процессе.</summary>
    internal static void ResetSweep() => Volatile.Write(ref _swept, 0);

    public static string RootHash(string root)
    {
        var norm = Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..20];
    }

    /// <summary>Синхронизировать индекс корня с найденными файлами и открыть его для запросов.</summary>
    public static async Task<IndexSession> OpenAsync(IndexRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var stamps = Stamp(req.Files, ct);
        string? dbPath = null;
        try
        {
            dbPath = DbPathFor(req.Root);
            Directory.CreateDirectory(IndexDir);
            SweepStale(dbPath);
            using var fileLock = await AcquireLockAsync(dbPath + ".lock", ct).ConfigureAwait(false);
            var session = OpenPersistent(dbPath, req, stamps, sw, ct);
            try
            {
                MarkUsed(dbPath);
                // Снимок — до освобождения блокировки: иначе другой процесс успеет записать, и Rows разойдутся с базой.
                session.BeginSnapshot();
            }
            catch
            {
                session.Dispose();
                throw;
            }
            return session;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            Log.Warn("index", $"постоянный индекс недоступен ({dbPath}): {ex.Message}; индекс строится в памяти");
            var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            try
            {
                EnsureSchema(conn, persistent: false);
                var (rows, stats) = Sync(conn, req, stamps, persistent: false, sw, ct);
                return new IndexSession(conn, false, null, rows, stats);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }
    }

    private static IndexSession OpenPersistent(string dbPath, IndexRequest req, (long Size, long Mtime)[] stamps, Stopwatch sw, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 60,
            }.ToString());
            try
            {
                conn.Open();
                EnsureSchema(conn, persistent: true);
                var (rows, stats) = Sync(conn, req, stamps, persistent: true, sw, ct);
                return new IndexSession(conn, true, dbPath, rows, stats);
            }
            catch (SqliteException ex) when (attempt == 0 && ex.SqliteErrorCode is 11 or 26)
            {
                // SQLITE_CORRUPT / SQLITE_NOTADB: база испорчена — удаляем и строим заново.
                conn.Dispose();
                Log.Warn("index", $"индекс {dbPath} повреждён ({ex.Message}); перестраиваю");
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

    /// <summary>Размер и время изменения каждого файла (параллельно); пропавший файл — (-1, -1).</summary>
    private static (long Size, long Mtime)[] Stamp(IReadOnlyList<string> files, CancellationToken ct)
    {
        var stamps = new (long, long)[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, i =>
        {
            try
            {
                var info = new FileInfo(files[i]);
                stamps[i] = info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, -1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stamps[i] = (-1, -1);
            }
        });
        return stamps;
    }

    /// <summary>Межпроцессная блокировка записи: файл, открытый без совместного доступа (освобождается ОС и при падении процесса).</summary>
    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var delay = 25;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (sw.Elapsed < LockTimeout)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Math.Min(250, delay * 2);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"индекс занят другим процессом дольше {LockTimeout.TotalSeconds:0.#} с (вероятно, строится с нуля)", ex);
            }
        }
    }

    /// <summary>Чтение не меняет файл базы — отмечаем использование временем изменения (раз в сутки), чтобы очистка не удалила живой индекс.</summary>
    private static void MarkUsed(string dbPath)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(dbPath) > TimeSpan.FromDays(1)) File.SetLastWriteTimeUtc(dbPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не критично: в худшем случае индекс будет перестроен после очистки.
        }
    }

    /// <summary>
    /// Раз за процесс: удалить индексы корней, к которым не обращались больше 45 дней, и индексы этого же корня от других версий
    /// Offload, не использованные дольше 3 дней (живой процесс старой версии отмечает свой индекс не реже раза в сутки).
    /// </summary>
    private static void SweepStale(string current)
    {
        if (Interlocked.Exchange(ref _swept, 1) == 1) return;
        var name = Path.GetFileName(current);
        var sameRoot = name[..name.LastIndexOf('-')]; // хэш корня (подходит и к старому имени без версии)
        try
        {
            foreach (var db in Directory.EnumerateFiles(IndexDir, "*.db"))
            {
                if (string.Equals(db, current, StringComparison.OrdinalIgnoreCase)) continue;
                var keep = Path.GetFileName(db).StartsWith(sameRoot, StringComparison.OrdinalIgnoreCase) ? SupersededAfter : StaleAfter;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(db) < keep) continue;
                try
                {
                    foreach (var suffix in new[] { "", "-wal", "-shm", ".lock" })
                        File.Delete(db + suffix);
                    Log.Info("index", $"удалён давно не используемый индекс {Path.GetFileName(db)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Занят другим процессом — попробуем в следующий раз.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("index", "очистка старых индексов: " + ex.Message);
        }
    }

    // ───────────────────────── схема ─────────────────────────

    private const string CreateSql =
        """
        CREATE TABLE files(id INTEGER PRIMARY KEY, path TEXT NOT NULL COLLATE NOCASE UNIQUE, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
            limit_bytes INTEGER NOT NULL, sha TEXT NOT NULL, redacted INTEGER NOT NULL, ok INTEGER NOT NULL, lang INTEGER NOT NULL,
            lines INTEGER NOT NULL, chars INTEGER NOT NULL, tokens INTEGER NOT NULL, truncated INTEGER NOT NULL, generated INTEGER NOT NULL,
            longtok INTEGER NOT NULL, entry TEXT);
        CREATE TABLE symbols(id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, container TEXT,
            line INTEGER NOT NULL, end_line INTEGER NOT NULL, signature TEXT NOT NULL);
        CREATE INDEX symbols_file ON symbols(file_id);
        CREATE INDEX symbols_name ON symbols(name);
        CREATE INDEX symbols_name_nocase ON symbols(name COLLATE NOCASE);
        CREATE TABLE terms(id INTEGER PRIMARY KEY, text TEXT NOT NULL UNIQUE);
        CREATE TABLE term_parts(part TEXT NOT NULL, term_id INTEGER NOT NULL, PRIMARY KEY(part, term_id)) WITHOUT ROWID;
        CREATE TABLE refs(term_id INTEGER NOT NULL, file_id INTEGER NOT NULL, tf INTEGER NOT NULL, lines TEXT NOT NULL,
            PRIMARY KEY(term_id, file_id)) WITHOUT ROWID;
        CREATE INDEX refs_file ON refs(file_id);
        CREATE TABLE calls(caller_id INTEGER NOT NULL, file_id INTEGER NOT NULL, callee TEXT NOT NULL, chain TEXT, line INTEGER NOT NULL);
        CREATE INDEX calls_callee ON calls(callee);
        CREATE INDEX calls_caller ON calls(caller_id);
        CREATE INDEX calls_file ON calls(file_id);
        """;

    private const string DropSql =
        "DROP TABLE IF EXISTS files; DROP TABLE IF EXISTS symbols; DROP TABLE IF EXISTS terms; DROP TABLE IF EXISTS term_parts; " +
        "DROP TABLE IF EXISTS refs; DROP TABLE IF EXISTS calls;";

    /// <summary>Режим WAL и ожидание блокировок; таблицы нужной версии (иначе — пересоздать).</summary>
    internal static void EnsureSchema(SqliteConnection conn, bool persistent)
    {
        Exec(conn, null, "PRAGMA busy_timeout=15000;");
        if (persistent) Exec(conn, null, "PRAGMA journal_mode=WAL;");
        Exec(conn, null, "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-20000;");
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        var schema = Meta(conn, tx, "schema");
        var extractor = Meta(conn, tx, "extractor");
        if (schema != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) || extractor != Extractor)
        {
            if (schema is not null) Log.Info("index", $"индекс другой версии (схема {schema}) — перестраиваю");
            Exec(conn, tx, DropSql);
            Exec(conn, tx, CreateSql);
            SetMeta(conn, tx, "schema", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetMeta(conn, tx, "extractor", Extractor);
        }
        tx.Commit();
    }

    private static string? Meta(SqliteConnection conn, SqliteTransaction? tx, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection conn, SqliteTransaction? tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ───────────────────────── синхронизация ─────────────────────────

    private static Dictionary<string, FileRow> LoadRows(SqliteConnection conn)
    {
        var rows = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, size, mtime, limit_bytes, sha, redacted, ok, lang, lines, chars, tokens, truncated, generated, longtok, entry FROM files";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new FileRow
            {
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                Size = r.GetInt64(2),
                Mtime = r.GetInt64(3),
                LimitBytes = r.GetInt64(4),
                Sha = r.GetString(5),
                Redacted = r.GetInt32(6),
                Ok = r.GetInt64(7) != 0,
                Lang = (CodeLang)r.GetInt32(8),
                Lines = r.GetInt32(9),
                Chars = r.GetInt64(10),
                Tokens = r.GetInt32(11),
                Truncated = r.GetInt64(12) != 0,
                Generated = r.GetInt64(13) != 0,
                LongTokens = r.GetInt64(14) != 0,
                Entry = r.IsDBNull(15) ? null : r.GetString(15),
            };
            rows[row.Path] = row;
        }
        return rows;
    }

    /// <summary>Файл не изменился: те же размер и время, и для обрезанного файла — тот же лимит чтения.</summary>
    private static bool UpToDate(FileRow row, long size, long mtime, int maxBytes) =>
        row.Size == size && row.Mtime == mtime && (!row.Truncated && size <= maxBytes || row.LimitBytes == maxBytes);

    private static (Dictionary<string, FileRow> Rows, IndexSyncStats Stats) Sync(SqliteConnection conn, IndexRequest req,
        (long Size, long Mtime)[] stamps, bool persistent, Stopwatch sw, CancellationToken ct)
    {
        var rows = LoadRows(conn);
        var toParse = new List<int>();
        for (var i = 0; i < req.Files.Count; i++)
        {
            var (size, mtime) = stamps[i];
            if (size < 0) continue;
            if (!rows.TryGetValue(req.Files[i], out var row) || !UpToDate(row, size, mtime, req.MaxBytes)) toParse.Add(i);
        }

        var removed = new List<FileRow>();
        if (req.Prune)
        {
            var present = new HashSet<string>(req.Files, StringComparer.OrdinalIgnoreCase);
            var prefix = Path.GetFullPath(req.Root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            removed.AddRange(rows.Values.Where(r => r.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !present.Contains(r.Path)));
        }

        var parsed = 0;
        var touched = 0;
        var replaced = 0;
        if (toParse.Count > 0 || removed.Count > 0)
        {
            var writer = new IndexWriter(conn, preloadTerms: toParse.Count > 300);
            if (removed.Count > 0)
            {
                using var tx = conn.BeginTransaction();
                foreach (var r in removed)
                {
                    writer.DeleteFile(tx, r.Id);
                    rows.Remove(r.Path);
                }
                writer.SaveSequences(tx);
                tx.Commit();
            }
            // Разбор — параллельно и пачками: память не растёт с размером репозитория, запись — короткими транзакциями.
            for (var b = 0; b < toParse.Count; b += BatchFiles)
            {
                ct.ThrowIfCancellationRequested();
                var batch = toParse.Skip(b).Take(BatchFiles).ToList();
                var data = new FileData?[batch.Count];
                Parallel.For(0, batch.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
                    k => data[k] = FileData.Extract(req.Files[batch[k]], req.Roots, req.MaxBytes));
                using var tx = conn.BeginTransaction();
                for (var k = 0; k < batch.Count; k++)
                {
                    var i = batch[k];
                    var d = data[k];
                    if (d is null) continue; // временно нечитаем — попробуем в следующий раз
                    var path = req.Files[i];
                    rows.TryGetValue(path, out var row);
                    if (row is not null && row.Ok == d.Ok && row.Sha == d.Sha && row.Truncated == d.Truncated)
                    {
                        // Содержимое то же (git checkout, touch) — только новые отметки.
                        row.Size = stamps[i].Size;
                        row.Mtime = stamps[i].Mtime;
                        row.LimitBytes = req.MaxBytes;
                        writer.Touch(tx, row);
                        touched++;
                        continue;
                    }
                    if (row is not null) replaced++;
                    rows[path] = writer.WriteFile(tx, row, path, stamps[i].Size, stamps[i].Mtime, req.MaxBytes, d);
                    parsed++;
                }
                writer.SaveSequences(tx);
                tx.Commit();
            }
            CollectOrphanTerms(conn, replaced + removed.Count);
        }
        // Для запросов — только файлы этого вызова с актуальной строкой (файл, который не удалось перечитать, не показываем устаревшим).
        var fresh = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < req.Files.Count; i++)
            if (stamps[i].Size >= 0 && rows.TryGetValue(req.Files[i], out var row) && UpToDate(row, stamps[i].Size, stamps[i].Mtime, req.MaxBytes))
                fresh[req.Files[i]] = row;
        var stats = new IndexSyncStats(req.Files.Count, parsed, touched, removed.Count, sw.Elapsed, persistent);
        if (parsed + touched + removed.Count > 0)
            Log.Info("index", $"индекс {(persistent ? "на диске" : "в памяти")}: файлов {req.Files.Count}, разобрано {parsed}, отметки {touched}, " +
                              $"удалено {removed.Count}, {sw.ElapsedMilliseconds} мс");
        return (fresh, stats);
    }

    /// <summary>
    /// Словарь идентификаторов только растёт: после изменения/удаления файлов в нём остаются слова, которых больше нигде нет.
    /// Счётчик «оборота» копится в meta; после ~20 тыс. перезаписанных/удалённых файлов такие слова удаляются одним проходом.
    /// </summary>
    private static void CollectOrphanTerms(SqliteConnection conn, int churn)
    {
        if (churn <= 0) return;
        using var tx = conn.BeginTransaction();
        var total = churn + (int.TryParse(Meta(conn, tx, "churn"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0);
        if (total >= OrphanSweepChurn)
        {
            Exec(conn, tx, "DELETE FROM terms WHERE id NOT IN (SELECT term_id FROM refs); DELETE FROM term_parts WHERE term_id NOT IN (SELECT id FROM terms);");
            total = 0;
        }
        SetMeta(conn, tx, "churn", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tx.Commit();
    }

    /// <summary>Подготовленные команды записи одного прохода синхронизации.</summary>
    private sealed class IndexWriter
    {
        private readonly SqliteConnection _conn;
        private readonly Dictionary<string, long> _terms = new(StringComparer.Ordinal);
        private readonly bool _preloaded;
        private long _nextFile;
        private long _nextSymbol;
        private long _nextTerm;

        public IndexWriter(SqliteConnection conn, bool preloadTerms)
        {
            _conn = conn;
            // Id не переиспользуются: счётчики в meta не уменьшаются, когда удалены строки с наибольшими id.
            _nextFile = Math.Max(MaxId("files"), Sequence("seq_files"));
            _nextSymbol = Math.Max(MaxId("symbols"), Sequence("seq_symbols"));
            _nextTerm = Math.Max(MaxId("terms"), Sequence("seq_terms"));
            if (!preloadTerms) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, text FROM terms";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _terms[r.GetString(1)] = r.GetInt64(0);
            _preloaded = true;
        }

        private long MaxId(string table)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = table switch
            {
                "files" => "SELECT COALESCE(MAX(id), 0) FROM files",
                "symbols" => "SELECT COALESCE(MAX(id), 0) FROM symbols",
                _ => "SELECT COALESCE(MAX(id), 0) FROM terms",
            };
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private long Sequence(string key) =>
            long.TryParse(Meta(_conn, null, key), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

        /// <summary>Сохранить счётчики id в той же транзакции, что и записанные/удалённые строки.</summary>
        public void SaveSequences(SqliteTransaction tx)
        {
            SetMeta(_conn, tx, "seq_files", _nextFile.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetMeta(_conn, tx, "seq_symbols", _nextSymbol.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetMeta(_conn, tx, "seq_terms", _nextTerm.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private SqliteCommand Cmd(SqliteTransaction tx, string sql, params string[] names)
        {
            var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, null));
            return cmd;
        }

        public void DeleteFile(SqliteTransaction tx, long id)
        {
            DeleteChildren(tx, id);
            using var cmd = Cmd(tx, "DELETE FROM files WHERE id = $id", "$id");
            cmd.Parameters[0].Value = id;
            cmd.ExecuteNonQuery();
        }

        private void DeleteChildren(SqliteTransaction tx, long id)
        {
            using var cmd = Cmd(tx, "DELETE FROM symbols WHERE file_id = $id; DELETE FROM refs WHERE file_id = $id; DELETE FROM calls WHERE file_id = $id;", "$id");
            cmd.Parameters[0].Value = id;
            cmd.ExecuteNonQuery();
        }

        public void Touch(SqliteTransaction tx, FileRow row)
        {
            using var cmd = Cmd(tx, "UPDATE files SET size = $s, mtime = $m, limit_bytes = $l WHERE id = $id", "$s", "$m", "$l", "$id");
            cmd.Parameters[0].Value = row.Size;
            cmd.Parameters[1].Value = row.Mtime;
            cmd.Parameters[2].Value = row.LimitBytes;
            cmd.Parameters[3].Value = row.Id;
            cmd.ExecuteNonQuery();
        }

        public FileRow WriteFile(SqliteTransaction tx, FileRow? old, string path, long size, long mtime, int maxBytes, FileData d)
        {
            long id;
            if (old is not null)
            {
                id = old.Id;
                DeleteChildren(tx, id);
                using var del = Cmd(tx, "DELETE FROM files WHERE id = $id", "$id");
                del.Parameters[0].Value = id;
                del.ExecuteNonQuery();
            }
            else
            {
                id = ++_nextFile;
            }
            var row = new FileRow
            {
                Id = id,
                Path = path,
                Size = size,
                Mtime = mtime,
                LimitBytes = maxBytes,
                Sha = d.Sha,
                Redacted = d.Redacted,
                Ok = d.Ok,
                Lang = d.Lang,
                Lines = d.Lines,
                Chars = d.Chars,
                Tokens = d.Tokens,
                Truncated = d.Truncated,
                Generated = d.Generated,
                LongTokens = d.LongTokens,
                Entry = d.Entry,
            };
            using (var ins = Cmd(tx,
                       "INSERT INTO files(id, path, size, mtime, limit_bytes, sha, redacted, ok, lang, lines, chars, tokens, truncated, generated, longtok, entry) " +
                       "VALUES($id, $p, $s, $m, $l, $sha, $red, $ok, $lang, $lines, $chars, $tok, $tr, $gen, $long, $entry)",
                       "$id", "$p", "$s", "$m", "$l", "$sha", "$red", "$ok", "$lang", "$lines", "$chars", "$tok", "$tr", "$gen", "$long", "$entry"))
            {
                object?[] values = [id, path, size, mtime, maxBytes, d.Sha, d.Redacted, d.Ok ? 1 : 0, (int)d.Lang, d.Lines, d.Chars, d.Tokens,
                    d.Truncated ? 1 : 0, d.Generated ? 1 : 0, d.LongTokens ? 1 : 0, (object?)d.Entry ?? DBNull.Value];
                for (var i = 0; i < values.Length; i++) ins.Parameters[i].Value = values[i];
                ins.ExecuteNonQuery();
            }
            if (!d.Ok) return row;

            var symbolIds = new long[d.Symbols.Count];
            using (var sym = Cmd(tx, "INSERT INTO symbols(id, file_id, name, kind, container, line, end_line, signature) VALUES($id, $f, $n, $k, $c, $l, $e, $s)",
                       "$id", "$f", "$n", "$k", "$c", "$l", "$e", "$s"))
            {
                for (var i = 0; i < d.Symbols.Count; i++)
                {
                    var s = d.Symbols[i];
                    symbolIds[i] = ++_nextSymbol;
                    sym.Parameters[0].Value = symbolIds[i];
                    sym.Parameters[1].Value = id;
                    sym.Parameters[2].Value = s.Name;
                    sym.Parameters[3].Value = s.Kind;
                    sym.Parameters[4].Value = (object?)s.Container ?? DBNull.Value;
                    sym.Parameters[5].Value = s.Line;
                    sym.Parameters[6].Value = s.EndLine;
                    sym.Parameters[7].Value = s.Signature;
                    sym.ExecuteNonQuery();
                }
            }
            using (var find = Cmd(tx, "SELECT id FROM terms WHERE text = $t", "$t"))
            using (var term = Cmd(tx, "INSERT INTO terms(id, text) VALUES($id, $t)", "$id", "$t"))
            using (var part = Cmd(tx, "INSERT OR IGNORE INTO term_parts(part, term_id) VALUES($p, $id)", "$p", "$id"))
            using (var reference = Cmd(tx, "INSERT INTO refs(term_id, file_id, tf, lines) VALUES($t, $f, $tf, $l)", "$t", "$f", "$tf", "$l"))
            {
                foreach (var (text, acc) in d.Terms)
                {
                    if (!_terms.TryGetValue(text, out var termId))
                    {
                        object? found = null;
                        if (!_preloaded)
                        {
                            find.Parameters[0].Value = text;
                            found = find.ExecuteScalar();
                        }
                        if (found is long existing)
                        {
                            termId = existing;
                        }
                        else
                        {
                            termId = ++_nextTerm;
                            term.Parameters[0].Value = termId;
                            term.Parameters[1].Value = text;
                            term.ExecuteNonQuery();
                            foreach (var p in IndexTokens.Parts(text))
                            {
                                part.Parameters[0].Value = p;
                                part.Parameters[1].Value = termId;
                                part.ExecuteNonQuery();
                            }
                        }
                        _terms[text] = termId;
                    }
                    reference.Parameters[0].Value = termId;
                    reference.Parameters[1].Value = id;
                    reference.Parameters[2].Value = acc.Tf;
                    reference.Parameters[3].Value = acc.Serialize();
                    reference.ExecuteNonQuery();
                }
            }
            using (var call = Cmd(tx, "INSERT INTO calls(caller_id, file_id, callee, chain, line) VALUES($c, $f, $n, $ch, $l)", "$c", "$f", "$n", "$ch", "$l"))
            {
                foreach (var c in d.Calls)
                {
                    call.Parameters[0].Value = symbolIds[c.CallerIndex];
                    call.Parameters[1].Value = id;
                    call.Parameters[2].Value = c.Callee;
                    call.Parameters[3].Value = (object?)c.Chain ?? DBNull.Value;
                    call.Parameters[4].Value = c.Line;
                    call.ExecuteNonQuery();
                }
            }
            return row;
        }
    }
}
