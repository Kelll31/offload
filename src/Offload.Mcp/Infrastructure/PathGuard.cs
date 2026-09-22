using Offload.Core;

namespace Offload.Mcp.Infrastructure;

/// <summary>
/// Проверка путей, присланных IDE (а значит — LLM, возможно под prompt injection из содержимого репозитория).
/// Чтение: внутри корней рабочей области и явные абсолютные пути, кроме секретов, .git и чувствительных мест профиля.
/// Запись: только внутри корней (RestrictWritesToWorkspace), не через ссылки, не в .git и не в конфигурацию агентов.
/// </summary>
internal static class PathGuard
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Папки с ключами и учётными данными — не читаются и не пишутся нигде.</summary>
    private static readonly HashSet<string> SensitiveDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker",
    };

    /// <summary>Встроенные имена секретных файлов (в дополнение к McpSettings.SecretFilePatterns).</summary>
    private static readonly string[] BuiltinSecretPatterns =
    [
        ".git-credentials", ".credentials.json", "*.credentials.json", ".claude.json", "*.ppk", "*.ovpn", "*.kdbx",
    ];

    /// <summary>
    /// Файлы и папки, которые инструменты записи не меняют даже внутри проекта: конфигурация IDE/агентов
    /// (через них можно выдать себе права или внедрить инструкции), CI и git-хуки.
    /// </summary>
    private static readonly string[] ProtectedWriteDirs =
    [
        ".git", ".claude", ".cursor", ".vscode", ".codex", ".gemini", ".opencode", ".windsurf", ".husky", ".github/workflows",
    ];

    private static readonly HashSet<string> ProtectedWriteFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mcp.json", "CLAUDE.md", "CLAUDE.local.md", "AGENTS.md", "GEMINI.md", "opencode.json", "opencode.jsonc",
        ".cursorrules", ".windsurfrules", ".clinerules", ".gitmodules", ".gitconfig",
    };

    /// <summary>
    /// Разобрать путь от IDE в полный нормализованный путь. Относительные — от первого корня.
    /// Ошибка синтаксиса/опасный путь → ToolException.
    /// </summary>
    public static string Resolve(string raw, IReadOnlyList<string> roots, bool allowWildcards = false)
    {
        var p = Clean(raw);
        var error = ValidateSyntax(p, allowWildcards);
        if (error is not null) throw new ToolException($"Invalid path '{Shorten(raw)}': {error}");

        string full;
        var isAbsolute = p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/';
        if (isAbsolute)
        {
            full = Path.GetFullPath(p);
        }
        else
        {
            if (p[0] is '\\' or '/')
                throw new ToolException($"Invalid path '{Shorten(raw)}': use a path relative to the project root or a full path with a drive letter.");
            if (roots.Count == 0) throw new ToolException("No workspace root is known; pass an absolute path.");
            full = Path.GetFullPath(Path.Combine(roots[0], p));
            if (!IsInside(full, roots[0]))
                throw new ToolException(
                    $"Path '{Shorten(raw)}' escapes the project root via '..'. Pass an absolute path if you really mean a file outside the project.");
        }
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ToolException($"Invalid path '{Shorten(raw)}': UNC and device paths are not allowed.");
        return TrimTrailingSeparator(full);
    }

    /// <summary>Снять кавычки/пробелы, которые LLM иногда оставляет вокруг пути.</summary>
    public static string Clean(string raw)
    {
        var p = (raw ?? "").Trim();
        if (p.Length >= 2 && ((p[0] == '"' && p[^1] == '"') || (p[0] == '\'' && p[^1] == '\'') || (p[0] == '`' && p[^1] == '`')))
            p = p[1..^1].Trim();
        if (p.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) p = Uri.UnescapeDataString(p[8..]);
        return p;
    }

    /// <summary>null — синтаксически допустимый путь, иначе причина отказа.</summary>
    public static string? ValidateSyntax(string p, bool allowWildcards)
    {
        if (string.IsNullOrWhiteSpace(p)) return "empty path";
        if (p.Length > 1024) return "path is too long";
        foreach (var ch in p)
        {
            if (ch < 0x20 || ch == 0x7F) return "control characters are not allowed";
            if (ch is '<' or '>' or '"' or '|') return $"character '{ch}' is not allowed";
            if (!allowWildcards && ch is '*' or '?') return "wildcards are not allowed here";
        }
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal))
            return "UNC and device paths are not allowed";

        // Двоеточие допустимо только после буквы диска: «C:\…». Иначе это альтернативный поток NTFS (file.txt:stream) или «C:rel».
        var colon = p.IndexOf(':');
        if (colon >= 0)
        {
            if (colon != 1 || !char.IsAsciiLetter(p[0]) || p.Length < 3 || p[2] is not ('\\' or '/') || p.IndexOf(':', 2) >= 0)
                return "':' is only allowed after a drive letter (alternate data streams and drive-relative paths are not allowed)";
        }

        foreach (var seg in p.Split('\\', '/'))
        {
            if (seg.Length == 0 || seg is "." or "..") continue;
            var stem = seg.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(stem) || ReservedNames.Contains(seg.TrimEnd(' ', '.'))) return $"reserved device name '{seg}'";
        }
        return null;
    }

    /// <summary>
    /// Каноническая форма: symlink/junction и 8.3-имена разрешены для существующей части пути,
    /// несуществующий хвост дописывается как есть. ToolException — если ссылку разрешить нельзя.
    /// </summary>
    public static string Canonicalize(string fullPath)
    {
        var current = TrimTrailingSeparator(fullPath);
        var tail = new Stack<string>();
        while (!EntryExists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null) return fullPath;
            tail.Push(Path.GetFileName(current));
            current = parent;
        }
        var final = NativeMethods.GetFinalPath(current)
            ?? throw new ToolException($"Cannot resolve '{current}' (broken link or access denied).");
        foreach (var seg in tail) final = Path.Combine(final, seg);
        return TrimTrailingSeparator(final);
    }

    /// <summary>Существует ли запись каталога (в том числе «висячая» ссылка).</summary>
    public static bool EntryExists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    public static bool IsInside(string path, string root)
    {
        var r = TrimTrailingSeparator(root);
        var p = TrimTrailingSeparator(path);
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r.EndsWith('\\') ? r : r + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string? FindRoot(string path, IReadOnlyList<string> roots) =>
        roots.Where(r => IsInside(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();

    /// <summary>Путь для вывода: относительно корня (с «/»), иначе полный.</summary>
    public static string Display(string fullPath, IReadOnlyList<string> roots)
    {
        var root = FindRoot(fullPath, roots);
        if (root is null) return fullPath;
        var rel = Path.GetRelativePath(root, fullPath);
        return rel == "." ? "." : rel.Replace('\\', '/');
    }

    public static bool IsSecretName(string fileName, IEnumerable<string>? patterns)
    {
        var name = fileName.TrimEnd(' ', '.');
        if (name.Length == 0) return false;
        foreach (var pat in (patterns ?? []).Concat(BuiltinSecretPatterns))
        {
            if (string.IsNullOrWhiteSpace(pat)) continue;
            if (Glob.MatchName(name, pat.Trim())) return true;
        }
        return false;
    }

    /// <summary>Причина запрета чтения (для списка пропусков) или null.</summary>
    public static string? CheckRead(string fullPath, IEnumerable<string>? secretPatterns)
    {
        var segments = Segments(fullPath);
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase))) return ".git internals";
        if (segments.Any(SensitiveDirNames.Contains)) return "secret";
        if (IsSecretName(Path.GetFileName(fullPath), secretPatterns)) return "secret";
        if (IsSensitiveLocation(fullPath) || IsOffloadData(fullPath)) return "private location";
        return null;
    }

    /// <summary>Проверка чтения явно указанного пути; разрешает ссылки и проверяет цель. ToolException при запрете.</summary>
    public static string CheckReadExplicit(string fullPath, IEnumerable<string>? secretPatterns, string rawForMessage)
    {
        var reason = CheckRead(fullPath, secretPatterns);
        if (reason is null && EntryExists(fullPath))
        {
            var canonical = Canonicalize(fullPath);
            if (canonical.StartsWith(@"\\", StringComparison.Ordinal)) reason = "network location";
            else reason = CheckRead(canonical, secretPatterns);
        }
        if (reason is not null)
            throw new ToolException($"Refusing to read '{Shorten(rawForMessage)}': {reason}. Offload never reads secrets, .git internals or private profile folders.");
        return fullPath;
    }

    /// <summary>
    /// Проверка записи. Возвращает канонический путь (через ссылки), в который реально будет записан файл.
    /// </summary>
    public static string CheckWrite(string fullPath, IReadOnlyList<string> canonicalRoots, bool restrictToWorkspace,
        IEnumerable<string>? secretPatterns, string rawForMessage)
    {
        string Fail(string why) => throw new ToolException($"Refusing to write '{Shorten(rawForMessage)}': {why}");

        if (IsReparsePoint(fullPath)) Fail("the target is a symbolic link or junction.");
        var canonical = Canonicalize(fullPath);
        if (canonical.StartsWith(@"\\", StringComparison.Ordinal)) Fail("network locations are not allowed.");

        foreach (var candidate in new[] { fullPath, canonical })
        {
            var reason = CheckRead(candidate, secretPatterns);
            if (reason is not null) Fail(reason + ".");
        }

        var root = FindRoot(canonical, canonicalRoots);
        if (root is null)
        {
            if (restrictToWorkspace)
                Fail("it is outside the workspace roots (" + string.Join(", ", canonicalRoots) + "). Writes are restricted to the project.");
        }
        else
        {
            if (TrimTrailingSeparator(canonical).Equals(TrimTrailingSeparator(root), StringComparison.OrdinalIgnoreCase))
                Fail("it is the workspace root itself.");
            var rel = Path.GetRelativePath(root, canonical).Replace('\\', '/');
            foreach (var dir in ProtectedWriteDirs)
            {
                if (rel.Equals(dir, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)
                    || rel.Contains("/" + dir + "/", StringComparison.OrdinalIgnoreCase))
                    Fail($"'{dir}' is protected (IDE/agent configuration, CI or git internals); edit it yourself.");
            }
            if (ProtectedWriteFiles.Contains(Path.GetFileName(canonical)))
                Fail("agent/IDE configuration and instruction files are protected; edit them yourself.");
        }
        return canonical;
    }

    /// <summary>
    /// Дополнительные ограничения чтения ВНЕ корней рабочей области: данные приложений (AppData) и «точечные» папки
    /// профиля (~/.config, ~/.aws…) — там токены других программ. %TEMP% разрешён (туда часто пишут логи).
    /// </summary>
    public static string? CheckOutsideRoots(string canonicalPath, IReadOnlyList<string> roots)
    {
        if (FindRoot(canonicalPath, roots) is not null) return null;
        if (IsInside(canonicalPath, TrimTrailingSeparator(Path.GetTempPath()))) return null;
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (!string.IsNullOrEmpty(dir) && IsInside(canonicalPath, TrimTrailingSeparator(dir))) return "private location (application data outside the workspace)";
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && IsInside(canonicalPath, profile) && !TrimTrailingSeparator(canonicalPath).Equals(TrimTrailingSeparator(profile), StringComparison.OrdinalIgnoreCase))
        {
            var first = Path.GetRelativePath(profile, canonicalPath).Split('\\', '/')[0];
            if (first.StartsWith('.') || first.Equals("AppData", StringComparison.OrdinalIgnoreCase)) return "private location (profile settings folder outside the workspace)";
        }
        return null;
    }

    /// <summary>Полная проверка чтения существующего пути: исходная и каноническая форма (ссылки, 8.3) + ограничения вне проекта.</summary>
    public static string? CheckReadResolved(string fullPath, IReadOnlyList<string> roots, IEnumerable<string>? secretPatterns)
    {
        var reason = CheckRead(fullPath, secretPatterns);
        if (reason is not null) return reason;
        string canonical;
        try { canonical = Canonicalize(fullPath); }
        catch (ToolException) { return "broken link"; }
        if (canonical.StartsWith(@"\\", StringComparison.Ordinal)) return "network location";
        return CheckRead(canonical, secretPatterns) ?? CheckOutsideRoots(canonical, roots);
    }

    private static bool IsSensitiveLocation(string fullPath)
    {
        foreach (var dir in SensitiveDirs.Value)
        {
            if (IsInside(fullPath, dir)) return true;
        }
        return false;
    }

    private static readonly Lazy<string[]> SensitiveDirs = new(() =>
    {
        var list = new List<string>();
        void Add(string? baseDir, params string[] parts)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            try { list.Add(TrimTrailingSeparator(Path.GetFullPath(Path.Combine([baseDir, .. parts])))); } catch { }
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(appData, "Microsoft", "Credentials");
        Add(appData, "Microsoft", "Protect");
        Add(appData, "Microsoft", "Crypto");
        Add(appData, "Microsoft", "SystemCertificates");
        Add(local, "Microsoft", "Credentials");
        Add(local, "Google", "Chrome", "User Data");
        Add(local, "Microsoft", "Edge", "User Data");
        Add(local, "BraveSoftware");
        Add(appData, "Mozilla", "Firefox", "Profiles");
        Add(appData, "Opera Software");
        Add(local, "Packages");
        return [.. list];
    });

    /// <summary>Папка данных Offload (config.json с ключом API, снимки задач) — закрыта для чтения инструментами.</summary>
    public static bool IsOffloadData(string fullPath)
    {
        var dataDir = TrimTrailingSeparator(AppPaths.DataDir);
        if (IsInside(fullPath, dataDir)) return true;
        var cache = _dataDirCanonical;
        if (cache is null || cache.Value.Key != dataDir)
        {
            string canon;
            try { canon = Directory.Exists(dataDir) ? NativeMethods.GetFinalPath(dataDir) ?? dataDir : dataDir; }
            catch { canon = dataDir; }
            cache = (dataDir, TrimTrailingSeparator(canon));
            _dataDirCanonical = cache;
        }
        return IsInside(fullPath, cache.Value.Canonical);
    }

    private static (string Key, string Canonical)? _dataDirCanonical;

    private static string[] Segments(string fullPath) =>
        fullPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    public static string TrimTrailingSeparator(string p)
    {
        if (p.Length > 3 && (p.EndsWith('\\') || p.EndsWith('/'))) return p.TrimEnd('\\', '/');
        return p;
    }

    private static string Shorten(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
