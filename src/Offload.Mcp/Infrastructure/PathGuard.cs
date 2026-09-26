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
        ".github/actions", ".devcontainer", ".idea",
    ];

    private static readonly HashSet<string> ProtectedWriteFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mcp.json", "CLAUDE.md", "CLAUDE.local.md", "AGENTS.md", "GEMINI.md", "opencode.json", "opencode.jsonc",
        ".cursorrules", ".windsurfrules", ".clinerules", ".gitmodules", ".gitconfig",
        ".gitlab-ci.yml", ".pre-commit-config.yaml", "lefthook.yml", ".lefthook.yml", "copilot-instructions.md",
    };

    /// <summary>
    /// Pathspec-исключения git для секретов (шаблоны настроек + встроенные) и секретных папок: такие файлы не хешируются
    /// в хранилище объектов (снимок песочницы, коммиты агента) и не попадают в поиск по истории.
    /// </summary>
    public static List<string> SecretPathspecExcludes(IEnumerable<string>? secretPatterns)
    {
        var list = new List<string>();
        foreach (var raw in (secretPatterns ?? []).Concat(BuiltinSecretPatterns))
        {
            var p = raw?.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(p) || p.Contains(':') || p.Contains(')')) continue;
            list.Add(":(exclude,glob,icase)**/" + p);
        }
        foreach (var d in SensitiveDirNames) list.Add(":(exclude,glob,icase)**/" + d + "/**");
        return list;
    }

    /// <summary>
    /// Разобрать путь от IDE в полный нормализованный путь. Относительные — от первого корня.
    /// Клиент в WSL (<see cref="WslPaths"/>): Linux-пути переводятся в Windows-форму, а путь \\wsl.localhost\&lt;дистрибутив&gt;\…
    /// допускается только внутри корней рабочей области. Ошибка синтаксиса/опасный путь → ToolException.
    /// </summary>
    public static string Resolve(string raw, IReadOnlyList<string> roots, bool allowWildcards = false)
    {
        var p = Clean(raw);
        if (WslPaths.IsActive && p.Length > 0 && p[0] == '/' && !p.StartsWith("//", StringComparison.Ordinal))
        {
            p = WslPaths.LinuxToWindows(p)
                ?? throw new ToolException($"Invalid path '{Shorten(raw)}': this Linux path cannot be mapped to Windows; pass a path relative to the project root.");
        }
        else if (WslPaths.IsActive)
        {
            p = WslPaths.NormalizeHost(p);
        }
        var wslUnc = WslPaths.IsActive && p.StartsWith(WslPaths.UncHost, StringComparison.OrdinalIgnoreCase);
        var error = ValidateSyntax(wslUnc ? @"C:\" + p[2..] : p, allowWildcards);
        if (error is not null) throw new ToolException($"Invalid path '{Shorten(raw)}': {error}");

        string full;
        var isAbsolute = wslUnc || (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/');
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
        full = WslPaths.ToDrivePath(full) ?? full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (!WslPaths.IsAllowedUnc(full))
                throw new ToolException($"Invalid path '{Shorten(raw)}': UNC and device paths are not allowed.");
            if (FindRoot(full, roots) is null)
                throw new ToolException($"Invalid path '{Shorten(raw)}': WSL paths are only allowed inside the workspace roots.");
        }
        return TrimTrailingSeparator(full);
    }

    /// <summary>Снять кавычки/пробелы, которые LLM иногда оставляет вокруг пути.</summary>
    public static string Clean(string raw)
    {
        var p = (raw ?? "").Trim();
        if (p.Length >= 2 && ((p[0] == '"' && p[^1] == '"') || (p[0] == '\'' && p[^1] == '\'') || (p[0] == '`' && p[^1] == '`')))
            p = p[1..^1].Trim();
        if (p.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            p = Uri.UnescapeDataString(p[8..]);
            // Клиент в WSL: file:///home/u/x — Linux-путь (без буквы диска), слеш в начале возвращаем.
            if (WslPaths.IsActive && !(p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')) p = "/" + p;
        }
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
        final = WslPaths.NormalizeHost(final);
        final = WslPaths.ToDrivePath(final) ?? final;
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
        // Linux-пути в WSL (\\wsl.localhost\<d>\home\u\proj) чувствительны к регистру: /home/u/PROJ — другая папка, чем
        // /home/u/proj. Без учёта регистра сравниваются только имя сервера и дистрибутива.
        if (WslUncSplit(r) is { } rs)
        {
            if (WslUncSplit(p) is not { } ps || !ps.Share.Equals(rs.Share, StringComparison.OrdinalIgnoreCase)) return false;
            if (ps.Tail.Equals(rs.Tail, StringComparison.Ordinal)) return true;
            var linuxPrefix = rs.Tail.Length == 0 || rs.Tail.EndsWith('\\') ? rs.Tail : rs.Tail + "\\";
            return ps.Tail.StartsWith(linuxPrefix, StringComparison.Ordinal);
        }
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r.EndsWith('\\') ? r : r + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>\\wsl.localhost\&lt;d&gt;\rest (или \\wsl$\…) → («\\wsl.localhost\&lt;d&gt;», «rest»); иначе null.</summary>
    private static (string Share, string Tail)? WslUncSplit(string path)
    {
        var p = WslPaths.NormalizeHost(path);
        if (!p.StartsWith(WslPaths.UncHost, StringComparison.OrdinalIgnoreCase)) return null;
        var slash = p.IndexOf('\\', WslPaths.UncHost.Length);
        return slash < 0 ? (p, "") : (p[..slash], p[(slash + 1)..]);
    }

    /// <summary>
    /// Задача (job) видна сессии: её корень внутри одного из корней сессии. Не наоборот — сессия, открытая в подпапке,
    /// не читает задачи родительского проекта (их diff может содержать файлы вне её корней).
    /// </summary>
    public static bool JobVisible(string jobRoot, IReadOnlyList<string> sessionRoots) =>
        !string.IsNullOrEmpty(jobRoot) && sessionRoots.Any(r => IsInside(jobRoot, r));

    public static string? FindRoot(string path, IReadOnlyList<string> roots) =>
        roots.Where(r => IsInside(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();

    /// <summary>Путь для вывода: относительно корня (с «/»), иначе полный.</summary>
    public static string Display(string fullPath, IReadOnlyList<string> roots)
    {
        var root = FindRoot(fullPath, roots);
        if (root is null) return WslPaths.ToClientPath(fullPath);
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
            if (canonical.StartsWith(@"\\", StringComparison.Ordinal) && !WslPaths.IsAllowedUnc(canonical)) reason = "network location";
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
        IEnumerable<string>? secretPatterns, string rawForMessage) =>
        CheckWriteCore(fullPath, canonicalRoots, restrictToWorkspace, secretPatterns, rawForMessage, resolveWslLinks: true);

    private static string CheckWriteCore(string fullPath, IReadOnlyList<string> canonicalRoots, bool restrictToWorkspace,
        IEnumerable<string>? secretPatterns, string rawForMessage, bool resolveWslLinks)
    {
        string Fail(string why) => throw new ToolException($"Refusing to write '{Shorten(rawForMessage)}': {why}");

        if (IsReparsePoint(fullPath)) Fail("the target is a symbolic link or junction.");
        var canonical = Canonicalize(fullPath);
        var wslUnc = canonical.StartsWith(@"\\", StringComparison.Ordinal);
        if (wslUnc && !WslPaths.IsAllowedUnc(canonical)) Fail("network locations are not allowed.");

        // WSL: сервер 9P может разрешать Linux-ссылки сам, и GetFinalPathNameByHandle их не показывает. Спрашиваем дистрибутив
        // (realpath) и проверяем настоящую цель заново — против настоящих корней и всегда с ограничением рабочей областью.
        if (wslUnc && resolveWslLinks && CheckWslWriteLinks(canonical, canonicalRoots, secretPatterns, rawForMessage) is { } redirected)
            return redirected;

        foreach (var candidate in new[] { fullPath, canonical })
        {
            var reason = CheckRead(candidate, secretPatterns);
            if (reason is not null) Fail(reason + ".");
        }

        var root = FindRoot(canonical, canonicalRoots);
        if (root is null)
        {
            // Путь WSL вне проекта не записывается никогда (профиль Linux, /etc), даже при RestrictWritesToWorkspace = false.
            if (restrictToWorkspace || wslUnc)
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
        if (BuildPolicy.Value is { Protect: true, Allow: false } && (IsBuildFile(fullPath) || IsBuildFile(canonical)))
            Fail("it is a build/test configuration file (build scripts run on local_verify), and Mcp.ProtectBuildFiles is on. " +
                 "Review the change and pass allow_build_files=true if it is intended, or edit the file yourself.");
        return canonical;
    }

    /// <summary>
    /// Запись по каноническому пути WSL: null — ссылок нет, проверка продолжается как обычно; иначе — канонический путь
    /// настоящей цели, уже проверенный заново (корни — настоящие, запись только внутри них). ToolException при отказе.
    /// </summary>
    internal static string? CheckWslWriteLinks(string canonical, IReadOnlyList<string> canonicalRoots, IEnumerable<string>? secretPatterns,
        string rawForMessage)
    {
        string Fail(string why) => throw new ToolException($"Refusing to write '{Shorten(rawForMessage)}': {why}");
        switch (ResolveWslLinks(canonical, canonicalRoots, forWrite: true))
        {
            case WslLinkCheck.Unverifiable u:
                return Fail($"symbolic links in this WSL path cannot be verified ({u.Why}); edit it from WSL yourself.");
            case WslLinkCheck.LeafLink:
                return Fail("the target is a symbolic link.");
            case WslLinkCheck.Redirected r:
                // Цель вне настоящих корней — отказ сразу, без обращения к файловой системе цели.
                if (FindRoot(r.Target, r.Roots) is null)
                    return Fail("it goes through a symbolic link to a location outside the workspace roots.");
                if (CheckRead(r.Target, secretPatterns) is { } reason) return Fail(reason + " (through a symbolic link).");
                return CheckWriteCore(r.Target, r.Roots, restrictToWorkspace: true, secretPatterns, rawForMessage, resolveWslLinks: false);
            default:
                return null;
        }
    }

    /// <summary>Итог проверки Linux-ссылок в пути WSL.</summary>
    private abstract record WslLinkCheck
    {
        /// <summary>Ссылок на пути нет — проверяется как есть.</summary>
        public sealed record Direct : WslLinkCheck;

        /// <summary>Сам целевой файл — символическая ссылка (запись через ссылку запрещена, как reparse point в Windows).</summary>
        public sealed record LeafLink : WslLinkCheck;

        /// <summary>Путь проходит через ссылку: настоящая цель и настоящие корни (Windows-форма).</summary>
        public sealed record Redirected(string Target, IReadOnlyList<string> Roots) : WslLinkCheck;

        /// <summary>Дистрибутив не ответил — проверить нельзя (отказ).</summary>
        public sealed record Unverifiable(string Why) : WslLinkCheck;
    }

    /// <summary>
    /// Разрешить ссылки пути WSL внутри дистрибутива: <c>realpath -m</c> для пути, его родителя и корней одним вызовом wsl.exe.
    /// Если realpath недоступен — <c>find … -maxdepth 0 -type l</c> по компонентам пути ниже корня: любая ссылка → отказ.
    /// </summary>
    private static WslLinkCheck ResolveWslLinks(string canonical, IReadOnlyList<string> roots, bool forWrite)
    {
        var linux = WslPaths.ToLinuxPath(canonical);
        if (linux is null || linux == "/") return new WslLinkCheck.Unverifiable("not a path of the current WSL distribution");
        var cut = linux.LastIndexOf('/');
        var parent = cut <= 0 ? "/" : linux[..cut];
        var name = linux[(cut + 1)..];
        var rootLinux = roots.Select(WslPaths.ToLinuxPath).ToList();
        var query = new List<string> { linux, parent };
        query.AddRange(rootLinux.OfType<string>());
        var real = WslPaths.RealPaths(query);
        if (real is null)
        {
            var root = FindRoot(canonical, roots);
            var rootL = root is null ? null : WslPaths.ToLinuxPath(root);
            if (rootL is null) return new WslLinkCheck.Unverifiable("wsl.exe realpath is unavailable");
            var components = new List<string>();
            var rel = linux.Length > rootL.Length ? linux[rootL.Length..].Trim('/') : "";
            var acc = rootL.TrimEnd('/');
            foreach (var seg in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                acc += "/" + seg;
                components.Add(acc);
            }
            var links = WslPaths.FindSymlinks(components);
            if (links is null) return new WslLinkCheck.Unverifiable("wsl.exe realpath and find are unavailable");
            return links.Count == 0 ? new WslLinkCheck.Direct() : new WslLinkCheck.Unverifiable("the path goes through a symbolic link " + links[0]);
        }
        var realFull = real[0];
        var realParent = real[1];
        if (forWrite && realFull != (realParent == "/" ? "/" + name : realParent + "/" + name)) return new WslLinkCheck.LeafLink();
        var realRoots = new List<string>();
        var rootsChanged = false;
        var k = 2;
        foreach (var (r, rl) in roots.Zip(rootLinux))
        {
            if (rl is null)
            {
                realRoots.Add(r);
                continue;
            }
            var rr = real[k++];
            if (rr == rl) realRoots.Add(r);
            else if (FromLinuxReal(rr) is { } w)
            {
                realRoots.Add(w);
                rootsChanged = true;
            }
        }
        if (realFull == linux && !rootsChanged) return new WslLinkCheck.Direct();
        return FromLinuxReal(realFull) is { } target
            ? new WslLinkCheck.Redirected(target, realRoots)
            : new WslLinkCheck.Unverifiable("the link target cannot be mapped to a Windows path");
    }

    /// <summary>Linux-путь, полученный от realpath, → Windows-путь (UNC дистрибутива или диск для /mnt/&lt;буква&gt;); null — не переводится.</summary>
    private static string? FromLinuxReal(string linuxPath)
    {
        if (WslPaths.LinuxToWindows(linuxPath) is not { } w) return null;
        try
        {
            var full = Path.GetFullPath(w);
            return TrimTrailingSeparator(WslPaths.ToDrivePath(full) ?? full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Чтение существующего пути WSL: настоящая цель (через realpath дистрибутива) и настоящие корни. null — путь без ссылок
    /// или не WSL; причина отказа — если проверить нельзя или цель за пределами.
    /// </summary>
    internal static string? CheckWslReadLinks(string canonical, IReadOnlyList<string> roots, IEnumerable<string>? secretPatterns)
    {
        switch (ResolveWslLinks(canonical, roots, forWrite: false))
        {
            case WslLinkCheck.Unverifiable:
                return "network location (WSL links cannot be verified)";
            case WslLinkCheck.Redirected r:
                if (r.Target.StartsWith(@"\\", StringComparison.Ordinal)
                    && (!WslPaths.IsAllowedUnc(r.Target) || FindRoot(r.Target, r.Roots) is null)) return "network location";
                return CheckRead(r.Target, secretPatterns) ?? CheckOutsideRoots(r.Target, r.Roots);
            default:
                // Сам файл — ссылка на цель внутри того же каталога или прямой путь: проверяется как обычно.
                return null;
        }
    }

    /// <summary>Политика правки файлов сборки на время одного вызова инструмента: Mcp.ProtectBuildFiles и allow_build_files.</summary>
    private sealed record BuildFilePolicy(bool Protect, bool Allow);

    /// <summary>
    /// Действует для всего асинхронного потока вызова, включая фоновые задачи и слияние песочницы: все записи
    /// идут через CheckWrite (ToolContext.ResolveWrite), поэтому защита не зависит от конкретного инструмента.
    /// </summary>
    private static readonly AsyncLocal<BuildFilePolicy?> BuildPolicy = new();

    /// <summary>В текущем вызове явно разрешена правка файлов сборки (allow_build_files=true); запоминается в задаче.</summary>
    public static bool BuildFilesAllowed => BuildPolicy.Value?.Allow == true;

    /// <summary>
    /// Слияние задачи (local_job merge): если задача создана с allow_build_files=true, то же разрешение действует на время
    /// тела — только для этой задачи; иначе политика вызова не меняется.
    /// </summary>
    public static async Task<T> WithJobBuildFilePolicy<T>(JobInfo job, Func<Task<T>> body)
    {
        // Значение AsyncLocal, заданное здесь, видно только телу и сбрасывается на выходе из метода.
        if (job.AllowBuildFiles && BuildPolicy.Value is { Allow: false } policy) BuildPolicy.Value = policy with { Allow = true };
        return await body().ConfigureAwait(false);
    }

    /// <summary>
    /// Обёртка тела записывающего инструмента: включает политику файлов сборки из настроек и параметра вызова.
    /// </summary>
    public static Func<ToolContext, Task<string>> WithBuildFilePolicy(bool allowBuildFiles, Func<ToolContext, Task<string>> body) =>
        async ctx =>
        {
            // Значение AsyncLocal, заданное в async-методе, видно только ему и вызванному им коду и сбрасывается на выходе.
            BuildPolicy.Value = new BuildFilePolicy(ctx.Cfg.Mcp.ProtectBuildFiles, allowBuildFiles);
            return await body(ctx).ConfigureAwait(false);
        };

    private static readonly HashSet<string> BuildFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "Makefile", "makefile", "GNUmakefile", "CMakeLists.txt", "conftest.py", "setup.py", "setup.cfg", "pyproject.toml",
        "pytest.ini", "tox.ini", "noxfile.py", "manage.py", "build.rs", "Cargo.toml", "pom.xml", "gradle.properties", "gradlew", "gradlew.bat",
        "mvnw", "mvnw.cmd", "nuget.config", "global.json", "Rakefile", "Gemfile", "justfile", "Taskfile.yml", "build.ps1", "build.cmd", "build.sh",
    };

    private static readonly HashSet<string> BuildFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".props", ".targets", ".csproj", ".fsproj", ".vbproj", ".vcxproj", ".proj", ".gradle", ".mk", ".cmake",
    };

    /// <summary>
    /// Файл, который выполняется или управляет выполнением при сборке и тестах: проекты и импорты MSBuild (Directory.Build.*,
    /// в том числе .rsp), package.json (scripts), Makefile, conftest.py, setup.py, build.rs, *.gradle(.kts), конфиги JS-инструментов
    /// (jest/vite/eslint.config.js). Эвристика: сам код тестов тоже выполняется при local_verify.
    /// </summary>
    public static bool IsBuildFile(string path)
    {
        var name = Path.GetFileName(TrimTrailingSeparator(path)).TrimEnd(' ', '.');
        if (name.Length == 0) return false;
        if (BuildFileNames.Contains(name) || BuildFileExtensions.Contains(Path.GetExtension(name))) return true;
        if (name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Directory.Packages.", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Contains(".gradle", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".js" or ".cjs" or ".mjs" or ".ts" or ".cts" or ".mts"
               && Path.GetFileNameWithoutExtension(name).EndsWith(".config", StringComparison.OrdinalIgnoreCase);
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
        if (canonical.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (!WslPaths.IsAllowedUnc(canonical) || FindRoot(canonical, roots) is null) return "network location";
            // Ссылка внутри дистрибутива (9P разрешает её незаметно для Windows) — настоящая цель проверяется заново.
            if (CheckWslReadLinks(canonical, roots, secretPatterns) is { } why) return why;
        }
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
