namespace Offload.App.Services;

/// <summary>Встроенные ресурсы: значок приложения и логотип.</summary>
internal static class AppIcons
{
    private const string IconResource = "Offload.App.app.ico";
    private const string LogoResource = "Offload.App.app-64.png";

    private static Icon? _appIcon;
    private static Image? _logo;

    public static Stream? OpenIconStream() => typeof(AppIcons).Assembly.GetManifestResourceStream(IconResource);

    /// <summary>Значок окна (все размеры из .ico).</summary>
    public static Icon AppIcon
    {
        get
        {
            if (_appIcon is not null) return _appIcon;
            using var s = OpenIconStream();
            _appIcon = s is null ? SystemIcons.Application : new Icon(s);
            return _appIcon;
        }
    }

    /// <summary>Значок нужного размера (например, для трея).</summary>
    public static Icon IconOfSize(Size size)
    {
        using var s = OpenIconStream();
        return s is null ? new Icon(SystemIcons.Application, size) : new Icon(s, size);
    }

    public static Image Logo
    {
        get
        {
            if (_logo is not null) return _logo;
            using var s = typeof(AppIcons).Assembly.GetManifestResourceStream(LogoResource);
            if (s is null)
            {
                _logo = AppIcon.ToBitmap();
            }
            else
            {
                // Копия в памяти: Image.FromStream требует, чтобы поток жил всё время жизни картинки.
                using var tmp = Image.FromStream(s);
                _logo = new Bitmap(tmp);
            }
            return _logo;
        }
    }
}
