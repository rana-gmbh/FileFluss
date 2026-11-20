// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Search;
using FileFluss.Core.Services;
using static FileFluss.Core.Tests.Search.SearchTestData;

namespace FileFluss.Core.Tests.Search;

[Collection("Localization")]
public sealed class SearchCoordinatorTests : IDisposable
{
    private readonly TempIndex _temp = new();
    private readonly TempFolder _local = new();
    private readonly TempFolder _drive = new();
    private readonly ProviderHub _hub = new();
    private readonly SearchCoordinator _coordinator;

    public SearchCoordinatorTests()
    {
        Localization.Use(AppLanguage.English);
        _coordinator = new SearchCoordinator(_temp.Index, _hub);
    }

    public void Dispose()
    {
        _temp.Dispose();
        _local.Dispose();
        _drive.Dispose();
    }

    private async Task<(List<SearchResultBatch> Batches, IReadOnlyList<SearchSourceInfo> Sources)> Run(SearchRequest request)
    {
        var run = _coordinator.Start(request, CancellationToken.None);
        var batches = new List<SearchResultBatch>();
        await foreach (var batch in run.Batches)
        {
            batches.Add(batch);
        }

        return (batches, run.Sources);
    }

    private static List<SearchResultItem> Items(IEnumerable<SearchResultBatch> batches, string sourceKey)
        => batches.Where(b => b.SourceKey == sourceKey).SelectMany(b => b.Items).ToList();

