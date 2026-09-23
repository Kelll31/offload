using System.Collections.Concurrent;
using System.Text;
using Offload.App.Controls;
using Offload.App.Services;
using Offload.App.Util;
using Offload.Core;
using Offload.Core.Logging;

namespace Offload.App.Forms.Pages;

/// <summary>
/// Вкладка «Журнал»: журнал приложения (вживую), llama-server и MCP (хвост файла, обновление раз в 2 секунды).
/// Строки раскрашены по уровню; фильтр по уровню и поиск по тексту; Ctrl+C копирует выделенные строки.
/// </summary>
internal sealed class LogPage : PageBase
{
    private const int MaxLines = 5000;
    private const int TailBytes = 1024 * 1024;
    private const byte NewLine = (byte)'\n';

    private enum Source { App, LlamaServer, Mcp }

    private enum Level { Debug, Info, Warn, Error }

    private readonly ComboBox _source = Kit.Combo(150);
    private readonly ComboBox _levels = Kit.Combo(190);
    private readonly TextBox _search = new()
    {
        Width = 240,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 3, 8, 3),
        PlaceholderText = L.T("Поиск по журналу…"),
        BackColor = Theme.Input,
        ForeColor = Theme.TextPrimary,
    };
    private readonly CheckBox _autoScroll = Kit.Check(L.T("Автопрокрутка"), true);
    private readonly ListView _list = new()
    {
        View = View.Details,
        VirtualMode = true,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.None,
        MultiSelect = true,
        HideSelection = false,
        Dock = DockStyle.Fill,
        Font = Theme.Mono(9f),
        BackColor = Theme.Input,
        ForeColor = Theme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 6, 0, 0),
    };
    private readonly Label _info = Kit.Hint("", autoWidth: true);
    private readonly Label _counts = Kit.Label("", Theme.Regular(8.5f), Theme.TextMuted);

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly System.Windows.Forms.Timer _appTimer;
    private readonly System.Windows.Forms.Timer _fileTimer;
    private readonly System.Windows.Forms.Timer _searchDebounce;
    private readonly List<(string Text, Level Level)> _lines = [];
    private readonly List<int> _view = [];
    private DateTime _appClearedAt = DateTime.MinValue;
    private readonly Dictionary<Source, long> _fileClearedAt = [];
    private (long Length, DateTime Write) _lastFileStamp;
    private bool _subscribed;
    private bool _reading;
    private int _maxChars;
    private long _fileOffset = -1;

    public LogPage(IAppShell shell) : base(shell)
    {
        _source.Items.AddRange([L.T("Приложение"), "llama-server", "MCP"]);
        _source.SelectedIndex = 0;
        _source.SelectedIndexChanged += (_, _) => SwitchSource();
        _levels.Items.AddRange([L.T("Все сообщения"), L.T("Без отладочных"), L.T("Предупреждения и ошибки"), L.T("Только ошибки")]);
        _levels.SelectedIndex = 1;
        _levels.SelectedIndexChanged += (_, _) => Rebuild(keepPosition: false);

        _list.Columns.Add("", 2000);
        _list.RetrieveVirtualItem += OnRetrieveItem;
        _list.KeyDown += OnListKeyDown;

        _searchDebounce = new System.Windows.Forms.Timer { Interval = 250 };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Rebuild(keepPosition: false);
        };
        _search.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };

        var root = Kit.FillTable();
        root.Padding = new Padding(16, 4, 28, 16);
        var filters = Kit.Flow(Kit.Label(L.T("Источник:")), _source, _levels, _search, _autoScroll);
        filters.WrapContents = true;
        root.AddRow(filters);
        var actions = Kit.Flow(
            Kit.Button(L.T("Копировать"), (_, _) => CopySelection()),
            Kit.Button(L.T("Очистить экран"), (_, _) => ClearScreen(), 120),
            Kit.Button(L.T("Открыть папку журналов"), (_, _) => Ui.OpenFolder(AppPaths.LogsDir), 160));
        root.AddRow(actions);
        var status = Kit.Table(100, 0);
        _counts.Anchor = AnchorStyles.Right;
        status.AddRow(_info, _counts);
        root.AddRow(status);
        root.AddFillRow(_list);
        Controls.Add(root);

        _appTimer = CreateTimer(300, FlushPending);
        _fileTimer = CreateTimer(2000, () => _ = RefreshFileAsync(force: false));
        _autoScroll.CheckedChanged += (_, _) =>
        {
            if (_autoScroll.Checked) ScrollToEnd();
        };
    }

    public override string Key => Tabs.Log;

    public override string Title => L.T("Журнал");

    public override string Subtitle => L.T("Журналы приложения, llama-server и MCP-сервера");

    public override string Glyph => Glyphs.Log;

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
        if (disposing)
        {
            Unsubscribe();
            _searchDebounce.Dispose();
        }
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
            SetLines(Log.Snapshot().Where(e => e.Time > _appClearedAt).Select(e => e.ToString()), keepPosition: false);
            _info.Text = Log.CurrentFile is { } f ? L.F("Файл: {0}", f) : "";
            Subscribe();
            _appTimer.Start();
        }
        else
        {
            Unsubscribe();
            _lastFileStamp = default;
            _fileOffset = -1;
            _info.Text = L.F("Файл: {0}", FilePath(Current));
            _ = RefreshFileAsync(force: true);
            _fileTimer.Start();
        }
    }

    // ---------- Строки и фильтр ----------

    private static Level Classify(string line)
    {
        if (line.Contains("[ERR]", StringComparison.Ordinal) || line.Contains("[FTL]", StringComparison.Ordinal)) return Level.Error;
        if (line.Contains("[WRN]", StringComparison.Ordinal)) return Level.Warn;
        if (line.Contains("[DBG]", StringComparison.Ordinal) || line.Contains("[TRC]", StringComparison.Ordinal)) return Level.Debug;
        if (line.Contains("[INF]", StringComparison.Ordinal)) return Level.Info;
        // Сырой вывод llama-server: уровни словами.
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("E ", StringComparison.Ordinal)) return Level.Error;
        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase) || line.StartsWith("W ", StringComparison.Ordinal)) return Level.Warn;
        return Level.Info;
    }

    private Level MinLevel => _levels.SelectedIndex switch
    {
        0 => Level.Debug,
        2 => Level.Warn,
        3 => Level.Error,
        _ => Level.Info,
    };

    private bool Matches((string Text, Level Level) l)
    {
        if (l.Level < MinLevel) return false;
        var q = _search.Text.Trim();
        return q.Length == 0 || l.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void SetLines(IEnumerable<string> lines, bool keepPosition)
    {
        _lines.Clear();
        foreach (var l in lines) _lines.Add((l, Classify(l)));
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        Rebuild(keepPosition);
    }

    private void Rebuild(bool keepPosition)
    {
        var top = keepPosition && _list.VirtualListSize > 0 ? SafeTopIndex() : -1;
        _view.Clear();
        _maxChars = 0;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (!Matches(_lines[i])) continue;
            _view.Add(i);
            _maxChars = Math.Max(_maxChars, _lines[i].Text.Length);
        }
        _list.BeginUpdate();
        _list.SelectedIndices.Clear();
        _list.VirtualListSize = _view.Count;
        FitColumn();
        _list.EndUpdate();
        if (_autoScroll.Checked || top < 0) ScrollToEnd();
        else if (top < _view.Count) _list.TopItem = _list.Items[top];
        UpdateCounts();
    }

    private void Append(IEnumerable<string> lines)
    {
        var added = 0;
        foreach (var text in lines)
        {
            var l = (text, Classify(text));
            _lines.Add(l);
            added++;
            if (!Matches(l)) continue;
            _view.Add(_lines.Count - 1);
            _maxChars = Math.Max(_maxChars, text.Length);
        }
        if (added == 0) return;
        if (_lines.Count > MaxLines + 500)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
            Rebuild(keepPosition: !_autoScroll.Checked);
            return;
        }
        _list.VirtualListSize = _view.Count;
        FitColumn();
        if (_autoScroll.Checked) ScrollToEnd();
        UpdateCounts();
    }

    private void FitColumn()
    {
        var charW = TextRenderer.MeasureText("0000000000", _list.Font).Width / 10.0;
        var width = (int)Math.Max(_list.ClientSize.Width - 4, (_maxChars + 4) * charW);
        if (_list.Columns[0].Width != width) _list.Columns[0].Width = width;
    }

    private void UpdateCounts()
    {
        int warn = 0, err = 0;
        foreach (var l in _lines)
        {
            if (l.Level == Level.Warn) warn++;
            else if (l.Level == Level.Error) err++;
        }
        var shown = _view.Count == _lines.Count
            ? Ui.Plural(_lines.Count, "строка", "строки", "строк")
            : L.F("показано {0} из {1}", Ui.N(_view.Count), Ui.Plural(_lines.Count, "строки", "строк", "строк"));
        _counts.Text = L.F("{0} · предупреждений: {1} · ошибок: {2}", shown, Ui.N(warn), Ui.N(err));
        _counts.ForeColor = err > 0 ? Theme.ErrorText : warn > 0 ? Theme.WarnText : Theme.TextMuted;
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var (text, level) = e.ItemIndex >= 0 && e.ItemIndex < _view.Count ? _lines[_view[e.ItemIndex]] : ("", Level.Info);
        e.Item = new ListViewItem(text)
        {
            ForeColor = level switch
            {
                Level.Error => Theme.ErrorText,
                Level.Warn => Theme.WarnText,
                Level.Debug => Theme.TextFaint,
                _ => Theme.TextPrimary,
            },
        };
    }

    private int SafeTopIndex()
    {
        try { return _list.TopItem?.Index ?? -1; }
        catch { return -1; }
    }

    private void ScrollToEnd()
    {
        if (_view.Count == 0) return;
        try { _list.EnsureVisible(_view.Count - 1); }
        catch (ArgumentOutOfRangeException) { }
    }

    // ---------- Источники ----------

    private void FlushPending()
    {
        if (Current != Source.App || _pending.IsEmpty) return;
        var added = new List<string>();
        while (_pending.TryDequeue(out var e))
        {
            if (e.Time <= _appClearedAt) continue;
            added.Add(e.ToString());
        }
        if (added.Count > 0) Append(added);
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
                    _fileOffset = -1;
                    SetLines([source == Source.LlamaServer
                        ? L.T("Журнал llama-server пока пуст: сервер ещё не запускался.")
                        : L.T("Журнал MCP пока пуст: IDE ещё не обращалась к Offload.")], keepPosition: false);
                }
                return;
            }
            var stamp = (fi.Length, fi.LastWriteTimeUtc);
            if (!force && stamp == _lastFileStamp) return;
            _lastFileStamp = stamp;

            // Дочитываем только новое: выделение и позиция чтения не сбрасываются, пока сервер пишет журнал.
            if (!force && _fileOffset >= 0 && fi.Length >= _fileOffset)
            {
                var offset = _fileOffset;
                var (added, next) = await Task.Run(() => ReadChunk(path, offset, skipPartialFirst: false));
                if (IsDisposed || Current != source) return;
                _fileOffset = next;
                if (added.Count > 0) Append(added);
                return;
            }

            var from = _fileClearedAt.GetValueOrDefault(source);
            if (from > fi.Length) from = 0; // файл ротирован
            var start = Math.Max(from, fi.Length - TailBytes);
            var (lines, end) = await Task.Run(() => ReadChunk(path, start, skipPartialFirst: start > from && start > 0));
            if (IsDisposed || Current != source) return;
            _fileOffset = end;
            if (lines.Count > MaxLines) lines = lines.GetRange(lines.Count - MaxLines, MaxLines);
            SetLines(lines, keepPosition: !_autoScroll.Checked);
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

    /// <summary>
    /// Полные строки файла от смещения до последнего перевода строки (недописанный хвост прочитается в следующий раз).
    /// Возвращает строки и смещение сразу после последнего прочитанного перевода строки. Файл открыт с FileShare.ReadWrite.
    /// </summary>
    private static (List<string> Lines, long Next) ReadChunk(string path, long offset, bool skipPartialFirst)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > fs.Length) offset = 0;
        fs.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[fs.Length - offset];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = fs.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var last = Array.LastIndexOf(bytes, NewLine, Math.Max(0, read - 1));
        if (read == 0 || last < 0) return ([], offset);
        var first = 0;
        if (skipPartialFirst)
        {
            // Первая строка обрезана посередине (читаем только хвост файла).
            var nl = Array.IndexOf(bytes, NewLine, 0, last + 1);
            first = nl < 0 ? last + 1 : nl + 1;
        }
        var text = Encoding.UTF8.GetString(bytes, first, last + 1 - first);
        if (offset == 0 && first == 0 && text.Length > 0 && text[0] == '﻿') text = text[1..];
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return (lines, offset + last + 1);
    }

    // ---------- Действия ----------

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelection();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.A)
        {
            _list.BeginUpdate();
            for (var i = 0; i < _list.VirtualListSize; i++) _list.SelectedIndices.Add(i);
            _list.EndUpdate();
            e.Handled = true;
        }
    }

    private void CopySelection()
    {
        var indices = _list.SelectedIndices.Count > 0
            ? _list.SelectedIndices.Cast<int>().OrderBy(i => i)
            : Enumerable.Range(0, _view.Count);
        var sb = new StringBuilder();
        foreach (var i in indices)
            if (i < _view.Count) sb.AppendLine(_lines[_view[i]].Text);
        if (sb.Length == 0) return;
        if (!Ui.TrySetClipboard(sb.ToString())) Ui.Warn(Owner, L.T("Не удалось скопировать в буфер обмена."));
    }

    private void ClearScreen()
    {
        if (Current == Source.App)
        {
            _appClearedAt = DateTime.Now;
            _pending.Clear();
        }
        else
        {
            try
            {
                var fi = new FileInfo(FilePath(Current));
                _fileClearedAt[Current] = fi.Exists ? fi.Length : 0;
                _fileOffset = _fileClearedAt[Current];
            }
            catch
            {
                _fileClearedAt[Current] = 0;
            }
        }
        SetLines([], keepPosition: false);
    }
}
