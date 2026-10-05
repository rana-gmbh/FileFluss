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
    {
        // macOS always says "files" and "folders"; Windows gets the singular right too.
        var size = ByteFormatter.Format(bytes);
        return (files == 1, folders == 1) switch
        {
            (true, true) => L("1 file, 1 folder — {0}", size),
            (true, false) => L("1 file, {0} folders — {1}", folders, size),
            (false, true) => L("{0} files, 1 folder — {1}", files, size),
            _ => L("{0} files, {1} folders — {2}", files, folders, size),
        };
    }

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
            return count == 1 ? L("Selected: 1 item, calculating…") : L("Selected: {0} items, calculating…", count);
        }

        if (bytes is not { } total)
        {
            return string.Empty;
        }

        return count == 1 ? L("Selected: 1 item, {0}", ByteFormatter.Format(total)) : L("Selected: {0} items, {1}", count, ByteFormatter.Format(total));
    }

    /// <summary>The non-empty parts joined with " · ".</summary>
    public static string Join(params string?[] parts)
        => string.Join(Separator, parts.Where(p => !string.IsNullOrEmpty(p)));
}
