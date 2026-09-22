using System.Runtime.InteropServices;

namespace Offload.App.Util;

internal static class NativeMethods
{
    /// <summary>Разрешить любому процессу вывести своё окно на передний план (для повторного запуска exe).</summary>
    public const int ASFW_ANY = -1;

    public const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    public const int EM_LINESCROLL = 0x00B6;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
