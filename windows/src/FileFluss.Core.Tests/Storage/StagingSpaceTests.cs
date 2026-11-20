// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Storage;
using FileFluss.Core.Sync;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests.Storage;

/// <summary>
/// macOS 1.6 "Say how much room a transfer needs on this Mac on the way through": a copy
/// between two accounts passes every file through the staging folder, which may be on a nearly
/// full disk the destination's quota says nothing about.
/// </summary>
public sealed class StagingSpaceTests
{
    private static async Task<TransferProgress> Run(TransferRequest request)
    {
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);
        return progress;
    }

    [Fact]
    public void TheNeedIsShortOnlyWhenTheFreeSpaceIsKnownAndTooSmall()
    {
        Assert.True(new StagingNeed("Local Disk (C:)", 10, 9).WouldExceed);
        Assert.False(new StagingNeed("Local Disk (C:)", 10, 10).WouldExceed);
        Assert.False(new StagingNeed("Local Disk (C:)", 10, null).WouldExceed);
        Assert.Null(StagingSpace.For(0));
    }

    [Fact]
    public async Task ACopyBetweenAccountsThatCantPassThroughStopsBeforeItStarts()
    {
        StagingSpace.FreeBytesOverride.Value = _ => 1_000;
        var source = new MemoryCloudProvider();
        source.AddFile("/Src/small.txt", new string('s', 10));
        source.AddFile("/Src/Folder/big.bin", new string('b', 5_000));
        var destination = new MemoryCloudProvider();
        destination.AddFolder("/Dst");
        var items = await source.ListDirectoryAsync("/Src");

        var progress = await Run(new TransferRequest(source, items, destination, "/Dst", IsMove: true));

        Assert.NotNull(progress.ErrorMessage);        Assert.Equal(["/Dst"], destination.AllPaths.Where(p => p != "/"));
        Assert.True(source.Exists("/Src/small.txt"));
    }

    [Fact]
    public async Task OnlyTheLargestFileHasToFitBecauseFilesPassOneAtATime()
    {
        StagingSpace.FreeBytesOverride.Value = _ => 3_000;
        var source = new MemoryCloudProvider();
        source.AddFile("/Src/a.bin", new string('a', 2_000));
        source.AddFile("/Src/b.bin", new string('b', 2_000));
        var destination = new MemoryCloudProvider();
        destination.AddFolder("/Dst");
        var items = await source.ListDirectoryAsync("/Src");

        var progress = await Run(new TransferRequest(source, items, destination, "/Dst", IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(destination.Exists("/Dst/b.bin"));
    }

    [Fact]
    public async Task ADownloadDoesntPassThroughTheStagingFolder()
    {
        StagingSpace.FreeBytesOverride.Value = _ => 0;
        var source = new MemoryCloudProvider();
        source.AddFile("/Src/a.bin", new string('a', 2_000));
        var target = Path.Combine(Path.GetTempPath(), "ff-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            var items = await source.ListDirectoryAsync("/Src");
            var progress = await Run(new TransferRequest(source, items, LocalFileProvider.Shared, target, IsMove: false));
            Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task TheSyncWindowShowsWhatASyncBetweenAccountsNeedsHere()
    {
        StagingSpace.FreeBytesOverride.Value = _ => 100;
        var left = new MemoryCloudProvider();
        left.AddFile("/L/a.bin", new string('a', 50));
        left.AddFile("/L/b.bin", new string('b', 400));
        var right = new MemoryCloudProvider();
        right.AddFolder("/R");
        var session = new SyncSession(new SyncEndpoint(left, "/L"), new SyncEndpoint(right, "/R"));

        await session.CalculateAsync();

        var staging = Assert.IsType<StagingNeed>(session.Staging);
        Assert.Equal(400, staging.Needed);
        Assert.True(staging.WouldExceed);
        Assert.NotNull(session.StagingNeededText);
        Assert.NotNull(session.StagingFreeText);
        Assert.NotNull(session.StagingWarning);
    }

    [Fact]
    public async Task ASyncFromThisPcNeedsNoRoomOnTheWay()
    {
        StagingSpace.FreeBytesOverride.Value = _ => 0;
        var root = Path.Combine(Path.GetTempPath(), "ff-staging-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "aaa");
            var cloud = new MemoryCloudProvider();
            cloud.AddFolder("/R");
            var session = new SyncSession(new SyncEndpoint(LocalFileProvider.Shared, root), new SyncEndpoint(cloud, "/R"));

            await session.CalculateAsync();

            Assert.NotNull(session.Plan);
            Assert.Null(session.Staging);
            Assert.Null(session.StagingWarning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
