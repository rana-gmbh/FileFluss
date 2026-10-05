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

/// <summary>
/// A cloud account's quota for the status bar and the sidebar tooltip (macOS
/// <c>CloudQuotaFormatter</c>): "Dropbox: 4.20 GB of 15.0 GB used (28%)", or
/// "Google Drive: 412 KB used" when there is no total.
/// </summary>
public static class CloudQuotaFormatter
{
    public static string Summary(CloudStorageQuota quota, string accountDisplayName)
    {
        var used = ByteFormatter.Format(quota.UsedBytes);
        if (quota.TotalBytes is not { } total || total <= 0)
        {
            return $"{accountDisplayName}: {L("{0} used", used)}";
        }

        var percent = PercentString(quota.UsedBytes, total);
        return $"{accountDisplayName}: {L("{0} of {1} used", used, ByteFormatter.Format(total))} ({percent})";
    }

    /// <summary>
    /// "28%", clamped to 0–100 (soft limits can briefly report more). Whole percent from 1%
    /// up; one decimal below, so a 10 MB file on a 1 TB drive doesn't read as a flat 0%.
    /// </summary>
    public static string PercentString(long used, long total)
    {
        if (total <= 0 || used <= 0)
        {
            return "0%";
        }

        var clamped = Math.Clamp((double)used / total * 100, 0, 100);
        return clamped >= 1
            ? Math.Round(clamped, MidpointRounding.AwayFromZero).ToString("0", Culture) + "%"
            : clamped.ToString("0.0", Culture) + "%";
    }
}
