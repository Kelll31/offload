using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Offload.Core.Config;
using Offload.Core.Logging;
using Offload.Llama;
using Offload.Mcp.Index;
using Offload.Mcp.Tools;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Клиент модели роли embed для одного вызова инструмента (ROADMAP §6.3): запуск сервера роли через трей
/// (<see cref="ServerEnsurer.EnsureAsync"/> с ролью, без перехода на основную модель — она векторы не считает), запросы
/// пачками под своей очередью <see cref="GpuQueue"/>, нормализация векторов, инструкции запроса/документа по семейству модели.
/// </summary>
internal sealed class Embedder
{
    /// <summary>Сколько текстов в одном запросе /v1/embeddings.</summary>
    internal const int BatchSize = 32;

    private readonly ToolContext _ctx;
    private readonly LlamaClient _client;

    private Embedder(ToolContext ctx, LlamaClient client, string modelId)
    {
        _ctx = ctx;
        _client = client;
        ModelId = modelId;
    }

    /// <summary>Идентификатор назначенной модели эмбеддингов (ключ векторов в хранилище).</summary>
    public string ModelId { get; }

    /// <summary>Назначена ли модель роли embed (установлена и подходит роли). Нет — векторный поиск полностью пропускается.</summary>
    public static bool Configured(AppConfig cfg) => cfg.RoleModel(ModelRole.Embed) is not null;

    /// <summary>Сервер эмбеддингов готов — клиент; не назначен или не запустился — null (причина — в прогрессе и журнале).</summary>
    public static async Task<Embedder?> ConnectAsync(ToolContext ctx)
    {
        if (ctx.Cfg.RoleModel(ModelRole.Embed) is not { } model) return null;
        try
        {
            var client = await ServerEnsurer.EnsureAsync(ctx.Cfg, ctx.State, ctx.Progress, ctx.Ct, ModelRole.Embed).ConfigureAwait(false);
            return new Embedder(ctx, client, model.Id);
        }
        catch (ToolException ex)
        {
            Log.Warn("vectors", "модель эмбеддингов недоступна: " + ex.Message);
            ctx.Progress.Report("embedding model unavailable, using keyword ranking: " + ex.Message);
            return null;
        }
    }

    /// <summary>Нормализованные векторы текстов в их порядке (пачками по <see cref="BatchSize"/>). Ошибки сервера — LlamaApiException.</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts)
    {
        var result = new float[texts.Count][];
        for (var start = 0; start < texts.Count; start += BatchSize)
        {
            var batch = texts.Skip(start).Take(BatchSize).ToList();
            EmbeddingResult r;
            await using (var slot = await AcquireAsync(_ctx, ModelRole.Embed).ConfigureAwait(false))
                r = await _client.EmbedAsync(batch, _ctx.Ct).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++) result[start + i] = VectorMath.Normalize(r.Vectors[i]);
        }
        return result;
    }

    /// <summary>Вектор запроса (с инструкцией поиска, если модель её ожидает).</summary>
    public async Task<float[]> EmbedQueryAsync(string query) => (await EmbedAsync([QueryText(ModelId, query)]).ConfigureAwait(false))[0];

    /// <summary>Текст документа для эмбеддинга (с префиксом, если модель его ожидает).</summary>
    public string DocumentText(string text) => DocumentText(ModelId, text);

    /// <summary>
    /// Запрос в формате семейства модели: Qwen3-Embedding — «Instruct: …\nQuery: …» (документы без инструкции), nomic-embed —
    /// «search_query: ». Остальные — как есть.
    /// </summary>
    internal static string QueryText(string modelId, string query)
    {
        var id = modelId.ToLowerInvariant();
        if (id.Contains("qwen3", StringComparison.Ordinal) && id.Contains("embed", StringComparison.Ordinal))
            return "Instruct: Given a programming task or question, retrieve the source code and documentation relevant to it\nQuery: " + query;
        if (id.Contains("nomic", StringComparison.Ordinal)) return "search_query: " + query;
        return query;
    }

    internal static string DocumentText(string modelId, string text) =>
        modelId.Contains("nomic", StringComparison.OrdinalIgnoreCase) ? "search_document: " + text : text;

    /// <summary>Слот очереди вспомогательного сервера роли (свой пул из одного слота, не мешает основной модели).</summary>
    internal static async Task<GpuSlot> AcquireAsync(ToolContext ctx, ModelRole role)
    {
        var request = GpuQueue.RequestFor(ctx.Tool, ctx.Server?.ClientInfo?.Name, GpuQueue.PriorityFor(ctx.Tool), 1, role);
        var slot = await GpuQueue.AcquireAsync(request, ctx.Progress, ctx.Ct).ConfigureAwait(false);
        ctx.Stats.AddQueueWait(slot.Waited);
        return slot;
    }

    /// <summary>Ключ документа по тексту: sha256 строк, соединённых «\n» (как sha файла в индексе).</summary>
    internal static string Sha(IEnumerable<string> lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
}

