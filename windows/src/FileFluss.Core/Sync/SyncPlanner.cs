// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;

namespace FileFluss.Core.Sync;

/// <summary>
/// Builds sync plans: enumerates both endpoints, then diffs the two flat lists according to
/// the chosen mode. Compare Folders uses the same enumeration.
/// </summary>
public static class SyncPlanner
{
    /// <summary>
    /// Cloud APIs truncate timestamps to whole seconds and FAT-like file systems have
    /// 2-second granularity, so dates within this window count as equal. Compare uses the same.
    /// </summary>
    public static readonly TimeSpan ModificationDateTolerance = TimeSpan.FromSeconds(2);

    // MARK: - Enumeration

    /// <summary>
    /// Everything beneath the endpoint, flattened, parents before children. Local hidden and
    /// system items are skipped (macOS skipped hidden files the same way), and a junction or
    /// symlinked folder is listed but not entered — following one can loop forever. A folder
    /// that can't be listed fails the whole enumeration: a plan built on a partial listing
    /// would delete or re-copy what it didn't see.
    /// </summary>
    /// <param name="onProgress">Called with the running entry count after each folder, from the enumerating thread.</param>
    public static async Task<IReadOnlyList<SyncEntry>> EnumerateAsync(
        SyncEndpoint endpoint,
        CancellationToken cancellationToken = default,
        Action<int>? onProgress = null)
    {
        if (!endpoint.IsUsable)
        {
            throw new InvalidOperationException("\"This PC\" can't be enumerated as a folder.");
        }

        var provider = endpoint.Provider;
        var local = provider.IsLocal;
        var results = new List<SyncEntry>();
        var pending = new Queue<(string Path, string Key)>();
        pending.Enqueue((endpoint.Path, string.Empty));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, key) = pending.Dequeue();
            var items = await provider.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                if (local && item.IsHidden)
                {
                    continue;
                }

                if (item.Role == CloudItemRole.VirtualContainer)
                {
                    // A synthetic grouping node (Google Drive's "Shared drives"): not a folder
                    // anything can be created in or deleted from.
                    continue;
                }

                var childKey = key.Length == 0 ? item.Name : key + "/" + item.Name;
                results.Add(new SyncEntry(
                    childKey,
                    item.IsDirectory,
                    item.IsDirectory ? 0 : item.Size,
                    item.ModificationDate,
                    item.CreationDate > CloudFileItem.UnknownDate ? item.CreationDate : null));

