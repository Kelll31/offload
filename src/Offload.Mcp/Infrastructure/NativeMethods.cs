using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Offload.Mcp.Infrastructure;

/// <summary>Нужные функции Win32: окончательный путь (через ссылки/junction) и наследуемость stdio.</summary>
internal static class NativeMethods
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint HandleFlagInherit = 0x1;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    /// <summary>
    /// Окончательный путь существующего файла/папки: разрешает symlink/junction и короткие имена 8.3.
    /// null — не удалось открыть (нет доступа, «висячая» ссылка).
    /// </summary>
    public static string? GetFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        try
        {
            using var h = CreateFile(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var buf = new char[1024];
            var n = GetFinalPathNameByHandle(h, buf, (uint)buf.Length, 0);
            if (n == 0) return null;
            if (n > buf.Length)
            {
                buf = new char[n + 1];
                n = GetFinalPathNameByHandle(h, buf, (uint)buf.Length, 0);
                if (n == 0 || n > buf.Length) return null;
            }
            var s = new string(buf, 0, (int)n);
            if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s[8..];
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) return s[4..];
            return s;
        }
        catch
        {
            return null;
        }
    }

    private const uint GenericWrite = 0x40000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize,
        out int returned, IntPtr overlapped);

    /// <summary>
    /// Создать junction (точку подключения каталога) link → target без прав администратора и без cmd. false — не удалось
    /// (link при этом не остаётся). Используется, чтобы песочница агента видела зависимости проекта (node_modules).
    /// </summary>
    public static bool TryCreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            target = PathGuard.TrimTrailingSeparator(Path.GetFullPath(target));
            if (!Directory.Exists(target) || Directory.Exists(link) || File.Exists(link)) return false;
            Directory.CreateDirectory(link);
            var subst = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
            var print = System.Text.Encoding.Unicode.GetBytes(target);
            var pathBytes = subst.Length + 2 + print.Length + 2;
            var buf = new byte[16 + pathBytes];
            BitConverter.TryWriteBytes(buf.AsSpan(0), IoReparseTagMountPoint);
            BitConverter.TryWriteBytes(buf.AsSpan(4), (ushort)(8 + pathBytes));
            BitConverter.TryWriteBytes(buf.AsSpan(8), (ushort)0);
            BitConverter.TryWriteBytes(buf.AsSpan(10), (ushort)subst.Length);
            BitConverter.TryWriteBytes(buf.AsSpan(12), (ushort)(subst.Length + 2));
            BitConverter.TryWriteBytes(buf.AsSpan(14), (ushort)print.Length);
            subst.CopyTo(buf, 16);
            print.CopyTo(buf, 16 + subst.Length + 2);
            bool ok;
            using (var h = CreateFile(link, GenericWrite, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero))
                ok = !h.IsInvalid && DeviceIoControl(h, FsctlSetReparsePoint, buf, buf.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!ok) Directory.Delete(link);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Сделать stdin/stdout/stderr процесса ненаследуемыми: дочерние процессы (git, cmd, opencode, трей)
    /// не должны получить копии каналов MCP — иначе IDE не увидит закрытия stdout и трей держал бы его вечно.
    /// </summary>
    public static void MakeStdHandlesNonInheritable()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var id in new[] { -10, -11, -12 })
        {
            try
            {
                var h = GetStdHandle(id);
                if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, HandleFlagInherit, 0);
            }
            catch
            {
                // Не критично.
            }
        }
    }
}