    [Fact]
    public async Task TheLocalFolderIsSearchedRecursivelyByName()
    {
        _local.File(@"Reports\2026\Quarterly Report.pdf");
        _local.File("report-notes.txt");
        _local.File("unrelated.txt");
        _local.Folder("Old Reports");
        File.SetAttributes(_local.File(@"Hidden\report.tmp"), FileAttributes.Normal);
        File.SetAttributes(Path.Combine(_local.Root, "Hidden"), FileAttributes.Directory | FileAttributes.Hidden);

        var (batches, sources) = await Run(new SearchRequest("report") { LocalDirectory = _local.Root });

        var source = Assert.Single(sources);
        Assert.Equal(SearchSourceKind.LocalFolder, source.Kind);
        var names = Items(batches, "local").Select(i => i.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Old Reports", "Quarterly Report.pdf", "Reports", "report-notes.txt"], names);
        Assert.All(Items(batches, "local"), i => Assert.Equal(SearchResultKind.Local, i.Kind));
        var done = Assert.Single(batches, b => b.IsComplete);
        Assert.Null(done.ErrorMessage);
    }

    [Fact]
    public async Task TheCurrentFolderScopeDoesNotDescend()
    {
        _local.File(@"Sub\report.txt");
        _local.File("report.txt");

        var (batches, _) = await Run(new SearchRequest("report") { LocalDirectory = _local.Root, Scope = SearchScope.CurrentFolder });

        var hit = Assert.Single(Items(batches, "local"));
        Assert.Equal(Path.Combine(_local.Root, "report.txt"), hit.Path);
    }

    [Fact]
    public async Task AFolderThatIsGoneFailsItsSourceOnly()
    {
        var provider = new SearchableProvider();
        provider.Inner.AddFile("/report.txt", "x");
        _hub.Register(provider.AccountId, provider);

        var (batches, _) = await Run(new SearchRequest("report")
        {
            LocalDirectory = Path.Combine(_local.Root, "missing"),
            Accounts = [new SearchAccount(provider.AccountId, "Cloud")],
        });

        Assert.NotNull(Assert.Single(batches, b => b.SourceKey == "local" && b.IsComplete).ErrorMessage);
        Assert.Single(Items(batches, "cloud:" + provider.AccountId));
    }

    /// <summary>Mounted drives are searched too — except the one the local folder is on, already covered.</summary>
    [Fact]
    public async Task MountedDrivesAreSearchedButNotTwice()
    {
        _drive.File(@"Backup\report.zip");
        _local.File("report.txt");

        var (batches, sources) = await Run(new SearchRequest("report")
        {
            LocalDirectory = _local.Root,
            MountedDrives =
            [
                new SearchDrive("vol:1", "Backup Disk", _drive.Root),
                new SearchDrive("vol:2", "Same Disk", _local.Root),
            ],
        });

        Assert.Equal(["local", "drive:vol:1"], sources.Select(s => s.Key));
        var onDrive = Assert.Single(Items(batches, "drive:vol:1"));
        Assert.Equal("report.zip", onDrive.Name);
        Assert.Equal("Backup Disk", onDrive.SourceName);
    }

    /// <summary>Cached hits come at once; the server's search adds what the cache didn't have, and feeds the cache.</summary>
    [Fact]
    public async Task ACloudAccountAnswersFromTheCacheThenTheServer()
    {
        var provider = new SearchableProvider();
        provider.Inner.AddFile("/Docs/report-2025.pdf", "x");
        provider.Inner.AddFile("/Docs/report-2026.pdf", "x");
        _hub.Register(provider.AccountId, provider);
        _temp.Index.UpsertItems([CloudItem("/Docs/report-2025.pdf")], provider.AccountId);

        var (batches, _) = await Run(new SearchRequest("report") { Accounts = [new SearchAccount(provider.AccountId, "Dropbox", CloudProviderType.Dropbox)] });

        var results = batches.Where(b => !b.IsComplete).ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal("/Docs/report-2025.pdf", Assert.Single(results[0].Items).Path);
        Assert.Equal("/Docs/report-2026.pdf", Assert.Single(results[1].Items).Path);
        Assert.All(results.SelectMany(b => b.Items), i => Assert.Equal(provider.AccountId, i.AccountId));
        Assert.Equal(1, provider.SearchCalls);

        // The server's answer is in the cache now.
        Assert.Equal(2, _temp.Index.Search("report", provider.AccountId).Count);
    }

    [Fact]
    public async Task AProviderWithoutSearchIsAnsweredFromTheIndex()
    {
        var provider = new SearchableProvider(supportsSearch: false);
        _hub.Register(provider.AccountId, provider);
        _temp.Index.UpsertItems([CloudItem("/Photos/holiday.jpg")], provider.AccountId);

        var (batches, _) = await Run(new SearchRequest("holiday") { Accounts = [new SearchAccount(provider.AccountId, "WebDAV")] });

        Assert.Equal("/Photos/holiday.jpg", Assert.Single(Items(batches, "cloud:" + provider.AccountId)).Path);
        Assert.Null(batches.Single(b => b.IsComplete).ErrorMessage);
    }

    /// <summary>A failing server search keeps the cached hits and marks the source failed.</summary>
    [Fact]
    public async Task AFailingServerSearchKeepsCachedHitsAndSaysSo()
    {
        var provider = new SearchableProvider { SearchFailure = CloudProviderException.ServerError(503) };
        _hub.Register(provider.AccountId, provider);
        _temp.Index.UpsertItems([CloudItem("/cached-report.txt")], provider.AccountId);

        var (batches, _) = await Run(new SearchRequest("report") { Accounts = [new SearchAccount(provider.AccountId, "Box")] });

        Assert.Single(Items(batches, "cloud:" + provider.AccountId));
        var done = batches.Single(b => b.IsComplete);
        Assert.Contains("503", done.ErrorMessage);
    }

    [Fact]
    public async Task OfflineSourcesAreSearchedOnlyWhenAskedFor()
    {
        var offlineAccount = Guid.NewGuid();
        _temp.Index.UpsertItems([CloudItem("/Old/report.doc")], offlineAccount);
        _temp.Index.ReplaceFiles("vol:9", "drive-external", "Shelf Disk", [new IndexedFile("vol:9", "/Scans/report.tif", "/Scans", "report.tif", false, 7, Stamp)]);
        var request = new SearchRequest("report")
        {
            OfflineAccounts = [new SearchAccount(offlineAccount, "Old Drive Account")],
            OfflineDrives = [new SearchDrive("vol:9", "Shelf Disk")],
        };

        var (without, noSources) = await Run(request);
        Assert.Empty(without);
        Assert.Empty(noSources);

        var (with, sources) = await Run(request with { IncludeOffline = true });
        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.True(s.IsOffline));

        var scan = Assert.Single(Items(with, "offline:vol:9"));
        Assert.Equal(SearchResultKind.Offline, scan.Kind);
        Assert.Equal(OfflineSourceKind.Drive, scan.OfflineKind);
        Assert.Equal("Shelf Disk (offline): /Scans", scan.LocationDescription);

        var doc = Assert.Single(Items(with, "offline:" + SearchIndex.SourceIdFor(offlineAccount)));
        Assert.Equal(OfflineSourceKind.Cloud, doc.OfflineKind);
        Assert.Equal(offlineAccount, doc.AccountId);
    }

