using Offload.App.Util;
using Offload.Core;
using Offload.Core.Config;
using Offload.Core.Localization;
using Offload.Core.Logging;
using Offload.Models;

namespace Offload.App.Services;

/// <summary>Папка для моделей: текущее значение и смена (общая для разделов «Модели» и «Настройки»).</summary>
internal static class ModelsFolder
{
    public static string Current()
    {
        var cfg = ConfigStore.Current;
        return Ui.Try(() => ModelManager.ModelsDir(cfg), cfg.Models.ModelsDir ?? AppPaths.DefaultModelsDir, "ModelsDir");
    }

    /// <summary>Выбрать новую папку. true — папка сменилась (уже скачанные модели остаются на месте).</summary>
    public static bool Change(IWin32Window? owner, IAppShell shell)
    {
        var current = Current();
        using var dlg = new FolderBrowserDialog
        {
            Description = L.T("Папка для хранения моделей (нужно много свободного места)"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
        var path = dlg.SelectedPath;
        if (string.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        if (!Ui.RunSafe(owner, () =>
            {
                Directory.CreateDirectory(path);
                ConfigStore.Update(c => c.Models.ModelsDir = path);
            }, L.T("Не удалось сменить папку моделей"))) return false;
        Log.Info("models", $"Папка моделей: {path}");
        Ui.Info(owner, L.F("Новые модели будут сохраняться в папку:{0}{1}{0}{0}Уже скачанные модели не перемещаются: они остаются на прежнем месте и продолжают работать.",
            Environment.NewLine, path));
        shell.ConfigChanged();
        return true;
    }
}
