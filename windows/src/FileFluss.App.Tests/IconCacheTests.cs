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
