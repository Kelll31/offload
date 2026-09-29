using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Offload.Core;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Models;

/// <summary>
/// Модели, найденные пользователем на Hugging Face (<see cref="HubImport"/>): записи того же вида, что и в каталоге
/// (<see cref="CatalogModel"/>, <c>FromHub = true</c>), чтобы загрузка с докачкой и проверкой SHA-256, оценка памяти,
/// роли и сэмплинг работали одинаково. Хранятся в папке данных (catalog\huggingface.json) и переживают перезапуск;
/// повреждённые записи пропускаются (с записью в журнал), остальные читаются.
/// </summary>
public static partial class HubCatalog
{
    private const int FileVersion = 1;
    private const int MaxEntries = 500;
    private const long MaxFileBytes = 16 * 1024 * 1024;

    private static readonly object Lock = new();
    private static (string Path, DateTime Stamp, IReadOnlyList<CatalogModel> Models)? _cache;

    internal static string FilePath => Path.Combine(AppPaths.DataDir, "catalog", "huggingface.json");

    /// <summary>Сохранённые модели в порядке добавления.</summary>
    public static IReadOnlyList<CatalogModel> Models
    {
        get
        {
            lock (Lock) return LoadLocked();
        }
    }

    public static CatalogModel? Find(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Добавить или заменить запись (по идентификатору). Неполная запись — <see cref="ModelException"/>.</summary>
    public static void Save(CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Problem(model) is { } problem) throw new ModelException(L.F("Модель {0} не сохранена: {1}.", model.Id, problem));
        lock (Lock)
        {
            var list = LoadLocked().Where(m => !string.Equals(m.Id, model.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(model);
            if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
            WriteLocked(list);
        }
        Log.Info("models", $"Модель с Hugging Face сохранена в списке: {model.Id} ({model.Repo}@{model.Revision})");
    }

    /// <summary>Убрать запись из списка (файлы установленной модели не трогаются). false — записи не было.</summary>
    public static bool Remove(string id)
    {
        lock (Lock)
        {
            var list = LoadLocked().ToList();
            if (list.RemoveAll(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) == 0) return false;
            WriteLocked(list);
        }
        Log.Info("models", $"Модель с Hugging Face убрана из списка: {id}");
        return true;
    }

    /// <summary>Только для тестов: забыть кэш (следующее обращение снова прочитает файл).</summary>
    internal static void ResetCache()
    {
        lock (Lock) _cache = null;
    }

    private static IReadOnlyList<CatalogModel> LoadLocked()
    {
        var path = FilePath;
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_cache is { } c && c.Path == path && c.Stamp == stamp) return c.Models;
        IReadOnlyList<CatalogModel> models = [];
        if (stamp != DateTime.MinValue)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                    Log.Warn("models", $"{path}: файл слишком большой — список моделей с Hugging Face не прочитан.");
                else
                    models = Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warn("models", $"Список моделей с Hugging Face не прочитан ({path}): {ex.Message}");
            }
        }
        _cache = (path, stamp, models);
        return models;
    }

    private static void WriteLocked(IReadOnlyList<CatalogModel> list)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var doc = new JsonObject
        {
            ["version"] = FileVersion,
            ["models"] = JsonSerializer.SerializeToNode(list, Json.Options),
        };
        FileUtil.WriteAllTextAtomic(path, doc.ToJsonString(Json.Options));
        _cache = (path, File.GetLastWriteTimeUtc(path), list.ToArray());
    }

    /// <summary>Разбор файла: каждая запись отдельно, неверные пропускаются.</summary>
    internal static IReadOnlyList<CatalogModel> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<CatalogModel>();
        foreach (var e in models.EnumerateArray())
        {
            CatalogModel? m;
            try
            {
                m = e.Deserialize<CatalogModel>(Json.Options);
            }
            catch (JsonException ex)
            {
                Log.Warn("models", $"Запись модели с Hugging Face повреждена: {ex.Message}");
                continue;
            }
            if (m is null) continue;
            if (Problem(m) is { } problem)
            {
                Log.Warn("models", $"Запись модели с Hugging Face {m.Id} пропущена: {problem}");
                continue;
            }
            if (result.Any(x => string.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(m);
        }
        return result;
    }

    /// <summary>
    /// Что не так с записью (null — всё в порядке): закреплённая ревизия, пути внутри репозитория, точные размеры и SHA-256
    /// каждого файла (загрузка без проверки не допускается), параметры для оценки памяти.
    /// </summary>
    internal static string? Problem(CatalogModel m)
    {
        if (m is null) return L.T("пустая запись");
        if (string.IsNullOrWhiteSpace(m.Id) || !m.Id.StartsWith(HubImport.IdPrefix, StringComparison.Ordinal) || !IdRegex().IsMatch(m.Id))
            return L.T("неверный идентификатор");
        if (!m.FromHub) return L.T("запись не помечена как модель с Hugging Face");
        if (string.IsNullOrWhiteSpace(m.DisplayName)) return L.T("нет названия");
        if (string.IsNullOrWhiteSpace(m.Repo) || !RepoRegex().IsMatch(m.Repo)) return L.T("неверный репозиторий");
        if (m.Revision is null || !RevisionRegex().IsMatch(m.Revision)) return L.T("не закреплена ревизия");
        if (m.Quants is not { Count: > 0 } || m.Files is not { Count: > 0 }) return L.T("нет файлов");
        foreach (var f in m.Files)
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Quant)) return L.T("неполное описание файла");
            foreach (var (path, size, sha) in new[] { (f.Path, f.Size, f.Sha256) }.Concat((f.Parts ?? []).Select(p => (p?.Path ?? "", p?.Size ?? 0, p?.Sha256))))
            {
                if (!SafePath(path) || size <= 0) return L.F("недопустимый файл {0}", path);
                if (sha is null || !Sha256Regex().IsMatch(sha)) return L.F("нет SHA-256 файла {0}", path);
            }
        }
        if (m.Quants.Any(q => m.FindFile(q) is null)) return L.T("нет файла для квантизации");
        if (m.Kv is null || m.Kv.Layers <= 0 || m.Kv.KvHeads <= 0 || m.Kv.HeadDim <= 0) return L.T("неверные параметры KV-кэша");
        if (m.Sampling is null) return L.T("нет параметров сэмплирования");
        if (m.NativeContext <= 0 || m.DefaultContext <= 0 || m.DefaultContext > m.NativeContext) return L.T("неверный контекст");
        if (!Enum.IsDefined(m.Role)) return L.T("неверное назначение");
        return null;
    }

    private static bool SafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("..", StringComparison.Ordinal) && !path.StartsWith('/') && !path.Contains('\\') && path.All(c => !char.IsControl(c));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,150}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepoRegex();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex RevisionRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();
}
