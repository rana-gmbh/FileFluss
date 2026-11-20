// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;

namespace FileFluss.Core.Compare;

/// <summary>Entries beneath a folder, by status.</summary>
public readonly record struct CompareCounts(int Different, int OnlyLeft, int OnlyRight, int Identical)
{
    public int Total => Different + OnlyLeft + OnlyRight + Identical;

    public CompareCounts Add(CompareCounts other)
        => new(Different + other.Different, OnlyLeft + other.OnlyLeft, OnlyRight + other.OnlyRight, Identical + other.Identical);

    internal static CompareCounts Of(FolderCompareStatus status) => status switch
    {
        FolderCompareStatus.Differs => new(1, 0, 0, 0),
        FolderCompareStatus.OnlyLeft => new(0, 1, 0, 0),
        FolderCompareStatus.OnlyRight => new(0, 0, 1, 0),
        _ => new(0, 0, 0, 1),
    };
}

/// <summary>
/// A node of the tree built from the flat comparison, so folders render as collapsible
/// groups. A synthetic node (<see cref="Entry"/> null) stands for a path component nobody's
/// entry names but which has descendants — when one side's listing lacks the folder itself.
/// Immutable once built.
/// </summary>
public sealed class CompareTreeNode
{
    private readonly List<CompareTreeNode> _children = [];

    // Children by name, so building doesn't rescan a folder's siblings for every component —
    // a folder of 10k files otherwise costs ~50M string comparisons.
    private readonly Dictionary<string, CompareTreeNode> _childrenByName = new(StringComparer.Ordinal);

    internal CompareTreeNode(string path, string name, bool isDirectory, FolderCompareEntry? entry = null)
    {
        Path = path;
        Name = name;
        IsDirectory = isDirectory;
        Entry = entry;
    }

    /// <summary>The relative path ("" for the root); stable across re-comparisons, so expansion state survives one.</summary>
    public string Path { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public FolderCompareEntry? Entry { get; internal set; }

    /// <summary>Folders first, then names in Explorer order.</summary>
    public IReadOnlyList<CompareTreeNode> Children => _children;

    /// <summary>Entries beneath this node (not counting itself), by status. Synthetic nodes contribute nothing themselves.</summary>
    public CompareCounts DescendantCounts { get; private set; }

    internal CompareTreeNode? Child(string name) => _childrenByName.GetValueOrDefault(name);

    internal void AddChild(CompareTreeNode node)
    {
        _children.Add(node);
        _childrenByName[node.Name] = node;
    }

    /// <summary>Sorts the subtree and fills in <see cref="DescendantCounts"/>; returns this node's own contribution plus its descendants'.</summary>
    internal CompareCounts Finish(IComparer<string> names)
    {
        _children.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : names.Compare(a.Name, b.Name));
        var counts = default(CompareCounts);
        foreach (var child in _children)
        {
            counts = counts.Add(child.Finish(names));
        }

        DescendantCounts = counts;
        return Entry is { } entry ? counts.Add(CompareCounts.Of(entry.Status)) : counts;
    }
}

public static class CompareTreeBuilder
{
    /// <summary>Builds the sorted tree; the root's children are the entries directly under the compared folders.</summary>
    public static CompareTreeNode Build(IReadOnlyList<FolderCompareEntry> entries)
    {
        var root = new CompareTreeNode(string.Empty, string.Empty, isDirectory: true);
        foreach (var entry in entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            var parts = entry.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var node = root;
            var pathSoFar = string.Empty;
            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                var isLast = index == parts.Length - 1;
                pathSoFar = pathSoFar.Length == 0 ? part : pathSoFar + "/" + part;
                if (node.Child(part) is { } existing)
                {
                    if (isLast)
                    {
                        existing.Entry = entry;
                    }

                    node = existing;
                }
                else
                {
                    var created = new CompareTreeNode(pathSoFar, part, isLast ? entry.IsDirectory : true, isLast ? entry : null);
                    node.AddChild(created);
                    node = created;
                }
            }
        }

        root.Finish(StringComparer.Create(Localization.Culture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering));
        return root;
    }
}
