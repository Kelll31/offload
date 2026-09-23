using Microsoft.Win32;
using Offload.Llama;

namespace Offload.App.Util;

/// <summary>
/// Цвета и шрифты интерфейса в духе Windows 11: светлая и тёмная палитры.
/// Тема выбирается при старте (<see cref="Initialize"/>) и при смене в настройках; уже созданные окна
/// пересоздаются (цвета элементов задаются при их создании).
/// </summary>
internal static class Theme
{
    public const string ModeSystem = "system";
    public const string ModeLight = "light";
    public const string ModeDark = "dark";

    private static Palette _p = Palette.Light;

    /// <summary>Выбранный режим (system / light / dark).</summary>
    public static string Mode { get; private set; } = ModeSystem;

    public static bool IsDark { get; private set; }

    // ---- Цветовые схемы ----

    public const string PresetDefault = "default";
    public const string PresetCustom = "custom";

    /// <summary>Готовая цветовая схема: акцент для светлой и тёмной основы; Midnight/Contrast — своя тёмная основа.</summary>
    public sealed record PresetInfo(string Key, string Name, Color LightAccent, Color DarkAccent, bool ForcesDark = false);

    /// <summary>Готовые схемы (Name — русский текст, в интерфейсе через L.T).</summary>
    public static readonly IReadOnlyList<PresetInfo> Presets =
    [
        new(PresetDefault, "Синяя (Windows)", Color.FromArgb(0, 95, 184), Color.FromArgb(96, 205, 255)), // l10n-key
        new("teal", "Бирюзовая", Color.FromArgb(0, 120, 124), Color.FromArgb(76, 194, 180)), // l10n-key
        new("green", "Зелёная", Color.FromArgb(16, 124, 16), Color.FromArgb(108, 203, 95)), // l10n-key
        new("purple", "Фиолетовая", Color.FromArgb(118, 76, 178), Color.FromArgb(180, 150, 255)), // l10n-key
        new("orange", "Оранжевая", Color.FromArgb(188, 72, 12), Color.FromArgb(255, 150, 90)), // l10n-key
        new("pink", "Розовая", Color.FromArgb(180, 44, 140), Color.FromArgb(240, 120, 220)), // l10n-key
        new("midnight", "Полночь (тёмная)", Color.FromArgb(122, 162, 255), Color.FromArgb(122, 162, 255), ForcesDark: true), // l10n-key
        new("contrast", "Высокий контраст (тёмная)", Color.FromArgb(255, 214, 0), Color.FromArgb(255, 214, 0), ForcesDark: true), // l10n-key
    ];

    /// <summary>Выбранная схема (ключ из <see cref="Presets"/> или custom).</summary>
    public static string Preset { get; private set; } = PresetDefault;

    /// <summary>Применить режим темы (и схему/свой акцент). Возвращает true, если тёмная.</summary>
    public static bool Initialize(string? mode, string? preset = null, string? accent = null)
    {
        Mode = mode is ModeLight or ModeDark ? mode : ModeSystem;
        Preset = preset == PresetCustom || Presets.Any(p => p.Key == preset) ? preset! : PresetDefault;
        var info = Presets.FirstOrDefault(p => p.Key == Preset);
        IsDark = info is { ForcesDark: true } || Mode == ModeDark || (Mode == ModeSystem && SystemPrefersDark());
        var palette = Preset switch
        {
            "midnight" => Palette.Midnight,
            "contrast" => Palette.Contrast,
            _ => IsDark ? Palette.Dark : Palette.Light,
        };
        if (Preset == PresetCustom && TryParseColor(accent, out var custom)) palette = WithAccent(palette, custom, IsDark);
        else if (info is not null && Preset != PresetDefault && !info.ForcesDark) palette = WithAccent(palette, IsDark ? info.DarkAccent : info.LightAccent, IsDark);
        _p = palette;
        return IsDark;
    }

    /// <summary>Будет ли тема тёмной при этих настройках (без применения).</summary>
    public static bool WouldBeDark(string? mode, string? preset) =>
        Presets.FirstOrDefault(p => p.Key == preset) is { ForcesDark: true } || mode == ModeDark || (mode is not ModeLight && SystemPrefersDark());

