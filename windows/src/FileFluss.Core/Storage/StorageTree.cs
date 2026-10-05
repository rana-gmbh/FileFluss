// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Storage;

/// <summary>
/// One entry of a storage analysis: a file, or a folder with everything beneath it rolled up.
///
/// <para>Folder sizes are always summed from the files underneath, never taken from what a
/// provider reports for a directory — most report 0, some a stale or capped figure, and a
/// size view that quietly under-reports is worse than none.</para>
///
/// <para>Built once on a worker thread and never changed afterwards, so the UI can read it
/// from any thread. A partial report during a scan is a separate, newer tree.</para>
/// </summary>
public sealed class StorageNode
{
    private readonly List<StorageNode> _children = [];

    internal StorageNode(string name, string path, bool isDirectory, long size = 0, int fileCount = 0, DateTimeOffset? modificationDate = null)
    {
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
        Size = size;
        FileCount = fileCount;
        ModificationDate = modificationDate;
    }

    /// <summary>The provider path; stable across partial reports, so expansion state survives a refresh.</summary>
    public string Id => Path;

    public string Name { get; }

    public string Path { get; }

    public bool IsDirectory { get; }

    /// <summary>Rolled-up bytes for a folder; the file's own size for a file.</summary>
    public long Size { get; private set; }

    /// <summary>Files at any depth beneath a folder; 1 for a file.</summary>
    public int FileCount { get; private set; }

    public DateTimeOffset? ModificationDate { get; }

    /// <summary>Biggest first — the order the user wants to read them in.</summary>
    public IReadOnlyList<StorageNode> Children => _children;

    /// <summary>This node's share of <paramref name="total"/>, 0…1, for the size bars.</summary>
    public double Fraction(long total) => total > 0 ? Math.Min(1, (double)Size / total) : 0;

    internal void Add(StorageNode child) => _children.Add(child);

    /// <summary>Depth-first roll-up; children end up sorted by size, then name.</summary>
    internal void RollUp(IComparer<string> names)
    {
        if (!IsDirectory)
        {
            FileCount = 1;
            return;
        }

        long total = 0;
        var files = 0;
        foreach (var child in _children)
        {
            child.RollUp(names);
            total += child.Size;
            files += child.FileCount;
        }

        Size = total;
        FileCount = files;
        _children.Sort((a, b) => a.Size == b.Size ? names.Compare(a.Name, b.Name) : b.Size.CompareTo(a.Size));
    }
}

/// <summary>A finished (or partial) analysis.</summary>
/// <param name="LargestFiles">The biggest individual files, sorted, so "which single file is huge" needs no drilling.</param>
/// <param name="WasCancelled">Stopped early; the totals are then a lower bound.</param>
public sealed record StorageReport(
    StorageNode Root,
    IReadOnlyList<StorageNode> LargestFiles,
    long TotalBytes,
    int FileCount,
    int FolderCount,
    bool WasCancelled);

/// <summary>A flat listing row the tree is built from.</summary>
public sealed record StorageEntry(string Path, string Name, bool IsDirectory, long Size, DateTimeOffset? ModificationDate);

/// <summary>
/// Builds the tree from a flat list of entries. Accepts them in any order and tolerates
/// missing intermediate folders, which is normal: a listing can yield files whose parent
/// folders were never listed themselves.
/// </summary>
public static class StorageTreeBuilder
{
    public const int DefaultLargestFileLimit = 250;

    /// <summary>Explorer-like name order (numbers compare as numbers) for equal sizes.</summary>
    internal static IComparer<string> NameComparer
        => StringComparer.Create(Localization.Culture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    /// <param name="provider">Supplies the path syntax (native Windows paths or POSIX).</param>
    public static StorageReport Build(
        IReadOnlyList<StorageEntry> entries,
        string rootPath,
        string rootName,
        FileProvider provider,
        bool wasCancelled = false,
        int largestFileLimit = DefaultLargestFileLimit)
    {
        var local = provider.IsLocal;
        var rootKey = Normalize(rootPath, local);
        if (rootKey.Length == 0 && !local)
        {
            rootKey = "/";
        }

        var root = new StorageNode(rootName, rootKey, isDirectory: true);
        var folders = new Dictionary<string, StorageNode>(local ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [rootKey] = root,
        };
        var files = new List<StorageNode>();

        // Folders first (shallow before deep), so a file never has to invent a parent that
        // the listing is about to provide properly.
        var sorted = entries
            .OrderBy(e => e.IsDirectory ? 0 : 1)
            .ThenBy(e => e.Path.Length);

        foreach (var entry in sorted)
        {
            var path = Normalize(entry.Path, local);
            if (folders.Comparer.Equals(path, rootKey))
            {
                continue;
            }

            var parent = ParentOf(path, rootKey, provider);
            if (entry.IsDirectory)
            {
                if (folders.ContainsKey(path))
                {
                    continue;
                }

                var node = new StorageNode(entry.Name, path, isDirectory: true, modificationDate: entry.ModificationDate);
                folders[path] = node;
                EnsureFolder(parent, folders, root, provider).Add(node);
            }
            else
            {
                var node = new StorageNode(entry.Name, path, isDirectory: false, entry.Size, 1, entry.ModificationDate);
                EnsureFolder(parent, folders, root, provider).Add(node);
                files.Add(node);
            }
        }

        root.RollUp(NameComparer);
        var largest = files.OrderByDescending(f => f.Size).Take(largestFileLimit).ToList();
        return new StorageReport(root, largest, root.Size, files.Count, Math.Max(0, folders.Count - 1), wasCancelled);
    }

    /// <summary>
    /// The folder node for <paramref name="path"/>, creating placeholders for any missing
    /// level — a provider that lists files without their folders would otherwise lose whole branches.
    /// </summary>
    private static StorageNode EnsureFolder(string path, Dictionary<string, StorageNode> folders, StorageNode root, FileProvider provider)
    {
        if (folders.TryGetValue(path, out var existing))
        {
            return existing;
        }

        var name = provider.GetName(path);
        var node = new StorageNode(name.Length == 0 ? path : name, path, isDirectory: true);
        folders[path] = node;
        var parent = ParentOf(path, root.Path, provider);

        // A malformed path must not walk past the root forever.
        var container = folders.Comparer.Equals(parent, path) ? root : EnsureFolder(parent, folders, root, provider);
        container.Add(node);
        return node;
    }

    private static string ParentOf(string path, string root, FileProvider provider)
    {
        var parent = Normalize(provider.GetParentPath(path), provider.IsLocal);
        return parent.Length == 0 || parent.Length < root.Length ? root : parent;
    }

    private static string Normalize(string path, bool local)
    {
        if (local)
        {
            return Path.TrimEndingDirectorySeparator(path);
        }

        var trimmed = path;
        while (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }
}
