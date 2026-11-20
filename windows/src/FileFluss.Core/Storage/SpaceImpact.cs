// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Formatting;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Storage;

/// <summary>
/// The projected effect of a transfer on its destination's free space — a cloud account's
/// quota or a local volume — for the copy/move pre-flight warning.
/// </summary>
/// <param name="Available">Free space now; null = unlimited or unknown, which never warns.</param>
/// <param name="AddedBytes">Net bytes the destination gains (files that overwrite existing ones free their old size).</param>
public sealed record SpaceImpact(string DestinationName, bool IsCloud, long? Available, long AddedBytes)
{
    /// <summary>Free space left after the transfer; null when unlimited.</summary>
    public long? ProjectedFree => Available - AddedBytes;

    /// <summary>The transfer would not fit in the known free space.</summary>
    public bool WouldExceed => Available is { } available && AddedBytes > available;
}

/// <summary>
/// Room a transfer needs on this PC on its way through (macOS 1.6 <c>SpaceImpact.Staging</c>).
/// Only a copy between two accounts passes through here: a download is written beside its
/// destination and an upload streams from the file. Unlike the Mac, which downloads the whole
/// batch first, Windows stages one file at a time, so <see cref="Needed"/> is the largest file.
/// </summary>
/// <param name="VolumeName">The drive the staging folder (Settings → Storage) is on, as Explorer names it.</param>
/// <param name="Free">Free space there now; null when it couldn't be read.</param>
public sealed record StagingNeed(string VolumeName, long Needed, long? Free)
{
    public bool WouldExceed => Free is { } free && Needed > free;
}

/// <summary>Measures the staging folder for <see cref="StagingNeed"/>.</summary>
public static class StagingSpace
{
    /// <summary>Test seam: free bytes for a path, per async flow so parallel tests don't see each other's.</summary>
    internal static readonly AsyncLocal<Func<string, long?>?> FreeBytesOverride = new();

    /// <summary>What staging <paramref name="needed"/> bytes would take; null when nothing passes through.</summary>
    public static StagingNeed? For(long needed)
    {
        if (needed <= 0)
        {
            return null;
        }

        var root = StagingLocation.Base();
        return new StagingNeed(VolumeName(root), needed, (FreeBytesOverride.Value ?? FreeBytes)(root));
    }

    /// <summary>"Local Disk (C:)" — which disk needs the room — or the path when that can't be told.</summary>
    public static string VolumeName(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? path : LocalFileProvider.DriveLabel(root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The verb the warning is phrased with, as on macOS.</summary>
public enum SpaceCheckVerb
{
    Copy,
    Move,
    Upload,
    Download,
}

/// <summary>
/// The copy/move space pre-flight (macOS <c>SpaceCheck</c> + <c>AppState.spaceImpact</c>),
/// and the free-space figure the sync planner projects from. Gate a transfer with it only
/// when <c>AppSettings.CheckSpaceBeforeTransfer</c> is on — summing folder sizes can take a
/// while, which is why it is off by default; show "Checking available space…" meanwhile.
/// </summary>
public static class SpaceCheck
{
    /// <summary>
    /// Free bytes at <paramref name="path"/> on <paramref name="provider"/>; null when
    /// unlimited, unknown, or the lookup failed (a quota hiccup must never block a transfer).
    /// </summary>
    public static async Task<long?> AvailableSpaceAsync(FileProvider provider, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await provider.AvailableSpaceAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Free-space lookup failed: {e.Message}", "Space");
            return null;
        }
    }

    /// <summary>The verb for a transfer between these two sides.</summary>
    public static SpaceCheckVerb VerbFor(FileProvider source, FileProvider destination, bool isMove)
    {
        if (isMove)
        {
            return SpaceCheckVerb.Move;
        }

        if (source.IsLocal && !destination.IsLocal)
        {
            return SpaceCheckVerb.Upload;
        }

        return !source.IsLocal && destination.IsLocal ? SpaceCheckVerb.Download : SpaceCheckVerb.Copy;
    }

    /// <summary>Two local paths on one volume: moving between them is a rename and costs no space.</summary>
    public static bool SameVolume(string a, string b)
    {
        try
        {
            var left = Path.GetPathRoot(Path.GetFullPath(a));
            var right = Path.GetPathRoot(Path.GetFullPath(b));
            return !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Pre-flight for a <see cref="TransferRequest"/>.</summary>
    public static Task<SpaceImpact?> ImpactAsync(TransferRequest request, string? destinationName = null, CancellationToken cancellationToken = default)
        => ImpactAsync(request.Source, request.Items, request.Destination, request.DestinationPath, request.IsMove, destinationName, cancellationToken);

    /// <summary>
    /// How copying or moving <paramref name="items"/> into <paramref name="destinationPath"/>
    /// affects the destination's free space. Null when there is nothing to check — no items,
    /// a move that consumes no space, or unlimited/unknown free space — and the caller simply
    /// proceeds; otherwise warn when <see cref="SpaceImpact.WouldExceed"/>.
    /// </summary>
    /// <param name="destinationName">How the warning names the destination: the account's name for a cloud; defaults to the folder name (local) or the provider's name.</param>
    public static async Task<SpaceImpact?> ImpactAsync(
        FileProvider source,
        IReadOnlyList<CloudFileItem> items,
        FileProvider destination,
        string destinationPath,
        bool isMove,
        string? destinationName = null,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return null;
        }

        // A move within one volume is a rename; within one account it is a server-side move.
        // Neither consumes space.
        if (isMove && source.IsLocal && destination.IsLocal && items.All(i => SameVolume(i.Path, destinationPath)))
        {
            return null;
        }

        if (isMove && !source.IsLocal && ReferenceEquals(source, destination))
        {
            return null;
        }

        var available = await AvailableSpaceAsync(destination, destinationPath, cancellationToken).ConfigureAwait(false);
        if (available is null)
        {
            // Unlimited or unknown: nothing to warn about, so don't spend time summing sizes.
            return null;
        }

        // Gross size of everything being transferred.
        long gross = 0;
        foreach (var item in items)
        {
            gross += await ItemSizeAsync(source, item, cancellationToken).ConfigureAwait(false);
        }

        // Net out top-level files that will be overwritten (and so free their old size).
        var existing = await DestinationFileSizesAsync(destination, destinationPath, cancellationToken).ConfigureAwait(false);
        long overwritten = 0;
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.Name, out var prior))
            {
                overwritten += prior;
            }
        }

        var name = destinationName ?? DefaultName(destination, destinationPath);
        return new SpaceImpact(name, !destination.IsLocal, available, Math.Max(0, gross - overwritten));
    }

