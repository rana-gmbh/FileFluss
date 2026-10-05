// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Providers;
using FileFluss.Core.Storage;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Tests.Sync;

namespace FileFluss.Core.Tests.Storage;

public class StorageTreeTests
{
    private static StorageEntry File(string path, long size) => new(path, path[(path.LastIndexOf('/') + 1)..], false, size, null);

    private static StorageEntry Folder(string path) => new(path, path[(path.LastIndexOf('/') + 1)..], true, 0, null);

    [Fact]
    public void FoldersRollUpAndSortBiggestFirst()
    {
        var report = StorageTreeBuilder.Build(
            [File("/a/x", 100), Folder("/a"), File("/b", 300), File("/a/y", 50), File("/c/d/e", 10), Folder("/a/")],
            "/",
            "Dropbox",
            new MemoryCloudProvider());

        var root = report.Root;
        Assert.Equal("Dropbox", root.Name);
        Assert.Equal("/", root.Id);
        Assert.Equal(460, root.Size);
        Assert.Equal(460, report.TotalBytes);
        Assert.Equal(4, root.FileCount);
        Assert.Equal(4, report.FileCount);

        // /c and /c/d were never listed themselves; they are made up so /c/d/e isn't lost.
        Assert.Equal(3, report.FolderCount);
        Assert.Equal(["b", "a", "c"], root.Children.Select(c => c.Name).ToArray());
        Assert.Equal(["x", "y"], root.Children[1].Children.Select(c => c.Name).ToArray());
        Assert.Equal(150, root.Children[1].Size);
        Assert.Equal("/c/d", root.Children[2].Children.Single().Path);
        Assert.Equal(["/b", "/a/x", "/a/y", "/c/d/e"], report.LargestFiles.Select(f => f.Path).ToArray());
        Assert.Equal(300.0 / 460, root.Children[0].Fraction(root.Size), 6);
        Assert.False(report.WasCancelled);
    }

    [Fact]
    public void TheLargestFilesListIsCapped()
    {
        var entries = Enumerable.Range(1, 20).Select(i => File($"/f{i}", i)).ToList();
        var report = StorageTreeBuilder.Build(entries, "/", "x", new MemoryCloudProvider(), largestFileLimit: 5);
        Assert.Equal([20L, 19, 18, 17, 16], report.LargestFiles.Select(f => f.Size).ToArray());
    }

    [Fact]
    public void ASubfolderRootKeepsItsOwnName()
    {
        var report = StorageTreeBuilder.Build([File("/Photos/2026/a.jpg", 5)], "/Photos/", "Photos", new MemoryCloudProvider());
        Assert.Equal("/Photos", report.Root.Path);
        Assert.Equal("2026", report.Root.Children.Single().Name);
        Assert.Equal(5, report.TotalBytes);
    }

    [Fact]
    public void LocalPathsUseWindowsSyntax()
    {
        var report = StorageTreeBuilder.Build(
            [
                new StorageEntry(@"C:\Data\big.iso", "big.iso", false, 900, null),
                new StorageEntry(@"C:\Data\Docs\a.txt", "a.txt", false, 100, null),
            ],
            @"C:\Data\",
            "Data",
            LocalFileProvider.Shared);

        Assert.Equal(@"C:\Data", report.Root.Path);
        Assert.Equal(["big.iso", "Docs"], report.Root.Children.Select(c => c.Name).ToArray());
        Assert.Equal(@"C:\Data\Docs", report.Root.Children[1].Path);
        Assert.Equal(1000, report.TotalBytes);
    }
}

