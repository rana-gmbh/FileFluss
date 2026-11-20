// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Providers;
using FileFluss.Core.Storage;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Storage;

[Collection("Localization")]
public class StorageLayoutTests
{
    public StorageLayoutTests() => Localization.Use(AppLanguage.English);

    [Theory]
    [InlineData(0.5, 30)]
    [InlineData(1.0, 60)]
    [InlineData(0.0, 2)]
    [InlineData(0.01, 2)]
    [InlineData(0.05, 3)]
    [InlineData(1.7, 60)]
    [InlineData(-0.3, 2)]
    [InlineData(double.NaN, 2)]
    public void ShareBarsAreProportionalWithAVisibleMinimum(double fraction, double expected)
        => Assert.Equal(expected, StorageLayout.BarFillWidth(StorageLayout.BarTrackWidth, fraction), 6);

    [Fact]
    public void ANarrowTrackNeverOverflows()
    {
        Assert.Equal(1, StorageLayout.BarFillWidth(1, 0.1), 6);
        Assert.Equal(0, StorageLayout.BarFillWidth(0, 0.5));
        Assert.Equal(0, StorageLayout.BarFillWidth(double.NaN, 0.5));
    }

    [Fact]
    public void RowsIndentByDepth()
    {
        Assert.Equal(0, StorageLayout.Indent(0));
        Assert.Equal(42, StorageLayout.Indent(3));
        Assert.Equal(0, StorageLayout.Indent(-1));
    }

    [Fact]
    public void ShareTextIsAPercentageWithOneDecimal()
    {
        Assert.Equal("12.5% of the total", StorageLayout.ShareText(0.125));
        Assert.Equal("100.0% of the total", StorageLayout.ShareText(1));
        Assert.Equal("0.0% of the total", StorageLayout.ShareText(double.NaN));
    }

    [Fact]
    public async Task RevealOpensFoldersAndSelectsFilesInTheirFolder()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Videos/movie.mp4", "mmmm");
        cloud.AddFile("/readme.md", "r");
        var report = await StorageScanner.ScanAsync(StorageScanSource.ForAccount(cloud, "Box"));
        var videos = report.Root.Children.Single(c => c.Name == "Videos");
        var movie = videos.Children.Single();
        var readme = report.Root.Children.Single(c => c.Name == "readme.md");

        Assert.Equal(("/Videos", (string?)null), StorageLayout.RevealTarget(videos, cloud));
        Assert.Equal(("/Videos", (string?)"movie.mp4"), StorageLayout.RevealTarget(movie, cloud));
        Assert.Equal(("/", (string?)"readme.md"), StorageLayout.RevealTarget(readme, cloud));
        Assert.Equal("/Videos", StorageLayout.ContainingFolder(movie, cloud));
    }

    [Fact]
    public void LocalFilesRevealInTheirWindowsFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ff-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "a.bin");
            File.WriteAllBytes(file, [1, 2, 3]);
            var report = StorageTreeBuilder.Build(
                [new StorageEntry(file, "a.bin", false, 3, null)],
                folder,
                "x",
                LocalFileProvider.Shared);
            var node = report.Root.Children.Single();

            Assert.Equal((folder, (string?)"a.bin"), StorageLayout.RevealTarget(node, LocalFileProvider.Shared));
            Assert.Equal((folder, (string?)null), StorageLayout.RevealTarget(report.Root, LocalFileProvider.Shared));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ParentRowIsTheNearestShallowerRowAbove()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/a/x/deep.bin", new string('d', 50));
        cloud.AddFile("/a/y.bin", new string('y', 20));
        cloud.AddFile("/b.bin", "b");
        var session = new StorageAnalysisSession();
        await session.ScanAsync(StorageScanSource.ForAccount(cloud, "Box"));
        session.ToggleExpanded(session.Rows[1].Node);
        session.ToggleExpanded(session.Rows[2].Node);
        var rows = session.Rows;

        // Box, a, x, deep.bin, y.bin, b.bin
        Assert.Equal(["Box", "a", "x", "deep.bin", "y.bin", "b.bin"], rows.Select(r => r.Node.Name).ToArray());
        Assert.Equal(-1, StorageLayout.ParentRowIndex(rows, 0));
        Assert.Equal(0, StorageLayout.ParentRowIndex(rows, 1));
        Assert.Equal(1, StorageLayout.ParentRowIndex(rows, 2));
        Assert.Equal(2, StorageLayout.ParentRowIndex(rows, 3));
        Assert.Equal(1, StorageLayout.ParentRowIndex(rows, 4));
        Assert.Equal(0, StorageLayout.ParentRowIndex(rows, 5));
        Assert.Equal(-1, StorageLayout.ParentRowIndex(rows, 6));
    }
}