                if (item.IsDirectory && !(local && item.IsSymlink))
                {
                    pending.Enqueue((item.Path, childKey));
                }
            }

            onProgress?.Invoke(results.Count);
        }

        return results;
    }

    // MARK: - Planning

    /// <summary>
    /// The operations that make the destination match the source under <paramref name="mode"/>.
    /// </summary>
    /// <param name="ignoreCase">
    /// Match names case-insensitively. Pass true whenever either side is the local disk: NTFS
    /// treats "Photo.JPG" and "photo.jpg" as one file, and a case-sensitive mirror would copy
    /// one onto the other and then delete the result as "extra".
    /// </param>
    public static SyncPlan Plan(
        IReadOnlyList<SyncEntry> sourceEntries,
        IReadOnlyList<SyncEntry> destinationEntries,
        SyncMode mode,
        PlanDirection direction,
        bool sourceIsCloud,
        bool destinationIsCloud,
        bool ignoreCase = false)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var destinationMap = new Dictionary<string, SyncEntry>(destinationEntries.Count, comparer);
        foreach (var entry in destinationEntries)
        {
            destinationMap[entry.RelativePath] = entry;
        }

        var sourceMap = new Dictionary<string, SyncEntry>(sourceEntries.Count, comparer);
        foreach (var entry in sourceEntries)
        {
            sourceMap[entry.RelativePath] = entry;
        }

        // Names a renamed copy must not take: anything on either side, and earlier renames.
        HashSet<string>? taken = null;

        var operations = new List<SyncOperation>();
        int adds = 0, replaces = 0, deletes = 0, folderAdds = 0, folderDeletes = 0;
        long bytesMoved = 0;
        long netDelta = 0;

        // Ordinal order puts a folder before everything inside it, so folders are created
        // before their children.
        foreach (var entry in sourceEntries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            if (destinationMap.TryGetValue(entry.RelativePath, out var existing))
            {
                // On both sides. A folder already exists; its contents are planned one by one.
                if (entry.IsDirectory)
                {
                    continue;
                }

                switch (mode)
                {
                    case SyncMode.Mirror:
                        if (FilesLookIdentical(entry, existing))
                        {
                            continue;
                        }

                        operations.Add(Replace(entry));
                        replaces++;
                        bytesMoved += entry.Size;
                        netDelta += entry.Size - existing.Size;
                        break;

                    case SyncMode.Newer:
                        if (entry.ModificationDate - existing.ModificationDate > ModificationDateTolerance)
                        {
                            operations.Add(Replace(entry));
                            replaces++;
                            bytesMoved += entry.Size;
                            netDelta += entry.Size - existing.Size;
                        }

                        break;

                    case SyncMode.Additive:
                        if (taken is null)
                        {
                            taken = new HashSet<string>(destinationMap.Keys, comparer);
                            taken.UnionWith(sourceMap.Keys);
                        }

                        var unique = UniqueName(entry.RelativePath, taken);
                        taken.Add(unique);
                        operations.Add(new SyncOperation(SyncOperationKind.AddRenamed, entry.RelativePath, false, entry.Size)
                        {
                            DestinationRelativePath = unique,
                            SourceModificationDate = entry.ModificationDate,
                        });
                        adds++;
                        bytesMoved += entry.Size;
                        netDelta += entry.Size;
                        break;
                }
            }
            else
            {
                operations.Add(new SyncOperation(SyncOperationKind.Add, entry.RelativePath, entry.IsDirectory, entry.Size)
                {
                    SourceModificationDate = entry.ModificationDate,
                });
                if (entry.IsDirectory)
                {
                    folderAdds++;
                }
                else
                {
                    adds++;
                }

                bytesMoved += entry.Size;
                netDelta += entry.Size;
            }
        }

        if (mode == SyncMode.Mirror)
        {
            // Extra items on the destination go, deepest first so children precede their parents.
            var extra = destinationEntries
                .Where(e => !sourceMap.ContainsKey(e.RelativePath))
                .OrderByDescending(e => e.RelativePath.Length)
                .ThenBy(e => e.RelativePath, StringComparer.Ordinal);
            foreach (var entry in extra)
            {
                operations.Add(new SyncOperation(SyncOperationKind.Delete, entry.RelativePath, entry.IsDirectory, entry.Size));
                if (entry.IsDirectory)
                {
                    folderDeletes++;
                }
                else
                {
                    deletes++;
                }

                netDelta -= entry.Size;
            }
        }

        return new SyncPlan(
            mode,
            direction,
            operations,
            adds,
            replaces,
            deletes,
            folderAdds,
            folderDeletes,
            DownloadBytes: sourceIsCloud ? bytesMoved : 0,
            UploadBytes: destinationIsCloud ? bytesMoved : 0,
            TotalBytes: bytesMoved,
            NetDestinationDelta: netDelta);
    }

    private static SyncOperation Replace(SyncEntry entry)
        => new(SyncOperationKind.Replace, entry.RelativePath, false, entry.Size) { SourceModificationDate = entry.ModificationDate };

    /// <summary>Same size and a modification date within the tolerance: treated as the same file.</summary>
    public static bool FilesLookIdentical(SyncEntry a, SyncEntry b)
        => a.Size == b.Size && (a.ModificationDate - b.ModificationDate).Duration() <= ModificationDateTolerance;

    /// <summary>"sub/name (2).ext" — the first free "name (n)" in the same folder.</summary>
    internal static string UniqueName(string relativePath, ISet<string> taken)
    {
        var slash = relativePath.LastIndexOf('/');
        var folder = slash >= 0 ? relativePath[..(slash + 1)] : string.Empty;
        var name = relativePath[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        for (var index = 2; ; index++)
        {
            var candidate = $"{folder}{stem} ({index}){extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
