// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Sync;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests.Sync;

public sealed class SyncExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-sync-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileProvider _local = LocalFileProvider.Shared;

    public SyncExecutorTests()
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

    private static void WriteFile(string path, string content, DateTime? modifiedUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (modifiedUtc is { } stamp)
        {
            File.SetLastWriteTimeUtc(path, stamp);
        }
    }

    private static async Task<SyncPlan> PlanAsync(SyncEndpoint source, SyncEndpoint destination, SyncMode mode)
        => SyncPlanner.Plan(
            await SyncPlanner.EnumerateAsync(source),
            await SyncPlanner.EnumerateAsync(destination),
            mode,
            PlanDirection.LeftToRight,
            source.IsCloud,
            destination.IsCloud,
            ignoreCase: !source.IsCloud || !destination.IsCloud);

    private static async Task<TransferProgress> RunAsync(SyncPlan plan, SyncEndpoint source, SyncEndpoint destination)
    {
        var progress = SyncExecutor.CreateProgress(plan, source, destination, PanelSide.Right);
        await SyncExecutor.ExecuteAsync(plan, source, destination, progress);
        return progress;
    }

    // MARK: - Enumeration

    [Fact]
    public async Task LocalEnumerationIsRelativeSlashSeparatedAndSkipsHiddenItems()
    {
        var source = LocalDir("enum");
        WriteFile(Path.Combine(source, "a.txt"), "aaa");
        WriteFile(Path.Combine(source, "sub", "b.txt"), "bb");
        var hidden = Path.Combine(source, "secret.txt");
        WriteFile(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var entries = await SyncPlanner.EnumerateAsync(new SyncEndpoint(_local, source));

        Assert.Equal(["a.txt", "sub", "sub/b.txt"], entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal).ToArray());
        var b = entries.Single(e => e.RelativePath == "sub/b.txt");
        Assert.Equal(2, b.Size);
        Assert.NotNull(b.CreationDate);
        Assert.Equal(0, entries.Single(e => e.RelativePath == "sub").Size);
    }

    [Fact]
    public async Task CloudEnumerationIsRelativeToTheEndpoint()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Base/x/y.txt", "yy");
        cloud.AddFile("/Other/z.txt", "z");

        var entries = await SyncPlanner.EnumerateAsync(new SyncEndpoint(cloud, "/Base"));

        Assert.Equal(["x", "x/y.txt"], entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal).ToArray());
        Assert.Null(entries[0].CreationDate);
    }

    [Fact]
    public async Task ThisPcIsNotAFolderToSync()
    {
        var endpoint = new SyncEndpoint(_local, string.Empty);
        Assert.False(endpoint.IsUsable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SyncPlanner.EnumerateAsync(endpoint));
    }

    // MARK: - Execution

    [Fact]
    public async Task MirrorCloudToLocalWithNamesWindowsCantHoldIsStable()
    {
        // "a:b" lands as "a_b", in a folder "Q?" that lands as "Q_". Matching the raw names
        // made every run download the files again and then delete the copies as "extra".
        var cloud = new MemoryCloudProvider();
        var stamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        cloud.AddFile("/Src/a:b.txt", "colon", stamp);
        cloud.AddFile("/Src/Q?/inner.txt", "inner", stamp);
        var target = LocalDir("mirror-illegal");
        var source = new SyncEndpoint(cloud, "/Src");
        var destination = new SyncEndpoint(_local, target);

        var first = await RunAsync(await PlanAsync(source, destination, SyncMode.Mirror), source, destination);
        Assert.True(first.CompletedCleanly, first.CompletionSummary);
        Assert.Equal("colon", File.ReadAllText(Path.Combine(target, "a_b.txt")));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(target, "Q_", "inner.txt")));

        var second = await PlanAsync(source, destination, SyncMode.Mirror);
        Assert.True(second.IsEmpty, string.Join(", ", second.Operations.Select(o => $"{o.Kind} {o.RelativePath}")));
    }

    [Fact]
    public async Task MirrorMatchesMacAndWindowsSpellingsOfTheSameName()
    {
        var nfd = "Café";
        var nfc = "Café";
        var cloud = new MemoryCloudProvider();
        var stamp = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        cloud.AddFile($"/Src/{nfd}/menu.txt", "new menu", new DateTimeOffset(stamp));
        var target = LocalDir("mirror-nfc");
        WriteFile(Path.Combine(target, nfc, "menu.txt"), "old", stamp.AddDays(-1));
        var source = new SyncEndpoint(cloud, "/Src");
        var destination = new SyncEndpoint(_local, target);

        var plan = await PlanAsync(source, destination, SyncMode.Mirror);
        Assert.DoesNotContain(plan.Operations, o => o.Kind == SyncOperationKind.Delete);

        var progress = await RunAsync(plan, source, destination);

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        var folder = Assert.Single(Directory.GetDirectories(target));
        Assert.Equal(nfc, Path.GetFileName(folder));
        Assert.Equal("new menu", File.ReadAllText(Path.Combine(folder, "menu.txt")));
    }

    [Fact]
    public async Task MirrorLocalToCloudMakesTheDestinationMatchAndIsIdempotent()
    {
        var sourceDir = LocalDir("mirror-src");
        var stamp = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        WriteFile(Path.Combine(sourceDir, "a.txt"), "new content", stamp);
        WriteFile(Path.Combine(sourceDir, "sub", "b.txt"), "bbb", stamp);
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/a.txt", "old", DateTimeOffset.FromUnixTimeSeconds(1_000));
        cloud.AddFile("/Dst/extra.txt", "x");
        cloud.AddFile("/Dst/oldsub/c.txt", "c");

        var source = new SyncEndpoint(_local, sourceDir);
        var destination = new SyncEndpoint(cloud, "/Dst");
        var plan = await PlanAsync(source, destination, SyncMode.Mirror);
        var progress = await RunAsync(plan, source, destination);

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal(TransferOperation.Mirroring, progress.Operation);
        Assert.Equal("new content", cloud.Read("/Dst/a.txt"));
        Assert.Equal("bbb", cloud.Read("/Dst/sub/b.txt"));
        Assert.False(cloud.Exists("/Dst/extra.txt"));
        Assert.False(cloud.Exists("/Dst/oldsub"));
        Assert.Equal(plan.Operations.Count, progress.Results.Count);
        Assert.Equal(plan.UploadBytes, progress.UploadBytes);
        Assert.Equal(1.0, progress.Fraction);
        Assert.Equal(2, progress.CompletedFiles);

        // Dates were carried over, so the next mirror finds nothing to do.
        Assert.Equal(new DateTimeOffset(stamp), cloud.ModifiedAt("/Dst/sub/b.txt").ToUniversalTime());
        Assert.True((await PlanAsync(source, destination, SyncMode.Mirror)).IsEmpty);
    }

    [Fact]
    public async Task NewerCloudToLocalReplacesOlderFilesAndKeepsExtras()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Src/a.txt", "fresh", DateTimeOffset.UtcNow);
        cloud.AddFile("/Src/new/n.txt", "n", DateTimeOffset.UtcNow);
        var target = LocalDir("newer-dst");
        WriteFile(Path.Combine(target, "a.txt"), "stale", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteFile(Path.Combine(target, "mine.txt"), "keep");

        var source = new SyncEndpoint(cloud, "/Src");
        var destination = new SyncEndpoint(_local, target);
        var plan = await PlanAsync(source, destination, SyncMode.Newer);
        Assert.Equal((1, 1, 1), (plan.FilesToAdd, plan.FilesToReplace, plan.FoldersToAdd));

        var progress = await RunAsync(plan, source, destination);

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("fresh", await File.ReadAllTextAsync(Path.Combine(target, "a.txt")));
        Assert.Equal("n", await File.ReadAllTextAsync(Path.Combine(target, "new", "n.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "mine.txt")));
        Assert.Equal(plan.DownloadBytes, progress.DownloadBytes);
    }

    [Fact]
    public async Task AdditiveKeepsBothCopies()
    {
        var sourceDir = LocalDir("add-src");
        var targetDir = LocalDir("add-dst");
        WriteFile(Path.Combine(sourceDir, "a.txt"), "S");
        WriteFile(Path.Combine(targetDir, "a.txt"), "D");

        var source = new SyncEndpoint(_local, sourceDir);
        var destination = new SyncEndpoint(_local, targetDir);
        var progress = await RunAsync(await PlanAsync(source, destination, SyncMode.Additive), source, destination);

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("D", await File.ReadAllTextAsync(Path.Combine(targetDir, "a.txt")));
        Assert.Equal("S", await File.ReadAllTextAsync(Path.Combine(targetDir, "a (2).txt")));
        Assert.Equal("a (2).txt", Assert.Single(progress.Results).Name);
    }

    [Fact]
    public async Task CloudToCloudAcrossAccountsMovesEveryByteTwice()
    {
        var from = new MemoryCloudProvider();
        from.AddFile("/A/one.bin", new string('1', 300));
        from.AddFile("/A/deep/two.bin", new string('2', 200));
        var to = new MemoryCloudProvider();
        to.AddFolder("/B");

        var source = new SyncEndpoint(from, "/A");
        var destination = new SyncEndpoint(to, "/B");
        var plan = await PlanAsync(source, destination, SyncMode.Mirror);
        var progress = await RunAsync(plan, source, destination);

        Assert.True(progress.IsCloudToCloud);
        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal(1000, progress.ExpectedBytes);
        Assert.Equal(progress.ExpectedBytes, progress.MovedBytes);
        Assert.Equal(new string('2', 200), to.Read("/B/deep/two.bin"));
    }

    [Fact]
    public async Task SameAccountCopiesServerSideAndStillFillsTheBar()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/A/x.bin", new string('x', 100));
        cloud.AddFile("/A/y.bin", new string('y', 50));
        cloud.AddFolder("/B");

        var source = new SyncEndpoint(cloud, "/A");
        var destination = new SyncEndpoint(cloud, "/B");
        var plan = await PlanAsync(source, destination, SyncMode.Newer);
        var progress = await RunAsync(plan, source, destination);

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal(2, cloud.ServerSideCalls);
        Assert.Equal(progress.ExpectedBytes, progress.MovedBytes);
        Assert.Equal(progress.TotalFiles, progress.CompletedFiles);
        Assert.Equal(new string('y', 50), cloud.Read("/B/y.bin"));
    }

    [Fact]
    public async Task MirrorDeletesAnExtraFolderInOneCall()
    {
        var memory = new MemoryCloudProvider();
        memory.AddFile("/Dst/old/x", "1");
        memory.AddFile("/Dst/old/y", "2");
        memory.AddFile("/Dst/old/deeper/z", "3");
        var destinationProvider = new WrappingProvider(memory);
        var sourceDir = LocalDir("mirror-empty");

        var source = new SyncEndpoint(_local, sourceDir);
        var destination = new SyncEndpoint(destinationProvider, "/Dst");
        var plan = await PlanAsync(source, destination, SyncMode.Mirror);
        Assert.Equal((3, 2), (plan.FilesToDelete, plan.FoldersToDelete));

        var progress = await RunAsync(plan, source, destination);

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal(1, destinationProvider.DeleteCalls);
        Assert.False(memory.Exists("/Dst/old"));
        Assert.Equal(5, progress.SuccessCount);
        Assert.Equal(1.0, progress.Fraction);
    }

    [Fact]
    public async Task AFailedFileIsRecordedAndTheRestStillArrive()
    {
        var sourceDir = LocalDir("fail-src");
        WriteFile(Path.Combine(sourceDir, "bad.txt"), "bad");
        WriteFile(Path.Combine(sourceDir, "good.txt"), "good");
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Dst");
        cloud.FailingUploads.Add("bad.txt");

        var source = new SyncEndpoint(_local, sourceDir);
        var destination = new SyncEndpoint(cloud, "/Dst");
        var progress = await RunAsync(await PlanAsync(source, destination, SyncMode.Newer), source, destination);

        Assert.Equal(1, progress.SuccessCount);
        Assert.Equal(1, progress.FailureCount);
        Assert.Equal("bad.txt", progress.Results.Single(r => r.Status == TransferItemStatus.Failed).Name);
        Assert.Equal("good", cloud.Read("/Dst/good.txt"));
        Assert.False(cloud.Exists("/Dst/bad.txt"));
    }

    [Fact]
    public async Task CancelledBeforeItStartsChangesNothing()
    {
        var sourceDir = LocalDir("cancel-src");
        WriteFile(Path.Combine(sourceDir, "a.txt"), "a");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/extra.txt", "x");

        var source = new SyncEndpoint(_local, sourceDir);
        var destination = new SyncEndpoint(cloud, "/Dst");
        var plan = await PlanAsync(source, destination, SyncMode.Mirror);
        var progress = SyncExecutor.CreateProgress(plan, source, destination, PanelSide.Left);
        progress.Cancel();
        await SyncExecutor.ExecuteAsync(plan, source, destination, progress);

        Assert.True(progress.IsCancelled);
        Assert.True(progress.IsComplete);
        Assert.False(cloud.Exists("/Dst/a.txt"));
        Assert.True(cloud.Exists("/Dst/extra.txt"));
        Assert.Empty(progress.Results);
    }

    [Fact]
    public async Task AnOfflineDestinationIsRefused()
    {
        var cloud = new MemoryCloudProvider();
        var source = new SyncEndpoint(_local, LocalDir("offline-src"));
        var destination = new SyncEndpoint(cloud, "/") { IsOffline = true, DisplayName = "Box" };
        var plan = new SyncPlan(SyncMode.Newer, PlanDirection.LeftToRight, [new SyncOperation(SyncOperationKind.Add, "d", true, 0)], 0, 0, 0, 1, 0, 0, 0, 0, 0);

        var progress = await RunAsync(plan, source, destination);

        Assert.True(progress.HasErrors);
        Assert.Equal(["/"], cloud.AllPaths);
    }

    [Fact]
    public void EndpointsKnowWhenTheyOverlap()
    {
        var cloud = new MemoryCloudProvider();
        Assert.True(new SyncEndpoint(cloud, "/A").Overlaps(new SyncEndpoint(cloud, "/A/B")));
        Assert.True(new SyncEndpoint(cloud, "/A/B").Overlaps(new SyncEndpoint(cloud, "/A")));
        Assert.False(new SyncEndpoint(cloud, "/A").Overlaps(new SyncEndpoint(cloud, "/AB")));
        Assert.False(new SyncEndpoint(cloud, "/A").Overlaps(new SyncEndpoint(new MemoryCloudProvider(), "/A")));
        Assert.True(new SyncEndpoint(_local, @"C:\").Overlaps(new SyncEndpoint(_local, @"c:\Users")));
        Assert.False(new SyncEndpoint(_local, @"C:\Data").Overlaps(new SyncEndpoint(_local, @"C:\Data2")));
        Assert.Equal(Path.Combine(_root, "x", "y.txt"), new SyncEndpoint(_local, _root).Resolve("x/y.txt"));
        Assert.Equal("/A/x/y.txt", new SyncEndpoint(cloud, "/A").Resolve("x/y.txt"));
    }
}
