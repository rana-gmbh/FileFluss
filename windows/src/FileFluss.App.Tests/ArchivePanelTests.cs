// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.IO.Compression;
using FileFluss.App.ViewModels;
using FileFluss.Core.Archives;
using FileFluss.Core.Models;

namespace FileFluss.App.Tests;

/// <summary>A panel inside an archive: rows, breadcrumb, title, read-only, and the way back out.</summary>
public sealed class ArchivePanelTests
{
    [Fact]
    public async Task BrowsesAnArchiveLikeAFolderAndLeavesItUpwards()
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-archive-panel-").FullName;
        var path = Path.Combine(folder, "photos.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "2026/beach.jpg", "2026/city/night.jpg", "readme.txt" })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write("x"u8);
            }
        }

        var services = new AppServices(Directory.CreateTempSubdirectory("FileFluss-archive-data-").FullName);
        var panel = new PanelViewModel(PanelSide.Left, services);
        await panel.NavigateAsync(PanelLocation.Local(folder));
        services.Archives.Open(null, path, path, ArchiveFormat.Zip, "photos.zip");

        await panel.NavigateAsync(PanelLocation.Archive(path, null));
        Assert.True(panel.IsArchive);
        Assert.True(panel.IsReadOnly);
        Assert.False(panel.CanCreateFolders);
        Assert.Equal("photos.zip", panel.Title);
        Assert.Equal(["2026", "readme.txt"], panel.Rows.Select(r => r.Name).ToList());

        await panel.NavigateAsync(panel.Location with { Path = "/2026/city" });
        Assert.Equal(["night.jpg"], panel.Rows.Select(r => r.Name).ToList());
        var crumbs = panel.Breadcrumbs.Select(b => b.Title).ToList();
        Assert.Equal(["photos.zip", "2026", "city"], crumbs[^3..]);
        Assert.Equal(Path.GetFileName(folder), crumbs[^4]);
        Assert.Equal(PanelLocation.Local(folder), panel.Breadcrumbs[^4].Location);

        await panel.GoUpAsync();
        await panel.GoUpAsync();
        Assert.True(panel.CanGoUp);
        await panel.GoUpAsync();
        Assert.Equal(PanelLocation.Local(folder), panel.Location);
        Assert.False(panel.IsReadOnly);

        await panel.GoBackAsync();
        Assert.True(panel.IsArchive);
        Assert.Equal(["2026", "readme.txt"], panel.Rows.Select(r => r.Name).ToList());
    }
}
