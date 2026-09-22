using System.Globalization;
using System.Text;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Hardware;
using Offload.Core.Logging;
using Offload.Core.Net;
using Offload.Core.Util;

namespace Offload.Models;

/// <summary>
/// Установка/удаление/выбор моделей. Все изменения сохраняются в ConfigStore
/// (Models.Installed, Models.ActiveModelId).
/// </summary>
public static class ModelManager
{
    /// <summary>Запас свободного места на диске сверх размера модели.</summary>
    public const long DiskMarginBytes = 1L * 1024 * 1024 * 1024;

    private const int CustomRecommendedContext = 32768;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly HashSet<string> ActiveDownloads = new(StringComparer.OrdinalIgnoreCase);

    public static string ModelsDir(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var configured = cfg.Models?.ModelsDir;
        var dir = string.IsNullOrWhiteSpace(configured)
            ? AppPaths.DefaultModelsDir
            : Environment.ExpandEnvironmentVariables(configured.Trim());
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Скачать модель из каталога (с докачкой и проверкой SHA-256), зарегистрировать как установленную.
    /// Проверяет свободное место на диске. Если установлена первой — делает активной.
    /// </summary>
    public static async Task<InstalledModel> DownloadAsync(CatalogModel model, string? quant = null,
        IProgress<StepProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (ActiveDownloads)
        {
            if (!ActiveDownloads.Add(model.Id))
                throw new ModelException($"Модель {model.DisplayName} уже загружается.");
        }
        try
        {
            return await DownloadCoreAsync(model, quant, progress, ct);
        }
        finally
        {
            lock (ActiveDownloads) ActiveDownloads.Remove(model.Id);
        }
    }

    private static async Task<InstalledModel> DownloadCoreAsync(CatalogModel model, string? quant,
        IProgress<StepProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new StepProgress("Поиск файлов модели на Hugging Face…"));
        var resolved = await HfClient.ResolveAsync(model, quant, ct);
        var cfg = ConfigStore.Current;
        var dir = ModelsDir(cfg);

        var targets = resolved.Files.Select(f => (File: f, Dest: DestinationFor(cfg, dir, model, f.Path))).ToList();
        var total = resolved.TotalSize;

        // Место на диске: считаем только то, что ещё предстоит скачать (с учётом недокачанных .part).
        long remaining = 0;
        foreach (var (file, dest) in targets)
        {
            if (file.Size <= 0) continue;
            if (File.Exists(dest) && new FileInfo(dest).Length == file.Size) continue;
            var part = dest + ".part";
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            remaining += have <= file.Size ? file.Size - have : file.Size;
        }
        var free = HardwareDetector.GetFreeDiskBytes(dir);
        if (free <= 0)
            Log.Warn("models", $"Не удалось определить свободное место на диске для {dir}.");
        else if (free < remaining + DiskMarginBytes)
            throw new ModelException(
                $"Недостаточно места на диске {Path.GetPathRoot(dir)}: для модели нужно ≈{Size(remaining + DiskMarginBytes)} " +
                $"(с запасом 1 ГБ), свободно {Size(free)}. Освободите место или выберите другую папку для моделей в настройках.");

        Log.Info("models", $"Загрузка {model.Id} ({resolved.Quant}): {targets.Count} файл(ов), {Size(total)} → {dir}");

        long done = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var (file, dest) = targets[i];
            var stage = targets.Count > 1
                ? $"Загрузка модели {model.DisplayName} (часть {i + 1} из {targets.Count})"
                : $"Загрузка модели {model.DisplayName}";
            var before = done;
            var reporter = progress is null ? null : new SyncProgress<DownloadProgress>(p => progress.Report(Describe(p, stage, before, total)));
            await HttpDownloader.DownloadFileAsync(
                HfClient.DownloadUrl(model.Repo, file.Path, model.Revision),
                dest,
                file.Size > 0 ? file.Size : null,
                file.Sha256,
                reporter,
                ct);
            done += file.Size > 0 ? file.Size : new FileInfo(dest).Length;
        }

