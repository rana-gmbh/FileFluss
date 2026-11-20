// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Compare;
using FileFluss.Core.Sync;

namespace FileFluss.Core.Tests.Compare;

[Collection("Localization")]
public class FolderComparisonTests
{
    public FolderComparisonTests() => Localization.Use(AppLanguage.English);

    private static DateTimeOffset T(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static SyncEntry File(string path, long size, long modified = 1_000, DateTimeOffset? created = null)
        => new(path, false, size, T(modified), created);

    private static SyncEntry Folder(string path) => new(path, true, 0, T(1_000));

    private static FolderCompareEntry Entry(FolderComparisonResult result, string path) => result.Entries.Single(e => e.RelativePath == path);

    [Fact]
    public void EachPathGetsOneStatus()
    {
        var result = FolderComparison.Compare(
            [File("a", 10), File("b", 5), Folder("d"), File("d/x", 1)],
            [File("a", 10), File("b", 6), File("c", 3), Folder("d")]);

        Assert.Equal(["a", "b", "c", "d", "d/x"], result.Entries.Select(e => e.RelativePath).ToArray());
        Assert.Equal(FolderCompareStatus.Identical, Entry(result, "a").Status);
        Assert.Equal(FolderCompareStatus.Differs, Entry(result, "b").Status);
        Assert.Equal(FolderCompareStatus.OnlyRight, Entry(result, "c").Status);
        Assert.Equal(FolderCompareStatus.Identical, Entry(result, "d").Status);
        Assert.Equal(FolderCompareStatus.OnlyLeft, Entry(result, "d/x").Status);
        Assert.Equal((1, 1, 1, 2), (result.DiffersCount, result.OnlyLeftCount, result.OnlyRightCount, result.IdenticalCount));
        Assert.Equal("1 different • 1 only-left • 1 only-right • 2 same", result.SummaryText);
    }

    [Fact]
    public void DatesCountOnlyWhenAskedAndOutsideTheTolerance()
    {
        SyncEntry[] left = [File("same", 10, 1_000), File("close", 10, 1_000)];
        SyncEntry[] right = [File("same", 10, 9_000), File("close", 10, 1_001)];

        var bySize = FolderComparison.Compare(left, right);
        Assert.Equal(0, bySize.DiffersCount);
        Assert.False(bySize.ComparedDates);

        var byDate = FolderComparison.Compare(left, right, compareDates: true);
        var same = Entry(byDate, "same");
        Assert.Equal(FolderCompareStatus.Differs, same.Status);
        Assert.True(same.DateDiffersOnly);
        Assert.Equal(CompareSide.Right, same.NewerSide);
        Assert.Equal(FolderCompareStatus.Identical, Entry(byDate, "close").Status);
        Assert.Null(Entry(byDate, "close").NewerSide);
        Assert.True(byDate.ComparedDates);
    }

    [Fact]
    public void AFolderAndAFileOfOneNameDiffer()
    {
        var result = FolderComparison.Compare([Folder("x")], [File("x", 1)]);
        Assert.Equal(FolderCompareStatus.Differs, Assert.Single(result.Entries).Status);
    }

    [Fact]
    public void NamesMatchIgnoringCaseWhenAsked()
    {
        Assert.Equal(1, FolderComparison.Compare([File("A.txt", 1)], [File("a.txt", 1)], ignoreCase: true).IdenticalCount);
        Assert.Equal(2, FolderComparison.Compare([File("A.txt", 1)], [File("a.txt", 1)]).Entries.Count);
    }

    [Fact]
    public void IdenticalFoldersSaySo()
    {
        var result = FolderComparison.Compare([File("a", 1), File("b", 2), Folder("c")], [File("a", 1), File("b", 2), Folder("c")]);
        Assert.Equal("Folders are identical (3 items)", result.SummaryText);
    }

    [Fact]
    public void RowsDescribeSizesAndDates()
    {
        var created = T(500);
        var result = FolderComparison.Compare(
            [File("only-left", 10), File("grew", 10), File("same-size", 7, 1_000, created), File("same", 4)],
            [File("only-right", 20), File("grew", 20), File("same-size", 7, 5_000), File("same", 4)],
            compareDates: true);

        Assert.Equal("Left: 10 bytes", Entry(result, "only-left").DetailText);
        Assert.Equal("Right: 20 bytes", Entry(result, "only-right").DetailText);
        Assert.Equal("Left: 10 bytes • Right: 20 bytes — different size", Entry(result, "grew").DetailText);
        Assert.Equal("Same size: 7 bytes", Entry(result, "same-size").DetailText);
        Assert.Equal("4 bytes", Entry(result, "same").DetailText);

        var sameSize = Entry(result, "same-size");
        Assert.Equal(
            $"Modified — Left: {FolderCompareEntry.FormatDate(T(1_000))}  •  Right: {FolderCompareEntry.FormatDate(T(5_000))}",
            sameSize.ModifiedDetail);
        Assert.Equal($"Created  — Left: {FolderCompareEntry.FormatDate(created)}  •  Right: —", sameSize.CreatedDetail);
        Assert.Null(Entry(result, "grew").CreatedDetail);
    }

    [Fact]
    public void TheTreeGroupsFoldersFirstInExplorerOrderWithCounts()
    {
        var result = FolderComparison.Compare(
            [File("file10", 1), File("file2", 1), File("Folder/x", 1), File("deep/a/b.txt", 1)],
            [File("file10", 1), File("file2", 2), File("Folder/y", 1)]);

        var tree = CompareTreeBuilder.Build(result.Entries);

        // "deep" and "deep/a" are in nobody's listing — synthetic groups.
        Assert.Equal(["deep", "Folder", "file2", "file10"], tree.Children.Select(c => c.Name).ToArray());
        var deep = tree.Children[0];
        Assert.Null(deep.Entry);
        Assert.True(deep.IsDirectory);
        Assert.Equal("deep/a", deep.Children.Single().Path);
        Assert.Equal(new CompareCounts(0, 1, 0, 0), deep.DescendantCounts);
        Assert.Equal(new CompareCounts(0, 1, 1, 0), tree.Children[1].DescendantCounts);
        Assert.Equal(new CompareCounts(1, 2, 1, 1), tree.DescendantCounts);
    }

    [Fact]
    public void FolderSummariesFollowTheFilter()
    {
        var counts = new CompareCounts(Different: 2, OnlyLeft: 1, OnlyRight: 0, Identical: 4);
        Assert.Equal("2 different · 1 only left · 4 same", CompareFilter.All.FolderSummary(counts));
        Assert.Equal("2 different · 1 only left", CompareFilter.Differences.FolderSummary(counts));
        Assert.Equal("1 only left", CompareFilter.OnlyLeft.FolderSummary(counts));
        Assert.Equal(string.Empty, CompareFilter.OnlyRight.FolderSummary(counts));
        Assert.Equal("4 same", CompareFilter.Identical.FolderSummary(counts));
    }
}
