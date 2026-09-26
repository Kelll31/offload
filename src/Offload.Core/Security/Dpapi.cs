using System.Runtime.InteropServices;
using System.Text;

namespace Offload.Core.Security;

/// <summary>
/// Шифрование секретов (токенов) для текущего пользователя Windows через DPAPI — CryptProtectData из crypt32.dll
/// напрямую, без пакета System.Security.Cryptography.ProtectedData. Расшифровать может только тот же пользователь
/// на том же компьютере; в config.json хранится base64 зашифрованного блока.
/// </summary>
public static class Dpapi
{
    /// <summary>Дополнительная «соль» приложения: блок, зашифрованный другой программой без неё, не расшифруется.</summary>
    private static readonly byte[] Entropy = "Offload.Secret.v1"u8.ToArray();

    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>Зашифровать строку; результат — base64.</summary>
    public static string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        var data = Encoding.UTF8.GetBytes(plainText);
        try
        {
            return Convert.ToBase64String(Transform(data, protect: true)
                ?? throw new InvalidOperationException(L.T("Не удалось зашифровать секрет средствами Windows (DPAPI).")));
        }
        finally
        {
            Array.Clear(data);
        }
    }

    /// <summary>Расшифровать base64 от <see cref="Protect"/>; null — пусто, повреждено или зашифровано другим пользователем.</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedBase64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        var plain = Transform(blob, protect: false);
        if (plain is null) return null;
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outBlob = default(DataBlob);
        try
        {
            inBlob = Alloc(input);
            entropyBlob = Alloc(Entropy);
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob);
            if (!ok || outBlob.pbData == IntPtr.Zero) return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            Free(inBlob);
            Free(entropyBlob);
            if (outBlob.pbData != IntPtr.Zero)
            {
                // Расшифрованные данные не оставляем в освобождённой памяти.
                Marshal.Copy(new byte[outBlob.cbData], 0, outBlob.pbData, outBlob.cbData);
                LocalFree(outBlob.pbData);
            }
        }
    }

    private static DataBlob Alloc(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob { cbData = data.Length, pbData = ptr };
    }

    private static void Free(DataBlob blob)
    {
        if (blob.pbData == IntPtr.Zero) return;
        Marshal.Copy(new byte[blob.cbData], 0, blob.pbData, blob.cbData);
        Marshal.FreeHGlobal(blob.pbData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