        var firstPath = targets[0].Dest;
        var info = TryReadGguf(firstPath);
        if (info is not null)
        {
            if (model.Architecture is not null && info.Architecture is not null &&
                !string.Equals(model.Architecture, info.Architecture, StringComparison.OrdinalIgnoreCase))
                Log.Warn("models", $"{model.Id}: архитектура в GGUF «{info.Architecture}», в каталоге «{model.Architecture}».");
            if (info.ContextLength > 0 && model.NativeContext > 0 && info.ContextLength < model.NativeContext)
                Log.Warn("models", $"{model.Id}: контекст в GGUF {info.ContextLength}, в каталоге {model.NativeContext}.");
        }

        var installed = new InstalledModel
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            Repo = model.Repo,
            FilePath = firstPath,
            SizeBytes = targets.Sum(t => File.Exists(t.Dest) ? new FileInfo(t.Dest).Length : t.File.Size),
            Quant = resolved.Quant,
            NativeContext = model.NativeContext,
            RecommendedContext = model.DefaultContext,
            IsMoe = model.IsMoe,
            Architecture = info?.Architecture ?? model.Architecture,
            GoodToolCalling = model.GoodToolCalling,
            Reasoning = model.Reasoning,
            HasMtp = model.HasMtp,
            Sampling = Clone(model.Sampling),
            IsCustom = false,
            InstalledAtUtc = DateTime.UtcNow,
        };
        Register(installed);
        progress?.Report(new StepProgress("Модель установлена", 1, $"{model.DisplayName} · {Size(installed.SizeBytes)}"));
        Log.Info("models", $"Модель {model.Id} установлена: {firstPath}");
        return installed;
    }

    /// <summary>Добавить существующий GGUF-файл пользователя (метаданные читаются из заголовка).</summary>
    public static InstalledModel AddCustom(string ggufPath, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(ggufPath)) throw new ArgumentException("Не указан файл модели.", nameof(ggufPath));
        var path = Path.GetFullPath(ggufPath.Trim().Trim('"'));
        if (!File.Exists(path)) throw new FileNotFoundException($"Файл модели не найден: {path}", path);

        // Для разбитой модели llama-server нужен первый шард.
        var shard = HfClient.ShardInfo(Path.GetFileName(path));
        if (shard is { Index: > 1 } s)
        {
            var first = Path.Combine(Path.GetDirectoryName(path)!, ShardName(Path.GetFileName(path), 1, s.Total));
            if (!File.Exists(first)) throw new ModelException($"Это часть {s.Index} из {s.Total} разбитой модели — укажите первый файл ({Path.GetFileName(first)}).");
            path = first;
        }

        GgufInfo info;
        try
        {
            info = GgufReader.Read(path);
        }
        catch (InvalidDataException ex)
        {
            throw new ModelException($"Не удалось прочитать модель {Path.GetFileName(path)}: {ex.Message}", ex);
        }

        var files = ShardFiles(path);
        var missing = files.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0)
            throw new ModelException($"Не хватает частей разбитой модели: {string.Join(", ", missing.Select(Path.GetFileName))}.");

        var template = info.ChatTemplate ?? "";
        var reasoning = string.Equals(info.Architecture, "gpt-oss", StringComparison.OrdinalIgnoreCase) ? ReasoningControl.ReasoningEffort
            : template.Contains("enable_thinking", StringComparison.Ordinal) ? ReasoningControl.EnableThinkingKwarg
            : ReasoningControl.None;
        var name = string.IsNullOrWhiteSpace(displayName)
            ? (string.IsNullOrWhiteSpace(info.Name) ? BaseName(path) : info.Name!.Trim())
            : displayName.Trim();

        var model = new InstalledModel
        {
            DisplayName = name,
            FilePath = path,
            SizeBytes = files.Sum(f => new FileInfo(f).Length),
            Quant = HfClient.QuantTag(Path.GetFileName(path)),
            NativeContext = info.ContextLength,
            RecommendedContext = info.ContextLength > 0 ? Math.Min(info.ContextLength, CustomRecommendedContext) : 8192,
            IsMoe = info.IsMoe,
            Architecture = info.Architecture,
            // Уверенно судить о качестве вызова инструментов нельзя — ориентируемся на поддержку в шаблоне.
            GoodToolCalling = template.Contains("tool", StringComparison.OrdinalIgnoreCase),
            Reasoning = reasoning,
            HasMtp = info.NextNPredictLayers > 0,
            Sampling = new SamplingSettings(),
            IsCustom = true,
            InstalledAtUtc = DateTime.UtcNow,
        };

        ConfigStore.Update(c =>
        {
            var baseId = CustomId(path);
            var id = baseId;
            for (var n = 2; c.Models.Installed.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase) && !SamePath(m.FilePath, path)); n++)
                id = $"{baseId}-{n}";
            model.Id = id;
            ReplaceAndActivate(c, model);
        });
        Log.Info("models", $"Добавлена пользовательская модель {model.Id}: {path} ({info.Architecture}, контекст {info.ContextLength})");
        return model;
    }

    public static void SetActive(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("Не указана модель.", nameof(modelId));
        var found = false;
        ConfigStore.Update(c =>
        {
            var m = c.Models.Installed.FirstOrDefault(x => string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null) return;
            found = true;
            c.Models.ActiveModelId = m.Id;
        });
        if (!found) throw new ModelException($"Модель «{modelId}» не установлена.");
    }

    /// <summary>Удалить модель из списка и (опционально) файлы с диска.</summary>
    /// <remarks>Файлы вне папки моделей не удаляются никогда (пользовательские модели там только отменяют регистрацию).</remarks>
    public static void Remove(string modelId, bool deleteFiles)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("Не указана модель.", nameof(modelId));
        var cfg = ConfigStore.Current;
        var entry = cfg.Models.Installed.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Log.Info("models", $"Удаление: модель {modelId} не найдена в списке.");
            return;
        }

        if (deleteFiles && !string.IsNullOrWhiteSpace(entry.FilePath))
        {
            var dir = ModelsDir(cfg);
            var otherUsers = cfg.Models.Installed.Where(m => !ReferenceEquals(m, entry)).SelectMany(m => ShardFiles(m.FilePath)).ToList();
            var failed = new List<string>();
            foreach (var file in ShardFiles(entry.FilePath).SelectMany(f => new[] { f, f + ".part" }))
            {
                if (!File.Exists(file)) continue;
                if (!IsInside(file, dir))
                {
                    Log.Info("models", $"Файл вне папки моделей не удаляется: {file}");
                    continue;
                }
                if (otherUsers.Any(o => SamePath(o, file)))
                {
                    Log.Info("models", $"Файл используется другой моделью и не удаляется: {file}");
                    continue;
                }
                try
                {
                    File.Delete(file);
                    Log.Info("models", $"Удалён файл {file}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("models", $"Не удалось удалить {file}: {ex.Message}");
                    failed.Add(Path.GetFileName(file));
                }
            }
            if (failed.Count > 0)
                throw new ModelException(
                    $"Не удалось удалить файл(ы) модели: {string.Join(", ", failed)}. Возможно, модель сейчас загружена сервером — " +
                    "остановите сервер и повторите удаление.");
        }

        ConfigStore.Update(c =>
        {
            c.Models.Installed.RemoveAll(m => string.Equals(m.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            FixActive(c);
        });
        Log.Info("models", $"Модель {entry.Id} удалена из списка{(deleteFiles ? " вместе с файлами" : "")}.");
    }

    /// <summary>Проверить, что файлы установленных моделей существуют; убрать пропавшие. Возвращает число удалённых записей.</summary>
    public static int Validate()
    {
        var cfg = ConfigStore.Current;
        var missing = cfg.Models.Installed.Where(m => string.IsNullOrWhiteSpace(m.FilePath) || !File.Exists(m.FilePath)).ToList();
        var activeBroken = cfg.Models.ActiveModelId is { } a && cfg.Models.Installed.All(m => m.Id != a || missing.Contains(m));
        if (missing.Count == 0 && !activeBroken && (cfg.Models.ActiveModelId is not null || cfg.Models.Installed.Count == 0))
            return 0;

        var removed = 0;
        ConfigStore.Update(c =>
        {
            removed = c.Models.Installed.RemoveAll(m => string.IsNullOrWhiteSpace(m.FilePath) || !File.Exists(m.FilePath));
            FixActive(c);
        });
        foreach (var m in missing) Log.Warn("models", $"Файл модели {m.Id} не найден ({m.FilePath}) — модель убрана из списка.");
        return removed;
    }

    // ── Вспомогательное ────────────────────────────────────────────────────────────

    private static void Register(InstalledModel model) => ConfigStore.Update(c => ReplaceAndActivate(c, model));

    private static void ReplaceAndActivate(AppConfig c, InstalledModel model)
    {
        var old = c.Models.Installed.FindIndex(m => string.Equals(m.Id, model.Id, StringComparison.OrdinalIgnoreCase));
        if (old >= 0)
        {
            if (!SamePath(c.Models.Installed[old].FilePath, model.FilePath))
                Log.Info("models", $"{model.Id}: заменён файл {c.Models.Installed[old].FilePath} → {model.FilePath} (старый файл не удалялся).");
            c.Models.Installed[old] = model;
        }
        else
        {
            c.Models.Installed.Add(model);
        }
        if (string.IsNullOrEmpty(c.Models.ActiveModelId) || !c.Models.Installed.Any(m => m.Id == c.Models.ActiveModelId))
            c.Models.ActiveModelId = model.Id;
    }

    private static void FixActive(AppConfig c)
    {
        if (c.Models.ActiveModelId is null || !c.Models.Installed.Any(m => m.Id == c.Models.ActiveModelId))
            c.Models.ActiveModelId = c.Models.Installed.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Куда сохранить файл: папка моделей + имя файла из репозитория. Если такое имя уже занято файлом
    /// другой модели (из другого репозитория) — подпапка с именем репозитория.
    /// </summary>
    internal static string DestinationFor(AppConfig cfg, string dir, CatalogModel model, string repoPath)
    {
        var name = SanitizeFileName(repoPath[(repoPath.LastIndexOf('/') + 1)..]);
        var dest = Path.Combine(dir, name);
        var conflict = cfg.Models.Installed.Any(m =>
            !string.Equals(m.Id, model.Id, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(m.Repo, model.Repo, StringComparison.OrdinalIgnoreCase) &&
            ShardFiles(m.FilePath).Any(f => SamePath(f, dest)));
        if (conflict) dest = Path.Combine(dir, SanitizeFileName(model.Repo.Replace('/', '_')), name);
        if (!IsInside(dest, dir)) throw new ModelException($"Недопустимое имя файла модели: {repoPath}");
        return dest;
    }

    /// <summary>Имя файла без недопустимых символов и попыток выйти из папки.</summary>
    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0 || result is "." or "..") result = "model.gguf";
        return result.Length > 200 ? result[^200..] : result;
    }

    /// <summary>Идентификатор пользовательской модели: «custom-» + имя файла (строчные латиница/цифры/.-_).</summary>
    internal static string CustomId(string path)
    {
        var stem = BaseName(path).ToLowerInvariant();
        var sb = new StringBuilder("custom-");
        var dash = false;
        foreach (var ch in stem)
        {
            var ok = ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_';
            if (ok) { sb.Append(ch); dash = false; }
            else if (!dash) { sb.Append('-'); dash = true; }
        }
        var id = sb.ToString().TrimEnd('-', '.');
        return id.Length > "custom-".Length ? id : "custom-model";
    }

    /// <summary>Имя файла без .gguf и суффикса шарда.</summary>
    private static string BaseName(string path)
    {
        var name = HfClient.StripShardSuffix(Path.GetFileName(path));
        return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Все шарды модели по пути первого (для неразбитой — сам файл).</summary>
    internal static IReadOnlyList<string> ShardFiles(string? firstPath)
    {
        if (string.IsNullOrWhiteSpace(firstPath)) return [];
        var name = Path.GetFileName(firstPath);
        var shard = HfClient.ShardInfo(name);
        if (shard is null) return [firstPath];
        var dir = Path.GetDirectoryName(firstPath) ?? "";
        return Enumerable.Range(1, shard.Value.Total).Select(i => Path.Combine(dir, ShardName(name, i, shard.Value.Total))).ToList();
    }

    private static string ShardName(string anyShardName, int index, int total)
    {
        var baseName = HfClient.StripShardSuffix(anyShardName);
        var prefix = baseName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? baseName[..^5] : baseName;
        return $"{prefix}-{index:D5}-of-{total:D5}.gguf";
    }

    internal static bool IsInside(string path, string dir)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static GgufInfo? TryReadGguf(string path)
    {
        try
        {
            return GgufReader.Read(path);
        }
        catch (Exception ex)
        {
            Log.Warn("models", $"Не удалось прочитать заголовок GGUF {path}: {ex.Message}");
            return null;
        }
    }

    private static SamplingSettings Clone(SamplingSettings? s) => s is null ? new SamplingSettings() : new SamplingSettings
    {
        Temperature = s.Temperature,
        TopP = s.TopP,
        TopK = s.TopK,
        MinP = s.MinP,
        RepeatPenalty = s.RepeatPenalty,
        PresencePenalty = s.PresencePenalty,
    };

    /// <summary>Прогресс загрузки одного файла → общий прогресс по всем шардам (тексты на русском).</summary>
    internal static StepProgress Describe(DownloadProgress p, string stage, long doneBefore, long total)
    {
        var overall = doneBefore + p.BytesReceived;
        double? fraction = total > 0 ? Math.Clamp((double)overall / total, 0, 1) : p.Fraction;
        switch (p.Stage)
        {
            case DownloadStage.Connecting:
                return new StepProgress(p.BytesReceived > 0 ? "Возобновление загрузки…" : "Подключение к Hugging Face…", fraction,
                    total > 0 ? $"{Size(overall)} из {Size(total)}" : null);
            case DownloadStage.Verifying:
                return new StepProgress("Проверка целостности файла (SHA-256)…", p.Fraction,
                    p.TotalBytes is > 0 ? $"{Size(p.BytesReceived)} из {Size(p.TotalBytes.Value)}" : null);
            case DownloadStage.Completed:
                return new StepProgress(stage, total > 0 ? Math.Clamp((double)(doneBefore + (p.TotalBytes ?? p.BytesReceived)) / total, 0, 1) : 1,
                    total > 0 ? $"{Size(doneBefore + (p.TotalBytes ?? p.BytesReceived))} из {Size(total)}" : null);
            default:
                var parts = new List<string> { total > 0 ? $"{Size(overall)} из {Size(total)}" : Size(overall) };
                if (p.BytesPerSecond > 1)
                {
                    parts.Add($"{Size((long)p.BytesPerSecond)}/с");
                    if (total > overall) parts.Add("осталось " + Eta(TimeSpan.FromSeconds((total - overall) / p.BytesPerSecond)));
                }
                return new StepProgress(stage, fraction, string.Join(" · ", parts));
        }
    }

    /// <summary>«1,2 ГБ», «85,3 МБ», «512 КБ».</summary>
    internal static string Size(long bytes)
    {
        double v = bytes;
        if (v >= 1024d * 1024 * 1024) return (v / (1024d * 1024 * 1024)).ToString("0.0", Ru) + " ГБ";
        if (v >= 1024d * 1024) return (v / (1024d * 1024)).ToString("0.0", Ru) + " МБ";
        if (v >= 1024) return (v / 1024).ToString("0", Ru) + " КБ";
        return bytes.ToString(Ru) + " Б";
    }

    internal static string Eta(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} ч {t.Minutes} мин";
        if (t.TotalMinutes >= 1) return $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} мин";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} с";
    }

    /// <summary>IProgress без захвата контекста синхронизации: пересчёт делается сразу в потоке загрузки.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            try { handler(value); } catch (Exception ex) { Log.Debug("models", $"Обработчик прогресса: {ex.Message}"); }
        }
    }
}
