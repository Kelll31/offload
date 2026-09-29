using System.Runtime.InteropServices;
using Offload.App.Util;
using Offload.Core.Logging;

namespace Offload.App.Services;

/// <summary>
/// Глобальная горячая клавиша (RegisterHotKey): Ctrl+Alt+O открывает панель управления из любой программы. Если сочетание
/// уже занято другой программой, регистрация не удаётся — это пишется в журнал, а <see cref="IsRegistered"/> остаётся false.
/// </summary>
internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyId = 0x0FF1;

    /// <summary>Сочетание для показа в интерфейсе.</summary>
    public const string Display = "Ctrl+Alt+O";

    private readonly Action _pressed;

    public GlobalHotkey(Action pressed)
    {
        _pressed = pressed;
        CreateHandle(new CreateParams { Caption = "OffloadHotkey" });
    }

    public bool IsRegistered { get; private set; }

    /// <summary>Включить или выключить сочетание (повторные вызовы безопасны).</summary>
    public void Set(bool enabled)
    {
        if (enabled == IsRegistered || Handle == IntPtr.Zero) return;
        if (enabled)
        {
            IsRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, (uint)Keys.O);
            if (!IsRegistered) Log.Warn("ui", $"Горячая клавиша {Display} не зарегистрирована (занята другой программой?): код {Marshal.GetLastWin32Error()}");
            else Log.Debug("ui", $"Горячая клавиша {Display} зарегистрирована");
        }
        else
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            IsRegistered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == HotkeyId)
        {
            try
            {
                _pressed();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Исключение из обработчика сообщений окна не должно ронять цикл сообщений трея.
                Log.Error("ui", "Горячая клавиша", ex);
            }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Set(false);
        DestroyHandle();
    }
}
