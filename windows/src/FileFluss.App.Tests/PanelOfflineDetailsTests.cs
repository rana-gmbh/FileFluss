// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;
using FileFluss.Core.Search;

namespace FileFluss.App.Tests;

/// <summary>
/// An unplugged drive's name and the offline banner come from the search index; they're read
/// off the UI thread and announced when they arrive, instead of being queried by every getter.
/// </summary>
public sealed class PanelOfflineDetailsTests
{
    [Fact]
    public async Task AnUnpluggedDrivesNameArrivesWithoutTheGetterAskingTheIndex()
    {
        var services = new AppServices(Directory.CreateTempSubdirectory("FileFluss-offline-").FullName);
        services.Search.Drives.Upsert(new KnownDrive
        {
            Id = "vol:AUDIT1",
            VolumeId = "AUDIT1",
            Label = "Holiday Disk",
            Kind = DriveKind.External,
            LastIndexed = DateTimeOffset.UtcNow,
        });
        var panel = new PanelViewModel(PanelSide.Left, services);
        var announced = 0;
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.Title))
            {
                Interlocked.Increment(ref announced);
            }
        };

        await panel.NavigateAsync(PanelLocation.OfflineDrive("vol:AUDIT1", "/"));

        var watch = Stopwatch.StartNew();
        while (panel.Title != "Holiday Disk" && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal("Holiday Disk", panel.Title);
        Assert.True(announced > 0);
        Assert.True(panel.IsOffline);
        Assert.False(string.IsNullOrEmpty(panel.OfflineBanner));
    }
}
