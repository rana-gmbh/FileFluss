// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.RegularExpressions;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests;

/// <summary>
/// macOS 1.6 "Replace never leaves nothing": the new copy is written under a temporary name and
/// swapped in once complete — in a sync, a manual Replace and the conflict dialog alike — and a
/// server-side replace sets the old item aside instead of deleting it first. Plus the other
/// transfer items of that release: a file can't replace the folder it's in, Keep Both keeps
/// both, and downloads carry the mark of the web.
/// </summary>
public sealed class TransferReplaceSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-replace-" + Guid.NewGuid().ToString("N"));

    public TransferReplaceSafetyTests()
    {
        Directory.CreateDirectory(_root);
        LocalFileProvider.RecycleHandler = null;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LocalFile(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<TransferProgress> Run(TransferRequest request)
    {
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);
        return progress;
    }

    private static ConflictResolver Always(ConflictChoice choice)
        => (_, _) => Task.FromResult(new ConflictResolution(choice, true));

    private static bool IsTemporary(string path) => path.Contains(".filefluss-part-", StringComparison.Ordinal);

    // MARK: - Temporary names

    [Theory]
    [InlineData("Report.pdf", "^Report\\.filefluss-part-[0-9a-f]{8}\\.pdf$")]
    [InlineData("archive.tar.gz", "^archive\\.tar\\.filefluss-part-[0-9a-f]{8}\\.gz$")]
    [InlineData(".env", "^\\.env\\.filefluss-part-[0-9a-f]{8}$")]
    [InlineData("noext", "^noext\\.filefluss-part-[0-9a-f]{8}$")]
    public void TheTemporaryNameKeepsTheExtensionLast(string name, string pattern)
        => Assert.Matches(new Regex(pattern), TransferEngine.TemporaryReplacementName(name));

    [Fact]
    public void ALongNameLeavesRoomForTheTag()
    {
        var name = new string('n', 240) + ".txt";
        var temporary = TransferEngine.TemporaryReplacementName(name);
        Assert.True(temporary.Length <= 255, temporary.Length.ToString());
        Assert.EndsWith(".txt", temporary, StringComparison.Ordinal);
    }

    // MARK: - Replacing a cloud file

    [Fact]
    public async Task AFailedUploadLeavesTheFileItWouldHaveReplaced()
    {
        var local = LocalFile("src/a.txt", "new");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/a.txt", "old");
        cloud.OnUpload = path => throw CloudProviderException.QuotaExceeded();

        var progress = await Run(new TransferRequest(LocalFileProvider.Shared, [LocalFileProvider.ToItem(new FileInfo(local))], cloud, "/Dst", IsMove: true)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        });

        Assert.Equal(1, progress.FailureCount);
        Assert.Equal("old", cloud.Read("/Dst/a.txt"));
        Assert.DoesNotContain(cloud.AllPaths, IsTemporary);
        Assert.True(File.Exists(local));
    }

    [Fact]
    public async Task AReplacementIsUploadedBesideTheOldFileAndSwappedIn()
    {
        var local = LocalFile("src/a.txt", "new");
        var stamp = new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(local, stamp.UtcDateTime);
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/a.txt", "old");
        var uploadedTo = new List<string>();
        cloud.OnUpload = path =>
        {
            uploadedTo.Add(path);
            Assert.Equal("old", cloud.Read("/Dst/a.txt"));
        };

        var progress = await Run(new TransferRequest(LocalFileProvider.Shared, [LocalFileProvider.ToItem(new FileInfo(local))], cloud, "/Dst", IsMove: false)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        });

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(IsTemporary(Assert.Single(uploadedTo)));
        Assert.Equal("new", cloud.Read("/Dst/a.txt"));
        Assert.Equal(stamp, cloud.ModifiedAt("/Dst/a.txt"));
        Assert.DoesNotContain(cloud.AllPaths, IsTemporary);
    }

    [Fact]
    public async Task AMergeIntoACloudFolderKeepsTheOldFileWhenItsReplacementFails()
    {
        LocalFile("src/Folder/a.txt", "new a");
        LocalFile("src/Folder/b.txt", "new b");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/Folder/a.txt", "old a");
        cloud.OnUpload = path =>
        {
            if (path.Contains("/a.", StringComparison.Ordinal))
            {
                throw CloudProviderException.Network(new IOException("reset"));
            }
        };

        var folder = LocalFileProvider.ToItem(new DirectoryInfo(Path.Combine(_root, "src", "Folder")));
        var progress = await Run(new TransferRequest(LocalFileProvider.Shared, [folder], cloud, "/Dst", IsMove: true)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        });

        Assert.Equal(1, progress.FailureCount);
        Assert.Equal("old a", cloud.Read("/Dst/Folder/a.txt"));
        Assert.Equal("new b", cloud.Read("/Dst/Folder/b.txt"));
        Assert.DoesNotContain(cloud.AllPaths, IsTemporary);

        // A move only deletes what arrived.
        Assert.True(File.Exists(Path.Combine(_root, "src", "Folder", "a.txt")));
    }

    [Fact]
    public async Task AFolderReplacingACloudFileIsBuiltBesideItFirst()
    {
        LocalFile("src/Thing/a.txt", "a");
        LocalFile("src/Thing/b.txt", "b");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/Thing", "a file in the way");
        cloud.OnUpload = path =>
        {
            if (path.EndsWith("/b.txt", StringComparison.Ordinal))
            {
                throw CloudProviderException.ServerError(500);
            }
        };
        var folder = LocalFileProvider.ToItem(new DirectoryInfo(Path.Combine(_root, "src", "Thing")));
        var request = new TransferRequest(LocalFileProvider.Shared, [folder], cloud, "/Dst", IsMove: false)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        };

        var failed = await Run(request);
        Assert.Equal(1, failed.FailureCount);
        Assert.Equal("a file in the way", cloud.Read("/Dst/Thing"));
        Assert.DoesNotContain(cloud.AllPaths, IsTemporary);

        cloud.OnUpload = null;
        var worked = await Run(request);
        Assert.True(worked.CompletedCleanly, worked.CompletionSummary);
        Assert.Equal("b", cloud.Read("/Dst/Thing/b.txt"));
        Assert.DoesNotContain(cloud.AllPaths, IsTemporary);
    }

    [Fact]
    public async Task ACloudToCloudMoveVerifiesTheSwappedInFileBeforeDeletingTheSource()
    {
        var source = new MemoryCloudProvider();
        source.AddFile("/Src/a.txt", "new");
        var destination = new MemoryCloudProvider();
        destination.AddFile("/Dst/a.txt", "old");

        var items = await source.ListDirectoryAsync("/Src");
        var progress = await Run(new TransferRequest(source, items, destination, "/Dst", IsMove: true)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        });

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("new", destination.Read("/Dst/a.txt"));
        Assert.False(source.Exists("/Src/a.txt"));
    }

    // MARK: - Server-side replace

    [Fact]
    public async Task AServerSideMoveThatFailsPutsTheReplacedFileBack()
    {
        var target = LocalFile("dst/x.txt", "old");
        var moving = LocalFile("src/x.txt", "new");
        var item = LocalFileProvider.ToItem(new FileInfo(moving));

        TransferProgress progress;
        using (new FileStream(moving, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // A rename of a file that's open elsewhere fails, after the old one was set aside.
            progress = await Run(new TransferRequest(LocalFileProvider.Shared, [item], LocalFileProvider.Shared, Path.GetDirectoryName(target)!, IsMove: true)
            {
                ResolveConflict = Always(ConflictChoice.Replace),
            });
        }

        Assert.Equal(1, progress.FailureCount);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Equal(["x.txt"], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(target)!).Select(Path.GetFileName));
        Assert.True(File.Exists(moving));
    }

    [Fact]
    public async Task AServerSideReplaceSwapsWithoutAGap()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/A/x.txt", "new");
        cloud.AddFile("/B/x.txt", "old");
        var items = await cloud.ListDirectoryAsync("/A");

        var progress = await Run(new TransferRequest(cloud, items, cloud, "/B", IsMove: true)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        });

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal(1, cloud.ServerSideCalls);
        Assert.Equal("new", cloud.Read("/B/x.txt"));
        Assert.False(cloud.Exists("/A/x.txt"));
        Assert.DoesNotContain(cloud.AllPaths, IsTemporary);
    }

    [Fact]
    public async Task AServerSideFallbackStillReplacesSafely()
    {
        var cloud = new MemoryCloudProvider { SupportsServerSide = false };
        cloud.AddFile("/A/x.txt", "new");
        cloud.AddFile("/B/x.txt", "old");
        var items = await cloud.ListDirectoryAsync("/A");

        var progress = await Run(new TransferRequest(cloud, items, cloud, "/B", IsMove: false)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        });

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("new", cloud.Read("/B/x.txt"));
        Assert.Equal("new", cloud.Read("/A/x.txt"));
        Assert.DoesNotContain(cloud.AllPaths, IsTemporary);
    }

    // MARK: - Other transfer rules

    [Fact]
    public async Task AFileCantReplaceTheFolderItIsIn()
    {
        // Moving /A/foo/foo to /A: "Replace" would delete /A/foo, and the file with it.
        var inner = LocalFile("A/foo/foo", "precious");
        var item = LocalFileProvider.ToItem(new FileInfo(inner));

        var progress = await Run(new TransferRequest(LocalFileProvider.Shared, [item], LocalFileProvider.Shared, Path.Combine(_root, "A"), IsMove: true)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        });

        Assert.Equal(1, progress.FailureCount);
        Assert.Equal("precious", File.ReadAllText(inner));
    }

    [Fact]
    public async Task KeepBothOnADownloadKeepsTheExistingFile()
    {
        var existing = LocalFile("dst/a.txt", "mine");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Src/a.txt", "theirs");
        var items = await cloud.ListDirectoryAsync("/Src");

        var progress = await Run(new TransferRequest(cloud, items, LocalFileProvider.Shared, Path.GetDirectoryName(existing)!, IsMove: false)
        {
            ResolveConflict = Always(ConflictChoice.KeepBoth),
        });

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("mine", File.ReadAllText(existing));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(_root, "dst", "a (2).txt")));
    }

    [Fact]
    public async Task DownloadsCarryTheMarkOfTheWeb()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cloud = new MemoryCloudProvider();
        var stamp = new DateTimeOffset(2022, 1, 2, 3, 4, 5, TimeSpan.Zero);
        cloud.AddFile("/Src/invoice.pdf.exe", "MZ", stamp);
        cloud.AddFile("/Src/Folder/inner.docm", "PK", stamp);
        var destination = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(destination);
        var items = await cloud.ListDirectoryAsync("/Src");

        var progress = await Run(new TransferRequest(cloud, items, LocalFileProvider.Shared, destination, IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        var file = Path.Combine(destination, "invoice.pdf.exe");
        Assert.Equal(3, MarkOfTheWeb.Zone(file));
        Assert.Equal(3, MarkOfTheWeb.Zone(Path.Combine(destination, "Folder", "inner.docm")));
        Assert.Equal(stamp.UtcDateTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task LocalCopiesAreNotMarked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = LocalFile("src/setup.exe", "MZ");
        var destination = Path.Combine(_root, "copies");
        Directory.CreateDirectory(destination);

        await Run(new TransferRequest(LocalFileProvider.Shared, [LocalFileProvider.ToItem(new FileInfo(source))], LocalFileProvider.Shared, destination, IsMove: false));

        Assert.Null(MarkOfTheWeb.Zone(Path.Combine(destination, "setup.exe")));
    }
}
