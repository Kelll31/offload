namespace Offload.Core.Util;

/// <summary>
/// Нечёткий поиск для палитры команд: все символы запроса по порядку должны встретиться в тексте (без учёта регистра
/// и «ё/е»). Очки выше за совпадение с начала слова, подряд идущие символы и совпадение с начала строки; несколько слов
/// запроса ищутся независимо (все должны найтись).
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Оценка совпадения (больше — лучше) или null, если текст не подходит. Пустой запрос подходит всему (0).</summary>
    public static int? Score(string query, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        var t = Normalize(text);
        var total = 0;
        foreach (var w in words)
        {
            if (WordScore(w, t) is not { } s) return null;
            total += s;
        }
        // Короткие совпадения чуть выше длинных: «Журнал» раньше «Открыть журнал в Блокноте».
        return total - t.Length / 8;
    }

    /// <summary>Лучшие совпадения по нескольким полям элемента (заголовок, ключевые слова), по убыванию оценки.</summary>
    public static IReadOnlyList<T> Rank<T>(string query, IEnumerable<T> items, Func<T, IEnumerable<string>> fields, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fields);
        return items
            .Select((item, index) => (item, index, score: fields(item).Select(f => Score(query, f ?? "")).Max()))
            .Where(x => x.score is not null)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(limit)
            .Select(x => x.item)
            .ToList();
    }

    private static int? WordScore(string w, string t)
    {
        // Точное вхождение подстроки — лучший случай.
        var at = t.IndexOf(w, StringComparison.Ordinal);
        if (at >= 0)
        {
            var s = 100 + w.Length * 10;
            if (at == 0) s += 60;
            else if (!char.IsLetterOrDigit(t[at - 1])) s += 40;
            return s;
        }
        // Иначе — символы по порядку (подпоследовательность).
        var score = 0;
        var ti = 0;
        var prev = -2;
        // Поиск продолжается с позиции прошлого совпадения — состояние между шагами, поэтому цикл, а не Select.
        for (var k = 0; k < w.Length; k++)
        {
            var found = t.IndexOf(w[k], ti);
            if (found < 0) return null;
            score += 5;
            if (found == prev + 1) score += 8;
            if (found == 0 || !char.IsLetterOrDigit(t[found - 1])) score += 12;
            prev = found;
            ti = found + 1;
        }
        return score;
    }

    private static string Normalize(string s) => s.Trim().ToLowerInvariant().Replace('ё', 'е');
}
