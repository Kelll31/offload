using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Offload.Core.Logging;
using Offload.Mcp.Index;

namespace Offload.Mcp.Infrastructure;

/// <summary>Вектор фрагмента документа: номер фрагмента, строки (для кода; у записи памяти — 0), метка и нормализованный вектор.</summary>
internal sealed record ChunkVector(int Ord, int StartLine, int EndLine, string Label, float[] Vector);

/// <summary>Лучший фрагмент документа по близости к запросу (косинус).</summary>
internal sealed record ChunkHit(string Key, int Ord, int StartLine, int EndLine, string Label, float Score);

/// <summary>
/// Векторы постоянного индекса (ROADMAP §6.3): <c>DataDir/index/vectors/&lt;хэш корня&gt;.db</c> (SQLite, WAL). Ключ документа —
/// sha замаскированного текста файла (тот же, что в таблице files индекса) или «mem:&lt;sha&gt;» для записи памяти, поэтому
/// переэмбеддинг нужен только изменившимся файлам, а переименование или откат по git ничего не стоит. Хранятся только векторы
/// (float16, нормализованные), строки и метки фрагментов — сам текст в базу не пишется. Модель — строка models (идентификатор
/// назначенной модели роли embed и размерность): смена модели или размерности не смешивает несравнимые векторы. База не
/// зависит от версии Offload (в отличие от индекса символов) — обновление Offload не требует заново считать все векторы.
/// Документы, к которым не обращались 30 дней, удаляются (раз за процесс).
/// </summary>
internal sealed class EmbeddingsStore : IDisposable
{
    public const int SchemaVersion = 1;

    /// <summary>Сколько дней хранить векторы документа, который больше не встречается (файл изменён или удалён).</summary>
    private const int KeepDays = 30;

    private static readonly HashSet<string> Pruned = new(StringComparer.OrdinalIgnoreCase);

    private readonly SqliteConnection _conn;
    private readonly long _modelId;

    private EmbeddingsStore(SqliteConnection conn, long modelId, int dimension, string dbPath)
    {
        _conn = conn;
        _modelId = modelId;
        Dimension = dimension;
        DbPath = dbPath;
    }

    /// <summary>Размерность векторов модели (0 — ещё не известна: векторов нет).</summary>
    public int Dimension { get; private set; }

    public string DbPath { get; }

    public static string DirPath => Path.Combine(IndexStore.IndexDir, "vectors");

    public static string DbPathFor(string root) => Path.Combine(DirPath, IndexStore.RootHash(root) + ".db");

    /// <summary>Сегодняшний день (дни от эпохи Unix) — отметка использования документа.</summary>
    internal static long Today => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400;

