using System.Diagnostics;
using Offload.Core.Logging;

namespace Offload.App.Util;

/// <summary>Общие помощники интерфейса: безопасный запуск операций, сообщения, открытие файлов и ссылок.</summary>
internal static class Ui
{
    public const string Caption = "Offload";

    /// <summary>Понятный пользователю текст ошибки (на языке интерфейса).</summary>
    public static string FriendlyError(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } agg) ex = agg.InnerExceptions[0];
        return ex switch
        {
            NotImplementedException => L.T("Эта функция ещё не реализована в текущей сборке Offload."),
            OperationCanceledException => L.T("Операция отменена."),
            UnauthorizedAccessException => L.F("Нет доступа: {0}", ex.Message),
            HttpRequestException h => L.F("Ошибка сети: {0}", h.Message),
            _ => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message,
        };
    }

    /// <summary>
    /// Выполнить операцию интерфейса: исключения журналируются и показываются пользователю.
    /// Отмена (OperationCanceledException) не считается ошибкой.
    /// </summary>
    public static async Task<bool> RunSafeAsync(IWin32Window? owner, Func<Task> action, string? errorTitle = null)
    {
        errorTitle ??= DefaultErrorTitle;
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
    public static bool RunSafe(IWin32Window? owner, Action action, string? errorTitle = null)
    {
        errorTitle ??= DefaultErrorTitle;
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

    private static string DefaultErrorTitle => L.T("Не удалось выполнить операцию");

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
            Warn(null, L.F("Не удалось открыть:{0}{1}{0}{0}{2}", Environment.NewLine, target, ex.Message));
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
            Warn(null, L.F("Не удалось открыть папку:{0}{1}", Environment.NewLine, path));
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
                Warn(null, L.F("Файл не найден:{0}{1}", Environment.NewLine, file));
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

    /// <summary>Форматирование числа токенов: 12 345 (в культуре языка интерфейса).</summary>
    public static string N(long value) => value.ToString("N0", L.Culture);

    /// <summary>Число со словом в нужной форме: Plural(2, "строка", "строки", "строк") → «2 строки» / «2 lines».</summary>
    public static string Plural(long n, string one, string few, string many) => L.Plural(n, one, few, many);

    /// <summary>Короткая запись большого числа: 950, 12,4 тыс., 3,1 млн (12.4K, 3.1M).</summary>
    public static string Short(long value)
    {
        var c = L.Culture;
        var a = Math.Abs(value);
        var en = L.IsEnglish;
        return a >= 1_000_000 ? (value / 1_000_000d).ToString(a >= 10_000_000 ? "0" : "0.#", c) + (en ? "M" : " млн") // l10n-ignore — единицы по языку
            : a >= 10_000 ? (value / 1_000d).ToString(a >= 100_000 ? "0" : "0.#", c) + (en ? "K" : " тыс.") // l10n-ignore
            : N(value);
    }

    public static string Tokens(int contextTokens) =>
        contextTokens <= 0 ? L.T("авто")
        : contextTokens % 1024 == 0 ? $"{contextTokens / 1024}K"
        : N(contextTokens);
}
