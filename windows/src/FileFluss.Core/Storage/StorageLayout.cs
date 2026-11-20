// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Providers;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Storage;

/// <summary>
/// The geometry and navigation rules of the Storage Analysis window, kept apart from WPF so
/// they can be tested: share-bar widths, tree indentation, where "Reveal" goes, and which row
/// is a row's parent for keyboard navigation.
/// </summary>
public static class StorageLayout
{
    /// <summary>Width of the share bar beside each tree row (macOS: 60 pt).</summary>
    public const double BarTrackWidth = 60;

    /// <summary>A sliver stays visible even for a tiny share, so "small" never reads as "missing".</summary>
    public const double MinimumBarFill = 2;

    /// <summary>Indent per tree level (macOS: 14 pt).</summary>
    public const double IndentStep = 14;

    /// <summary>The filled part of a share bar <paramref name="trackWidth"/> wide for <paramref name="fraction"/> (0…1).</summary>
    public static double BarFillWidth(double trackWidth, double fraction)
    {
        if (!(trackWidth > 0))
        {
            return 0;
        }

        var share = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
        return Math.Min(trackWidth, Math.Max(MinimumBarFill, trackWidth * share));
    }

    /// <summary>Left indent of a tree row at <paramref name="depth"/> (the root is depth 0).</summary>
    public static double Indent(int depth) => Math.Max(0, depth) * IndentStep;

    /// <summary>"12.5% of the total" — the share-bar tooltip, also used for the largest files.</summary>
    public static string ShareText(double fraction)
        => L("{0}% of the total", (Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1) * 100).ToString("0.0", Culture));

    /// <summary>
    /// Where "Reveal" takes the panel: a folder opens itself (as on macOS); a file opens its
    /// parent folder with the file selected.
    /// </summary>
    public static (string Folder, string? SelectName) RevealTarget(StorageNode node, FileProvider provider)
        => node.IsDirectory ? (node.Path, null) : (provider.GetParentPath(node.Path), node.Name);

    /// <summary>The folder a file lives in, for the second line of a "Largest files" row.</summary>
    public static string ContainingFolder(StorageNode node, FileProvider provider) => provider.GetParentPath(node.Path);

    /// <summary>
    /// The index of the row that holds <paramref name="index"/> in the flattened tree (the nearest
    /// row above it one level up), or -1 for the root. Left arrow on a collapsed row goes there.
    /// </summary>
    public static int ParentRowIndex(IReadOnlyList<StorageRow> rows, int index)
    {
        if (index <= 0 || index >= rows.Count)
        {
            return -1;
        }

        var depth = rows[index].Depth;
        for (var i = index - 1; i >= 0; i--)
        {
            if (rows[i].Depth < depth)
            {
                return i;
            }
        }

        return -1;
    }
}
