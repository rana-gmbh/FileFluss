// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Sync;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Sync;

/// <summary>
/// The sync half of macOS 1.6 "Sync and large copies you can rely on" (DataSafetyTests,
/// PanelAndSyncTests): hidden files left alone, scans that stop rather than guess, Mirror that
/// won't empty a destination, Update newer that never replaces a folder, minute-precision
/// servers, and a read-only destination refused by the executor itself.
/// </summary>
public sealed class SyncDataSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-syncsafety-" + Guid.NewGuid().ToString("N"));

    public SyncDataSafetyTests()
    {
        Directory.CreateDirectory(_root);
        LocalFileProvider.RecycleHandler = null;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static DateTimeOffset T(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static SyncEntry FileEntry(string path, long size = 10, long modified = 1_000) => new(path, false, size, T(modified));

    private static SyncEntry FolderEntry(string path) => new(path, true, 0, T(1_000));

    private string LocalDir(string name, params string[] files)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        foreach (var file in files)
        {
            var full = Path.Combine(path, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "local");
        }

        return path;
    }

    // MARK: - Hidden files

    [Fact]
    public void MirrorToTheCloudLeavesTheCloudsDotfilesAlone()
    {
        // The local scan never sees ".git" (Windows marks it hidden), so without the filter the
        // cloud's copy looked extra and Mirror deleted it.
        var plan = SyncPlanner.Plan(
            [FileEntry("a.txt")],
            [FileEntry("a.txt"), FolderEntry(".git"), FileEntry(".git/config"), FileEntry("sub/.env")],
            SyncMode.Mirror,
            PlanDirection.LeftToRight,
            sourceIsCloud: false,
            destinationIsCloud: true,
            ignoreCase: true);

        Assert.True(plan.IsEmpty);
        Assert.Equal(1, plan.DestinationEntryCount);
    }

    [Fact]
    public void LocalDotfilesAreNotUploaded()
    {
        var plan = SyncPlanner.Plan(
            [FileEntry(".env"), FolderEntry(".config"), FileEntry(".config/token"), FileEntry("a.txt")],
            [],
            SyncMode.Mirror,
            PlanDirection.LeftToRight,
            sourceIsCloud: false,
            destinationIsCloud: true,
            ignoreCase: true);

        Assert.Equal(["a.txt"], plan.Operations.Select(o => o.RelativePath));
    }

    [Fact]
    public void ItemsWindowsMarksHiddenAreLeftAloneOnTheOtherSideToo()
    {
        // "desktop.ini" and "Thumbs" are hidden/system locally and skipped; the cloud's copies
        // must not be deleted (local → cloud) or written over the hidden local ones (cloud → local).
        string[] hidden = ["desktop.ini", "Thumbs"];
        var cloud = new[] { FileEntry("desktop.ini"), FolderEntry("Thumbs"), FileEntry("Thumbs/1.db"), FileEntry("b.txt") };

        var upload = SyncPlanner.Plan([], cloud, SyncMode.Mirror, PlanDirection.LeftToRight, false, true, ignoreCase: true, hiddenPaths: hidden);
        Assert.Equal(["b.txt"], upload.Operations.Select(o => o.RelativePath));
        Assert.All(upload.Operations, o => Assert.Equal(SyncOperationKind.Delete, o.Kind));

        var download = SyncPlanner.Plan(cloud, [], SyncMode.Mirror, PlanDirection.RightToLeft, true, false, ignoreCase: true, hiddenPaths: ["DESKTOP.INI", "thumbs"]);
        Assert.Equal(["b.txt"], download.Operations.Select(o => o.RelativePath));
    }

    [Fact]
    public void CloudToCloudSyncsDotfiles()
    {
        var plan = SyncPlanner.Plan([FileEntry(".env"), FileEntry("a.txt")], [], SyncMode.Mirror, PlanDirection.LeftToRight, true, true);
        Assert.Equal(2, plan.FilesToAdd);
    }

    [Fact]
    public async Task ARealMirrorKeepsAHiddenLocalFilesCloudCopy()
    {
        var source = LocalDir("hidden-src", "a.txt", "desktop.ini");
        File.SetAttributes(Path.Combine(source, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/desktop.ini", "cloud");
        cloud.AddFile("/Dst/.git/HEAD", "ref");
        cloud.AddFile("/Dst/stale.txt", "old");
        var session = new SyncSession(new SyncEndpoint(LocalFileProvider.Shared, source), new SyncEndpoint(cloud, "/Dst"))
        {
            Mode = SyncMode.Mirror,
            ConfirmDestructive = true,
        };

        await session.CalculateAsync();
        var progress = session.StartSync();
        Assert.NotNull(progress);
        await session.ExecutionTask!;

        Assert.True(cloud.Exists("/Dst/desktop.ini"));
        Assert.True(cloud.Exists("/Dst/.git/HEAD"));
        Assert.False(cloud.Exists("/Dst/stale.txt"));
        Assert.True(cloud.Exists("/Dst/a.txt"));
    }

    // MARK: - Scans that stop rather than guess

    [Fact]
    public async Task AnUnreadableFolderStopsTheScan()
    {
        var memory = new MemoryCloudProvider();
        memory.AddFile("/Src/keep/a.txt", "a");
        memory.AddFile("/Src/locked/b.txt", "b");
        var provider = new WrappingProvider(memory);
        provider.FailingListings.Add("/Src/locked");

        await Assert.ThrowsAsync<CloudProviderException>(() => SyncPlanner.EnumerateAsync(new SyncEndpoint(provider, "/Src")));
    }

    // MARK: - Mirror that won't empty a destination

    [Fact]
    public void MirroringAnEmptySourceOverSomethingIsRefused()
    {
        var plan = SyncPlanner.Plan([], [FileEntry("a.txt"), FileEntry("b.txt")], SyncMode.Mirror, PlanDirection.LeftToRight, false, true);
        Assert.True(plan.WouldEmptyDestination);

        var additive = SyncPlanner.Plan([], [FileEntry("a.txt")], SyncMode.Additive, PlanDirection.LeftToRight, false, true);
        Assert.False(additive.WouldEmptyDestination);
    }

    [Fact]
    public void DeletingMostOfALargeDestinationNeedsItsOwnConfirmation()
    {
        var destination = Enumerable.Range(0, 20).Select(i => FileEntry($"f{i}.txt")).ToArray();
        var source = destination.Take(9).ToArray();
        var plan = SyncPlanner.Plan(source, destination, SyncMode.Mirror, PlanDirection.LeftToRight, false, true);
        Assert.Equal(11, plan.FilesToDelete);
        Assert.True(plan.DeletesMostOfDestination);

        // Fewer than ten deletions never count, however small the destination.
        var small = SyncPlanner.Plan([], [FileEntry("x"), FileEntry("y")], SyncMode.Mirror, PlanDirection.LeftToRight, false, true);
        Assert.False(small.DeletesMostOfDestination);
    }

    [Fact]
    public async Task TheSessionWontStartAnEmptyingMirrorAndAsksBeforeALargeDeletion()
    {
        var empty = LocalDir("empty-src");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/a.txt", "a");
        var session = new SyncSession(new SyncEndpoint(LocalFileProvider.Shared, empty), new SyncEndpoint(cloud, "/Dst"))
        {
            Mode = SyncMode.Mirror,
            ConfirmDestructive = true,
        };
        await session.CalculateAsync();
        Assert.True(session.Plan!.WouldEmptyDestination);
        Assert.NotNull(session.EmptyDestinationWarning);
        Assert.False(session.CanStart);
        Assert.Null(session.StartSync());
        Assert.True(cloud.Exists("/Dst/a.txt"));

        var few = LocalDir("few-src", "keep.txt");
        var big = new MemoryCloudProvider();
        big.AddFile("/Dst/keep.txt", "local");
        for (var i = 0; i < 12; i++)
        {
            big.AddFile($"/Dst/old{i}.txt", "x");
        }

        var large = new SyncSession(new SyncEndpoint(LocalFileProvider.Shared, few), new SyncEndpoint(big, "/Dst"))
        {
            Mode = SyncMode.Mirror,
            ConfirmDestructive = true,
        };
        await large.CalculateAsync();
        Assert.True(large.Plan!.DeletesMostOfDestination);
        Assert.NotNull(large.LargeDeletionConfirmationText);
        Assert.False(large.CanStart);
        large.ConfirmLargeDeletion = true;
        Assert.True(large.CanStart);

        // A new plan needs a new confirmation.
        await large.CalculateAsync();
        Assert.False(large.ConfirmLargeDeletion);
        Assert.False(large.CanStart);
    }

    // MARK: - Update newer and dates

    [Fact]
    public void UpdateNewerNeverReplacesAFolderWithAFile()
    {
        var plan = SyncPlanner.Plan(
            [FileEntry("Report", modified: 9_000), FileEntry("b.txt", modified: 9_000)],
            [FolderEntry("Report"), FileEntry("Report/inside.txt"), FileEntry("b.txt", modified: 1_000)],
            SyncMode.Newer,
            PlanDirection.LeftToRight,
            false,
            true);

        Assert.Equal(["b.txt"], plan.Operations.Select(o => o.RelativePath));
        Assert.Equal(1, plan.TypeClashes);
    }

    [Fact]
    public void SftpAndFtpCompareDatesToTheMinute()
    {
        var local = new SyncEndpoint(LocalFileProvider.Shared, _root);
        Assert.Equal(TimeSpan.FromSeconds(60), SyncPlanner.DateToleranceFor(local, new SyncEndpoint(new MemoryCloudProvider(CloudProviderType.Sftp), "/")));
        Assert.Equal(TimeSpan.FromSeconds(60), SyncPlanner.DateToleranceFor(new SyncEndpoint(new MemoryCloudProvider(CloudProviderType.Ftp), "/"), local));
        Assert.Equal(SyncPlanner.ModificationDateTolerance, SyncPlanner.DateToleranceFor(local, new SyncEndpoint(new MemoryCloudProvider(), "/")));

        // An `ls -l` date 40 s off is the same file there; a repeat Mirror has nothing to do.
        var plan = SyncPlanner.Plan(
            [FileEntry("a.txt", modified: 1_040)],
            [FileEntry("a.txt", modified: 1_000)],
            SyncMode.Mirror,
            PlanDirection.LeftToRight,
            false,
            true,
            dateTolerance: SyncPlanner.MinuteDateTolerance);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public async Task ARepeatedMirrorToTheCloudHasNothingToDo()
    {
        var source = LocalDir("repeat-src", "a.txt", "sub/b.txt");
        File.SetLastWriteTimeUtc(Path.Combine(source, "a.txt"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Dst");
        var src = new SyncEndpoint(LocalFileProvider.Shared, source);
        var dst = new SyncEndpoint(cloud, "/Dst");

        var first = new SyncSession(src, dst) { Mode = SyncMode.Mirror, ConfirmDestructive = true };
        await first.CalculateAsync();
        first.StartSync();
        await first.ExecutionTask!;

        var second = new SyncSession(src, dst) { Mode = SyncMode.Mirror, ConfirmDestructive = true };
        await second.CalculateAsync();
        Assert.True(second.Plan!.IsEmpty);
    }

    [Fact]
    public async Task AReplaceThatFailsInASyncKeepsTheDestinationsVersion()
    {
        // macOS 1.6: deleting first lost the destination's version to any failed upload.
        var source = LocalDir("replace-src", "a.txt");
        File.SetLastWriteTimeUtc(Path.Combine(source, "a.txt"), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/a.txt", "the only copy of this version", T(1_000));
        cloud.OnUpload = _ => throw CloudProviderException.QuotaExceeded();
        var session = new SyncSession(new SyncEndpoint(LocalFileProvider.Shared, source), new SyncEndpoint(cloud, "/Dst"))
        {
            Mode = SyncMode.Newer,
        };

        await session.CalculateAsync();
        Assert.Equal(1, session.Plan!.FilesToReplace);
        var progress = session.StartSync()!;
        await session.ExecutionTask!;

        Assert.Equal(1, progress.FailureCount);
        Assert.Equal("the only copy of this version", cloud.Read("/Dst/a.txt"));
        Assert.Equal(["/Dst", "/Dst/a.txt"], cloud.AllPaths.Where(p => p != "/"));
    }

    // MARK: - Running what was calculated

    [Fact]
    public async Task AReadOnlyDestinationIsRefusedByTheExecutor()
    {
        var source = LocalDir("ro-src", "a.txt");
        var gopro = new MemoryCloudProvider(CloudProviderType.GoPro);
        gopro.AddFile("/Media/clip.mp4", "footage");
        var src = new SyncEndpoint(LocalFileProvider.Shared, source);
        var dst = new SyncEndpoint(gopro, "/Media");
        var plan = SyncPlanner.Plan(
            await SyncPlanner.EnumerateAsync(src),
            await SyncPlanner.EnumerateAsync(dst),
            SyncMode.Mirror,
            PlanDirection.LeftToRight,
            false,
            true,
            ignoreCase: true);
        Assert.NotEmpty(plan.Operations);

        var progress = SyncExecutor.CreateProgress(plan, src, dst, PanelSide.Right);
        await SyncExecutor.ExecuteAsync(plan, src, dst, progress);

        Assert.NotNull(progress.ErrorMessage);
        Assert.True(gopro.Exists("/Media/clip.mp4"));
        Assert.False(gopro.Exists("/Media/a.txt"));
    }

    [Fact]
    public void APlanRunsOnlyUnderTheModeAndDirectionItWasMadeFor()
    {
        var plan = SyncPlanner.Plan([FileEntry("a.txt")], [], SyncMode.Newer, PlanDirection.RightToLeft, false, true);
        Assert.True(plan.Matches(SyncMode.Newer, PlanDirection.RightToLeft));
        Assert.False(plan.Matches(SyncMode.Mirror, PlanDirection.RightToLeft));
        Assert.False(plan.Matches(SyncMode.Newer, PlanDirection.LeftToRight));
    }
}
