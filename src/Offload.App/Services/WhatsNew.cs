using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Offload.Core.Update;

namespace Offload.App.Services;

/// <summary>
/// «Что нового» этой версии: раздел заметок к выпуску (.github/release-notes.md, встроен в exe) на языке интерфейса —
/// «## Что нового в X» по-русски и абзац «**New in X:**» по-английски. Показ после обновления — раз на версию
/// (<see cref="Offload.Core.Config.UiSettings.LastRunVersion"/>).
/// </summary>
internal static partial class WhatsNew
{
    private const string ResourceName = "Offload.App.release-notes.md";

    /// <summary>Встроенные заметки к выпуску (null — ресурса нет).</summary>
    internal static string? Embedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (s is null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Текст «что нового» текущей версии на языке интерфейса или null, если в заметках его нет.</summary>
    public static string? ForCurrentVersion() =>
        Embedded() is { } md ? Extract(md, Offload.Core.AppInfo.Version, L.IsEnglish) : null;

    /// <summary>
    /// Раздел версии <paramref name="version"/> обычным текстом: пункты списка — строками «• …», разметка Markdown
    /// (жирный, код, ссылки) убрана. null — раздела нет.
    /// </summary>
    internal static string? Extract(string markdown, string version, bool english)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        return english ? ExtractEnglish(lines, version) : ExtractRussian(lines, version);
    }

    private static string? ExtractRussian(string[] lines, string version)
    {
        var start = Array.FindIndex(lines, l => l.Trim().Equals("## Что нового в " + version, StringComparison.OrdinalIgnoreCase)); // l10n-ignore — заголовок в заметках к выпуску
        if (start < 0) return null;
        var items = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.Trim() == "---") break;
            if (line.StartsWith("- ", StringComparison.Ordinal)) items.Add(Plain(line[2..]));
            else if (line.StartsWith("  ", StringComparison.Ordinal) && items.Count > 0 && line.Trim().Length > 0) items[^1] += " " + Plain(line.Trim());
        }
        return items.Count == 0 ? null : string.Join("\n", items.Select(x => "• " + x));
    }

    private static string? ExtractEnglish(string[] lines, string version)
    {
        var marker = "**New in " + version + ":**";
        var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith(marker, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var sb = new StringBuilder(lines[start].Trim()[marker.Length..].Trim());
        for (var i = start + 1; i < lines.Length && lines[i].Trim().Length > 0; i++) sb.Append(' ').Append(lines[i].Trim());
        var text = Plain(sb.ToString());
        return text.Length == 0 ? null : text;
    }

    /// <summary>Убрать разметку: **жирный**, `код`, [текст](ссылка).</summary>
    internal static string Plain(string s)
    {
        s = LinkRegex().Replace(s, "$1");
        return s.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// Запуск после обновления: предыдущая версия, если эта новее запомненной (иначе null). Первый запуск вообще
    /// (версии нет) обновлением не считается.
    /// </summary>
    internal static string? UpdatedFrom(string? lastRun, string current) =>
        lastRun is not null && AppReleases.CompareVersions(current, lastRun) > 0 ? lastRun : null;

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();
}
