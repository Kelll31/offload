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
    public const string PresetContrast = "contrast";

    /// <summary>Схема, следующая за цветом акцента Windows («Персонализация» → «Цвета»).</summary>
    public const string PresetSystemAccent = "system-accent";

    /// <summary>Готовая цветовая схема: акцент для светлой и тёмной основы; Midnight/Contrast — своя тёмная основа.</summary>
    public sealed record PresetInfo(string Key, string Name, Color LightAccent, Color DarkAccent, bool ForcesDark = false);

    /// <summary>Готовые схемы (Name — русский текст, в интерфейсе через L.T).</summary>
    public static readonly IReadOnlyList<PresetInfo> Presets =
    [
        new(PresetDefault, "Индиго (Offload)", Color.FromArgb(79, 70, 229), Color.FromArgb(129, 140, 248)), // l10n-key
        new("blue", "Синяя (Windows)", Color.FromArgb(0, 95, 184), Color.FromArgb(96, 205, 255)), // l10n-key
        new(PresetSystemAccent, "Как акцент Windows", Color.Empty, Color.Empty), // l10n-key
        new("teal", "Бирюзовая", Color.FromArgb(0, 120, 124), Color.FromArgb(76, 194, 180)), // l10n-key
        new("green", "Зелёная", Color.FromArgb(16, 124, 16), Color.FromArgb(108, 203, 95)), // l10n-key
        new("purple", "Фиолетовая", Color.FromArgb(118, 76, 178), Color.FromArgb(180, 150, 255)), // l10n-key
        new("orange", "Оранжевая", Color.FromArgb(188, 72, 12), Color.FromArgb(255, 150, 90)), // l10n-key
        new("pink", "Розовая", Color.FromArgb(180, 44, 140), Color.FromArgb(240, 120, 220)), // l10n-key
        new("aurora", "Аврора (тёмная)", Color.FromArgb(45, 212, 191), Color.FromArgb(45, 212, 191), ForcesDark: true), // l10n-key
        new("graphite", "Графит (тёмная)", Color.FromArgb(251, 146, 60), Color.FromArgb(251, 146, 60), ForcesDark: true), // l10n-key
        new("midnight", "Полночь (тёмная)", Color.FromArgb(122, 162, 255), Color.FromArgb(122, 162, 255), ForcesDark: true), // l10n-key
        new(PresetContrast, "Высокий контраст (тёмная)", Color.FromArgb(255, 214, 0), Color.FromArgb(255, 214, 0), ForcesDark: true), // l10n-key
    ];

    /// <summary>Выбранная схема (ключ из <see cref="Presets"/> или custom).</summary>
    public static string Preset { get; private set; } = PresetDefault;

    /// <summary>
    /// Включён ли в Windows режим высокой контрастности, под который подобрана текущая палитра
    /// (для отслеживания его включения и выключения).
    /// </summary>
    public static bool HighContrastApplied { get; private set; }

    /// <summary>Режим высокой контрастности Windows.</summary>
    public static bool SystemHighContrast()
    {
        try
        {
            return SystemInformation.HighContrast;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Схема с учётом высокой контрастности Windows: при теме «как в Windows» и включённом режиме — «Высокий контраст»,
    /// иначе — выбранная пользователем.
    /// </summary>
    internal static string? EffectivePreset(string? mode, string? preset, bool highContrast) =>
        highContrast && mode is not (ModeLight or ModeDark) ? PresetContrast : preset;

    /// <summary>Применить режим темы (и схему/свой акцент). Возвращает true, если тёмная.</summary>
    public static bool Initialize(string? mode, string? preset = null, string? accent = null)
    {
        Mode = mode is ModeLight or ModeDark ? mode : ModeSystem;
        HighContrastApplied = SystemHighContrast();
        preset = EffectivePreset(Mode, preset, HighContrastApplied);
        Preset = preset == PresetCustom || Presets.Any(p => p.Key == preset) ? preset! : PresetDefault;
        var info = Presets.FirstOrDefault(p => p.Key == Preset);
        IsDark = info is { ForcesDark: true } || Mode == ModeDark || (Mode == ModeSystem && SystemPrefersDark());
        var palette = Preset switch
        {
            "midnight" => Palette.Midnight,
            "aurora" => Palette.Aurora,
            "graphite" => Palette.Graphite,
            PresetContrast => Palette.Contrast,
            _ => IsDark ? Palette.Dark : Palette.Light,
        };
        if (Preset == PresetCustom && TryParseColor(accent, out var custom)) palette = WithAccent(palette, custom, IsDark);
        else if (Preset == PresetSystemAccent && SystemAccentColor() is { } sys) palette = WithAccent(palette, ReadableAccent(sys, IsDark), IsDark);
        else if (info is not null && Preset != PresetDefault && Preset != PresetSystemAccent && !info.ForcesDark)
            palette = WithAccent(palette, IsDark ? info.DarkAccent : info.LightAccent, IsDark);
        _p = palette;
        return IsDark;
    }

    /// <summary>Будет ли тема тёмной при этих настройках (без применения).</summary>
    public static bool WouldBeDark(string? mode, string? preset)
    {
        preset = EffectivePreset(mode, preset, SystemHighContrast());
        return Presets.FirstOrDefault(p => p.Key == preset) is { ForcesDark: true } || mode == ModeDark || (mode is not ModeLight && SystemPrefersDark());
    }

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
        var luminance = Luminance(accent);
        // Заливка кнопок: на тёмной основе светлый акцент затемняется, чтобы белый текст оставался читаемым.
        var fill = dark && luminance > 0.55 ? Blend(accent, Color.Black, 0.28) : accent;
        return p with
        {
            Accent = accent,
            AccentHover = Blend(accent, Color.Black, dark ? 0.10 : 0.12),
            AccentPressed = Blend(accent, Color.Black, dark ? 0.20 : 0.25),
            OnAccent = luminance > 0.6 ? Color.Black : Color.White,
            AccentLight = Blend(p.Background, accent, dark ? 0.22 : 0.10),
            NavSelected = Blend(p.Background, accent, dark ? 0.16 : 0.09),
            Fill = fill,
            FillEnd = ShiftHue(fill, 28),
            OnFill = Luminance(fill) > 0.62 ? Color.Black : Color.White,
            // Акцент — первая серия графиков; похожие на него цвета убираем, чтобы две серии не совпали.
            Series = [accent, .. p.Series.Skip(1).Where(c => ColorDistance(c, accent) > 60), .. p.Series.Skip(1).Where(c => ColorDistance(c, accent) <= 60).Select(c => Blend(c, dark ? Color.White : Color.Black, 0.35))],
        };
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    /// <summary>Сдвинуть оттенок на <paramref name="degrees"/> (для второго цвета градиента).</summary>
    internal static Color ShiftHue(Color c, float degrees)
    {
        var h = (c.GetHue() + degrees) % 360f;
        var s = c.GetSaturation();
        var l = c.GetBrightness();
        return FromHsl(h < 0 ? h + 360 : h, s, l);
    }

    internal static Color FromHsl(float h, float s, float l)
    {
        if (s <= 0) return Color.FromArgb((int)(l * 255), (int)(l * 255), (int)(l * 255));
        var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        float Hue(float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1f / 6) return p + (q - p) * 6 * t;
            if (t < 1f / 2) return q;
            if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6;
            return p;
        }
        var hk = h / 360f;
        return Color.FromArgb(
            (int)Math.Round(Math.Clamp(Hue(hk + 1f / 3), 0, 1) * 255),
            (int)Math.Round(Math.Clamp(Hue(hk), 0, 1) * 255),
            (int)Math.Round(Math.Clamp(Hue(hk - 1f / 3), 0, 1) * 255));
    }

    /// <summary>
    /// Цвет акцента Windows (HKCU\…\Explorer\Accent, AccentColorMenu: 0xAABBGGRR). null — не задан или недоступен.
    /// </summary>
    public static Color? SystemAccentColor()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentColorMenu") is int abgr) return FromAbgr(abgr);
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int dwmAbgr) return FromAbgr(dwmAbgr);
        }
        catch
        {
            // Нет доступа к реестру — схема по умолчанию.
        }
        return null;
    }

    /// <summary>Значение реестра 0xAABBGGRR → цвет.</summary>
    internal static Color FromAbgr(int abgr) => Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);

    /// <summary>
    /// Акцент Windows, подогнанный под фон: на светлой основе слишком светлый цвет затемняется, на тёмной слишком тёмный —
    /// осветляется (иначе ссылки и выделение не читаются).
    /// </summary>
    internal static Color ReadableAccent(Color c, bool dark)
    {
        var l = Luminance(c);
        if (dark && l < 0.45) return Blend(c, Color.White, Math.Min(0.6, 0.45 - l + 0.25));
        if (!dark && l > 0.55) return Blend(c, Color.Black, Math.Min(0.6, l - 0.55 + 0.2));
        return c;
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

    /// <summary>Градиентная заливка основных кнопок и значков: начало, конец и текст на ней.</summary>
    public static Color Fill => _p.Fill;
    public static Color FillEnd => _p.FillEnd;
    public static Color OnFill => _p.OnFill;

    /// <summary>Цвет тени карточек (с прозрачностью).</summary>
    public static Color Shadow => IsDark ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(22, 20, 24, 60);

    /// <summary>Радиусы скругления (логические пиксели): элементы управления, карточки, крупные блоки.</summary>
    public const int RadiusControl = 6;
    public const int RadiusCard = 12;
    public const int RadiusHero = 16;

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
    public static Color Sidebar => Blend(_p.Background, _p.Card, IsDark ? 0.35 : 0.55);
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
            // HRESULT осознанно не проверяем: на старых Windows атрибут просто не поддерживается, и это ловится catch ниже.
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            var caption = ColorTranslator.ToWin32(Surface);
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            // Windows 11: скруглённые углы и тонкая рамка оттенка акцента.
            var corners = NativeMethods.DWMWCP_ROUND;
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
            var border = ColorTranslator.ToWin32(Blend(Border, Accent, 0.45));
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
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
        Color[] Series, Color Fill, Color FillEnd, Color OnFill)
    {
        public static readonly Palette Light = new(
            Accent: Color.FromArgb(79, 70, 229),
            AccentHover: Color.FromArgb(67, 56, 202),
            AccentPressed: Color.FromArgb(55, 48, 163),
            OnAccent: Color.White,
            AccentLight: Color.FromArgb(238, 240, 255),
            Green: Color.FromArgb(22, 163, 74),
            Amber: Color.FromArgb(234, 160, 0),
            Red: Color.FromArgb(220, 38, 38),
            Gray: Color.FromArgb(148, 152, 163),
            WarnText: Color.FromArgb(161, 98, 7),
            ErrorText: Color.FromArgb(185, 28, 28),
            OkText: Color.FromArgb(21, 128, 61),
            TextPrimary: Color.FromArgb(22, 24, 29),
            TextMuted: Color.FromArgb(91, 96, 107),
            TextFaint: Color.FromArgb(141, 146, 156),
            Border: Color.FromArgb(227, 229, 236),
            Background: Color.FromArgb(244, 245, 249),
            Card: Color.White,
            CardHover: Color.FromArgb(248, 249, 252),
            NavSelected: Color.FromArgb(230, 231, 250),
            NavHover: Color.FromArgb(234, 236, 243),
            Input: Color.White,
            Track: Color.FromArgb(226, 228, 236),
            Series:
            [
                Color.FromArgb(79, 70, 229), Color.FromArgb(14, 165, 233), Color.FromArgb(168, 85, 247),
                Color.FromArgb(234, 88, 12), Color.FromArgb(22, 163, 74), Color.FromArgb(219, 39, 119),
                Color.FromArgb(234, 179, 8), Color.FromArgb(100, 116, 139),
            ],
            Fill: Color.FromArgb(79, 70, 229),
            FillEnd: Color.FromArgb(147, 51, 234),
            OnFill: Color.White);

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
            ],
            Fill: Color.FromArgb(79, 110, 230),
            FillEnd: Color.FromArgb(139, 92, 246),
            OnFill: Color.White);

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
            ],
            Fill: Color.FromArgb(255, 214, 0),
            FillEnd: Color.FromArgb(255, 214, 0),
            OnFill: Color.Black);

        public static readonly Palette Dark = new(
            Accent: Color.FromArgb(129, 140, 248),
            AccentHover: Color.FromArgb(115, 125, 235),
            AccentPressed: Color.FromArgb(99, 108, 214),
            OnAccent: Color.Black,
            AccentLight: Color.FromArgb(40, 42, 74),
            Green: Color.FromArgb(74, 222, 128),
            Amber: Color.FromArgb(250, 204, 21),
            Red: Color.FromArgb(248, 113, 113),
            Gray: Color.FromArgb(128, 134, 148),
            WarnText: Color.FromArgb(250, 204, 21),
            ErrorText: Color.FromArgb(252, 165, 165),
            OkText: Color.FromArgb(74, 222, 128),
            TextPrimary: Color.FromArgb(236, 237, 242),
            TextMuted: Color.FromArgb(163, 168, 181),
            TextFaint: Color.FromArgb(111, 116, 131),
            Border: Color.FromArgb(43, 46, 56),
            Background: Color.FromArgb(18, 19, 24),
            Card: Color.FromArgb(27, 29, 36),
            CardHover: Color.FromArgb(34, 37, 46),
            NavSelected: Color.FromArgb(36, 38, 62),
            NavHover: Color.FromArgb(29, 31, 40),
            Input: Color.FromArgb(21, 23, 28),
            Track: Color.FromArgb(43, 46, 56),
            Series:
            [
                Color.FromArgb(129, 140, 248), Color.FromArgb(56, 189, 248), Color.FromArgb(192, 132, 252),
                Color.FromArgb(251, 146, 60), Color.FromArgb(74, 222, 128), Color.FromArgb(244, 114, 182),
                Color.FromArgb(250, 204, 21), Color.FromArgb(148, 163, 184),
            ],
            Fill: Color.FromArgb(99, 102, 241),
            FillEnd: Color.FromArgb(147, 51, 234),
            OnFill: Color.White);

        /// <summary>«Аврора»: глубокий сине-зелёный фон, бирюзовый акцент с переходом в фиолетовый.</summary>
        public static readonly Palette Aurora = new(
            Accent: Color.FromArgb(45, 212, 191),
            AccentHover: Color.FromArgb(38, 190, 172),
            AccentPressed: Color.FromArgb(30, 165, 150),
            OnAccent: Color.Black,
            AccentLight: Color.FromArgb(20, 58, 62),
            Green: Color.FromArgb(52, 211, 153),
            Amber: Color.FromArgb(251, 191, 36),
            Red: Color.FromArgb(251, 113, 133),
            Gray: Color.FromArgb(120, 140, 150),
            WarnText: Color.FromArgb(252, 211, 77),
            ErrorText: Color.FromArgb(253, 164, 175),
            OkText: Color.FromArgb(52, 211, 153),
            TextPrimary: Color.FromArgb(230, 244, 244),
            TextMuted: Color.FromArgb(150, 180, 184),
            TextFaint: Color.FromArgb(100, 128, 134),
            Border: Color.FromArgb(28, 52, 58),
            Background: Color.FromArgb(8, 20, 24),
            Card: Color.FromArgb(13, 30, 35),
            CardHover: Color.FromArgb(18, 38, 44),
            NavSelected: Color.FromArgb(17, 48, 52),
            NavHover: Color.FromArgb(14, 34, 39),
            Input: Color.FromArgb(6, 16, 19),
            Track: Color.FromArgb(28, 52, 58),
            Series:
            [
                Color.FromArgb(45, 212, 191), Color.FromArgb(167, 139, 250), Color.FromArgb(56, 189, 248),
                Color.FromArgb(251, 146, 60), Color.FromArgb(52, 211, 153), Color.FromArgb(244, 114, 182),
                Color.FromArgb(251, 191, 36), Color.FromArgb(148, 163, 184),
            ],
            Fill: Color.FromArgb(13, 148, 136),
            FillEnd: Color.FromArgb(124, 58, 237),
            OnFill: Color.White);

        /// <summary>«Графит»: нейтральная тёмно-серая основа, тёплый оранжевый акцент.</summary>
        public static readonly Palette Graphite = new(
            Accent: Color.FromArgb(251, 146, 60),
            AccentHover: Color.FromArgb(234, 130, 46),
            AccentPressed: Color.FromArgb(214, 115, 35),
            OnAccent: Color.Black,
            AccentLight: Color.FromArgb(60, 42, 30),
            Green: Color.FromArgb(132, 204, 22),
            Amber: Color.FromArgb(250, 204, 21),
            Red: Color.FromArgb(248, 113, 113),
            Gray: Color.FromArgb(140, 140, 140),
            WarnText: Color.FromArgb(250, 204, 21),
            ErrorText: Color.FromArgb(252, 165, 165),
            OkText: Color.FromArgb(163, 230, 53),
            TextPrimary: Color.FromArgb(240, 238, 235),
            TextMuted: Color.FromArgb(172, 168, 162),
            TextFaint: Color.FromArgb(120, 117, 112),
            Border: Color.FromArgb(50, 49, 47),
            Background: Color.FromArgb(22, 22, 21),
            Card: Color.FromArgb(31, 31, 30),
            CardHover: Color.FromArgb(38, 38, 37),
            NavSelected: Color.FromArgb(48, 40, 34),
            NavHover: Color.FromArgb(34, 34, 33),
            Input: Color.FromArgb(18, 18, 17),
            Track: Color.FromArgb(50, 49, 47),
            Series:
            [
                Color.FromArgb(251, 146, 60), Color.FromArgb(56, 189, 248), Color.FromArgb(192, 132, 252),
                Color.FromArgb(250, 204, 21), Color.FromArgb(132, 204, 22), Color.FromArgb(244, 114, 182),
                Color.FromArgb(45, 212, 191), Color.FromArgb(168, 162, 158),
            ],
            Fill: Color.FromArgb(234, 88, 12),
            FillEnd: Color.FromArgb(219, 39, 119),
            OnFill: Color.White);
    }
}
