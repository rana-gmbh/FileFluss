// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Search;
using FileFluss.Core.Services;
using static FileFluss.Core.Tests.Search.SearchTestData;

namespace FileFluss.Core.Tests.Search;

[Collection("Localization")]
public sealed class IndexingServiceTests : IDisposable
{
    private const string SystemRoot = @"C:\";
    private readonly TempIndex _temp = new();
    private readonly TempFolder _disk = new();
    private readonly ProviderHub _hub = new();
    private readonly DriveRegistry _drives;
    private readonly IndexingService _service;
    private readonly List<IndexingFinishedEventArgs> _finished = [];

    public IndexingServiceTests()
    {
        Localization.Use(AppLanguage.English);
        _drives = new DriveRegistry(_temp.Index, SystemRoot);
        _service = new IndexingService(_temp.Index, _hub, _drives);
        _service.IndexingFinished += (_, e) =>
        {
            lock (_finished)
            {
                _finished.Add(e);
            }
        };
    }

    public void Dispose()
    {
        _temp.Dispose();
        _disk.Dispose();
    }

    private KnownDrive MountDisk(string serial = "D15C-0001")
    {
        _drives.UpdateMounted([new DriveIdentity(serial, "Archive", _disk.Root, DriveType.Removable)]);
        return _drives.Find(KnownDrive.SourceIdFor(serial))!;
    }

    // MARK: - Drives

