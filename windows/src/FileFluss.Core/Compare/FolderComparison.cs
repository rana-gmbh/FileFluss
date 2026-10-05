// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Sync;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Compare;

/// <summary>Outcome of comparing one entry across the two folders.</summary>
public enum FolderCompareStatus
{
    /// <summary>On both sides and they look the same.</summary>
    Identical,

    OnlyLeft,

    OnlyRight,

    /// <summary>On both sides, but the size (or, with Compare Date, the modification date) differs.</summary>
    Differs,
}

public enum CompareSide
{
    Left,
    Right,
}

/// <summary>One path present on either side or both.</summary>
/// <param name="LeftDate">Modification date on the left; null when absent or unknown.</param>
/// <param name="DateDiffersOnly">Same size, different date — only possible with Compare Date on.</param>
public sealed record FolderCompareEntry(
    string RelativePath,
    bool IsDirectory,
    FolderCompareStatus Status,
    long? LeftSize,
    long? RightSize,
    DateTimeOffset? LeftDate,
    DateTimeOffset? RightDate,
    DateTimeOffset? LeftCreated,
    DateTimeOffset? RightCreated,
    bool DateDiffersOnly)
{
    public string Id => RelativePath;

    public string Name
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? RelativePath : RelativePath[(slash + 1)..];
        }
    }

    /// <summary>Which side has the later modification date; null when either is unknown or they are within the tolerance.</summary>
    public CompareSide? NewerSide
    {
        get
        {
            if (LeftDate is not { } left || RightDate is not { } right || (left - right).Duration() <= SyncPlanner.ModificationDateTolerance)
            {
                return null;
            }

            return left > right ? CompareSide.Left : CompareSide.Right;
        }
    }

    /// <summary>The size line under the name: "1.20 MB", "Left: 1.20 MB", "Left: 1.20 MB • Right: 900 KB — different size"…; null for folders.</summary>
    public string? DetailText
    {
        get
        {
            if (IsDirectory)
            {
                return null;
            }

            switch (Status)
            {
                case FolderCompareStatus.Identical:
                    return LeftSize is { } size ? ByteFormatter.Format(size) : null;
                case FolderCompareStatus.OnlyLeft:
                    return LeftSize is { } left ? L("Left: {0}", ByteFormatter.Format(left)) : null;
                case FolderCompareStatus.OnlyRight:
                    return RightSize is { } right ? L("Right: {0}", ByteFormatter.Format(right)) : null;
                default:
                    if (LeftSize == RightSize && LeftSize is { } same)
                    {
                        return L("Same size: {0}", ByteFormatter.Format(same));
                    }

                    return L(
                        "Left: {0} • Right: {1} — different size",
                        LeftSize is { } l ? ByteFormatter.Format(l) : "—",
                        RightSize is { } r ? ByteFormatter.Format(r) : "—");
            }
        }
    }

    /// <summary>"Modified — Left: … • Right: …"; null when neither side has a date.</summary>
    public string? ModifiedDetail
        => LeftDate is null && RightDate is null ? null : L("Modified — Left: {0}  •  Right: {1}", FormatDate(LeftDate), FormatDate(RightDate));

    /// <summary>"Created — Left: … • Right: …"; null when neither side knows it (cloud listings rarely do).</summary>
    public string? CreatedDetail
        => LeftCreated is null && RightCreated is null ? null : L("Created  — Left: {0}  •  Right: {1}", FormatDate(LeftCreated), FormatDate(RightCreated));

    /// <summary>Short date and time in the app's language, as Explorer shows them; "—" when unknown.</summary>
    public static string FormatDate(DateTimeOffset? date)
        => date is { } value ? value.ToLocalTime().ToString("g", Culture) : "—";
}

/// <summary>A comparison's entries with their counts, counted once rather than on every redraw.</summary>
public sealed class FolderComparisonResult
{
    public FolderComparisonResult(IReadOnlyList<FolderCompareEntry> entries, bool comparedDates)
    {
        Entries = entries;
        ComparedDates = comparedDates;
        foreach (var entry in entries)
        {
            switch (entry.Status)
            {
                case FolderCompareStatus.Identical:
                    IdenticalCount++;
                    break;
                case FolderCompareStatus.OnlyLeft:
                    OnlyLeftCount++;
                    break;
                case FolderCompareStatus.OnlyRight:
                    OnlyRightCount++;
                    break;
                default:
                    DiffersCount++;
                    break;
            }
        }
    }