/// <summary>Реранкер (роль rerank): оценки документов по запросу; недоступен — null, вызывающий оставляет свой порядок.</summary>
internal static class Reranker
{
    public static bool Configured(AppConfig cfg) => cfg.RoleModel(ModelRole.Rerank) is not null;

    /// <summary>Оценки документов по убыванию релевантности (все документы) или null, если реранкер не назначен или недоступен.</summary>
    public static async Task<IReadOnlyList<RerankScore>?> RerankAsync(ToolContext ctx, string query, IReadOnlyList<string> documents)
    {
        if (!Configured(ctx.Cfg) || documents.Count == 0) return null;
        try
        {
            var client = await ServerEnsurer.EnsureAsync(ctx.Cfg, ctx.State, ctx.Progress, ctx.Ct, ModelRole.Rerank).ConfigureAwait(false);
            await using var slot = await Embedder.AcquireAsync(ctx, ModelRole.Rerank).ConfigureAwait(false);
            ctx.Progress.Report($"Reranking {documents.Count} candidates…");
            var r = await client.RerankAsync(query, documents, null, ctx.Ct).ConfigureAwait(false);
            return r.Scores;
        }
        catch (Exception ex) when (ex is ToolException or LlamaApiException)
        {
            Log.Warn("vectors", "реранкер недоступен: " + ex.Message);
            ctx.Progress.Report("reranker unavailable: " + ex.Message);
            return null;
        }
    }
}

/// <summary>Фрагмент файла для эмбеддинга: строки (с 1, включительно) и метка (символы или «lines a-b»).</summary>
internal sealed record TextChunk(int StartLine, int EndLine, string Label);

/// <summary>
/// Нарезка файла на фрагменты для эмбеддинга: по объявлениям (методы, функции, свойства — внешние, без вложенных), промежутки
/// между ними (поля, заголовки типов, импорты) — отдельными кусками; соседние короткие куски склеиваются до <see cref="Window"/>
/// строк, длинные режутся окнами по <see cref="Window"/> строк. Без символов — просто окна по 60 строк.
/// </summary>
internal static class EmbeddingsChunker
{
    public const int Window = 60;
    public const int MaxChunksPerFile = 48;
    public const int MaxChunkChars = 4000;

    public static List<TextChunk> Chunk(IReadOnlyList<CodeSymbol> symbols, string[] lines)
    {
        var n = lines.Length;
        var chunks = new List<TextChunk>();
        if (n == 0) return chunks;

        var units = new List<CodeSymbol>();
        foreach (var s in symbols.Where(s => SymbolsTool.IsCallable(s) && s.Line >= 1 && s.Line <= n).OrderBy(s => s.Line).ThenByDescending(s => s.EndLine))
        {
            if (units.Count > 0 && s.Line <= units[^1].EndLine) continue; // вложенный или пересекающийся
            units.Add(s);
        }
        var segments = new List<(int From, int To, string? Label)>();
        var next = 1;
        foreach (var u in units)
        {
            var end = Math.Min(n, Math.Max(u.Line, u.EndLine));
            if (u.Line > next) segments.Add((next, u.Line - 1, null));
            segments.Add((u.Line, end, u.QualifiedName));
            next = end + 1;
        }
        if (next <= n) segments.Add((next, n, null));

        int? start = null;
        var stop = 0;
        var labels = new List<string>();
        void Flush()
        {
            if (start is int s0 && HasText(lines, s0, stop)) chunks.Add(new TextChunk(s0, stop, LabelOf(labels, s0, stop)));
            start = null;
            labels.Clear();
        }
        foreach (var (from, to, label) in segments)
        {
            if (to - from + 1 > Window)
            {
                Flush();
                for (var a = from; a <= to; a += Window)
                {
                    var b = Math.Min(to, a + Window - 1);
                    if (!HasText(lines, a, b)) continue;
                    chunks.Add(new TextChunk(a, b, label is null ? $"lines {a}-{b}" : a == from ? label : label + " (cont.)"));
                }
                continue;
            }
            if (start is int s && to - s + 1 > Window) Flush();
            start ??= from;
            stop = to;
            if (label is not null) labels.Add(label);
        }
        Flush();
        return chunks.Count > MaxChunksPerFile ? chunks.Take(MaxChunksPerFile).ToList() : chunks;
    }