    [Fact]
    public void TheNarrowScopesStayInTheActiveSource()
    {
        var active = Guid.NewGuid();
        var other = Guid.NewGuid();
        var request = new SearchRequest("x")
        {
            LocalDirectory = @"D:\Work",
            CloudAccountId = active,
            CloudPath = "/Docs",
            Accounts = [new SearchAccount(active, "Active"), new SearchAccount(other, "Other")],
            MountedDrives = [new SearchDrive("vol:1", "Disk", @"E:\")],
        };

        Assert.Equal(
            ["local", "drive:vol:1", "cloud:" + active, "cloud:" + other],
            SearchCoordinator.PlanSources(request).Select(s => s.Key));

        var source = Assert.Single(SearchCoordinator.PlanSources(request with { Scope = SearchScope.CurrentSource }));
        Assert.Equal(SearchSourceKind.CloudAccount, source.Kind);
        Assert.Equal("/Docs", source.Path);

        var folder = Assert.Single(SearchCoordinator.PlanSources(request with { Scope = SearchScope.CurrentFolder }));
        Assert.Equal(SearchSourceKind.CloudFolder, folder.Kind);

        var local = Assert.Single(SearchCoordinator.PlanSources(request with { CloudAccountId = null, Scope = SearchScope.CurrentSource }));
        Assert.Equal(SearchSourceKind.LocalFolder, local.Kind);
    }

    [Fact]
    public async Task TheCurrentCloudFolderIsListedAndFiltered()
    {
        var provider = new SearchableProvider();
        provider.Inner.AddFile("/Docs/report.txt", "x");
        provider.Inner.AddFile("/Docs/Sub/report-deep.txt", "x");
        provider.Inner.AddFile("/Docs/other.txt", "x");
        _hub.Register(provider.AccountId, provider);

        var (batches, _) = await Run(new SearchRequest("REPORT")
        {
            Scope = SearchScope.CurrentFolder,
            CloudAccountId = provider.AccountId,
            CloudPath = "/Docs",
            Accounts = [new SearchAccount(provider.AccountId, "Cloud")],
        });

        Assert.Equal("/Docs/report.txt", Assert.Single(Items(batches, "cloud:" + provider.AccountId)).Path);
        Assert.Equal(0, provider.SearchCalls);
    }

    [Fact]
    public async Task CancellingStopsTheStream()
    {
        _local.File("report.txt");
        using var cancellation = new CancellationTokenSource();
        var run = _coordinator.Start(new SearchRequest("report") { LocalDirectory = _local.Root }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in run.Batches)
            {
            }
        });
    }

    /// <summary>The request the search window builds: who is searched live, who from the index.</summary>
    [Fact]
    public void TheWindowsRequestSortsSourcesByWhetherTheyAreOnline()
    {
        var online = Account(Guid.NewGuid(), "Online");
        var forcedOffline = Account(Guid.NewGuid(), "Forced", offlineMode: true);
        var disconnected = Account(Guid.NewGuid(), "Gone", connected: false);
        KnownDrive[] drives =
        [
            new() { Id = "vol:1", VolumeId = "1", Kind = DriveKind.External, Label = "Mounted", IsOnline = true, RootPath = @"E:\" },
            new() { Id = "vol:2", VolumeId = "2", Kind = DriveKind.External, Label = "Shelf", LastIndexed = Stamp },
            new() { Id = "vol:3", VolumeId = "3", Kind = DriveKind.External, Label = "Never" },
        ];
        var context = new SearchContext(PanelLocation.Local(@"D:\Work"), [online, forcedOffline, disconnected]);

        var request = SearchRequest.Build("q", context, drives, includeOffline: true);

        Assert.Equal(@"D:\Work", request.LocalDirectory);
        Assert.Equal([online.Id], request.Accounts.Select(a => a.Id));
        Assert.Equal([forcedOffline.Id, disconnected.Id], request.OfflineAccounts.Select(a => a.Id));
        Assert.Equal(["vol:1"], request.MountedDrives.Select(d => d.Id));
        Assert.Equal(["vol:2"], request.OfflineDrives.Select(d => d.Id));

        var cloudActive = SearchRequest.Build("q", new SearchContext(PanelLocation.Cloud(online.Id, "/Docs"), [online]) { LocalFolder = @"D:\Last" }, [], false);
        Assert.Equal(online.Id, cloudActive.CloudAccountId);
        Assert.Equal("/Docs", cloudActive.CloudPath);
        Assert.Equal(@"D:\Last", cloudActive.LocalDirectory);

        var thisPc = SearchRequest.Build("q", new SearchContext(PanelLocation.Local(string.Empty), []), [], false);
        Assert.Null(thisPc.LocalDirectory);
    }
}
