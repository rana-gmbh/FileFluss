// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Search;

namespace FileFluss.Core.Tests.Search;

/// <summary>
/// Writing an index, and finding out when it didn't work (port of the macOS
/// <c>SearchIndexWriteTests</c>). A user indexed 179,000 files from an SMB share: the sidebar
/// reported them, the database held none, and nothing anywhere said so.
/// </summary>
public sealed class SearchIndexWriteTests : IDisposable
{
    private readonly TempIndex _temp = new();

    private SearchIndex Index => _temp.Index;

    public void Dispose() => _temp.Dispose();

    private static List<IndexedFile> Files(int count, string sourceId, int folders = 0)
        => Enumerable.Range(0, count).Select(i => new IndexedFile(
            sourceId,
            $"/dir/file-{i}.txt",
            "/dir",
            $"file-{i}.txt",
            i < folders,
            10,
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))).ToList();

    /// <summary>A path the index can't be created at: its "directory" is a file.</summary>
    private static (string DbPath, string Blocker) UnwritablePath()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "ff-index-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        return (Path.Combine(blocker, "index.sqlite"), blocker);
    }

    [Fact]
    public void WhatGoesInCanBeReadBackAndIsReportedHonestly()
    {
        var result = Index.ReplaceFiles("vol:TEST", "drive-network", "Share", Files(500, "vol:TEST", folders: 20));

        Assert.Equal(500, result.Requested);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(500, result.Stored);
        Assert.Equal(20, result.StoredFolders);
        Assert.True(result.IsComplete);

        var sources = Index.ListIndexedSources();
        Assert.Single(sources);
        Assert.Equal("vol:TEST", sources[0].SourceId);
        var counts = Index.SourceCounts("vol:TEST");
        Assert.Equal(480, counts?.Files);
        Assert.Equal(20, counts?.Folders);
    }

    /// <summary>
    /// The index opens itself when used. It used to be opened once at launch, behind
    /// reconnecting the cloud accounts, and anything that ran before that wrote into nothing.
    /// </summary>
    [Fact]
    public void NoSeparateOpenCallIsNeeded()
    {
        Index.ReplaceFiles("vol:TEST", "drive-external", "Disk", Files(3, "vol:TEST"));

        Assert.Equal(3, Index.SourceCounts("vol:TEST")?.Files);
        Assert.True(File.Exists(_temp.Path));
    }

    /// <summary>An index that cannot be opened must say so, not return as if it had worked.</summary>
    [Fact]
    public void AnIndexThatCantBeWrittenReportsItInsteadOfPretending()
    {
        var (dbPath, blocker) = UnwritablePath();
        try
        {
            using var index = new SearchIndex(dbPath);
            var error = Assert.Throws<SearchIndexException>(() => index.ReplaceFiles("vol:TEST", "drive-network", "Share", Files(10, "vol:TEST")));
            Assert.Equal(SearchIndexErrorKind.OpenFailed, error.Kind);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void ACloudWriteReportsFailureToo()
    {
        var (dbPath, blocker) = UnwritablePath();
        try
        {
            using var index = new SearchIndex(dbPath);
            Assert.Throws<SearchIndexException>(() => index.UpsertItems([SearchTestData.CloudItem("/a.txt")], Guid.NewGuid()));
            Assert.Throws<SearchIndexException>(() => index.ApplyListing(Guid.NewGuid(), "/", [SearchTestData.CloudItem("/a.txt")]));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    /// <summary>
    /// A row the table refuses — two entries claiming the same path — doesn't throw the whole
    /// index away, but the shortfall is visible rather than guessed at from a file count.
    /// </summary>
    [Fact]
    public void ARejectedRowIsCountedNotFatal()
    {
        var duplicate = new IndexedFile("vol:TEST", "/same.txt", "/", "same.txt", false, 1, DateTimeOffset.Now);

        var result = Index.ReplaceFiles("vol:TEST", "drive-network", "Share", [duplicate, duplicate]);

        Assert.Equal(2, result.Requested);
        Assert.Equal(1, result.Rejected);
        Assert.Equal(1, result.Stored);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void IndexingAgainReplacesWhatWasThere()
    {
        Index.ReplaceFiles("vol:TEST", "drive-network", "Share", Files(100, "vol:TEST"));
        var second = Index.ReplaceFiles("vol:TEST", "drive-network", "Share", Files(5, "vol:TEST"));

        Assert.Equal(5, second.Stored);
        Assert.Equal(5, Index.SourceCounts("vol:TEST")?.Files);
        Assert.Single(Index.ListIndexedSources());
        Assert.Equal(5, Index.SearchIndexed("file", ["vol:TEST"]).Count);
    }

    /// <summary>The walk feeds rows in batches instead of holding a whole drive in memory; the result is the same index.</summary>
    [Fact]
    public void RowsWrittenInBatchesLandAsOneIndex()
    {
        var all = Files(250, "vol:TEST", folders: 10);

        var rejected = 0;
        foreach (var chunk in all.Chunk(60))
        {
            rejected += Index.StageFiles(chunk, "vol:TEST");
        }

        var result = Index.PromoteStagedFiles("vol:TEST", "drive-network", "Share", all.Count, rejected);

        Assert.Equal(250, result.Stored);
        Assert.True(result.IsComplete);
        Assert.Equal(240, Index.SourceCounts("vol:TEST")?.Files);
    }

    /// <summary>Half a walk is not an index: until promoted, nothing sees it, and the old one is what everything sees.</summary>
    [Fact]
    public void AHalfWrittenIndexIsInvisibleAndDoesntDisturbTheOldOne()
    {
        Index.ReplaceFiles("vol:TEST", "drive-network", "Share", Files(10, "vol:TEST"));

        // A new run starts and gets part way.
        Index.StageFiles(Files(400, "vol:TEST"), "vol:TEST");

        Assert.Equal(10, Index.SourceCounts("vol:TEST")?.Files);
        Assert.Single(Index.ListIndexedSources());
        Assert.Equal(10, Index.ListChildren("vol:TEST", "/dir").Count);
        Assert.Equal(10, Index.SearchIndexed("file", ["vol:TEST"]).Count);
    }

    /// <summary>A cancelled or failed run throws its staged rows away; the previous index survives untouched.</summary>
    [Fact]
    public void DiscardingAHalfWrittenIndexLeavesThePreviousOne()
    {
        Index.ReplaceFiles("vol:TEST", "drive-network", "Share", Files(10, "vol:TEST"));
        Index.StageFiles(Files(400, "vol:TEST"), "vol:TEST");
        Index.DiscardStagedFiles("vol:TEST");

        Assert.Equal(10, Index.SourceCounts("vol:TEST")?.Files);

        // And the discarded rows are really gone — promoting now would otherwise resurrect them.
        var promoted = Index.PromoteStagedFiles("vol:TEST", "drive-network", "Share", 0, 0);
        Assert.Equal(0, promoted.Stored);
    }

    /// <summary>A quit in the middle of indexing leaves rows staged. They are not an index and must not become one on the next launch.</summary>
    [Fact]
    public void RowsStagedByARunThatNeverFinishedAreDroppedAtOpen()
    {
        Index.StageFiles(Files(50, "vol:TEST"), "vol:TEST");
        Index.Close();

        using var next = _temp.Reopen();
        var promoted = next.PromoteStagedFiles("vol:TEST", "drive-network", "Share", 0, 0);

        Assert.Equal(0, promoted.Stored);
        Assert.Equal(0, next.SourceCounts("vol:TEST")?.Files);
    }

    /// <summary>Survives a quit: the point of an index is that it is still there next time.</summary>
    [Fact]
    public void AnIndexWrittenInOneSessionIsThereInTheNext()
    {
        Index.ReplaceFiles("vol:TEST", "drive-network", "Share", Files(42, "vol:TEST"));
        Index.Close();

        using var second = _temp.Reopen();
        Assert.Equal(42, second.SourceCounts("vol:TEST")?.Files);
        Assert.Equal("Share", second.ListIndexedSources().First().DisplayName);
    }

    /// <summary>One index serves panels, searches and an indexing run at once.</summary>
    [Fact]
    public async Task ConcurrentWritesAndReadsAllLand()
    {
        var accounts = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var writers = accounts.Select(account => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                Index.UpsertItems([SearchTestData.CloudItem($"/folder/report-{i}.pdf")], account);
            }
        }));
        var stager = Task.Run(() =>
        {
            foreach (var chunk in Files(2000, "vol:TEST").Chunk(100))
            {
                Index.StageFiles(chunk, "vol:TEST");
            }

            Index.PromoteStagedFiles("vol:TEST", "drive-external", "Disk", 2000, 0);
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                Index.Search("report", null);
                Index.ListIndexedSources();
            }
        }));

        await Task.WhenAll(writers.Concat(readers).Append(stager));

        Assert.Equal(100, Index.Search("report", null).Count);
        Assert.Equal(2000, Index.SourceCounts("vol:TEST")?.Files);
    }
}
