using System.Diagnostics;
using Offload.Core.Logging;

namespace Offload.App.Util;

/// <summary>Общие помощники интерфейса: безопасный запуск операций, сообщения, открытие файлов и ссылок.</summary>
internal static class Ui
{
    public const string Caption = "Offload";

    /// <summary>Понятный пользователю текст ошибки (на русском).</summary>
    public static string FriendlyError(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } agg) ex = agg.InnerExceptions[0];
        return ex switch
        {
            NotImplementedException => "Эта функция ещё не реализована в текущей сборке Offload.",
            OperationCanceledException => "Операция отменена.",
            UnauthorizedAccessException => $"Нет доступа: {ex.Message}",
            HttpRequestException h => $"Ошибка сети: {h.Message}",
            _ => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message,
        };
    }

    /// <summary>
    /// Выполнить операцию интерфейса: исключения журналируются и показываются пользователю.
    /// Отмена (OperationCanceledException) не считается ошибкой.
    /// </summary>
    public static async Task<bool> RunSafeAsync(IWin32Window? owner, Func<Task> action, string errorTitle = "Не удалось выполнить операцию")
    {
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("ui", errorTitle, ex);
            ShowError(owner, errorTitle, ex);
            return false;
        }
    }

    /// <summary>Синхронный вариант для коротких действий.</summary>
    public static bool RunSafe(IWin32Window? owner, Action action, string errorTitle = "Не удалось выполнить операцию")
    {
        try
        {
            action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("ui", errorTitle, ex);
            ShowError(owner, errorTitle, ex);
            return false;
        }
    }

    /// <summary>Вызов модуля с запасным значением при ошибке (ошибка пишется в журнал).</summary>
    public static T Try<T>(Func<T> func, T fallback, string what)
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"{what}: {ex.GetType().Name}: {ex.Message}");
            return fallback;
        }
    }

    public static void ShowError(IWin32Window? owner, string title, Exception ex) =>
        ShowError(owner, title, FriendlyError(ex));

    public static void ShowError(IWin32Window? owner, string title, string details)
    {
        var text = string.IsNullOrWhiteSpace(details) ? title : $"{title}.{Environment.NewLine}{Environment.NewLine}{details}";
        Show(owner, text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public static void Info(IWin32Window? owner, string text) =>
        Show(owner, text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(IWin32Window? owner, string text) =>
        Show(owner, text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static bool Confirm(IWin32Window? owner, string text, bool warning = false) =>
        Show(owner, text, MessageBoxButtons.YesNo, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Question) == DialogResult.Yes;

    public static DialogResult Show(IWin32Window? owner, string text, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        try
        {
            return owner is Control { IsDisposed: false, IsHandleCreated: true, Visible: true }
                ? MessageBox.Show(owner, text, Caption, buttons, icon)
                : MessageBox.Show(text, Caption, buttons, icon);
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Не удалось показать сообщение: {ex.Message}");
            return DialogResult.None;
        }
    }

    /// <summary>Открыть ссылку или файл программой по умолчанию.</summary>
    public static void OpenShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Не удалось открыть {target}: {ex.Message}");
            Warn(null, $"Не удалось открыть:{Environment.NewLine}{target}{Environment.NewLine}{Environment.NewLine}{ex.Message}");
        }
    }

    /// <summary>Открыть папку в проводнике (создаётся при необходимости).</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Не удалось открыть папку {path}: {ex.Message}");
            Warn(null, $"Не удалось открыть папку:{Environment.NewLine}{path}");
        }
    }

    /// <summary>Показать файл в проводнике (если файла нет — открыть его папку).</summary>
    public static void SelectInExplorer(string file)
    {
        try
        {
            if (File.Exists(file))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false });
            else if (Path.GetDirectoryName(file) is { } dir && Directory.Exists(dir))
                OpenFolder(dir);
            else
                Warn(null, $"Файл не найден:{Environment.NewLine}{file}");
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Не удалось показать файл {file}: {ex.Message}");
        }
    }

    /// <summary>Открыть текстовый файл в Блокноте.</summary>
    public static void OpenInNotepad(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { file }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn("ui", $"Не удалось открыть {file}: {ex.Message}");
            SelectInExplorer(file);
        }
    }

    public static bool TrySetClipboard(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    /// <summary>Выполнить действие в потоке интерфейса элемента (если он ещё жив).</summary>
    public static void Post(Control control, Action action)
    {
        if (control.IsDisposed) return;
        try
        {
            if (!control.IsHandleCreated) return;
            control.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Окно уже закрыто.
        }
    }

    /// <summary>Форматирование числа токенов: 12 345.</summary>
    public static string N(long value) => value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public static string Tokens(int contextTokens) =>
        contextTokens <= 0 ? "авто"
        : contextTokens % 1024 == 0 ? $"{contextTokens / 1024}K"
        : N(contextTokens);
}
