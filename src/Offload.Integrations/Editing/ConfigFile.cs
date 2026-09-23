using System.Security.Cryptography;
using System.Text;
using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Integrations.Editing;

/// <summary>Файл нельзя прочитать как текст UTF-8 (UTF-16, битые байты…).</summary>
internal sealed class ConfigReadException(string message) : Exception(message);

/// <summary>Файл постоянно меняется другой программой — запись отменена.</summary>
internal sealed class ConfigBusyException(string message) : IOException(message);

/// <summary>Снимок файла на момент чтения (для проверки, что его не переписали, пока мы готовили правку).</summary>
internal sealed record ConfigSnapshot(string Path, bool Exists, byte[] Hash, string Text);

internal enum WriteOutcome { Unchanged, Written }

internal sealed record WriteResult(WriteOutcome Outcome, string? BackupPath);

/// <summary>
/// Чтение/запись чужих конфигов: строгий UTF-8 (BOM допускается при чтении, при записи не пишется),
/// резервная копия перед изменением, атомарная замена и повтор, если файл изменился между чтением и записью.
/// </summary>
internal static class ConfigFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ConfigSnapshot Read(string path)
    {
        if (!File.Exists(path)) return new ConfigSnapshot(path, false, [], "");
        var bytes = ReadAllBytesShared(path);
        return new ConfigSnapshot(path, true, SHA256.HashData(bytes), Decode(bytes));
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            throw new ConfigReadException(L.T("файл сохранён в кодировке UTF-16, а не UTF-8"));
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            var text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            if (text.Contains('\0')) throw new ConfigReadException(L.T("файл содержит нулевые байты (повреждён или не текстовый)"));
            return text;
        }
        catch (DecoderFallbackException)
        {
            throw new ConfigReadException(L.T("файл не в кодировке UTF-8"));
        }
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) when (attempt < 5 && File.Exists(path))
            {
                Thread.Sleep(40 * (attempt + 1));
            }
        }
    }

    public static bool IsUnchanged(ConfigSnapshot snap)
    {
        if (!File.Exists(snap.Path)) return !snap.Exists;
        if (!snap.Exists) return false;
        return SHA256.HashData(ReadAllBytesShared(snap.Path)).AsSpan().SequenceEqual(snap.Hash);
    }

    /// <summary>
    /// Цикл «прочитать → изменить → записать, если файл не менялся». <paramref name="transform"/> получает текст
    /// (без BOM; пустой для отсутствующего файла) и возвращает новый текст или null, если менять нечего.
    /// Может вызываться повторно, поэтому должна быть без побочных эффектов.
    /// </summary>
    public static WriteResult Edit(string path, Func<ConfigSnapshot, string?> transform, int attempts = 6)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var snap = Read(path);
            var updated = transform(snap);
            if (updated is null) return new WriteResult(WriteOutcome.Unchanged, null);

            if (!IsUnchanged(snap))
            {
                // Файл переписали, пока мы готовили правку (например, Claude Code) — начинаем заново.
                Thread.Sleep(60 * (attempt + 1));
                continue;
            }

            string? backup = null;
            if (snap.Exists)
            {
                backup = FileUtil.Backup(path);
                if (backup is null) Log.Warn("Integrations", $"Не удалось сделать резервную копию {path}");
            }
            FileUtil.WriteAllTextAtomic(path, updated);
            Log.Info("Integrations", $"Изменён файл {path}" + (backup is null ? "" : $" (копия: {backup})"));
            return new WriteResult(WriteOutcome.Written, backup);
        }
        throw new ConfigBusyException(L.F("файл {0} постоянно изменяется другой программой — попробуйте ещё раз", path));
    }

    /// <summary>Удалить файл (с резервной копией), только если он не менялся с момента снимка.</summary>
    public static string? Delete(ConfigSnapshot snap)
    {
        if (!snap.Exists || !File.Exists(snap.Path)) return null;
        if (!IsUnchanged(snap)) throw new ConfigBusyException(L.F("файл {0} изменился — попробуйте ещё раз", snap.Path));
        var backup = FileUtil.Backup(snap.Path);
        File.Delete(snap.Path);
        Log.Info("Integrations", $"Удалён файл {snap.Path}" + (backup is null ? "" : $" (копия: {backup})"));
        return backup;
    }
}
