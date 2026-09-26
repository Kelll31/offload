using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Offload.Mcp.Index;

namespace Offload.Mcp.Infrastructure;

/// <summary>Прочитанный текстовый файл проекта: строки и (лениво) символы.</summary>
internal sealed class SourceFile
{
    private IReadOnlyList<CodeSymbol>? _symbols;

    public required string FullPath { get; init; }
    public required string Display { get; init; }
    public required string[] Lines { get; init; }
    public required CodeLang Lang { get; init; }
    public bool Truncated { get; init; }

    /// <summary>Сколько значений секретов замаскировано при чтении (0 — текст как в файле или секретов нет).</summary>
    public int RedactedCount { get; init; }

    public IReadOnlyList<CodeSymbol> Symbols => _symbols ??= Infrastructure.Symbols.Parse(FullPath, Lines);

    public bool IsTest => CodeIndex.IsTestPath(Display);
}

/// <summary>Результат загрузки индекса: файлы и заметки о пропусках/ограничениях.</summary>
internal sealed class CodeIndexResult
{
    public List<SourceFile> Files { get; } = [];
    public GatherResult Info { get; } = new();
    public string? Note { get; set; }

    public string CoverageNote()
    {
        var parts = new List<string> { $"{Files.Count} files indexed" };
        if (Info.LimitNote is not null) parts.Add(Info.LimitNote);
        if (Note is not null) parts.Add(Note);
        var skipped = Info.Skipped.Where(s => s.Reason is not ("binary" or "no files match")).Take(5).ToList();
        if (skipped.Count > 0) parts.Add("skipped: " + string.Join(", ", skipped.Select(s => $"{s.Display} ({s.Reason})")));
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Индекс исходников рабочей папки для поиска, символов, контекста и анализа влияния. Файлы перечисляются через
/// FileGatherer (учёт .gitignore, без секретов, двоичных и служебных папок) и кэшируются в процессе по (размер, время изменения).
/// При Mcp.RedactSecrets текст маскируется при загрузке (<see cref="SecretRedactor"/> с путём файла — действуют и правила
/// конфигов «password: …»): поиск regex, files_only, символы и фрагменты работают по замаскированному тексту, и значение
/// секрета нельзя подобрать по наличию совпадения. Незамаскированный текст — только для проверок, которые сами ищут
/// секреты (code_scan secrets, security_review) или пишут файлы (rename), и в ответ он не попадает.
/// </summary>
internal static partial class CodeIndex
{
    public const int MaxIndexFiles = 20_000;
    public const long MaxIndexChars = 160L * 1024 * 1024;
    private const int MaxCacheEntries = 60_000;

    /// <summary>Ключ — «флаг маскирования|полный путь»: замаскированная и исходная версии файла кэшируются раздельно.</summary>
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Mtime, int MaxBytes, SourceFile File)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Текстовые файлы, которые полезно искать помимо кода (конфигурация, документация, разметка).</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".jsonc", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".props", ".targets", ".csproj",
        ".fsproj", ".vbproj", ".sln", ".slnx", ".html", ".htm", ".css", ".scss", ".less", ".sql", ".sh", ".ps1", ".psm1", ".bat", ".cmd",
        ".gradle", ".cmake", ".proto", ".graphql", ".razor", ".cshtml", ".xaml", ".resx", ".dfm", ".fmx", ".dproj", ".rst", ".adoc",
        ".editorconfig", ".gitignore", ".dockerignore", ".env.example", ".lua", ".r", ".scala", ".dart", ".fs", ".vb", ".m", ".mm",
        ".ex", ".exs", ".erl", ".hs", ".clj", ".groovy", ".pl", ".tf", ".hcl", ".nix", ".zig", ".jl",
    };