    /// <summary>Открыть (создать) хранилище векторов корня для модели <paramref name="model"/>. Ошибки SQLite и ввода-вывода — наружу.</summary>
    public static EmbeddingsStore Open(string root, string model)
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
                var (id, dim) = ModelRow(conn, model);
                var store = new EmbeddingsStore(conn, id, dim, dbPath);
                store.PruneOnce();
                return store;
            }
            catch (SqliteException ex) when (attempt == 0 && ex.SqliteErrorCode is 11 or 26)
            {
                // SQLITE_CORRUPT / SQLITE_NOTADB: база испорчена — удаляем, векторы посчитаются заново.
                conn.Dispose();
                Log.Warn("vectors", $"база векторов {dbPath} повреждена ({ex.Message}); создаю заново");
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
                    DROP TABLE IF EXISTS models; DROP TABLE IF EXISTS docs; DROP TABLE IF EXISTS vectors;
                    CREATE TABLE models(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, dim INTEGER NOT NULL);
                    CREATE TABLE docs(model_id INTEGER NOT NULL, key TEXT NOT NULL, chunks INTEGER NOT NULL, used INTEGER NOT NULL,
                        PRIMARY KEY(model_id, key)) WITHOUT ROWID;
                    CREATE TABLE vectors(model_id INTEGER NOT NULL, key TEXT NOT NULL, ord INTEGER NOT NULL, start_line INTEGER NOT NULL,
                        end_line INTEGER NOT NULL, label TEXT NOT NULL, vec BLOB NOT NULL, PRIMARY KEY(model_id, key, ord)) WITHOUT ROWID;
                    INSERT OR REPLACE INTO meta(key, value) VALUES('schema', '1');
                    """);
            }
        }
        tx.Commit();
    }

    private static (long Id, int Dim) ModelRow(SqliteConnection conn, string model)
    {
        using var tx = conn.BeginTransaction();
        using var find = conn.CreateCommand();
        find.Transaction = tx;
        find.CommandText = "SELECT id, dim FROM models WHERE name = $n";
        find.Parameters.AddWithValue("$n", model);
        using (var r = find.ExecuteReader())
        {
            if (r.Read()) return (r.GetInt64(0), r.GetInt32(1));
        }
        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO models(name, dim) VALUES($n, 0); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$n", model);
        var id = Convert.ToInt64(ins.ExecuteScalar(), CultureInfo.InvariantCulture);
        tx.Commit();
        return (id, 0);
    }

    /// <summary>Раз за процесс на базу: удалить документы, к которым не обращались дольше <see cref="KeepDays"/> дней.</summary>
    private void PruneOnce()
    {
        lock (Pruned)
        {
            if (!Pruned.Add(DbPath)) return;
        }
        try
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "DELETE FROM vectors WHERE (model_id, key) IN (SELECT model_id, key FROM docs WHERE used < $d); DELETE FROM docs WHERE used < $d;";
            cmd.Parameters.AddWithValue("$d", Today - KeepDays);
            var n = cmd.ExecuteNonQuery();
            tx.Commit();
            if (n > 0) Log.Info("vectors", $"удалены давно не используемые векторы ({n} строк)");
        }
        catch (SqliteException ex)
        {
            Log.Debug("vectors", "очистка старых векторов: " + ex.Message);
        }
    }

    /// <summary>Только для тестов: разрешить повторную очистку в этом процессе.</summary>
    internal static void ResetPrune()
    {
        lock (Pruned) Pruned.Clear();
    }

    /// <summary>
    /// Какие из документов уже посчитаны этой моделью; заодно отмечает их использование (не чаще раза в сутки на документ),
    /// чтобы очистка не удалила векторы живых файлов.
    /// </summary>
    public HashSet<string> Embedded(IEnumerable<string> keys)
    {
        var wanted = keys as IReadOnlySet<string> ?? keys.ToHashSet(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var stale = new List<string>();
        var today = Today;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT key, used FROM docs WHERE model_id = $m";
            cmd.Parameters.AddWithValue("$m", _modelId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                if (!wanted.Contains(key)) continue;
                done.Add(key);
                if (r.GetInt64(1) < today) stale.Add(key);
            }
        }
        if (stale.Count > 0)
        {
            using var tx = _conn.BeginTransaction();
            using var upd = _conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE docs SET used = $d WHERE model_id = $m AND key = $k";
            upd.Parameters.AddWithValue("$d", today);
            upd.Parameters.AddWithValue("$m", _modelId);
            var k = upd.Parameters.Add("$k", SqliteType.Text);
            foreach (var key in stale)
            {
                k.Value = key;
                upd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return done;
    }

    /// <summary>
    /// Сохранить векторы документов одной транзакцией (документ без фрагментов тоже отмечается посчитанным). Размерность
    /// отличается от сохранённой — модель сменила формат: старые векторы модели удаляются.
    /// </summary>
    public void Save(IReadOnlyList<(string Key, IReadOnlyList<ChunkVector> Chunks)> docs)
    {
        if (docs.Count == 0) return;
        var dim = docs.SelectMany(d => d.Chunks).Select(c => c.Vector.Length).FirstOrDefault();
        using var tx = _conn.BeginTransaction();
        if (dim > 0 && dim != Dimension)
        {
            using var reset = _conn.CreateCommand();
            reset.Transaction = tx;
            reset.CommandText = "DELETE FROM vectors WHERE model_id = $m; DELETE FROM docs WHERE model_id = $m; UPDATE models SET dim = $d WHERE id = $m;";
            reset.Parameters.AddWithValue("$m", _modelId);
            reset.Parameters.AddWithValue("$d", dim);
            reset.ExecuteNonQuery();
            if (Dimension > 0) Log.Info("vectors", $"размерность векторов модели изменилась ({Dimension} → {dim}); старые векторы удалены");
            Dimension = dim;
        }
        using var del = _conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM vectors WHERE model_id = $m AND key = $k";
        del.Parameters.AddWithValue("$m", _modelId);
        var delKey = del.Parameters.Add("$k", SqliteType.Text);
        using var doc = _conn.CreateCommand();
        doc.Transaction = tx;
        doc.CommandText = "INSERT OR REPLACE INTO docs(model_id, key, chunks, used) VALUES($m, $k, $c, $d)";
        doc.Parameters.AddWithValue("$m", _modelId);
        var docKey = doc.Parameters.Add("$k", SqliteType.Text);
        var docChunks = doc.Parameters.Add("$c", SqliteType.Integer);
        doc.Parameters.AddWithValue("$d", Today);
        using var vec = _conn.CreateCommand();
        vec.Transaction = tx;
        vec.CommandText = "INSERT INTO vectors(model_id, key, ord, start_line, end_line, label, vec) VALUES($m, $k, $o, $s, $e, $l, $v)";
        vec.Parameters.AddWithValue("$m", _modelId);
        var vKey = vec.Parameters.Add("$k", SqliteType.Text);
        var vOrd = vec.Parameters.Add("$o", SqliteType.Integer);
        var vStart = vec.Parameters.Add("$s", SqliteType.Integer);
        var vEnd = vec.Parameters.Add("$e", SqliteType.Integer);
        var vLabel = vec.Parameters.Add("$l", SqliteType.Text);
        var vBlob = vec.Parameters.Add("$v", SqliteType.Blob);
        foreach (var (key, chunks) in docs)
        {
            var valid = chunks.Where(c => c.Vector.Length == Dimension).ToList();
            delKey.Value = key;
            del.ExecuteNonQuery();
            foreach (var c in valid)
            {
                vKey.Value = key;
                vOrd.Value = c.Ord;
                vStart.Value = c.StartLine;
                vEnd.Value = c.EndLine;
                vLabel.Value = c.Label;
                vBlob.Value = VectorMath.ToHalfBytes(c.Vector);
                vec.ExecuteNonQuery();
            }
            docKey.Value = key;
            docChunks.Value = valid.Count;
            doc.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Лучший фрагмент каждого документа из <paramref name="keys"/> по косинусу с нормализованным запросом. Векторы читаются
    /// потоком, в памяти — только по одному лучшему фрагменту на документ.
    /// </summary>
    public Dictionary<string, ChunkHit> BestPerKey(float[] query, IReadOnlySet<string> keys)
    {
        var best = new Dictionary<string, ChunkHit>(StringComparer.Ordinal);
        if (Dimension == 0 || query.Length != Dimension || keys.Count == 0) return best;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key, ord, start_line, end_line, label, vec FROM vectors WHERE model_id = $m";
        cmd.Parameters.AddWithValue("$m", _modelId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = r.GetString(0);
            if (!keys.Contains(key)) continue;
            var blob = (byte[])r.GetValue(5);
            if (blob.Length != Dimension * 2) continue;
            var score = VectorMath.DotHalf(query, blob);
            if (best.TryGetValue(key, out var cur) && cur.Score >= score) continue;
            best[key] = new ChunkHit(key, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), score);
        }
        return best;
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

/// <summary>Арифметика векторов: нормализация, float16 для хранения, скалярное произведение, слияние рангов.</summary>
internal static class VectorMath
{
    /// <summary>Нормализованная копия (длина 1); нулевой или нечисловой вектор — нулевой.</summary>
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        var norm = Math.Sqrt(sum);
        var result = new float[v.Length];
        if (norm <= 0 || !double.IsFinite(norm)) return result;
        for (var i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }

    public static float Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < n; i++) sum += (double)a[i] * b[i];
        return (float)sum;
    }

    public static byte[] ToHalfBytes(float[] v)
    {
        var halves = new Half[v.Length];
        for (var i = 0; i < v.Length; i++) halves[i] = (Half)v[i];
        return MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
    }

    public static float[] FromHalfBytes(byte[] blob)
    {
        var halves = MemoryMarshal.Cast<byte, Half>(blob.AsSpan());
        var result = new float[halves.Length];
        for (var i = 0; i < halves.Length; i++) result[i] = (float)halves[i];
        return result;
    }

    /// <summary>Скалярное произведение запроса и вектора в float16 (без промежуточного массива).</summary>
    public static float DotHalf(float[] query, byte[] blob)
    {
        var halves = MemoryMarshal.Cast<byte, Half>(blob.AsSpan());
        var n = Math.Min(query.Length, halves.Length);
        float sum = 0;
        for (var i = 0; i < n; i++) sum += query[i] * (float)halves[i];
        return sum;
    }

    /// <summary>Константа k метода reciprocal rank fusion (стандартное значение из статьи Cormack и др.).</summary>
    public const int RrfK = 60;

    /// <summary>
    /// Reciprocal rank fusion: оценка элемента — сумма 1/(k + ранг) по спискам, где он есть (ранг с 1). Порядок при равенстве —
    /// по первому появлению в списках (детерминированно).
    /// </summary>
    public static List<(T Item, double Score)> Fuse<T>(IEqualityComparer<T>? comparer, params IReadOnlyList<T>[] lists) where T : notnull
    {
        var scores = new Dictionary<T, double>(comparer);
        var first = new Dictionary<T, int>(comparer);
        var seen = 0;
        foreach (var list in lists)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                scores[item] = scores.GetValueOrDefault(item) + 1.0 / (RrfK + i + 1);
                if (!first.ContainsKey(item)) first[item] = seen++;
            }
        }
        return [.. scores.OrderByDescending(kv => kv.Value).ThenBy(kv => first[kv.Key]).Select(kv => (kv.Key, kv.Value))];
    }
}