    /// <summary>«#RRGGBB» → цвет.</summary>
    public static bool TryParseColor(string? text, out Color color)
    {
        color = Color.Empty;
        var t = text?.Trim().TrimStart('#');
        if (t is null || t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
        color = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static double ColorDistance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    /// <summary>Палитра с другим акцентом: оттенки наведения/нажатия, подложка и контрастный текст на акценте — из него.</summary>
    private static Palette WithAccent(Palette p, Color accent, bool dark)
    {
        var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        return p with
        {
            Accent = accent,
            AccentHover = Blend(accent, Color.Black, dark ? 0.10 : 0.12),
            AccentPressed = Blend(accent, Color.Black, dark ? 0.20 : 0.25),
            OnAccent = luminance > 0.6 ? Color.Black : Color.White,
            AccentLight = Blend(p.Background, accent, dark ? 0.25 : 0.12),
            // Акцент — первая серия графиков; похожие на него цвета убираем, чтобы две серии не совпали.
            Series = [accent, .. p.Series.Skip(1).Where(c => ColorDistance(c, accent) > 60), .. p.Series.Skip(1).Where(c => ColorDistance(c, accent) <= 60).Select(c => Blend(c, dark ? Color.White : Color.Black, 0.35))],
        };
    }

    /// <summary>Тёмная тема приложений в параметрах Windows.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- Палитра ----

    public static Color Accent => _p.Accent;
    /// <summary>Акцент при наведении / нажатии (кнопки).</summary>
    public static Color AccentHover => _p.AccentHover;
    public static Color AccentPressed => _p.AccentPressed;
    /// <summary>Текст на акцентном фоне.</summary>
    public static Color OnAccent => _p.OnAccent;
    /// <summary>Мягкая подложка акцентного цвета (выделение, подсветка).</summary>
    public static Color AccentLight => _p.AccentLight;

    public static Color Green => _p.Green;
    public static Color Amber => _p.Amber;
    public static Color Red => _p.Red;
    public static Color Gray => _p.Gray;

    /// <summary>Читаемый на фоне «жёлтый» для текста предупреждений.</summary>
    public static Color WarnText => _p.WarnText;
    public static Color ErrorText => _p.ErrorText;
    public static Color OkText => _p.OkText;

    public static Color TextPrimary => _p.TextPrimary;
    public static Color TextMuted => _p.TextMuted;
    /// <summary>Ещё тише, чем TextMuted: подписи осей, неактивные элементы.</summary>
    public static Color TextFaint => _p.TextFaint;
    public static Color Border => _p.Border;

    /// <summary>Фон окна и страниц.</summary>
    public static Color Surface => _p.Background;
    /// <summary>Фон карточек и плиток.</summary>
    public static Color SurfaceAlt => _p.Card;
    public static Color Card => _p.Card;
    /// <summary>Фон карточки при наведении.</summary>
    public static Color CardHover => _p.CardHover;
    /// <summary>Фон боковой панели.</summary>
    public static Color Sidebar => _p.Background;
    /// <summary>Выделенный пункт навигации.</summary>
    public static Color NavSelected => _p.NavSelected;
    public static Color NavHover => _p.NavHover;
    /// <summary>Поля ввода, списки, журнал.</summary>
    public static Color Input => _p.Input;
    /// <summary>Дорожка индикаторов (пустая часть полосы).</summary>
    public static Color Track => _p.Track;

    /// <summary>Всплывающая подсказка графиков.</summary>
    public static Color TooltipBack => IsDark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(40, 40, 40);
    public static Color TooltipText => Color.White;

    /// <summary>Цвета серий графиков (по порядку).</summary>
    public static Color Series(int i) => _p.Series[Math.Abs(i) % _p.Series.Length];

    /// <summary>Цвет индикатора состояния сервера.</summary>
    public static Color StateColor(ServerState state) => state switch
    {
        ServerState.Running => Green,
        ServerState.Starting or ServerState.Stopping => Amber,
        ServerState.Failed => Red,
        _ => Gray,
    };

    /// <summary>Цвет заполнения по доле: зелёный → жёлтый → красный.</summary>
    public static Color LoadColor(double fraction) => fraction >= 0.95 ? Red : fraction >= 0.85 ? Amber : Accent;

    /// <summary>Смешать цвет с другим (amount 0…1 — доля второго).</summary>
    public static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * amount),
            (int)Math.Round(a.G + (b.G - a.G) * amount),
            (int)Math.Round(a.B + (b.B - a.B) * amount));
    }

    // ---- Шрифты ----

    private static readonly string SemiboldFamily = FontExists("Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";
    private static readonly string MonoFamily = FontExists("Cascadia Mono") ? "Cascadia Mono" : "Consolas";
    private static readonly string IconFamily = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    private static readonly Dictionary<string, Font> Cache = new();

    public static Font Regular(float sizePt = 9f) => Get("Segoe UI", sizePt, FontStyle.Regular);

    public static Font Semibold(float sizePt = 9f) =>
        SemiboldFamily == "Segoe UI" ? Get("Segoe UI", sizePt, FontStyle.Bold) : Get(SemiboldFamily, sizePt, FontStyle.Regular);

    public static Font Bold(float sizePt = 9f) => Get("Segoe UI", sizePt, FontStyle.Bold);

    public static Font Mono(float sizePt = 9f) => Get(MonoFamily, sizePt, FontStyle.Regular);

    /// <summary>Шрифт значков (Segoe Fluent Icons в Windows 11, Segoe MDL2 Assets в Windows 10).</summary>
    public static Font Icons(float sizePt = 11f) => Get(IconFamily, sizePt, FontStyle.Regular);

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

    // ---- Заголовок окна ----

    /// <summary>Тёмный заголовок и цвет рамки окна под тему (Windows 10 20H1+ / Windows 11; на старых — без эффекта).</summary>
    public static void ApplyWindowFrame(Form form)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            var dark = IsDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            var caption = ColorTranslator.ToWin32(Surface);
            NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }
        catch
        {
            // Нет DWM-атрибута в этой версии Windows — оставляем системный вид.
        }
    }

    private sealed record Palette(
        Color Accent, Color AccentHover, Color AccentPressed, Color OnAccent, Color AccentLight,
        Color Green, Color Amber, Color Red, Color Gray,
        Color WarnText, Color ErrorText, Color OkText,
        Color TextPrimary, Color TextMuted, Color TextFaint, Color Border,
        Color Background, Color Card, Color CardHover, Color NavSelected, Color NavHover, Color Input, Color Track,
        Color[] Series)
    {
        public static readonly Palette Light = new(
            Accent: Color.FromArgb(0, 95, 184),
            AccentHover: Color.FromArgb(0, 84, 166),
            AccentPressed: Color.FromArgb(0, 70, 140),
            OnAccent: Color.White,
            AccentLight: Color.FromArgb(229, 241, 251),
            Green: Color.FromArgb(16, 137, 62),
            Amber: Color.FromArgb(234, 160, 0),
            Red: Color.FromArgb(209, 52, 56),
            Gray: Color.FromArgb(138, 136, 134),
            WarnText: Color.FromArgb(157, 93, 0),
            ErrorText: Color.FromArgb(164, 38, 44),
            OkText: Color.FromArgb(16, 124, 16),
            TextPrimary: Color.FromArgb(27, 27, 27),
            TextMuted: Color.FromArgb(96, 94, 92),
            TextFaint: Color.FromArgb(138, 136, 134),
            Border: Color.FromArgb(229, 229, 229),
            Background: Color.FromArgb(243, 243, 243),
            Card: Color.FromArgb(251, 251, 251),
            CardHover: Color.FromArgb(246, 246, 246),
            NavSelected: Color.FromArgb(233, 233, 233),
            NavHover: Color.FromArgb(237, 237, 237),
            Input: Color.White,
            Track: Color.FromArgb(225, 225, 225),
            Series:
            [
                Color.FromArgb(0, 95, 184), Color.FromArgb(0, 153, 188), Color.FromArgb(135, 100, 184),
                Color.FromArgb(202, 80, 16), Color.FromArgb(16, 137, 62), Color.FromArgb(194, 57, 179),
                Color.FromArgb(234, 160, 0), Color.FromArgb(96, 94, 92),
            ]);

        /// <summary>«Полночь»: тёмно-синяя основа.</summary>
        public static readonly Palette Midnight = new(
            Accent: Color.FromArgb(122, 162, 255),
            AccentHover: Color.FromArgb(108, 146, 235),
            AccentPressed: Color.FromArgb(94, 128, 210),
            OnAccent: Color.Black,
            AccentLight: Color.FromArgb(36, 48, 82),
            Green: Color.FromArgb(108, 203, 95),
            Amber: Color.FromArgb(252, 206, 0),
            Red: Color.FromArgb(255, 110, 120),
            Gray: Color.FromArgb(128, 138, 160),
            WarnText: Color.FromArgb(252, 225, 0),
            ErrorText: Color.FromArgb(255, 153, 164),
            OkText: Color.FromArgb(108, 203, 95),
            TextPrimary: Color.FromArgb(232, 236, 248),
            TextMuted: Color.FromArgb(176, 186, 210),
            TextFaint: Color.FromArgb(128, 138, 166),
            Border: Color.FromArgb(44, 52, 76),
            Background: Color.FromArgb(15, 19, 32),
            Card: Color.FromArgb(23, 29, 46),
            CardHover: Color.FromArgb(29, 36, 56),
            NavSelected: Color.FromArgb(32, 40, 62),
            NavHover: Color.FromArgb(26, 33, 51),
            Input: Color.FromArgb(11, 15, 26),
            Track: Color.FromArgb(44, 52, 76),
            Series:
            [
                Color.FromArgb(122, 162, 255), Color.FromArgb(76, 194, 180), Color.FromArgb(180, 150, 255),
                Color.FromArgb(255, 150, 90), Color.FromArgb(108, 203, 95), Color.FromArgb(240, 120, 220),
                Color.FromArgb(252, 206, 0), Color.FromArgb(160, 170, 190),
            ]);

        /// <summary>Высокий контраст: чёрный фон, белый текст, жёлтый акцент, заметные границы.</summary>
        public static readonly Palette Contrast = new(
            Accent: Color.FromArgb(255, 214, 0),
            AccentHover: Color.FromArgb(255, 230, 90),
            AccentPressed: Color.FromArgb(220, 180, 0),
            OnAccent: Color.Black,
            AccentLight: Color.FromArgb(64, 56, 0),
            Green: Color.FromArgb(0, 255, 120),
            Amber: Color.FromArgb(255, 214, 0),
            Red: Color.FromArgb(255, 80, 80),
            Gray: Color.FromArgb(190, 190, 190),
            WarnText: Color.FromArgb(255, 230, 0),
            ErrorText: Color.FromArgb(255, 120, 120),
            OkText: Color.FromArgb(0, 255, 120),
            TextPrimary: Color.White,
            TextMuted: Color.FromArgb(230, 230, 230),
            TextFaint: Color.FromArgb(200, 200, 200),
            Border: Color.FromArgb(170, 170, 170),
            Background: Color.Black,
            Card: Color.FromArgb(12, 12, 12),
            CardHover: Color.FromArgb(30, 30, 30),
            NavSelected: Color.FromArgb(48, 48, 48),
            NavHover: Color.FromArgb(30, 30, 30),
            Input: Color.Black,
            Track: Color.FromArgb(90, 90, 90),
            Series:
            [
                Color.FromArgb(255, 214, 0), Color.FromArgb(0, 230, 255), Color.FromArgb(255, 120, 255),
                Color.FromArgb(255, 150, 60), Color.FromArgb(0, 255, 120), Color.FromArgb(160, 160, 255),
                Color.FromArgb(255, 255, 255), Color.FromArgb(190, 190, 190),
            ]);

        public static readonly Palette Dark = new(
            Accent: Color.FromArgb(96, 205, 255),
            AccentHover: Color.FromArgb(87, 187, 232),
            AccentPressed: Color.FromArgb(78, 168, 209),
            OnAccent: Color.Black,
            AccentLight: Color.FromArgb(38, 58, 72),
            Green: Color.FromArgb(108, 203, 95),
            Amber: Color.FromArgb(252, 206, 0),
            Red: Color.FromArgb(255, 110, 120),
            Gray: Color.FromArgb(140, 140, 140),
            WarnText: Color.FromArgb(252, 225, 0),
            ErrorText: Color.FromArgb(255, 153, 164),
            OkText: Color.FromArgb(108, 203, 95),
            TextPrimary: Color.FromArgb(242, 242, 242),
            TextMuted: Color.FromArgb(200, 200, 200),
            TextFaint: Color.FromArgb(150, 150, 150),
            Border: Color.FromArgb(60, 60, 60),
            Background: Color.FromArgb(32, 32, 32),
            Card: Color.FromArgb(43, 43, 43),
            CardHover: Color.FromArgb(50, 50, 50),
            NavSelected: Color.FromArgb(45, 45, 45),
            NavHover: Color.FromArgb(40, 40, 40),
            Input: Color.FromArgb(28, 28, 28),
            Track: Color.FromArgb(62, 62, 62),
            Series:
            [
                Color.FromArgb(96, 205, 255), Color.FromArgb(76, 194, 180), Color.FromArgb(180, 150, 255),
                Color.FromArgb(255, 150, 90), Color.FromArgb(108, 203, 95), Color.FromArgb(240, 120, 220),
                Color.FromArgb(252, 206, 0), Color.FromArgb(170, 170, 170),
            ]);
    }
}
