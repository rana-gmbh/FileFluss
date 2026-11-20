// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Settings;

/// <summary>
/// The sidebar's widths (macOS <c>ContentView</c> / <c>SidebarResizeHandle</c>): below
/// <see cref="ExpandedMinWidth"/> a dragged sidebar snaps magnetically to either the icon-only
/// detent or back to the narrowest expanded width, switching at the midpoint between them.
/// </summary>
public static class SidebarLayout
{
    /// <summary>Icon-only: a 16 px icon centred with comfortable padding.</summary>
    public const double CompactWidth = 48;

    /// <summary>The narrowest width that still shows names — twice the compact detent, as on macOS.</summary>
    public const double ExpandedMinWidth = 96;

    /// <summary>What a double-click on the splitter restores; the same as a first launch.</summary>
    public const double DefaultWidth = 220;

    /// <summary>Anything narrower shows icons only.</summary>
    public const double CompactThreshold = (CompactWidth + ExpandedMinWidth) / 2;

    /// <summary>The width a drag to <paramref name="raw"/> settles on.</summary>
    public static double Snap(double raw)
    {
        if (double.IsNaN(raw) || raw >= ExpandedMinWidth)
        {
            return double.IsNaN(raw) ? DefaultWidth : raw;
        }

        return raw < CompactThreshold ? CompactWidth : ExpandedMinWidth;
    }

    public static bool IsCompact(double width) => width < CompactThreshold;
}
