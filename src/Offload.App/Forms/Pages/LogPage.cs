using System.Collections.Concurrent;
using System.Text;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.App.Forms.Pages;

/// <summary>Вкладка «Журнал»: журнал приложения (вживую), llama-server и MCP (хвост файла, обновление раз в 2 секунды).</summary>
internal sealed class LogPage : PageBase
{
    private const int MaxLines = 2000;
    private const int TailBytes = 512 * 1024;

    private enum Source { App, LlamaServer, Mcp }

    private readonly ComboBox _source = Kit.Combo(180);
    private readonly CheckBox _autoScroll = Kit.Check("Автопрокрутка", true);
    private readonly TextBox _text = new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Dock = DockStyle.Fill,
        Font = Theme.Mono(9f),
        BackColor = Color.White,
        Margin = new Padding(0, 4, 0, 0),
        MaxLength = 0,
    };
    private readonly Label _info = Kit.Hint("", autoWidth: true);

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly System.Windows.Forms.Timer _appTimer;
    private readonly System.Windows.Forms.Timer _fileTimer;
    private readonly List<string> _appLines = [];
    private DateTime _appClearedAt = DateTime.MinValue;
    private readonly Dictionary<Source, long> _fileClearedAt = [];
    private (long Length, DateTime Write) _lastFileStamp;
    private bool _subscribed;
    private bool _reading;

    public LogPage(IAppShell shell) : base(shell)
    {
        _source.Items.AddRange(["Приложение", "llama-server", "MCP"]);
        _source.SelectedIndex = 0;
        _source.SelectedIndexChanged += (_, _) => SwitchSource();

        var root = Kit.FillTable();
        root.Padding = new Padding(16, 12, 16, 12);
        var bar = Kit.Flow(
            Kit.Label("Источник:"), _source, _autoScroll,
            Kit.Button("Копировать", (_, _) => CopyAll()),
            Kit.Button("Очистить экран", (_, _) => ClearScreen(), 120),
            Kit.Button("Открыть папку журналов", (_, _) => Ui.OpenFolder(AppPaths.LogsDir), 160));
        root.AddRow(bar);
        root.AddRow(_info);
        root.AddFillRow(_text);
        Controls.Add(root);

        _appTimer = CreateTimer(300, FlushPending);
        _fileTimer = CreateTimer(2000, () => _ = RefreshFileAsync(force: false));
        _autoScroll.CheckedChanged += (_, _) =>
        {
            if (_autoScroll.Checked) ScrollToEnd();
        };
    }

    public override string Key => Tabs.Log;

    public override string Title => "Журнал";

    private Source Current => (Source)Math.Max(0, _source.SelectedIndex);

    private string FilePath(Source s) => Path.Combine(AppPaths.LogsDir, s == Source.LlamaServer ? "llama-server.log" : "mcp.log");

    protected override void OnActivated() => SwitchSource();

    protected override void OnDeactivated()
    {
        _appTimer.Stop();
        _fileTimer.Stop();
        Unsubscribe();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Unsubscribe();
        base.Dispose(disposing);
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        Log.EntryAdded += OnEntry;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        Log.EntryAdded -= OnEntry;
        _subscribed = false;
        _pending.Clear();
    }

    // Вызывается из любого потока — только кладём в очередь, вывод делает таймер интерфейса.
    private void OnEntry(LogEntry e) => _pending.Enqueue(e);

    private void SwitchSource()
    {
        if (!IsActive) return;
        _appTimer.Stop();
        _fileTimer.Stop();
        if (Current == Source.App)
        {
            Unsubscribe();
            _appLines.Clear();
            _appLines.AddRange(Log.Snapshot().Where(e => e.Time > _appClearedAt).Select(e => e.ToString()));
            TrimAppLines();
            SetText(string.Join(Environment.NewLine, _appLines), keepPosition: false);
            _info.Text = Log.CurrentFile is { } f ? $"Файл: {f}" : "";
            Subscribe();
            _appTimer.Start();
        }
        else
        {
            Unsubscribe();
            _lastFileStamp = default;
            _info.Text = $"Файл: {FilePath(Current)}";
            _ = RefreshFileAsync(force: true);
            _fileTimer.Start();
        }
    }

    private void FlushPending()
    {
        if (Current != Source.App || _pending.IsEmpty) return;
        var added = new StringBuilder();
        while (_pending.TryDequeue(out var e))
        {
            if (e.Time <= _appClearedAt) continue;
            var line = e.ToString();
            _appLines.Add(line);
            added.Append(_appLines.Count > 1 ? Environment.NewLine : "").Append(line);
        }
        if (added.Length == 0) return;
        if (_appLines.Count > MaxLines + 200)
        {
            TrimAppLines();
            SetText(string.Join(Environment.NewLine, _appLines), keepPosition: !_autoScroll.Checked);
            return;
        }
        if (_autoScroll.Checked)
        {
            _text.AppendText(added.ToString());
        }
        else
        {
            var first = FirstVisibleLine();
            var selStart = _text.SelectionStart;
            var selLen = _text.SelectionLength;
            _text.AppendText(added.ToString());
            _text.Select(selStart, selLen);
            ScrollToLine(first);
        }
    }

    private void TrimAppLines()
    {
        if (_appLines.Count > MaxLines) _appLines.RemoveRange(0, _appLines.Count - MaxLines);
    }

    private async Task RefreshFileAsync(bool force)
    {
        if (_reading) return;
        var source = Current;
        if (source == Source.App) return;
        var path = FilePath(source);
        _reading = true;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                if (force || _lastFileStamp != default)
                {
                    _lastFileStamp = default;
                    SetText(source == Source.LlamaServer
                        ? "Журнал llama-server пока пуст: сервер ещё не запускался."
                        : "Журнал MCP пока пуст: IDE ещё не обращалась к Offload.", keepPosition: false);
                }
                return;
            }
            var stamp = (fi.Length, fi.LastWriteTimeUtc);
            if (!force && stamp == _lastFileStamp) return;
            _lastFileStamp = stamp;
            var from = _fileClearedAt.GetValueOrDefault(source);
            if (from > fi.Length) from = 0; // файл ротирован
            var text = await Task.Run(() => ReadTail(path, from));
            if (IsDisposed || Current != source) return;
            SetText(text, keepPosition: !_autoScroll.Checked);
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"Чтение журнала {path}: {ex.Message}");
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>Последние строки файла (не более MaxLines), файл открыт с FileShare.ReadWrite.</summary>
    private static string ReadTail(string path, long fromOffset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(fromOffset, fs.Length - TailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = sr.ReadToEnd();
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var skipFirst = start > fromOffset && start > 0; // первая строка обрезана посередине
        var list = lines.Skip(skipFirst ? 1 : 0).ToList();
        if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        if (list.Count > MaxLines) list = list.GetRange(list.Count - MaxLines, MaxLines);
        return string.Join(Environment.NewLine, list);
    }

    private void SetText(string text, bool keepPosition)
    {
        if (_text.Text == text) return;
        var first = keepPosition ? FirstVisibleLine() : 0;
        _text.Text = text;
        if (_autoScroll.Checked || !keepPosition) ScrollToEnd();
        else ScrollToLine(first);
    }

    private void ScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }

    private int FirstVisibleLine() =>
        _text.IsHandleCreated ? (int)NativeMethods.SendMessage(_text.Handle, NativeMethods.EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0;

    private void ScrollToLine(int line)
    {
        if (!_text.IsHandleCreated) return;
        var current = FirstVisibleLine();
        NativeMethods.SendMessage(_text.Handle, NativeMethods.EM_LINESCROLL, IntPtr.Zero, (IntPtr)(line - current));
    }

    private void CopyAll()
    {
        var text = _text.SelectionLength > 0 ? _text.SelectedText : _text.Text;
        if (string.IsNullOrEmpty(text)) return;
        if (!Ui.TrySetClipboard(text)) Ui.Warn(Owner, "Не удалось скопировать в буфер обмена.");
    }

    private void ClearScreen()
    {
        if (Current == Source.App)
        {
            _appClearedAt = DateTime.Now;
            _appLines.Clear();
            _pending.Clear();
        }
        else
        {
            try
            {
                var fi = new FileInfo(FilePath(Current));
                _fileClearedAt[Current] = fi.Exists ? fi.Length : 0;
            }
            catch
            {
                _fileClearedAt[Current] = 0;
            }
        }
        _text.Clear();
    }
}
