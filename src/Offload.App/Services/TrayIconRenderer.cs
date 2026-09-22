using System.Drawing.Drawing2D;
using Offload.App.Util;

namespace Offload.App.Services;

/// <summary>
/// Значок трея: значок приложения размером SystemInformation.SmallIconSize с цветной точкой состояния.
/// HICON, созданный GetHicon, уничтожается через DestroyIcon при замене значка (иначе утечка GDI).
/// </summary>
internal sealed class TrayIconRenderer : IDisposable
{
    private readonly Bitmap _base;
    private readonly Size _size;
    private IntPtr _handle;
    private Icon? _icon;
    private Color? _lastColor;

    public TrayIconRenderer()
    {
        _size = SystemInformation.SmallIconSize;
        _base = new Bitmap(_size.Width, _size.Height);
        using var ico = AppIcons.IconOfSize(_size);
        using var bmp = ico.ToBitmap();
        using var g = Graphics.FromImage(_base);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(bmp, new Rectangle(Point.Empty, _size));
    }

    /// <summary>Установить значок с точкой заданного цвета; предыдущий HICON уничтожается после замены.</summary>
    public void Apply(NotifyIcon tray, Color dot)
    {
        if (_icon is not null && _lastColor == dot) return;

        using var bmp = new Bitmap(_base);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var d = Math.Max(6, (int)Math.Round(_size.Width * 0.5));
            var rect = new Rectangle(_size.Width - d, _size.Height - d, d - 1, d - 1);
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, rect);
            var inner = Rectangle.Inflate(rect, -Math.Max(1, d / 7), -Math.Max(1, d / 7));
            using var fill = new SolidBrush(dot);
            g.FillEllipse(fill, inner);
        }

        var newHandle = bmp.GetHicon();
        var newIcon = Icon.FromHandle(newHandle);
        var oldIcon = _icon;
        var oldHandle = _handle;

        tray.Icon = newIcon;
        _icon = newIcon;
        _handle = newHandle;
        _lastColor = dot;

        // Icon.FromHandle не владеет HICON — уничтожаем его явно.
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero) NativeMethods.DestroyIcon(oldHandle);
    }

    /// <summary>Маленькое изображение точки для пунктов меню.</summary>
    public static Bitmap DotImage(Color color, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var pad = Math.Max(2, size / 5);
        using var b = new SolidBrush(color);
        g.FillEllipse(b, pad, pad, size - pad * 2 - 1, size - pad * 2 - 1);
        return bmp;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_handle);
            _handle = IntPtr.Zero;
        }
        _base.Dispose();
    }
}
