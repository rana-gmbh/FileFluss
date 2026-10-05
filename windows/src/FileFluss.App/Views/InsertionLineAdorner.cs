// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FileFluss.App.Views;

/// <summary>
/// The accent line that shows where a dragged row or folder will land in a sidebar list
/// (macOS <c>FavoritesInsertionLine</c>): a short bar with a ring at its leading end, drawn in
/// the gap above row <c>index</c> — or below the last row when the index is the row count.
/// </summary>
public sealed class InsertionLineAdorner : Adorner
{
    private readonly ItemsControl _list;
    private int _index;

    private InsertionLineAdorner(ItemsControl list)
        : base(list)
    {
        _list = list;
        IsHitTestVisible = false;
    }

    /// <summary>Shows the line at gap <paramref name="index"/> of <paramref name="list"/>, reusing <paramref name="current"/> when it already belongs to that list.</summary>
    public static InsertionLineAdorner? Show(InsertionLineAdorner? current, ItemsControl list, int index)
    {
        if (current is not null && !ReferenceEquals(current._list, list))
        {
            current.Remove();
            current = null;
        }

        if (current is null)
        {
            if (AdornerLayer.GetAdornerLayer(list) is not { } layer)
            {
                return null;
            }

            current = new InsertionLineAdorner(list);
            layer.Add(current);
        }

        if (current._index != index)
        {
            current._index = index;
            current.InvalidateVisual();
        }

        return current;
    }

    public void Remove() => AdornerLayer.GetAdornerLayer(_list)?.Remove(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var count = _list.Items.Count;
        double y = 0;
        if (count > 0 && _list.ItemContainerGenerator.ContainerFromIndex(Math.Min(_index, count - 1)) is FrameworkElement row)
        {
            var top = row.TranslatePoint(default, _list).Y;
            y = _index >= count ? top + row.ActualHeight : top;
        }

        // Keep the line inside the list so the first and last gaps aren't clipped away.
        y = Math.Clamp(y, 2, Math.Max(2, _list.ActualHeight - 2));
        var brush = _list.TryFindResource("AccentTextBrush") as Brush ?? SystemColors.HighlightBrush;
        var pen = new Pen(brush, 2);
        const double left = 10;
        drawingContext.DrawEllipse(null, pen, new Point(left, y), 3, 3);
        drawingContext.DrawLine(pen, new Point(left + 3, y), new Point(Math.Max(left + 3, _list.ActualWidth - 6), y));
    }
}
