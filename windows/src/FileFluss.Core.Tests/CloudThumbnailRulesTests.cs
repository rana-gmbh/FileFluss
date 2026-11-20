// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests;

/// <summary>
/// Which cloud files get a preview on a gallery tile, which may be downloaded to draw one,
/// and how the disk cache of previews is named and trimmed (macOS <c>CloudThumbnailLoader</c>).
/// </summary>
public sealed class CloudThumbnailRulesTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "CloudThumbnailRulesTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static CloudFileItem Item(
        string name,
        long size = 1_000_000,
        bool isDirectory = false,
        CloudDownloadStatus status = CloudDownloadStatus.Local,
        CloudItemRole role = CloudItemRole.Normal,
        string? iconOverride = null,
        string? path = null,
        DateTimeOffset? modified = null)
        => new(
            id: path ?? "/" + name,
            name: name,
            path: path ?? "/" + name,
            isDirectory: isDirectory,
            size: size,
            modificationDate: modified ?? new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            checksum: null)
        {
            DownloadStatus = status,
            Role = role,
            IconOverride = iconOverride,
        };

    [Fact]
    public void PhotosVideosAndPdfsGetAPreview_FoldersAndOtherFilesKeepTheirIcon()
    {
        Assert.True(CloudThumbnailRules.WantsThumbnail(Item("IMG_0001.JPG")));
        Assert.True(CloudThumbnailRules.WantsThumbnail(Item("photo.heic")));
        Assert.True(CloudThumbnailRules.WantsThumbnail(Item("clip.mov")));
        Assert.True(CloudThumbnailRules.WantsThumbnail(Item("report.pdf")));

        Assert.False(CloudThumbnailRules.WantsThumbnail(Item("Holiday.jpg", isDirectory: true)));
        Assert.False(CloudThumbnailRules.WantsThumbnail(Item("notes.txt")));
        Assert.False(CloudThumbnailRules.WantsThumbnail(Item("README")));
    }

    [Fact]
    public void SyntheticEntriesNeverAsk()
    {
        Assert.False(CloudThumbnailRules.WantsThumbnail(Item("drive.png", iconOverride: "drive")));
        Assert.False(CloudThumbnailRules.WantsThumbnail(Item("x.jpg", role: CloudItemRole.VirtualContainer)));
        Assert.False(CloudThumbnailRules.WantsThumbnail(Item("x.jpg", role: CloudItemRole.DriveRoot)));
    }

    [Fact]
    public void SmallImagesAndPdfsMayBeDownloaded_VideosAndBigFilesMayNot()
    {
        Assert.True(CloudThumbnailRules.MayDownloadForThumbnail(Item("a.jpg", size: 3_000_000)));
        Assert.True(CloudThumbnailRules.MayDownloadForThumbnail(Item("a.pdf", size: 3_000_000)));
        Assert.False(CloudThumbnailRules.MayDownloadForThumbnail(Item("a.mov", size: 3_000_000)));

        Assert.True(CloudThumbnailRules.MayDownloadForThumbnail(Item("a.jpg", size: CloudThumbnailRules.MaxFallbackDownloadBytes)));
        Assert.False(CloudThumbnailRules.MayDownloadForThumbnail(Item("a.jpg", size: CloudThumbnailRules.MaxFallbackDownloadBytes + 1)));

        // A size the service didn't report could be anything.
        Assert.False(CloudThumbnailRules.MayDownloadForThumbnail(Item("a.jpg", size: 0)));
    }

    [Fact]
    public void APhotoICloudMovedOffThisPcIsNotBroughtBackForAPreview()
        => Assert.False(CloudThumbnailRules.MayDownloadForThumbnail(Item("a.jpg", status: CloudDownloadStatus.Evicted)));

    [Fact]
    public void OfflineAndCancelledMeanTryAgain_RefusalsDont()
    {
        Assert.True(CloudThumbnailRules.IsTransient(new OperationCanceledException()));
        Assert.True(CloudThumbnailRules.IsTransient(new HttpRequestException("no route")));
        Assert.True(CloudThumbnailRules.IsTransient(CloudProviderException.Network(new HttpRequestException("offline"))));

        Assert.False(CloudThumbnailRules.IsTransient(new HttpRequestException("gone", null, HttpStatusCode.NotFound)));
        Assert.False(CloudThumbnailRules.IsTransient(CloudProviderException.NotFound("/a.jpg")));
        Assert.False(CloudThumbnailRules.IsTransient(new InvalidDataException()));
    }

    [Fact]
    public void SameFileSameName_AnotherFileVersionOrAccountAnotherName()
    {
        var account = Guid.NewGuid();
        var item = Item("a.jpg");
        var first = CloudThumbnailRules.CacheFileName(account, item, 320);
        Assert.Equal(first, CloudThumbnailRules.CacheFileName(account, item, 320));

        string[] others =
        [
            CloudThumbnailRules.CacheFileName(account, Item("b.jpg"), 320),
            CloudThumbnailRules.CacheFileName(account, Item("a.jpg", size: 2), 320),
            CloudThumbnailRules.CacheFileName(account, Item("a.jpg", modified: DateTimeOffset.UtcNow), 320),
            CloudThumbnailRules.CacheFileName(Guid.NewGuid(), item, 320),
            CloudThumbnailRules.CacheFileName(account, item, 192),
        ];
        Assert.All(others, other => Assert.NotEqual(first, other));
    }

    [Fact]
    public void NothingOfTheRemoteNameReachesTheFileSystem()
    {
        var name = CloudThumbnailRules.CacheFileName(Guid.NewGuid(), Item("..\\..\\evil.jpg", path: "/x/../../evil.jpg"), 320);

        Assert.Matches("^[0-9a-f]{64}\\.thumb$", name);
    }

    [Fact]
    public void PruningDropsTheOldestPreviewsFirst_DownToThreeQuartersOfTheLimit()
    {
        Directory.CreateDirectory(_temp);
        var files = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var file = Path.Combine(_temp, $"{i}.thumb");
            File.WriteAllBytes(file, new byte[1000]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(i - 100));
            files.Add(file);
        }

        CloudThumbnailRules.Prune(_temp, limit: 8000);

        Assert.Equal(files.Skip(4), files.Where(File.Exists));
    }

    [Fact]
    public void ACacheWithinTheLimitIsLeftAlone()
    {
        Directory.CreateDirectory(_temp);
        var file = Path.Combine(_temp, "a.thumb");
        File.WriteAllBytes(file, new byte[1000]);

        CloudThumbnailRules.Prune(_temp, limit: 8000);
        CloudThumbnailRules.Prune(Path.Combine(_temp, "missing"), limit: 8000);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task ProvidersWithoutAPreviewApiSayNone()
    {
        using var provider = new MemoryCloudProvider();

        Assert.Null(await provider.ThumbnailAsync(Item("a.jpg"), 320));
    }
}
