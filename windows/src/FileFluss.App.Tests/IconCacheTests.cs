// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO;
using FileFluss.App.Services;
using FileFluss.App.Tests.Support;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.App.Tests;

public sealed class IconCacheTests
{
    /// <summary>
    /// The shell's thumbnail comes back the right way up: an image with a red band across its
    /// top has red in its top rows and not in its bottom ones. Every thumbnail used to be drawn
    /// upside down.
    /// </summary>
    [Theory]
    [InlineData("png")]
    [InlineData("bmp")]
    [InlineData("jpg")]
    public void ThumbnailsAreTheRightWayUp(string extension)
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-thumb-").FullName;
        var path = Path.Combine(folder, "orientation." + extension);
        WriteImageWithRedTop(path, extension);
        try
        {
            Bench.OnSta(() =>
            {
                var image = (System.Windows.Media.Imaging.BitmapSource?)IconCache.Thumbnail(path, 96);
                Assert.NotNull(image);
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var width = converted.PixelWidth;
                var height = converted.PixelHeight;
                var pixels = new byte[width * height * 4];
                converted.CopyPixels(pixels, width * 4, 0);

                bool IsRed(int row)
                {
                    var i = ((row * width) + (width / 2)) * 4;
                    return pixels[i + 2] > 200 && pixels[i + 1] < 80 && pixels[i] < 80;
                }

                Assert.True(IsRed(height / 10), "top of the thumbnail should be red");
                Assert.False(IsRed(height - 1 - (height / 10)), "bottom of the thumbnail should not be red");
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A 400×300 image, blue with its top third red, written in the given format.</summary>
    private static void WriteImageWithRedTop(string path, string extension)
    {
        const int width = 400, height = 300;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var red = y < height / 3;
                pixels[i] = red ? (byte)0 : (byte)200;     // B
                pixels[i + 1] = 0;                           // G
                pixels[i + 2] = red ? (byte)230 : (byte)0;   // R
                pixels[i + 3] = 255;                         // A
            }
        }

        var source = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        System.Windows.Media.Imaging.BitmapEncoder encoder = extension switch
        {
            "png" => new System.Windows.Media.Imaging.PngBitmapEncoder(),
            "bmp" => new System.Windows.Media.Imaging.BmpBitmapEncoder(),
            _ => new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 },
        };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    [Fact]
    public void FolderRowsGetTheGenericIconAtOnceAndTheirOwnLater()
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-icons-").FullName;
        var item = LocalFileProvider.ToItem(new DirectoryInfo(folder));
        Bench.OnSta(() =>
        {
            var (first, isFinal) = IconCache.ForRow(item, isLocal: true, isDrive: false, 16, _ => { });
            Assert.NotNull(first);
            Assert.False(isFinal);

            // The background lookup lands in the cache; asking again then answers for good.
            var watch = Stopwatch.StartNew();
            (System.Windows.Media.ImageSource? Icon, bool IsFinal) again;
            do
            {
                Thread.Sleep(10);
                again = IconCache.ForRow(item, isLocal: true, isDrive: false, 16, _ => { });
            }
            while (!again.IsFinal && watch.Elapsed < TimeSpan.FromSeconds(10));

            Assert.True(again.IsFinal);
            Assert.NotNull(again.Icon);
        });
    }

    [Fact]
    public void CloudAndPlainFileRowsAreFinalStraightAway()
    {
        Bench.OnSta(() =>
        {
            var cloudFolder = new CloudFileItem("/Photos", "Photos", "/Photos", isDirectory: true, 0, CloudFileItem.UnknownDate);
            var localFile = new CloudFileItem(@"C:\nowhere\report.pdf", "report.pdf", @"C:\nowhere\report.pdf", isDirectory: false, 10, CloudFileItem.UnknownDate);
            Assert.True(IconCache.ForRow(cloudFolder, isLocal: false, isDrive: false, 16, _ => { }).IsFinal);
            Assert.True(IconCache.ForRow(localFile, isLocal: true, isDrive: false, 16, _ => { }).IsFinal);
        });
    }
}
