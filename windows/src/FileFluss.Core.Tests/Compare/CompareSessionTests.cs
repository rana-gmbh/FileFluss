// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Compare;
using FileFluss.Core.Providers;
using FileFluss.Core.Sync;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Tests.Sync;

namespace FileFluss.Core.Tests.Compare;

[Collection("Localization")]
public sealed class CompareSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-compare-" + Guid.NewGuid().ToString("N"));

    public CompareSessionTests()
    {
        Directory.CreateDirectory(_root);
        Localization.Use(AppLanguage.English);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly DateTimeOffset Stamp = new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>
    /// Left: same.txt, grew.txt (3), left.txt, docs/a.txt, docs/b.txt.
    /// Right: same.txt, grew.txt (5), right.txt, docs/a.txt.
    /// </summary>
    private static (MemoryCloudProvider Left, MemoryCloudProvider Right) TwoSides()
    {
        var left = new MemoryCloudProvider();
        var right = new MemoryCloudProvider();
        foreach (var side in new[] { left, right })
        {
            side.AddFile("/R/same.txt", "same", Stamp);
            side.AddFile("/R/docs/a.txt", "a", Stamp);
        }

        left.AddFile("/R/grew.txt", "abc", Stamp);
        right.AddFile("/R/grew.txt", "abcde", Stamp);
        left.AddFile("/R/left.txt", "l", Stamp);
        left.AddFile("/R/docs/b.txt", "b", Stamp);
        right.AddFile("/R/right.txt", "r", Stamp);
        return (left, right);
    }

    [Fact]
    public async Task ComparingShowsDifferencesByDefault()
    {
        var (left, right) = TwoSides();
        var session = new CompareSession();

        await session.CompareAsync(new SyncEndpoint(left, "/R"), new SyncEndpoint(right, "/R"));

        Assert.True(session.HasEndpoints);
        Assert.False(session.IsCalculating);
        Assert.Null(session.ErrorMessage);
        Assert.Equal(CompareFilter.Differences, session.Filter);
        Assert.Equal("1 different • 2 only-left • 1 only-right • 3 same", session.SummaryText);

        // "docs" is a group (it holds docs/b.txt, only on the left), collapsed.
        Assert.Equal(["docs", "grew.txt", "left.txt", "right.txt"], session.Rows.Select(r => r.Name).ToArray());
        var docs = session.Rows[0];
        Assert.True(docs.IsGroup);
        Assert.False(docs.IsExpanded);
        Assert.Equal("1 only left", docs.FolderSummary);
        Assert.Equal(FolderCompareStatus.Identical, docs.Status);
        Assert.Equal("Left: 3 bytes • Right: 5 bytes — different size", session.Rows[1].DetailText);
        Assert.Null(session.Rows[1].NewerBadge);

        session.ToggleExpanded(docs.Node);
        Assert.Equal(["docs", "b.txt", "grew.txt", "left.txt", "right.txt"], session.Rows.Select(r => r.Name).ToArray());
        Assert.Equal(1, session.Rows[1].Depth);

        var chips = session.FilterChips;
        Assert.Equal(["All", "Differences", "Only Left", "Only Right", "Identical"], chips.Select(c => c.Title).ToArray());
        Assert.Equal([7, 4, 2, 1, 3], chips.Select(c => c.Count).ToArray());
        Assert.True(chips[1].IsSelected);
    }

    [Fact]
    public async Task FiltersNarrowTheRowsAndKeepExpansion()
    {
        var (left, right) = TwoSides();
        var session = new CompareSession();
        await session.CompareAsync(new SyncEndpoint(left, "/R"), new SyncEndpoint(right, "/R"));
        session.SetExpanded(session.Rows[0].Node, true);

        session.Filter = CompareFilter.Identical;
        Assert.Equal(["docs", "a.txt", "same.txt"], session.Rows.Select(r => r.Name).ToArray());
        Assert.Equal("1 same", session.Rows[0].FolderSummary);

        session.Filter = CompareFilter.OnlyRight;
        Assert.Equal(["right.txt"], session.Rows.Select(r => r.Name).ToArray());
        Assert.False(session.NothingMatchesFilter);

        session.Filter = CompareFilter.All;
        Assert.Equal(7, session.Rows.Count);
    }

    [Fact]
    public async Task IdenticalFoldersLeaveNothingUnderDifferences()
    {
        var left = new MemoryCloudProvider();
        var right = new MemoryCloudProvider();
        left.AddFile("/x.txt", "x", Stamp);
        right.AddFile("/x.txt", "x", Stamp);
        var session = new CompareSession();

        await session.CompareAsync(new SyncEndpoint(left, "/"), new SyncEndpoint(right, "/"));

        Assert.True(session.NothingMatchesFilter);
        Assert.Empty(session.Rows);
        Assert.Equal("Folders are identical (1 items)", session.SummaryText);
    }

    [Fact]
    public async Task CompareDateRediffsWithoutReadingAgain()
    {
        var left = new MemoryCloudProvider();
        var right = new MemoryCloudProvider();
        left.AddFile("/doc.txt", "same", Stamp);
        right.AddFile("/doc.txt", "same", Stamp.AddDays(1));
        var wrapped = new WrappingProvider(right);
        var session = new CompareSession();

        await session.CompareAsync(new SyncEndpoint(left, "/"), new SyncEndpoint(wrapped, "/"));
        Assert.Equal(1, session.Result!.IdenticalCount);
        var reads = wrapped.ListCalls;

        session.CompareDates = true;

        Assert.Equal(reads, wrapped.ListCalls);
        var row = Assert.Single(session.Rows);
        Assert.Equal(FolderCompareStatus.Differs, row.Status);
        Assert.Equal("Right newer", row.NewerBadge);
        Assert.Equal("Same size: 4 bytes", row.DetailText);
        Assert.StartsWith("Modified — Left: ", row.ModifiedDetail);
        Assert.Null(row.CreatedDetail);

        session.CompareDates = false;
        Assert.Empty(session.Rows);
    }

    [Fact]
    public async Task CompareAgainReadsTheSnapshottedFoldersAfresh()
    {
        var (left, right) = TwoSides();
        var session = new CompareSession();
        var leftEndpoint = new SyncEndpoint(left, "/R");
        await session.CompareAsync(leftEndpoint, new SyncEndpoint(right, "/R"));
        Assert.Equal(1, session.Result!.OnlyRightCount);

        right.AddFile("/R/later.txt", "new", Stamp);
        await session.CompareAgainAsync();

        Assert.Equal(2, session.Result!.OnlyRightCount);
        Assert.Same(leftEndpoint, session.LeftEndpoint);
    }

    [Fact]
    public async Task LocalFoldersCompareAgainstACloudIgnoringCase()
    {
        File.WriteAllText(Path.Combine(_root, "Report.PDF"), "pdf");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/report.pdf", "pdf");
        var session = new CompareSession();

        await session.CompareAsync(new SyncEndpoint(LocalFileProvider.Shared, _root), new SyncEndpoint(cloud, "/"));

        Assert.Equal(1, session.Result!.IdenticalCount);
        Assert.Single(session.Result.Entries);
    }

    [Fact]
    public async Task AMissingSideShowsThePlaceholder()
    {
        var session = new CompareSession();
        await session.CompareAsync(null, new SyncEndpoint(new MemoryCloudProvider(), "/"));

        Assert.False(session.HasEndpoints);
        Assert.Null(session.Result);
        Assert.Null(session.SummaryText);
        Assert.Empty(session.FilterChips);
        Assert.Equal("Open a folder on each side to compare", CompareSession.PlaceholderTitle);
    }

    [Fact]
    public async Task AnUnreadableFolderShowsAnError()
    {
        var memory = new MemoryCloudProvider();
        var wrapped = new WrappingProvider(memory);
        wrapped.FailingListings.Add("/");
        var session = new CompareSession();

        await session.CompareAsync(new SyncEndpoint(new MemoryCloudProvider(), "/"), new SyncEndpoint(wrapped, "/"));

        Assert.Equal("Could not read folders: listing refused", session.ErrorMessage);
        Assert.Null(session.Result);
    }

    [Fact]
    public async Task ANewComparisonSupersedesOneInFlight()
    {
        var gate = new TaskCompletionSource();
        var slow = new WrappingProvider(new MemoryCloudProvider()) { BeforeList = (_, ct) => gate.Task.WaitAsync(ct) };
        var fast = new MemoryCloudProvider();
        fast.AddFile("/only.txt", "x");
        var session = new CompareSession();

        var first = session.CompareAsync(new SyncEndpoint(slow, "/"), new SyncEndpoint(fast, "/"));
        Assert.True(session.IsCalculating);
        await session.CompareAsync(new SyncEndpoint(fast, "/"), new SyncEndpoint(new MemoryCloudProvider(), "/"));
        gate.SetResult();
        await first;

        Assert.False(session.IsCalculating);
        Assert.Equal(1, session.Result!.OnlyLeftCount);
        Assert.Same(fast, session.LeftEndpoint!.Provider);
    }
}
