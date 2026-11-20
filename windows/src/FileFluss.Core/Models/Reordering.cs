// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Models;

/// <summary>
/// Drag-to-reorder arithmetic shared by the sidebar's favourites and cloud accounts (macOS
/// <c>Array.move(fromOffsets:toOffset:)</c>). An insertion index counts gaps in the list as it
/// is before the move: 0 is above the first row, <c>count</c> below the last.
/// </summary>
public static class Reordering
{
    /// <summary>
    /// Where the item at <paramref name="from"/> ends up when dropped at the gap
    /// <paramref name="insertionIndex"/> — one less when that gap lies below it, since the item
    /// leaves its old place first.
    /// </summary>
    public static int TargetIndex(int from, int insertionIndex, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var target = insertionIndex > from ? insertionIndex - 1 : insertionIndex;
        return Math.Clamp(target, 0, count - 1);
    }

    /// <summary>A copy of <paramref name="items"/> with the first match moved to the gap <paramref name="insertionIndex"/>; unchanged when nothing matches.</summary>
    public static List<T> Move<T>(IReadOnlyList<T> items, Predicate<T> match, int insertionIndex)
    {
        var list = items.ToList();
        var from = list.FindIndex(match);
        if (from < 0)
        {
            return list;
        }

        var item = list[from];
        list.RemoveAt(from);
        list.Insert(TargetIndex(from, insertionIndex, items.Count), item);
        return list;
    }
}
