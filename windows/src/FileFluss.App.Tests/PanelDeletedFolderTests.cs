// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;

namespace FileFluss.App.Tests;

/// <summary>
/// A local folder deleted outside FileFluss while a panel shows it: the panel moves up to the
/// nearest folder that still exists, as Explorer does, instead of showing "Item not found".
/// </summary>
public sealed class PanelDeletedFolderTests
{
    [Fact]
    public async Task ARefreshOfADeletedFolderGoesUpToTheNearestExistingOne()
    {
        var root = Directory.CreateTempSubdirectory("FileFluss-gone-").FullName;
        var deep = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(deep);
        var panel = new PanelViewModel(PanelSide.Left, new AppServices(Directory.CreateTempSubdirectory("FileFluss-gone-data-").FullName));
        await panel.NavigateAsync(PanelLocation.Local(deep));

        Directory.Delete(Path.Combine(root, "a"), recursive: true);
        await panel.RefreshAsync(quiet: true); // what the folder watch does

        Assert.Equal(root, panel.CurrentPath);
        Assert.Null(panel.Error);
    }

    [Fact]
    public async Task ARetryTheUserAskedForStillSaysWhatIsWrong()
    {
        var root = Directory.CreateTempSubdirectory("FileFluss-gone-").FullName;
        var folder = Path.Combine(root, "a");
        Directory.CreateDirectory(folder);
        var panel = new PanelViewModel(PanelSide.Left, new AppServices(Directory.CreateTempSubdirectory("FileFluss-gone-data-").FullName));
        await panel.NavigateAsync(PanelLocation.Local(folder));

        Directory.Delete(folder);
        await panel.RefreshAsync();

        Assert.Equal(folder, panel.CurrentPath);
        Assert.NotNull(panel.Error);
    }

    [Fact]
    public async Task ADriveThatIsGoneHasNoParentToGoTo()
    {
        // A drive letter nothing is mounted on: no folder of it exists, the panel keeps its error.
        var letter = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !Directory.Exists($@"{c}:\"));
        Assert.Null(await PanelViewModel.NearestExistingFolderAsync($@"{letter}:\does\not\exist"));
    }
}
