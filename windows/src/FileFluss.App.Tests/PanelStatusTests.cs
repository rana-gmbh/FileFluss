// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.Core;
using FileFluss.Core.Formatting;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;

namespace FileFluss.App.Tests;

/// <summary>The panel's status line while a folder is being listed.</summary>
public sealed class PanelStatusTests
{
    /// <summary>
    /// Opening a folder said "0 files, 0 folders — 0 bytes" until the listing arrived; it says
    /// "Loading..." instead, and never claims the folder is empty.
    /// </summary>
    [Fact]
    public async Task AFolderBeingListedSaysLoadingNotEmpty()
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-status-").FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "abc");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "de");
        var services = new AppServices(Directory.CreateTempSubdirectory("FileFluss-status-data-").FullName);
        var panel = new PanelViewModel(PanelSide.Left, services);
        var seen = new List<string>();
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.ListingText))
            {
                seen.Add(panel.ListingText);
            }
        };

        await panel.NavigateAsync(PanelLocation.Local(folder));

        Assert.Contains(Localization.L("Loading..."), seen);
        Assert.DoesNotContain(seen, text => text.StartsWith(StatusBarText.Listing(0, 0, 0), StringComparison.Ordinal));
        Assert.Equal(StatusBarText.Listing(2, 0, 5), panel.ListingText);
    }
}