    private static string DefaultName(FileProvider destination, string path)
    {
        if (destination is CloudProvider cloud)
        {
            return cloud.ProviderType.DisplayName();
        }

        var name = destination.GetName(path);
        return name.Length > 0 ? name : path;
    }

    private static async Task<long> ItemSizeAsync(FileProvider provider, CloudFileItem item, CancellationToken ct)
    {
        if (!item.IsDirectory)
        {
            return item.Size;
        }

        try
        {
            return await provider.FolderSizeAsync(item.Path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Folder size for the space check failed: {e.Message}", "Space");
            return 0;
        }
    }

    /// <summary>
    /// Name → size of the files directly in the destination folder. Only file overwrites are
    /// netted out: sizing every same-named destination folder would dominate the check and
    /// rarely changes the answer. Best-effort — empty on failure (the check is then gross).
    /// </summary>
    private static async Task<Dictionary<string, long>> DestinationFileSizesAsync(FileProvider destination, string path, CancellationToken ct)
    {
        var sizes = new Dictionary<string, long>(destination.IsLocal ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            foreach (var entry in await destination.ListDirectoryAsync(path, ct).ConfigureAwait(false))
            {
                if (!entry.IsDirectory)
                {
                    sizes[entry.Name] = entry.Size;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            sizes.Clear();
        }

        return sizes;
    }
}

/// <summary>Texts of the "Not enough space" alert.</summary>
public static class SpaceImpactFormatter
{
    public static string Title => L("Not enough space");

    /// <summary>The overlay shown while sizes are summed.</summary>
    public static string CheckingText => L("Checking available space…");

    public static string VerbText(SpaceCheckVerb verb) => verb switch
    {
        SpaceCheckVerb.Move => L("Move"),
        SpaceCheckVerb.Upload => L("Upload"),
        SpaceCheckVerb.Download => L("Download"),
        _ => L("Copy"),
    };

    /// <summary>"Move 4.20 GB into Dropbox would exceed the available space (only 1.10 GB free). Continue anyway?"</summary>
    public static string Warning(SpaceImpact impact, SpaceCheckVerb verb)
    {
        var added = ByteFormatter.Format(impact.AddedBytes);
        var location = impact.IsCloud ? impact.DestinationName : $"\"{impact.DestinationName}\"";
        var verbText = VerbText(verb);
        return impact.Available is { } available
            ? L("{0} {1} into {2} would exceed the available space (only {3} free). Continue anyway?", verbText, added, location, ByteFormatter.Format(Math.Max(0, available)))
            : L("{0} {1} into {2} may exceed the available space. Continue anyway?", verbText, added, location);
    }

    /// <summary>The destructive button: "Move Anyway".</summary>
    public static string ProceedButton(SpaceCheckVerb verb) => L("{0} Anyway", VerbText(verb));
}

/// <summary>A local volume's free space the way cloud accounts show their quota: "120 GB free of 500 GB".</summary>
public static class LocalVolumeFormatter
{
    public static string Summary(long free, long total)
        => L("{0} free of {1}", ByteFormatter.Format(Math.Max(0, free)), ByteFormatter.Format(Math.Max(0, total)));
}
