// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Storage;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Tests.Sync;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests.Storage;

[Collection("Localization")]
public sealed class SpaceCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-space-" + Guid.NewGuid().ToString("N"));

    public SpaceCheckTests()
    {
        Directory.CreateDirectory(_root);
        Localization.Use(AppLanguage.English);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<IReadOnlyList<CloudFileItem>> ItemsAsync(FileProvider provider, params string[] paths)
    {
        var items = new List<CloudFileItem>();
        foreach (var path in paths)
        {
            items.Add(await provider.GetFileMetadataAsync(path));
        }

        return items;
    }

    [Fact]
    public async Task ATransferBiggerThanTheFreeSpaceWouldExceed()
    {
        var source = new MemoryCloudProvider();
        source.AddFile("/big.bin", new string('b', 120));
        source.AddFile("/folder/a", new string('a', 20));
        source.AddFile("/folder/deep/b", new string('b', 10));
        var destination = new WrappingProvider(new MemoryCloudProvider()) { Available = 100 };

        var impact = await SpaceCheck.ImpactAsync(source, await ItemsAsync(source, "/big.bin", "/folder"), destination, "/", isMove: false, "Dropbox");

        Assert.NotNull(impact);
        Assert.Equal(150, impact.AddedBytes);
        Assert.Equal(100, impact.Available);
        Assert.Equal(-50, impact.ProjectedFree);
        Assert.True(impact.WouldExceed);
        Assert.True(impact.IsCloud);
        Assert.Equal(
            "Copy 150 bytes into Dropbox would exceed the available space (only 100 bytes free). Continue anyway?",
            SpaceImpactFormatter.Warning(impact, SpaceCheck.VerbFor(source, destination, isMove: false)));
        Assert.Equal("Copy Anyway", SpaceImpactFormatter.ProceedButton(SpaceCheckVerb.Copy));
        Assert.Equal("Not enough space", SpaceImpactFormatter.Title);
    }

    [Fact]
    public async Task OverwrittenFilesFreeTheirOldSize()
    {
        var source = new MemoryCloudProvider();
        source.AddFile("/report.pdf", new string('n', 150));
        var inner = new MemoryCloudProvider();
        inner.AddFile("/Docs/report.pdf", new string('o', 60));
        var destination = new WrappingProvider(inner) { Available = 100 };

        var impact = await SpaceCheck.ImpactAsync(source, await ItemsAsync(source, "/report.pdf"), destination, "/Docs", isMove: false);

        Assert.Equal(90, impact!.AddedBytes);
        Assert.False(impact.WouldExceed);
        Assert.Equal("Docs", impact.DestinationName);
    }

    [Fact]
    public async Task UnknownFreeSpaceNeverWarns()
    {
        var source = new MemoryCloudProvider();
        source.AddFile("/a", "a");
        var items = await ItemsAsync(source, "/a");

        Assert.Null(await SpaceCheck.ImpactAsync(source, items, new MemoryCloudProvider(), "/", isMove: false));
        Assert.Null(await SpaceCheck.ImpactAsync(source, items, new WrappingProvider(new MemoryCloudProvider()) { AvailableThrows = true }, "/", isMove: false));
        Assert.Null(await SpaceCheck.ImpactAsync(source, [], new WrappingProvider(new MemoryCloudProvider()) { Available = 0 }, "/", isMove: false));
    }

    [Fact]
    public async Task MovesThatAreRenamesNeedNoSpace()
    {
        var local = LocalFileProvider.Shared;
        var file = Path.Combine(_root, "a.bin");
        await File.WriteAllTextAsync(file, "abc");
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var items = await ItemsAsync(local, file);

        Assert.Null(await SpaceCheck.ImpactAsync(local, items, local, target, isMove: true));
        var copy = await SpaceCheck.ImpactAsync(local, items, local, target, isMove: false);
        Assert.NotNull(copy);
        Assert.False(copy.IsCloud);
        Assert.Equal("target", copy.DestinationName);
        Assert.Contains("into \"target\"", SpaceImpactFormatter.Warning(copy with { AddedBytes = long.MaxValue }, SpaceCheckVerb.Copy));

        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/x", "x");
        Assert.Null(await SpaceCheck.ImpactAsync(cloud, await ItemsAsync(cloud, "/x"), cloud, "/y", isMove: true));
    }

    [Fact]
    public async Task ARequestCanBeCheckedDirectly()
    {
        var source = new MemoryCloudProvider();
        source.AddFile("/a", new string('a', 10));
        var destination = new WrappingProvider(new MemoryCloudProvider()) { Available = 5 };
        var request = new TransferRequest(source, await ItemsAsync(source, "/a"), destination, "/", IsMove: true);

        var impact = await SpaceCheck.ImpactAsync(request, "Box");

        Assert.True(impact!.WouldExceed);
        Assert.Equal(SpaceCheckVerb.Move, SpaceCheck.VerbFor(request.Source, request.Destination, request.IsMove));
    }

    [Fact]
    public void VerbsAndVolumeTexts()
    {
        var cloud = new MemoryCloudProvider();
        var local = LocalFileProvider.Shared;
        Assert.Equal(SpaceCheckVerb.Upload, SpaceCheck.VerbFor(local, cloud, false));
        Assert.Equal(SpaceCheckVerb.Download, SpaceCheck.VerbFor(cloud, local, false));
        Assert.Equal(SpaceCheckVerb.Copy, SpaceCheck.VerbFor(local, local, false));
        Assert.Equal(SpaceCheckVerb.Copy, SpaceCheck.VerbFor(cloud, new MemoryCloudProvider(), false));
        Assert.True(SpaceCheck.SameVolume(@"C:\a", @"c:\b\c"));
        Assert.False(SpaceCheck.SameVolume(@"C:\a", @"D:\a"));
        Assert.Equal("500 bytes free of 1,000 bytes", LocalVolumeFormatter.Summary(500, 1_000));
        Assert.Equal(
            "Upload 10 bytes into Box may exceed the available space. Continue anyway?",
            SpaceImpactFormatter.Warning(new SpaceImpact("Box", true, null, 10), SpaceCheckVerb.Upload));
    }

    [Fact]
    public async Task TheLocalDiskReportsFreeSpace()
    {
        var free = await SpaceCheck.AvailableSpaceAsync(LocalFileProvider.Shared, _root);
        Assert.True(free > 0);
    }
}