    [Fact]
    public async Task ADriveIsIndexedWithPathsRelativeToItsRoot()
    {
        _disk.File(@"Photos\2026\beach.jpg", "12345");
        _disk.File(@"Photos\notes.txt");
        _disk.File("readme.md");
        var hidden = _disk.File("secret.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var drive = MountDisk();

        var job = _service.IndexDrive(drive.Id);
        Assert.NotNull(job);
        await job.Completion;

        Assert.Null(job.ErrorMessage);
        Assert.False(job.IsCancelled);
        Assert.True(job.IsComplete);
        Assert.Equal(5, job.FilesProcessed);
        Assert.Equal(5, job.StoredCount);

        var index = _temp.Index;
        Assert.Equal(["Photos", "readme.md"], index.ListChildren(drive.Id, "/").Select(f => f.Name));
        var beach = Assert.Single(index.SearchIndexed("beach", [drive.Id]));
        Assert.Equal("/Photos/2026/beach.jpg", beach.Path);
        Assert.Equal("/Photos/2026", beach.ParentPath);
        Assert.Equal(5, beach.Size);
        Assert.Empty(index.SearchIndexed("secret", [drive.Id]));

        var source = index.GetSource(drive.Id);
        Assert.Equal("drive-external", source?.Kind);
        Assert.Equal("Archive", source?.DisplayName);

        // The registry says what the index holds.
        var recorded = _drives.Find(drive.Id)!;
        Assert.True(recorded.IsIndexed);
        Assert.Equal(5, recorded.TotalFiles);
        Assert.Equal(source?.TotalBytes, recorded.TotalBytes);

        var finished = Assert.Single(_finished);
        Assert.True(finished.Succeeded);
        Assert.Equal(IndexingJobKind.Drive, finished.Kind);
        Assert.Null(_service.ActiveJob(drive.Id));
    }

    [Fact]
    public async Task ReindexingReplacesTheDrivesIndex()
    {
        var gone = _disk.File("old.txt");
        var drive = MountDisk();
        await _service.IndexDrive(drive.Id)!.Completion;

        File.Delete(gone);
        _disk.File("new.txt");
        await _service.IndexDrive(drive.Id)!.Completion;

        Assert.Empty(_temp.Index.SearchIndexed("old", [drive.Id]));
        Assert.Single(_temp.Index.SearchIndexed("new", [drive.Id]));
    }

    /// <summary>
    /// macOS walked nothing when the mount was gone and promoted the empty walk, replacing a
    /// good index with nothing. A missing root is a failure, and the previous index stays.
    /// </summary>
    [Fact]
    public async Task AMissingRootFailsAndKeepsThePreviousIndex()
    {
        _disk.File("keep.txt");
        var drive = MountDisk();
        await _service.IndexDrive(drive.Id)!.Completion;

        var job = _service.IndexDrive(drive, Path.Combine(_disk.Root, "not-there"));
        await job.Completion;

        Assert.NotNull(job.ErrorMessage);
        Assert.Null(job.StoredCount);
        Assert.Single(_temp.Index.SearchIndexed("keep", [drive.Id]));
        Assert.False(_finished.Last().Succeeded);
    }

    [Fact]
    public void AnUnmountedDriveCannotBeIndexed()
    {
        var drive = MountDisk();
        _drives.RecordIndexed(drive.Id, DateTimeOffset.Now, 0, 0);
        _drives.UpdateMounted([]);

        Assert.Null(_service.IndexDrive(drive.Id));
    }

    // MARK: - Cloud accounts

    [Fact]
    public async Task ACloudAccountIsWalkedAndRecorded()
    {
        var provider = new SearchableProvider();
        provider.Inner.AddFile("/Docs/report.pdf", "abc");
        provider.Inner.AddFile("/Docs/Old/archive.zip", "abcdef");
        provider.Inner.AddFile("/top.txt", "x");
        var account = Account(provider.AccountId, "Dropbox (Robert)");
        _hub.Register(account.Id, provider);

        var job = _service.IndexCloudAccount(account);
        await job.Completion;

        Assert.Null(job.ErrorMessage);
        Assert.Equal(5, job.FilesProcessed);
        Assert.Equal(5, job.StoredCount);
        Assert.Equal("/Docs/Old/archive.zip", Assert.Single(_temp.Index.Search("archive", account.Id)).Item.Path);

        var info = _temp.Index.FullCloudIndexInfo(account.Id);
        Assert.NotNull(info);
        Assert.Equal(3, info.TotalFiles);
        Assert.Equal(2, info.TotalFolders);
        Assert.Equal(10, info.TotalBytes);
        Assert.Equal("Dropbox (Robert)", _temp.Index.GetSource(SearchIndex.SourceIdFor(account.Id))?.DisplayName);

        var finished = Assert.Single(_finished);
        Assert.Equal(account.Id, finished.AccountId);
        Assert.True(finished.Succeeded);
    }

    /// <summary>A re-index is authoritative: what was deleted since the last run leaves the index.</summary>
    [Fact]
    public async Task ReindexingAnAccountRetiresWhatWasDeleted()
    {
        var provider = new SearchableProvider();
        provider.Inner.AddFile("/Docs/report.pdf", "abc");
        provider.Inner.AddFile("/Docs/Old/archive.zip", "abcdef");
        var account = Account(provider.AccountId);
        _hub.Register(account.Id, provider);
        await _service.IndexCloudAccount(account).Completion;

        await provider.Inner.DeleteItemAsync("/Docs/Old");
        await _service.IndexCloudAccount(account).Completion;

        Assert.Empty(_temp.Index.Search("archive", account.Id));
        Assert.Null(_temp.Index.CloudEntry(account.Id, "/Docs/Old"));
        Assert.Single(_temp.Index.Search("report", account.Id));
    }

    [Fact]
    public async Task AnUnreadableFolderIsSkippedNotFatal()
    {
        var provider = new SearchableProvider();
        provider.Inner.AddFile("/Good/a.txt", "x");
        provider.Inner.AddFile("/Bad/b.txt", "x");
        provider.FailingListings.Add("/Bad");
        var account = Account(provider.AccountId);
        _hub.Register(account.Id, provider);

        var job = _service.IndexCloudAccount(account);
        await job.Completion;

        Assert.Null(job.ErrorMessage);
        Assert.Single(_temp.Index.Search("a", account.Id));
        Assert.NotNull(_temp.Index.FullCloudIndexInfo(account.Id));
    }

    [Fact]
    public async Task AnAccountWhoseRootCantBeListedIsNotRecordedAsIndexed()
    {
        var provider = new SearchableProvider();
        provider.FailingListings.Add("/");
        var account = Account(provider.AccountId);
        _hub.Register(account.Id, provider);

        var job = _service.IndexCloudAccount(account);
        await job.Completion;

        Assert.NotNull(job.ErrorMessage);
        Assert.Null(_temp.Index.GetSource(SearchIndex.SourceIdFor(account.Id)));
    }

    [Fact]
    public async Task ADisconnectedAccountSaysSo()
    {
        var job = _service.IndexCloudAccount(Account(Guid.NewGuid()));
        await job.Completion;

        Assert.Equal("Account not connected", job.ErrorMessage);
    }

    [Fact]
    public async Task CancellingKeepsTheAccountUnrecorded()
    {
        var provider = new SearchableProvider { ListingGate = new TaskCompletionSource() };
        provider.Inner.AddFile("/a.txt", "x");
        var account = Account(provider.AccountId);
        _hub.Register(account.Id, provider);

        var job = _service.IndexCloudAccount(account);
        Assert.Same(job, _service.ActiveJob(SearchIndex.SourceIdFor(account.Id)));
        _service.Cancel(SearchIndex.SourceIdFor(account.Id));
        await job.Completion;

        Assert.True(job.IsCancelled);
        Assert.Equal("Cancelled", job.ErrorMessage);
        Assert.Null(_temp.Index.GetSource(SearchIndex.SourceIdFor(account.Id)));
        Assert.Contains(job, _service.Jobs);
    }

    /// <summary>
    /// One run per source: a second request cancels the first and waits for it to stop, so
    /// two walks never write the same source at once.
    /// </summary>
    [Fact]
    public async Task StartingAgainReplacesTheRunningRun()
    {
        var gate = new TaskCompletionSource();
        var provider = new SearchableProvider { ListingGate = gate };
        provider.Inner.AddFile("/Docs/a.txt", "x");
        var account = Account(provider.AccountId);
        _hub.Register(account.Id, provider);

        var first = _service.IndexCloudAccount(account);
        var second = _service.IndexCloudAccount(account);
        gate.SetResult();
        await Task.WhenAll(first.Completion, second.Completion);

        Assert.True(first.IsCancelled);
        Assert.Null(second.ErrorMessage);
        Assert.False(second.IsCancelled);
        Assert.NotNull(_temp.Index.FullCloudIndexInfo(account.Id));
        Assert.Equal(2, _service.Jobs.Count);

        _service.Remove(first.Id);
        Assert.Equal(new[] { second }, _service.Jobs);
    }
}
