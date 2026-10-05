// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;

namespace FileFluss.Core.Search;

/// <summary>
/// One row of a drive's index (<c>indexed_files</c>), or of a cloud account's cache when read
/// for browsing. Paths are POSIX and relative to the source's root ("/Photos/2026/a.jpg"), so
/// a drive that comes back under another letter keeps its index.
/// </summary>
public sealed record IndexedFile(
    string SourceId,
    string Path,
    string ParentPath,
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset ModificationDate)
{
    public string Id => $"{SourceId}|{Path}";

    /// <summary>The row as a panel item. The index keeps no provider ids, so the path is the id.</summary>
    public CloudFileItem ToCloudFileItem()
        => new(Path, Name, Path, IsDirectory, Size, ModificationDate)
        {
            IsHidden = CloudFileItem.IsDotHidden(Name),
        };
}

/// <summary>A row of <c>indexed_sources</c>: one fully indexed drive or cloud account.</summary>
public sealed record IndexedSource(
    string SourceId,
    string Kind,
    string DisplayName,
    DateTimeOffset? LastIndexed,
    int TotalFiles,
    long TotalBytes)
{
    public bool IsCloud => Kind == SearchIndex.CloudKind;
}

/// <summary>What a write actually did.</summary>
/// <param name="Requested">Rows handed to the index.</param>
/// <param name="Rejected">Rows the table refused, individually (a duplicate path).</param>
public sealed record IndexWriteResult(int Requested, int Rejected, int StoredFiles, int StoredFolders)
{
    public int Stored => StoredFiles + StoredFolders;

    /// <summary>True when everything offered is now in the index.</summary>
    public bool IsComplete => Rejected == 0 && Stored == Requested;
}

/// <summary>Per-account aggregate over the cloud cache (<c>cloud_files</c>).</summary>
public sealed record IndexedAccountSummary(Guid Id, DateTimeOffset LastIndexed, int TotalFiles, int TotalFolders, long TotalBytes);

/// <summary>A cloud cache hit: the account it belongs to, the item, and when it was last seen.</summary>
public sealed record IndexedCloudFile(Guid AccountId, CloudFileItem Item, DateTimeOffset LastIndexed);

/// <summary>Bulk read of the cloud cache for Compare and Storage Analysis.</summary>
public sealed record CloudIndexedFile(Guid AccountId, string Path, string Name, bool IsDirectory, long Size, DateTimeOffset ModificationDate);

/// <summary>
/// One row of Settings → Index Status: what has been indexed, when, and how much. Cloud rows
/// count from the cloud cache, drive rows from <c>indexed_files</c> — the same split the macOS
/// panel makes.
/// </summary>
public sealed record IndexStatusEntry(
    string SourceId,
    string Kind,
    string DisplayName,
    DateTimeOffset? LastIndexed,
    int TotalFiles,
    int TotalFolders,
    long TotalBytes)
{
    public bool IsCloud => Kind == SearchIndex.CloudKind;

    /// <summary>The account for a cloud row; null for drives.</summary>
    public Guid? AccountId => IsCloud && Guid.TryParse(SourceId, out var id) ? id : null;

    /// <summary>The drive kind recorded at indexing time ("drive-network" → Network); null for cloud rows.</summary>
    public DriveKind? DriveKind => DriveKinds.FromIndexKind(Kind);
}

public enum SearchIndexErrorKind
{
    OpenFailed,
    ExecutionFailed,
}

/// <summary>
/// The index could not be opened or written. Thrown by the writes that build an index — a
/// failure there used to be silent, and a user indexed 179,000 files from an SMB share into
/// an empty database with nothing saying so.
/// </summary>
public sealed class SearchIndexException : Exception
{
    public SearchIndexException(SearchIndexErrorKind kind, string detail, Exception? inner = null)
        : base(kind == SearchIndexErrorKind.OpenFailed
            ? Localization.L("Failed to open search index: {0}", detail)
            : Localization.L("Search index error: {0}", detail), inner)
    {
        Kind = kind;
        Detail = detail;
    }

    public SearchIndexErrorKind Kind { get; }

    /// <summary>SQLite's own message, unlocalized.</summary>
    public string Detail { get; }
}

/// <summary>Dates are stored as seconds since 1970, as on macOS; "unknown" is year 1 on both sides.</summary>
internal static class IndexDates
{
    /// <summary>0001-01-01T00:00:00Z — Swift's <c>Date.distantPast</c> and .NET's <c>DateTimeOffset.MinValue</c>.</summary>
    private const double UnknownSeconds = -62135596800d;

    public static double ToStored(DateTimeOffset date)
        => (date.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

    public static DateTimeOffset FromStored(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= UnknownSeconds + 1)
        {
            return CloudFileItem.UnknownDate;
        }

        var ticks = DateTimeOffset.UnixEpoch.UtcTicks + (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
        if (ticks >= DateTimeOffset.MaxValue.UtcTicks - TimeSpan.TicksPerDay)
        {
            return DateTimeOffset.MaxValue;
        }

        return new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime();
    }
}
