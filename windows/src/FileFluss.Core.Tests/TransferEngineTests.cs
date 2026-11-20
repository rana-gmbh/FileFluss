// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests;

public sealed class TransferEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileProvider _local = LocalFileProvider.Shared;

    public TransferEngineTests()
    {
        Directory.CreateDirectory(_root);
        LocalFileProvider.RecycleHandler = null;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string LocalDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<TransferProgress> Run(TransferRequest request)
    {
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);
        return progress;
    }

    private static ConflictResolver Always(ConflictChoice choice, bool applyToAll = false)
        => (_, _) => Task.FromResult(new ConflictResolution(choice, applyToAll));

    [Fact]
    public async Task LocalToLocalCopiesTreesAndKeepsDates()
    {
        var source = LocalDir("src");
        var target = LocalDir("dst");
        Directory.CreateDirectory(Path.Combine(source, "folder", "inner"));
        await File.WriteAllTextAsync(Path.Combine(source, "folder", "inner", "a.txt"), "aaa");
        var stamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(source, "folder", "inner", "a.txt"), stamp);

        var item = await _local.GetFileMetadataAsync(Path.Combine(source, "folder"));
        var progress = await Run(new TransferRequest(_local, [item], _local, target, IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        var copied = Path.Combine(target, "folder", "inner", "a.txt");
        Assert.Equal("aaa", await File.ReadAllTextAsync(copied));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(copied));
        Assert.Equal(1, progress.TotalFiles);
        Assert.Equal(1.0, progress.Fraction);
        Assert.True(Directory.Exists(Path.Combine(source, "folder")));
    }

    [Fact]
    public async Task UploadAndDownloadCountBytes()
    {
        var source = LocalDir("up");
        await File.WriteAllTextAsync(Path.Combine(source, "x.bin"), new string('x', 1000));
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Docs");

        var item = await _local.GetFileMetadataAsync(Path.Combine(source, "x.bin"));
        var up = await Run(new TransferRequest(_local, [item], cloud, "/Docs", IsMove: false));
        Assert.True(up.CompletedCleanly, up.CompletionSummary);
        Assert.Equal(TransferOperation.Uploading, up.Operation);
        Assert.Equal(1000, up.UploadBytes);
        Assert.Equal(1000, cloud.Read("/Docs/x.bin").Length);

        var target = LocalDir("down");
        var remote = await cloud.GetFileMetadataAsync("/Docs/x.bin");
        var down = await Run(new TransferRequest(cloud, [remote], _local, target, IsMove: false));
        Assert.True(down.CompletedCleanly, down.CompletionSummary);
        Assert.Equal(TransferOperation.Downloading, down.Operation);
        Assert.True(File.Exists(Path.Combine(target, "x.bin")));
    }

    [Fact]
    public async Task CloudToCloudStagesAndCountsBothPhases()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        a.AddFile("/Folder/one.txt", "12345");
        a.AddFile("/Folder/Sub/two.txt", "123");
        var folder = await a.GetFileMetadataAsync("/Folder");

        var progress = await Run(new TransferRequest(a, [folder], b, "/", IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(progress.IsCloudToCloud);
        Assert.Equal("12345", b.Read("/Folder/one.txt"));
        Assert.Equal("123", b.Read("/Folder/Sub/two.txt"));
        Assert.Equal(16, progress.ExpectedBytes);
        Assert.Equal(16, progress.MovedBytes);
        Assert.False(a.Exists("/Folder"));
    }

    [Fact]
    public async Task SameAccountMovesServerSide()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/a/file.txt", "x");
        cloud.AddFolder("/b");
        var file = await cloud.GetFileMetadataAsync("/a/file.txt");

        var progress = await Run(new TransferRequest(cloud, [file], cloud, "/b", IsMove: true));

        Assert.True(progress.CompletedCleanly);
        Assert.Equal(1, cloud.ServerSideCalls);
        Assert.True(cloud.Exists("/b/file.txt"));
        Assert.False(cloud.Exists("/a/file.txt"));
    }

    [Fact]
    public async Task SameAccountFallsBackWhenServerCannot()
    {
        var cloud = new MemoryCloudProvider { SupportsServerSide = false };
        cloud.AddFile("/a/file.txt", "payload");
        cloud.AddFolder("/b");
        var file = await cloud.GetFileMetadataAsync("/a/file.txt");

        var progress = await Run(new TransferRequest(cloud, [file], cloud, "/b", IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("payload", cloud.Read("/b/file.txt"));
        Assert.False(cloud.Exists("/a/file.txt"));
    }

    [Fact]
    public async Task KeepBothUsesExplorerNumbering()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        a.AddFile("/report.pdf", "new");
        b.AddFile("/report.pdf", "old");
        b.AddFile("/report (2).pdf", "older");
        var item = await a.GetFileMetadataAsync("/report.pdf");

        var progress = await Run(new TransferRequest(a, [item], b, "/", IsMove: false) { ResolveConflict = Always(ConflictChoice.KeepBoth) });

        Assert.True(progress.CompletedCleanly);
        Assert.Equal("old", b.Read("/report.pdf"));
        Assert.Equal("new", b.Read("/report (3).pdf"));
    }

    [Fact]
    public async Task ReplaceOverwritesAndSkipLeavesAlone()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        a.AddFile("/x.txt", "new");
        a.AddFile("/y.txt", "new");
        b.AddFile("/x.txt", "old");
        b.AddFile("/y.txt", "old");
        var items = new[] { await a.GetFileMetadataAsync("/x.txt"), await a.GetFileMetadataAsync("/y.txt") };
        var answers = new Queue<ConflictChoice>([ConflictChoice.Replace, ConflictChoice.Skip]);

        var progress = await Run(new TransferRequest(a, items, b, "/", IsMove: true)
        {
            ResolveConflict = (_, _) => Task.FromResult(new ConflictResolution(answers.Dequeue(), false)),
        });

        Assert.Equal("new", b.Read("/x.txt"));
        Assert.Equal("old", b.Read("/y.txt"));
        Assert.False(a.Exists("/x.txt"));
        Assert.True(a.Exists("/y.txt"), "a skipped item must never be deleted by a move");
        Assert.Equal(1, progress.SkippedCount);
        Assert.False(progress.CompletedCleanly);
    }

    [Fact]
    public async Task StopSkipsEverythingLeft()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        foreach (var name in new[] { "1.txt", "2.txt", "3.txt" })
        {
            a.AddFile("/" + name, "a");
            b.AddFile("/" + name, "b");
        }

        var items = await a.ListDirectoryAsync("/");
        var progress = await Run(new TransferRequest(a, items, b, "/", IsMove: false) { ResolveConflict = Always(ConflictChoice.Stop) });

        Assert.Equal(3, progress.SkippedCount);
        Assert.All(new[] { "/1.txt", "/2.txt", "/3.txt" }, p => Assert.Equal("b", b.Read(p)));
    }

    [Fact]
    public async Task FailedUploadKeepsTheSourceOfAMove()
    {
        var source = LocalDir("mv");
        await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "important");
        await File.WriteAllTextAsync(Path.Combine(source, "go.txt"), "fine");
        var cloud = new MemoryCloudProvider();
        cloud.FailingUploads.Add("keep.txt");
        var items = await _local.ListDirectoryAsync(source);

        var progress = await Run(new TransferRequest(_local, items, cloud, "/", IsMove: true));

        Assert.Equal(1, progress.FailureCount);
        Assert.True(File.Exists(Path.Combine(source, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(source, "go.txt")));
        Assert.Contains("1 of 2", progress.CompletionSummary.Replace("Moved", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FolderCannotGoIntoItself()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/a/b");
        var folder = await cloud.GetFileMetadataAsync("/a");

        var progress = await Run(new TransferRequest(cloud, [folder], cloud, "/a/b", IsMove: true));

        Assert.Equal(1, progress.FailureCount);
        Assert.True(cloud.Exists("/a/b"));
    }

    [Fact]
    public async Task CopyIntoSameFolderMakesANumberedCopy()
    {
        var folder = LocalDir("dup");
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "x");
        var item = await _local.GetFileMetadataAsync(Path.Combine(folder, "a.txt"));

        var progress = await Run(new TransferRequest(_local, [item], _local, folder, IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(File.Exists(Path.Combine(folder, "a (2).txt")));
    }

    [Fact]
    public async Task CancelledTransferStaysWhereItStopped()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        a.AddFile("/f.txt", "x");
        var item = await a.GetFileMetadataAsync("/f.txt");
        var request = new TransferRequest(a, [item], b, "/", IsMove: true);
        var progress = TransferEngine.CreateProgress(request, PanelSide.Left);
        progress.Cancel();

        await TransferEngine.RunAsync(request, progress);

        Assert.True(progress.IsComplete);
        Assert.True(progress.IsCancelled);
        Assert.True(a.Exists("/f.txt"));
        Assert.StartsWith("Cancelled", progress.CompletionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsafeRemoteNamesAreSanitisedOnTheWayDown()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/what?.txt", "x");
        var item = await cloud.GetFileMetadataAsync("/what?.txt");
        var target = LocalDir("safe");

        var progress = await Run(new TransferRequest(cloud, [item], _local, target, IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(File.Exists(Path.Combine(target, "what_.txt")));
    }

    [Fact]
    public void UniqueNameCounts()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt", "A (2).txt" };
        Assert.Equal("a (3).txt", TransferEngine.UniqueName("a.txt", taken));
        Assert.Equal("folder", TransferEngine.UniqueName("folder", taken));
    }
}