public sealed class StorageScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-storage-" + Guid.NewGuid().ToString("N"));

    public StorageScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static MemoryCloudProvider Account()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Videos/movie.mp4", new string('m', 500));
        cloud.AddFile("/Videos/clip.mp4", new string('c', 100));
        cloud.AddFile("/Docs/a.txt", new string('a', 20));
        cloud.AddFile("/readme.md", new string('r', 5));
        return cloud;
    }

    [Fact]
    public async Task ScanningAnAccountTotalsEverything()
    {
        var cloud = Account();
        var source = StorageScanSource.ForAccount(cloud, "Box (Robert)");

        var report = await StorageScanner.ScanAsync(source);

        Assert.Equal("Box (Robert)", source.Title);
        Assert.Equal("Box (Robert)", report.Root.Name);
        Assert.Equal(625, report.TotalBytes);
        Assert.Equal(4, report.FileCount);
        Assert.Equal(2, report.FolderCount);
        Assert.Equal("Videos", report.Root.Children[0].Name);
        Assert.Equal("/Videos/movie.mp4", report.LargestFiles[0].Path);
    }

    [Fact]
    public async Task ASubfolderIsTitledWithItsPath()
    {
        var source = StorageScanSource.ForAccount(Account(), "Box", "/Videos");
        var report = await StorageScanner.ScanAsync(source);

        Assert.Equal("Box — /Videos", source.Title);
        Assert.Equal("Videos", report.Root.Name);
        Assert.Equal(600, report.TotalBytes);
    }

    [Fact]
    public async Task AnUnreadableFolderIsSkippedButAnUnreadableRootFails()
    {
        var wrapped = new WrappingProvider(Account());
        wrapped.FailingListings.Add("/Videos");
        var report = await StorageScanner.ScanAsync(new StorageScanSource(wrapped, "/", "Box"));
        Assert.Equal(25, report.TotalBytes);

        wrapped.FailingListings.Add("/");
        await Assert.ThrowsAsync<CloudProviderException>(() => StorageScanner.ScanAsync(new StorageScanSource(wrapped, "/", "Box")));
    }

    [Fact]
    public async Task PartialReportsArriveWhileScanning()
    {
        var updates = new List<StorageScanUpdate>();
        var report = await StorageScanner.ScanAsync(
            new StorageScanSource(Account(), "/", "Box"),
            updates.Add,
            TimeSpan.Zero,
            partialMinimumFiles: 1,
            CancellationToken.None);

        var partials = updates.Where(u => u.Partial is not null).ToList();
        Assert.True(partials.Count >= 3);
        Assert.True(partials[0].Partial!.TotalBytes < report.TotalBytes);
        Assert.Same(report, updates[^1].Partial);
        Assert.Equal(string.Empty, updates[^1].CurrentPath);
    }

    [Fact]
    public async Task CancellingKeepsWhatWasFound()
    {
        using var cts = new CancellationTokenSource();
        var wrapped = new WrappingProvider(Account());
        wrapped.BeforeList = (path, ct) =>
        {
            if (path != "/")
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        var report = await StorageScanner.ScanAsync(new StorageScanSource(wrapped, "/", "Box"), cancellationToken: cts.Token);

        Assert.True(report.WasCancelled);
        Assert.Equal(5, report.TotalBytes);
    }

    [Fact]
    public async Task ALocalScanSkipsHiddenFiles()
    {
        File.WriteAllText(Path.Combine(_root, "visible.bin"), new string('v', 40));
        var hidden = Path.Combine(_root, "hidden.bin");
        File.WriteAllText(hidden, new string('h', 400));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "inner.bin"), new string('i', 4));

        var source = StorageScanSource.ForLocal(_root);
        var report = await StorageScanner.ScanAsync(source);

        Assert.Equal(_root, source.Title);
        Assert.Equal(Path.GetFileName(_root), report.Root.Name);
        Assert.Equal(44, report.TotalBytes);
        Assert.Equal(2, report.FileCount);
        Assert.Equal(Path.Combine(_root, "sub", "inner.bin"), report.LargestFiles[1].Path);
    }
}

[Collection("Localization")]
public class StorageAnalysisSessionTests
{
    public StorageAnalysisSessionTests() => Localization.Use(AppLanguage.English);

