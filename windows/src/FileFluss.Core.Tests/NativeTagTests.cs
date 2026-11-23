// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Services;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests;

/// <summary>
/// Keeping a service's own labels and FileFluss's tags in step: the same rules as the Mac app
/// (FileFlussTests/NativeTagTests.swift).
/// </summary>
public sealed class NativeTagSyncTests : IDisposable
{
    private static readonly FileTag Red = new("Red", TagColor.Red);
    private static readonly FileTag Blue = new("Blue", TagColor.Blue);
    private static readonly FileTag Green = new("Green", TagColor.Green);
    private static readonly FileTag Project = new("Projekt X", TagColor.Purple);
    private static readonly FileTag Plain = new("Steuer 2026", TagColor.None);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-nativetags-" + Guid.NewGuid().ToString("N"));

    public NativeTagSyncTests()
    {
        Directory.CreateDirectory(_root);
        LocalFileProvider.RecycleHandler = null;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>What a one-colour service reports for a colour: the colour, under no name of its own.</summary>
    private static IReadOnlyList<FileTag> Label(TagColor color) => [new FileTag(color.EnglishName(), color)];

    private static NativeTagSync.Outcome OneColor(IReadOnlyList<FileTag> stored, IReadOnlyList<FileTag> native, bool pending = false)
        => NativeTagSync.Reconcile(stored, native, NativeTagSupport.OneColor, pending, FileTags.Standard);

    private static NativeTagSync.Outcome Full(IReadOnlyList<FileTag> stored, IReadOnlyList<FileTag> native, bool pending = false)
        => NativeTagSync.Reconcile(stored, native, NativeTagSupport.Full, pending, FileTags.Standard);

    // MARK: - One colour per item (MEGA)

    [Fact]
    public void TheServiceIsAskedToHoldTheLastColour()
    {
        Assert.Equal([Blue], NativeTagSync.NativeForm([Red, Plain, Blue], NativeTagSupport.OneColor));
        Assert.Empty(NativeTagSync.NativeForm([Plain], NativeTagSupport.OneColor));
        Assert.Equal([Red, Blue], NativeTagSync.NativeForm([Red, Blue], NativeTagSupport.Full));
        Assert.Empty(NativeTagSync.NativeForm([Red], NativeTagSupport.None));
    }

    [Fact]
    public void WhenBothSidesAgreeNothingChangesAndNothingIsSent()
    {
        var outcome = OneColor([Red, Blue], Label(TagColor.Blue));
        Assert.Equal([Red, Blue], outcome.Tags);
        Assert.Null(outcome.Push);

        var untagged = OneColor([], []);
        Assert.Empty(untagged.Tags);
        Assert.Null(untagged.Push);
    }

    [Fact]
    public void ALabelGivenInTheServicesOwnAppBecomesTheItemsLastTag()
    {
        var fresh = OneColor([], Label(TagColor.Green));
        Assert.Equal([Green], fresh.Tags);
        Assert.Null(fresh.Push);

        // The tags it already had stay, behind the new colour.
        Assert.Equal([Red, Plain, Blue], OneColor([Red, Plain], Label(TagColor.Blue)).Tags);
    }

    [Fact]
    public void AColourTheItemAlreadyHasUnderItsOwnNameKeepsThatName()
    {
        var outcome = OneColor([Project, Red], Label(TagColor.Purple));
        Assert.Equal([Red, Project], outcome.Tags);
        Assert.Equal(TagColor.Purple, outcome.Tags.FolderTint());
    }

    [Fact]
    public void ALabelTakenOffElsewhereTakesTheColoursOffHere()
    {
        var outcome = OneColor([Red, Plain, Blue], []);
        Assert.Equal([Plain], outcome.Tags);
        Assert.Null(outcome.Push);
    }

    [Fact]
    public void AnItemJustWrittenKeepsItsTagsAndTheServiceIsTold()
    {
        var outcome = OneColor([Red, Blue], [], pending: true);
        Assert.Equal([Red, Blue], outcome.Tags);
        Assert.Equal([Blue], outcome.Push);
    }

    // MARK: - Named labels, several per item (kDrive)

    [Fact]
    public void WhereTheServiceHoldsEverythingWhatItHasIsWhatIsShown()
    {
        var invoices = new FileTag("Invoices", TagColor.Orange);
        var outcome = Full([Red], [invoices, Blue]);
        Assert.Equal([invoices, Blue], outcome.Tags);
        Assert.Null(outcome.Push);
        Assert.Empty(Full([Red], []).Tags);
    }

    [Fact]
    public void AnItemJustWrittenAddsItsTagsToWhatTheServiceHas()
    {
        var lower = new FileTag("blue", TagColor.Blue);
        var outcome = Full([Red, Blue], [lower], pending: true);

        // "Blue" is there already, whatever its capitals.
        Assert.Equal([lower, Red], outcome.Tags);
        Assert.Equal(outcome.Tags, outcome.Push);
        Assert.Null(Full([Red], [Red], pending: true).Push);
    }

    [Fact]
    public void WithoutLabelsOfItsOwnAServiceLeavesTheRecordAlone()
    {
        var outcome = NativeTagSync.Reconcile([Red], [], NativeTagSupport.None, pending: true, FileTags.Standard);
        Assert.Equal([Red], outcome.Tags);
        Assert.Null(outcome.Push);
    }

    [Fact]
    public void AnyColourIsShownAsTheNearestOfTheSeven()
    {
        Assert.Equal(TagColor.Red, NativeTagSync.NearestColor("#FF0000"));
        Assert.Equal(TagColor.Blue, NativeTagSync.NearestColor("0098ff"));
        Assert.Equal(TagColor.Green, NativeTagSync.NearestColor("#0A0"));
        Assert.Equal(TagColor.Gray, NativeTagSync.NearestColor("#888888"));
        Assert.Equal(TagColor.Gray, NativeTagSync.NearestColor("not a colour"));
        Assert.Equal(TagColor.Gray, NativeTagSync.NearestColor(null));

        // What FileFluss creates comes back as what it meant.
        foreach (var color in FileTags.MenuOrder)
        {
            Assert.Equal(color, NativeTagSync.NearestColor(NativeTagSync.HexColor(color)));
        }
    }

    // MARK: - With the record and a transfer

    [Fact]
    public void ARecordWrittenByATransferIsToldApartFromOneTheUserMade()
    {
        using var store = new TagStore(Path.Combine(_root, "store"));
        var scope = TagScope.Account(Guid.NewGuid(), lastingItemIds: true);
        store.SetTags(scope, "/uploaded.txt", [Red]);
        store.SetTags(scope, "/tagged.txt", [Blue], itemId: "id-2");
        CloudFileItem[] items =
        [
            new("id-1", "uploaded.txt", "/uploaded.txt", false, 1, DateTimeOffset.UnixEpoch),
            new("id-2", "tagged.txt", "/tagged.txt", false, 1, DateTimeOffset.UnixEpoch),
        ];

        var first = store.ResolveDetailed(scope, items);
        Assert.Equal([Red], first.Tags["/uploaded.txt"]);
        Assert.Equal(["/uploaded.txt"], first.NewlySeen);

        // Only the first time: after that the service has been told.
        Assert.Empty(store.ResolveDetailed(scope, items).NewlySeen);
    }

    [Fact]
    public async Task AFolderFromAServiceWithLabelsComesDownTaggedThoughNeverListedInAPanel()
    {
        using var store = new TagStore(Path.Combine(_root, "store"));
        var cloud = new MemoryCloudProvider(CloudProviderType.Mega) { NativeSupport = NativeTagSupport.OneColor };
        cloud.AddFile("/Trip/day1/photo.jpg", "jpg");
        cloud.AddFile("/Trip/notes.txt", "x");
        cloud.Labels["/Trip/day1"] = Label(TagColor.Green);
        cloud.Labels["/Trip/day1/photo.jpg"] = Label(TagColor.Red);
        var target = Path.Combine(_root, "down");
        Directory.CreateDirectory(target);

        var request = new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/Trip")], LocalFileProvider.Shared, target, IsMove: false) { Tags = store };
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal([Green], store.Tags(TagScope.Local, Path.Combine(target, "Trip", "day1")));
        Assert.Equal([Red], store.Tags(TagScope.Local, Path.Combine(target, "Trip", "day1", "photo.jpg")));
        Assert.Empty(store.Tags(TagScope.Local, Path.Combine(target, "Trip", "notes.txt")));
    }
}
