// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Compare;

/// <summary>What a selection in the Compare window acts on.</summary>
public static class CompareSelection
{
    /// <summary>
    /// The selected entries, leaving out any whose folder is selected too (copying or deleting
    /// the folder covers them), in their original order. Paths are relative and '/'-separated.
    ///
    /// <para>Each path's ancestors are looked up in a set of the selected folders, so the cost
    /// grows with the selection times its depth. Testing every entry against every selected
    /// folder instead made selecting all of a 20,000-row comparison hang the window.</para>
    /// </summary>
    public static List<T> Topmost<T>(IReadOnlyCollection<T> selected, Func<T, string> pathOf, Func<T, bool> isDirectory)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in selected)
        {
            if (isDirectory(item))
            {
                folders.Add(pathOf(item));
            }
        }

        var result = new List<T>(selected.Count);
        foreach (var item in selected)
        {
            if (folders.Count == 0 || !HasSelectedAncestor(pathOf(item), folders))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>The selected entries of a comparison, as <see cref="Topmost{T}"/> describes.</summary>
    public static List<CompareTreeNode> Topmost(IReadOnlyCollection<CompareTreeNode> selected)
        => Topmost(selected, n => n.Path, n => n.IsDirectory);

    /// <summary>Whether a strict ancestor of <paramref name="path"/> ("a" and "a/b" for "a/b/c") is among <paramref name="folders"/>.</summary>
    private static bool HasSelectedAncestor(string path, HashSet<string> folders)
    {
        var lookup = folders.GetAlternateLookup<ReadOnlySpan<char>>();
        var span = path.AsSpan();
        for (var slash = span.IndexOf('/'); slash >= 0; slash = NextSlash(span, slash))
        {
            if (slash > 0 && lookup.Contains(span[..slash]))
            {
                return true;
            }
        }

        return false;
    }

    private static int NextSlash(ReadOnlySpan<char> path, int previous)
    {
        var next = path[(previous + 1)..].IndexOf('/');
        return next < 0 ? -1 : previous + 1 + next;
    }
}
