using System.Text;

namespace Offload.Mcp.Infrastructure;

internal enum DiffOp { Equal, Delete, Insert }

internal readonly record struct DiffEdit(DiffOp Op, int OldIndex, int NewIndex);

/// <summary>
/// Построчный diff: алгоритм Майерса O((N+M)·D) после отсечения общего начала и конца.
/// При очень большом числе различий — упрощённый результат (замена всей середины), он корректен, но не минимален.
/// </summary>
internal static class LineDiff
{
    private const int MaxD = 4000;

    public static List<DiffEdit> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var edits = new List<DiffEdit>(a.Count + b.Count);
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix]) suffix++;

        for (var i = 0; i < prefix; i++) edits.Add(new(DiffOp.Equal, i, i));
        Middle(a, b, prefix, a.Count - suffix, prefix, b.Count - suffix, edits);
        for (var i = 0; i < suffix; i++) edits.Add(new(DiffOp.Equal, a.Count - suffix + i, b.Count - suffix + i));
        return edits;
    }

    private static void Middle(IReadOnlyList<string> a, IReadOnlyList<string> b, int aStart, int aEnd, int bStart, int bEnd, List<DiffEdit> edits)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        if (n == 0 && m == 0) return;
        if (n == 0)
        {
            for (var j = bStart; j < bEnd; j++) edits.Add(new(DiffOp.Insert, aStart, j));
            return;
        }
        if (m == 0)
        {
            for (var i = aStart; i < aEnd; i++) edits.Add(new(DiffOp.Delete, i, bStart));
            return;
        }

        var max = n + m;
        var offset = max;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        var found = -1;
        // Память trace = D × (2(N+M)+2) целых — ограничиваем ~80 МБ.
        var maxD = (int)Math.Min(Math.Min(max, MaxD), 20_000_000L / v.Length);
        for (var d = 0; d <= maxD; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                else x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
            if (found >= 0) break;
        }

        if (found < 0)
        {
            // Слишком много различий: удаляем всё старое, вставляем всё новое.
            for (var i = aStart; i < aEnd; i++) edits.Add(new(DiffOp.Delete, i, bStart));
            for (var j = bStart; j < bEnd; j++) edits.Add(new(DiffOp.Insert, aEnd, j));
            return;
        }

        // Обратный проход по сохранённым V.
        var back = new List<DiffEdit>();
        int cx = n, cy = m;
        for (var d = found; d > 0; d--)
        {
            var vd = trace[d];
            var k = cx - cy;
            int prevK;
            if (k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1])) prevK = k + 1;
            else prevK = k - 1;
            var prevX = vd[offset + prevK];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                cx--;
                cy--;
                back.Add(new(DiffOp.Equal, aStart + cx, bStart + cy));
            }
            if (cx == prevX) back.Add(new(DiffOp.Insert, aStart + cx, bStart + prevY));
            else back.Add(new(DiffOp.Delete, aStart + prevX, bStart + cy));
            cx = prevX;
            cy = prevY;
        }
        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            back.Add(new(DiffOp.Equal, aStart + cx, bStart + cy));
        }
        back.Reverse();
        edits.AddRange(back);
    }

    public static (int Added, int Removed) Stats(List<DiffEdit> edits) =>
        (edits.Count(e => e.Op == DiffOp.Insert), edits.Count(e => e.Op == DiffOp.Delete));

    public static (int Added, int Removed) Stats(IReadOnlyList<string> a, IReadOnlyList<string> b) => Stats(Compute(a, b));

    /// <summary>
    /// Unified diff (как «diff -u»): «--- a/path», «+++ b/path», ханки «@@ -l,s +l,s @@» с context строками контекста.
    /// Пусто, если различий нет.
    /// </summary>
    public static string Unified(IReadOnlyList<string> a, IReadOnlyList<string> b, string oldName, string newName, int context = 3)
    {
        var edits = Compute(a, b);
        if (edits.All(e => e.Op == DiffOp.Equal)) return "";
        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldName).Append('\n');
        sb.Append("+++ ").Append(newName).Append('\n');

        var i = 0;
        while (i < edits.Count)
        {
            // Начало очередного изменения.
            while (i < edits.Count && edits[i].Op == DiffOp.Equal) i++;
            if (i >= edits.Count) break;
            var start = Math.Max(0, i - context);
            // Конец ханка: изменения, разделённые не более чем 2*context равными строками, объединяются.
            var end = i;
            var lastChange = i;
            while (end < edits.Count)
            {
                if (edits[end].Op != DiffOp.Equal) lastChange = end;
                else if (end - lastChange > 2 * context) break;
                end++;
            }
            var stop = Math.Min(edits.Count, lastChange + context + 1);

            int oldStart = -1, newStart = -1, oldCount = 0, newCount = 0;
            var body = new StringBuilder();
            for (var j = start; j < stop; j++)
            {
                var e = edits[j];
                switch (e.Op)
                {
                    case DiffOp.Equal:
                        if (oldStart < 0) oldStart = e.OldIndex;
                        if (newStart < 0) newStart = e.NewIndex;
                        oldCount++;
                        newCount++;
                        body.Append(' ').Append(a[e.OldIndex]).Append('\n');
                        break;
                    case DiffOp.Delete:
                        if (oldStart < 0) oldStart = e.OldIndex;
                        if (newStart < 0) newStart = e.NewIndex;
                        oldCount++;
                        body.Append('-').Append(a[e.OldIndex]).Append('\n');
                        break;
                    case DiffOp.Insert:
                        if (oldStart < 0) oldStart = e.OldIndex;
                        if (newStart < 0) newStart = e.NewIndex;
                        newCount++;
                        body.Append('+').Append(b[e.NewIndex]).Append('\n');
                        break;
                }
            }
            sb.Append("@@ -").Append(Range(oldStart, oldCount)).Append(" +").Append(Range(newStart, newCount)).Append(" @@\n");
            sb.Append(body);
            i = stop;
        }
        return sb.ToString();
    }

    /// <summary>Диапазон GNU diff: при пустом диапазоне номер — строка перед ним; «,1» опускается.</summary>
    private static string Range(int start0, int count)
    {
        if (count == 0) return $"{start0},0";
        if (count == 1) return $"{start0 + 1}";
        return $"{start0 + 1},{count}";
    }
}
