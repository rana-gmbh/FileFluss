// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Models;

/// <summary>
/// Tile dimensions of the gallery, in device-independent units. They follow the list's row
/// size, so Ctrl+Plus and Ctrl+Minus make the tiles bigger and smaller as well (macOS
/// <c>FileGalleryMetrics</c>, which follows ⌘+ / ⌘-).
/// </summary>
public readonly record struct GalleryTileMetrics(double ImageSize, double TileWidth, double TileHeight)
{
    /// <summary>Room around the image for the selection, as on macOS.</summary>
    public const double Padding = 12;

    /// <summary>Two lines of the 12-px UI font, the most a name gets below its image.</summary>
    public const double LabelHeight = 34;

    public static GalleryTileMetrics For(FileListRowSize size)
    {
        var image = size switch
        {
            FileListRowSize.Small => 80.0,
            FileListRowSize.Large => 160.0,
            _ => 112.0,
        };
        return new GalleryTileMetrics(image, image + 2 * Padding, image + Padding + 6 + LabelHeight + 8);
    }
}

/// <summary>
/// Where the gallery's tiles go: a grid of equal cells, as many columns as fit the width, the
/// cells stretched to share the leftover space so the rows end flush. Pure arithmetic, so the
/// virtualizing panel that uses it stays thin and this stays testable off Windows.
/// </summary>
public readonly record struct GalleryLayout
{
    public GalleryLayout(double viewportWidth, double tileWidth, double tileHeight, int count)
    {
        TileWidth = Math.Max(1, tileWidth);
        TileHeight = Math.Max(1, tileHeight);
        Count = Math.Max(0, count);
        var width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : TileWidth;
        Columns = Math.Max(1, (int)Math.Floor(width / TileWidth));
        CellWidth = Math.Max(TileWidth, width / Columns);
        Rows = (Count + Columns - 1) / Columns;
    }

    public double TileWidth { get; }

    public double TileHeight { get; }

    public int Count { get; }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>A tile's cell: at least the tile's width, more when the row has room to spare.</summary>
    public double CellWidth { get; }

    public double ExtentHeight => Rows * TileHeight;

    public double ExtentWidth => Columns * CellWidth;

    /// <summary>The top-left corner of the item's cell.</summary>
    public (double X, double Y) PositionOf(int index)
    {
        var row = index / Columns;
        var column = index % Columns;
        return (column * CellWidth, row * TileHeight);
    }

    /// <summary>
    /// The items whose cells meet the visible band, plus <paramref name="extraRows"/> rows
    /// above and below so the arrow keys find the next tile already there. Empty (first &gt;
    /// last) when there is nothing to show.
    /// </summary>
    public (int First, int Last) VisibleRange(double offset, double viewportHeight, int extraRows = 1)
    {
        if (Count == 0)
        {
            return (0, -1);
        }

        var top = Math.Max(0, offset);
        var bottom = top + Math.Max(0, viewportHeight);
        var firstRow = Math.Max(0, (int)Math.Floor(top / TileHeight) - extraRows);

        // The row the band's bottom edge falls in; a row starting exactly there isn't visible.
        var lastVisibleRow = Math.Max(0, (int)Math.Ceiling(bottom / TileHeight) - 1);
        var lastRow = Math.Min(Rows - 1, lastVisibleRow + extraRows);
        if (lastRow < firstRow)
        {
            return (0, -1);
        }

        return (firstRow * Columns, Math.Min(Count - 1, (lastRow * Columns) + Columns - 1));
    }

    /// <summary>The largest offset that still fills the viewport.</summary>
    public double MaxOffset(double viewportHeight) => Math.Max(0, ExtentHeight - Math.Max(0, viewportHeight));

    /// <summary>
    /// The scroll offset that brings the item's row into view, moving as little as possible:
    /// unchanged when the row is already fully visible.
    /// </summary>
    public double OffsetToShow(int index, double offset, double viewportHeight)
    {
        if (Count == 0)
        {
            return 0;
        }

        var top = (Math.Clamp(index, 0, Count - 1) / Columns) * TileHeight;
        var bottom = top + TileHeight;
        var target = offset;
        if (top < offset)
        {
            target = top;
        }
        else if (bottom > offset + viewportHeight)
        {
            target = bottom - viewportHeight;
        }

        return Math.Clamp(target, 0, MaxOffset(viewportHeight));
    }
}
