// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Search;

namespace FileFluss.Core.Tests.Search;

/// <summary>Which mounted volumes belong in the Drives section (port of the macOS <c>DriveClassificationTests</c>).</summary>
public class DriveClassificationTests
{
    private const string SystemRoot = @"C:\";

    private static DriveKind? Kind(string root, DriveType type, bool ready = true, bool externalBus = false)
        => DriveClassifier.Classify(new DriveIdentity("1234-ABCD", "Label", root, type) { IsReady = ready, IsExternalBus = externalBus }, SystemRoot);

    /// <summary>
    /// Reported on macOS by a user with a second partition on his internal SSD: it appeared
    /// nowhere and there was no way to open it.
    /// </summary>
    [Fact]
    public void ASecondPartitionOnTheInternalDiskIsADrive()
        => Assert.Equal(DriveKind.InternalVolume, Kind(@"D:\", DriveType.Fixed));

    [Fact]
    public void AUsbStickIsAnExternalDrive()
        => Assert.Equal(DriveKind.External, Kind(@"E:\", DriveType.Removable));

    /// <summary>Many USB disks report Fixed, as many Thunderbolt SSDs report non-removable on macOS.</summary>
    [Fact]
    public void AFixedDiskOnAnExternalBusIsExternal()
        => Assert.Equal(DriveKind.External, Kind(@"F:\", DriveType.Fixed, externalBus: true));

    [Fact]
    public void ANetworkShareIsANetworkDrive()
        => Assert.Equal(DriveKind.Network, Kind(@"Z:\", DriveType.Network));

    [Fact]
    public void AnOpticalDiscIsExternal()
        => Assert.Equal(DriveKind.External, Kind(@"G:\", DriveType.CDRom));

    [Fact]
    public void TheWindowsVolumeIsNotListed()
    {
        Assert.Null(Kind(@"C:\", DriveType.Fixed));
        Assert.Null(Kind("C:", DriveType.Fixed));
        Assert.Null(Kind(@"c:\", DriveType.Fixed));
    }

    [Fact]
    public void AVolumeThatIsntReadyIsSkipped()
    {
        Assert.Null(Kind(@"E:\", DriveType.Removable, ready: false));
        Assert.Null(Kind(@"D:\", DriveType.Fixed, ready: false));
    }

    [Fact]
    public void RamDisksAndUnknownVolumesAreNotDrives()
    {
        Assert.Null(Kind(@"R:\", DriveType.Ram));
        Assert.Null(Kind(@"U:\", DriveType.Unknown));
        Assert.Null(Kind(@"N:\", DriveType.NoRootDirectory));
    }
}

[Collection("Localization")]
public sealed class DriveRegistryTests : IDisposable
{
    private const string SystemRoot = @"C:\";
    private readonly TempIndex _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DriveIdentity Usb(string serial = "AAAA-1111", string root = @"E:\", string label = "Stick")
        => new(serial, label, root, DriveType.Removable);

    [Fact]
    public void MountedDrivesAreListedOnline()
    {
        var registry = new DriveRegistry(_temp.Index, SystemRoot);
        var changes = 0;
        registry.Changed += (_, _) => changes++;

        registry.UpdateMounted([Usb(), new DriveIdentity("SYS-0001", "Windows", @"C:\", DriveType.Fixed)]);

        var drive = Assert.Single(registry.Drives);
        Assert.Equal("vol:AAAA-1111", drive.Id);
        Assert.True(drive.IsOnline);
        Assert.Equal(@"E:\", drive.RootPath);
        Assert.Equal(@"E:\", registry.RootPathFor(drive.Id));
        Assert.Equal("Stick", drive.DisplayName);
        Assert.Equal(1, changes);
    }

    /// <summary>The point of indexing a drive: unplugged, it stays — offline — with its index.</summary>
    [Fact]
    public void AnIndexedDriveStaysListedOfflineAndAcrossLaunches()
    {
        var registry = new DriveRegistry(_temp.Index, SystemRoot);
        registry.UpdateMounted([Usb()]);
        registry.RecordIndexed("vol:AAAA-1111", DateTimeOffset.Now, 10, 2048);

        registry.UpdateMounted([]);

        var offline = Assert.Single(registry.Drives);
        Assert.False(offline.IsOnline);
        Assert.Null(offline.RootPath);
        Assert.Equal(@"E:\", offline.LastRootPath);
        Assert.Equal(10, offline.TotalFiles);
        Assert.Single(registry.OfflineIndexedDrives);

        var nextLaunch = new DriveRegistry(_temp.Index, SystemRoot);
        var restored = Assert.Single(nextLaunch.Drives);
        Assert.Equal("vol:AAAA-1111", restored.Id);
        Assert.True(restored.IsIndexed);
        Assert.False(restored.IsOnline);
    }

    /// <summary>Yesterday's one-off stick doesn't linger: never indexed and gone means forgotten.</summary>
    [Fact]
    public void ANeverIndexedDriveIsDroppedWhenItGoes()
    {
        var registry = new DriveRegistry(_temp.Index, SystemRoot);
        registry.UpdateMounted([Usb()]);
        registry.UpdateMounted([]);

        Assert.Empty(registry.Drives);
    }

    /// <summary>Identity is the volume serial, so a drive back under another letter keeps its index.</summary>
    [Fact]
    public void ADriveBackUnderAnotherLetterIsTheSameDrive()
    {
        var registry = new DriveRegistry(_temp.Index, SystemRoot);
        registry.UpdateMounted([Usb(root: @"E:\")]);
        registry.RecordIndexed("vol:AAAA-1111", DateTimeOffset.Now, 5, 1);
        registry.UpdateMounted([]);

        registry.UpdateMounted([Usb(root: @"F:\", label: "Renamed")]);

        var drive = Assert.Single(registry.Drives);
        Assert.True(drive.IsOnline);
        Assert.Equal(@"F:\", drive.RootPath);
        Assert.Equal("Renamed", drive.DisplayName);
        Assert.True(drive.IsIndexed);
    }

    [Fact]
    public void AnUnlabelledDriveIsNamedByItsKind()
    {
        var registry = new DriveRegistry(_temp.Index, SystemRoot);
        registry.UpdateMounted([Usb(label: "")]);

        Assert.Equal(DriveKind.External.DisplayName(), registry.Drives.Single().DisplayName);
    }

    [Fact]
    public void RemovingTheIndexOfAnOfflineDriveForgetsIt()
    {
        var registry = new DriveRegistry(_temp.Index, SystemRoot);
        registry.UpdateMounted([Usb(), Usb("BBBB-2222", @"F:\", "Other")]);
        registry.RecordIndexed("vol:AAAA-1111", DateTimeOffset.Now, 1, 1);
        registry.RecordIndexed("vol:BBBB-2222", DateTimeOffset.Now, 1, 1);
        _temp.Index.ReplaceFiles("vol:AAAA-1111", "drive-external", "Stick", [new IndexedFile("vol:AAAA-1111", "/a.txt", "/", "a.txt", false, 1, DateTimeOffset.Now)]);
        registry.UpdateMounted([Usb("BBBB-2222", @"F:\", "Other")]);

        // Unplugged: forgotten with its index. Mounted: stays, no longer indexed.
        registry.RemoveOfflineIndex("vol:AAAA-1111");
        registry.RemoveOfflineIndex("vol:BBBB-2222");

        var remaining = Assert.Single(registry.Drives);
        Assert.Equal("vol:BBBB-2222", remaining.Id);
        Assert.False(remaining.IsIndexed);
        Assert.Null(_temp.Index.GetSource("vol:AAAA-1111"));
        Assert.Empty(_temp.Index.SearchIndexed("a", ["vol:AAAA-1111"]));
    }

    [Fact]
    public void ErasingAllIndexesClearsWhatDrivesClaim()
    {
        var registry = new DriveRegistry(_temp.Index, SystemRoot);
        registry.UpdateMounted([Usb(), Usb("BBBB-2222", @"F:\", "Other")]);
        registry.RecordIndexed("vol:AAAA-1111", DateTimeOffset.Now, 1, 1);
        registry.RecordIndexed("vol:BBBB-2222", DateTimeOffset.Now, 1, 1);
        registry.UpdateMounted([Usb()]);

        _temp.Index.WipeAll();
        registry.ClearIndexedState();

        var remaining = Assert.Single(registry.Drives);
        Assert.Equal("vol:AAAA-1111", remaining.Id);
        Assert.False(remaining.IsIndexed);
    }
}