    /// <summary>Текст фрагмента для эмбеддинга: метка и строки, не длиннее <see cref="MaxChunkChars"/>.</summary>
    public static string Text(string[] lines, TextChunk c)
    {
        var sb = new StringBuilder(c.Label).Append('\n');
        for (var i = c.StartLine; i <= c.EndLine && i <= lines.Length && sb.Length < MaxChunkChars; i++) sb.Append(lines[i - 1]).Append('\n');
        return sb.Length > MaxChunkChars ? sb.ToString(0, MaxChunkChars) : sb.ToString();
    }

    private static bool HasText(string[] lines, int from, int to)
    {
        var chars = 0;
        for (var i = from; i <= to && i <= lines.Length; i++)
        {
            chars += lines[i - 1].AsSpan().Trim().Length;
            if (chars >= 16) return true;
        }
        return false;
    }

    private static string LabelOf(List<string> labels, int from, int to) =>
        labels.Count == 0 ? $"lines {from}-{to}" : string.Join(", ", labels.Take(3)) + (labels.Count > 3 ? ", …" : "");
}

/// <summary>Итог векторного поиска по файлам: файлы по близости к запросу с лучшим фрагментом и покрытие векторами.</summary>
internal sealed record VectorSearch(List<(IndexedFile File, ChunkHit Hit)> Ranked, int Eligible, int Covered, int EmbeddedNow)
{
    /// <summary>Строка для подвала: «vectors: 62% of files (620/1000), +400 chunks this call».</summary>
    public string Note()
    {
        var pct = Eligible == 0 ? 100 : (int)Math.Floor(100.0 * Covered / Eligible);
        var sb = new StringBuilder($"vectors: {pct}% of files ({Covered}/{Eligible})");
        if (EmbeddedNow > 0) sb.Append($", +{EmbeddedNow} chunks this call");
        if (Covered < Eligible) sb.Append("; the rest are embedded on later calls");
        return sb.ToString();
    }
}

