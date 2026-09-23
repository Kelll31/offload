using Offload.App.Services;
using Offload.App.Util;
using Offload.Core.Util;
using Offload.Models;

namespace Offload.App.Forms;

/// <summary>Общий список моделей для вкладки «Модели» и мастера настройки.</summary>
internal static class ModelListBinder
{
    public static string RecommendedMark => L.T("★ рекомендуется");

    /// <summary>Столбцы списка (полный вариант — для вкладки «Модели»).</summary>
    public static ListView Create(bool full)
    {
        var lv = full
            ? Kit.List((L.T("Модель"), 27), (L.T("Размер"), 8), (L.T("Контекст"), 9), (L.T("Видеопамять"), 39), (L.T("Агент"), 7), (L.T("Статус"), 10))
            : Kit.List((L.T("Модель"), 32), (L.T("Размер"), 10), (L.T("Видеопамять"), 41), (L.T("Агент"), 7), (L.T("Статус"), 10));
        return lv;
    }

    /// <summary>Заполнить список, сохранив выделение (по идентификатору строки).</summary>
    public static void Fill(ListView lv, IReadOnlyList<ModelRow> rows, bool full, string? selectId = null)
    {
        selectId ??= Selected(lv)?.Id;
        lv.BeginUpdate();
        try
        {
            lv.Items.Clear();
            foreach (var r in rows)
            {
                var name = r.IsRecommended ? $"{r.Name}   {RecommendedMark}" : r.Name;
                var size = r.SizeBytes > 0 ? FileUtil.FormatBytes(r.SizeBytes) : "—";
                var tools = r.GoodToolCalling ? "✓" : "—";
                string[] cells = full
                    ? [name, size, r.ContextText, r.FitText, tools, r.StatusText]
                    : [name, size, r.FitText, tools, r.StatusText];
                var item = new ListViewItem(cells) { Tag = r, UseItemStyleForSubItems = false };
                if (r.IsRecommended) item.Font = Theme.Semibold(9f);
                if (r.IsActive) item.SubItems[^1].ForeColor = Theme.OkText;
                if (r.Fit is not null) item.SubItems[full ? 3 : 2].ForeColor = Texts.FitColor(r.Fit.Level);
                item.ToolTipText = r.Fit?.Explanation ?? r.Description;
                lv.Items.Add(item);
            }
            var toSelect = lv.Items.Cast<ListViewItem>().FirstOrDefault(i => ((ModelRow)i.Tag!).Id == selectId);
            if (toSelect is not null)
            {
                toSelect.Selected = true;
                toSelect.Focused = true;
                toSelect.EnsureVisible();
            }
        }
        finally
        {
            lv.EndUpdate();
        }
    }

    public static ModelRow? Selected(ListView lv) =>
        lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as ModelRow : null;

    /// <summary>Подписи квантизаций с размерами файлов, если они известны из каталога.</summary>
    public static IReadOnlyList<(string Quant, string Text, long Size)> Quants(CatalogModel m)
    {
        var list = new List<(string, string, long)>();
        foreach (var q in m.Quants)
        {
            var file = m.Files?.FirstOrDefault(f => string.Equals(f.Quant, q, StringComparison.OrdinalIgnoreCase));
            var size = file?.Size ?? (q == m.Quants.FirstOrDefault() ? m.ApproxSizeBytes : 0);
            list.Add((q, size > 0 ? $"{q}  ({FileUtil.FormatBytes(size)})" : q, size));
        }
        return list;
    }

    /// <summary>Сколько места нужно для квантизации (или приблизительный размер модели).</summary>
    public static long RequiredBytes(CatalogModel m, string? quant)
    {
        var file = m.Files?.FirstOrDefault(f => string.Equals(f.Quant, quant, StringComparison.OrdinalIgnoreCase));
        return file?.Size > 0 ? file.Size : m.ApproxSizeBytes;
    }

    /// <summary>Запас на диске сверх размера модели (временные файлы, журнал).</summary>
    public const long DiskReserveBytes = 512L * 1024 * 1024;
}
