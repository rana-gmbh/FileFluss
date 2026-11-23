// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileFluss.App.Views;

namespace FileFluss.App.Tests;

/// <summary>
/// Quick Look decodes photos off the UI thread, at the size they're shown: a big photo used to
/// be decoded in full on every arrow-key press.
/// </summary>
public sealed class QuickLookTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("FileFluss-quicklook-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ABigPhotoIsDecodedOnABackgroundThreadAtTheSizeItIsShown()
    {
        var path = WritePng("big.png", 4000, 3000);

        var image = await Task.Run(() => QuickLookWindow.DecodeImage(path, new Size(1600, 1200)));

        Assert.True(image.IsFrozen);
        Assert.Equal(1600, image.PixelWidth);
        Assert.Equal(1200, image.PixelHeight);
    }

    [Fact]
    public void APortraitPhotoFitsTheBoxByItsHeight()
    {
        var path = WritePng("tall.png", 1000, 4000);

        var image = QuickLookWindow.DecodeImage(path, new Size(1600, 1200));

        Assert.Equal(300, image.PixelWidth);
        Assert.Equal(1200, image.PixelHeight);
    }

    [Fact]
    public void ASmallPictureKeepsItsOwnSize()
    {
        var path = WritePng("small.png", 320, 200);

        var image = QuickLookWindow.DecodeImage(path, new Size(1600, 1200));

        Assert.Equal(320, image.PixelWidth);
        Assert.Equal(200, image.PixelHeight);
    }

    private string WritePng(string name, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x80);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        var path = Path.Combine(_root, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
