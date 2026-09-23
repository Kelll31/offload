using Offload.App.Controls;

namespace Offload.App.Util;

/// <summary>
/// Фабрики элементов для построения интерфейса в коде (без дизайнера).
/// Все размеры — в логических пикселях при 96 DPI; масштабирование выполняет AutoScaleMode.Dpi формы,
/// поэтому элементы создаются в конструкторе формы до первого показа.
/// Имя «Kit», а не «Layout»: внутри наследников Control имя Layout занято событием Control.Layout.
/// </summary>
internal static class Kit
{
    /// <summary>Таблица с автоматической высотой, растянутая по ширине (Dock = Top). 0 — столбец по содержимому.</summary>
    public static TableLayoutPanel Table(params float[] columnPercents)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            ColumnCount = Math.Max(1, columnPercents.Length),
            RowCount = 0,
            BackColor = Color.Transparent,
        };
        if (columnPercents.Length == 0)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }
        else
        {
            foreach (var p in columnPercents)
                t.ColumnStyles.Add(p <= 0 ? new ColumnStyle(SizeType.AutoSize) : new ColumnStyle(SizeType.Percent, p));
        }
        return t;
    }

    /// <summary>Таблица на всю доступную область (Dock = Fill).</summary>
    public static TableLayoutPanel FillTable(params float[] columnPercents)
    {
        var t = Table(columnPercents);
        t.AutoSize = false;
        t.Dock = DockStyle.Fill;
        return t;
    }

    /// <summary>Таблица «подпись — значение»: первый столбец по ширине подписей, второй — всё остальное.</summary>
    public static TableLayoutPanel Grid() => Table(0, 100);

    /// <summary>Добавить строку с автоматической высотой. Последний элемент занимает оставшиеся столбцы.</summary>
    public static int AddRow(this TableLayoutPanel t, params Control?[] cells) => AddRowCore(t, new RowStyle(SizeType.AutoSize), cells);

    /// <summary>Добавить строку, забирающую всё оставшееся место по высоте.</summary>
    public static int AddFillRow(this TableLayoutPanel t, params Control?[] cells) => AddRowCore(t, new RowStyle(SizeType.Percent, 100), cells);

    /// <summary>Добавить строку фиксированной высоты (логические пиксели).</summary>
    public static int AddFixedRow(this TableLayoutPanel t, int height, params Control?[] cells) => AddRowCore(t, new RowStyle(SizeType.Absolute, height), cells);

    private static int AddRowCore(TableLayoutPanel t, RowStyle style, Control?[] cells)
    {
        var row = t.RowCount;
        t.RowCount = row + 1;
        t.RowStyles.Add(style);
        for (var i = 0; i < cells.Length; i++)
        {
            var c = cells[i];
            if (c is null) continue;
            t.Controls.Add(c, i, row);
            if (i == cells.Length - 1 && i < t.ColumnCount - 1)
                t.SetColumnSpan(c, t.ColumnCount - i);
        }
        return row;
    }

    /// <summary>Строка «подпись: элемент [пояснение]» для таблицы Grid().</summary>
    public static int AddField(this TableLayoutPanel grid, string caption, Control editor, string? hint = null)
    {
        var label = Label(caption);
        label.Margin = new Padding(0, 7, 12, 4);
        label.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        if (editor is Label { AutoSize: true } value and not LinkLabel)
        {
            // Значение-текст выравниваем по верхнему краю вместе с подписью.
            value.Anchor = value.Dock == DockStyle.Fill
                ? AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
                : AnchorStyles.Left | AnchorStyles.Top;
            value.Dock = DockStyle.None;
            value.Margin = new Padding(0, 7, 8, 4);
            if (value.Font.Size > 9.5f) value.Margin = new Padding(0, 5, 8, 4);
        }
        if (hint is null) return grid.AddRow(label, editor);
        var cell = Flow(editor, Hint(hint, autoWidth: true));
        // Без переноса: иначе предварительный расчёт высоты при узкой ширине оставляет пустоту под таблицей.
        cell.WrapContents = false;
        cell.Margin = Padding.Empty;
        return grid.AddRow(label, cell);
    }

    /// <summary>Горизонтальный ряд элементов (кнопки и т. п.).</summary>
    public static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 4, 0, 4),
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };
        f.Controls.AddRange(controls);
        return f;
    }

    /// <summary>Прокручиваемая область: содержимое растягивается по ширине, высота — по содержимому.</summary>
    public static Panel Scroll(Control content, Padding? padding = null)
    {
        var p = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = padding ?? new Padding(16, 12, 16, 12),
            Margin = Padding.Empty,
            BackColor = Theme.Surface,
        };
        content.Dock = DockStyle.Top;
        p.Controls.Add(content);
        return p;
    }

    public static Label Label(string text, Font? font = null, Color? color = null)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 4, 8, 4),
            Anchor = AnchorStyles.Left,
            BackColor = Color.Transparent,
        };
        if (font is not null) l.Font = font;
        if (color is not null) l.ForeColor = color.Value;
        return l;
    }

    /// <summary>Многострочная подпись, переносящаяся по ширине ячейки таблицы.</summary>
    public static Label Wrap(string text, Font? font = null, Color? color = null)
    {
        var l = Label(text, font, color);
        l.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        l.Dock = DockStyle.Fill;
        l.Margin = new Padding(0, 2, 0, 6);
        return l;
    }

    /// <summary>Пояснение мелким серым шрифтом (переносится по ширине, если autoWidth = false).</summary>
    public static Label Hint(string text, bool autoWidth = false)
    {
        if (!autoWidth) return Wrap(text, Theme.Regular(8.5f), Theme.TextMuted);
        var l = Label(text, Theme.Regular(8.5f), Theme.TextMuted);
        l.Margin = new Padding(0, 7, 8, 4);
        return l;
    }

    /// <summary>Заголовок раздела.</summary>
    public static SectionHeader Section(string text, bool first = false) => new(text, first);

    public static Button Button(string text, EventHandler? onClick = null, int minWidth = 96)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(minWidth, 30),
            Padding = new Padding(8, 0, 8, 0),
            Margin = new Padding(0, 2, 8, 2),
            UseVisualStyleBackColor = true,
            UseMnemonic = false,
        };
        if (onClick is not null) b.Click += onClick;
        return b;
    }

    public static AccentButton Primary(string text, EventHandler? onClick = null, int minWidth = 110)
    {
        var b = new AccentButton
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(minWidth, 30),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 2, 8, 2),
            UseMnemonic = false,
        };
        if (onClick is not null) b.Click += onClick;
        return b;
    }

    public static CheckBox Check(string text, bool isChecked = false)
    {
        return new CheckBox
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 4, 8, 4),
            Anchor = AnchorStyles.Left,
            BackColor = Color.Transparent,
        };
    }

    public static NumericUpDown Number(decimal min, decimal max, decimal value, int width = 110, int decimals = 0, decimal increment = 1)
    {
        var n = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            DecimalPlaces = decimals,
            Increment = increment,
            Width = width,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 3),
            TextAlign = HorizontalAlignment.Right,
            ThousandsSeparator = decimals == 0 && max >= 10000,
        };
        n.Value = Math.Clamp(value, min, max);
        return n;
    }

    public static ComboBox Combo(int width = 220)
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = width,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 3),
        };
    }

    public static TextBox TextBox(string text = "", bool readOnly = false)
    {
        return new TextBox
        {
            Text = text,
            ReadOnly = readOnly,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 3, 8, 3),
        };
    }

    /// <summary>Многострочное поле ввода фиксированной высоты (логические пиксели).</summary>
    public static TextBox MultiLine(int height, bool mono = false, bool readOnly = false)
    {
        return new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            ReadOnly = readOnly,
            Height = height,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 0, 6),
            Font = mono ? Theme.Mono(9f) : Theme.Regular(9f),
            BackColor = Theme.Input,
            ForeColor = Theme.TextPrimary,
        };
    }

    /// <summary>Ссылка, открывающая адрес в браузере.</summary>
    public static LinkLabel Link(string text, string url)
    {
        var l = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 4, 12, 4),
            Anchor = AnchorStyles.Left,
            BackColor = Color.Transparent,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Accent,
        };
        l.LinkClicked += (_, _) => Ui.OpenShell(url);
        return l;
    }

    /// <summary>Ссылка-действие (без адреса).</summary>
    public static LinkLabel ActionLink(string text, Action onClick)
    {
        var l = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 4, 12, 4),
            Anchor = AnchorStyles.Left,
            BackColor = Color.Transparent,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Accent,
        };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    /// <summary>Список с подробностями: выделение строки, сетка, столбцы делят ширину пропорционально весам.</summary>
    public static ListView List(params (string Title, int Weight)[] columns)
    {
        var lv = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
            ShowItemToolTips = true,
        };
        foreach (var (title, _) in columns) lv.Columns.Add(title);
        var weights = columns.Select(c => Math.Max(1, c.Weight)).ToArray();

        void Fit()
        {
            if (!lv.IsHandleCreated || lv.Columns.Count != weights.Length) return;
            var width = lv.ClientSize.Width - 2;
            if (width <= 0) return;
            var total = weights.Sum();
            var used = 0;
            lv.BeginUpdate();
            for (var i = 0; i < weights.Length; i++)
            {
                var w = i == weights.Length - 1 ? width - used : width * weights[i] / total;
                lv.Columns[i].Width = Math.Max(20, w);
                used += w;
            }
            lv.EndUpdate();
        }

        lv.HandleCreated += (_, _) => Fit();
        lv.ClientSizeChanged += (_, _) => Fit();
        return lv;
    }

    /// <summary>Тонкая горизонтальная линия-разделитель.</summary>
    public static Control Separator() => new Panel
    {
        Height = 1,
        Dock = DockStyle.Top,
        BackColor = Theme.Border,
        Margin = new Padding(0, 8, 0, 8),
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
    };

    /// <summary>Пустой отступ заданной высоты.</summary>
    public static Control Spacer(int height) => new Panel { Height = height, Margin = Padding.Empty, BackColor = Color.Transparent, Width = 1 };

    /// <summary>Настроить форму: DPI-масштабирование, шрифт, значок. Вызывать после добавления всех элементов.</summary>
    public static void FinishForm(Form form)
    {
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
    }
}
