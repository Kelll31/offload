using Offload.Core;
using Offload.Core.Logging;
using Offload.Integrations.Clients;
using Offload.Integrations.Editing;

namespace Offload.Integrations;

/// <summary>
/// Инструкции по делегированию для клиентов, кроме Claude Code: управляемая секция в глобальном файле инструкций клиента
/// (Codex — <c>~/.codex/AGENTS.md</c>, Gemini CLI — <c>~/.gemini/GEMINI.md</c>). Секция ограничена метками
/// <c>&lt;!-- offload:begin x-offload: managed v=… h=… --&gt;</c> … <c>&lt;!-- offload:end --&gt;</c>; текст пользователя вне неё
/// никогда не меняется. Запись — через <see cref="ConfigFile.Edit"/> (резервная копия, атомарно, повтор при гонке).
/// </summary>
public sealed class GuidanceFile
{
    internal const string BeginPrefix = "<!-- offload:begin";
    internal const string EndMarker = "<!-- offload:end -->";

    private readonly Func<string> _path;
    private readonly Func<bool> _installed;

    internal GuidanceFile(string id, string displayName, Func<string> path, Func<bool> installed)
    {
        Id = id;
        DisplayName = displayName;
        _path = path;
        _installed = installed;
    }

    /// <summary>Идентификатор клиента (как у интеграции): codex, gemini-cli.</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>Файл инструкций клиента.</summary>
    public string FilePath => _path();

