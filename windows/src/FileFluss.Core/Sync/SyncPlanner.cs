// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using static FileFluss.Core.Localization;

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
    /// <param name="hiddenPaths">
    /// Collects the relative paths of the local hidden and system items that were skipped, so a
    /// sync can leave the other side's copies of them alone too (see <see cref="Plan"/>).
    /// </param>
    /// <exception cref="CloudProviderException">
    /// A cloud listing named an item that climbs out of the folder (<c>..</c>). The scan stops:
    /// skipping the entry would let Mirror delete it instead.
    /// </exception>
    public static async Task<IReadOnlyList<SyncEntry>> EnumerateAsync(
        SyncEndpoint endpoint,
        CancellationToken cancellationToken = default,
        Action<int>? onProgress = null,
        ICollection<string>? hiddenPaths = null)
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
                var childKey = key.Length == 0 ? item.Name : key + "/" + item.Name;
                if (local && item.IsHidden)
                {
                    hiddenPaths?.Add(childKey);
                    continue;
                }

                if (item.Role == CloudItemRole.VirtualContainer)
                {
                    // A synthetic grouping node (Google Drive's "Shared drives"): not a folder
                    // anything can be created in or deleted from.
                    continue;
                }

                if (!local && CloudPath.IsTraversalName(item.Name))
                {
                    // The name comes from the server. A hostile one could list "../../x".
                    throw CloudProviderException.CommandFailed(
                        L("The server listed an unsafe path (“{0}”), so the sync was stopped.", item.Path));
                }

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
    /// <param name="dateTolerance">How far apart two dates may be and still mean the same moment; <see cref="DateToleranceFor"/>.</param>
    /// <param name="hiddenPaths">
    /// Local items <see cref="EnumerateAsync"/> skipped as hidden or system. When the local
    /// disk is involved their counterparts on the other side are left alone as well.
    /// </param>
    public static SyncPlan Plan(
        IReadOnlyList<SyncEntry> sourceEntries,
        IReadOnlyList<SyncEntry> destinationEntries,
        SyncMode mode,
        PlanDirection direction,
        bool sourceIsCloud,
        bool destinationIsCloud,
        bool ignoreCase = false,
        TimeSpan? dateTolerance = null,
        IReadOnlyCollection<string>? hiddenPaths = null)
    {
        var tolerance = dateTolerance ?? ModificationDateTolerance;

        // Names match regardless of Unicode form (a Mac's NFD "Café" is the PC's NFC one), and —
        // when the files land on the local disk — under the name they actually get there:
        // "a:b" from a server arrives as "a_b", and matching the raw name would make every
        // mirror download the file again and then delete the copy as "extra".
        var comparer = FileNames.MatchComparer(ignoreCase);
        var sanitizeSource = !destinationIsCloud;
        string KeyOf(string sourceRelativePath) => sanitizeSource ? LocalKey(sourceRelativePath) : sourceRelativePath;
        string SourceKey(SyncEntry entry) => KeyOf(entry.RelativePath);

        // A local scan skips hidden files, so without this the cloud's ".git" and ".env" look
        // like extras and Mirror deletes them. Scanning them locally instead would upload
        // secrets nobody chose to upload, so hidden files stay out of any sync that touches
        // the local disk — dotfiles on both sides, and whatever Windows marks hidden here.
        if (!sourceIsCloud || !destinationIsCloud)
        {
            var hidden = new HashSet<string>(comparer);
            foreach (var path in hiddenPaths ?? [])
            {
                hidden.Add(path);
                hidden.Add(LocalKey(path));
            }

            bool IsHidden(SyncEntry entry)
            {
                if (entry.IsDotHidden)
                {
                    return true;
                }

                if (hidden.Count == 0)
                {
                    return false;
                }

                var parts = entry.RelativePath.Split('/');
                for (var i = 1; i <= parts.Length; i++)
                {
                    var prefix = string.Join('/', parts, 0, i);
                    if (hidden.Contains(prefix) || hidden.Contains(LocalKey(prefix)))
                    {
                        return true;
                    }
                }

                return false;
            }

            sourceEntries = sourceEntries.Where(e => !IsHidden(e)).ToList();
            destinationEntries = destinationEntries.Where(e => !IsHidden(e)).ToList();
        }

        var destinationMap = new Dictionary<string, SyncEntry>(destinationEntries.Count, comparer);
        foreach (var entry in destinationEntries)
        {
            destinationMap[entry.RelativePath] = entry;
        }

        var sourceMap = new Dictionary<string, SyncEntry>(sourceEntries.Count, comparer);
        foreach (var entry in sourceEntries)
        {
            sourceMap[SourceKey(entry)] = entry;
        }

        // Where a source path lands: folders that already exist keep the destination's own
        // spelling (its case and Unicode form — otherwise a file would go to a folder that isn't
        // there), new components are named as the destination will name them.
        string DestinationPathFor(string sourceRelativePath)
        {
            var parts = sourceRelativePath.Split('/');
            var landed = string.Empty;
            for (var i = 0; i < parts.Length; i++)
            {
                if (destinationMap.TryGetValue(KeyOf(string.Join('/', parts, 0, i + 1)), out var match))
                {
                    landed = match.RelativePath;
                    continue;
                }

                var name = sanitizeSource ? SafeLocalPath.FileName(parts[i]) : parts[i];
                landed = landed.Length == 0 ? name : landed + "/" + name;
            }

            return landed;
        }

        // Names a renamed copy must not take: anything on either side, and earlier renames.
        HashSet<string>? taken = null;

        var operations = new List<SyncOperation>();
        int adds = 0, replaces = 0, deletes = 0, folderAdds = 0, folderDeletes = 0, typeClashes = 0;
        long bytesMoved = 0;
        long netDelta = 0;

        // Ordinal order puts a folder before everything inside it, so folders are created
        // before their children.
        foreach (var entry in sourceEntries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            if (destinationMap.TryGetValue(SourceKey(entry), out var existing))
            {
                // On both sides. A folder already exists; its contents are planned one by one.
                if (entry.IsDirectory)
                {
                    continue;
                }

                switch (mode)
                {
                    case SyncMode.Mirror:
                        if (FilesLookIdentical(entry, existing, tolerance))
                        {
                            continue;
                        }

                        operations.Add(Replace(entry, existing.RelativePath));
                        replaces++;
                        bytesMoved += entry.Size;
                        netDelta += entry.Size - existing.Size;
                        break;

                    case SyncMode.Newer:
                        if (existing.IsDirectory)
                        {
                            // Replacing a folder with a file would delete the folder, and this
                            // mode promises never to delete.
                            typeClashes++;
                            continue;
                        }

                        if (entry.ModificationDate - existing.ModificationDate > tolerance)
                        {
                            operations.Add(Replace(entry, existing.RelativePath));
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

                        var unique = UniqueName(DestinationPathFor(entry.RelativePath), taken);
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
                    DestinationRelativePath = DestinationPathFor(entry.RelativePath),
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
            NetDestinationDelta: netDelta)
        {
            SourceEntryCount = sourceEntries.Count,
            DestinationEntryCount = destinationEntries.Count,
            TypeClashes = typeClashes,
        };
    }

    /// <summary>SFTP and FTP listings come from <c>ls -l</c>-style data, which shows minutes at best.</summary>
    public static readonly TimeSpan MinuteDateTolerance = TimeSpan.FromSeconds(60);

    /// <summary>How far apart two dates may be and still mean the same moment, for a sync between these two sides.</summary>
    public static TimeSpan DateToleranceFor(SyncEndpoint source, SyncEndpoint destination)
    {
        static bool UsesMinuteDates(SyncEndpoint endpoint)
            => endpoint.Provider is CloudProvider { ProviderType: CloudProviderType.Sftp or CloudProviderType.Ftp };

        return UsesMinuteDates(source) || UsesMinuteDates(destination) ? MinuteDateTolerance : ModificationDateTolerance;
    }

    /// <summary>A relative path as the local disk will spell it: each component made legal (<see cref="SafeLocalPath.FileName"/>).</summary>
    internal static string LocalKey(string relativePath)
        => string.Join('/', relativePath.Split('/').Select(SafeLocalPath.FileName));

    private static SyncOperation Replace(SyncEntry entry, string destinationRelativePath)
        => new(SyncOperationKind.Replace, entry.RelativePath, false, entry.Size)
        {
            DestinationRelativePath = destinationRelativePath,
            SourceModificationDate = entry.ModificationDate,
        };

    /// <summary>Same size and a modification date within the tolerance: treated as the same file.</summary>
    public static bool FilesLookIdentical(SyncEntry a, SyncEntry b)
        => FilesLookIdentical(a, b, ModificationDateTolerance);

    public static bool FilesLookIdentical(SyncEntry a, SyncEntry b, TimeSpan tolerance)
        => a.Size == b.Size && (a.ModificationDate - b.ModificationDate).Duration() <= tolerance;

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
