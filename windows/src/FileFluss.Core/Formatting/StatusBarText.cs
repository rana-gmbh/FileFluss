// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Formatting;

/// <summary>
/// The panel status bar, as macOS writes it (<c>FileListView.statusFooter</c>,
/// <c>CloudFileListView.cloudStatusFooter</c>): the listing summary, then the selection,
/// then the volume's free space or the account's quota, separated by " · ".
/// </summary>
public static class StatusBarText
{
    public const string Separator = " · ";

    /// <summary>"3 files, 2 folders — 1.20 MB".</summary>
    public static string Listing(int files, int folders, long bytes)
        => L("{0} files, {1} folders — {2}", files, folders, ByteFormatter.Format(bytes));

    /// <summary>
    /// "Selected: 4 items, 12.0 MB", or "Selected: 4 items, calculating…" while folder sizes
    /// are summed. Empty without a selection.
    /// </summary>
    public static string Selection(int count, long? bytes, bool calculating)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        if (calculating)
        {
            return L("Selected: {0} items, calculating…", count);
        }

        return bytes is { } size ? L("Selected: {0} items, {1}", count, ByteFormatter.Format(size)) : string.Empty;
    }

    /// <summary>The non-empty parts joined with " · ".</summary>
    public static string Join(params string?[] parts)
        => string.Join(Separator, parts.Where(p => !string.IsNullOrEmpty(p)));
}
