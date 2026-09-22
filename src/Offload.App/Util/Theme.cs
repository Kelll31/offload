using Offload.Llama;

namespace Offload.App.Util;

/// <summary>Цвета и шрифты интерфейса (светлая тема в духе Windows 11).</summary>
internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(0, 95, 184);
    public static readonly Color AccentLight = Color.FromArgb(229, 241, 251);

    public static readonly Color Green = Color.FromArgb(16, 137, 62);
    public static readonly Color Amber = Color.FromArgb(234, 160, 0);
    public static readonly Color Red = Color.FromArgb(209, 52, 56);
    public static readonly Color Gray = Color.FromArgb(138, 136, 134);

    /// <summary>Читаемый на белом «жёлтый» для текста предупреждений.</summary>
    public static readonly Color WarnText = Color.FromArgb(157, 93, 0);
    public static readonly Color ErrorText = Color.FromArgb(164, 38, 44);
    public static readonly Color OkText = Color.FromArgb(16, 124, 16);

    public static readonly Color TextPrimary = Color.FromArgb(27, 27, 27);
    public static readonly Color TextMuted = Color.FromArgb(96, 94, 92);
    public static readonly Color Border = Color.FromArgb(222, 222, 222);
    public static readonly Color Surface = SystemColors.Window;
    public static readonly Color SurfaceAlt = Color.FromArgb(248, 248, 248);
    public static readonly Color Sidebar = Color.FromArgb(243, 246, 250);

    private static readonly string SemiboldFamily = FontExists("Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";
    private static readonly string MonoFamily = FontExists("Cascadia Mono") ? "Cascadia Mono" : "Consolas";

    private static readonly Dictionary<string, Font> Cache = new();

    public static Font Regular(float sizePt = 9f) => Get("Segoe UI", sizePt, FontStyle.Regular);

    public static Font Semibold(float sizePt = 9f) =>
        SemiboldFamily == "Segoe UI" ? Get("Segoe UI", sizePt, FontStyle.Bold) : Get(SemiboldFamily, sizePt, FontStyle.Regular);

    public static Font Bold(float sizePt = 9f) => Get("Segoe UI", sizePt, FontStyle.Bold);

    public static Font Mono(float sizePt = 9f) => Get(MonoFamily, sizePt, FontStyle.Regular);

    /// <summary>Цвет индикатора состояния сервера.</summary>
    public static Color StateColor(ServerState state) => state switch
    {
        ServerState.Running => Green,
        ServerState.Starting or ServerState.Stopping => Amber,
        ServerState.Failed => Red,
        _ => Gray,
    };

    private static Font Get(string family, float size, FontStyle style)
    {
        var key = $"{family}|{size}|{style}";
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var f))
            {
                f = new Font(family, size, style, GraphicsUnit.Point);
                Cache[key] = f;
            }
            return f;
        }
    }

    private static bool FontExists(string family)
    {
        try
        {
            using var f = new Font(family, 9f);
            return string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
