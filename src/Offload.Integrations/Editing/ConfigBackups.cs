using Offload.Core.Logging;
using Offload.Core.Util;

namespace Offload.Integrations;

/// <summary>Резервная копия конфигурации IDE: какой файл, какая копия, когда сделана.</summary>
public sealed record ConfigBackup(string ConfigPath, string BackupPath, DateTime Created, long Size);

/// <summary>
/// Восстановление конфигурации IDE из резервной копии, которую Offload сделал перед правкой. Восстанавливается только копия
/// этого же файла из папки резервных копий Offload; текущий файл перед заменой тоже сохраняется в копию, запись — атомарная
/// (<see cref="Editing.ConfigFile.Edit"/>). Копия должна читаться как UTF-8 — иначе файл не трогается.
/// </summary>
public static class ConfigBackups
{
    /// <summary>Копии всех файлов конфигурации IDE (новые сверху).</summary>
    public static IReadOnlyList<ConfigBackup> For(IIdeIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(integration);
        return IntegrationRegistry.ConfigFiles(integration)
            .SelectMany(path => FileUtil.BackupsOf(path).Select(b => new ConfigBackup(path, b.Path, b.Created, b.Size)))
            .OrderByDescending(b => b.Created)
            .ToList();
    }

    public static IntegrationResult Restore(ConfigBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        // Только копия именно этого файла из папки копий Offload — путь из интерфейса не должен указывать куда угодно.
        if (!FileUtil.BackupsOf(backup.ConfigPath).Any(b => string.Equals(b.Path, backup.BackupPath, StringComparison.OrdinalIgnoreCase)))
            return new IntegrationResult(false, L.F("{0} не является резервной копией файла {1}.", backup.BackupPath, backup.ConfigPath));
        try
        {
            var text = Editing.ConfigFile.Decode(File.ReadAllBytes(backup.BackupPath));
            var res = Editing.ConfigFile.Edit(backup.ConfigPath, snap => snap.Exists && snap.Text == text ? null : text);
            if (res.Outcome == Editing.WriteOutcome.Unchanged)
                return new IntegrationResult(true, L.F("Файл {0} уже совпадает с этой копией.", backup.ConfigPath));
            Log.Info("Integrations", $"Восстановлен {backup.ConfigPath} из {backup.BackupPath}");
            return new IntegrationResult(true,
                L.F("Файл {0} восстановлен из копии от {1:g}.", backup.ConfigPath, backup.Created), res.BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Editing.ConfigReadException)
        {
            return new IntegrationResult(false, L.F("Не удалось восстановить {0}: {1}", backup.ConfigPath, ex.Message));
        }
    }
}
