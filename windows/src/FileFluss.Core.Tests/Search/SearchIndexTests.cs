// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Search;
using static FileFluss.Core.Tests.Search.SearchTestData;

namespace FileFluss.Core.Tests.Search;

public sealed class SearchIndexTests : IDisposable
{
    private readonly TempIndex _temp = new();
    private readonly Guid _account = Guid.NewGuid();

    private SearchIndex Index => _temp.Index;

    public void Dispose() => _temp.Dispose();

    // MARK: - Query building

    [Theory]
    [InlineData("store.txt", "\"store\"* \"txt\"*")]
    [InlineData("  Urlaub 2026 ", "\"Urlaub\"* \"2026\"*")]
    [InlineData("Ärger-Bericht", "\"Ärger\"* \"Bericht\"*")]
    [InlineData("...", "")]
    [InlineData("", "")]
    public void FtsQueryFollowsTheTokenizer(string raw, string expected)
        => Assert.Equal(expected, SearchIndex.MakeFtsQuery(raw));

    [Fact]
    public void OperatorWordsAreSearchedAsWords()
    {
        Index.UpsertItems([CloudItem("/OR notes.txt"), CloudItem("/order.txt")], _account);

        // "OR" bare would be an FTS5 operator and a syntax error; quoted, it is a prefix.
        Assert.Equal(2, Index.Search("OR", _account).Count);
        Assert.Equal("/OR notes.txt", Assert.Single(Index.Search("NOT", _account)).Item.Path);
    }

    // MARK: - Cloud cache

    [Fact]
    public void CloudSearchMatchesTokenPrefixes()
    {
        Index.UpsertItems(
        [
            CloudItem("/Docs", isDirectory: true),
            CloudItem("/Docs/store.txt"),
            CloudItem("/Docs/storage-plan.pdf"),
            CloudItem("/Docs/restore.txt"),
        ], _account);

        var names = Index.Search("store.txt", _account).Select(h => h.Item.Name).ToList();
        Assert.Equal(["store.txt"], names);

        var prefix = Index.Search("stor", _account).Select(h => h.Item.Name).Order().ToList();
        Assert.Equal(["storage-plan.pdf", "store.txt"], prefix);

        var hit = Index.Search("store", _account).Single();
        Assert.Equal(_account, hit.AccountId);
        Assert.Equal("/Docs/store.txt", hit.Item.Path);
        Assert.Equal(Stamp, hit.Item.ModificationDate);
        Assert.False(hit.Item.IsDirectory);
    }

    [Fact]
    public void SearchCanBeLimitedToOneAccount()
    {
        var other = Guid.NewGuid();
        Index.UpsertItems([CloudItem("/a/invoice.pdf")], _account);
        Index.UpsertItems([CloudItem("/b/invoice.pdf")], other);

        Assert.Equal(2, Index.Search("invoice", null).Count);
        Assert.Equal("/b/invoice.pdf", Index.Search("invoice", other).Single().Item.Path);
        Assert.Equal("/a/invoice.pdf", Index.SearchCloudCached("invoice", [_account]).Single().Item.Path);
        Assert.Empty(Index.SearchCloudCached("invoice", []));
    }

    /// <summary>
    /// A refreshed entry replaces its search entry. INSERT OR REPLACE would leave the old name
    /// in the external-content FTS table, so a renamed file stayed findable by its old name.
    /// </summary>
    [Fact]
    public void RefreshingAnEntryKeepsTheSearchTableInStep()
    {
        Index.UpsertItems([CloudItem("/x.txt") with { Name = "draft.txt" }], _account);
        Index.UpsertItems([CloudItem("/x.txt") with { Name = "final.txt", Size = 99 }], _account);

        Assert.Empty(Index.Search("draft", _account));
        var hit = Assert.Single(Index.Search("final", _account));
        Assert.Equal(99, hit.Item.Size);
        Assert.Equal(1, Index.CloudAccountSummary(_account)?.TotalFiles);
    }