    public static bool IsSearchable(string path)
    {
        if (Symbols.IsCode(path)) return true;
        var name = Path.GetFileName(path);
        if (name is "Makefile" or "Dockerfile" or "CMakeLists.txt" or "Jenkinsfile" or "Gemfile" or "Rakefile") return true;
        return TextExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>Тестовый файл: путь или имя указывают на тесты (tests/, *Tests.cs, *.test.ts, test_*.py, *_test.go и т. п.).</summary>
    public static bool IsTestPath(string display)
    {
        var p = display.Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(p);
        if (TestDir().IsMatch("/" + p)) return true;
        return name.EndsWith("Tests", StringComparison.Ordinal) || name.EndsWith("Test", StringComparison.Ordinal)
            || name.EndsWith("Spec", StringComparison.Ordinal) || name.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".spec", StringComparison.OrdinalIgnoreCase) || name.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_test", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"/(tests?|__tests__|specs?|testing|[\w.-]*\.tests?)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestDir();

    /// <summary>
    /// Загрузить файлы области (пути/папки/glob; по умолчанию — вся рабочая папка). codeOnly — только исходники.
    /// unredacted — текст как в файле, даже при Mcp.RedactSecrets (только для поиска секретов и записи; не для показа).
    /// </summary>
    public static async Task<CodeIndexResult> LoadAsync(ToolContext ctx, IReadOnlyList<string>? scope, bool codeOnly, CancellationToken ct,
        bool unredacted = false)
    {
        var redact = ctx.Cfg.Mcp.RedactSecrets && !unredacted;
        var result = new CodeIndexResult();
        var specs = scope is { Count: > 0 } ? scope.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() : [ctx.Roots[0]];
        if (specs.Count == 0) specs = [ctx.Roots[0]];
        var opts = ctx.GatherOptions with { MaxFiles = MaxIndexFiles, MaxEntriesVisited = 120_000 };
        var files = await FileGatherer.ListFilesAsync(specs, ctx.Roots, opts, result.Info, ct).ConfigureAwait(false);
        var wanted = files.Where(f => codeOnly ? Symbols.IsCode(f) : IsSearchable(f)).ToList();

        var loaded = new SourceFile?[wanted.Count];
        Parallel.For(0, wanted.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
            i => loaded[i] = TryLoad(wanted[i], ctx.Roots, opts.MaxFileBytes, redact, out _));
        long chars = 0;
        foreach (var f in loaded)
        {
            if (f is null) continue;
            chars += f.Lines.Sum(l => (long)l.Length + 1);
            if (chars > MaxIndexChars)
            {
                result.Note = "index size limit reached; narrow the paths";
                break;
            }
            result.Files.Add(f);
        }
        if (Cache.Count > MaxCacheEntries) Cache.Clear();
        return result;
    }

    /// <summary>Один файл (для явного пути): с проверками чтения. null — не текст или недоступен. unredacted — как в <see cref="LoadAsync"/>.</summary>
    public static SourceFile? LoadOne(ToolContext ctx, string raw, bool unredacted = false)
    {
        var full = ctx.ResolveRead(raw);
        if (!File.Exists(full)) throw new ToolException(Directory.Exists(full) ? $"'{raw}' is a directory; pass a file." : $"File '{raw}' not found.");
        if (FileGatherer.CheckFile(full, ctx.GatherOptions, ctx.Roots, isExplicit: true) is { } why) throw new ToolException($"Cannot read '{raw}': {why}.");
        return TryLoad(full, ctx.Roots, Math.Max(ctx.GatherOptions.MaxFileBytes, 2 * 1024 * 1024), ctx.Cfg.Mcp.RedactSecrets && !unredacted, out _)
               ?? throw new ToolException($"'{raw}' is not a readable text file.");
    }

    /// <summary>
    /// Открыть постоянный индекс (<see cref="IndexStore"/>) для области: файлы перечисляются и проверяются как в <see cref="LoadAsync"/>
    /// (FileGatherer/PathGuard на каждом вызове), изменившиеся с прошлого раза разбираются заново, текст читается лениво — только
    /// для файлов, которые реально нужно показать или проверить. codeOnly — в представлении только исходники.
    /// </summary>
    public static async Task<IndexView> OpenAsync(ToolContext ctx, IReadOnlyList<string>? scope, bool codeOnly, CancellationToken ct)
    {
        var info = new GatherResult();
        var specs = scope is { Count: > 0 } ? scope.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() : [ctx.Roots[0]];
        if (specs.Count == 0) specs = [ctx.Roots[0]];
        var opts = ctx.GatherOptions with { MaxFiles = MaxIndexFiles, MaxEntriesVisited = 120_000 };
        var all = await FileGatherer.ListFilesAsync(specs, ctx.Roots, opts, info, ct).ConfigureAwait(false);
        var searchable = all.Where(IsSearchable).ToList();
        // Удалять из базы пропавшие файлы можно, только если перечислен весь корень без обрезки по лимиту.
        var prune = IsWholeRoot(specs, ctx.Roots) && info.LimitNote is null;
        var session = await IndexStore.OpenAsync(new IndexRequest(ctx.Roots[0], ctx.Roots, searchable, opts.MaxFileBytes, prune), ct)
            .ConfigureAwait(false);
        return new IndexView(session, searchable, all, info, ctx.Roots, codeOnly, opts.MaxFileBytes, ctx.Cfg.Mcp.RedactSecrets);
    }

    private static bool IsWholeRoot(List<string> specs, IReadOnlyList<string> roots)
    {
        if (specs.Count != 1) return false;
        try
        {
            return string.Equals(Path.GetFullPath(PathGuard.Resolve(specs[0], roots)).TrimEnd('\\', '/'), Path.GetFullPath(roots[0]).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ToolException)
        {
            return false;
        }
    }

    /// <summary>Только для тестов: сбросить кэш текста процесса (имитация нового MCP-процесса).</summary>
    internal static void ClearCache() => Cache.Clear();

    /// <summary>
    /// Прочитать текстовый файл (с кэшем процесса по размеру и времени изменения). null — не текст (binary = true) или
    /// временно недоступен. Путь должен быть уже проверен FileGatherer/PathGuard.
    /// </summary>
    internal static SourceFile? TryLoad(string full, IReadOnlyList<string> roots, int maxBytes, bool redact, out bool binary)
    {
        binary = false;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) return null;
            var key = (redact ? "r|" : "u|") + full;
            if (Cache.TryGetValue(key, out var c) && c.Length == info.Length && c.Mtime == info.LastWriteTimeUtc && c.MaxBytes == maxBytes) return c.File;
            var toRead = (int)Math.Min(info.Length, maxBytes);
            var buffer = new byte[toRead];
            int read;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                read = fs.ReadAtLeast(buffer, toRead, throwOnEndOfStream: false);
            var data = buffer.AsSpan(0, read);
            if (TextCodec.LooksBinary(data))
            {
                binary = true;
                return null;
            }
            var (text, _) = TextCodec.Decode(data, truncated: info.Length > read);
            // Маскирование — до разбиения на строки и до кэша: число строк сохраняется, path:line остаются верными.
            var redacted = 0;
            if (redact) text = SecretRedactor.Redact(text, full, out redacted);
            var file = new SourceFile
            {
                FullPath = full,
                Display = PathGuard.Display(full, roots),
                Lines = TextCodec.SplitLines(text),
                Lang = Symbols.LangOf(full),
                Truncated = info.Length > read,
                RedactedCount = redacted,
            };
            Cache[key] = (info.Length, info.LastWriteTimeUtc, maxBytes, file);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Регулярное выражение «слово целиком» для идентификатора.</summary>
    public static Regex WordRegex(string name, bool ignoreCase = false) =>
        new(@"(?<![\w$])" + Regex.Escape(name) + @"(?![\w$])",
            RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(2));

    /// <summary>Строка — комментарий целиком (упрощённо, для отсева ссылок).</summary>
    public static bool IsCommentLine(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal) || t.StartsWith('*')
               || t.StartsWith('#') && !t.StartsWith("#include", StringComparison.Ordinal) && !t.StartsWith("#define", StringComparison.Ordinal)
               || t.StartsWith("--", StringComparison.Ordinal) || t.StartsWith("'''", StringComparison.Ordinal);
    }

    /// <summary>Вхождение в позиции index находится внутри строкового литерала (по чётности кавычек до него) или после «//».</summary>
    public static bool InStringOrComment(string line, int index)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0 && comment < index && !InQuotes(line, comment)) return true;
        return InQuotes(line, index);
    }

    private static bool InQuotes(string line, int index)
    {
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < index && i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) inString = false;
            }
            else if (c is '"' or '\'' or '`')
            {
                inString = true;
                quote = c;
            }
        }
        return inString;
    }

    /// <summary>Строки файла с номерами «N| текст» (для контекста), с обрезкой длинных строк.</summary>
    public static string Numbered(SourceFile f, int from, int to, int maxLineChars = 220)
    {
        var sb = new System.Text.StringBuilder();
        from = Math.Max(1, from);
        to = Math.Min(f.Lines.Length, to);
        for (var i = from; i <= to; i++)
        {
            var l = f.Lines[i - 1];
            if (l.Length > maxLineChars) l = l[..maxLineChars] + "…";
            sb.Append(i).Append("| ").Append(l).Append('\n');
        }
        return sb.ToString();
    }
}
