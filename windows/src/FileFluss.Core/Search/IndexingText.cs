// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Search;

/// <summary>How the UI words an index's age, with the macOS strings.</summary>
public static class IndexingText
{
    /// <summary>The offline view's subtitle: "Never indexed", "Indexed today", "Indexed 1 day ago", "Indexed 3 days ago".</summary>
    public static string LastIndexed(DateTimeOffset? lastIndexed, DateTimeOffset now)
    {
        if (lastIndexed is not { } date)
        {
            return Localization.L("Never indexed");
        }

        var days = (int)((now - date).TotalSeconds / 86_400);
        return days switch
        {
            <= 0 => Localization.L("Indexed today"),
            1 => Localization.L("Indexed 1 day ago"),
            _ => Localization.L("Indexed {0} days ago", days),
        };
    }

    /// <summary>
    /// The sidebar context menu's disabled first line for an indexed drive or account:
    /// "Indexed today · 12,304 files", "Indexed 3 days ago · 12,304 files".
    /// </summary>
    public static string IndexedSummary(DateTimeOffset date, int files, DateTimeOffset now)
    {
        var seconds = (now - date).TotalSeconds;
        var when = seconds switch
        {
            < 60 => Localization.L("just now"),
            < 86_400 => Localization.L("today"),
            < 86_400 * 2 => Localization.L("yesterday"),
            _ => Localization.L("{0} days ago", (int)(seconds / 86_400)),
        };
        var count = files.ToString("N0", Localization.Culture);
        return Localization.L("Indexed {0}", Localization.L("{0} · {1} files", when, count));
    }

    /// <summary>"Not indexed yet" (drives) / "Not fully indexed yet" (accounts only ever browsed).</summary>
    public static string NotIndexed(IndexingJobKind kind)
        => kind == IndexingJobKind.Cloud ? Localization.L("Not fully indexed yet") : Localization.L("Not indexed yet");
}
