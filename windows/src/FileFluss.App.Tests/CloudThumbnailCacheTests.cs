// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Tests.Support;
using FileFluss.Core.Models;
using FileFluss.Core.Providers.ICloud;
using FileFluss.Core.Security;

namespace FileFluss.App.Tests;

/// <summary>
/// Previews of cloud files on gallery tiles: what a service sends is scaled to the tile and
/// stored small, and only rows of a real account ask for them.
/// </summary>
public sealed class CloudThumbnailCacheTests
{
    private static byte[] Jpeg(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x80);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void ALargePhotoComesBackNoBiggerThanTheTile()
        => Bench.OnSta(() =>
        {
            var image = CloudThumbnailCache.Decode(Jpeg(1200, 900), 320);

            Assert.NotNull(image);
            Assert.Equal(320, image!.PixelWidth);
            Assert.True(image.IsFrozen);
        });

    [Fact]
    public void APortraitPhotoIsBoundedByItsHeight()
        => Bench.OnSta(() =>
        {
            var image = CloudThumbnailCache.Decode(Jpeg(600, 900), 320);

            Assert.NotNull(image);
            Assert.Equal(320, image!.PixelHeight);
        });

    [Fact]
    public void ASmallPreviewIsNotBlownUp()
        => Bench.OnSta(() => Assert.Equal(100, CloudThumbnailCache.Decode(Jpeg(100, 80), 320)!.PixelWidth));

    [Fact]
    public void BytesThatArentAnImageGiveNothingRatherThanABrokenTile()
        => Assert.Null(CloudThumbnailCache.Decode("<html>sign in</html>"u8.ToArray(), 320));

    [Fact]
    public void AStoredPreviewReadsBack()
        => Bench.OnSta(() =>
        {
            var image = CloudThumbnailCache.Decode(Jpeg(400, 300), 320)!;
            var bytes = CloudThumbnailCache.Encode(image);

            Assert.NotNull(bytes);
            Assert.Equal(320, CloudThumbnailCache.Decode(bytes!, 320)!.PixelWidth);
        });

    [Fact]
    public void ARowKeepsItsPreviewOnlyForTheSameAccount()
    {
        var item = new CloudFileItem("/a.jpg", "a.jpg", "/a.jpg", false, 10, DateTimeOffset.UnixEpoch, null);
        var credentials = new MemoryCredentialStore();
        using var account = new ICloudProvider(Guid.NewGuid(), credentials);
        using var other = new ICloudProvider(Guid.NewGuid(), credentials);
        var row = new FileRowViewModel(item, isLocal: false, hideExtension: false, account);

        Assert.True(row.CanReuseFor(item, isLocal: false, hideExtension: false, account));
        Assert.False(row.CanReuseFor(item, isLocal: false, hideExtension: false, other));
        Assert.False(row.CanReuseFor(item, isLocal: false, hideExtension: false));
    }
}
