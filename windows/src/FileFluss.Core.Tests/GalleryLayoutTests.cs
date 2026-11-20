// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;

namespace FileFluss.Core.Tests;

/// <summary>
/// The gallery's grid (macOS gallery view): equal tiles, as many columns as fit, and only the
/// rows on screen realized — the arithmetic the virtualizing panel runs on.
/// </summary>
public sealed class GalleryLayoutTests
{
    [Fact]
    public void AsManyColumnsAsFit_TheCellsShareTheRest()
    {
        var layout = new GalleryLayout(viewportWidth: 500, tileWidth: 136, tileHeight: 172, count: 10);

        Assert.Equal(3, layout.Columns);
        Assert.Equal(500.0 / 3, layout.CellWidth, 6);
        Assert.Equal(4, layout.Rows);
        Assert.Equal(4 * 172, layout.ExtentHeight);
        Assert.Equal((2 * (500.0 / 3), 172.0), layout.PositionOf(5));
    }

    [Fact]
    public void ANarrowPanelStillShowsOneColumn()
    {
        var layout = new GalleryLayout(viewportWidth: 60, tileWidth: 136, tileHeight: 172, count: 3);

        Assert.Equal(1, layout.Columns);
        Assert.Equal(136, layout.CellWidth);
        Assert.Equal(3, layout.Rows);
    }

    [Fact]
    public void AnUnmeasuredWidthLaysOutOneTileWide()
    {
        var layout = new GalleryLayout(double.PositiveInfinity, 136, 172, 4);
        Assert.Equal(1, layout.Columns);
    }

    [Fact]
    public void OnlyTheVisibleRowsAndOneEitherSideAreRealized()
    {
        var layout = new GalleryLayout(viewportWidth: 410, tileWidth: 136, tileHeight: 100, count: 30_000);

        // 3 columns; scrolled to row 50, a 250-high viewport shows rows 50–52.
        var (first, last) = layout.VisibleRange(offset: 5000, viewportHeight: 250);

        Assert.Equal(49 * 3, first);
        Assert.Equal((53 * 3) + 2, last);
        Assert.True(last - first < 20);
    }

    [Fact]
    public void TheRangeStopsAtTheEnds()
    {
        var layout = new GalleryLayout(410, 136, 100, count: 7);

        Assert.Equal((0, 6), layout.VisibleRange(0, 10_000));
        Assert.Equal((0, -1), new GalleryLayout(410, 136, 100, 0).VisibleRange(0, 500));
    }

    [Fact]
    public void ScrollingToAnItemMovesAsLittleAsPossible()
    {
        var layout = new GalleryLayout(410, 136, 100, count: 300); // 3 columns, 100 rows

        // Already visible: nothing moves.
        Assert.Equal(1000, layout.OffsetToShow(index: 31, offset: 1000, viewportHeight: 300));

        // Above: its row comes to the top.
        Assert.Equal(500, layout.OffsetToShow(index: 15, offset: 1000, viewportHeight: 300));

        // Below: its row comes to the bottom.
        Assert.Equal(1700 - 300 + 100, layout.OffsetToShow(index: 51, offset: 1000, viewportHeight: 300));

        // Never past the end.
        Assert.Equal(layout.MaxOffset(300), layout.OffsetToShow(index: 299, offset: 0, viewportHeight: 300));
    }

    [Fact]
    public void BiggerRowsMakeBiggerTiles()
    {
        var small = GalleryTileMetrics.For(FileListRowSize.Small);
        var medium = GalleryTileMetrics.For(FileListRowSize.Medium);
        var large = GalleryTileMetrics.For(FileListRowSize.Large);

        Assert.True(small.ImageSize < medium.ImageSize && medium.ImageSize < large.ImageSize);
        Assert.True(medium.TileWidth > medium.ImageSize);
        Assert.True(medium.TileHeight >= medium.ImageSize + GalleryTileMetrics.LabelHeight);
    }
}