    public IReadOnlyList<FolderCompareEntry> Entries { get; }

    /// <summary>Compare Date was on: rows show the newer-side badge and both dates.</summary>
    public bool ComparedDates { get; }

    public int IdenticalCount { get; }

    public int OnlyLeftCount { get; }

    public int OnlyRightCount { get; }

    public int DiffersCount { get; }

    /// <summary>The header line: "1 different • 5 only-left • 0 only-right • 12 same", or "Folders are identical (12 items)".</summary>
    public string SummaryText
        => DiffersCount + OnlyLeftCount + OnlyRightCount == 0
            ? L("Folders are identical ({0} items)", IdenticalCount)
            : L("{0} different • {1} only-left • {2} only-right • {3} same", DiffersCount, OnlyLeftCount, OnlyRightCount, IdenticalCount);
}

public static class FolderComparison
{
    /// <summary>
    /// Compares two enumerated trees. Folders on both sides are identical (their contents are
    /// compared entry by entry); files differ by size, and — with <paramref name="compareDates"/>
    /// — by a modification date outside the 2-second tolerance.
    /// </summary>
    /// <param name="ignoreCase">Match names case-insensitively; pass true when either side is the local disk.</param>
    public static FolderComparisonResult Compare(
        IReadOnlyList<SyncEntry> left,
        IReadOnlyList<SyncEntry> right,
        bool compareDates = false,
        bool ignoreCase = false)
    {
        // Unicode-form-insensitive too: a folder written by a Mac (NFD) and its copy on a PC
        // (NFC) hold the same names, and would otherwise compare as two lists of one-sided files.
        var comparer = IO.FileNames.MatchComparer(ignoreCase);
        var leftMap = new Dictionary<string, SyncEntry>(left.Count, comparer);
        foreach (var entry in left)
        {
            leftMap[entry.RelativePath] = entry;
        }

        var rightMap = new Dictionary<string, SyncEntry>(right.Count, comparer);
        foreach (var entry in right)
        {
            rightMap[entry.RelativePath] = entry;
        }

        var paths = new HashSet<string>(leftMap.Keys, comparer);
        paths.UnionWith(rightMap.Keys);

        var entries = new List<FolderCompareEntry>(paths.Count);
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            var hasLeft = leftMap.TryGetValue(path, out var a);
            var hasRight = rightMap.TryGetValue(path, out var b);
            if (hasLeft && !hasRight)
            {
                entries.Add(new FolderCompareEntry(
                    a!.RelativePath, a.IsDirectory, FolderCompareStatus.OnlyLeft,
                    a.Size, null, Known(a.ModificationDate), null, a.CreationDate, null, false));
            }
            else if (!hasLeft && hasRight)
            {
                entries.Add(new FolderCompareEntry(
                    b!.RelativePath, b.IsDirectory, FolderCompareStatus.OnlyRight,
                    null, b.Size, null, Known(b.ModificationDate), null, b.CreationDate, false));
            }
            else if (hasLeft && hasRight)
            {
                var status = FolderCompareStatus.Identical;
                var dateOnly = false;
                if (a!.IsDirectory != b!.IsDirectory)
                {
                    // A folder on one side and a file on the other are not the same thing.
                    status = FolderCompareStatus.Differs;
                }
                else if (!a.IsDirectory)
                {
                    if (a.Size != b!.Size)
                    {
                        status = FolderCompareStatus.Differs;
                    }
                    else if (compareDates && (a.ModificationDate - b.ModificationDate).Duration() > SyncPlanner.ModificationDateTolerance)
                    {
                        status = FolderCompareStatus.Differs;
                        dateOnly = true;
                    }
                }

                entries.Add(new FolderCompareEntry(
                    a.RelativePath, a.IsDirectory, status,
                    a.Size, b!.Size, Known(a.ModificationDate), Known(b.ModificationDate), a.CreationDate, b.CreationDate, dateOnly));
            }
        }

        return new FolderComparisonResult(entries, compareDates);
    }

    private static DateTimeOffset? Known(DateTimeOffset date) => date > CloudFileItem.UnknownDate ? date : null;
}