    [Fact]
    public void CloudChildrenAreListedFolderByFolderFoldersFirst()
    {
        Index.UpsertItems(
        [
            CloudItem("/b.txt"),
            CloudItem("/Alpha", isDirectory: true),
            CloudItem("/Alpha/inner.txt"),
            CloudItem("/Alpha/Deep", isDirectory: true),
            CloudItem("/Alpha/Deep/deeper.txt"),
            CloudItem("/a.txt"),
            CloudItem("/Alphabet.txt"),
        ], _account);

        var root = Index.ListCloudChildren(_account, "/");
        Assert.Equal(["Alpha", "a.txt", "Alphabet.txt", "b.txt"], root.Select(r => r.Name));
        Assert.All(root, r => Assert.Equal("/", r.ParentPath));

        var alpha = Index.ListCloudChildren(_account, "/Alpha/");
        Assert.Equal(["Deep", "inner.txt"], alpha.Select(r => r.Name));
        Assert.Equal(["/Alpha/Deep/deeper.txt"], Index.ListCloudChildren(_account, "/Alpha/Deep").Select(r => r.Path));
        Assert.Empty(Index.ListCloudChildren(Guid.NewGuid(), "/"));
    }

    /// <summary>
    /// "Let a listing retire cache entries, not only add them": browsing a folder whose file
    /// was deleted elsewhere drops it — and what was beneath it — from the cache.
    /// </summary>
    [Fact]
    public void AListingDropsCachedChildrenItNoLongerContains()
    {
        Index.UpsertItems(
        [
            CloudItem("/Test", isDirectory: true),
            CloudItem("/Test/Documents", isDirectory: true),
            CloudItem("/Test/Documents/a.txt"),
            CloudItem("/Testing", isDirectory: true),
            CloudItem("/Other", isDirectory: true),
        ], _account);

        var retired = Index.ApplyListing(_account, "/", [CloudItem("/Testing", isDirectory: true), CloudItem("/Other", isDirectory: true), CloudItem("/new.txt")]);

        Assert.Equal(3, retired);
        Assert.Null(Index.CloudEntry(_account, "/Test"));
        Assert.Null(Index.CloudEntry(_account, "/Test/Documents/a.txt"));
        Assert.NotNull(Index.CloudEntry(_account, "/Testing"));
        Assert.NotNull(Index.CloudEntry(_account, "/new.txt"));
        Assert.Empty(Index.Search("Documents", _account));
    }

    [Fact]
    public void AListingSaysNothingAboutLevelsBelowIt()
    {
        Index.UpsertItems(
        [
            CloudItem("/Test", isDirectory: true),
            CloudItem("/Test/Documents", isDirectory: true),
            CloudItem("/Test/Documents/a.txt"),
            CloudItem("/Test/Immobilien", isDirectory: true),
            CloudItem("/Testing", isDirectory: true),
        ], _account);

        Index.ApplyListing(_account, "/Test", [CloudItem("/Test/Documents", isDirectory: true)]);

        Assert.NotNull(Index.CloudEntry(_account, "/Test/Documents/a.txt"));
        Assert.Null(Index.CloudEntry(_account, "/Test/Immobilien"));
        Assert.NotNull(Index.CloudEntry(_account, "/Test"));
        Assert.NotNull(Index.CloudEntry(_account, "/Testing"));
    }

    [Fact]
    public void AnEmptyListingEmptiesThatLevel()
    {
        Index.UpsertItems([CloudItem("/Test", isDirectory: true), CloudItem("/Test/a.txt"), CloudItem("/Test/b", isDirectory: true), CloudItem("/Test/b/c.txt")], _account);

        Index.ApplyListing(_account, "/Test", []);

        Assert.Empty(Index.ListCloudChildren(_account, "/Test"));
        Assert.Null(Index.CloudEntry(_account, "/Test/b/c.txt"));
        Assert.NotNull(Index.CloudEntry(_account, "/Test"));
    }

