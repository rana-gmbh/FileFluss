// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Sync;

/// <summary>Which panel is the source of a sync.</summary>
public enum PlanDirection
{
    /// <summary>Left → Right: the right panel is made to match the left.</summary>
    LeftToRight,

    /// <summary>Left ← Right: the left panel is made to match the right.</summary>
    RightToLeft,
}

public enum SyncMode
{
    /// <summary>Replace the destination to match the source: add + replace + delete.</summary>
    Mirror,

    /// <summary>Add missing files; replace older ones; never delete.</summary>
    Newer,

    /// <summary>Add missing files; a clash adds the source under a unique name; never delete.</summary>
    Additive,
}

public static class SyncModeExtensions
{
    /// <summary>The order the Sync sheet lists the modes in.</summary>
    public static IReadOnlyList<SyncMode> All { get; } = [SyncMode.Mirror, SyncMode.Newer, SyncMode.Additive];

    public static string Title(this SyncMode mode) => mode switch
    {
        SyncMode.Mirror => L("Mirror (replace all)"),
        SyncMode.Newer => L("Update newer files"),
        _ => L("Add only (keep both on conflict)"),
    };

    public static string Subtitle(this SyncMode mode) => mode switch
    {
        SyncMode.Mirror => L("Destination will match source exactly. Extra files on the destination will be deleted."),
        SyncMode.Newer => L("Copies missing files and replaces files that are older than the source."),
        _ => L("Copies missing files. When a file exists on both sides, the source copy is added with a unique name."),
    };

    /// <summary>Mirror deletes on the destination, so the sheet asks for an explicit confirmation.</summary>
    public static bool IsDestructive(this SyncMode mode) => mode == SyncMode.Mirror;

    /// <summary>What the transfer list calls a running sync ("Mirroring", "Syncing newer", "Adding").</summary>
    public static TransferOperation ToTransferOperation(this SyncMode mode) => mode switch
    {
        SyncMode.Mirror => TransferOperation.Mirroring,
        SyncMode.Newer => TransferOperation.SyncingNewer,
        _ => TransferOperation.Adding,
    };
}

/// <summary>
/// A file or folder found while enumerating one side, flattened.
/// <paramref name="RelativePath"/> is relative to the side's root with <c>/</c> separators
/// whatever the provider ("sub/file.txt"), never starting with a slash, so the two sides can
/// be matched key by key.
/// </summary>
/// <param name="CreationDate">Known for local files and a few providers; null means "don't compare against it".</param>
public sealed record SyncEntry(
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTimeOffset ModificationDate,
    DateTimeOffset? CreationDate = null)
{
    public string Name
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? RelativePath : RelativePath[(slash + 1)..];
        }
    }
}

public enum SyncOperationKind
{
    /// <summary>Not on the destination — create it.</summary>
    Add,

    /// <summary>On both sides; the source overwrites the destination.</summary>
    Replace,

    /// <summary>On both sides; the source is added beside it under <see cref="SyncOperation.DestinationRelativePath"/>.</summary>
    AddRenamed,

    /// <summary>Only on the destination — deleted (mirror only).</summary>
    Delete,
}

/// <summary>One step of a <see cref="SyncPlan"/>.</summary>
/// <param name="RelativePath">The source item (for <see cref="SyncOperationKind.Delete"/>, the destination item).</param>
/// <param name="Bytes">The file's size; 0 for folders.</param>
public sealed record SyncOperation(SyncOperationKind Kind, string RelativePath, bool IsDirectory, long Bytes)
{
    /// <summary>Where it lands on the destination; differs from <see cref="RelativePath"/> only for <see cref="SyncOperationKind.AddRenamed"/>.</summary>
    public string DestinationRelativePath { get; init; } = RelativePath;

    /// <summary>The source's "Date modified", stamped on the copy so the next run sees the two as identical.</summary>
    public DateTimeOffset SourceModificationDate { get; init; } = CloudFileItem.UnknownDate;

    /// <summary>What the transfer details list it as: the path it ends up at.</summary>
    public string DisplayName => Kind == SyncOperationKind.AddRenamed ? DestinationRelativePath : RelativePath;

    /// <summary>A file that travels (add, replace or add-renamed), as opposed to a folder or a delete.</summary>
    public bool CopiesFile => !IsDirectory && Kind != SyncOperationKind.Delete;
}

/// <summary>Everything a sync will do, with the totals the Sync sheet shows before it starts.</summary>
/// <param name="DownloadBytes">Bytes read from a cloud source.</param>
/// <param name="UploadBytes">Bytes written to a cloud destination.</param>
/// <param name="TotalBytes">Bytes moved whatever the direction (the only figure for local → local).</param>
/// <param name="NetDestinationDelta">
/// Net change in the destination's used space: new files and the growth of replaced files,
/// minus the old size of replaced files and mirror deletes. Negative when a mirror frees more
/// than it adds. Drives the "After sync" projection.
/// </param>
public sealed record SyncPlan(
    SyncMode Mode,
    PlanDirection Direction,
    IReadOnlyList<SyncOperation> Operations,
    int FilesToAdd,
    int FilesToReplace,
    int FilesToDelete,
    int FoldersToAdd,
    int FoldersToDelete,
    long DownloadBytes,
    long UploadBytes,
    long TotalBytes,
    long NetDestinationDelta)
{
    public bool IsEmpty => Operations.Count == 0;
}
