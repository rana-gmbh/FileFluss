// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Sync;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Sync;

[Collection("Localization")]
public sealed class SyncSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-syncsession-" + Guid.NewGuid().ToString("N"));

    public SyncSessionTests()
    {
        Directory.CreateDirectory(_root);
        LocalFileProvider.RecycleHandler = null;
        Localization.Use(AppLanguage.English);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SyncEndpoint LocalSource(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string('x', 100));
        }

        return new SyncEndpoint(LocalFileProvider.Shared, _root);
    }

    [Fact]
    public async Task CalculateBuildsAPlanAndProjectsTheSpaceLeft()
    {
        var memory = new MemoryCloudProvider();
        memory.AddFolder("/Dst");
        var cloud = new WrappingProvider(memory) { Available = 1_000 };
        var session = new SyncSession(LocalSource("a.txt", "sub/b.txt"), new SyncEndpoint(cloud, "/Dst") { DisplayName = "Box" });
        var notifications = 0;
        session.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);

        Assert.True(session.CanCalculate);
        Assert.Null(session.Plan);
        await session.CalculateAsync();

        var plan = Assert.IsType<SyncPlan>(session.Plan);
        Assert.Equal((2, 1), (plan.FilesToAdd, plan.FoldersToAdd));
        Assert.Equal(200, plan.UploadBytes);
        Assert.Equal(1_000, session.DestinationAvailableBytes);
        Assert.Equal(800, session.AvailableAfterSync);
        Assert.False(session.WouldExceedAvailableSpace);
        Assert.Equal("200 bytes", session.UploadText);
        Assert.Null(session.DownloadText);
        Assert.Null(session.TransferText);
        Assert.Equal("1,000 bytes", session.AvailableText);
        Assert.Equal("800 bytes", session.AfterSyncText);
        Assert.True(session.CanStart);
        Assert.False(session.IsCalculating);
        Assert.True(notifications >= 2);

        cloud.Available = 150;
        await session.CalculateAsync();
        Assert.True(session.WouldExceedAvailableSpace);
        Assert.Equal(-50, session.AvailableAfterSync);
        Assert.Equal("0 bytes", session.AfterSyncText);
    }

    [Fact]
    public async Task UnknownFreeSpaceShowsNoProjection()
    {
        var memory = new MemoryCloudProvider();
        memory.AddFolder("/Dst");
        var session = new SyncSession(LocalSource("a.txt"), new SyncEndpoint(new WrappingProvider(memory) { AvailableThrows = true }, "/Dst"));

        await session.CalculateAsync();

        Assert.NotNull(session.Plan);
        Assert.Null(session.DestinationAvailableBytes);
        Assert.Null(session.AvailableAfterSync);
        Assert.Null(session.AvailableText);
        Assert.False(session.WouldExceedAvailableSpace);
    }

    [Fact]
    public async Task ChangingDirectionOrModeDiscardsThePlan()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/remote.txt", "r");
        var session = new SyncSession(LocalSource("a.txt"), new SyncEndpoint(cloud, "/Dst"));

        await session.CalculateAsync();
        Assert.NotNull(session.Plan);
        session.Direction = PlanDirection.RightToLeft;
        Assert.Null(session.Plan);
        Assert.Equal(PanelSide.Left, session.DestinationSide);
        Assert.Same(session.Right, session.Source);

        await session.CalculateAsync();
        Assert.Equal("remote.txt", Assert.Single(session.Plan!.Operations).RelativePath);
        Assert.Equal(1, session.Plan.DownloadBytes);

        session.Mode = SyncMode.Mirror;
        session.ConfirmDestructive = true;
        session.Mode = SyncMode.Additive;
        Assert.Null(session.Plan);
        Assert.False(session.ConfirmDestructive);
    }

    [Fact]
    public async Task AMirrorStartsOnlyOnceConfirmedAndRunsToTheEnd()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/extra.txt", "x");
        var session = new SyncSession(LocalSource("a.txt"), new SyncEndpoint(cloud, "/Dst")) { Mode = SyncMode.Mirror };

        Assert.True(session.RequiresConfirmation);
        await session.CalculateAsync();
        Assert.Equal(1, session.Plan!.FilesToDelete);
        Assert.False(session.CanStart);
        Assert.Null(session.StartSync());

        session.ConfirmDestructive = true;
        Assert.True(session.CanStart);
        var progress = Assert.IsType<Transfers.TransferProgress>(session.StartSync());
        await session.ExecutionTask!;

        Assert.Equal(PanelSide.Right, progress.Panel);
        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(cloud.Exists("/Dst/a.txt"));
        Assert.False(cloud.Exists("/Dst/extra.txt"));
        Assert.Equal("Done: Synced " + Path.GetFileName(_root), progress.CompletionSummary);
    }

    [Fact]
    public async Task StartSyncHandsOverTheProgressBeforeItRuns()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Dst");
        var session = new SyncSession(LocalSource("a.txt"), new SyncEndpoint(cloud, "/Dst"));
        await session.CalculateAsync();

        Transfers.TransferProgress? registered = null;
        var completions = 0;
        var progress = session.StartSync(p =>
        {
            registered = p;
            Assert.Null(session.ExecutionTask);
            Assert.False(p.IsComplete);
            p.Completed += (_, _) => Interlocked.Increment(ref completions);
        });
        await session.ExecutionTask!;

        Assert.Same(progress, registered);
        Assert.Equal(1, completions);
        Assert.True(cloud.Exists("/Dst/a.txt"));
    }

    [Fact]
    public async Task NothingToDoCannotStart()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Dst");
        var session = new SyncSession(new SyncEndpoint(LocalFileProvider.Shared, _root), new SyncEndpoint(cloud, "/Dst"));

        await session.CalculateAsync();

        Assert.True(session.Plan!.IsEmpty);
        Assert.False(session.CanStart);
    }

    [Fact]
    public async Task OverlappingFoldersAreRefused()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/A/B/x.txt", "x");
        var session = new SyncSession(new SyncEndpoint(cloud, "/A"), new SyncEndpoint(cloud, "/A/B"));

        await session.CalculateAsync();

        Assert.Null(session.Plan);
        Assert.StartsWith("Could not compute plan: ", session.ErrorMessage);
    }

    [Fact]
    public async Task AListingFailureIsShownAsAnError()
    {
        var memory = new MemoryCloudProvider();
        memory.AddFolder("/Dst");
        var cloud = new WrappingProvider(memory);
        cloud.FailingListings.Add("/Dst");
        var session = new SyncSession(LocalSource("a.txt"), new SyncEndpoint(cloud, "/Dst"));

        await session.CalculateAsync();

        Assert.Equal("Could not compute plan: listing refused", session.ErrorMessage);
        Assert.Null(session.Plan);
        Assert.False(session.CanStart);
        Assert.True(session.CanCalculate);
    }

    [Fact]
    public async Task ADirectionChangeSupersedesACalculationInFlight()
    {
        var memory = new MemoryCloudProvider();
        memory.AddFile("/Dst/r.txt", "r");
        var gate = new TaskCompletionSource();
        var cloud = new WrappingProvider(memory) { BeforeList = (_, ct) => gate.Task.WaitAsync(ct) };
        var session = new SyncSession(LocalSource("a.txt"), new SyncEndpoint(cloud, "/Dst"));

        var calculation = session.CalculateAsync();
        Assert.True(session.IsCalculating);
        session.Direction = PlanDirection.RightToLeft;
        Assert.False(session.IsCalculating);
        gate.SetResult();
        await calculation;

        Assert.Null(session.Plan);
        Assert.Null(session.ErrorMessage);
    }

    [Fact]
    public void ThisPcOrAMissingSideCannotBeCalculated()
    {
        var cloud = new MemoryCloudProvider();
        Assert.False(new SyncSession(new SyncEndpoint(LocalFileProvider.Shared, string.Empty), new SyncEndpoint(cloud, "/")).CanCalculate);
        Assert.False(new SyncSession(null, new SyncEndpoint(cloud, "/")).CanCalculate);
    }

    [Fact]
    public void ModeTextsAreTheMacOnes()
    {
        Assert.Equal("Mirror (replace all)", SyncMode.Mirror.Title());
        Assert.Equal("Update newer files", SyncMode.Newer.Title());
        Assert.Equal("Add only (keep both on conflict)", SyncMode.Additive.Title());
        Assert.Equal("Copies missing files and replaces files that are older than the source.", SyncMode.Newer.Subtitle());
        Assert.Equal("Press Calculate to compute how many files will be added, replaced, or deleted.", SyncSession.PlanHint);

        Localization.Use(AppLanguage.German);
        try
        {
            Assert.NotEqual("Mirror (replace all)", SyncMode.Mirror.Title());
        }
        finally
        {
            Localization.Use(AppLanguage.English);
        }
    }
}
