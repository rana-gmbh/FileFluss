// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Buffers.Binary;
using System.Text;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;
using FileFluss.Core.VirtualFiles;

namespace FileFluss.Core.Tests.VirtualFiles;

public sealed class FileGroupDescriptorTests
{
    private static readonly DateTimeOffset March = new(2024, 3, 2, 11, 22, 33, TimeSpan.Zero);

    [Fact]
    public void LayoutMatchesTheWin32Structs()
    {
        // sizeof(FILEDESCRIPTORW) on every architecture: no pointers, 4-byte alignment.
        Assert.Equal(592, FileGroupDescriptor.DescriptorSize);
        Assert.Equal(72 + (260 * 2), FileGroupDescriptor.DescriptorSize);

        var bytes = FileGroupDescriptor.Build(
        [
            new VirtualFileEntry("a.txt", "/a.txt", false, 5, March, null),
            new VirtualFileEntry("Folder", "/Folder", true, null, null, null),
        ]);

        Assert.Equal(4 + (2 * 592), bytes.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
    }

    [Fact]
    public void FileEntryCarriesFlagsSizeTimeAndName()
    {
        var bytes = FileGroupDescriptor.Build([new VirtualFileEntry("Docs\\Report.pdf", "/Docs/Report.pdf", false, 5L * 1024 * 1024 * 1024 + 7, March, March.AddDays(-1))]);
        var d = bytes.AsSpan(4, 592);

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(d);
        Assert.Equal(
            FileGroupDescriptor.FdAttributes | FileGroupDescriptor.FdFileSize | FileGroupDescriptor.FdWriteTime |
            FileGroupDescriptor.FdCreateTime | FileGroupDescriptor.FdProgressUi | FileGroupDescriptor.FdUnicode,
            flags);
        Assert.Equal(FileGroupDescriptor.FileAttributeNormal, BinaryPrimitives.ReadUInt32LittleEndian(d[36..]));

        // 5 GiB + 7 straddles the 32-bit boundary: high DWORD first, then low.
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(d[64..]));
        Assert.Equal((uint)(((5L * 1024 * 1024 * 1024) + 7) & 0xFFFFFFFF), BinaryPrimitives.ReadUInt32LittleEndian(d[68..]));

        // FILETIME = 100 ns ticks since 1601 UTC, low DWORD first.
        Assert.Equal(March.UtcDateTime.ToFileTimeUtc(), BinaryPrimitives.ReadInt64LittleEndian(d[56..]));
        Assert.Equal(March.AddDays(-1).UtcDateTime.ToFileTimeUtc(), BinaryPrimitives.ReadInt64LittleEndian(d[40..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(d[48..])); // last access: not set

        var name = Encoding.Unicode.GetString(d.Slice(72, 520));
        Assert.Equal("Docs\\Report.pdf", name[..name.IndexOf('\0')]);
    }

    [Fact]
    public void FoldersAndUnknownValuesLeaveTheirFlagsOff()
    {
        var bytes = FileGroupDescriptor.Build(
        [
            new VirtualFileEntry("Folder", "/Folder", true, null, null, null),
            new VirtualFileEntry("Report.docx", "/Report", false, null, null, null),
        ], progressUi: false);

        var parsed = FileGroupDescriptor.Parse(bytes);
        Assert.True(parsed[0].IsDirectory);
        Assert.Equal(FileGroupDescriptor.FileAttributeDirectory, parsed[0].Attributes);
        Assert.Equal(FileGroupDescriptor.FdAttributes | FileGroupDescriptor.FdUnicode, parsed[0].Flags);
        Assert.Null(parsed[0].Size);

        // An exported Google document: the size is unknown until it is downloaded.
        Assert.False(parsed[1].IsDirectory);
        Assert.Null(parsed[1].Size);
        Assert.Null(parsed[1].Modified);
        Assert.Equal(0u, parsed[1].Flags & FileGroupDescriptor.FdProgressUi);
    }

    [Fact]
    public void ParseRoundTripsWhatBuildWrote()
    {
        var entries = new[]
        {
            new VirtualFileEntry("Ünïcødé 文件.txt", "/x", false, 0, March, null),
            new VirtualFileEntry("big.bin", "/big.bin", false, uint.MaxValue + 1L, March, null),
        };

        var parsed = FileGroupDescriptor.Parse(FileGroupDescriptor.Build(entries));

        Assert.Equal(new[] { "Ünïcødé 文件.txt", "big.bin" }, parsed.Select(p => p.Name));
        Assert.Equal(0, parsed[0].Size);
        Assert.Equal(uint.MaxValue + 1L, parsed[1].Size);
        Assert.Equal(March, parsed[1].Modified);
    }

    [Fact]
    public void FileTimeRejectsUnknownAndPre1601Dates()
    {
        Assert.Null(FileGroupDescriptor.ToFileTime(null));
        Assert.Null(FileGroupDescriptor.ToFileTime(CloudFileItem.UnknownDate));
        Assert.Null(FileGroupDescriptor.ToFileTime(new DateTimeOffset(1600, 12, 31, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(0, FileGroupDescriptor.ToFileTime(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(116444736000000000, FileGroupDescriptor.ToFileTime(DateTimeOffset.UnixEpoch));

        // Offsets don't matter: FILETIME is UTC.
        var local = new DateTimeOffset(2024, 3, 2, 13, 22, 33, TimeSpan.FromHours(2));
        Assert.Equal(FileGroupDescriptor.ToFileTime(March), FileGroupDescriptor.ToFileTime(local));
    }

    [Fact]
    public void NamesThatDoNotFitAreRefused()
    {
        var name = new string('a', 260);
        var error = Assert.Throws<VirtualFileTreeException>(() => FileGroupDescriptor.Build([new VirtualFileEntry(name, "/a", false, 1, null, null)]));
        Assert.Equal(VirtualFileTreeProblem.PathTooLong, error.Problem);

        var fits = new string('a', 259);
        Assert.Equal(fits, FileGroupDescriptor.Parse(FileGroupDescriptor.Build([new VirtualFileEntry(fits, "/a", false, 1, null, null)]))[0].Name);
    }
}

public sealed class VirtualFileTreeTests
{
    private static readonly DateTimeOffset Date = new(2023, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static MemoryCloudProvider Sample()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Photos/2026/a.jpg", "aaa", Date);
        cloud.AddFile("/Photos/2026/b.jpg", "bb", Date);
        cloud.AddFolder("/Photos/Empty");
        cloud.AddFile("/Photos/cover.png", "c", Date);
        cloud.AddFile("/notes.txt", "hello", Date);
        return cloud;
    }

    private static async Task<IReadOnlyList<CloudFileItem>> Items(FileProvider provider, params string[] names)
        => (await provider.ListDirectoryAsync("/")).Where(i => names.Contains(i.Name)).OrderBy(i => Array.IndexOf(names, i.Name)).ToList();

    [Fact]
    public async Task FoldersComeBeforeTheirContentsWithRelativePaths()
    {
        var cloud = Sample();
        var tree = await VirtualFileTree.BuildAsync(cloud, await Items(cloud, "Photos", "notes.txt"));

        var paths = tree.Entries.Select(e => e.RelativePath).ToList();
        Assert.Equal(7, paths.Count);
        Assert.Equal("Photos", paths[0]);
        Assert.Equal("notes.txt", paths[^1]);
        foreach (var entry in tree.Entries.Where(e => e.RelativePath.Contains('\\')))
        {
            var parent = entry.RelativePath[..entry.RelativePath.LastIndexOf('\\')];
            Assert.True(paths.IndexOf(parent) < paths.IndexOf(entry.RelativePath), $"{parent} must precede {entry.RelativePath}");
        }

        Assert.Contains("Photos\\2026\\a.jpg", paths);
        Assert.Contains("Photos\\Empty", paths);
        var a = tree.Entries.Single(e => e.RelativePath == "Photos\\2026\\a.jpg");
        Assert.Equal("/Photos/2026/a.jpg", a.RemotePath);
        Assert.Equal(3, a.Size);
        Assert.Equal(Date, a.Modified);
        Assert.Equal("a.jpg", a.Name);
        Assert.True(tree.Entries.Single(e => e.RelativePath == "Photos\\Empty").IsDirectory);

        Assert.Equal(4, tree.FileCount);
        Assert.Equal(3 + 2 + 1 + 5, tree.KnownBytes);
    }

    [Fact]
    public async Task UnsafeAndCollidingNamesAreMadeWindowsSafe()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dir/re:port?.txt", "1");
        cloud.AddFile("/Dir/re_port_.txt", "2");
        cloud.AddFile("/Dir/CON.txt", "3");
        cloud.AddFile("/Dir/Case.txt", "4");
        cloud.AddFile("/Dir/case.txt", "5");

        var tree = await VirtualFileTree.BuildAsync(cloud, await Items(cloud, "Dir"));
        var names = tree.Entries.Skip(1).Select(e => e.RelativePath).ToList();

        Assert.All(names, n => Assert.StartsWith("Dir\\", n));
        Assert.DoesNotContain(names, n => n.IndexOfAny([':', '?']) >= 0);
        Assert.Contains("Dir\\_CON.txt", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(names, n => n.EndsWith(" (2).txt", StringComparison.Ordinal));
    }

    [Fact]
    public void UniqueNameNumbersBeforeTheExtension()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a.txt", VirtualFileTree.UniqueName("a.txt", taken));
        Assert.Equal("A (2).TXT", VirtualFileTree.UniqueName("A.TXT", taken));
        Assert.Equal("a (3).txt", VirtualFileTree.UniqueName("a.txt", taken));
        Assert.Equal("README", VirtualFileTree.UniqueName("README", taken));
        Assert.Equal("README (2)", VirtualFileTree.UniqueName("README", taken));
    }

    [Fact]
    public async Task TooManyEntriesStopsTheWalk()
    {
        var cloud = new MemoryCloudProvider();
        for (var i = 0; i < 30; i++)
        {
            cloud.AddFile($"/Big/f{i}.txt", "x");
        }

        var big = await Items(cloud, "Big");
        var error = await Assert.ThrowsAsync<VirtualFileTreeException>(() => VirtualFileTree.BuildAsync(cloud, big, maxEntries: 20));
        Assert.Equal(VirtualFileTreeProblem.TooManyItems, error.Problem);

        var fits = await VirtualFileTree.BuildAsync(cloud, big, maxEntries: 31);
        Assert.Equal(31, fits.Entries.Count);
    }

    [Fact]
    public async Task DeepPathsBeyondMaxPathAreRefused()
    {
        var cloud = new MemoryCloudProvider();
        var segment = new string('d', 100);
        cloud.AddFile($"/{segment}/{segment}/{segment}/file.txt", "x");

        var error = await Assert.ThrowsAsync<VirtualFileTreeException>(async () => await VirtualFileTree.BuildAsync(cloud, await Items(cloud, segment)));
        Assert.Equal(VirtualFileTreeProblem.PathTooLong, error.Problem);
    }

    [Fact]
    public async Task ExportedDocumentsCarryTheExportExtensionAndNoSize()
    {
        var cloud = new ExportingProvider();
        cloud.Inner.AddFile("/Report", string.Empty, Date);
        cloud.Inner.AddFile("/plain.txt", "abc", Date);

        var tree = await VirtualFileTree.BuildAsync(cloud, await Items(cloud, "Report", "plain.txt"));

        Assert.Equal("Report.docx", tree.Entries[0].RelativePath);
        Assert.Null(tree.Entries[0].Size);
        Assert.Equal("/Report", tree.Entries[0].RemotePath);
        Assert.Equal("plain.txt", tree.Entries[1].RelativePath);
        Assert.Equal(3, tree.Entries[1].Size);
    }

    [Fact]
    public async Task SyntheticContainersAreLeftOut()
    {
        var cloud = Sample();
        var items = (await Items(cloud, "notes.txt")).Append(new CloudFileItem("v", "Shared drives", "/Shared drives", true, 0, Date) { Role = CloudItemRole.VirtualContainer }).ToList();

        var tree = await VirtualFileTree.BuildAsync(cloud, items);

        Assert.Equal(new[] { "notes.txt" }, tree.Entries.Select(e => e.RelativePath));
    }

    [Fact]
    public async Task CancellationStopsTheListing()
    {
        var cloud = Sample();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await VirtualFileTree.BuildAsync(cloud, await Items(cloud, "Photos"), cancellationToken: cancel.Token));
    }
}

public sealed class VirtualFileSourceTests
{
    private static async Task<string> ReadAllAsync(Task<FileStream> open)
    {
        await using var stream = await open;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<VirtualFileSource> SourceAsync(FileProvider provider, params string[] names)
    {
        var items = (await provider.ListDirectoryAsync("/")).Where(i => names.Contains(i.Name)).ToList();
        return new VirtualFileSource(provider, items, PanelSide.Left) { IdleTimeout = TimeSpan.FromMilliseconds(200) };
    }

    private static int IndexOf(VirtualFileTree tree, string relative) => tree.Entries.ToList().FindIndex(e => e.RelativePath == relative);

    [Fact]
    public void OnlyRealCloudProvidersCanProvideContents()
    {
        Assert.True(VirtualFileSource.CanProvideContents(new MemoryCloudProvider()));
        Assert.False(VirtualFileSource.CanProvideContents(LocalFileProvider.Shared));
    }

    [Fact]
    public async Task NothingIsListedOrDownloadedUntilAskedFor()
    {
        var cloud = new CountingProvider();
        cloud.Inner.AddFile("/a.txt", "alpha");
        using var source = await SourceAsync(cloud, "a.txt");
        var listings = cloud.Listings;

        await Task.Delay(50);
        Assert.False(source.IsTreeStarted);
        Assert.Equal(listings, cloud.Listings);
        Assert.Equal(0, cloud.Downloads);

        var tree = await source.TreeAsync();
        Assert.Single(tree.Entries);
        Assert.Equal(0, cloud.Downloads);
    }

    [Fact]
    public async Task OpenDownloadsOnceAndServesTheStagedFile()
    {
        var cloud = new CountingProvider();
        cloud.Inner.AddFile("/Folder/one.txt", "first");
        cloud.Inner.AddFile("/Folder/two.txt", "second");
        using var source = await SourceAsync(cloud, "Folder");
        TransferProgress? progress = null;
        source.SessionStarted += p => progress = p;

        var tree = await source.TreeAsync();
        var one = IndexOf(tree, "Folder\\one.txt");
        Assert.Equal("first", await ReadAllAsync(source.OpenAsync(one)));
        Assert.Equal("first", await ReadAllAsync(source.OpenAsync(one)));
        Assert.Equal("second", await ReadAllAsync(source.OpenAsync(IndexOf(tree, "Folder\\two.txt"))));

        Assert.Equal(2, cloud.Downloads); // look-ahead fetched "two" with "one"; nothing twice
        Assert.NotNull(progress);
        Assert.Equal(TransferOperation.Downloading, progress!.Operation);
        Assert.Equal(PanelSide.Left, progress.Panel);
        Assert.Equal(2, progress.TotalFiles);
        Assert.Equal(11, progress.ExpectedBytes);
        Assert.Equal(11, progress.DownloadBytes);
        Assert.Equal(new[] { "Folder" }, progress.ItemNames);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.OpenAsync(IndexOf(tree, "Folder")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.OpenAsync(99));
    }

    [Fact]
    public async Task EndOperationFinishesTheSessionAndDeletesStagedFiles()
    {
        var cloud = new CountingProvider();
        cloud.Inner.AddFile("/a.txt", "alpha");
        using var source = await SourceAsync(cloud, "a.txt");
        var ended = new TaskCompletionSource<TransferProgress>();
        source.SessionEnded += p => ended.TrySetResult(p);

        source.BeginOperation();
        string staged;
        await using (var stream = await source.OpenAsync(0))
        {
            staged = stream.Name;
            Assert.StartsWith(StagingLocation.Base(), staged, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("alpha", await new StreamReader(stream).ReadToEndAsync());
        }

        source.EndOperation();
        var progress = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(progress.IsComplete);
        Assert.True(progress.CompletedCleanly);
        Assert.False(File.Exists(staged));
        Assert.False(Directory.Exists(Path.GetDirectoryName(staged)));

        // The same source serves a second paste: a new session downloads again.
        Assert.Equal("alpha", await ReadAllAsync(source.OpenAsync(0)));
        Assert.Equal(2, cloud.Downloads);
    }

    [Fact]
    public async Task AnIdleSessionEndsByItself()
    {
        var cloud = new CountingProvider();
        cloud.Inner.AddFile("/a.txt", "alpha");
        using var source = await SourceAsync(cloud, "a.txt");
        var ended = new TaskCompletionSource<TransferProgress>();
        source.SessionEnded += p => ended.TrySetResult(p);

        Assert.Equal("alpha", await ReadAllAsync(source.OpenAsync(0)));

        var progress = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(progress.IsComplete);
    }

    [Fact]
    public async Task ExportedFilesAreFoundUnderTheirExportName()
    {
        var cloud = new ExportingProvider();
        cloud.Inner.AddFile("/Report", "docx bytes");
        using var source = await SourceAsync(cloud, "Report");

        var tree = await source.TreeAsync();
        Assert.Equal("Report.docx", tree.Entries[0].RelativePath);
        Assert.Equal("docx bytes", await ReadAllAsync(source.OpenAsync(0)));
    }

    [Fact]
    public async Task AFailedDownloadIsReportedAndRetriedOnTheNextRequest()
    {
        var cloud = new CountingProvider { FailNext = 1 };
        cloud.Inner.AddFile("/a.txt", "alpha");
        using var source = await SourceAsync(cloud, "a.txt");
        TransferProgress? progress = null;
        source.SessionStarted += p => progress = p;

        await Assert.ThrowsAsync<CloudProviderException>(() => source.OpenAsync(0));
        Assert.Equal(1, progress!.FailureCount);

        Assert.Equal("alpha", await ReadAllAsync(source.OpenAsync(0)));
        Assert.Equal(2, cloud.Downloads);
    }

    [Fact]
    public async Task CancellingTheTransferFailsReadsAsCancelled()
    {
        var cloud = new CountingProvider { Gate = new TaskCompletionSource() };
        cloud.Inner.AddFile("/a.txt", "alpha");
        using var source = await SourceAsync(cloud, "a.txt");
        var started = new TaskCompletionSource<TransferProgress>();
        source.SessionStarted += p => started.TrySetResult(p);

        var open = source.OpenAsync(0);
        var progress = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        progress.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
    }

    [Fact]
    public async Task RetireDisposesOnceNothingIsRunning()
    {
        var cloud = new CountingProvider();
        cloud.Inner.AddFile("/a.txt", "alpha");
        var source = await SourceAsync(cloud, "a.txt");

        source.BeginOperation();
        Assert.Equal("alpha", await ReadAllAsync(source.OpenAsync(0)));
        source.Retire();
        Assert.False(source.IsDisposed); // Explorer's operation is still open

        source.EndOperation();
        await WaitUntil(() => source.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.OpenAsync(0));
    }

    [Fact]
    public async Task TreeFailuresSurfaceFromTheTask()
    {
        var cloud = new MemoryCloudProvider();
        for (var i = 0; i < 5; i++)
        {
            cloud.AddFile($"/Big/{i}.txt", "x");
        }

        var items = (await cloud.ListDirectoryAsync("/")).ToList();
        using var source = new VirtualFileSource(cloud, items, PanelSide.Right, maxEntries: 3);

        var error = await Assert.ThrowsAsync<VirtualFileTreeException>(source.TreeAsync);
        Assert.Equal(VirtualFileTreeProblem.TooManyItems, error.Problem);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}

/// <summary>A memory cloud that counts calls, can fail or hold a download.</summary>
internal sealed class CountingProvider : FileProvider
{
    private int _listings;
    private int _downloads;

    public MemoryCloudProvider Inner { get; } = new();

    public int Listings => _listings;

    public int Downloads => _downloads;

    public int FailNext { get; set; }

    public TaskCompletionSource? Gate { get; init; }

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _listings);
        return Inner.ListDirectoryAsync(path, cancellationToken);
    }

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _downloads);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (FailNext > 0)
        {
            FailNext--;
            throw CloudProviderException.ServerError(500);
        }

        await Inner.DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);
    }

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default) => Inner.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default) => Inner.DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Inner.CreateDirectoryAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default) => Inner.RenameItemAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default) => Inner.GetFileMetadataAsync(path, cancellationToken);
}

/// <summary>Behaves like Google Drive for files without an extension: they download as "name.docx".</summary>
internal sealed class ExportingProvider : FileProvider
{
    public MemoryCloudProvider Inner { get; } = new();

    public override ValueTask<string?> ExportExtensionAsync(CloudFileItem item, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(!item.IsDirectory && !item.Name.Contains('.') ? "docx" : null);

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default) => Inner.ListDirectoryAsync(path, cancellationToken);

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var target = GetName(remotePath).Contains('.') ? localPath : localPath + ".docx";
        await Inner.DownloadFileAsync(remotePath, target, onBytes, cancellationToken);
    }

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default) => Inner.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default) => Inner.DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Inner.CreateDirectoryAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default) => Inner.RenameItemAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default) => Inner.GetFileMetadataAsync(path, cancellationToken);
}