    [Fact]
    public void RemovingAFolderTakesItsSubtree()
    {
        Index.UpsertItems([CloudItem("/A", isDirectory: true), CloudItem("/A/x.txt"), CloudItem("/AB.txt")], _account);

        Index.RemoveItems(_account, ["/A"]);

        Assert.Null(Index.CloudEntry(_account, "/A/x.txt"));
        Assert.NotNull(Index.CloudEntry(_account, "/AB.txt"));
    }

    [Fact]
    public void SummariesCountFilesFoldersAndBytes()
    {
        Index.UpsertItems([CloudItem("/F", isDirectory: true), CloudItem("/F/a.bin", size: 100), CloudItem("/F/b.bin", size: 250)], _account);

        var summary = Index.CloudAccountSummary(_account);
        Assert.NotNull(summary);
        Assert.Equal(2, summary.TotalFiles);
        Assert.Equal(1, summary.TotalFolders);
        Assert.Equal(350, summary.TotalBytes);
        Assert.Null(Index.CloudAccountSummary(Guid.NewGuid()));
        Assert.Equal(350, Index.CloudSizeUnder(_account, "/F"));
        Assert.Equal(100, Index.CloudSizeUnder(_account, "/F/a.bin"));
    }

    /// <summary>
    /// Browsing upserts entries for incremental search, but a few browsed folders are not an
    /// index: only a recorded full run makes an account "indexed".
    /// </summary>
    [Fact]
    public void OnlyARecordedFullRunCountsAsIndexed()
    {
        Index.ApplyListing(_account, "/", [CloudItem("/browsed.txt")]);
        Assert.Null(Index.FullCloudIndexInfo(_account));
        Assert.Empty(Index.GetIndexStatus());

        Index.RecordCloudSource(_account, "Dropbox (Robert)", Index.CloudAccountSummary(_account)!);

        var info = Index.FullCloudIndexInfo(_account);
        Assert.NotNull(info);
        Assert.Equal(1, info.TotalFiles);
        var status = Assert.Single(Index.GetIndexStatus());
        Assert.True(status.IsCloud);
        Assert.Equal(_account, status.AccountId);
        Assert.Equal("Dropbox (Robert)", status.DisplayName);
        Assert.Equal(1, status.TotalFiles);
        Assert.NotNull(status.LastIndexed);
    }

    [Fact]
    public void IndexStatusListsDrivesWithTheirCounts()
    {
        Index.ReplaceFiles("vol:1", DriveKind.External.IndexKind(), "Backup", [
            new IndexedFile("vol:1", "/a", "/", "a", true, 0, Stamp),
            new IndexedFile("vol:1", "/a/b.bin", "/a", "b.bin", false, 500, Stamp),
        ]);

        var row = Assert.Single(Index.GetIndexStatus());
        Assert.False(row.IsCloud);
        Assert.Equal(DriveKind.External, row.DriveKind);
        Assert.Equal(1, row.TotalFiles);
        Assert.Equal(1, row.TotalFolders);
        Assert.Equal(500, row.TotalBytes);
    }

    // MARK: - Drives

    /// <summary>An empty source list means "no drives", not "every drive" — online drives must not appear among offline results.</summary>
    [Fact]
    public void AnEmptySourceListSearchesNothing()
    {
        Index.ReplaceFiles("vol:1", "drive-external", "Disk", [new IndexedFile("vol:1", "/photo.jpg", "/", "photo.jpg", false, 1, Stamp)]);

        Assert.Empty(Index.SearchIndexed("photo", []));
        Assert.Single(Index.SearchIndexed("photo"));
        Assert.Single(Index.SearchIndexed("photo", ["vol:1"]));
        Assert.Empty(Index.SearchIndexed("photo", ["vol:2"]));
    }