    /// <summary>Клиент установлен (есть его папка настроек) — только тогда создаём файл.</summary>
    public bool IsClientInstalled()
    {
        try
        {
            return _installed();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Состояние секции: нет / актуальна / устарела / изменена пользователем (или повреждены метки).</summary>
    public ManagedState State()
    {
        try
        {
            var snap = ConfigFile.Read(FilePath);
            if (!snap.Exists) return ManagedState.Missing;
            var span = GuidanceSection.Find(snap.Text);
            if (span is null) return ManagedState.Missing;
            if (span.Value.Error is not null) return ManagedState.Modified;
            return ManagedContent.Inspect(GuidanceSection.SectionText(snap.Text, span.Value), GuidanceSection.Template());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException)
        {
            return ManagedState.Foreign;
        }
    }

    public bool IsInstalled() => State() is ManagedState.Current or ManagedState.Outdated or ManagedState.Modified;

    /// <summary>Добавить (или обновить) секцию. Изменённая пользователем секция заменяется только при явной установке.</summary>
    public IntegrationResult Install()
    {
        if (!IsClientInstalled()) return new IntegrationResult(false, L.F("{0} не найден на этом компьютере.", DisplayName));
        var path = FilePath;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var res = ConfigFile.Edit(path, snap => GuidanceSection.Apply(snap.Text, GuidanceSection.Template()));
            return new IntegrationResult(true,
                res.Outcome == WriteOutcome.Unchanged
                    ? L.F("{0}: инструкции по делегированию уже добавлены ({1}).", DisplayName, path)
                    : L.F("{0}: инструкции по делегированию добавлены в {1}.", DisplayName, path),
                res.BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException or GuidanceSectionException)
        {
            return new IntegrationResult(false, L.F("Не удалось записать файл {0}: {1}", path, ex.Message));
        }
    }

    /// <summary>Убрать секцию; текст пользователя остаётся. Файл, в котором кроме секции ничего не было, удаляется (с копией).</summary>
    public IntegrationResult Remove()
    {
        var path = FilePath;
        try
        {
            var snap = ConfigFile.Read(path);
            if (!snap.Exists || GuidanceSection.Find(snap.Text) is null)
                return new IntegrationResult(true, L.F("{0}: инструкции по делегированию не были добавлены.", DisplayName));
            var remaining = GuidanceSection.Remove(snap.Text);
            if (remaining is not null && remaining.Trim().Length == 0)
            {
                var backup = ConfigFile.Delete(snap);
                return new IntegrationResult(true, L.F("{0}: инструкции по делегированию удалены ({1}).", DisplayName, path), backup);
            }
            var res = ConfigFile.Edit(path, s => GuidanceSection.Remove(s.Text));
            return new IntegrationResult(true, L.F("{0}: инструкции по делегированию удалены ({1}).", DisplayName, path), res.BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigReadException or GuidanceSectionException)
        {
            return new IntegrationResult(false, L.F("Не удалось изменить файл {0}: {1}", path, ex.Message));
        }
    }
}

/// <summary>
/// Инструкции по делегированию в глобальных файлах правил агентов: Codex, Gemini CLI (общий файл с Antigravity), OpenCode,
/// Windsurf, Cline, Roo Code. Cursor не поддерживается: глобальных правил в файле у него нет (только в настройках).
/// </summary>
public static class ClientGuidance
{
    public static IReadOnlyList<GuidanceFile> All { get; } =
    [
        new("codex", "OpenAI Codex", () => System.IO.Path.Combine(ClientLocations.CodexHome, "AGENTS.md"),
            () => Directory.Exists(ClientLocations.CodexHome)),
        new("gemini-cli", "Gemini CLI", () => System.IO.Path.Combine(IntegrationEnvironment.UserProfile, ".gemini", "GEMINI.md"),
            () => Directory.Exists(System.IO.Path.Combine(IntegrationEnvironment.UserProfile, ".gemini"))),
        new("opencode", "OpenCode", () => System.IO.Path.Combine(OpenCodeHome, "AGENTS.md"),
            () => Directory.Exists(OpenCodeHome)),
        // Windsurf читает не больше 6000 символов global_rules.md — секция Offload компактна.
        new("windsurf", "Windsurf", () => System.IO.Path.Combine(WindsurfHome, "memories", "global_rules.md"),
            () => Directory.Exists(WindsurfHome)),
        // Cline и Roo Code читают все файлы каталога правил — у Offload свой файл, чужие правила не затрагиваются.
        new("cline", "Cline", () => System.IO.Path.Combine(ClineHome, "Rules", "offload.md"),
            () => Directory.Exists(ClineHome)),
        new("roo-code", "Roo Code", () => System.IO.Path.Combine(RooHome, "rules", "offload.md"),
            () => Directory.Exists(RooHome)),
    ];

    // Как у остальных правок конфига OpenCode: учитывает XDG_CONFIG_HOME.
    private static string OpenCodeHome => ClientLocations.OpenCodeGlobalDir;

    private static string WindsurfHome => System.IO.Path.Combine(IntegrationEnvironment.UserProfile, ".codeium", "windsurf");

    private static string ClineHome => System.IO.Path.Combine(IntegrationEnvironment.Documents, "Cline");

    private static string RooHome => System.IO.Path.Combine(IntegrationEnvironment.UserProfile, ".roo");

    public static GuidanceFile? Find(string id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>После обновления Offload: переписать устаревшие секции (изменённые пользователем не трогаются).</summary>
    public static IReadOnlyList<IntegrationResult> RefreshAll()
    {
        var results = new List<IntegrationResult>();
        foreach (var g in All)
        {
            try
            {
                if (g.State() == ManagedState.Outdated) results.Add(g.Install());
            }
            catch (Exception ex)
            {
                Log.Warn("Integrations", $"{g.Id}: обновление инструкций: {ex.Message}");
            }
        }
        return results;
    }

    /// <summary>Снять все секции (при удалении программы). Ошибка одной не мешает остальным.</summary>
    public static IReadOnlyList<IntegrationResult> RemoveAll()
    {
        var results = new List<IntegrationResult>();
        foreach (var g in All)
        {
            try
            {
                results.Add(g.Remove());
            }
            catch (Exception ex)
            {
                results.Add(new IntegrationResult(false, L.F("Не удалось изменить файл {0}: {1}", g.Id, ex.Message)));
            }
        }
        return results;
    }
}

internal sealed class GuidanceSectionException(string message) : Exception(message);

/// <summary>Поиск, вставка и удаление управляемой секции в markdown-файле. Чистые функции над текстом (тестируются без файлов).</summary>
internal static class GuidanceSection
{
    internal readonly record struct Span(int Start, int End, string? Error);

    /// <summary>Шаблон секции (метка без версии и хэша; концы строк — \n).</summary>
    public static string Template()
    {
        static string N(string tool) => "`" + tool + "`";
        return $$"""
            {{GuidanceFile.BeginPrefix}} {{ManagedContent.Marker}} -->
            ## Local model delegation (Offload)
            This section is maintained by Offload (Offload -> Integrations). Edit it freely - Offload then stops updating it; delete the whole section including the offload:begin/offload:end lines to opt out.

            If the tools of the `offload` MCP server ({{N(McpToolNames.Status)}}, {{N(McpToolNames.AskFiles)}}, ...) are available in this session, use them to save cloud tokens:
            - To find the code relevant to a task, start with {{N(McpToolNames.FindContext)}} instead of many file reads; navigate with {{N(McpToolNames.Symbols)}} / {{N(McpToolNames.SearchCode)}}; repo overview: {{N(McpToolNames.ProjectMap)}}.
            - Before reading a file over ~300 lines or a log/test output over ~200 lines, prefer {{N(McpToolNames.AskFiles)}} / {{N(McpToolNames.SummarizeLog)}}.
            - To run a build or tests, prefer {{N(McpToolNames.Verify)}}; for structured errors use {{N(McpToolNames.Diagnostics)}}; before/after edits check {{N(McpToolNames.Impact)}}.
            - For a first-pass review of a large diff use {{N(McpToolNames.ReviewDiff)}} (and {{N(McpToolNames.SecurityReview)}} for risky changes); for commit/PR text use {{N(McpToolNames.CommitMessage)}}.
            - For well-specified coding tasks a build/test command can verify, prefer {{N(McpToolNames.Solve)}} or {{N(McpToolNames.AgentTask)}} (isolated git sandbox, merged back only if checks pass); for your own small diffs {{N(McpToolNames.ApplyPatch)}}, for renames {{N(McpToolNames.Refactor)}}, for single new files or edits of listed files {{N(McpToolNames.WriteFile)}} / {{N(McpToolNames.EditFiles)}}.
            - Brief the local model like it sees nothing: paths, acceptance criteria, a verify command; one task per call. Its results are drafts - check the returned proof, not every line.
            - Keep architecture, debugging, security-sensitive code and final review for yourself.
            If those tools are not available, ignore this section.
            {{GuidanceFile.EndMarker}}
            """.ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Границы секции (от начала строки begin до конца строки end, включая перевод строки); null — секции нет.</summary>
    public static Span? Find(string text)
    {
        var begin = text.IndexOf(GuidanceFile.BeginPrefix, StringComparison.Ordinal);
        if (begin < 0) return null;
        if (text.IndexOf(GuidanceFile.BeginPrefix, begin + GuidanceFile.BeginPrefix.Length, StringComparison.Ordinal) >= 0)
            return new Span(begin, begin, L.T("в файле несколько секций Offload — исправьте файл вручную"));
        var end = text.IndexOf(GuidanceFile.EndMarker, begin, StringComparison.Ordinal);
        if (end < 0) return new Span(begin, begin, L.T("у секции Offload нет закрывающей метки offload:end — исправьте файл вручную"));
        var lineStart = begin == 0 ? 0 : text.LastIndexOf('\n', begin - 1) + 1;
        if (text[lineStart..begin].Trim().Length > 0)
            return new Span(begin, begin, L.T("метка offload:begin не в начале строки — исправьте файл вручную"));
        var stop = end + GuidanceFile.EndMarker.Length;
        if (stop < text.Length && text[stop] == '\r') stop++;
        if (stop < text.Length && text[stop] == '\n') stop++;
        return new Span(lineStart, stop, null);
    }

    /// <summary>Текст секции для сравнения с шаблоном (секция в конце файла без перевода строки получает его).</summary>
    public static string SectionText(string text, Span span) => text[span.Start..span.End].TrimEnd('\r', '\n') + "\n";

    /// <summary>Текст с актуальной секцией или null, если менять нечего. Концы строк секции — как в файле.</summary>
    public static string? Apply(string text, string template)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var section = ManagedContent.Stamp(template).ReplaceLineEndings(nl);
        var span = Find(text);
        if (span is { Error: { } error }) throw new GuidanceSectionException(error);
        if (span is { } s)
        {
            if (ManagedContent.Inspect(SectionText(text, s), template) == ManagedState.Current) return null;
            var tail = text[s.End..];
            // Секция была последней строкой без перевода строки — не добавляем его.
            if (s.End == text.Length && !text.EndsWith('\n')) section = section.TrimEnd('\r', '\n');
            return text[..s.Start] + section + tail;
        }
        if (text.Length == 0) return section;
        var prefix = text;
        if (!prefix.EndsWith('\n')) prefix += nl;
        if (!prefix.EndsWith(nl + nl, StringComparison.Ordinal)) prefix += nl;
        return prefix + section;
    }

    /// <summary>Текст без секции (null — секции нет). Пустые строки, которыми секция отделялась в конце файла, тоже убираются.</summary>
    public static string? Remove(string text)
    {
        var span = Find(text);
        if (span is null) return null;
        if (span.Value.Error is { } error) throw new GuidanceSectionException(error);
        var before = text[..span.Value.Start];
        var after = text[span.Value.End..];
        if (after.Trim().Length > 0) return before + after;
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trimmed = before.TrimEnd('\r', '\n');
        return trimmed.Length == 0 ? "" : trimmed + nl;
    }
}
