// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.VirtualFiles;

/// <summary>
/// One file or folder handed to another app as a "virtual file": its Windows-safe path
/// relative to the drop folder (<c>Photos\2026\a.jpg</c>), and where its bytes come from.
/// </summary>
/// <param name="RelativePath">Backslash-separated, every component made safe by <see cref="SafeLocalPath.FileName"/>.</param>
/// <param name="RemotePath">The provider path to download (folders: the folder).</param>
/// <param name="Size">Bytes, or null when unknown — an exported Google document is not the size its listing reports.</param>
/// <param name="Modified">Null when the provider reports no date.</param>
/// <param name="Created">Null when the provider reports no date.</param>
public sealed record VirtualFileEntry(
    string RelativePath,
    string RemotePath,
    bool IsDirectory,
    long? Size,
    DateTimeOffset? Modified,
    DateTimeOffset? Created)
{
    /// <summary>The last path component.</summary>
    public string Name => RelativePath[(RelativePath.LastIndexOf('\\') + 1)..];
}

public enum VirtualFileTreeProblem
{
    /// <summary>More entries than <see cref="VirtualFileTree.MaxEntries"/>.</summary>
    TooManyItems,

    /// <summary>A relative path longer than a FILEDESCRIPTOR can carry.</summary>
    PathTooLong,
}

/// <summary>Why a selection can't be handed over as virtual files; the user copies it with a local panel instead.</summary>
public sealed class VirtualFileTreeException(VirtualFileTreeProblem problem)
    : Exception(problem == VirtualFileTreeProblem.TooManyItems ? "Too many items for a virtual-file transfer." : "A path is too long for a virtual-file transfer.")
{
    public VirtualFileTreeProblem Problem { get; } = problem;
}

/// <summary>
/// The flattened tree behind a drag or copy out of a cloud panel: every folder before its
/// contents (Explorer creates folders from the folder entries, so empty ones survive), in the
/// order the descriptor lists them.
/// </summary>
public sealed class VirtualFileTree
{
    /// <summary>
    /// Above this the transfer is refused: Explorer asks for each file one at a time with no
    /// way to batch, and a descriptor of tens of thousands of entries makes it stall before the
    /// first byte. FileFluss's own transfer engine handles any size.
    /// </summary>
    public const int MaxEntries = 5000;

    /// <summary>A FILEDESCRIPTORW name is MAX_PATH (260) UTF-16 units including the terminator.</summary>
    public const int MaxRelativePathLength = 259;

    public VirtualFileTree(IReadOnlyList<CloudFileItem> topLevel, IReadOnlyList<VirtualFileEntry> entries)
    {
        TopLevel = topLevel;
        Entries = entries;
        FileCount = entries.Count(e => !e.IsDirectory);
        KnownBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size ?? 0);
    }

    public IReadOnlyList<CloudFileItem> TopLevel { get; }

    public IReadOnlyList<VirtualFileEntry> Entries { get; }

    public int FileCount { get; }

    /// <summary>The sum of the sizes that are known.</summary>
    public long KnownBytes { get; }

    /// <summary>
    /// Lists <paramref name="items"/> recursively. Folders are listed a few at a time; the walk
    /// stops as soon as the tree outgrows <paramref name="maxEntries"/> or a path gets too long,
    /// so a huge folder costs no more than the listings needed to find that out.
    /// </summary>
    /// <exception cref="VirtualFileTreeException">Too many entries, or a path too long.</exception>
    public static async Task<VirtualFileTree> BuildAsync(
        FileProvider provider,
        IReadOnlyList<CloudFileItem> items,
        int maxEntries = MaxEntries,
        CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var builder = new Builder(provider, maxEntries, cancellation);
        var entries = await builder.ChildrenAsync(items, string.Empty).ConfigureAwait(false);
        return new VirtualFileTree(items, entries);
    }

    /// <summary>
    /// "name (2).ext" for the second "name.ext" in one folder. Windows folds case and the
    /// sanitising can merge names ("a:b" and "a_b"), so names that were distinct in the cloud
    /// can collide on disk; Explorer would otherwise ask to replace one with the other.
    /// </summary>
    public static string UniqueName(string name, ISet<string> taken)
    {
        if (taken.Add(name))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private sealed class Builder(FileProvider provider, int maxEntries, CancellationTokenSource cancellation)
    {
        private const int ParallelListings = 4;
        private readonly SemaphoreSlim _listings = new(ParallelListings);
        private readonly CancellationToken cancellationToken = cancellation.Token;
        private int _count;

        /// <summary>The entries for <paramref name="items"/> (one folder's children, or the selection) and everything below them.</summary>
        public async Task<List<VirtualFileEntry>> ChildrenAsync(IReadOnlyList<CloudFileItem> items, string parent)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var named = new List<(CloudFileItem Item, VirtualFileEntry Entry)>();
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Role == CloudItemRole.VirtualContainer)
                {
                    continue;
                }

                var exported = item.IsDirectory ? null : await provider.ExportExtensionAsync(item, cancellationToken).ConfigureAwait(false);
                var name = UniqueName(SafeLocalPath.FileName(exported is null ? item.Name : item.Name + "." + exported), taken);
                var relative = parent.Length == 0 ? name : parent + "\\" + name;
                if (relative.Length > MaxRelativePathLength)
                {
                    throw new VirtualFileTreeException(VirtualFileTreeProblem.PathTooLong);
                }

                if (Interlocked.Increment(ref _count) > maxEntries)
                {
                    throw new VirtualFileTreeException(VirtualFileTreeProblem.TooManyItems);
                }

                named.Add((item, new VirtualFileEntry(
                    relative,
                    item.Path,
                    item.IsDirectory,
                    item.IsDirectory || exported is not null ? null : item.Size,
                    item.HasKnownModificationDate ? item.ModificationDate : null,
                    item.CreationDate > CloudFileItem.UnknownDate ? item.CreationDate : null)));
            }

            // Sub-folders are walked concurrently; each result lands in its own slot so the
            // order stays "folder, then its contents" whatever finishes first.
            var subtrees = named
                .Select(n => n.Item.IsDirectory ? FolderAsync(n.Item, n.Entry.RelativePath) : Task.FromResult(new List<VirtualFileEntry>()))
                .ToList();
            try
            {
                await Task.WhenAll(subtrees).ConfigureAwait(false);
            }
            catch
            {
                // Surface the first real problem, not the cancellations it caused in sibling walks.
                var failure = subtrees.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!).FirstOrDefault(e => e is not OperationCanceledException);
                if (failure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                }

                throw;
            }

            var result = new List<VirtualFileEntry>();
            for (var i = 0; i < named.Count; i++)
            {
                result.Add(named[i].Entry);
                result.AddRange(subtrees[i].Result);
            }

            return result;
        }

        private async Task<List<VirtualFileEntry>> FolderAsync(CloudFileItem folder, string relative)
        {
            try
            {
                IReadOnlyList<CloudFileItem> children;
                await _listings.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    children = await provider.ListDirectoryAsync(folder.Path, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _listings.Release();
                }

                return await ChildrenAsync(children, relative).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One failure decides the outcome; stop the sibling walks instead of finishing them.
                await cancellation.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
