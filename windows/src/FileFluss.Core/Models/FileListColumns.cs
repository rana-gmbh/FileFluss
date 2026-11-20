// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using static FileFluss.Core.Localization;

namespace FileFluss.Core.Models;

/// <summary>
/// Which file-list columns a panel shows (macOS <c>FileListColumnPrefs</c>): local and cloud
/// panels keep separate sets, toggled from the column header's context menu. The stored list
/// is the display order and always holds <see cref="FileListColumn.Name"/>, which cannot be
/// hidden; anything else in it that the panel kind does not offer is ignored.
/// </summary>
public static class FileListColumns
{
    /// <summary>Header order for a newly shown column: where it sits relative to the others.</summary>
    private static readonly FileListColumn[] Canonical =
        [FileListColumn.Name, FileListColumn.DateModified, FileListColumn.DateCreated, FileListColumn.Size, FileListColumn.Kind];

    /// <summary>macOS shows Date Modified and Size until the user picks otherwise.</summary>
    public static IReadOnlyList<FileListColumn> Defaults { get; } = [FileListColumn.Name, FileListColumn.DateModified, FileListColumn.Size];

    /// <summary>
    /// The columns the header menu offers, in menu order. Cloud panels have no Date Created:
    /// most providers don't expose a creation date, or expose it inconsistently (macOS rule).
    /// </summary>
    public static IReadOnlyList<FileListColumn> Toggleable(bool cloud) => cloud
        ? [FileListColumn.DateModified, FileListColumn.Size, FileListColumn.Kind]
        : [FileListColumn.DateModified, FileListColumn.DateCreated, FileListColumn.Size, FileListColumn.Kind];

    /// <summary>The columns to show, in display order: Name always, duplicates and unavailable ones dropped.</summary>
    public static List<FileListColumn> Visible(IEnumerable<FileListColumn>? stored, bool cloud)
    {
        var available = Toggleable(cloud);
        var result = new List<FileListColumn>();
        foreach (var column in stored ?? Defaults)
        {
            if ((column == FileListColumn.Name || available.Contains(column)) && !result.Contains(column))
            {
                result.Add(column);
            }
        }

        if (!result.Contains(FileListColumn.Name))
        {
            result.Insert(0, FileListColumn.Name);
        }

        return result;
    }

    /// <summary>
    /// The stored list after showing or hiding <paramref name="column"/>. A shown column goes
    /// before the first visible column that follows it in the default header order, so turning
    /// a column off and on again puts it back where it was.
    /// </summary>
    public static List<FileListColumn> Toggle(IEnumerable<FileListColumn>? stored, FileListColumn column, bool cloud)
    {
        var visible = Visible(stored, cloud);
        if (column == FileListColumn.Name || !Toggleable(cloud).Contains(column))
        {
            return visible;
        }

        if (visible.Remove(column))
        {
            return visible;
        }

        var rank = Array.IndexOf(Canonical, column);
        var index = visible.FindIndex(c => Array.IndexOf(Canonical, c) > rank && c != FileListColumn.Name);
        visible.Insert(index < 0 ? visible.Count : index, column);
        return visible;
    }

    /// <summary>The <c>AppSettings.ColumnWidths</c> key: "local.Size", "cloud.Name", …</summary>
    public static string WidthKey(bool cloud, FileListColumn column) => (cloud ? "cloud." : "local.") + column;

    /// <summary>The header and menu title, as on macOS.</summary>
    public static string Title(FileListColumn column) => column switch
    {
        FileListColumn.DateModified => L("Date Modified"),
        FileListColumn.DateCreated => L("Date Created"),
        FileListColumn.Size => L("Size"),
        FileListColumn.Kind => L("Kind"),
        _ => L("Name"),
    };

    /// <summary>What clicking the column's header sorts by.</summary>
    public static SortField SortFieldFor(FileListColumn column) => column switch
    {
        FileListColumn.DateModified => SortField.Date,
        FileListColumn.DateCreated => SortField.DateCreated,
        FileListColumn.Size => SortField.Size,
        FileListColumn.Kind => SortField.Kind,
        _ => SortField.Name,
    };
}
