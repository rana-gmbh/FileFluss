// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;

namespace FileFluss.Core.Formatting;

/// <summary>
/// A cloud account's quota as the sidebar tooltip shows it (macOS <c>CloudQuotaFormatter</c>):
/// "Dropbox: 4.20 GB of 15.0 GB used (28%)", or "Google Drive: 412 KB used" without a total.
/// </summary>
public static class CloudQuotaFormatter
{
    public static string Summary(CloudStorageQuota quota, string accountDisplayName)
    {
        var used = ByteFormatter.Format(Math.Max(0, quota.UsedBytes));
        if (quota.TotalBytes is not { } total || total <= 0)
        {
            return $"{accountDisplayName}: {Localization.L("{0} used", used)}";
        }

        var usage = Localization.L("{0} of {1} used", used, ByteFormatter.Format(total));
        return $"{accountDisplayName}: {usage} ({PercentString(quota.UsedBytes, total)})";
    }

    /// <summary>
    /// "28%", clamped to 0–100 (quotas briefly report more than 100% when a soft limit is
    /// exceeded). Under 1% keeps one decimal, so 10 MB on a 1 TB account doesn't read "0%".
    /// </summary>
    public static string PercentString(long used, long total)
    {
        if (total <= 0 || used <= 0)
        {
            return "0%";
        }

        var clamped = Math.Clamp((double)used / total * 100, 0, 100);
        return clamped >= 1
            ? Math.Round(clamped, MidpointRounding.AwayFromZero).ToString("0", Localization.Culture) + "%"
            : clamped.ToString("0.0", Localization.Culture) + "%";
    }
}