    [Fact]
    public void BulkReadsEscapeWildcardsInTheRoot()
    {
        Index.ReplaceFiles("vol:1", "drive-external", "Disk",
        [
            new IndexedFile("vol:1", "/a_b", "/", "a_b", true, 0, Stamp),
            new IndexedFile("vol:1", "/a_b/x.txt", "/a_b", "x.txt", false, 1, Stamp),
            new IndexedFile("vol:1", "/aXb", "/", "aXb", true, 0, Stamp),
            new IndexedFile("vol:1", "/aXb/y.txt", "/aXb", "y.txt", false, 1, Stamp),
        ]);
        Index.UpsertItems([CloudItem("/100%", isDirectory: true), CloudItem("/100%/z.txt"), CloudItem("/1000/w.txt")], _account);

        Assert.Equal(["/a_b", "/a_b/x.txt"], Index.IndexedFilesUnder("vol:1", "/a_b").Select(f => f.Path));
        Assert.Equal(4, Index.IndexedFilesUnder("vol:1", "/").Count);
        Assert.Equal(["/100%", "/100%/z.txt"], Index.CloudFilesUnder(_account, "/100%").Select(f => f.Path));
    }

    // MARK: - Maintenance

    [Fact]
    public void RemovingAnAccountTakesEverythingItLeftBehind()
    {
        Index.UpsertItems([CloudItem("/a.txt")], _account);
        Index.RecordCloudSource(_account, "Box", Index.CloudAccountSummary(_account)!);

        Index.DropIndexedCloudSource(_account);

        Assert.Null(Index.GetSource(SearchIndex.SourceIdFor(_account)));
        Assert.Null(Index.CloudAccountSummary(_account));
        Assert.Empty(Index.Search("a", _account));
    }

    [Fact]
    public void LaunchPurgesSourcesOfAccountsThatNoLongerExist()
    {
        var kept = Guid.NewGuid();
        foreach (var id in new[] { _account, kept })
        {
            Index.UpsertItems([CloudItem("/f.txt")], id);
            Index.RecordCloudSource(id, "Account", Index.CloudAccountSummary(id)!);
        }

        Index.ReplaceFiles("vol:1", "drive-external", "Disk", [new IndexedFile("vol:1", "/f.txt", "/", "f.txt", false, 1, Stamp)]);

        Index.PurgeOrphanCloudSources(new HashSet<Guid> { kept });

        Assert.Null(Index.GetSource(SearchIndex.SourceIdFor(_account)));
        Assert.Null(Index.CloudAccountSummary(_account));
        Assert.NotNull(Index.GetSource(SearchIndex.SourceIdFor(kept)));
        Assert.NotNull(Index.GetSource("vol:1"));
    }

    [Fact]
    public void RenamingASourceChangesOnlyItsName()
    {
        Index.UpsertItems([CloudItem("/f.txt")], _account);
        Index.RecordCloudSource(_account, "Old", Index.CloudAccountSummary(_account)!);

        Index.RenameIndexedSource(SearchIndex.SourceIdFor(_account), "New");

        var source = Index.GetSource(SearchIndex.SourceIdFor(_account));
        Assert.Equal("New", source?.DisplayName);
        Assert.Equal(SearchIndex.CloudKind, source?.Kind);
    }

    [Fact]
    public void EraseAllEmptiesEveryStore()
    {
        Index.UpsertItems([CloudItem("/f.txt")], _account);
        Index.ReplaceFiles("vol:1", "drive-external", "Disk", [new IndexedFile("vol:1", "/f.txt", "/", "f.txt", false, 1, Stamp)]);

        Index.WipeAll();

        Assert.Empty(Index.ListIndexedSources());
        Assert.Empty(Index.Search("f", null));
        Assert.Empty(Index.SearchIndexed("f"));
        Assert.Null(Index.CloudAccountSummary(_account));
    }

    [Fact]
    public void KnownDrivesPersist()
    {
        var drive = new KnownDrive
        {
            Id = "vol:ABCD-1234",
            VolumeId = "ABCD-1234",
            Label = "Archive",
            Kind = DriveKind.Network,
            LastRootPath = @"Z:\",
            LastIndexed = Stamp,
            TotalFiles = 12,
            TotalBytes = 3456,
        };
        Index.SaveKnownDrives([drive]);
        Index.Close();

        using var next = _temp.Reopen();
        var loaded = Assert.Single(next.LoadKnownDrives());
        Assert.Equal(drive, loaded);
    }
}
