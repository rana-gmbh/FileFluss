// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Search;
using FileFluss.Core.Services;
using FileFluss.Core.Settings;
using static FileFluss.Core.Tests.Search.SearchTestData;

namespace FileFluss.Core.Tests.Search;

[Collection("Localization")]
public sealed class SearchSessionTests : IDisposable
{
    private readonly TempIndex _temp = new();
    private readonly TempFolder _local = new();
    private readonly ProviderHub _hub = new();
    private readonly AppSettings _settings = new() { SearchIncludeOffline = false };
    private readonly List<CloudAccount> _accounts = [];
    private readonly SearchSession _session;

    public SearchSessionTests()
    {
        Localization.Use(AppLanguage.English);
        var drives = new DriveRegistry(_temp.Index, @"C:\");
        _session = new SearchSession(
            new SearchCoordinator(_temp.Index, _hub),
            () => new SearchContext(PanelLocation.Local(_local.Root), _accounts),
            drives,
            _settings);
    }

    public void Dispose()
    {
        _session.Dispose();
        _temp.Dispose();
        _local.Dispose();
    }

    private SearchableProvider ConnectAccount(string name)
    {
        var provider = new SearchableProvider();
        _hub.Register(provider.AccountId, provider);
        _accounts.Add(Account(provider.AccountId, name));
        return provider;
    }

    [Fact]
    public async Task ResultsStreamInPerSourceAndTheStatusSaysWhenAllAreIn()
    {
        _local.File("budget-2026.xlsx");
        var cloud = ConnectAccount("Dropbox (Work)");
        cloud.Inner.AddFile("/Finance/budget.pdf", "x");

        _session.SearchText = "budget";
        Assert.Equal(SearchStatus.Searching, _session.Status);
        await _session.Completion;

        Assert.Equal(SearchStatus.Complete, _session.Status);
        Assert.Equal(2, _session.ResultCount);
        Assert.Equal("2 results", _session.StatusText);
        Assert.Equal(2, _session.SourcesTotal);
        Assert.Equal(2, _session.SourcesCompleted);
        Assert.All(_session.Sources, s => Assert.Equal(SearchSourceState.Done, s.State));
        Assert.Equal(1, _session.Sources.Single(s => s.Kind == SearchSourceKind.CloudAccount).ResultCount);

        var groups = _session.GroupedResults;
        Assert.Equal(["Local Files", "Dropbox (Work)"], groups.Select(g => g.Title));
        Assert.Equal(SearchGroupKind.Local, groups[0].Kind);
        Assert.Equal(CloudProviderType.Dropbox, groups[1].ProviderType);
    }

    /// <summary>Typing on cancels the search for the shorter query; only the last one's results remain.</summary>
    [Fact]
    public async Task ChangingTheQueryCancelsTheRunningSearch()
    {
        _local.File("alpha.txt");
        _local.File("beta.txt");

        _session.SearchText = "alpha";
        var first = _session.Completion;
        _session.SearchText = "beta";
        await Task.WhenAll(first, _session.Completion);

        Assert.Equal("beta.txt", Assert.Single(_session.Results).Name);
        Assert.Equal(SearchStatus.Complete, _session.Status);
    }

    [Fact]
    public async Task ClearingResetsEverything()
    {
        _local.File("alpha.txt");
        _session.SearchText = "alpha";
        await _session.Completion;

        _session.Clear();

        Assert.Equal(string.Empty, _session.SearchText);
        Assert.Empty(_session.Results);
        Assert.Empty(_session.Sources);
        Assert.Equal(SearchStatus.Idle, _session.Status);
        Assert.Equal(string.Empty, _session.StatusText);

        _session.SearchText = "   ";
        Assert.Equal(SearchStatus.Idle, _session.Status);
    }

    [Fact]
    public async Task AFailedSourceIsMarkedButOthersStillReport()
    {
        _local.File("budget.txt");
        var cloud = ConnectAccount("Box");
        cloud.SearchFailure = new HttpRequestException("offline");

        _session.SearchText = "budget";
        await _session.Completion;

        var failed = _session.Sources.Single(s => s.Kind == SearchSourceKind.CloudAccount);
        Assert.Equal(SearchSourceState.Failed, failed.State);
        Assert.Equal("offline", failed.ErrorMessage);
        Assert.Equal(SearchSourceState.Done, _session.Sources.Single(s => s.Kind == SearchSourceKind.LocalFolder).State);
        Assert.Single(_session.Results);
    }

    /// <summary>"Show offline results" lives in the settings; flipping it searches again.</summary>
    [Fact]
    public async Task ShowOfflineResultsIsPersistedAndReruns()
    {
        var offline = Account(Guid.NewGuid(), "Archive Account", connected: false);
        _accounts.Add(offline);
        _temp.Index.UpsertItems([CloudItem("/Old/budget-2019.xls")], offline.Id);

        _session.SearchText = "budget";
        await _session.Completion;
        Assert.Empty(_session.Results);

        _session.IncludeOffline = true;
        Assert.True(_settings.SearchIncludeOffline);
        await _session.Completion;

        var hit = Assert.Single(_session.Results);
        Assert.True(hit.IsOffline);
        Assert.Equal("Archive Account (offline)", hit.GroupTitle);

        _settings.SearchIncludeOffline = false;
        Assert.False(_session.IncludeOffline);
        await _session.Completion;
        Assert.Empty(_session.Results);
    }

    [Fact]
    public async Task FiltersNarrowTheList()
    {
        _local.File("plan.txt");
        var work = ConnectAccount("Work");
        work.Inner.AddFile("/plan.docx", "x");
        var home = ConnectAccount("Home");
        home.Inner.AddFile("/plan.md", "x");

        _session.SearchText = "plan";
        await _session.Completion;

        var labels = _session.AvailableFilters.Select(f => f.Label).ToList();
        Assert.Equal(["All", "Local"], labels.Take(2));
        Assert.Equal(["Home", "Work"], labels.Skip(2).Order(StringComparer.Ordinal));
        _session.SourceFilter = SearchSourceFilter.Cloud(home.AccountId);
        Assert.Equal("/plan.md", Assert.Single(_session.FilteredResults).Path);
        _session.SourceFilter = SearchSourceFilter.Local;
        Assert.Equal(SearchResultKind.Local, Assert.Single(_session.FilteredResults).Kind);
        Assert.Single(_session.GroupedResults);
    }

    [Fact]
    public async Task OpeningAResultAsksTheAppToNavigate()
    {
        var file = _local.File(@"Deep\Folder\target.txt");
        _session.SearchText = "target";
        await _session.Completion;
        SearchOpenRequestedEventArgs? request = null;
        _session.OpenRequested += (_, e) => request = e;

        _session.OpenInPanel(_session.Results.Single(), PanelSide.Right);

        Assert.NotNull(request);
        Assert.Equal(PanelSide.Right, request.Side);
        Assert.Equal(SearchLocationKind.Local, request.Target.Kind);
        Assert.Equal(Path.GetDirectoryName(file), request.Target.FolderPath);
        Assert.Equal(file, request.Target.SelectPath);
        Assert.Equal(PanelLocation.Local(Path.GetDirectoryName(file)!), request.Target.ToPanelLocation());
    }
}

[Collection("Localization")]
public class SearchResultTests
{
    [Fact]
    public void CloudFilesOpenTheirFolderAndAreSelected()
    {
        Localization.Use(AppLanguage.English);
        var account = Guid.NewGuid();
        var result = SearchResultItem.Cloud(CloudItem("/Docs/2026/report.pdf") with { Id = "id:42" }, account, "Dropbox");

        Assert.Equal($"cloud:{account}:id:42", result.Id);
        Assert.Equal("Dropbox: /Docs/2026", result.LocationDescription);
        var target = result.Navigation;
        Assert.Equal(SearchLocationKind.Cloud, target.Kind);
        Assert.Equal("/Docs/2026", target.FolderPath);
        Assert.Equal("/Docs/2026/report.pdf", target.SelectPath);
        Assert.Equal(PanelLocation.Cloud(account, "/Docs/2026"), target.ToPanelLocation());
    }

