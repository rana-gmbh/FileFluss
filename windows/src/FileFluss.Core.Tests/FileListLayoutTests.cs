// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Settings;

namespace FileFluss.Core.Tests;

/// <summary>Column sets, status bar and quota texts of the file panels.</summary>
[Collection("Localization")]
public sealed class FileListLayoutTests
{
    private const FileListColumn Name = FileListColumn.Name;
    private const FileListColumn Modified = FileListColumn.DateModified;
    private const FileListColumn Created = FileListColumn.DateCreated;
    private const FileListColumn Size = FileListColumn.Size;
    private const FileListColumn Kind = FileListColumn.Kind;

    public FileListLayoutTests() => Localization.Use(AppLanguage.English);

    [Fact]
    public void DefaultsMatchTheMac()
    {
        Assert.Equal([Name, Modified, Size], FileListColumns.Visible(null, cloud: false));
        Assert.Equal([Name, Modified, Size], FileListColumns.Visible(null, cloud: true));
        var settings = new AppSettings();
        Assert.Equal([Name, Modified, Size], settings.LocalColumns);
        Assert.Equal([Name, Modified, Size], settings.CloudColumns);
    }

    [Fact]
    public void CloudPanelsOfferNoDateCreated()
    {
        Assert.Equal([Modified, Created, Size, Kind], FileListColumns.Toggleable(cloud: false));
        Assert.Equal([Modified, Size, Kind], FileListColumns.Toggleable(cloud: true));
        Assert.Equal([Name, Size], FileListColumns.Visible([Created, Size], cloud: true));
        Assert.Equal([Name, Modified, Size], FileListColumns.Toggle([Name, Modified, Size], Created, cloud: true));
    }

    [Fact]
    public void VisibleKeepsOrderAndAlwaysHasName()
    {
        Assert.Equal([Kind, Name, Size], FileListColumns.Visible([Kind, Name, Size, Kind], cloud: false));
        Assert.Equal([Name, Size], FileListColumns.Visible([Size], cloud: false));
        Assert.Equal([Name], FileListColumns.Visible([], cloud: false));
    }

    [Fact]
    public void ToggleHidesAndShowsInHeaderOrder()
    {
        var stored = new List<FileListColumn> { Name, Modified, Size };
        Assert.Equal([Name, Modified, Size, Kind], FileListColumns.Toggle(stored, Kind, cloud: false));
        Assert.Equal([Name, Modified, Created, Size], FileListColumns.Toggle(stored, Created, cloud: false));
        Assert.Equal([Name, Size], FileListColumns.Toggle(stored, Modified, cloud: false));
        Assert.Equal([Name, Modified, Size], FileListColumns.Toggle([Name, Size], Modified, cloud: false));

        // Name can't be hidden; a user's reordering survives toggling another column.
        Assert.Equal([Name, Modified, Size], FileListColumns.Toggle(stored, Name, cloud: false));
        Assert.Equal([Size, Name, Kind], FileListColumns.Toggle([Size, Name], Kind, cloud: false));
    }

    [Fact]
    public void WidthKeysAndSortFields()
    {
        Assert.Equal("local.Size", FileListColumns.WidthKey(false, Size));
        Assert.Equal("cloud.Name", FileListColumns.WidthKey(true, Name));
        Assert.Equal(SortField.Date, FileListColumns.SortFieldFor(Modified));
        Assert.Equal(SortField.DateCreated, FileListColumns.SortFieldFor(Created));
        Assert.Equal("Date Created", FileListColumns.Title(Created));
    }

    [Fact]
    public void StatusBarTexts()
    {
        Assert.Equal("3 files, 2 folders — 1.50 KB", StatusBarText.Listing(3, 2, 1536));
        Assert.Equal(string.Empty, StatusBarText.Selection(0, 10, false));
        Assert.Equal("Selected: 4 items, calculating…", StatusBarText.Selection(4, 10, true));
        Assert.Equal("Selected: 1 items, 10 bytes", StatusBarText.Selection(1, 10, false));
        Assert.Equal("a · c", StatusBarText.Join("a", null, string.Empty, "c"));
    }

    [Fact]
    public void QuotaTexts()
    {
        const long gb = 1024L * 1024 * 1024;
        Assert.Equal("Dropbox: 4.00 GB of 16.0 GB used (25%)", CloudQuotaFormatter.Summary(new CloudStorageQuota(4 * gb, 16 * gb), "Dropbox"));
        Assert.Equal("Google Drive: 412 bytes used", CloudQuotaFormatter.Summary(new CloudStorageQuota(412, null), "Google Drive"));
        Assert.Equal("Box: 412 bytes used", CloudQuotaFormatter.Summary(new CloudStorageQuota(412, 0), "Box"));
        Assert.Equal("100%", CloudQuotaFormatter.PercentString(2 * gb, gb));
        Assert.Equal("0%", CloudQuotaFormatter.PercentString(0, gb));
        Assert.Equal("0.5%", CloudQuotaFormatter.PercentString(5, 1000));
        Assert.Equal("3%", CloudQuotaFormatter.PercentString(25, 1000));
    }
}