    private static MemoryCloudProvider Account()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Videos/movie.mp4", new string('m', 2_000_000));
        cloud.AddFile("/Videos/tiny.txt", "t");
        cloud.AddFile("/readme.md", new string('r', 5));
        return cloud;
    }

    [Fact]
    public async Task AScanFillsTheTreeWithTheRootOpen()
    {
        var session = new StorageAnalysisSession();
        var source = StorageScanSource.ForAccount(Account(), "Box");

        await session.ScanAsync(source);

        Assert.False(session.IsScanning);
        Assert.Same(source, session.ScannedSource);
        Assert.Equal(["Box", "Videos", "readme.md"], session.Rows.Select(r => r.Node.Name).ToArray());
        Assert.True(session.Rows[0].IsExpanded);
        Assert.True(session.Rows[1].IsExpandable);
        Assert.False(session.Rows[1].IsExpanded);
        Assert.Equal(1, session.Rows[1].Depth);
        Assert.Equal("1.90 MB", session.Rows[1].SizeText);
        Assert.Equal("100.0% of the total", session.Rows[1].ShareText);
        Assert.Equal("1.90 MB · 3 files · 1 folders", session.SummaryText);
        Assert.Equal(3, session.FilesSeen);
        Assert.Null(session.CancelledNotice);

        session.ToggleExpanded(session.Rows[1].Node);
        Assert.Equal(["Box", "Videos", "movie.mp4", "tiny.txt", "readme.md"], session.Rows.Select(r => r.Node.Name).ToArray());
        Assert.True(session.IsExpanded(session.Rows[1].Node));
    }

    [Fact]
    public async Task TheSizeFilterHidesSmallFilesButNotFolders()
    {
        var session = new StorageAnalysisSession();
        await session.ScanAsync(StorageScanSource.ForAccount(Account(), "Box"));
        session.ToggleExpanded(session.Rows[1].Node);

        session.MinimumSize = StorageAnalysisSession.SizeThresholds[1].Bytes;

        Assert.Equal("1 MB and up", StorageAnalysisSession.SizeThresholds[1].Label);
        Assert.Equal(["Box", "Videos", "movie.mp4"], session.Rows.Select(r => r.Node.Name).ToArray());
        Assert.Equal(["movie.mp4"], session.LargestFiles.Select(f => f.Name).ToArray());

        session.MinimumSize = StorageAnalysisSession.SizeThresholds[4].Bytes;
        Assert.Empty(session.LargestFiles);
        Assert.Equal("No files above this size.", StorageAnalysisSession.NoLargeFilesText);
    }

    [Fact]
    public async Task CancellingShowsTheLowerBoundNotice()
    {
        var session = new StorageAnalysisSession();
        var wrapped = new WrappingProvider(Account());
        wrapped.BeforeList = (path, ct) =>
        {
            if (path != "/")
            {
                session.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        await session.ScanAsync(new StorageScanSource(wrapped, "/", "Box"));

        Assert.True(session.Report!.WasCancelled);
        Assert.Equal("Stopped early, so these totals are a lower bound — the real figures are larger.", session.CancelledNotice);
    }

    [Fact]
    public async Task AnUnreadableRootIsAnError()
    {
        var wrapped = new WrappingProvider(Account());
        wrapped.FailingListings.Add("/");
        var session = new StorageAnalysisSession();

        await session.ScanAsync(new StorageScanSource(wrapped, "/", "Box"));

        Assert.Equal("listing refused", session.ErrorMessage);
        Assert.Null(session.Report);
        Assert.False(session.IsScanning);
    }

    [Fact]
    public async Task ProgressTextCountsFilesAndBytes()
    {
        var gate = new TaskCompletionSource();
        var wrapped = new WrappingProvider(Account()) { BeforeList = (_, ct) => gate.Task.WaitAsync(ct) };
        var session = new StorageAnalysisSession();

        var scan = session.ScanAsync(new StorageScanSource(wrapped, "/", "Box"));
        Assert.True(session.IsScanning);
        Assert.Equal("0 files · 0 bytes", session.ProgressText);
        gate.SetResult();
        await scan;

        Assert.False(session.IsScanning);
        Assert.Equal("3 files · 1.90 MB", session.ProgressText);
    }
}
