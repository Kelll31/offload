using Offload.Core.Config;

namespace Offload.App.Services;

/// <summary>
/// Сохранение и безопасное восстановление положения окна: окно не должно оказаться за пределами экрана после отключения
/// монитора, смены разрешения или масштаба.
/// </summary>
internal static class WindowPlacements
{
    private const int LogicalDpi = 96;

    /// <summary>Снимок положения: угол — пиксели экрана, размер — логические пиксели.</summary>
    public static WindowPlacement Capture(Rectangle normalBounds, bool maximized, int dpi)
    {
        dpi = dpi > 0 ? dpi : LogicalDpi;
        return new WindowPlacement
        {
            X = normalBounds.X,
            Y = normalBounds.Y,
            Width = Scale(normalBounds.Width, LogicalDpi, dpi),
            Height = Scale(normalBounds.Height, LogicalDpi, dpi),
            Maximized = maximized,
        };
    }

    /// <summary>
    /// Где показать окно (пиксели экрана) или null — положение неизвестно/некорректно или монитора больше нет
    /// (тогда окно открывается по центру с размером по умолчанию).
    /// </summary>
    /// <param name="saved">Сохранённое положение.</param>
    /// <param name="workingAreas">Рабочие области мониторов (без панели задач).</param>
    /// <param name="dpiAt">DPI монитора в точке экрана (null — неизвестен).</param>
    /// <param name="fallbackDpi">DPI, если монитор его не сообщил.</param>
    /// <param name="minimumLogical">Минимальный размер окна в логических пикселях.</param>
    public static Rectangle? Restore(WindowPlacement? saved, IReadOnlyList<Rectangle> workingAreas, Func<Point, int?> dpiAt,
        int fallbackDpi, Size minimumLogical)
    {
        if (saved is null || saved.Width <= 0 || saved.Height <= 0 || workingAreas.Count == 0) return null;

        // Монитор, на котором окно было: по центру заголовка (его пользователь должен видеть, чтобы перетащить окно).
        var anchor = new Point(saved.X + Math.Min(saved.Width, 200) / 2, saved.Y + 10);
        var area = workingAreas.FirstOrDefault(a => a.Contains(anchor));
        if (area.IsEmpty)
        {
            // Угол вне экранов — берём монитор с наибольшим пересечением; нет пересечения — монитора больше нет.
            var rect = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
            var best = workingAreas
                .Select(a => (Area: a, Overlap: Rectangle.Intersect(a, rect)))
                .Where(x => !x.Overlap.IsEmpty)
                .OrderByDescending(x => (long)x.Overlap.Width * x.Overlap.Height)
                .FirstOrDefault();
            if (best.Area.IsEmpty) return null;
            area = best.Area;
        }

        var dpi = dpiAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2)) ?? fallbackDpi;
        if (dpi <= 0) dpi = LogicalDpi;
        var width = Scale(Math.Max(saved.Width, minimumLogical.Width), dpi, LogicalDpi);
        var height = Scale(Math.Max(saved.Height, minimumLogical.Height), dpi, LogicalDpi);
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        var x = Math.Clamp(saved.X, area.Left, area.Right - width);
        var y = Math.Clamp(saved.Y, area.Top, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    /// <summary>Совпадают ли положения (чтобы не переписывать config.json без изменений).</summary>
    public static bool Same(WindowPlacement? a, WindowPlacement? b) =>
        a is not null && b is not null &&
        (a.X, a.Y, a.Width, a.Height, a.Maximized) == (b.X, b.Y, b.Width, b.Height, b.Maximized);

    private static int Scale(int value, int to, int from) => (int)Math.Round(value * (double)to / from);
}