    [Fact]
    public void FoldersAreOpenedThemselves()
    {
        var result = SearchResultItem.Cloud(CloudItem("/Docs/2026", isDirectory: true), Guid.NewGuid(), "Box");

        Assert.Equal("/Docs/2026", result.Navigation.FolderPath);
        Assert.Null(result.Navigation.SelectPath);
    }

    [Fact]
    public void OfflineResultsNameTheirSource()
    {
        var account = Guid.NewGuid();
        var drive = SearchResultItem.Offline(CloudItem("/Scans/a.tif"), OfflineSourceKind.Drive, "vol:9", "Shelf");
        var cloud = SearchResultItem.Offline(CloudItem("/a.doc"), OfflineSourceKind.Cloud, SearchIndex.SourceIdFor(account), "Old");

        Assert.Equal(SearchLocationKind.OfflineDrive, drive.Navigation.Kind);
        Assert.Equal("vol:9", drive.Navigation.DriveId);
        Assert.Equal("/Scans", drive.Navigation.FolderPath);
        Assert.Null(drive.Navigation.ToPanelLocation());

        Assert.Equal(SearchLocationKind.OfflineCloud, cloud.Navigation.Kind);
        Assert.Equal(account, cloud.Navigation.AccountId);
        Assert.Equal("/", cloud.Navigation.FolderPath);
        Assert.Equal(PanelLocation.Cloud(account, "/"), cloud.Navigation.ToPanelLocation());
        Assert.Equal($"offline:{SearchIndex.SourceIdFor(account)}:/a.doc", cloud.Id);
    }

    [Fact]
    public void ResultsAreLocalizedInGerman()
    {
        Localization.Use(AppLanguage.German);
        try
        {
            Assert.NotEqual("Indexed today", IndexingText.LastIndexed(DateTimeOffset.Now, DateTimeOffset.Now));
            Assert.NotEqual("Never indexed", IndexingText.LastIndexed(null, DateTimeOffset.Now));
        }
        finally
        {
            Localization.Use(AppLanguage.English);
        }
    }

    [Theory]
    [InlineData(0, "Indexed today")]
    [InlineData(1, "Indexed 1 day ago")]
    [InlineData(5, "Indexed 5 days ago")]
    public void LastIndexedReadsInDays(int days, string expected)
    {
        Localization.Use(AppLanguage.English);
        var now = DateTimeOffset.Now;
        Assert.Equal(expected, IndexingText.LastIndexed(now.AddDays(-days).AddMinutes(-1), now));
    }

    [Fact]
    public void TheSidebarSummaryCountsFiles()
    {
        Localization.Use(AppLanguage.English);
        var now = DateTimeOffset.Now;
        Assert.Equal("Indexed today · 1,234 files", IndexingText.IndexedSummary(now.AddHours(-2), 1234, now));
        Assert.Equal("Indexed 3 days ago · 7 files", IndexingText.IndexedSummary(now.AddDays(-3), 7, now));
    }
}