/// <summary>
/// Векторы файлов постоянного индекса: ленивый досчёт без фоновой работы — во время вызова, в пределах бюджета времени и числа
/// фрагментов (сначала кандидаты BM25, затем остальные файлы по порядку), и поиск ближайших к запросу файлов по лучшему фрагменту.
/// Эмбеддятся только замаскированные тексты (<see cref="IndexView.RedactedText"/>).
/// </summary>
internal static class EmbeddingsIndex
{
    /// <summary>Бюджет досчёта векторов за один вызов (время).</summary>
    internal static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Бюджет досчёта векторов за один вызов (фрагментов).</summary>
    internal static int MaxChunksPerCall { get; set; } = 400;

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".rst", ".txt", ".adoc" };

    /// <summary>Файл стоит эмбеддить: код или документация, не сгенерированный и не минифицированный.</summary>
    public static bool IsEligible(IndexedFile f) =>
        f.Sha.Length > 0 && f.LineCount > 0 && !f.Generated && !f.LongTokens && (f.IsCode || DocExtensions.Contains(Path.GetExtension(f.FullPath)));

    public static async Task<VectorSearch> SearchAsync(ToolContext ctx, Embedder embedder, EmbeddingsStore store, IndexView index, string query,
        IReadOnlyList<IndexedFile> priority, bool wantsTests, int take)
    {
        var eligible = index.Files.Where(IsEligible).ToList();
        var keys = eligible.Select(f => f.Sha).ToHashSet(StringComparer.Ordinal);
        var done = store.Embedded(keys);
        var missing = priority.Where(IsEligible).Concat(eligible).Where(f => !done.Contains(f.Sha)).DistinctBy(f => f.Sha).ToList();
        var embeddedNow = missing.Count == 0 ? 0 : await EmbedFilesAsync(ctx, embedder, store, index, missing, done, eligible.Count).ConfigureAwait(false);

        var q = await embedder.EmbedQueryAsync(query).ConfigureAwait(false);
        var present = new HashSet<string>(keys.Where(done.Contains), StringComparer.Ordinal);
        var hits = store.BestPerKey(q, present);
        var ranked = eligible
            .Where(f => hits.ContainsKey(f.Sha))
            .Select(f => (File: f, Hit: hits[f.Sha], Adjusted: hits[f.Sha].Score - (f.IsTest && !wantsTests ? 0.1f : 0f)))
            .OrderByDescending(x => x.Adjusted).ThenBy(x => x.File.Order)
            .Take(take)
            .Select(x => (x.File, x.Hit))
            .ToList();
        return new VectorSearch(ranked, eligible.Count, eligible.Count(f => done.Contains(f.Sha)), embeddedNow);
    }

    /// <summary>Досчитать векторы недостающих файлов в пределах бюджета; возвращает число посчитанных фрагментов.</summary>
    private static async Task<int> EmbedFilesAsync(ToolContext ctx, Embedder embedder, EmbeddingsStore store, IndexView index,
        List<IndexedFile> missing, HashSet<string> done, int eligible)
    {
        var sw = Stopwatch.StartNew();
        var total = 0;
        var pending = new List<(string Key, List<TextChunk> Chunks)>();
        var texts = new List<string>();

        async Task FlushAsync()
        {
            if (pending.Count == 0) return;
            float[][] vectors = texts.Count == 0 ? [] : await embedder.EmbedAsync(texts).ConfigureAwait(false);
            var docs = new List<(string, IReadOnlyList<ChunkVector>)>();
            var k = 0;
            foreach (var (key, chunks) in pending)
            {
                var list = new List<ChunkVector>();
                for (var i = 0; i < chunks.Count; i++, k++)
                    list.Add(new ChunkVector(i, chunks[i].StartLine, chunks[i].EndLine, chunks[i].Label, vectors[k]));
                docs.Add((key, list));
            }
            store.Save(docs);
            foreach (var (key, _) in pending) done.Add(key);
            total += texts.Count;
            pending.Clear();
            texts.Clear();
            ctx.Progress.Report($"Embedding files for semantic search… {done.Count}/{eligible} files");
        }

        foreach (var f in missing)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            if (sw.Elapsed >= Budget || total + texts.Count >= MaxChunksPerCall) break;
            var text = index.RedactedText(f);
            if (text is null) continue;
            var key = Embedder.Sha(text.Lines);
            if (done.Contains(key) || pending.Any(p => p.Key == key)) continue;
            var chunks = EmbeddingsChunker.Chunk(text.Symbols, text.Lines);
            if (texts.Count + total > 0 && total + texts.Count + chunks.Count > MaxChunksPerCall) break;
            pending.Add((key, chunks));
            texts.AddRange(chunks.Select(c => embedder.DocumentText(EmbeddingsChunker.Text(text.Lines, c))));
            if (texts.Count >= Embedder.BatchSize) await FlushAsync().ConfigureAwait(false);
        }
        await FlushAsync().ConfigureAwait(false);
        if (total > 0) Log.Info("vectors", $"посчитано фрагментов: {total}, файлов с векторами {done.Count}/{eligible}, {sw.ElapsedMilliseconds} мс");
        return total;
    }

    /// <summary>Открыть хранилище векторов корня; недоступно — null (векторный поиск пропускается).</summary>
    public static EmbeddingsStore? TryOpenStore(ToolContext ctx, string model)
    {
        try
        {
            return EmbeddingsStore.Open(ctx.Roots[0], model);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("vectors", "хранилище векторов недоступно: " + ex.Message);
            ctx.Progress.Report("vector store unavailable: " + ex.Message);
            return null;
        }
    }
}
